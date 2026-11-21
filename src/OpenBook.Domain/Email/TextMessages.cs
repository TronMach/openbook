using System.Globalization;
using OpenBook.Domain.Common;

namespace OpenBook.Domain.Email;

/// <summary>
/// The company's own Twilio account, for sending documents by text message (one row, created on first save): the
/// account SID, its auth token (stored through the host's secret protector, never shown again, left out of the data
/// export and audit) and who texts come from: a Twilio phone number or a messaging service.
/// </summary>
public sealed class TextSettings
{
    public const int MaxSidLength = 34;

    private TextSettings()
    {
    }

    public Guid Id { get; private set; }

    /// <summary>"AC" and 32 hexadecimal digits, from the Twilio Console's Account Info.</summary>
    public string? AccountSid { get; private set; }

    public string? ProtectedAuthToken { get; private set; }

    /// <summary>A Twilio number in international form ("+15551234567") or a messaging service SID ("MG…").</summary>
    public string? Sender { get; private set; }

    public bool IsReady => AccountSid is not null && ProtectedAuthToken is not null && Sender is not null;

    /// <summary>Whether texts go through a messaging service rather than from one number.</summary>
    public bool SendsThroughService => Sender?.StartsWith("MG", StringComparison.Ordinal) == true;

    public static TextSettings Create() => new() { Id = Guid.CreateVersion7() };

    /// <param name="protectedAuthToken">Null keeps the saved token; empty clears it.</param>
    /// <param name="region">The company's region, for a sender number typed without its country code.</param>
    public void Update(string? accountSid, string? protectedAuthToken, string? sender, string? region)
    {
        AccountSid = Sid(accountSid, "AC", "text.account_sid_invalid",
            "The account SID is AC followed by 32 letters and digits, as the Twilio Console shows it under Account Info.");
        if (protectedAuthToken is not null)
            ProtectedAuthToken = protectedAuthToken.Length == 0 ? null : protectedAuthToken;
        var from = sender?.Trim();
        Sender = string.IsNullOrEmpty(from) ? null
            : from.StartsWith("MG", StringComparison.OrdinalIgnoreCase)
                ? Sid(from, "MG", "text.sender_invalid", "A messaging service SID is MG followed by 32 letters and digits.")
                : PhoneNumbers.Normalize(from, "The number texts come from", region);
    }

    private static string? Sid(string? value, string prefix, string code, string message)
    {
        var text = value?.Trim();
        if (string.IsNullOrEmpty(text))
            return null;
        if (text.Length != MaxSidLength || !text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || !text[2..].All(char.IsAsciiHexDigit))
            throw new DomainException(code, message);
        return prefix + text[2..].ToLowerInvariant();
    }
}

/// <summary>Phone numbers as people type them, written the one way text services take them: "+" and the country code (E.164).</summary>
public static class PhoneNumbers
{
    public const int MaxLength = 16;

    /// <summary>The country calling code and whether national numbers start with a 0 to drop, per company region.</summary>
    private static readonly Dictionary<string, (string Code, bool TrunkZero, int NationalDigits)> Countries = new(StringComparer.OrdinalIgnoreCase)
    {
        ["US"] = ("1", false, 10),
        ["CA"] = ("1", false, 10),
        ["GB"] = ("44", true, 10),
        ["AU"] = ("61", true, 9),
        ["NZ"] = ("64", true, 0),
        ["IE"] = ("353", true, 0),
    };

    /// <summary>
    /// "+" and 8 to 15 digits, or null when blank. A number without "+" is read as one from the company's region
    /// (a US or Canadian number when no region is chosen yet): its country code goes in front, and a leading 0 goes.
    /// Spaces, dashes, dots and brackets are ignored; anything else is refused.
    /// </summary>
    public static string? Normalize(string? text, string label, string? region)
    {
        var raw = text?.Trim();
        if (string.IsNullOrEmpty(raw))
            return null;
        if (raw.Length > 40)
            throw Invalid(label, raw[..40] + "…");
        var international = raw.StartsWith('+') || raw.StartsWith("00", StringComparison.Ordinal);
        var digits = new System.Text.StringBuilder();
        foreach (var c in raw.StartsWith('+') ? raw[1..] : raw)
        {
            if (char.IsAsciiDigit(c))
                digits.Append(c);
            else if (c is not (' ' or '-' or '.' or '(' or ')' or ' '))
                throw Invalid(label, raw);
        }

        var number = digits.ToString();
        if (international)
        {
            if (raw.StartsWith("00", StringComparison.Ordinal))
                number = number[2..];
        }
        else
        {
            var country = region is { Length: >= 5 } r ? r[^2..] : "US";
            if (!Countries.TryGetValue(country, out var rule))
                throw new DomainException("text.number_country_required", $"{label}: start the number with + and its country code.");
            if (rule.Code == "1" && number.Length == 11 && number[0] == '1')
                number = number[1..];
            if (rule.TrunkZero && number.StartsWith('0'))
                number = number[1..];
            if (rule.NationalDigits > 0 && number.Length != rule.NationalDigits)
                throw Invalid(label, raw);
            number = rule.Code + number;
        }

        if (number.Length is < 8 or > 15 || number[0] == '0')
            throw Invalid(label, raw);
        return "+" + number;
    }

    /// <summary>The number in international form, or null when it's blank or can't be read.</summary>
    public static string? TryNormalize(string? text, string? region)
    {
        try
        {
            return Normalize(text, "Number", region);
        }
        catch (DomainException)
        {
            return null;
        }
    }

    private static DomainException Invalid(string label, string value) =>
        new("text.number_invalid", $"{label}: '{value}' isn't a phone number texts can go to. Write it with + and the country code if it's from abroad.");
}

/// <summary>How long a text is: the parts the network splits it into, which text services charge for one by one.</summary>
/// <param name="Unicode">It has a character outside the GSM alphabet (an emoji, a curly quote), so each part holds 70 characters, not 160.</param>
public sealed record TextLength(int Characters, int Parts, bool Unicode);

/// <summary>The wording of texts, their placeholders, and their length in parts.</summary>
public static class TextTemplates
{
    /// <summary>Twilio's limit for one message, the line about stopping included.</summary>
    public const int MaxBody = 1600;

    /// <summary>Added under every text, as carriers require: Twilio stops texting a number that replies STOP.</summary>
    public const string OptOutLine = "Reply STOP to opt out.";

    private const string Gsm = "@£$¥èéùìòÇ\nØø\rÅåΔ_ΦΓΛΩΠΨΣΘΞÆæßÉ !\"#¤%&'()*+,-./0123456789:;<=>?¡ABCDEFGHIJKLMNOPQRSTUVWXYZÄÖÑÜ§¿abcdefghijklmnopqrstuvwxyzäöñüà";
    private const string GsmExtended = "^{}\\[~]|€\f";

    /// <summary>Invoices, estimates, credit memos and sales receipts: the documents a link can open.</summary>
    public static bool CanText(EmailKind kind) => DocumentLink.CanLink(kind);

    /// <summary>A kind's email placeholders, and {link} (the document) and, for invoices, {pay_link}.</summary>
    public static IReadOnlyList<EmailPlaceholder> Placeholders(EmailKind kind) =>
    [
        .. EmailTemplates.Placeholders(kind),
        new("link", "The link that opens it", "https://books.example.com/view/obd_…"),
        .. kind == EmailKind.Invoice ? [new EmailPlaceholder("pay_link", "The link to pay it online (blank when it can't be paid online)", "https://books.example.com/view/obd_…/pay")] : Array.Empty<EmailPlaceholder>(),
    ];

    /// <summary>The wording OpenBook uses. Lines whose placeholders all come out blank drop out, as in emails.</summary>
    public static string Usual(EmailKind kind) => kind switch
    {
        EmailKind.Invoice => "{company}: invoice {number} for {balance} is due on {due}.\nView it: {link}\nPay online: {pay_link}",
        EmailKind.Estimate => "{company}: estimate {number} for {total}.\nView it: {link}",
        EmailKind.CreditMemo => "{company}: credit memo {number} for {total}.\nView it: {link}",
        EmailKind.SalesReceipt => "{company}: thank you for your purchase. Your receipt {number}: {link}",
        _ => "{company}: {link}",
    };

    /// <summary>Refuses placeholders the kind doesn't take, and a message with no {link}, which is the point of the text.</summary>
    public static void Check(EmailKind kind, string? body)
    {
        var known = Placeholders(kind).Select(p => p.Key).ToHashSet(StringComparer.Ordinal);
        var unknown = System.Text.RegularExpressions.Regex.Matches(body ?? "", @"\{([a-z_]+)\}").Select(m => m.Groups[1].Value)
            .Where(k => !known.Contains(k)).Distinct().ToList();
        if (unknown.Count > 0)
            throw new DomainException("text.placeholder_unknown",
                $"{string.Join(", ", unknown.Select(u => "{" + u + "}"))} isn't a placeholder for {EmailTemplates.Label(kind).ToLowerInvariant()} texts. " +
                $"You can use {string.Join(", ", known.Select(k => "{" + k + "}"))}.");
        if (body?.Contains("{link}", StringComparison.Ordinal) != true)
            throw new DomainException("text.link_required", "Keep {link} in the message: it's how the customer opens the document.");
    }

    /// <summary>The message as it goes: filled in, tidied, with the line about stopping under it.</summary>
    public static string Compose(string body, IReadOnlyDictionary<string, string> values)
    {
        var filled = EmailTemplates.Fill(body, values).Replace("\r\n", "\n", StringComparison.Ordinal).Trim();
        return $"{filled}\n{OptOutLine}";
    }

    public static TextLength Measure(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var unicode = false;
        var septets = 0;
        foreach (var c in text)
        {
            if (Gsm.Contains(c, StringComparison.Ordinal))
                septets++;
            else if (GsmExtended.Contains(c, StringComparison.Ordinal))
                septets += 2;
            else
                unicode = true;
        }

        // UTF-16 code units: an emoji takes two, as the network counts them.
        var units = unicode ? text.Length : septets;
        var (single, multi) = unicode ? (70, 67) : (160, 153);
        var parts = units == 0 ? 0 : units <= single ? 1 : (units + multi - 1) / multi;
        return new TextLength(new StringInfo(text).LengthInTextElements, parts, unicode);
    }
}

/// <summary>One text message as it was sent: the number, what it said, the link in it and the service's id for it. Append-only.</summary>
public sealed class SentText
{
    private SentText()
    {
        EntityType = string.Empty;
        To = string.Empty;
        Body = string.Empty;
        SentBy = string.Empty;
    }

    public Guid Id { get; private set; }
    public EmailKind Kind { get; private set; }

    /// <summary>The record it's about, as the audit log names it ("invoice").</summary>
    public string EntityType { get; private set; }

    public Guid EntityId { get; private set; }
    public DateTimeOffset SentAt { get; private set; }
    public string SentBy { get; private set; }

    /// <summary>The number in international form.</summary>
    public string To { get; private set; }

    /// <summary>As sent, with the line about stopping; the link's token is replaced by "(link)", since it opens the document.</summary>
    public string Body { get; private set; }

    public Guid? DocumentLinkId { get; private set; }

    /// <summary>Twilio's id for the message ("SM…"), to look it up in the Twilio Console.</summary>
    public string? ProviderId { get; private set; }

    public static SentText Create(
        EmailKind kind, string entityType, Guid entityId, DateTimeOffset now, string actor, string to, string body, Guid? linkId, string? providerId) => new()
    {
        Id = Guid.CreateVersion7(),
        Kind = kind,
        EntityType = entityType,
        EntityId = entityId,
        SentAt = now.ToUniversalTime(),
        SentBy = actor,
        To = to,
        Body = body.Length <= TextTemplates.MaxBody ? body : body[..TextTemplates.MaxBody],
        DocumentLinkId = linkId,
        ProviderId = providerId is { Length: > 0 and <= 64 } ? providerId : null,
    };
}
