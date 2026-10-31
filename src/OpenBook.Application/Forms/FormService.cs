using System.Globalization;
using OpenBook.Application.Companies;
using OpenBook.Application.Sales;
using OpenBook.Domain.Common;
using OpenBook.Domain.Companies;
using OpenBook.Domain.Sales;
using OpenBook.Domain.SalesTax;

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
public sealed class FormService(ICompanyStore companies, ISalesStore store, ReceivablesService receivables, IFormRenderer renderer, CustomFields.CustomFieldService customFields)
{
    public async Task<RenderedForm> RenderAsync(FormKind kind, Guid id, CancellationToken ct = default)
    {
        var company = await companies.GetRequiredCompanyAsync(ct);
        var form = await BuildAsync(company, kind, id, ct);
        return new RenderedForm(FileName(company, form.Form.Title, form.Form.Number), renderer.Render(form.Form), form.CustomerId, $"{form.Form.Title} {form.Form.Number}");
    }

    /// <summary>The form's content, as <see cref="RenderAsync"/> prints it.</summary>
    public async Task<SalesForm> BuildAsync(FormKind kind, Guid id, CancellationToken ct = default) =>
        (await BuildAsync(await companies.GetRequiredCompanyAsync(ct), kind, id, ct)).Form;

    private async Task<(SalesForm Form, Guid CustomerId)> BuildAsync(Company company, FormKind kind, Guid id, CancellationToken ct)
    {
        var (form, customerId) = kind switch
        {
            FormKind.Invoice => await InvoiceAsync(company, id, ct),
            FormKind.Estimate => await EstimateAsync(company, id, ct),
            FormKind.CreditMemo => await CreditMemoAsync(company, id, ct),
            _ => await ReceiptAsync(company, id, ct),
        };
        return (await customFields.DecorateAsync(form, id, ct), customerId);
    }

    public async Task<RenderedForm> RenderStatementAsync(Guid customerId, DateOnly from, DateOnly to, CancellationToken ct = default)
    {
        var company = await companies.GetRequiredCompanyAsync(ct);
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
            Style(company));
        return new RenderedForm(FileName(company, "Statement", to.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)), renderer.Render(form), customerId,
            $"Statement {to.ToString("d", CultureInfo.CurrentCulture)}");
    }

    private async Task<(SalesForm Form, Guid CustomerId)> InvoiceAsync(Company company, Guid id, CancellationToken ct)
    {
        var invoice = await store.GetInvoiceAsync(id, ct) ?? throw new EntityNotFoundException("invoice", id);
        var customer = await RequiredCustomerAsync(invoice.CustomerId, ct);
        var facts = new List<(string, string)>
        {
            ("Date", D(invoice.Date)),
            ("Due date", D(invoice.DueDate)),
            ("Terms", new PaymentTerms(invoice.DueDate.DayNumber - invoice.Date.DayNumber, invoice.EarlyPaymentDiscountPercent,
                invoice.EarlyPaymentDiscountDate is { } d ? d.DayNumber - invoice.Date.DayNumber : 0).Label),
        };
        var totals = TaxTotals(invoice.Lines, invoice.TaxCodeId, invoice.TaxExemption);
        totals.Add(new FormTotal("Total", invoice.Total));
        if (invoice.AmountPaid > 0m)
            totals.Add(new FormTotal("Paid and credited", -invoice.AmountPaid));
        totals.Add(new FormTotal("Balance due", invoice.Balance, Emphasis: true));
        AddSavings(totals, invoice.Lines);
        var title = invoice.Status == DocumentStatus.Voided ? "Invoice (void)" : "Invoice";
        return (Form(company, customer, title, invoice.Number, facts, invoice.Lines, totals, invoice.Memo), customer.Id);
    }

    private async Task<(SalesForm Form, Guid CustomerId)> EstimateAsync(Company company, Guid id, CancellationToken ct)
    {
        var estimate = await store.GetEstimateAsync(id, ct) ?? throw new EntityNotFoundException("estimate", id);
        var customer = await RequiredCustomerAsync(estimate.CustomerId, ct);
        var facts = new List<(string, string)> { ("Date", D(estimate.Date)) };
        if (estimate.ExpirationDate is { } expires)
            facts.Add(("Valid until", D(expires)));
        List<FormTotal> totals = [.. TaxTotals(estimate.Lines, estimate.TaxCodeId, estimate.TaxExemption), new FormTotal("Total", estimate.Total, Emphasis: true)];
        AddSavings(totals, estimate.Lines);
        return (Form(company, customer, "Estimate", estimate.Number, facts, estimate.Lines, totals, estimate.Memo), customer.Id);
    }

    private async Task<(SalesForm Form, Guid CustomerId)> CreditMemoAsync(Company company, Guid id, CancellationToken ct)
    {
        var credit = await store.GetCreditMemoAsync(id, ct) ?? throw new EntityNotFoundException("credit_memo", id);
        var customer = await RequiredCustomerAsync(credit.CustomerId, ct);
        var totals = TaxTotals(credit.Lines, credit.TaxCodeId, credit.TaxExemption);
        totals.Add(new FormTotal("Total credit", credit.Total));
        if (credit.Total != credit.Balance)
            totals.Add(new FormTotal("Used", credit.Balance - credit.Total));
        totals.Add(new FormTotal("Credit remaining", credit.Balance, Emphasis: true));
        return (Form(company, customer, "Credit memo", credit.Number, [("Date", D(credit.Date))], credit.Lines, totals, credit.Memo), customer.Id);
    }

    private async Task<(SalesForm Form, Guid CustomerId)> ReceiptAsync(Company company, Guid id, CancellationToken ct)
    {
        var receipt = await store.GetReceiptAsync(id, ct) ?? throw new EntityNotFoundException("sales_receipt", id);
        var customer = receipt.CustomerId is { } customerId ? await RequiredCustomerAsync(customerId, ct) : null;
        var facts = new List<(string, string)> { ("Date", D(receipt.Date)) };
        if (receipt.Reference is not null)
            facts.Add(("Reference", receipt.Reference));
        var party = customer is null ? new FormParty("Cash sale") : CustomerParty(customer);
        List<FormTotal> totals = [.. TaxTotals(receipt.Lines, receipt.TaxCodeId, receipt.TaxExemption), new FormTotal("Total paid", receipt.Total, Emphasis: true)];
        AddSavings(totals, receipt.Lines);
        var form = new SalesForm("Sales receipt", receipt.Number, CompanyParty(company), party, facts, Lines(receipt.Lines),
            totals, receipt.Memo, company.HomeCurrency.Code, Style(company));
        return (form, customer?.Id ?? Guid.Empty);
    }

    private static SalesForm Form(
        Company company, Customer customer, string title, string number, IReadOnlyList<(string, string)> facts,
        IEnumerable<SalesLine> lines, IReadOnlyList<FormTotal> totals, string? message) =>
        new(title, number, CompanyParty(company), CustomerParty(customer), facts, Lines(lines), totals, message, company.HomeCurrency.Code, Style(company));

    /// <summary>
    /// What price levels, quantity breaks and rules took off the list prices (manual prices don't count), rounded to the
    /// cent; zero when nothing did.
    /// </summary>
    public static decimal Savings(IEnumerable<SalesLine> lines) =>
        lines.Where(l => l.Kind == SalesLineKind.Item && l.PriceSource is not null && l.PriceSource != SalesLine.ManualPrice).Sum(l => l.Saved);

    /// <summary>A "You saved" row under the totals when the customer's pricing saved them anything.</summary>
    private static void AddSavings(List<FormTotal> totals, IEnumerable<SalesLine> lines)
    {
        if (Savings(lines) is var saved and > 0m)
            totals.Add(new FormTotal("You saved", saved));
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

    private static List<FormLine> Lines(IEnumerable<SalesLine> lines) => SalesLineGroups.Arrange(lines)
        .Select(shown => shown.Line is not { } l
            ? new FormLine(shown.Heading, null, null, shown.Subtotal, shown.Subtotal is null ? FormLineStyle.Heading : FormLineStyle.Subtotal)
            : l.Kind switch
            {
                SalesLineKind.Note => new FormLine(l.Description, null, null, null, LineNumber: l.LineNumber),
                SalesLineKind.DiscountPercent or SalesLineKind.DiscountAmount => new FormLine(l.Description, null, null, l.Amount, LineNumber: l.LineNumber),
                // Lot and serial numbers follow the description, so a customer's copy shows what was shipped.
                _ => new FormLine(l.Lots is { } lots ? $"{l.Description} · {lots}" : l.Description, l.Quantity, l.Rate, l.Amount, LineNumber: l.LineNumber),
            })
        .ToList();

    private async Task<Customer> RequiredCustomerAsync(Guid id, CancellationToken ct) =>
        await store.GetCustomerAsync(id, ct) ?? throw new EntityNotFoundException("customer", id);

    internal static FormParty CompanyParty(Company c) => new(c.LegalName ?? c.Name, c.Address, c.Email, c.Phone);

    internal static FormParty CustomerParty(Customer c) =>
        new(c.CompanyName is { } company && company != c.DisplayName ? $"{c.DisplayName}\n{company}" : c.DisplayName, c.BillingAddress, c.Email, c.Phone);

    internal static FormStyle Style(Company c) => new(c.FormTemplate, c.FormAccentColor, c.FormFooter);

    private static string D(DateOnly date) => date.ToString("d", CultureInfo.CurrentCulture);

    /// <summary>"Studio Books Invoice 1001.pdf", without characters file systems reject.</summary>
    internal static string FileName(Company company, string title, string number)
    {
        var name = $"{company.Name} {title} {number}.pdf";
        var invalid = Path.GetInvalidFileNameChars();
        return new string(name.Select(ch => invalid.Contains(ch) ? '-' : ch).ToArray());
    }
}
