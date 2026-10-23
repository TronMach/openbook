using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using OpenBook.Application.Companies;
using OpenBook.Application.Forms;
using OpenBook.Application.Sales;
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

public sealed record RecurringInvoiceResponse(
    Guid Id, string Name, Guid CustomerId, string? Memo, RecurrenceFrequency Frequency, int Interval, DateOnly StartDate, DateOnly? EndDate,
    int? MaxOccurrences, int OccurrencesPosted, DateOnly? NextDate, bool AutoCreate, bool AutoEmail, bool IsActive, IReadOnlyList<SalesLineDraft> Lines)
{
    public static RecurringInvoiceResponse From(RecurringInvoice r) => new(
        r.Id, r.Name, r.CustomerId, r.Memo, r.Frequency, r.Interval, r.StartDate, r.EndDate, r.MaxOccurrences, r.OccurrencesPosted, r.NextDate,
        r.AutoCreate, r.AutoEmail, r.IsActive, r.Lines.Select(l => l.ToDraft()).ToList());
}

public sealed record DepositResponse(Guid Id, Guid BankAccountId, DateOnly Date, string? Memo, decimal Total, DocumentStatus Status, Guid JournalEntryId,
    IReadOnlyList<DepositLineResponse> Lines)
{
    public static DepositResponse From(BankDeposit d) => new(d.Id, d.BankAccountId, d.Date, d.Memo, d.Total, d.Status, d.JournalEntryId,
        d.Lines.Select(l => new DepositLineResponse(l.Kind, l.SourceId, l.FromAccountId, l.Amount)).ToList());
}

public sealed record DepositLineResponse(DepositedKind Kind, Guid SourceId, Guid FromAccountId, decimal Amount);

public sealed record DepositItemRequest(DepositedKind Kind, Guid Id);

public sealed record MakeDepositRequest(Guid BankAccountId, DateOnly Date, IReadOnlyList<DepositItemRequest> Items, string? Memo = null);

public sealed record ApplyCreditRequest(Guid CustomerId, DateOnly Date, IReadOnlyList<ApplyCredit> Applications);

public sealed record ConvertEstimateRequest(DateOnly? Date = null);

public sealed record SetEstimateStatusRequest(EstimateStatus Status);

public sealed record EmailFormRequest(string? To = null, string? Subject = null, string? Body = null);

public sealed record FormStyleRequest(FormTemplate Template, string? AccentColor, string? Footer);

public sealed record EmailSettingsResponse(
    EmailDelivery Delivery, string? Host, int Port, SmtpSecurity Security, string? Username, bool HasPassword,
    string? FromAddress, string? FromName, string? BccAddress, bool IsConfigured)
{
    public static EmailSettingsResponse From(EmailSettings s) => new(
        s.Delivery, s.Host, s.Port, s.Security, s.Username, s.ProtectedPassword is not null, s.FromAddress, s.FromName, s.BccAddress, s.IsConfigured);
}

public sealed record TestEmailRequest(string To);

internal static class ReceivablesApi
{
    public static void MapReceivablesApi(this RouteGroupBuilder api)
    {
        var sales = api.MapGroup("/sales");
        static DateOnly Today() => DateOnly.FromDateTime(DateTime.Today);

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
        sales.MapPost("/credit-memos/{id:guid}/void", async (Guid id, VoidRequest r, CreditService s, CancellationToken ct) =>
            CreditMemoResponse.From(await s.VoidCreditMemoAsync(id, r.Reason, ct)));

        sales.MapGet("/customers/{id:guid}/credits", async (Guid id, CreditService s, CancellationToken ct) => await s.AvailableAsync(id, ct));
        sales.MapPost("/credits/apply", async (ApplyCreditRequest r, CreditService s, CancellationToken ct) =>
        {
            await s.ApplyAsync(r.CustomerId, r.Date, r.Applications ?? [], ct);
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
        sales.MapPut("/recurring-invoices/{id:guid}/active", async (Guid id, SetActiveRequest r, RecurringInvoiceService s, CancellationToken ct) =>
            RecurringInvoiceResponse.From(await s.SetActiveAsync(id, r.Active, ct)));
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
        sales.MapGet("/deposits/{id:guid}", async (Guid id, DepositService s, CancellationToken ct) =>
            await s.GetAsync(id, ct) is { } d ? Results.Ok(DepositResponse.From(d)) : Results.NotFound());
        sales.MapPost("/deposits", async (MakeDepositRequest r, DepositService s, CancellationToken ct) =>
        {
            var d = await s.DepositAsync(new MakeDepositCommand(r.BankAccountId, r.Date, (r.Items ?? []).Select(i => (i.Kind, i.Id)).ToList(), r.Memo), ct);
            return Results.Created($"/api/sales/deposits/{d.Id}", DepositResponse.From(d));
        });
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
        sales.MapGet("/{kind}/{id:guid}/pdf", async (string kind, Guid id, HttpContext http, FormService s, CancellationToken ct) =>
        {
            var form = await s.RenderAsync(ParseKind(kind), id, ct);
            return Pdf(http, form);
        });
        sales.MapPost("/{kind}/{id:guid}/email", async (string kind, Guid id, EmailFormRequest r, EmailService s, CancellationToken ct) =>
        {
            var formKind = ParseKind(kind);
            var draft = await s.DraftAsync(formKind, id, ct);
            var message = new EmailDraft(r.To ?? draft.To, r.Subject ?? draft.Subject, r.Body ?? draft.Body);
            await s.SendFormAsync(formKind, id, message, ct);
            return Results.Ok(new { message.To, message.Subject });
        });
        sales.MapGet("/customers/{id:guid}/statement/pdf", async (Guid id, DateOnly? from, DateOnly? to, HttpContext http, FormService s, CancellationToken ct) =>
        {
            var end = to ?? Today();
            var form = await s.RenderStatementAsync(id, from ?? end.AddMonths(-1).AddDays(1), end, ct);
            return Pdf(http, form);
        });
        sales.MapPost("/customers/{id:guid}/statement/email", async (Guid id, DateOnly? from, DateOnly? to, EmailFormRequest r, EmailService s, CancellationToken ct) =>
        {
            var end = to ?? Today();
            var draft = await s.DraftStatementAsync(id, end, ct);
            var message = new EmailDraft(r.To ?? draft.To, r.Subject ?? draft.Subject, r.Body ?? draft.Body);
            await s.SendStatementAsync(id, from ?? end.AddMonths(-1).AddDays(1), end, message, ct);
            return Results.Ok(new { message.To, message.Subject });
        });

        api.MapGet("/settings/email", async (EmailService s, CancellationToken ct) => EmailSettingsResponse.From(await s.GetSettingsAsync(ct)));
        api.MapPut("/settings/email", async (EmailSettingsCommand r, EmailService s, CancellationToken ct) =>
            EmailSettingsResponse.From(await s.SaveSettingsAsync(r, ct)));
        api.MapPost("/settings/email/test", async (TestEmailRequest r, EmailService s, CancellationToken ct) =>
        {
            await s.SendTestAsync(r.To, ct);
            return Results.NoContent();
        });
        api.MapPut("/settings/forms", async (FormStyleRequest r, CompanySettingsService s, CancellationToken ct) =>
        {
            var c = await s.UpdateFormStyleAsync(r.Template, r.AccentColor, r.Footer, ct);
            return new { c.FormTemplate, c.FormAccentColor, c.FormFooter };
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
}
