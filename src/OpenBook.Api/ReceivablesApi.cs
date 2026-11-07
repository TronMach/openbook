using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using OpenBook.Application.Companies;
using OpenBook.Application.Forms;
using OpenBook.Application.Sales;
using OpenBook.Application;
using OpenBook.Domain.Common;
using OpenBook.Domain.Companies;
using OpenBook.Domain.Ledger;
using OpenBook.Domain.Sales;

namespace OpenBook.Api;

public sealed record EstimateResponse(
    Guid Id, string Number, Guid CustomerId, DateOnly Date, DateOnly? ExpirationDate, string? Memo, decimal Total,
    EstimateStatus Status, Guid? InvoiceId, DateTimeOffset? LastEmailedAt, IReadOnlyList<SalesLineResponse> Lines,
    Guid? TaxCodeId = null, string? TaxExemption = null, decimal Subtotal = 0m, decimal TaxTotal = 0m)
{
    public static EstimateResponse From(Estimate e) => new(
        e.Id, e.Number, e.CustomerId, e.Date, e.ExpirationDate, e.Memo, e.Total, e.Status, e.InvoiceId, e.LastEmailedAt,
        e.Lines.Select(SalesLineResponse.From).ToList(), e.TaxCodeId, e.TaxExemption, e.Subtotal, e.TaxTotal);
}

public sealed record CreditMemoResponse(
    Guid Id, string Number, Guid CustomerId, DateOnly Date, string? Memo, decimal Total, decimal AmountApplied, decimal AmountRefunded,
    decimal Balance, DocumentStatus Status, Guid JournalEntryId, IReadOnlyList<SalesLineResponse> Lines,
    Guid? TaxCodeId = null, string? TaxExemption = null, decimal Subtotal = 0m, decimal TaxTotal = 0m)
{
    public static CreditMemoResponse From(CreditMemo c) => new(
        c.Id, c.Number, c.CustomerId, c.Date, c.Memo, c.Total, c.AmountApplied, c.AmountRefunded, c.Balance, c.Status, c.JournalEntryId,
        c.Lines.Select(SalesLineResponse.From).ToList(), c.TaxCodeId, c.TaxExemption, c.Subtotal, c.TaxTotal);
}

public sealed record RefundResponse(
    Guid Id, Guid CustomerId, DateOnly Date, decimal Amount, Guid PaymentAccountId, Guid? CreditMemoId, Guid? CustomerPaymentId,
    string? Reference, string? Memo, DocumentStatus Status, Guid JournalEntryId)
{
    public static RefundResponse From(CustomerRefund r) => new(
        r.Id, r.CustomerId, r.Date, r.Amount, r.PaymentAccountId, r.CreditMemoId, r.CustomerPaymentId, r.Reference, r.Memo, r.Status, r.JournalEntryId);
}

public sealed record WriteOffResponse(
    Guid Id, Guid CustomerId, Guid InvoiceId, DateOnly Date, decimal Amount, Guid ExpenseAccountId, string? Memo, DocumentStatus Status, Guid JournalEntryId)
{
    public static WriteOffResponse From(BadDebtWriteOff w) =>
        new(w.Id, w.CustomerId, w.InvoiceId, w.Date, w.Amount, w.ExpenseAccountId, w.Memo, w.Status, w.JournalEntryId);
}

public sealed record WriteOffCandidateResponse(Guid InvoiceId, string Number, Guid CustomerId, DateOnly DueDate, int DaysPastDue, decimal Balance);

public sealed record CollectionSettingsResponse(
    bool LateFeesEnabled, bool LateFeesAutomatic, decimal LateFeePercent, decimal LateFeeAmount, int LateFeeGraceDays, int LateFeeRepeatDays,
    Guid? LateFeeItemId, bool RemindersEnabled, IReadOnlyList<int> ReminderSchedule, string ReminderSubject, string ReminderBody)
{
    public static CollectionSettingsResponse From(CollectionSettings s) => new(
        s.LateFeesEnabled, s.LateFeesAutomatic, s.LateFeePercent, s.LateFeeAmount, s.LateFeeGraceDays, s.LateFeeRepeatDays, s.LateFeeItemId,
        s.RemindersEnabled, s.ReminderSteps, s.ReminderSubject, s.ReminderBody);
}

public sealed record LateFeeDueResponse(Guid InvoiceId, string Number, Guid CustomerId, int DaysPastDue, DateOnly? LastFee, decimal Balance, decimal Fee);

public sealed record ReminderDueResponse(Guid InvoiceId, string Number, Guid CustomerId, int Step, string To);

public sealed record ReminderResultResponse(Guid InvoiceId, string Number, int Step, string? Error);

/// <param name="InvoiceIds">Null for every one due.</param>
public sealed record ChargeLateFeesRequest(DateOnly? Date, IReadOnlyList<Guid>? InvoiceIds);

/// <param name="InvoiceIds">Null for every one due today.</param>
public sealed record SendRemindersRequest(IReadOnlyList<Guid>? InvoiceIds);

public sealed record SalesOrderResponse(
    Guid Id, string Number, Guid CustomerId, DateOnly Date, DateOnly? ExpectedDate, string? CustomerReference, string? Memo, decimal Total,
    SalesOrderStatus Status, Guid? TaxCodeId, IReadOnlyList<SalesLineResponse> Lines)
{
    public static SalesOrderResponse From(SalesOrder o) => new(
        o.Id, o.Number, o.CustomerId, o.Date, o.ExpectedDate, o.CustomerReference, o.Memo, o.Total, o.Status, o.TaxCodeId,
        o.Lines.Select(SalesLineResponse.From).ToList());
}

public sealed record InvoicingProgressResponse(IReadOnlyList<Guid> InvoiceIds, decimal InvoicedSubtotal, IReadOnlyDictionary<int, decimal> Invoiced, IReadOnlyDictionary<int, decimal> Remaining)
{
    public static InvoicingProgressResponse From(InvoicingProgress p) =>
        new(p.Invoices.Select(i => i.Id).ToList(), p.InvoicedSubtotal, p.Invoiced, p.Remaining);
}

public sealed record RetainerResponse(
    Guid Id, Guid CustomerId, DateOnly Date, decimal Amount, Guid DepositAccountId, Guid LiabilityAccountId, string? Reference, string? Memo,
    DocumentStatus Status, decimal Balance, IReadOnlyList<RetainerApplication> Applications, IReadOnlyList<RetainerRefund> Refunds)
{
    public static RetainerResponse From(Retainer r) => new(
        r.Id, r.CustomerId, r.Date, r.Amount, r.DepositAccountId, r.LiabilityAccountId, r.Reference, r.Memo, r.Status, r.Balance, r.Applications, r.Refunds);
}

public sealed record BillableExpenseResponse(
    Guid Id, BillableSource Kind, Guid DocumentId, int LineNumber, Guid CustomerId, DateOnly Date, string Description, decimal Cost, decimal MarkupPercent,
    decimal Amount, Guid? InvoiceId, bool IsBillable = true)
{
    public static BillableExpenseResponse From(BillableExpense b) =>
        new(b.Id, b.Kind, b.DocumentId, b.LineNumber, b.CustomerId, b.Date, b.Description, b.Cost, b.MarkupPercent, b.Amount, b.InvoiceId, b.IsBillable);
}

public sealed record OnlinePaymentSettingsResponse(PaymentProvider Provider, bool Enabled, Guid? DepositAccountId, string? PublicUrl, bool HasSecretKey, bool HasWebhookSecret)
{
    public static OnlinePaymentSettingsResponse From(OnlinePaymentSettings s) =>
        new(s.Provider, s.Enabled, s.DepositAccountId, s.PublicUrl, s.ProtectedSecretKey is not null, s.ProtectedWebhookSecret is not null);
}

public sealed record PaymentLinkResponse(Guid Id, Guid InvoiceId, PaymentProvider Provider, string Url, decimal Amount, PaymentLinkStatus Status, DateTimeOffset CreatedAt, Guid? CustomerPaymentId)
{
    public static PaymentLinkResponse From(PaymentLink l) => new(l.Id, l.InvoiceId, l.Provider, l.Url, l.Amount, l.Status, l.CreatedAt, l.CustomerPaymentId);
}

public sealed record RecurringInvoiceResponse(
    Guid Id, string Name, Guid CustomerId, string? Memo, RecurrenceFrequency Frequency, int Interval, DateOnly StartDate, DateOnly? EndDate,
    int? MaxOccurrences, int OccurrencesPosted, DateOnly? NextDate, bool AutoCreate, bool AutoEmail, bool IsActive, IReadOnlyList<SalesLineDraft> Lines, Guid? LocationId = null, IReadOnlyList<Guid>? TagIds = null)
{
    public static RecurringInvoiceResponse From(RecurringInvoice r) => new(
        r.Id, r.Name, r.CustomerId, r.Memo, r.Frequency, r.Interval, r.StartDate, r.EndDate, r.MaxOccurrences, r.OccurrencesPosted, r.NextDate,
        r.AutoCreate, r.AutoEmail, r.IsActive, r.Lines.Select(l => l.ToDraft()).ToList()) { LocationId = r.LocationId, TagIds = r.TagIds };
}

public sealed record DepositResponse(Guid Id, Guid BankAccountId, DateOnly Date, string? Memo, decimal Total, DocumentStatus Status, Guid JournalEntryId,
    IReadOnlyList<DepositLineResponse> Lines,
    IReadOnlyList<DepositExtraDraft>? Extras = null)
{
    public static DepositResponse From(BankDeposit d) => new(d.Id, d.BankAccountId, d.Date, d.Memo, d.Total, d.Status, d.JournalEntryId,
        d.Lines.Select(l => new DepositLineResponse(l.Kind, l.SourceId, l.FromAccountId, l.Amount)).ToList(), [.. d.Extras.Select(e => e.ToDraft())]);
}

public sealed record DepositLineResponse(DepositedKind Kind, Guid SourceId, Guid FromAccountId, decimal Amount);

public sealed record DepositItemRequest(DepositedKind Kind, Guid Id);

/// <param name="Extras">Fees and cash back (negative) or other money (positive) besides the items.</param>
public sealed record MakeDepositRequest(Guid BankAccountId, DateOnly Date, IReadOnlyList<DepositItemRequest> Items, string? Memo = null, IReadOnlyList<DepositExtraDraft>? Extras = null);

public sealed record EmailStatementsRequest(IReadOnlyList<Guid> CustomerIds, DateOnly? From = null, DateOnly? To = null);

public sealed record ChangeDepositRequest(Guid BankAccountId, DateOnly Date, IReadOnlyList<DepositItemRequest> Items, string Reason, string? Memo = null, IReadOnlyList<DepositExtraDraft>? Extras = null);

public sealed record ApplyCreditRequest(Guid CustomerId, DateOnly Date, IReadOnlyList<ApplyCredit> Applications);

public sealed record ConvertEstimateRequest(DateOnly? Date = null);

public sealed record SetEstimateStatusRequest(EstimateStatus Status);

public sealed record FormStyleRequest(FormTemplate Template, string? AccentColor, string? Footer);

public sealed record EmailSettingsResponse(
    EmailDelivery Delivery, string? Host, int Port, SmtpSecurity Security, string? Username, bool HasPassword,
    string? FromAddress, string? FromName, string? BccAddress, bool IsConfigured, string? LinkAddress = null)
{
    public static EmailSettingsResponse From(EmailSettings s) => new(
        s.Delivery, s.Host, s.Port, s.Security, s.Username, s.ProtectedPassword is not null, s.FromAddress, s.FromName, s.BccAddress, s.IsConfigured, s.LinkAddress);
}

public sealed record TestEmailRequest(string To);

/// <param name="SendAt">When to send, with its offset (e.g. 2026-11-02T09:00:00-05:00).</param>
public sealed record ScheduleEmailRequest(
    DateTimeOffset SendAt, string? To, string? Subject, string? Body, string? Cc = null, string? Bcc = null, Guid? DesignId = null);

internal static class ReceivablesApi
{
    public static void MapReceivablesApi(this RouteGroupBuilder api)
    {
        var sales = api.MapGroup("/sales");
        static DateOnly Today() => CompanyTime.Today;

        sales.MapGet("/estimates", async (Guid? customerId, EstimateService s, CancellationToken ct) =>
            (await s.ListAsync(customerId, ct)).Select(EstimateResponse.From));
        sales.MapGet("/estimates/{id:guid}", async (Guid id, EstimateService s, CancellationToken ct) =>
            await s.GetAsync(id, ct) is { } e ? Results.Ok(EstimateResponse.From(e)) : Results.NotFound());
        sales.MapPost("/estimates", async (SaveEstimateCommand r, EstimateService s, CancellationToken ct) =>
        {
            var e = await s.CreateAsync(r, ct);
            return Results.Created($"/api/sales/estimates/{e.Id}", EstimateResponse.From(e));
        });
        sales.MapPut("/estimates/{id:guid}", async (Guid id, SaveEstimateCommand r, EstimateService s, CancellationToken ct) =>
            EstimateResponse.From(await s.UpdateAsync(id, r, ct)));
        sales.MapPut("/estimates/{id:guid}/status", async (Guid id, SetEstimateStatusRequest r, EstimateService s, CancellationToken ct) =>
            EstimateResponse.From(await s.SetStatusAsync(id, r.Status, ct)));
        sales.MapPost("/estimates/{id:guid}/invoice", async (Guid id, ConvertEstimateRequest r, EstimateService s, CancellationToken ct) =>
        {
            var invoice = await s.ConvertToInvoiceAsync(id, r.Date ?? Today(), ct);
            return Results.Created($"/api/sales/invoices/{invoice.Id}", InvoiceResponse.From(invoice));
        });

        sales.MapGet("/credit-memos", async (Guid? customerId, CreditService s, CancellationToken ct) =>
            (await s.ListCreditMemosAsync(customerId, ct)).Select(CreditMemoResponse.From));
        sales.MapGet("/credit-memos/{id:guid}", async (Guid id, CreditService s, CancellationToken ct) =>
            await s.GetCreditMemoAsync(id, ct) is { } c ? Results.Ok(CreditMemoResponse.From(c)) : Results.NotFound());
        sales.MapPost("/credit-memos", async (CreateCreditMemoCommand r, CreditService s, CancellationToken ct) =>
        {
            var c = await s.CreateCreditMemoAsync(r, ct);
            return Results.Created($"/api/sales/credit-memos/{c.Id}", CreditMemoResponse.From(c));
        });
        // Correct a posted credit memo in place: its entry reversed and a new one posted.
        sales.MapPut("/credit-memos/{id:guid}", async (Guid id, ChangeCreditMemoCommand r, CreditService s, CancellationToken ct) =>
            CreditMemoResponse.From(await s.ChangeCreditMemoAsync(id, Kept(r, await s.GetCreditMemoAsync(id, ct)), ct)));
        sales.MapPost("/credit-memos/{id:guid}/void", async (Guid id, VoidRequest r, CreditService s, CancellationToken ct) =>
            CreditMemoResponse.From(await s.VoidCreditMemoAsync(id, r.Reason, ct)));

        sales.MapGet("/customers/{id:guid}/credits", async (Guid id, CreditService s, CancellationToken ct) => await s.AvailableAsync(id, ct));
        sales.MapPost("/credits/apply", async (ApplyCreditRequest r, CreditService s, CancellationToken ct) =>
        {
            await s.ApplyAsync(r.CustomerId, r.Date, r.Applications ?? [], ct);
            return Results.NoContent();
        });

        sales.MapDelete("/credit-memos/{id:guid}/applications/{invoiceId:guid}", async (Guid id, Guid invoiceId, CreditService s, CancellationToken ct) =>
        {
            await s.UnapplyAsync(id, invoiceId, ct);
            return Results.NoContent();
        });

        sales.MapGet("/refunds", async (Guid? customerId, CreditService s, CancellationToken ct) =>
            (await s.ListRefundsAsync(customerId, ct)).Select(RefundResponse.From));
        sales.MapGet("/refunds/{id:guid}", async (Guid id, CreditService s, CancellationToken ct) =>
            await s.GetRefundAsync(id, ct) is { } r ? Results.Ok(RefundResponse.From(r)) : Results.NotFound());
        sales.MapPost("/refunds", async (RefundCreditCommand r, CreditService s, CancellationToken ct) =>
        {
            var refund = await s.RefundAsync(r, ct);
            return Results.Created($"/api/sales/refunds/{refund.Id}", RefundResponse.From(refund));
        });
        sales.MapPost("/refunds/{id:guid}/void", async (Guid id, VoidRequest r, CreditService s, CancellationToken ct) =>
            RefundResponse.From(await s.VoidRefundAsync(id, r.Reason, ct)));

        sales.MapGet("/orders", async (Guid? customerId, SalesOrderService s, CancellationToken ct) =>
            (await s.ListAsync(customerId, ct)).Select(SalesOrderResponse.From));
        sales.MapGet("/orders/{id:guid}", async (Guid id, SalesOrderService s, CancellationToken ct) =>
            await s.GetAsync(id, ct) is { } o ? Results.Ok(SalesOrderResponse.From(o)) : Results.NotFound());
        sales.MapPost("/orders", async (SaveSalesOrderCommand r, SalesOrderService s, CancellationToken ct) =>
        {
            var order = await s.CreateAsync(r, ct);
            return Results.Created($"/api/sales/orders/{order.Id}", SalesOrderResponse.From(order));
        });
        sales.MapPut("/orders/{id:guid}", async (Guid id, SaveSalesOrderCommand r, SalesOrderService s, CancellationToken ct) =>
            SalesOrderResponse.From(await s.UpdateAsync(id, r, ct)));
        sales.MapPost("/orders/{id:guid}/cancel", async (Guid id, SalesOrderService s, CancellationToken ct) => SalesOrderResponse.From(await s.CancelAsync(id, ct)));
        sales.MapGet("/orders/{id:guid}/progress", async (Guid id, SalesOrderService s, CancellationToken ct) =>
            InvoicingProgressResponse.From(await s.ProgressAsync(id, ct)));
        sales.MapPost("/orders/{id:guid}/invoice", async (Guid id, InvoiceFromSourceCommand r, SalesOrderService s, CancellationToken ct) =>
        {
            var invoice = await s.InvoiceAsync(id, r, ct);
            return Results.Created($"/api/sales/invoices/{invoice.Id}", InvoiceResponse.From(invoice));
        });
        sales.MapGet("/estimates/{id:guid}/progress", async (Guid id, EstimateService s, CancellationToken ct) =>
            InvoicingProgressResponse.From(await s.ProgressAsync(id, ct)));
        sales.MapPost("/estimates/{id:guid}/progress-invoice", async (Guid id, InvoiceFromSourceCommand r, EstimateService s, CancellationToken ct) =>
        {
            var invoice = await s.ProgressInvoiceAsync(id, r, ct);
            return Results.Created($"/api/sales/invoices/{invoice.Id}", InvoiceResponse.From(invoice));
        });

        sales.MapGet("/retainers", async (Guid? customerId, RetainerService s, CancellationToken ct) =>
            (await s.ListAsync(customerId, ct)).Select(RetainerResponse.From));
        sales.MapGet("/retainers/{id:guid}", async (Guid id, RetainerService s, CancellationToken ct) =>
            await s.GetAsync(id, ct) is { } r ? Results.Ok(RetainerResponse.From(r)) : Results.NotFound());
        sales.MapPost("/retainers", async (ReceiveRetainerCommand r, RetainerService s, CancellationToken ct) =>
        {
            var retainer = await s.ReceiveAsync(r, ct);
            return Results.Created($"/api/sales/retainers/{retainer.Id}", RetainerResponse.From(retainer));
        });
        sales.MapPost("/retainers/{id:guid}/apply", async (Guid id, ApplyRetainerCommand r, RetainerService s, CancellationToken ct) => await s.ApplyAsync(id, r, ct));
        sales.MapPost("/retainers/{id:guid}/refund", async (Guid id, RefundRetainerCommand r, RetainerService s, CancellationToken ct) => await s.RefundAsync(id, r, ct));
        sales.MapPost("/retainers/{id:guid}/applications/{applicationId:guid}/void", async (Guid id, Guid applicationId, VoidRequest r, RetainerService s, CancellationToken ct) =>
            RetainerResponse.From(await s.VoidApplicationAsync(id, applicationId, r.Reason, ct)));
        sales.MapPost("/retainers/{id:guid}/refunds/{refundId:guid}/void", async (Guid id, Guid refundId, VoidRequest r, RetainerService s, CancellationToken ct) =>
            RetainerResponse.From(await s.VoidRefundAsync(id, refundId, r.Reason, ct)));
        sales.MapPost("/retainers/{id:guid}/void", async (Guid id, VoidRequest r, RetainerService s, CancellationToken ct) =>
            RetainerResponse.From(await s.VoidAsync(id, r.Reason, ct)));

        sales.MapGet("/billable-expenses", async (Guid? customerId, bool? openOnly, BillableExpenseService s, CancellationToken ct) =>
            (await s.ListAsync(customerId, ct)).Where(b => openOnly != true || b.InvoiceId is null).Select(BillableExpenseResponse.From));
        sales.MapGet("/billable-expenses/lines", async (BillableSource kind, Guid documentId, BillableExpenseService s, CancellationToken ct) =>
            (await s.LinesAsync(kind, documentId, ct)).Select(l => new { l.LineNumber, l.AccountId, l.Description, l.Cost, BillableId = l.Billable?.Id }));
        sales.MapPost("/billable-expenses", async (MarkBillableCommand r, BillableExpenseService s, CancellationToken ct) =>
        {
            var billable = await s.MarkAsync(r, ct);
            return Results.Created($"/api/sales/billable-expenses/{billable.Id}", BillableExpenseResponse.From(billable));
        });
        sales.MapDelete("/billable-expenses/{id:guid}", async (Guid id, BillableExpenseService s, CancellationToken ct) =>
        {
            await s.RemoveAsync(id, ct);
            return Results.NoContent();
        });

        sales.MapGet("/invoices/{id:guid}/payment-links", async (Guid id, OnlinePaymentService s, CancellationToken ct) =>
            (await s.LinksAsync(id, ct)).Select(PaymentLinkResponse.From));
        sales.MapPost("/invoices/{id:guid}/payment-links", async (Guid id, OnlinePaymentService s, CancellationToken ct) =>
            PaymentLinkResponse.From(await s.LinkForInvoiceAsync(id, ct)));
        sales.MapPost("/payment-links/{id:guid}/check", async (Guid id, OnlinePaymentService s, CancellationToken ct) =>
            PaymentLinkResponse.From(await s.CheckAsync(id, ct)));
        // Refunds and disputes made at Stripe, waiting to be dealt with in the books.
        sales.MapGet("/online-notices", async (Guid? invoiceId, bool? open, OnlinePaymentService s, CancellationToken ct) => await s.NoticesAsync(invoiceId, open ?? false, ct));
        sales.MapPost("/online-notices/{id:guid}/resolve", async (Guid id, OnlinePaymentService s, CancellationToken ct) => await s.ResolveNoticeAsync(id, ct));
        api.MapGet("/settings/online-payments", async (OnlinePaymentService s, CancellationToken ct) => OnlinePaymentSettingsResponse.From(await s.GetSettingsAsync(ct)));
        api.MapPut("/settings/online-payments", async (OnlinePaymentSettingsCommand r, OnlinePaymentService s, CancellationToken ct) =>
            OnlinePaymentSettingsResponse.From(await s.SaveSettingsAsync(r, ct)));

        sales.MapGet("/write-offs", async (Guid? customerId, WriteOffService s, CancellationToken ct) =>
            (await s.ListAsync(customerId, ct)).Select(WriteOffResponse.From));
        sales.MapGet("/write-offs/candidates", async (DateOnly? asOf, int? days, WriteOffService s, CancellationToken ct) =>
            (await s.CandidatesAsync(asOf ?? Today(), days ?? 90, ct))
                .Select(c => new WriteOffCandidateResponse(c.Invoice.Id, c.Invoice.Number, c.Customer.Id, c.Invoice.DueDate, c.DaysPastDue, c.Invoice.Balance)));
        sales.MapGet("/write-offs/{id:guid}", async (Guid id, WriteOffService s, CancellationToken ct) =>
            await s.GetAsync(id, ct) is { } w ? Results.Ok(WriteOffResponse.From(w)) : Results.NotFound());
        sales.MapPost("/write-offs", async (WriteOffCommand r, WriteOffService s, CancellationToken ct) =>
            (await s.WriteOffAsync(r, ct)).Select(WriteOffResponse.From).ToList());
        sales.MapPost("/write-offs/{id:guid}/void", async (Guid id, VoidRequest r, WriteOffService s, CancellationToken ct) =>
            WriteOffResponse.From(await s.VoidAsync(id, r.Reason, ct)));

        sales.MapGet("/collections/settings", async (CollectionService s, CancellationToken ct) => CollectionSettingsResponse.From(await s.GetAsync(ct)));
        sales.MapPut("/collections/settings/late-fees", async (LateFeeSettingsCommand r, CollectionService s, CancellationToken ct) =>
            CollectionSettingsResponse.From(await s.SaveLateFeesAsync(r, ct)));
        sales.MapPut("/collections/settings/reminders", async (ReminderSettingsCommand r, CollectionService s, CancellationToken ct) =>
            CollectionSettingsResponse.From(await s.SaveRemindersAsync(r, ct)));
        sales.MapGet("/late-fees", async (DateOnly? date, LateFeeService s, CancellationToken ct) =>
            (await s.DueAsync(date ?? Today(), ct))
                .Select(f => new LateFeeDueResponse(f.Invoice.Id, f.Invoice.Number, f.Customer.Id, f.DaysPastDue, f.LastFee, f.Invoice.Balance, f.Fee)));
        sales.MapPost("/late-fees/charge", async (ChargeLateFeesRequest r, LateFeeService s, CancellationToken ct) =>
            (await s.ChargeAsync(r.Date ?? Today(), r.InvoiceIds, ct)).Select(InvoiceResponse.From).ToList());
        sales.MapGet("/reminders", async (DateOnly? date, ReminderService s, CancellationToken ct) =>
            (await s.DueAsync(date ?? Today(), ct)).Select(d => new ReminderDueResponse(d.Invoice.Id, d.Invoice.Number, d.Invoice.CustomerId, d.Step, d.To)));
        sales.MapPost("/reminders/send", async (SendRemindersRequest r, ReminderService s, CancellationToken ct) =>
            (await s.SendAsync(Today(), r.InvoiceIds, ct)).Select(x => new ReminderResultResponse(x.Invoice.Id, x.Invoice.Number, x.Step, x.Error)).ToList());

        sales.MapGet("/recurring-invoices", async (RecurringInvoiceService s, CancellationToken ct) =>
            (await s.ListAsync(ct)).Select(RecurringInvoiceResponse.From));
        sales.MapGet("/recurring-invoices/{id:guid}", async (Guid id, RecurringInvoiceService s, CancellationToken ct) =>
            await s.GetAsync(id, ct) is { } r ? Results.Ok(RecurringInvoiceResponse.From(r)) : Results.NotFound());
        sales.MapPost("/recurring-invoices", async (RecurringInvoiceDetails r, RecurringInvoiceService s, CancellationToken ct) =>
        {
            var created = await s.CreateAsync(r, ct);
            return Results.Created($"/api/sales/recurring-invoices/{created.Id}", RecurringInvoiceResponse.From(created));
        });
        sales.MapPut("/recurring-invoices/{id:guid}", async (Guid id, RecurringInvoiceDetails r, RecurringInvoiceService s, CancellationToken ct) =>
            RecurringInvoiceResponse.From(await s.UpdateAsync(id, r, ct)));
        sales.MapPut("/recurring-invoices/{id:guid}/active", async (Guid id, SetActiveRequest r, bool? catchUp, RecurringInvoiceService s, CancellationToken ct) =>
            RecurringInvoiceResponse.From(await s.SetActiveAsync(id, r.Active, catchUp == true, ct)));
        sales.MapPost("/recurring-invoices/create-due", async (RecurringInvoiceService s, CancellationToken ct) =>
            (await s.CreateDueAsync(Today(), automaticOnly: false, ct)).Select(x => new
            {
                x.Template.Id,
                x.Template.Name,
                Created = x.Created.Select(i => new { i.Id, i.Number, i.Date }),
                x.Error,
                x.Emailed,
                x.EmailError,
            }));

        sales.MapGet("/deposits/undeposited", async (DepositService s, CancellationToken ct) => await s.UndepositedAsync(ct));
        sales.MapGet("/deposits", async (DepositService s, CancellationToken ct) => (await s.ListAsync(ct)).Select(DepositResponse.From));
        sales.MapGet("/deposits/{id:guid}/slip", async (Guid id, OpenBook.Application.Reporting.ListReports r, OpenBook.Application.Reporting.IReportExporter exporter, CancellationToken ct) =>
            Results.File(exporter.ToPdf(await r.DepositSlipAsync(id, ct)), "application/pdf", "Deposit slip.pdf"));
        sales.MapGet("/deposits/{id:guid}", async (Guid id, DepositService s, CancellationToken ct) =>
            await s.GetAsync(id, ct) is { } d ? Results.Ok(DepositResponse.From(d)) : Results.NotFound());
        sales.MapPost("/deposits", async (MakeDepositRequest r, DepositService s, CancellationToken ct) =>
        {
            var d = await s.DepositAsync(new MakeDepositCommand(r.BankAccountId, r.Date, (r.Items ?? []).Select(i => (i.Kind, i.Id)).ToList(), r.Memo, r.Extras), ct);
            return Results.Created($"/api/sales/deposits/{d.Id}", DepositResponse.From(d));
        });
        // Correct a deposit in place: its entry reversed and a new one posted, items added or taken out.
        sales.MapPut("/deposits/{id:guid}", async (Guid id, ChangeDepositRequest r, DepositService s, CancellationToken ct) =>
            DepositResponse.From(await s.ChangeAsync(id, new MakeDepositCommand(r.BankAccountId, r.Date, (r.Items ?? []).Select(i => (i.Kind, i.Id)).ToList(), r.Memo, r.Extras), r.Reason, ct)));
        sales.MapPost("/deposits/{id:guid}/void", async (Guid id, VoidRequest r, DepositService s, CancellationToken ct) =>
            DepositResponse.From(await s.VoidAsync(id, r.Reason, ct)));

        sales.MapGet("/aging", async (DateOnly? asOf, ReceivablesService s, CancellationToken ct) =>
        {
            var report = await s.AgingAsync(asOf ?? Today(), ct);
            return new
            {
                report.AsOf,
                report.Totals,
                Customers = report.Customers.Select(c => new
                {
                    c.Customer.Id,
                    c.Customer.DisplayName,
                    c.Buckets,
                    Invoices = c.Invoices.Select(i => new { i.Invoice.Id, i.Invoice.Number, i.Invoice.Date, i.Invoice.DueDate, i.Balance, i.DaysPastDue }),
                    c.Credits,
                }),
            };
        });
        sales.MapGet("/customers/{id:guid}/statement", async (Guid id, DateOnly? from, DateOnly? to, ReceivablesService s, CancellationToken ct) =>
        {
            var end = to ?? Today();
            var statement = await s.StatementAsync(id, from ?? end.AddMonths(-1).AddDays(1), end, ct);
            return new { CustomerId = id, statement.From, statement.To, statement.OpeningBalance, statement.Lines, statement.ClosingBalance, statement.Aging };
        });

        // PDFs and email.
        // design: print with another (active) design, e.g. a packing slip.
        sales.MapGet("/{kind}/{id:guid}/pdf", async (string kind, Guid id, Guid? design, HttpContext http, FormService s, CancellationToken ct) =>
        {
            var form = await s.RenderAsync(ParseKind(kind), id, design, ct);
            return Pdf(http, form);
        });
        // Fields left out take the usual draft's (the customer's address and copies, the template's wording).
        sales.MapPost("/{kind}/{id:guid}/email", async (string kind, Guid id, EmailRequest r, EmailService s, CancellationToken ct) =>
        {
            var sent = await EmailApi.SendAsync(s, EmailApi.SalesSource(kind, id, null, null), r, ct);
            return Results.Ok(new { sent.To, sent.Cc, sent.Subject, sent.Id });
        });
        sales.MapGet("/invoices/{id:guid}/scheduled-emails", async (Guid id, ScheduledEmailService s, CancellationToken ct) => await s.ListAsync(id, ct));
        sales.MapPost("/invoices/{id:guid}/scheduled-emails", async (Guid id, ScheduleEmailRequest r, EmailService email, ScheduledEmailService s, CancellationToken ct) =>
        {
            var draft = await email.DraftAsync(FormKind.Invoice, id, ct);
            var scheduled = await s.ScheduleAsync(id, new EmailDraft(r.To ?? draft.To, r.Subject ?? draft.Subject, r.Body ?? draft.Body, r.Cc ?? draft.Cc, r.Bcc,
                r.DesignId), r.SendAt, ct);
            return Results.Created($"/api/sales/scheduled-emails/{scheduled.Id}", scheduled);
        });
        sales.MapGet("/scheduled-emails", async (ScheduledEmailService s, CancellationToken ct) => await s.ListAsync(null, ct));
        sales.MapPost("/scheduled-emails/{id:guid}/cancel", async (Guid id, ScheduledEmailService s, CancellationToken ct) =>
        {
            await s.CancelAsync(id, ct);
            return Results.NoContent();
        });
        sales.MapPost("/scheduled-emails/{id:guid}/retry", async (Guid id, ScheduledEmailService s, CancellationToken ct) =>
        {
            await s.RetryAsync(id, ct);
            return Results.NoContent();
        });
        sales.MapGet("/customers/{id:guid}/statement/pdf", async (Guid id, DateOnly? from, DateOnly? to, Guid? design, HttpContext http, FormService s, CancellationToken ct) =>
        {
            var end = to ?? Today();
            var form = await s.RenderStatementAsync(id, from ?? end.AddMonths(-1).AddDays(1), end, design, ct);
            return Pdf(http, form);
        });
        sales.MapPost("/customers/{id:guid}/statement/email", async (Guid id, DateOnly? from, DateOnly? to, EmailRequest r, EmailService s, CancellationToken ct) =>
        {
            var end = to ?? Today();
            var sent = await EmailApi.SendAsync(s, EmailApi.SalesSource("customers", id, from ?? end.AddMonths(-1).AddDays(1), end), r, ct);
            return Results.Ok(new { sent.To, sent.Cc, sent.Subject, sent.Id });
        });

        // Each customer's statement for the period, by email with the usual message; those without an address are listed.
        sales.MapPost("/statements/email", async (EmailStatementsRequest r, EmailService s, CancellationToken ct) =>
        {
            var end = r.To ?? Today();
            var run = await s.SendStatementsAsync(r.CustomerIds ?? [], r.From ?? end.AddMonths(-1).AddDays(1), end, ct);
            return new { run.Sent, run.NoEmail, Failed = run.Failed.Select(f => new { Customer = f.Name, f.Reason }), run.AlreadySent };
        });

        api.MapGet("/settings/email", async (EmailService s, CancellationToken ct) => EmailSettingsResponse.From(await s.GetSettingsAsync(ct)));
        api.MapPut("/settings/email", async (EmailSettingsCommand r, EmailService s, CancellationToken ct) =>
            EmailSettingsResponse.From(await s.SaveSettingsAsync(r, ct)));
        api.MapPost("/settings/email/test", async (TestEmailRequest r, EmailService s, CancellationToken ct) =>
        {
            await s.SendTestAsync(r.To, ct);
            return Results.NoContent();
        });
        // The default design's template, color and footer (the rest of it is kept); /api/settings/form-designs has everything.
        api.MapPut("/settings/forms", async (FormStyleRequest r, FormDesignService s, CancellationToken ct) =>
        {
            var design = (await s.ListAsync(ct)).FirstOrDefault(d => d.IsDefault)
                ?? await s.CreateAsync(new FormDesignDetails(FormDesignService.DefaultName), ct: ct);
            var d = await s.UpdateAsync(design.Id, design.ToDetails() with { Template = r.Template, AccentColor = r.AccentColor, Footer = r.Footer }, ct);
            return new { FormTemplate = d.Template, FormAccentColor = d.AccentColor, FormFooter = d.Footer };
        });
    }

    /// <summary>Shown in the browser (to view or print) rather than downloaded; saving keeps the file name.</summary>
    private static IResult Pdf(HttpContext http, RenderedForm form)
    {
        var disposition = new System.Net.Http.Headers.ContentDispositionHeaderValue("inline") { FileNameStar = form.FileName };
        http.Response.Headers.ContentDisposition = disposition.ToString();
        return Results.File(form.Pdf, "application/pdf");
    }

    private static FormKind ParseKind(string kind) => kind switch
    {
        "invoices" => FormKind.Invoice,
        "estimates" => FormKind.Estimate,
        "credit-memos" => FormKind.CreditMemo,
        "receipts" => FormKind.SalesReceipt,
        _ => throw new DomainException("form.unknown", $"'{kind}' isn't a printable form."),
    };


    private static ChangeCreditMemoCommand Kept(ChangeCreditMemoCommand r, CreditMemo? e) => e is null ? r : r with { Memo = r.Memo ?? e.Memo, LocationId = ApiKeep.Id(r.LocationId, e.LocationId), WarehouseId = ApiKeep.Id(r.WarehouseId, e.WarehouseId) };
}
