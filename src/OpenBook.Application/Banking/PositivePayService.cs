using OpenBook.Application.Companies;
using System.Globalization;
using System.Text;
using System.Text.Json;
using OpenBook.Application.Accounts;
using OpenBook.Application.Purchases;
using OpenBook.Application.Reporting;
using OpenBook.Domain.Accounts;
using OpenBook.Domain.Auditing;
using OpenBook.Domain.Banking;
using OpenBook.Domain.Common;

namespace OpenBook.Application.Banking;

/// <summary>A numbered check written on the account, as it goes in the issue file.</summary>
public sealed record PositivePayCheck(CheckKind Kind, Guid Id, DateOnly Date, string Number, decimal Amount, string Payee, bool Void);

public sealed record PositivePayFile(string FileName, byte[] Content, int Count, decimal Total);

/// <param name="BankAccountNumber">The account number at the bank; remembered for the next export.</param>
public sealed record PositivePayOptions(string? BankAccountNumber, PositivePayDateFormat DateFormat, bool IncludeHeader);

/// <summary>
/// Positive pay issue files: every numbered check written on a bank account in a date range (expenses and bill
/// payments), voided ones marked void, as a CSV the bank's positive pay upload takes. The layout is remembered per
/// account, and so is how far exports have gone, so the next one can pick up the day after.
/// </summary>
public sealed class PositivePayService(
    IBankingStore bank,
    IPurchasesStore purchases,
    AccountService accounts,
    IClock clock,
    ICurrentUser user)
{
    private const int MaxCheckNumberLength = 15;

    public async Task<PositivePaySettings> SettingsAsync(Guid accountId, CancellationToken ct = default)
    {
        EnsureBank((await accounts.GetChartAsync(ct)).Get(accountId));
        return await bank.GetPositivePaySettingsAsync(accountId, ct) ?? PositivePaySettings.For(accountId);
    }

    /// <summary>Checks dated in the range, oldest first, to send again. Only numeric references count as check numbers.</summary>
    public Task<IReadOnlyList<PositivePayCheck>> ListAsync(Guid accountId, DateOnly from, DateOnly to, CancellationToken ct = default) =>
        to < from
            ? throw new DomainException("positive_pay.range_invalid", "The end date is before the start date.")
            : ChecksAsync(accountId, (date, _) => date >= from && date <= to, ct);

    /// <summary>What the bank hasn't been told yet, whatever its date: new checks, and voids of ones it knows of.</summary>
    public Task<IReadOnlyList<PositivePayCheck>> UnsentAsync(Guid accountId, CancellationToken ct = default) =>
        ChecksAsync(accountId, (_, needsSending) => needsSending, ct);

    private async Task<IReadOnlyList<PositivePayCheck>> ChecksAsync(Guid accountId, Func<DateOnly, bool, bool> wanted, CancellationToken ct)
    {
        EnsureBank((await accounts.GetChartAsync(ct)).Get(accountId));
        var vendors = (await purchases.ListVendorsAsync(ct)).ToDictionary(v => v.Id, v => v.DisplayName);
        var expenses = (await bank.ListAllTransactionsAsync(ct))
            .Where(t => t.AccountId == accountId && t.Kind == BankTransactionKind.Spend && IsCheckNumber(t.Reference)
                && wanted(t.Date, t.NeedsSendingToBank(t.Status == BankTransactionStatus.Voided)))
            .Select(t => new PositivePayCheck(CheckKind.Expense, t.Id, t.Date, t.Reference!.Trim(), t.Amount,
                t.Payee ?? (t.VendorId is { } v ? vendors.GetValueOrDefault(v) : null) ?? string.Empty, t.Status == BankTransactionStatus.Voided));
        var payments = (await purchases.ListPaymentsAsync(null, ct))
            .Where(p => p.PaymentAccountId == accountId && p.AchBatchId is null && IsCheckNumber(p.Reference)
                && wanted(p.Date, p.NeedsSendingToBank(p.Status == DocumentStatus.Voided)))
            .Select(p => new PositivePayCheck(CheckKind.BillPayment, p.Id, p.Date, p.Reference!.Trim(), p.Amount,
                vendors.GetValueOrDefault(p.VendorId) ?? string.Empty, p.Status == DocumentStatus.Voided));
        return expenses.Concat(payments)
            .OrderBy(c => c.Date)
            .ThenBy(c => c.Number.Length)
            .ThenBy(c => c.Number, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// Writes the issue file and records, check by check, what the bank now knows. With no dates it holds everything the
    /// bank hasn't had; with dates, those dates' checks, to send again.
    /// </summary>
    public async Task<PositivePayFile> ExportAsync(Guid accountId, DateOnly? from, DateOnly? to, PositivePayOptions options, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        var today = CompanyTime.Today;
        var ranged = from is not null || to is not null;
        var checks = ranged ? await ListAsync(accountId, from ?? DateOnly.MinValue, to ?? today, ct) : await UnsentAsync(accountId, ct);
        if (checks.Count == 0)
            throw new DomainException("positive_pay.no_checks", ranged ? "There are no numbered checks on this account in that range." : "The bank knows of every numbered check on this account.");

        var settings = await bank.GetPositivePaySettingsAsync(accountId, ct);
        if (settings is null)
        {
            settings = PositivePaySettings.For(accountId);
            bank.Add(settings);
        }

        settings.Update(options.BankAccountNumber, options.DateFormat, options.IncludeHeader);
        if (settings.BankAccountNumber is null)
            throw new DomainException("positive_pay.account_number_required", "Enter the account number at the bank; positive pay files need it on every line.");

        var csv = new StringBuilder();
        void Line(params string[] fields) => csv.Append(string.Join(',', fields.Select(ReportCsv.Quote))).Append("\r\n");
        if (settings.IncludeHeader)
            Line("Account Number", "Check Number", "Issue Date", "Amount", "Payee", "Void");
        foreach (var check in checks)
        {
            Line(settings.BankAccountNumber, check.Number, Format(check.Date, settings.DateFormat),
                check.Amount.ToString("0.00", CultureInfo.InvariantCulture), Ascii(check.Payee), check.Void ? "V" : "");
        }

        var now = clock.UtcNow;
        foreach (var check in checks)
        {
            if (check.Kind == CheckKind.Expense)
                (await bank.GetTransactionAsync(check.Id, ct))!.MarkSentToBank(now, check.Void);
            else
                (await purchases.GetPaymentAsync(check.Id, ct))!.MarkSentToBank(now, check.Void);
        }

        settings.Exported(now, to ?? today);
        var total = checks.Where(c => !c.Void).Sum(c => c.Amount);
        bank.Add(AuditRecord.Create(now, user.Actor, AuditAction.Created, "positive_pay_export", accountId, null, JsonSerializer.Serialize(new
        {
            AccountId = accountId,
            From = from,
            To = to,
            Checks = checks.Count,
            Voids = checks.Count(c => c.Void),
            Total = total,
        })));
        await bank.SaveChangesAsync(ct);

        var name = (await accounts.GetChartAsync(ct)).Get(accountId).Name;
        var safe = new string(name.Select(c => char.IsAsciiLetterOrDigit(c) ? c : '-').ToArray()).Trim('-');
        var fileName = FormattableString.Invariant($"positive-pay-{(safe.Length == 0 ? "account" : safe)}-{(ranged ? $"{from ?? to:yyyyMMdd}-{to ?? today:yyyyMMdd}" : $"{today:yyyyMMdd}")}.csv");
        return new PositivePayFile(fileName, Encoding.ASCII.GetBytes(csv.ToString()), checks.Count, total);
    }

    public static string Format(DateOnly date, PositivePayDateFormat format) => format switch
    {
        PositivePayDateFormat.YearMonthDay => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        PositivePayDateFormat.MonthDayYearCompact => date.ToString("MMddyyyy", CultureInfo.InvariantCulture),
        _ => date.ToString("MM/dd/yyyy", CultureInfo.InvariantCulture),
    };

    public static bool IsCheckNumber(string? reference) =>
        reference?.Trim() is { Length: > 0 and <= MaxCheckNumberLength } r && r.All(char.IsAsciiDigit);

    private static void EnsureBank(Account account)
    {
        if (account.Type != AccountType.Bank)
            throw new DomainException("positive_pay.not_bank", "Positive pay is for checking accounts (Bank type).");
    }

    /// <summary>Bank upload formats are plain ASCII: accents are dropped and anything else becomes a space.</summary>
    private static string Ascii(string text)
    {
        var plain = new StringBuilder(text.Length);
        foreach (var c in text.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark)
                continue;
            plain.Append(c is >= ' ' and <= '~' ? c : ' ');
        }

        var result = plain.ToString().Trim();
        return result.Length > 80 ? result[..80] : result;
    }
}
