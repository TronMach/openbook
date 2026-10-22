using OpenBook.Application.Ledger;
using OpenBook.Domain.Accounts;
using OpenBook.Domain.Ledger;

namespace OpenBook.Api;

public sealed record CreateAccountRequest(string Name, AccountType Type, string? Number = null, Guid? ParentId = null);

public sealed record AccountResponse(Guid Id, string? Number, string Name, AccountType Type, Guid? ParentId, bool IsActive)
{
    public static AccountResponse From(Account a) => new(a.Id, a.Number, a.Name, a.Type, a.ParentId, a.IsActive);
}

public sealed record JournalLineRequest(Guid AccountId, decimal Debit, decimal Credit, string? Memo = null);

public sealed record PostJournalEntryRequest(DateOnly Date, IReadOnlyList<JournalLineRequest> Lines, string? Memo = null);

public sealed record VoidJournalEntryRequest(string Reason);

public sealed record JournalLineResponse(int LineNumber, Guid AccountId, decimal Debit, decimal Credit, string? Memo);

public sealed record JournalEntryResponse(
    Guid Id,
    long Number,
    DateOnly Date,
    string Currency,
    string? Memo,
    JournalEntryKind Kind,
    JournalEntryStatus Status,
    string Source,
    Guid? ReversesEntryId,
    DateTimeOffset PostedAt,
    DateTimeOffset? VoidedAt,
    string? VoidReason,
    decimal TotalDebits,
    IReadOnlyList<JournalLineResponse> Lines)
{
    public static JournalEntryResponse From(JournalEntry e) => new(
        e.Id, e.Number, e.Date, e.Currency.Code, e.Memo, e.Kind, e.Status, e.Source, e.ReversesEntryId,
        e.PostedAt, e.VoidedAt, e.VoidReason, e.TotalDebits.Amount,
        e.Lines.OrderBy(l => l.LineNumber).Select(l => new JournalLineResponse(l.LineNumber, l.AccountId, l.Debit, l.Credit, l.Memo)).ToList());
}

public sealed record TrialBalanceLineResponse(Guid AccountId, string? Number, string Name, AccountType Type, decimal Debit, decimal Credit);

public sealed record TrialBalanceResponse(DateOnly AsOf, string Currency, decimal TotalDebits, decimal TotalCredits, bool IsBalanced, IReadOnlyList<TrialBalanceLineResponse> Lines)
{
    public static TrialBalanceResponse From(TrialBalance tb) => new(
        tb.AsOf, tb.TotalDebits.Currency.Code, tb.TotalDebits.Amount, tb.TotalCredits.Amount, tb.IsBalanced,
        tb.Lines.Select(l => new TrialBalanceLineResponse(l.AccountId, l.Number, l.Name, l.Type, l.Debit.Amount, l.Credit.Amount)).ToList());
}
