using System.Globalization;
using OpenBook.Application.Companies;
using OpenBook.Application.Sales;
using OpenBook.Domain.Common;
using OpenBook.Domain.Companies;
using OpenBook.Domain.Sales;

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
public sealed class FormService(ICompanyStore companies, ISalesStore store, ReceivablesService receivables, IFormRenderer renderer)
{
    public async Task<RenderedForm> RenderAsync(FormKind kind, Guid id, CancellationToken ct = default)
    {
        var company = await companies.GetRequiredCompanyAsync(ct);
        var form = kind switch
        {
            FormKind.Invoice => await InvoiceAsync(company, id, ct),
            FormKind.Estimate => await EstimateAsync(company, id, ct),
            FormKind.CreditMemo => await CreditMemoAsync(company, id, ct),
            _ => await ReceiptAsync(company, id, ct),
        };
        return new RenderedForm(FileName(company, form.Form.Title, form.Form.Number), renderer.Render(form.Form), form.CustomerId, $"{form.Form.Title} {form.Form.Number}");
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
        var totals = new List<FormTotal> { new("Total", invoice.Total) };
        if (invoice.AmountPaid > 0m)
            totals.Add(new FormTotal("Paid and credited", -invoice.AmountPaid));
        totals.Add(new FormTotal("Balance due", invoice.Balance, Emphasis: true));
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
        return (Form(company, customer, "Estimate", estimate.Number, facts, estimate.Lines, [new FormTotal("Total", estimate.Total, Emphasis: true)], estimate.Memo), customer.Id);
    }

    private async Task<(SalesForm Form, Guid CustomerId)> CreditMemoAsync(Company company, Guid id, CancellationToken ct)
    {
        var credit = await store.GetCreditMemoAsync(id, ct) ?? throw new EntityNotFoundException("credit_memo", id);
        var customer = await RequiredCustomerAsync(credit.CustomerId, ct);
        var totals = new List<FormTotal> { new("Total credit", credit.Total) };
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
        var form = new SalesForm("Sales receipt", receipt.Number, CompanyParty(company), party, facts, Lines(receipt.Lines),
            [new FormTotal("Total paid", receipt.Total, Emphasis: true)], receipt.Memo, company.HomeCurrency.Code, Style(company));
        return (form, customer?.Id ?? Guid.Empty);
    }

    private static SalesForm Form(
        Company company, Customer customer, string title, string number, IReadOnlyList<(string, string)> facts,
        IEnumerable<SalesLine> lines, IReadOnlyList<FormTotal> totals, string? message) =>
        new(title, number, CompanyParty(company), CustomerParty(customer), facts, Lines(lines), totals, message, company.HomeCurrency.Code, Style(company));

    private static List<FormLine> Lines(IEnumerable<SalesLine> lines) => lines
        .OrderBy(l => l.LineNumber)
        .Select(l => l.Kind switch
        {
            SalesLineKind.Note => new FormLine(l.Description, null, null, null),
            SalesLineKind.DiscountPercent or SalesLineKind.DiscountAmount => new FormLine(l.Description, null, null, l.Amount),
            _ => new FormLine(l.Description, l.Quantity, l.Rate, l.Amount),
        })
        .ToList();

    private async Task<Customer> RequiredCustomerAsync(Guid id, CancellationToken ct) =>
        await store.GetCustomerAsync(id, ct) ?? throw new EntityNotFoundException("customer", id);

    internal static FormParty CompanyParty(Company c) => new(c.LegalName ?? c.Name, c.Address, c.Email, c.Phone);

    private static FormParty CustomerParty(Customer c) =>
        new(c.CompanyName is { } company && company != c.DisplayName ? $"{c.DisplayName}\n{company}" : c.DisplayName, c.BillingAddress, c.Email, c.Phone);

    private static FormStyle Style(Company c) => new(c.FormTemplate, c.FormAccentColor, c.FormFooter);

    private static string D(DateOnly date) => date.ToString("d", CultureInfo.CurrentCulture);

    /// <summary>"Studio Books Invoice 1001.pdf", without characters file systems reject.</summary>
    private static string FileName(Company company, string title, string number)
    {
        var name = $"{company.Name} {title} {number}.pdf";
        var invalid = Path.GetInvalidFileNameChars();
        return new string(name.Select(ch => invalid.Contains(ch) ? '-' : ch).ToArray());
    }
}
