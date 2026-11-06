using OpenBook.Application.Accounts;
using OpenBook.Application.Ledger;
using OpenBook.Domain.Accounts;
using OpenBook.Domain.Companies;
using OpenBook.Domain.Ledger;

namespace OpenBook.Api;

public sealed record CreateAccountRequest(string Name, AccountType Type, string? Number = null, Guid? ParentId = null, string? Description = null, bool DonorRestricted = false);

/// <param name="DonorRestricted">Null keeps the current setting.</param>
public sealed record UpdateAccountRequest(string Name, AccountType Type, string? Number = null, Guid? ParentId = null, string? Description = null, bool? DonorRestricted = null);

public sealed record AccountResponse(
    Guid Id,
    string? Number,
    string Name,
    string FullName,
    int Depth,
    AccountType Type,
    AccountClass Class,
    Guid? ParentId,
    string? Description,
    bool IsActive,
    string? TaxLine,
    bool DonorRestricted = false)
{
    public static AccountResponse From(Account a, ChartOfAccounts chart) =>
        new(a.Id, a.Number, a.Name, chart.FullName(a.Id), chart.Depth(a.Id), a.Type, a.Type.ClassOf(), a.ParentId, a.Description, a.IsActive, a.TaxLineKey, a.DonorRestricted);
}

public sealed record ChartImportResponse(int New, int Existing, int Errors, bool Imported, IReadOnlyList<ChartImportRow> Rows)
{
    public static ChartImportResponse From(ChartImportResult r, bool imported) =>
        new(r.NewCount, r.ExistingCount, r.ErrorCount, imported, r.Rows);
}

/// <param name="ClassId">The line's class (Domain.Segments), if any.</param>
public sealed record JournalLineRequest(Guid AccountId, decimal Debit, decimal Credit, string? Memo = null, Guid? ClassId = null);

/// <param name="LocationId">The entry's location; every line carries it.</param>
/// <param name="TagIds">Tags on the entry.</param>
public sealed record PostJournalEntryRequest(
    DateOnly Date, IReadOnlyList<JournalLineRequest> Lines, string? Memo = null, EntryBasis Basis = EntryBasis.Both, DateOnly? ReverseOn = null,
    Guid? LocationId = null, IReadOnlyList<Guid>? TagIds = null);

public sealed record VoidJournalEntryRequest(string Reason);

/// <summary>A hand-entered journal entry's replacement and why (<c>PUT /api/journal-entries/{id}</c>).</summary>
public sealed record ChangeJournalEntryRequest(
    DateOnly Date, IReadOnlyList<JournalLineRequest> Lines, string Reason, string? Memo = null, EntryBasis Basis = EntryBasis.Both,
    Guid? LocationId = null, IReadOnlyList<Guid>? TagIds = null);

public sealed record JournalLineResponse(int LineNumber, Guid AccountId, decimal Debit, decimal Credit, string? Memo);

public sealed record JournalEntryResponse(
    Guid Id,
    long Number,
    DateOnly Date,
    string Currency,
    string? Memo,
    JournalEntryKind Kind,
    JournalEntryStatus Status,
    EntryBasis Basis,
    string Source,
    Guid? ReversesEntryId,
    DateTimeOffset PostedAt,
    DateTimeOffset? VoidedAt,
    string? VoidReason,
    decimal TotalDebits,
    IReadOnlyList<JournalLineResponse> Lines)
{
    public static JournalEntryResponse From(JournalEntry e) => new(
        e.Id, e.Number, e.Date, e.Currency.Code, e.Memo, e.Kind, e.Status, e.Basis, e.Source, e.ReversesEntryId,
        e.PostedAt, e.VoidedAt, e.VoidReason, e.TotalDebits.Amount,
        e.Lines.OrderBy(l => l.LineNumber).Select(l => new JournalLineResponse(l.LineNumber, l.AccountId, l.Debit, l.Credit, l.Memo)).ToList());
}

public sealed record JournalEntryPageResponse(int Total, int Skip, int Take, IReadOnlyList<JournalEntryResponse> Entries);

public sealed record TrialBalanceLineResponse(Guid AccountId, string? Number, string FullName, AccountType Type, decimal Debit, decimal Credit);

public sealed record TrialBalanceResponse(
    DateOnly AsOf,
    ReportBasis Basis,
    string Currency,
    decimal TotalDebits,
    decimal TotalCredits,
    bool IsBalanced,
    IReadOnlyList<TrialBalanceLineResponse> Lines)
{
    public static TrialBalanceResponse From(TrialBalance tb) => new(
        tb.AsOf, tb.Basis, tb.TotalDebits.Currency.Code, tb.TotalDebits.Amount, tb.TotalCredits.Amount, tb.IsBalanced,
        tb.Lines.Select(l => new TrialBalanceLineResponse(l.AccountId, l.Number, l.FullName, l.Type, l.Debit.Amount, l.Credit.Amount)).ToList());
}

public sealed record CompanySettingsRequest(
    string Name,
    BusinessType BusinessType,
    string? LegalName,
    string? Email,
    string? Phone,
    string? Address,
    int FiscalYearStartMonth,
    bool UseAccountNumbers,
    ReportBasis DefaultReportBasis,
    Edition? Edition = null,
    string? TimeZoneId = null,
    ViewMode? DefaultViewMode = null,
    bool? AllowSellingShort = null,
    string? Region = null);

public sealed record CompanyResponse(
    Guid Id,
    string Name,
    BusinessType BusinessType,
    string? LegalName,
    string? Email,
    string? Phone,
    string? Address,
    string HomeCurrency,
    int FiscalYearStartMonth,
    bool UseAccountNumbers,
    ReportBasis DefaultReportBasis,
    Edition Edition,
    string? TimeZoneId,
    ViewMode DefaultViewMode,
    bool AllowSellingShort,
    string? Region)
{
    public static CompanyResponse From(Company c) => new(
        c.Id, c.Name, c.BusinessType, c.LegalName, c.Email, c.Phone, c.Address, c.HomeCurrency.Code,
        c.FiscalYearStartMonth, c.UseAccountNumbers, c.DefaultReportBasis, c.Edition, c.TimeZoneId, c.DefaultViewMode, c.AllowSellingShort, c.Region);
}
