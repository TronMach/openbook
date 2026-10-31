using System.Text.Json;
using OpenBook.Application.Companies;
using OpenBook.Application.Sales;
using OpenBook.Domain.Auditing;
using OpenBook.Domain.Common;
using OpenBook.Domain.Companies;

namespace OpenBook.Application.Forms;

/// <summary>Form designs. Changes become visible only after <see cref="SaveChangesAsync"/>.</summary>
public interface IFormDesignStore
{
    /// <summary>Tracked; every design, active or not.</summary>
    Task<IReadOnlyList<FormDesign>> ListAsync(CancellationToken ct);

    void Add(FormDesign design);
    void Add(AuditRecord record);

    Task SaveChangesAsync(CancellationToken ct);
}

/// <summary>
/// Saved form designs: which one a document prints with (<see cref="FormDesigns.Pick"/>), applying it to a form
/// (title, message, ship-to, terms, look), and previews from sample data while a design is edited.
/// </summary>
public sealed class FormDesignService(
    IFormDesignStore store, ISalesStore sales, ICompanyStore companies, IFormRenderer renderer, FormFontService fonts, IClock clock, ICurrentUser user)
{
    public const string EntityType = "form_style";
    public const string DefaultName = "Standard";

    /// <summary>Default first, then by name.</summary>
    public async Task<IReadOnlyList<FormDesign>> ListAsync(CancellationToken ct = default) =>
        [.. (await store.ListAsync(ct)).OrderByDescending(d => d.IsDefault).ThenBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase)];

    public async Task<FormDesign?> GetAsync(Guid id, CancellationToken ct = default) => (await store.ListAsync(ct)).FirstOrDefault(d => d.Id == id);

    /// <summary>A new design; the first one is the default. <paramref name="copyLogoFrom"/> copies another design's logo.</summary>
    public async Task<FormDesign> CreateAsync(FormDesignDetails details, Guid? copyLogoFrom = null, CancellationToken ct = default)
    {
        var designs = await store.ListAsync(ct);
        await EnsureFontAsync(details, ct);
        var design = FormDesign.Create(details, clock.UtcNow, isDefault: designs.Count == 0);
        EnsureUnique(designs, design.Name, design.Id);
        if (copyLogoFrom is { } from && designs.FirstOrDefault(d => d.Id == from)?.Logo is { } logo)
            design.SetLogo(logo);
        store.Add(design);
        store.Add(Audit(AuditAction.Created, design.Id, null, Snapshot(design)));
        await store.SaveChangesAsync(ct);
        return design;
    }

    public async Task<FormDesign> UpdateAsync(Guid id, FormDesignDetails details, CancellationToken ct = default)
    {
        await EnsureFontAsync(details, ct);
        return await ChangeAsync(id, (design, designs) =>
        {
            design.Update(details);
            EnsureUnique(designs, design.Name, design.Id);
        }, ct);
    }

    public Task<FormDesign> SetLogoAsync(Guid id, byte[] content, CancellationToken ct = default) =>
        ChangeAsync(id, (design, _) => design.SetLogo(content), ct);

    public Task<FormDesign> RemoveLogoAsync(Guid id, CancellationToken ct = default) =>
        ChangeAsync(id, (design, _) => design.RemoveLogo(), ct);

    /// <summary>Makes it the design every document uses unless another is chosen for it.</summary>
    public async Task<FormDesign> MakeDefaultAsync(Guid id, CancellationToken ct = default)
    {
        var designs = await store.ListAsync(ct);
        var design = designs.FirstOrDefault(d => d.Id == id) ?? throw new EntityNotFoundException(EntityType, id);
        if (!design.IsActive)
            throw new DomainException("formdesign.inactive", $"{design.Name} is switched off.");
        foreach (var other in designs.Where(d => d.IsDefault && d.Id != id))
        {
            var before = Snapshot(other);
            other.MakeDefault(false);
            store.Add(Audit(AuditAction.Updated, other.Id, before, Snapshot(other)));
        }

        if (!design.IsDefault)
        {
            var before = Snapshot(design);
            design.MakeDefault(true);
            store.Add(Audit(AuditAction.Updated, design.Id, before, Snapshot(design)));
        }

        await store.SaveChangesAsync(ct);
        return design;
    }

    /// <summary>The documents this design prints by default; each is taken from whichever design had it.</summary>
    public async Task<FormDesign> SetDefaultForAsync(Guid id, FormDocuments documents, CancellationToken ct = default)
    {
        var designs = await store.ListAsync(ct);
        var design = designs.FirstOrDefault(d => d.Id == id) ?? throw new EntityNotFoundException(EntityType, id);
        if (!design.IsActive && documents != FormDocuments.None)
            throw new DomainException("formdesign.inactive", $"{design.Name} is switched off.");
        foreach (var other in designs.Where(d => d.Id != id && (d.DefaultFor & documents) != 0))
        {
            var before = Snapshot(other);
            other.SetDefaultFor(other.DefaultFor & ~documents);
            store.Add(Audit(AuditAction.Updated, other.Id, before, Snapshot(other)));
        }

        var mine = Snapshot(design);
        design.SetDefaultFor(documents);
        if (Snapshot(design) != mine)
            store.Add(Audit(AuditAction.Updated, design.Id, mine, Snapshot(design)));
        await store.SaveChangesAsync(ct);
        return design;
    }

    /// <summary>Switched off, a design stops being offered and isn't used (customers who had it get the usual design).</summary>
    public Task<FormDesign> SetActiveAsync(Guid id, bool active, CancellationToken ct = default) =>
        ChangeAsync(id, (design, _) => design.SetActive(active), ct);

    /// <summary>The design a customer's documents print with (null = the usual one); audited on the customer.</summary>
    public async Task<Domain.Sales.Customer> SetForCustomerAsync(Guid customerId, Guid? designId, CancellationToken ct = default)
    {
        var customer = await sales.GetCustomerAsync(customerId, ct) ?? throw new EntityNotFoundException("customer", customerId);
        string? name = null;
        if (designId is { } id && id != Guid.Empty)
        {
            var design = await GetAsync(id, ct) ?? throw new EntityNotFoundException(EntityType, id);
            if (!design.IsActive)
                throw new DomainException("formdesign.inactive", $"{design.Name} is switched off.");
            name = design.Name;
        }

        var before = customer.FormDesignId;
        customer.SetFormDesign(designId);
        if (before != customer.FormDesignId)
        {
            sales.Add(AuditRecord.Create(clock.UtcNow, user.Actor, AuditAction.Updated, "customer", customer.Id,
                JsonSerializer.Serialize(new { FormDesignId = before }), JsonSerializer.Serialize(new { FormDesignId = customer.FormDesignId, FormDesign = name })));
            await sales.SaveChangesAsync(ct);
        }

        return customer;
    }

    /// <summary>The design a document prints with: the customer's, else the one for this document, else the default.</summary>
    public async Task<FormDesign> ForAsync(FormDocuments document, Guid? customerId = null, CancellationToken ct = default)
    {
        var designs = await store.ListAsync(ct);
        if (designs.Count == 0)
            return Standard();
        Guid? chosen = customerId is { } c && c != Guid.Empty ? (await sales.GetCustomerAsync(c, ct))?.FormDesignId : null;
        return FormDesigns.Pick(designs, document, chosen);
    }

    /// <summary>The default design's look, for forms that aren't one of <see cref="FormDocuments"/> (donor letters).</summary>
    public async Task<FormStyle> DefaultStyleAsync(CancellationToken ct = default) =>
        await StyleAsync((await store.ListAsync(ct)).FirstOrDefault(d => d.IsDefault) ?? Standard(), ct);

    /// <summary>A design's look, with its font's faces loaded.</summary>
    public async Task<FormStyle> StyleAsync(FormDesign design, CancellationToken ct = default) =>
        FormStyle.From(design, await fonts.FaceAsync(design.FontKey, ct));

    /// <summary>A form with a design applied (<see cref="Apply(SalesForm, FormDesign, FormDocuments, FormStyle)"/>), its font loaded.</summary>
    public async Task<SalesForm> ApplyAsync(SalesForm form, FormDesign design, FormDocuments document, CancellationToken ct = default) =>
        Apply(form, design, document, await StyleAsync(design, ct));

    public async Task<StatementForm> ApplyAsync(StatementForm form, FormDesign design, CancellationToken ct = default) =>
        Apply(form, design, await StyleAsync(design, ct));

    /// <summary>
    /// A form with a design applied: its title (keeping a "(void)" after it), its message after the memo, ship-to and
    /// terms shown or not, and its look.
    /// </summary>
    public static SalesForm Apply(SalesForm form, FormDesign design, FormDocuments document, FormStyle style)
    {
        ArgumentNullException.ThrowIfNull(form);
        ArgumentNullException.ThrowIfNull(design);
        var usual = UsualTitle(document);
        var title = design.TitleFor(document, usual) is var own && own != usual && form.Title.StartsWith(usual, StringComparison.Ordinal)
            ? own + form.Title[usual.Length..]
            : form.Title;
        return form with
        {
            Title = title,
            Facts = design.ShowTerms ? form.Facts : [.. form.Facts.Where(f => f.Label is not ("Terms" or "Due date"))],
            Message = Join(form.Message, design.MessageFor(document)),
            ShipTo = design.ShowShipTo ? form.ShipTo : null,
            Style = style,
        };
    }

    public static StatementForm Apply(StatementForm form, FormDesign design, FormStyle style)
    {
        ArgumentNullException.ThrowIfNull(form);
        ArgumentNullException.ThrowIfNull(design);
        return form with
        {
            Title = design.TitleFor(FormDocuments.Statement, form.Title),
            Message = Join(form.Message, design.MessageFor(FormDocuments.Statement)),
            Style = style,
        };
    }

    public static string UsualTitle(FormDocuments document) => document switch
    {
        FormDocuments.CreditMemo => "Credit memo",
        FormDocuments.SalesReceipt => "Sales receipt",
        FormDocuments.PurchaseOrder => "Purchase order",
        _ => document.ToString(),
    };

    /// <summary>
    /// A sample document printed with unsaved settings (and the saved design's logo, when <paramref name="logoFrom"/>
    /// names one), so the editor can show the result before saving. Uses the company's own name and address.
    /// </summary>
    public async Task<byte[]> PreviewAsync(FormDesignDetails details, FormDocuments document, Guid? logoFrom = null, CancellationToken ct = default)
    {
        var design = FormDesign.Create(details, clock.UtcNow);
        if (logoFrom is { } id && (await GetAsync(id, ct))?.Logo is { } logo)
            design.SetLogo(logo);
        var company = await companies.GetRequiredCompanyAsync(ct);
        var from = FormService.CompanyParty(company);
        var customer = new FormParty("Sample Customer Ltd", "100 Main Street\nSpringfield, IL 62701", "accounts@example.com", "(555) 010-0100");
        var today = DateOnly.FromDateTime(clock.UtcNow.UtcDateTime);
        string D(DateOnly d) => d.ToString("d", System.Globalization.CultureInfo.CurrentCulture);

        if (document == FormDocuments.Statement)
        {
            var statement = new StatementForm(from, customer, today.AddDays(-30), today, 250m,
                [new StatementFormLine(today.AddDays(-20), "Invoice 1001", "1001", 480m, 730m), new StatementFormLine(today.AddDays(-5), "Payment", "Check 2210", -250m, 480m)],
                480m, [("Current", 480m), ("1–30 days", 0m), ("31–60 days", 0m), ("61–90 days", 0m), ("Over 90 days", 0m)],
                company.HomeCurrency.Code, FormStyle.From(design));
            return renderer.Render(await ApplyAsync(statement, design, ct));
        }

        var facts = new List<(string, string)> { ("Date", D(today)) };
        if (document == FormDocuments.Invoice)
            facts.AddRange([("Due date", D(today.AddDays(30))), ("Terms", "Net 30")]);
        var form = new SalesForm(
            UsualTitle(document), "1001", from, document == FormDocuments.PurchaseOrder ? new FormParty("Sample Supplier Co", "20 Industrial Way\nRiverside, CA 92501") : customer,
            facts,
            [
                new FormLine("Consulting services", 6m, 95m, 570m, LineNumber: 1),
                new FormLine("Widget, blue", 12m, 4.5m, 54m, LineNumber: 2, Details: "Pack size: Case of 12"),
                new FormLine("Delivery", 1m, 25m, 25m, LineNumber: 3),
            ],
            [new FormTotal("Subtotal", 649m), new FormTotal("Sales tax", 51.92m), new FormTotal("Total", 700.92m, Emphasis: true)],
            null, company.HomeCurrency.Code, FormStyle.From(design),
            PartyLabel: document == FormDocuments.PurchaseOrder ? "VENDOR" : "BILL TO",
            ShipTo: document == FormDocuments.PurchaseOrder ? null : new FormParty("Sample Customer Ltd", "Dock 4, 250 Commerce Drive\nSpringfield, IL 62702"));
        return renderer.Render(await ApplyAsync(form, design, document, ct));
    }

    internal static FormDesign Standard() => FormDesign.Create(new FormDesignDetails(DefaultName), DateTimeOffset.UnixEpoch, isDefault: true);

    private async Task<FormDesign> ChangeAsync(Guid id, Action<FormDesign, IReadOnlyList<FormDesign>> change, CancellationToken ct)
    {
        var designs = await store.ListAsync(ct);
        var design = designs.FirstOrDefault(d => d.Id == id) ?? throw new EntityNotFoundException(EntityType, id);
        var before = Snapshot(design);
        change(design, designs);
        var after = Snapshot(design);
        if (after != before)
        {
            store.Add(Audit(AuditAction.Updated, design.Id, before, after));
            await store.SaveChangesAsync(ct);
        }

        return design;
    }

    private async Task EnsureFontAsync(FormDesignDetails details, CancellationToken ct)
    {
        if (!await fonts.ExistsAsync(details.FontKey, ct))
            throw new DomainException("font.unknown", "That font isn't available: choose one from the list, or add it first.");
    }

    private static void EnsureUnique(IEnumerable<FormDesign> designs, string name, Guid id)
    {
        if (designs.Any(d => d.Id != id && string.Equals(d.Name, name, StringComparison.OrdinalIgnoreCase)))
            throw new DomainException("formdesign.name_taken", $"There's already a design named \"{name}\".");
    }

    private static string? Join(string? first, string? second) =>
        first is null ? second : second is null ? first : $"{first}\n\n{second}";

    private AuditRecord Audit(AuditAction action, Guid id, string? before, string? after) =>
        AuditRecord.Create(clock.UtcNow, user.Actor, action, EntityType, id, before, after);

    /// <summary>Without the logo's bytes: whether there is one, and a short fingerprint so a new logo shows as a change.</summary>
    internal static string Snapshot(FormDesign d) => JsonSerializer.Serialize(new
    {
        d.Name,
        Default = d.IsDefault,
        Active = d.IsActive,
        DefaultFor = d.DefaultFor.ToString(),
        Template = d.Template.ToString(),
        d.AccentColor,
        d.Footer,
        Font = d.FontKey,
        PaperSize = d.PaperSize.ToString(),
        Logo = d.Logo is { } logo ? $"{d.LogoContentType}, {logo.Length:N0} bytes, {Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(logo))[..12]}" : null,
        LogoPosition = d.LogoPosition.ToString(),
        LogoSize = d.LogoSize.ToString(),
        d.ShowShipTo,
        d.ShowTerms,
        d.ShowQuantityAndRate,
        Columns = new[] { d.DescriptionLabel, d.QuantityLabel, d.RateLabel, d.AmountLabel },
        Texts = d.Texts.Select(t => new { Document = t.Document.ToString(), t.Title, t.Message }),
    });
}
