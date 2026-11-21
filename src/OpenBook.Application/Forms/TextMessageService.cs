using System.Text.Json;
using OpenBook.Application.Companies;
using OpenBook.Application.Sales;
using OpenBook.Domain.Auditing;
using OpenBook.Domain.Common;
using OpenBook.Domain.Email;
using OpenBook.Domain.Sales;

namespace OpenBook.Application.Forms;

/// <param name="AuthToken">Null or empty keeps the saved token (unless <paramref name="ClearAuthToken"/>).</param>
public sealed record TextSettingsCommand(string? AccountSid, string? AuthToken, string? Sender, bool ClearAuthToken = false);

/// <summary>What a text will say and where it goes, and why it can't go yet (each a sentence).</summary>
/// <param name="Text">The message as it will arrive, with a stand-in for the link.</param>
/// <param name="Problems">What stops it being sent (texts not set up, the customer hasn't agreed); empty when it can go.</param>
/// <param name="Notes">Things to know that don't stop it.</param>
public sealed record TextPreview(string? To, string Text, TextLength Length, IReadOnlyList<string> Problems, IReadOnlyList<string> Notes)
{
    public bool CanSend => Problems.Count == 0;
}

/// <summary>A text from the history, with its link.</summary>
public sealed record SentTextEntry(SentText Text, DocumentLink? Link);

/// <summary>
/// Sending invoices, estimates, credit memos and sales receipts by text message through the company's own Twilio
/// account. A text carries a "View" link (a <see cref="DocumentLink"/>, as link emails do) and, for an open invoice that
/// can be paid online, a payment link; it goes only to the customer's mobile number, and only once someone has recorded
/// that the customer agreed to get texts. A customer who replies STOP is switched off the first time Twilio refuses them.
/// </summary>
public sealed class TextMessageService(
    ICompanyStore companies,
    ISalesStore sales,
    IEmailStore store,
    IEnumerable<IEmailSource> sources,
    EmailService email,
    ITextSender sender,
    ISecretProtector secrets,
    IClock clock,
    ICurrentUser user)
{
    public const string SettingsEntity = "text_settings";

    /// <summary>The link's length when sent, so previews count the parts the real text will take.</summary>
    private static readonly string SampleToken = DocumentLink.TokenPrefix + new string('x', 32);

    // ---- Settings ----

    public async Task<TextSettings> GetSettingsAsync(CancellationToken ct = default) => await store.GetTextSettingsAsync(ct) ?? TextSettings.Create();

    public async Task<TextSettings> SaveSettingsAsync(TextSettingsCommand command, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        var company = await companies.GetRequiredCompanyAsync(ct);
        var settings = await store.GetTextSettingsAsync(ct);
        string? before = null;
        if (settings is null)
        {
            settings = TextSettings.Create();
            store.Add(settings);
        }
        else
        {
            before = Snapshot(settings);
        }

        var token = command.ClearAuthToken ? "" : string.IsNullOrWhiteSpace(command.AuthToken) ? null : secrets.Protect(Token(command.AuthToken));
        settings.Update(command.AccountSid, token, command.Sender, company.Region);
        store.Add(AuditRecord.Create(clock.UtcNow, user.Actor, before is null ? AuditAction.Created : AuditAction.Updated, SettingsEntity, settings.Id, before, Snapshot(settings)));
        await store.SaveChangesAsync(ct);
        return settings;
    }

    /// <summary>A short text to a number, to check the settings; nothing is recorded. Returns the number as sent to.</summary>
    public async Task<string> SendTestAsync(string? number, CancellationToken ct = default)
    {
        var company = await companies.GetRequiredCompanyAsync(ct);
        var to = PhoneNumbers.Normalize(number, "Send a test to", company.Region)
            ?? throw new DomainException("text.to_required", "Enter the mobile number to send the test to.");
        await sender.SendAsync(await KeysAsync(ct), to, $"Test message from {company.Name} in OpenBook. If you can read it, text messages are set up.", ct);
        return to;
    }

    /// <summary>Whether texts can go at all (Settings, Text messages filled in).</summary>
    public async Task<bool> CanSendAsync(CancellationToken ct = default) => (await GetSettingsAsync(ct)).IsReady;

    // ---- Customers ----

    /// <summary>A customer's mobile number and whether they agreed to get texts. Audited on the customer.</summary>
    public async Task<Customer> SetCustomerAsync(Guid customerId, string? number, bool allowed, CancellationToken ct = default)
    {
        var company = await companies.GetRequiredCompanyAsync(ct);
        var customer = await sales.GetCustomerAsync(customerId, ct) ?? throw new EntityNotFoundException("customer", customerId);
        var before = CustomerSnapshot(customer);
        customer.SetTextPreferences(number, allowed, clock.UtcNow, company.Region);
        var after = CustomerSnapshot(customer);
        if (after != before)
        {
            sales.Add(AuditRecord.Create(clock.UtcNow, user.Actor, AuditAction.Updated, "customer", customer.Id, before, after));
            await sales.SaveChangesAsync(ct);
        }

        return customer;
    }

    /// <summary>The number to suggest when none is saved: the customer's phone, when it reads as one.</summary>
    public async Task<string?> SuggestedNumberAsync(Customer customer, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(customer);
        return customer.TextNumber ?? PhoneNumbers.TryNormalize(customer.Phone, (await companies.GetRequiredCompanyAsync(ct)).Region);
    }

    // ---- Writing and sending ----

    /// <summary>The usual wording for the kind.</summary>
    public static string Draft(EmailKind kind) => TextTemplates.Usual(kind);

    /// <summary>The message as it will arrive (a stand-in for the link), its length, and what stops it going. Never throws for a mistake in the wording.</summary>
    public async Task<TextPreview> PreviewAsync(EmailSource source, string? body, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        var company = await companies.GetRequiredCompanyAsync(ct);
        using var region = CompanyRegion.Use(company);
        var problems = new List<string>();
        var notes = new List<string>();
        var context = await ContextAsync(source, ct);
        var linkBase = await email.LinkBaseAsync(ct);
        problems.AddRange(Problems(context, await GetSettingsAsync(ct), linkBase));
        try
        {
            Check(source.Kind, body);
        }
        catch (DomainException ex)
        {
            problems.Add(ex.Message);
        }

        var link = $"{linkBase ?? "https://books.example.com"}/view/{SampleToken}";
        var text = TextTemplates.Compose(body ?? "", Values(context, link, context.PayOnline ? link + "/pay" : ""));
        var length = TextTemplates.Measure(text);
        if (text.Length > TextTemplates.MaxBody)
            problems.Add($"The text is too long: at most {TextTemplates.MaxBody:N0} characters, with the line about stopping.");
        if (length.Unicode)
            notes.Add("It has a character outside the usual text alphabet (an emoji or a curly quote, say), so each part holds 70 characters instead of 160.");
        if (length.Parts > 1)
            notes.Add($"It goes as {length.Parts} parts, which Twilio charges for one by one. The customer sees one message.");
        return new TextPreview(context.Customer?.TextNumber, text, length, problems, notes);
    }

    /// <summary>
    /// Sends the text with a new "View" link, records it in the history and the audit log. Refuses before sending anything
    /// when it can't go (see <see cref="PreviewAsync"/>); a failed send keeps nothing (not even the link).
    /// </summary>
    public async Task<SentText> SendAsync(EmailSource source, string? body, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        var company = await companies.GetRequiredCompanyAsync(ct);
        using var region = CompanyRegion.Use(company);
        var context = await ContextAsync(source, ct);
        if (context.Refusal is { } refusal)
            throw new DomainException(refusal.Code, refusal.Message);
        var linkBase = await email.LinkBaseAsync(ct);
        if (Problems(context, await GetSettingsAsync(ct), linkBase).FirstOrDefault() is { } problem)
            throw new DomainException(ProblemCode(context, await GetSettingsAsync(ct), linkBase), problem);
        Check(source.Kind, body);
        var customer = context.Customer!;
        var keys = await KeysAsync(ct);

        var (link, token) = DocumentLink.Create(source.Kind, source.Id, customer.Id, clock.UtcNow, user.Actor);
        var url = $"{linkBase}/view/{token}";
        var text = TextTemplates.Compose(body!, Values(context, url, context.PayOnline ? url + "/pay" : ""));
        if (text.Length > TextTemplates.MaxBody)
            throw new DomainException("text.too_long", $"The text is too long: at most {TextTemplates.MaxBody:N0} characters, with the line about stopping.");

        string id;
        try
        {
            id = await sender.SendAsync(keys, customer.TextNumber!, text, ct);
        }
        catch (DomainException ex) when (ex.Code == Twilio.OptedOutCode)
        {
            // Twilio keeps the STOP; the customer is switched off here too, so nobody tries again.
            var before = CustomerSnapshot(customer);
            customer.StopTexts();
            sales.Add(AuditRecord.Create(clock.UtcNow, user.Actor, AuditAction.Updated, "customer", customer.Id, before,
                JsonSerializer.Serialize(new { customer.TextNumber, TextsAllowed = false, Reason = "Replied STOP" })));
            await sales.SaveChangesAsync(ct);
            throw;
        }

        // Only once it's gone. The kept copy says "(link)": the token opens the document, so it goes in the text only.
        var kept = TextTemplates.Compose(body!, Values(context, "(link)", context.PayOnline ? "(payment link)" : ""));
        store.Add(link);
        var sent = SentText.Create(source.Kind, context.EntityType, context.EntityId, clock.UtcNow, user.Actor, customer.TextNumber!, kept, link.Id, id);
        store.Add(sent);
        store.Add(AuditRecord.Create(clock.UtcNow, user.Actor, AuditAction.Texted, context.EntityType, context.EntityId, null,
            JsonSerializer.Serialize(new { To = customer.TextNumber, Form = context.Title })));
        await store.SaveChangesAsync(ct);
        return sent;
    }

    /// <summary>The texts sent about a record, newest first, with their links.</summary>
    public async Task<IReadOnlyList<SentTextEntry>> HistoryAsync(string entityType, Guid entityId, CancellationToken ct = default)
    {
        var sent = await store.ListSentTextsAsync(entityType, entityId, ct);
        var ids = sent.Select(s => s.DocumentLinkId).OfType<Guid>().ToList();
        var links = ids.Count == 0 ? new Dictionary<Guid, DocumentLink>() : (await store.GetLinksAsync(ids, ct)).ToDictionary(l => l.Id);
        return [.. sent.Select(s => new SentTextEntry(s, s.DocumentLinkId is { } id ? links.GetValueOrDefault(id) : null))];
    }

    // ---- Helpers ----

    private async Task<EmailContext> ContextAsync(EmailSource source, CancellationToken ct)
    {
        if (!TextTemplates.CanText(source.Kind))
            throw new DomainException("text.kind_unsupported", $"{EmailTemplates.Label(source.Kind)}s can't be sent by text.");
        var provider = sources.FirstOrDefault(s => s.Kinds.Contains(source.Kind))
            ?? throw new DomainException("text.kind_unsupported", $"{EmailTemplates.Label(source.Kind)}s can't be sent by text.");
        return await provider.ContextAsync(source, ct);
    }

    private static IEnumerable<string> Problems(EmailContext context, TextSettings settings, string? linkBase)
    {
        if (context.Refusal is { } refusal)
            yield return refusal.Message;
        if (!settings.IsReady)
            yield return "Text messages aren't set up yet: an admin enters the company's Twilio account under Settings, Text messages.";
        if (linkBase is null)
            yield return "Texts carry a link, which needs the address customers open OpenBook at (Settings, Email).";
        if (context.Customer is not { } customer)
            yield return "Choose a customer on the document first: texts go to the customer's mobile number.";
        else if (customer.TextNumber is null)
            yield return $"{customer.DisplayName} has no mobile number for texts. Add it on the customer, under Text messages.";
        else if (customer.TextsAllowedAt is null)
            yield return $"{customer.DisplayName} hasn't agreed to get texts. Once they have, tick it on the customer, under Text messages.";
    }

    private static string ProblemCode(EmailContext context, TextSettings settings, string? linkBase) =>
        context.Refusal is { } refusal ? refusal.Code
        : !settings.IsReady ? "text.not_configured"
        : linkBase is null ? "text.link_address_required"
        : context.Customer is null ? "text.customer_required"
        : context.Customer.TextNumber is null ? "text.number_required"
        : "text.not_allowed";

    private static void Check(EmailKind kind, string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
            throw new DomainException("text.body_required", "Write the message.");
        if (body.Length > TextTemplates.MaxBody)
            throw new DomainException("text.too_long", $"The text is too long: at most {TextTemplates.MaxBody:N0} characters.");
        TextTemplates.Check(kind, body);
    }

    private static Dictionary<string, string> Values(EmailContext context, string link, string payLink) =>
        new(context.Values) { ["link"] = link, ["pay_link"] = payLink };

    private async Task<TextKeys> KeysAsync(CancellationToken ct)
    {
        var settings = await GetSettingsAsync(ct);
        if (!settings.IsReady)
            throw new DomainException("text.not_configured", "Text messages aren't set up yet: an admin enters the company's Twilio account under Settings, Text messages.");
        var token = secrets.Unprotect(settings.ProtectedAuthToken!)
            ?? throw new DomainException("text.token_unreadable", "The saved Twilio auth token can't be read on this computer. Enter it again under Settings, Text messages.");
        return new TextKeys(settings.AccountSid!, token, settings.Sender!);
    }

    private static string Token(string value)
    {
        var token = value.Trim();
        return token.Length is >= 16 and <= 64 && token.All(char.IsAsciiLetterOrDigit)
            ? token
            : throw new DomainException("text.auth_token_invalid", "The auth token is 32 letters and digits, as the Twilio Console shows it under Account Info.");
    }

    private static string CustomerSnapshot(Customer c) => JsonSerializer.Serialize(new { c.TextNumber, TextsAllowed = c.TextsAllowedAt is not null, c.TextsAllowedAt });

    /// <summary>No token in here: audit records feed webhooks.</summary>
    private static string Snapshot(TextSettings s) => JsonSerializer.Serialize(new { s.AccountSid, HasAuthToken = s.ProtectedAuthToken is not null, s.Sender });
}
