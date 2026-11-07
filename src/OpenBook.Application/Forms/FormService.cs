using System.Globalization;
using OpenBook.Application.Companies;
using OpenBook.Application.Sales;
using OpenBook.Domain.Common;
using OpenBook.Domain.Companies;
using OpenBook.Domain.Sales;
using OpenBook.Domain.SalesTax;
using OpenBook.Domain.Segments;

namespace OpenBook.Application.Forms;

public enum FormKind
{
    Invoice,
    Estimate,
    CreditMemo,
    SalesReceipt,
}

/// <summary>A rendered PDF and what to call it.</summary>
public sealed record RenderedForm(string FileName, byte[] Pdf, Guid CustomerId, string Title);

/// <summary>Builds printable invoices, estimates, credit memos, sales receipts and statements, and renders them to PDF.</summary>
public sealed class FormService(
    ICompanyStore companies, ISalesStore store, ReceivablesService receivables, IFormRenderer renderer, CustomFields.CustomFieldService customFields,
    FormDesignService designs, Segments.SegmentService segments, Projects.IProjectStore projects)
{
    /// <summary>The label of the totals row <see cref="FormParts.Savings"/> shows or hides.</summary>
    public const string SavingsLabel = "You saved";

    public Task<RenderedForm> RenderAsync(FormKind kind, Guid id, CancellationToken ct = default) => RenderAsync(kind, id, null, ct);

    /// <param name="designId">Print with this design rather than the usual one (a packing slip, say); null = the usual one.</param>
    public async Task<RenderedForm> RenderAsync(FormKind kind, Guid id, Guid? designId, CancellationToken ct = default)
    {
        var company = await companies.GetRequiredCompanyAsync(ct);
        using var region = CompanyRegion.Use(company);
        var form = await BuildAsync(company, kind, id, designId, ct);
        return new RenderedForm(FileName(company, form.Form.Title, form.Form.Number), renderer.Render(form.Form), form.CustomerId, $"{form.Form.Title} {form.Form.Number}");
    }

    /// <summary>The form's content, as <see cref="RenderAsync(FormKind, Guid, Guid?, CancellationToken)"/> prints it.</summary>
    public Task<SalesForm> BuildAsync(FormKind kind, Guid id, CancellationToken ct = default) => BuildAsync(kind, id, null, ct);

    public async Task<SalesForm> BuildAsync(FormKind kind, Guid id, Guid? designId, CancellationToken ct = default)
    {
        var company = await companies.GetRequiredCompanyAsync(ct);
        using var region = CompanyRegion.Use(company);
        return (await BuildAsync(company, kind, id, designId, ct)).Form;
    }

    private async Task<(SalesForm Form, Guid CustomerId)> BuildAsync(Company company, FormKind kind, Guid id, Guid? designId, CancellationToken ct)
    {
        var (form, customerId) = kind switch
        {
            FormKind.Invoice => await InvoiceAsync(company, id, ct),
            FormKind.Estimate => await EstimateAsync(company, id, ct),
            FormKind.CreditMemo => await CreditMemoAsync(company, id, ct),
            _ => await ReceiptAsync(company, id, ct),
        };
        var document = Document(kind);
        var design = await designs.ChosenAsync(document, customerId == Guid.Empty ? null : customerId, designId, ct);
        return (await customFields.DecorateAsync(await designs.ApplyAsync(form, design, document, ct), id, ct), customerId);
    }

    public static FormDocuments Document(FormKind kind) => kind switch
    {
        FormKind.Invoice => FormDocuments.Invoice,
        FormKind.Estimate => FormDocuments.Estimate,
        FormKind.CreditMemo => FormDocuments.CreditMemo,
        _ => FormDocuments.SalesReceipt,
    };

    public Task<RenderedForm> RenderStatementAsync(Guid customerId, DateOnly from, DateOnly to, CancellationToken ct = default) =>
        RenderStatementAsync(customerId, from, to, null, ct);

    /// <param name="designId">Print with this design rather than the usual one; null = the usual one.</param>
    public async Task<RenderedForm> RenderStatementAsync(Guid customerId, DateOnly from, DateOnly to, Guid? designId, CancellationToken ct = default)
    {
        var company = await companies.GetRequiredCompanyAsync(ct);
        using var region = CompanyRegion.Use(company);
        var statement = await receivables.StatementAsync(customerId, from, to, ct);
        var aging = statement.Aging;
        var form = new StatementForm(
            CompanyParty(company),
            CustomerParty(statement.Customer),
            from,
            to,
            statement.OpeningBalance,
            statement.Lines.Select(l => new StatementFormLine(l.Date, l.Description, l.Reference, l.Amount, l.Balance)).ToList(),
            statement.ClosingBalance,
            [("Current", aging.Current), ("1–30 days", aging.Days1To30), ("31–60 days", aging.Days31To60), ("61–90 days", aging.Days61To90), ("Over 90 days", aging.Over90)],
            company.HomeCurrency.Code,
            Unstyled);
        form = await designs.ApplyAsync(form, await designs.ChosenAsync(FormDocuments.Statement, customerId, designId, ct), ct);
        return new RenderedForm(FileName(company, form.Title, to.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)), renderer.Render(form), customerId,
            $"Statement {to.ToString("d", CultureInfo.CurrentCulture)}");
    }

    private async Task<(SalesForm Form, Guid CustomerId)> InvoiceAsync(Company company, Guid id, CancellationToken ct)
    {
        var invoice = await store.GetInvoiceAsync(id, ct) ?? throw new EntityNotFoundException("invoice", id);
        var (customer, job) = await BilledAsync(invoice.CustomerId, ct);
        var facts = new List<(string, string)>
        {
            ("Date", D(invoice.Date)),
            ("Due date", D(invoice.DueDate)),
            ("Terms", new PaymentTerms(invoice.DueDate.DayNumber - invoice.Date.DayNumber, invoice.EarlyPaymentDiscountPercent,
                invoice.EarlyPaymentDiscountDate is { } d ? d.DayNumber - invoice.Date.DayNumber : 0).Label),
        };
        // Time billed on it: the hours, whatever the lines' format.
        if ((await projects.ListTimeForInvoiceAsync(invoice.Id, ct)).Sum(t => t.Hours) is var hours and > 0m)
            facts.Add(("Hours", hours.ToString("0.##", CultureInfo.CurrentCulture)));
        // A progress bill shows where the contract stands: its total, billed before this invoice, this one, and what's left.
        if ((invoice.EstimateId is { } estimateId ? await store.GetEstimateAsync(estimateId, ct) : null) is { } estimate)
        {
            var before = (await store.ListInvoicesAsync(invoice.CustomerId, ct))
                .Where(i => i.EstimateId == estimate.Id && i.Status == DocumentStatus.Posted && i.Id != invoice.Id
                    && (i.Date < invoice.Date || (i.Date == invoice.Date && i.CreatedAt < invoice.CreatedAt)))
                .Sum(i => i.Total);
            facts.Add(("Contract", $"Estimate {estimate.Number}: {estimate.Total:N2}"));
            facts.Add(("Billed before", before.ToString("N2", CultureInfo.CurrentCulture)));
            facts.Add(("Left after this", (estimate.Total - before - invoice.Total).ToString("N2", CultureInfo.CurrentCulture)));
        }

        var totals = TaxTotals(invoice.Lines, invoice.TaxCodeId, invoice.TaxExemption);
        totals.Add(new FormTotal("Total", invoice.Total));
        if (invoice.AmountPaid > 0m)
            totals.Add(new FormTotal("Paid and credited", -invoice.AmountPaid));
        totals.Add(new FormTotal("Balance due", invoice.Balance, Emphasis: true));
        AddSavings(totals, invoice.Lines);
        var title = invoice.Status == DocumentStatus.Voided ? "Invoice (void)" : "Invoice";
        return (Form(company, customer, title, invoice.Number, facts, await LinesAsync(invoice.Lines, invoice.TaxCodeId, ct), totals, invoice.Memo, job, invoice.BillToAddress, invoice.ShipToAddress), customer.Id);
    }

    private async Task<(SalesForm Form, Guid CustomerId)> EstimateAsync(Company company, Guid id, CancellationToken ct)
    {
        var estimate = await store.GetEstimateAsync(id, ct) ?? throw new EntityNotFoundException("estimate", id);
        var (customer, job) = await BilledAsync(estimate.CustomerId, ct);
        var facts = new List<(string, string)> { ("Date", D(estimate.Date)) };
        if (estimate.ExpirationDate is { } expires)
            facts.Add(("Valid until", D(expires)));
        List<FormTotal> totals = [.. TaxTotals(estimate.Lines, estimate.TaxCodeId, estimate.TaxExemption), new FormTotal("Total", estimate.Total, Emphasis: true)];
        AddSavings(totals, estimate.Lines);
        return (Form(company, customer, "Estimate", estimate.Number, facts, await LinesAsync(estimate.Lines, estimate.TaxCodeId, ct), totals, estimate.Memo, job, estimate.BillToAddress, estimate.ShipToAddress), customer.Id);
    }

    private async Task<(SalesForm Form, Guid CustomerId)> CreditMemoAsync(Company company, Guid id, CancellationToken ct)
    {
        var credit = await store.GetCreditMemoAsync(id, ct) ?? throw new EntityNotFoundException("credit_memo", id);
        var (customer, job) = await BilledAsync(credit.CustomerId, ct);
        var totals = TaxTotals(credit.Lines, credit.TaxCodeId, credit.TaxExemption);
        var voided = credit.Status == DocumentStatus.Voided;
        totals.Add(new FormTotal("Total credit", credit.Total, Emphasis: voided));
        if (!voided)
        {
            if (credit.Total != credit.Balance)
                totals.Add(new FormTotal("Used", credit.Balance - credit.Total));
            totals.Add(new FormTotal("Credit remaining", credit.Balance, Emphasis: true));
        }

        return (Form(company, customer, voided ? "Credit memo (void)" : "Credit memo", credit.Number, [("Date", D(credit.Date))], await LinesAsync(credit.Lines, credit.TaxCodeId, ct), totals, credit.Memo, job, credit.BillToAddress, credit.ShipToAddress), customer.Id);
    }

    private async Task<(SalesForm Form, Guid CustomerId)> ReceiptAsync(Company company, Guid id, CancellationToken ct)
    {
        var receipt = await store.GetReceiptAsync(id, ct) ?? throw new EntityNotFoundException("sales_receipt", id);
        var customer = receipt.CustomerId is { } customerId ? await RequiredCustomerAsync(customerId, ct) : null;
        var facts = new List<(string, string)> { ("Date", D(receipt.Date)) };
        if (receipt.Reference is not null)
            facts.Add(("Reference", receipt.Reference));
        var party = customer is null ? new FormParty("Cash sale") : CustomerParty(customer, receipt.BillToAddress);
        List<FormTotal> totals = [.. TaxTotals(receipt.Lines, receipt.TaxCodeId, receipt.TaxExemption), new FormTotal("Total paid", receipt.Total, Emphasis: true)];
        AddSavings(totals, receipt.Lines);
        var form = new SalesForm(receipt.Status == DocumentStatus.Voided ? "Sales receipt (void)" : "Sales receipt", receipt.Number, CompanyParty(company), party, facts, await LinesAsync(receipt.Lines, receipt.TaxCodeId, ct),
            totals, receipt.Memo, company.HomeCurrency.Code, Unstyled, ShipTo: customer is null ? null : Kept(customer, receipt.ShipToAddress) ?? ShipToParty(customer));
        return (form, customer?.Id ?? Guid.Empty);
    }

    /// <param name="job">The record the document is for, when <paramref name="customer"/> is billed for it: a project or job of theirs.</param>
    private static SalesForm Form(
        Company company, Customer customer, string title, string number, IReadOnlyList<(string, string)> facts,
        IReadOnlyList<FormLine> lines, IReadOnlyList<FormTotal> totals, string? message, Customer? job = null, string? billTo = null, string? shipTo = null) =>
        new(title, number, CompanyParty(company), CustomerParty(customer, billTo), job is null || job.Id == customer.Id ? facts : [.. facts, ("For", job.DisplayName)],
            lines, totals, message, company.HomeCurrency.Code, Unstyled, ShipTo: Kept(customer, shipTo) ?? ShipToParty(job ?? customer) ?? ShipToParty(customer));

    /// <summary>
    /// What price levels, quantity breaks and rules took off the list prices (manual prices don't count), rounded to the
    /// cent; zero when nothing did.
    /// </summary>
    public static decimal Savings(IEnumerable<SalesLine> lines) =>
        lines.Where(l => l.DiscountedFromList && l.PriceSource != SalesLine.ManualPrice).Sum(l => l.Saved);

    /// <summary>A "You saved" row under the totals when the customer's pricing saved them anything.</summary>
    private static void AddSavings(List<FormTotal> totals, IEnumerable<SalesLine> lines)
    {
        if (Savings(lines) is var saved and > 0m)
            totals.Add(new FormTotal(SavingsLabel, saved));
    }

    /// <summary>Subtotal and one row per tax line, ahead of the total; nothing for an untaxed document.</summary>
    public static List<FormTotal> TaxTotals(IReadOnlyList<SalesLine> lines, Guid? taxCodeId, string? exemption)
    {
        if (taxCodeId is null)
            return [];
        var totals = new List<FormTotal> { new("Subtotal", TaxCalculator.SalesAmount(lines)) };
        if (exemption is not null)
            totals.Add(new FormTotal($"Sales tax: exempt ({exemption})", 0m));
        else
            totals.AddRange(lines.Where(l => l.Kind == SalesLineKind.Tax).OrderBy(l => l.LineNumber).Select(l => new FormTotal(l.Description ?? "Sales tax", l.Amount)));
        return totals;
    }

    /// <summary>
    /// The printed lines, with what the design's columns may show: item name and SKU, lot and serial numbers (printed after
    /// the description unless the design has a column for them, so a customer's copy shows what was shipped), class, and
    /// whether each line is taxed (only on documents that charge tax).
    /// </summary>
    private async Task<List<FormLine>> LinesAsync(IReadOnlyList<SalesLine> lines, Guid? taxCodeId, CancellationToken ct)
    {
        var itemIds = lines.Select(l => l.ItemId).OfType<Guid>().Distinct().ToList();
        var items = itemIds.Count == 0 ? new Dictionary<Guid, Item>() : await store.GetItemsAsync(itemIds, ct);
        var classes = lines.Any(l => l.ClassId is not null)
            ? (await segments.ListAsync(SegmentKind.Class, ct: ct)).ToDictionary(c => c.Segment.Id, c => c.FullName)
            : [];
        string? ClassOf(SalesLine l) => l.ClassId is { } id ? classes.GetValueOrDefault(id) : null;

        return SalesLineGroups.Arrange(lines)
            .Select(shown => shown.Line is not { } l
                ? new FormLine(shown.Heading, null, null, shown.Subtotal, shown.Subtotal is null ? FormLineStyle.Heading : FormLineStyle.Subtotal)
                : l.Kind switch
                {
                    SalesLineKind.Note => new FormLine(l.Description, null, null, null, LineNumber: l.LineNumber),
                    SalesLineKind.DiscountPercent or SalesLineKind.DiscountAmount => new FormLine(l.Description, null, null, l.Amount, LineNumber: l.LineNumber,
                        Cells: Cells(null, null, ClassOf(l), null)),
                    _ => new FormLine(l.Description, l.Quantity, l.Rate, l.Amount, LineNumber: l.LineNumber,
                        Cells: Cells(l.ItemId is { } item ? items.GetValueOrDefault(item) : null, l.Lots, ClassOf(l), taxCodeId is null ? null : l.Taxable)),
                })
            .ToList();
    }

    /// <summary>The built-in column values of a line (blank ones left out).</summary>
    internal static Dictionary<string, string> Cells(Item? item, string? lots, string? className, bool? taxable)
    {
        var cells = new Dictionary<string, string>();
        if (item is not null)
            cells[FormColumns.Item] = item.Name;
        if (item?.Barcode is { } barcode)
            cells[FormColumns.Sku] = barcode;
        if (!string.IsNullOrWhiteSpace(lots))
            cells[FormColumns.Lots] = lots;
        if (className is not null)
            cells[FormColumns.Class] = className;
        if (taxable is { } taxed)
            cells[FormColumns.Taxable] = taxed ? "Yes" : "No";
        return cells;
    }

    /// <summary>Who a document for this customer is addressed to, and the customer itself: they differ when it's billed with its parent.</summary>
    private async Task<(Customer Billed, Customer Job)> BilledAsync(Guid id, CancellationToken ct)
    {
        var job = await RequiredCustomerAsync(id, ct);
        if (!job.BillWithParent)
            return (job, job);
        var all = (await store.ListCustomersAsync(ct)).ToDictionary(c => c.Id);
        return (all[CustomerTree.BillTo(id, all)], job);
    }

    private async Task<Customer> RequiredCustomerAsync(Guid id, CancellationToken ct) =>
        await store.GetCustomerAsync(id, ct) ?? throw new EntityNotFoundException("customer", id);

    internal static FormParty CompanyParty(Company c) => new(c.LegalName ?? c.Name, c.Address, c.Email, c.Phone);

    /// <param name="billTo">The address kept on the document when it was made; null for the customer's address today.</param>
    internal static FormParty CustomerParty(Customer c, string? billTo = null) =>
        new(c.CompanyName is { } company && company != c.DisplayName ? $"{c.DisplayName}\n{company}" : c.DisplayName, billTo ?? c.BillingAddress, c.Email, c.Phone);

    private static FormParty? Kept(Customer c, string? shipTo) => shipTo is null ? null : new FormParty(c.CompanyName ?? c.DisplayName, shipTo);

    /// <summary>Forms are built with this, then <see cref="FormDesignService.ApplyAsync(SalesForm, FormDesign, FormDocuments, CancellationToken)"/> gives them their design's look.</summary>
    internal static readonly FormStyle Unstyled = new(FormTemplate.Classic, Company.DefaultAccentColor, null);

    /// <summary>The customer's shipping address, when they have one.</summary>
    internal static FormParty? ShipToParty(Customer c) =>
        c.ShippingAddress is { } address ? new FormParty(c.CompanyName ?? c.DisplayName, address) : null;

    private static string D(DateOnly date) => date.ToString("d", CultureInfo.CurrentCulture);

    /// <summary>"Studio Books Invoice 1001.pdf", without characters file systems reject.</summary>
    internal static string FileName(Company company, string title, string number)
    {
        var name = $"{company.Name} {title} {number}.pdf";
        var invalid = Path.GetInvalidFileNameChars();
        return new string(name.Select(ch => invalid.Contains(ch) ? '-' : ch).ToArray());
    }
}
