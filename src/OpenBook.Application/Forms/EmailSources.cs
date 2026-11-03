using OpenBook.Application.Companies;
using OpenBook.Application.Nonprofit;
using OpenBook.Application.Purchases;
using OpenBook.Application.Sales;
using OpenBook.Domain.Auditing;
using OpenBook.Domain.Common;
using OpenBook.Domain.Companies;
using OpenBook.Domain.Email;
using OpenBook.Domain.Nonprofit;
using OpenBook.Domain.Purchases;
using OpenBook.Domain.Sales;
using static OpenBook.Application.Forms.EmailService;

namespace OpenBook.Application.Forms;

/// <summary>Invoices, estimates, credit memos, sales receipts and statements.</summary>
internal sealed class SalesEmailSource(
    ISalesStore sales, ICompanyStore companies, FormService forms, ReceivablesService receivables, OnlinePaymentService online, IClock clock) : IEmailSource
{
    public IReadOnlyCollection<EmailKind> Kinds { get; } =
        [EmailKind.Invoice, EmailKind.Reminder, EmailKind.Estimate, EmailKind.CreditMemo, EmailKind.SalesReceipt, EmailKind.Statement, EmailKind.PaymentReceipt];

    public async Task<EmailContext> ContextAsync(EmailSource source, CancellationToken ct)
    {
        var company = await companies.GetRequiredCompanyAsync(ct);
        switch (source.Kind)
        {
            case EmailKind.Invoice or EmailKind.Reminder:
            {
                var i = await sales.GetInvoiceAsync(source.Id, ct) ?? throw new EntityNotFoundException("invoice", source.Id);
                var customer = await CustomerAsync(i.CustomerId, ct);
                var terms = new PaymentTerms(i.DueDate.DayNumber - i.Date.DayNumber, i.EarlyPaymentDiscountPercent,
                    i.EarlyPaymentDiscountDate is { } d ? d.DayNumber - i.Date.DayNumber : 0);
                return Document(company, "invoice", i.Id, "Invoice", i.Number, customer, FormDocuments.Invoice, new()
                {
                    ["number"] = i.Number, ["date"] = D(i.Date), ["due"] = D(i.DueDate), ["total"] = M(i.Total), ["balance"] = M(i.Balance),
                    ["terms"] = terms.Label,
                }, payOnline: i.IsOpen && (await online.GetSettingsAsync(ct)).IsReady);
            }

            case EmailKind.Estimate:
            {
                var e = await sales.GetEstimateAsync(source.Id, ct) ?? throw new EntityNotFoundException(EstimateService.EntityType, source.Id);
                return Document(company, EstimateService.EntityType, e.Id, "Estimate", e.Number, await CustomerAsync(e.CustomerId, ct), FormDocuments.Estimate, new()
                {
                    ["number"] = e.Number, ["date"] = D(e.Date), ["total"] = M(e.Total), ["valid_until"] = e.ExpirationDate is { } x ? D(x) : "",
                });
            }

            case EmailKind.CreditMemo:
            {
                var c = await sales.GetCreditMemoAsync(source.Id, ct) ?? throw new EntityNotFoundException("credit_memo", source.Id);
                return Document(company, "credit_memo", c.Id, "Credit memo", c.Number, await CustomerAsync(c.CustomerId, ct), FormDocuments.CreditMemo, new()
                {
                    ["number"] = c.Number, ["date"] = D(c.Date), ["total"] = M(c.Total),
                });
            }

            case EmailKind.SalesReceipt:
            {
                var r = await sales.GetReceiptAsync(source.Id, ct) ?? throw new EntityNotFoundException("sales_receipt", source.Id);
                var customer = r.CustomerId is { } id ? await sales.GetCustomerAsync(id, ct) : null;
                return Document(company, "sales_receipt", r.Id, "Sales receipt", r.Number, customer, FormDocuments.SalesReceipt, new()
                {
                    ["number"] = r.Number, ["date"] = D(r.Date), ["total"] = M(r.Total),
                });
            }

            case EmailKind.PaymentReceipt:
            {
                var p = await sales.GetPaymentAsync(source.Id, ct) ?? throw new EntityNotFoundException(PaymentService.EntityType, source.Id);
                var customer = await CustomerAsync(p.CustomerId, ct);
                var invoices = (await sales.GetInvoicesAsync(p.Applications.Select(a => a.InvoiceId).ToList(), ct)).ToDictionary(i => i.Id);
                var applied = string.Join('\n', p.Applications.Where(a => invoices.ContainsKey(a.InvoiceId)).Select(a =>
                {
                    var invoice = invoices[a.InvoiceId];
                    var left = invoice.Balance == 0m ? "paid in full" : $"{M(invoice.Balance)} still to pay";
                    return $"- Invoice {invoice.Number}: {M(a.Amount)}{(a.Discount > 0m ? $" (and {M(a.Discount)} early payment discount)" : "")}, {left}";
                }));
                var paidBy = p.Method switch
                {
                    null => p.Reference is { } r ? $"with reference {r}" : "",
                    PaymentMethod.Check => p.Reference is { } n ? $"by check no. {n}" : "by check",
                    { } m => $"by {PaymentMethods.Label(m).ToLowerInvariant()}{(p.Reference is { } r2 ? $", reference {r2}" : "")}",
                };
                var owed = (await sales.ListOpenInvoicesAsync(customer.Id, ct)).Sum(i => i.Balance);
                return new EmailContext(PaymentService.EntityType, p.Id, customer.DisplayName, $"Payment of {M(p.Amount)}", "", customer.Email, new Dictionary<string, string>
                {
                    ["customer"] = customer.DisplayName, ["amount"] = M(p.Amount), ["date"] = D(p.Date), ["paid_by"] = paidBy, ["applied"] = applied,
                    ["credit"] = p.Unapplied > 0m ? $"{M(p.Unapplied)} is kept as a credit for your next invoice." : "",
                    ["balance"] = M(owed), ["company"] = company.Name,
                }, null, customer,
                    Refusal: p.Status == DocumentStatus.Voided ? ("payment.voided", "This payment is voided, so there's no receipt to send.") : null, Attaches: false);
            }

            default:
            {
                var customer = await CustomerAsync(source.Id, ct);
                var (from, to) = source.Period(CompanyTime.TodayAt(clock.UtcNow, CompanyTime.Zone));
                var statement = await receivables.StatementAsync(customer.Id, from, to, ct);
                return new EmailContext("customer", customer.Id, customer.DisplayName, $"Statement {D(to)}",
                    FormService.FileName(company, "Statement", to.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture)),
                    customer.Email, new Dictionary<string, string>
                    {
                        ["customer"] = customer.DisplayName, ["from"] = D(from), ["to"] = D(to), ["balance"] = M(statement.ClosingBalance), ["company"] = company.Name,
                    }, FormDocuments.Statement, customer);
            }
        }
    }

    public async Task<RenderedForm> RenderAsync(EmailSource source, Guid? designId, CancellationToken ct)
    {
        if (source.Kind == EmailKind.PaymentReceipt)
            throw new InvalidOperationException("Payment receipts have nothing attached.");
        if (source.Kind != EmailKind.Statement)
            return await forms.RenderAsync(FormKindOf(source.Kind), source.Id, designId, ct);
        var (from, to) = source.Period(CompanyTime.TodayAt(clock.UtcNow, CompanyTime.Zone));
        return await forms.RenderStatementAsync(source.Id, from, to, designId, ct);
    }

    public async Task<string?> PayLinkAsync(EmailSource source, CancellationToken ct)
    {
        if (source.Kind is not (EmailKind.Invoice or EmailKind.Reminder) || !(await online.GetSettingsAsync(ct)).IsReady || await sales.GetInvoiceAsync(source.Id, ct) is not { IsOpen: true })
            return null;
        try
        {
            return (await online.LinkForInvoiceAsync(source.Id, ct)).Url;
        }
        catch (DomainException)
        {
            // The provider can't be reached: the email goes without the button.
            return null;
        }
    }

    public async Task<AuditRecord?> SentAsync(EmailSource source, string recipients, string title, CancellationToken ct)
    {
        var now = clock.UtcNow;
        switch (source.Kind)
        {
            case EmailKind.Invoice or EmailKind.Reminder:
                (await sales.GetInvoiceAsync(source.Id, ct))!.MarkEmailed(now);
                break;
            case EmailKind.Estimate:
                (await sales.GetEstimateAsync(source.Id, ct))!.MarkEmailed(now);
                break;
            case EmailKind.SalesReceipt:
                (await sales.GetReceiptAsync(source.Id, ct))!.MarkEmailed(now);
                break;
        }

        return null;
    }

    private static FormKind FormKindOf(EmailKind kind) => kind switch
    {
        EmailKind.Invoice or EmailKind.Reminder => FormKind.Invoice,
        EmailKind.Estimate => FormKind.Estimate,
        EmailKind.CreditMemo => FormKind.CreditMemo,
        _ => FormKind.SalesReceipt,
    };

    private async Task<Customer> CustomerAsync(Guid id, CancellationToken ct) =>
        await sales.GetCustomerAsync(id, ct) ?? throw new EntityNotFoundException("customer", id);

    private static EmailContext Document(
        Company company, string entityType, Guid id, string title, string number, Customer? customer, FormDocuments document,
        Dictionary<string, string> values, bool payOnline = false)
    {
        values["customer"] = customer?.DisplayName ?? "";
        values["company"] = company.Name;
        return new EmailContext(entityType, id, customer?.DisplayName ?? $"{title} {number}", $"{title} {number}", FormService.FileName(company, title, number),
            customer?.Email, values, document, customer, payOnline);
    }
}

/// <summary>Purchase orders, to the vendor.</summary>
internal sealed class PurchaseOrderEmailSource(IPurchasesStore store, ICompanyStore companies, PurchaseOrderForms forms) : IEmailSource
{
    public IReadOnlyCollection<EmailKind> Kinds { get; } = [EmailKind.PurchaseOrder];

    public async Task<EmailContext> ContextAsync(EmailSource source, CancellationToken ct)
    {
        var order = await store.GetPurchaseOrderAsync(source.Id, ct) ?? throw new EntityNotFoundException(PurchaseOrderService.EntityType, source.Id);
        var vendor = await store.GetVendorAsync(order.VendorId, ct);
        var company = await companies.GetRequiredCompanyAsync(ct);
        return new EmailContext(PurchaseOrderService.EntityType, order.Id, vendor?.DisplayName ?? $"Purchase order {order.Number}", $"Purchase order {order.Number}",
            FormService.FileName(company, "Purchase order", order.Number), vendor?.Email, new Dictionary<string, string>
            {
                ["vendor"] = vendor?.DisplayName ?? "", ["number"] = order.Number, ["date"] = D(order.Date), ["expected"] = order.ExpectedDate is { } x ? D(x) : "",
                ["total"] = M(order.Total), ["company"] = company.Name,
            }, FormDocuments.PurchaseOrder,
            Refusal: order.Status == PurchaseOrderStatus.Cancelled ? ("purchase_order.cancelled", "This order is cancelled.") : null);
    }

    public Task<RenderedForm> RenderAsync(EmailSource source, Guid? designId, CancellationToken ct) => forms.RenderAsync(source.Id, designId, ct);

    public Task<string?> PayLinkAsync(EmailSource source, CancellationToken ct) => Task.FromResult<string?>(null);

    public Task<AuditRecord?> SentAsync(EmailSource source, string recipients, string title, CancellationToken ct) => Task.FromResult<AuditRecord?>(null);
}

/// <summary>Gift acknowledgments and giving statements, to the donor.</summary>
internal sealed class GiftEmailSource(INonprofitStore store, ISalesStore sales, ICompanyStore companies, GiftService gifts) : IEmailSource
{
    public IReadOnlyCollection<EmailKind> Kinds { get; } = [EmailKind.GiftAcknowledgment, EmailKind.GivingStatement];

    public async Task<EmailContext> ContextAsync(EmailSource source, CancellationToken ct)
    {
        var company = await companies.GetRequiredCompanyAsync(ct);
        if (source.Kind == EmailKind.GiftAcknowledgment)
        {
            var gift = await store.GetDonationAsync(source.Id, ct) ?? throw new EntityNotFoundException(GiftService.DonationEntity, source.Id);
            var donor = await DonorAsync(gift.DonorId, ct);
            var number = gift.Number.ToString(System.Globalization.CultureInfo.InvariantCulture);
            return new EmailContext(GiftService.DonationEntity, gift.Id, donor.DisplayName, $"Gift acknowledgment {number}",
                FormService.FileName(company, "Gift acknowledgment", number), donor.Email, new Dictionary<string, string>
                {
                    ["donor"] = donor.DisplayName, ["number"] = number, ["date"] = D(gift.Date), ["amount"] = M(gift.Amount), ["company"] = company.Name,
                }, Refusal: gift.Status != DocumentStatus.Posted ? ("donation.voided", "This gift is voided.") : null);
        }

        var year = source.Year ?? throw new DomainException("email.year_required", "Choose the year of the giving statement.");
        var giver = await DonorAsync(source.Id, ct);
        var total = (await store.ListDonationsAsync(ct))
            .Where(d => d.DonorId == giver.Id && d.Status == DocumentStatus.Posted && d.Date.Year == year && d.Kind != GiftKind.InKind).Sum(d => d.Amount);
        return new EmailContext("customer", giver.Id, giver.DisplayName, $"Giving statement {year}", FormService.FileName(company, $"Giving statement {year}", giver.DisplayName),
            giver.Email, new Dictionary<string, string>
            {
                ["donor"] = giver.DisplayName, ["year"] = year.ToString(System.Globalization.CultureInfo.InvariantCulture), ["total"] = M(total), ["company"] = company.Name,
            });
    }

    public Task<RenderedForm> RenderAsync(EmailSource source, Guid? designId, CancellationToken ct) =>
        source.Kind == EmailKind.GiftAcknowledgment ? gifts.AcknowledgmentAsync(source.Id, ct) : gifts.YearStatementAsync(source.Id, source.Year!.Value, ct);

    public Task<string?> PayLinkAsync(EmailSource source, CancellationToken ct) => Task.FromResult<string?>(null);

    public async Task<AuditRecord?> SentAsync(EmailSource source, string recipients, string title, CancellationToken ct) =>
        source.Kind == EmailKind.GiftAcknowledgment ? await gifts.MarkAcknowledgedAsync(source.Id, ct) : null;

    private async Task<Customer> DonorAsync(Guid id, CancellationToken ct) =>
        await sales.GetCustomerAsync(id, ct) ?? throw new DomainException("donation.donor_required", "Choose the donor.");
}
