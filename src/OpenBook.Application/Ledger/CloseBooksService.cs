using System.Text.Json;
using OpenBook.Application.Companies;
using OpenBook.Application.Reporting;
using OpenBook.Domain.Auditing;
using OpenBook.Domain.Common;
using OpenBook.Domain.Ledger;

namespace OpenBook.Application.Ledger;

public sealed record ClosingStatus(DateOnly? ClosingDate, bool HasPassword);

public sealed record FiscalYearSummary(DateOnly Start, DateOnly End, decimal NetIncome, bool IsClosed);

/// <summary>
/// The closing date, its password, and year-end close. Nothing is posted at year end: as in QuickBooks,
/// the balance sheet rolls prior years' net income into retained earnings, and closing the year locks it.
/// </summary>
public sealed class CloseBooksService(
    ICompanyStore companies,
    FinancialReports reports,
    ILedgerStore ledger,
    IPasswordHasher hasher,
    ClosedPeriodOverride closedPeriod,
    IClock clock,
    ICurrentUser user)
{
    public async Task<ClosingStatus> GetAsync(CancellationToken ct = default)
    {
        var company = await companies.GetRequiredCompanyAsync(ct);
        return new ClosingStatus(company.ClosingDate, company.ClosingPasswordHash is not null);
    }

    /// <param name="closingDate">Null reopens every period.</param>
    /// <param name="newPassword">Sets or replaces the closing password; null keeps the current one.</param>
    /// <param name="removePassword">Remove the password, so closed periods can't be changed at all.</param>
    /// <remarks>
    /// Once a password is set, moving the closing date earlier or changing the password needs the current
    /// password (<see cref="ClosedPeriodOverride"/>); otherwise the password could be sidestepped here.
    /// </remarks>
    public async Task<ClosingStatus> SetAsync(DateOnly? closingDate, string? newPassword, bool removePassword, CancellationToken ct = default)
    {
        var company = await companies.GetRequiredCompanyAsync(ct);
        var before = Snapshot(company.ClosingDate, company.ClosingPasswordHash is not null);

        var reopens = company.ClosingDate is { } current && (closingDate is null || closingDate < current);
        var changesPassword = !string.IsNullOrEmpty(newPassword) || removePassword;
        if (company.ClosingPasswordHash is not null && (reopens || changesPassword))
        {
            if (string.IsNullOrEmpty(closedPeriod.Password))
                throw new DomainException("period.password_required", "Enter the current closing date password to reopen a period or change the password.");
            if (hasher.Verify(company.ClosingPasswordHash, closedPeriod.Password) == PasswordCheck.Failed)
                throw new DomainException("period.wrong_password", "That closing date password is incorrect.");
        }

        string? hash = null;
        if (!string.IsNullOrEmpty(newPassword))
        {
            if (newPassword.Length < 6)
                throw new DomainException("period.password_too_short", "The closing date password needs at least 6 characters.");
            hash = hasher.Hash(newPassword);
        }

        company.SetClosingDate(closingDate, hash);
        if (removePassword && hash is null)
            company.ClearClosingPassword();

        var after = Snapshot(company.ClosingDate, company.ClosingPasswordHash is not null);
        if (after != before || hash is not null)
        {
            companies.Add(AuditRecord.Create(clock.UtcNow, user.Actor, AuditAction.Updated, "closing_date", company.Id, before, after));
            await companies.SaveChangesAsync(ct);
        }

        return new ClosingStatus(company.ClosingDate, company.ClosingPasswordHash is not null);
    }

    /// <summary>Every fiscal year from the first transaction to the current one, newest first.</summary>
    public async Task<IReadOnlyList<FiscalYearSummary>> FiscalYearsAsync(DateOnly today, CancellationToken ct = default)
    {
        var company = await companies.GetRequiredCompanyAsync(ct);
        var lines = await ledger.GetLinesAsync(null, today, ct);
        var first = lines.Count == 0 ? today : lines.Min(l => l.Date);

        var years = new List<FiscalYearSummary>();
        for (var start = company.FiscalYearStart(first); start <= today; start = start.AddYears(1))
        {
            var end = start.AddYears(1).AddDays(-1);
            var pl = await reports.ProfitAndLossAsync(start, end, ReportBasis.Accrual, ct);
            years.Add(new FiscalYearSummary(start, end, pl.NetIncome, company.IsClosed(end)));
        }

        years.Reverse();
        return years;
    }

    /// <summary>Closes the books through the fiscal year ending on <paramref name="fiscalYearEnd"/>.</summary>
    public async Task<ClosingStatus> CloseYearAsync(DateOnly fiscalYearEnd, CancellationToken ct = default)
    {
        var company = await companies.GetRequiredCompanyAsync(ct);
        if (company.FiscalYearEnd(fiscalYearEnd) != fiscalYearEnd)
            throw new DomainException("period.not_year_end", $"{fiscalYearEnd:yyyy-MM-dd} isn't the last day of a fiscal year.");
        if (fiscalYearEnd >= DateOnly.FromDateTime(clock.UtcNow.UtcDateTime))
            throw new DomainException("period.year_not_over", "That fiscal year hasn't ended yet.");
        if (company.ClosingDate is { } current && current >= fiscalYearEnd)
            return new ClosingStatus(current, company.ClosingPasswordHash is not null);
        return await SetAsync(fiscalYearEnd, newPassword: null, removePassword: false, ct);
    }

    private static string Snapshot(DateOnly? closingDate, bool hasPassword) =>
        JsonSerializer.Serialize(new { ClosingDate = closingDate, HasPassword = hasPassword });
}
