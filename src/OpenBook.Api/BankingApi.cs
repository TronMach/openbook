using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using OpenBook.Application.Banking;
using OpenBook.Application.Import;
using OpenBook.Domain.Banking;

namespace OpenBook.Api;

public sealed record BankSplitRequest(Guid AccountId, decimal Amount, string? Memo = null, Guid? ClassId = null);

public sealed record RecordBankTransactionRequest(
    BankTransactionKind Kind,
    Guid AccountId,
    DateOnly Date,
    IReadOnlyList<BankSplitRequest> Splits,
    string? Payee = null,
    string? Reference = null,
    string? Memo = null,
    Guid? VendorId = null,
    Guid? LocationId = null);

public sealed record TransferRequest(Guid FromAccountId, Guid ToAccountId, DateOnly Date, decimal Amount, string? Memo = null);

public sealed record BankTransactionResponse(
    Guid Id,
    BankTransactionKind Kind,
    Guid AccountId,
    Guid? TransferAccountId,
    DateOnly Date,
    decimal Amount,
    string? Payee,
    string? Reference,
    string? Memo,
    BankTransactionStatus Status,
    Guid JournalEntryId,
    Guid? VendorId)
{
    public static BankTransactionResponse From(BankTransaction t) =>
        new(t.Id, t.Kind, t.AccountId, t.TransferAccountId, t.Date, t.Amount, t.Payee, t.Reference, t.Memo, t.Status, t.JournalEntryId, t.VendorId);
}

public sealed record BankAccountResponse(Guid Id, string FullName, string Type, decimal Balance, decimal ReconciledBalance, DateOnly? LastReconciledDate, Guid? ReconciliationInProgressId)
{
    public static BankAccountResponse From(BankAccountSummary s) =>
        new(s.Account.Id, s.FullName, s.Account.Type.ToString(), s.Balance, s.ReconciledBalance, s.LastReconciledDate, s.ReconciliationInProgressId);
}

public sealed record RegisterLineResponse(
    Guid JournalLineId,
    Guid JournalEntryId,
    long EntryNumber,
    DateOnly Date,
    Guid? BankTransactionId,
    string? Payee,
    string? Reference,
    string? Memo,
    decimal Amount,
    decimal Balance,
    LineClearing Clearing,
    IReadOnlyList<Guid> OtherAccountIds);

public sealed record VoidRequest(string Reason);

public sealed record ImportBankRowsRequest(IReadOnlyList<ImportBankRowCommand> Rows);

public sealed record StartReconciliationRequest(DateOnly StatementDate, decimal StatementEndingBalance);

public sealed record SetClearedRequest(IReadOnlyList<Guid> JournalLineIds, bool Cleared);

public sealed record ReconcileItemResponse(Guid JournalLineId, Guid JournalEntryId, DateOnly Date, string Description, string? Reference, decimal Amount, bool Cleared)
{
    public static ReconcileItemResponse From(ReconcileItem i) =>
        new(i.Line.JournalLineId, i.Line.JournalEntryId, i.Line.Date, i.Description, i.Reference, i.NaturalAmount, i.Cleared);
}

public sealed record WorksheetResponse(
    Guid Id,
    Guid AccountId,
    DateOnly StatementDate,
    decimal StatementEndingBalance,
    decimal BeginningBalance,
    decimal ClearedTotal,
    decimal Difference,
    string Status,
    IReadOnlyList<ReconcileItemResponse> Items)
{
    public static WorksheetResponse From(ReconciliationWorksheet w) => new(
        w.Reconciliation.Id, w.Account.Id, w.Reconciliation.StatementDate, w.Reconciliation.StatementEndingBalance,
        w.Reconciliation.BeginningBalance, w.ClearedTotal, w.Difference, w.Reconciliation.Status.ToString(),
        w.Items.Select(ReconcileItemResponse.From).ToList());
}

public sealed record ReconciliationReportResponse(
    Guid Id,
    Guid AccountId,
    DateOnly StatementDate,
    decimal BeginningBalance,
    decimal ClearedIncreases,
    decimal ClearedDecreases,
    decimal StatementEndingBalance,
    decimal OutstandingTotal,
    decimal RegisterBalance,
    DateTimeOffset? CompletedAt,
    string? CompletedBy,
    IReadOnlyList<ReconcileItemResponse> Cleared,
    IReadOnlyList<ReconcileItemResponse> Outstanding)
{
    public static ReconciliationReportResponse From(ReconciliationReport r) => new(
        r.Reconciliation.Id, r.Account.Id, r.Reconciliation.StatementDate, r.Reconciliation.BeginningBalance,
        r.ClearedIncreases, r.ClearedDecreases, r.Reconciliation.StatementEndingBalance, r.OutstandingTotal, r.RegisterBalance,
        r.Reconciliation.CompletedAt, r.Reconciliation.CompletedBy,
        r.Cleared.Select(ReconcileItemResponse.From).ToList(), r.Outstanding.Select(ReconcileItemResponse.From).ToList());
}

internal static class BankingApi
{
    public static void MapBankingApi(this RouteGroupBuilder api)
    {
        var banking = api.MapGroup("/banking");

        banking.MapGet("/accounts", async (BankingService s, CancellationToken ct) =>
            (await s.ListAccountsAsync(ct)).Select(BankAccountResponse.From));

        banking.MapGet("/accounts/{id:guid}/register", async (Guid id, int? take, BankingService s, CancellationToken ct) =>
            (await s.GetRegisterAsync(id, take ?? 500, ct)).Lines.Select(l => new RegisterLineResponse(
                l.Line.JournalLineId, l.Line.JournalEntryId, l.Line.EntryNumber, l.Line.Date, l.Document?.Id, l.Document?.Payee,
                l.Document?.Reference, l.Document?.Memo ?? l.Line.EntryMemo, l.NaturalAmount, l.Balance, l.Clearing, l.OtherAccountIds)));

        banking.MapGet("/accounts/{id:guid}/next-check-number", async (Guid id, BankingService s, CancellationToken ct) =>
            new { Number = await s.SuggestCheckNumberAsync(id, ct) });

        banking.MapPost("/transactions", async (RecordBankTransactionRequest r, BankingService s, CancellationToken ct) =>
        {
            var t = await s.RecordAsync(new RecordBankTransactionCommand(
                r.Kind, r.AccountId, r.Date, (r.Splits ?? []).Select(x => new BankSplit(x.AccountId, x.Amount, x.Memo, x.ClassId)).ToList(),
                r.Payee, r.Reference, r.Memo, r.VendorId, r.LocationId), ct);
            return Results.Created($"/api/banking/transactions/{t.Id}", BankTransactionResponse.From(t));
        });

        banking.MapPost("/transfers", async (TransferRequest r, BankingService s, CancellationToken ct) =>
        {
            var t = await s.TransferAsync(new TransferCommand(r.FromAccountId, r.ToAccountId, r.Date, r.Amount, r.Memo), ct);
            return Results.Created($"/api/banking/transactions/{t.Id}", BankTransactionResponse.From(t));
        });

        banking.MapGet("/transactions/{id:guid}", async (Guid id, BankingService s, CancellationToken ct) =>
            await s.GetAsync(id, ct) is { } t ? Results.Ok(BankTransactionResponse.From(t)) : Results.NotFound());

        // Correct a posted spend or receive in place: its entry reversed and a new one posted.
        banking.MapPut("/transactions/{id:guid}", async (Guid id, ChangeBankTransactionCommand r, BankingService s, CancellationToken ct) =>
            BankTransactionResponse.From(await s.ChangeAsync(id, Kept(r, await s.GetAsync(id, ct)), ct)));
        banking.MapPut("/transfers/{id:guid}", async (Guid id, ChangeTransferCommand r, BankingService s, CancellationToken ct) =>
            BankTransactionResponse.From(await s.ChangeTransferAsync(id, r, ct)));
        banking.MapPost("/transactions/{id:guid}/void", async (Guid id, VoidRequest r, BankingService s, CancellationToken ct) =>
            BankTransactionResponse.From(await s.VoidAsync(id, r.Reason, ct)));

        // Preview: POST the CSV as text/csv. Import: POST the chosen rows as JSON to /import/commit.
        banking.MapPost("/accounts/{id:guid}/import/preview", async (Guid id, HttpRequest request, DateOrder? dateOrder, AmountSign? amountSign, BankImportService s, CancellationToken ct) =>
        {
            return await s.PreviewAsync(id, await OpenBook.Application.Import.TextFiles.ReadAsync(request.Body, ct), new BankImportOptions(dateOrder, amountSign ?? AmountSign.PositiveIsMoneyIn), ct);
        }).Accepts<string>("text/csv");

        banking.MapPost("/accounts/{id:guid}/import/commit", async (Guid id, ImportBankRowsRequest r, BankImportService s, CancellationToken ct) =>
            new { Imported = await s.ImportAsync(id, r.Rows ?? [], ct) });

        banking.MapGet("/accounts/{id:guid}/reconciliations", async (Guid id, ReconciliationService s, CancellationToken ct) =>
            (await s.ListAsync(id, ct)).Select(r => new { r.Id, r.StatementDate, r.StatementEndingBalance, Status = r.Status.ToString(), r.CompletedAt }));

        banking.MapPost("/accounts/{id:guid}/reconciliations", async (Guid id, StartReconciliationRequest r, ReconciliationService s, CancellationToken ct) =>
        {
            var rec = await s.StartAsync(id, r.StatementDate, r.StatementEndingBalance, ct);
            return Results.Created($"/api/banking/reconciliations/{rec.Id}", WorksheetResponse.From(await s.GetWorksheetAsync(rec.Id, ct)));
        });

        banking.MapGet("/reconciliations/{id:guid}", async (Guid id, ReconciliationService s, CancellationToken ct) =>
            WorksheetResponse.From(await s.GetWorksheetAsync(id, ct)));

        banking.MapPut("/reconciliations/{id:guid}/statement", async (Guid id, StartReconciliationRequest r, ReconciliationService s, CancellationToken ct) =>
            WorksheetResponse.From(await s.UpdateStatementAsync(id, r.StatementDate, r.StatementEndingBalance, ct)));

        banking.MapPut("/reconciliations/{id:guid}/cleared", async (Guid id, SetClearedRequest r, ReconciliationService s, CancellationToken ct) =>
            WorksheetResponse.From(await s.SetClearedAsync(id, r.JournalLineIds ?? [], r.Cleared, ct)));

        banking.MapPost("/reconciliations/{id:guid}/complete", async (Guid id, ReconciliationService s, CancellationToken ct) =>
        {
            await s.CompleteAsync(id, ct);
            return ReconciliationReportResponse.From(await s.GetReportAsync(id, ct));
        });

        banking.MapDelete("/reconciliations/{id:guid}", async (Guid id, ReconciliationService s, CancellationToken ct) =>
        {
            await s.CancelAsync(id, ct);
            return Results.NoContent();
        });

        banking.MapGet("/reconciliations/{id:guid}/report", async (Guid id, ReconciliationService s, CancellationToken ct) =>
            ReconciliationReportResponse.From(await s.GetReportAsync(id, ct)));
    }


    private static ChangeBankTransactionCommand Kept(ChangeBankTransactionCommand r, BankTransaction? e) => e is null ? r : r with { LocationId = ApiKeep.Id(r.LocationId, e.LocationId) };
}
