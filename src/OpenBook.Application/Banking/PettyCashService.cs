using System.Globalization;
using System.Text.Json;
using OpenBook.Application.Accounts;
using OpenBook.Domain.Accounts;
using OpenBook.Domain.Auditing;
using OpenBook.Domain.Banking;
using OpenBook.Domain.Common;

namespace OpenBook.Application.Banking;

/// <param name="AccountId">An existing Bank account to use as the box; null creates one named <paramref name="NewAccountName"/>.</param>
/// <param name="FundFromAccountId">Transfers the float from this bank account on <paramref name="Date"/>; null records no funding.</param>
public sealed record CreatePettyCashFundCommand(
    Guid? AccountId, string? NewAccountName, decimal FloatAmount, string? Custodian, Guid? FundFromAccountId = null, DateOnly? Date = null);

/// <param name="Breakdown">Optional "value x count" pairs; when given, they must add up to <paramref name="Counted"/>.</param>
/// <param name="OverShortAccountId">Where a difference posts; null uses (or creates) "Cash Over and Short".</param>
public sealed record CountCashCommand(DateOnly Date, decimal Counted, string? Breakdown = null, string? Note = null, Guid? OverShortAccountId = null);

public sealed record PettyCashSummary(PettyCashFund Fund, string AccountName, decimal Balance, decimal ToReplenish, CashCount? LastCount);

/// <summary>
/// Petty cash on the imprest system. Spending from the box is recorded as ordinary spends from its account; a cash
/// count posts any over or short so the books match the box; replenishing transfers the float less what's left.
/// </summary>
public sealed class PettyCashService(
    IBankingStore bank,
    BankingService banking,
    AccountService accounts,
    IClock clock,
    ICurrentUser user)
{
    private const string EntityType = "petty_cash_fund";
    public const string OverShortName = "Cash Over and Short";

    public async Task<IReadOnlyList<PettyCashSummary>> ListAsync(CancellationToken ct = default)
    {
        var chart = await accounts.GetChartAsync(ct);
        var today = Today();
        var result = new List<PettyCashSummary>();
        foreach (var fund in (await bank.ListPettyCashFundsAsync(ct)).OrderByDescending(f => f.IsActive).ThenBy(f => chart.FullName(f.AccountId), StringComparer.CurrentCultureIgnoreCase))
            result.Add(await SummaryAsync(fund, chart, today, ct));
        return result;
    }

    public async Task<PettyCashSummary?> GetAsync(Guid id, CancellationToken ct = default) =>
        await bank.GetPettyCashFundAsync(id, ct) is { } fund ? await SummaryAsync(fund, await accounts.GetChartAsync(ct), Today(), ct) : null;

    public Task<IReadOnlyList<CashCount>> CountsAsync(Guid fundId, CancellationToken ct = default) => bank.ListCashCountsAsync(fundId, ct);

    public async Task<PettyCashFund> CreateAsync(CreatePettyCashFundCommand command, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        // Check what can be checked before creating anything.
        var probe = PettyCashFund.Create(Guid.Empty, command.FloatAmount, command.Custodian, clock.UtcNow);
        var chart = await accounts.GetChartAsync(ct);
        if (command.FundFromAccountId is { } source)
        {
            var from = chart.Get(source);
            if (from.Type != AccountType.Bank || !from.IsActive)
                throw new DomainException("petty_cash.source_invalid", "Fund the box from an active bank account.");
        }

        Account account;
        if (command.AccountId is { } existing)
        {
            account = chart.Get(existing);
            if (account.Type != AccountType.Bank || !account.IsActive)
                throw new DomainException("petty_cash.account_invalid", "A petty cash box is an active Bank-type account.");
            if ((await bank.ListPettyCashFundsAsync(ct)).Any(f => f.AccountId == existing))
                throw new DomainException("petty_cash.account_used", "That account is already a petty cash box.");
        }
        else
        {
            var name = string.IsNullOrWhiteSpace(command.NewAccountName) ? "Petty Cash" : command.NewAccountName.Trim();
            account = await accounts.CreateAsync(new CreateAccountCommand(name, AccountType.Bank, Description: "Cash kept on hand for small purchases."), ct);
        }

        if (command.FundFromAccountId == account.Id)
            throw new DomainException("petty_cash.source_invalid", "Fund the box from a different account.");

        var fund = PettyCashFund.Create(account.Id, probe.FloatAmount, probe.Custodian, clock.UtcNow);
        bank.Add(fund);
        bank.Add(AuditRecord.Create(clock.UtcNow, user.Actor, AuditAction.Created, EntityType, fund.Id, null, Snapshot(fund)));
        await bank.SaveChangesAsync(ct);

        if (command.FundFromAccountId is { } fromId)
            await banking.TransferAsync(new TransferCommand(fromId, account.Id, command.Date ?? Today(), fund.FloatAmount, "Fund petty cash"), ct);
        return fund;
    }

    public async Task<PettyCashFund> UpdateAsync(Guid id, decimal floatAmount, string? custodian, bool active, CancellationToken ct = default)
    {
        var fund = await RequiredAsync(id, ct);
        var before = Snapshot(fund);
        fund.Update(floatAmount, custodian);
        fund.SetActive(active);
        bank.Add(AuditRecord.Create(clock.UtcNow, user.Actor, AuditAction.Updated, EntityType, fund.Id, before, Snapshot(fund)));
        await bank.SaveChangesAsync(ct);
        return fund;
    }

    /// <summary>Records a count; a difference from the books posts against the over/short account in the same save.</summary>
    public async Task<CashCount> CountAsync(Guid fundId, CountCashCommand command, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        var fund = await ActiveAsync(fundId, ct);
        var breakdown = Breakdown.Normalize(command.Breakdown, command.Counted);
        var chart = await accounts.GetChartAsync(ct);
        var book = await BalanceAsync(chart.Get(fund.AccountId), command.Date, ct);
        var difference = command.Counted - book;

        Guid? adjustmentId = null;
        if (difference != 0m)
        {
            var overShort = command.OverShortAccountId is { } chosen ? chart.Get(chosen) : await OverShortAccountAsync(ct);
            if (overShort.Id == fund.AccountId || !overShort.IsActive)
                throw new DomainException("petty_cash.over_short_invalid", "Choose an active account other than the box for the over or short.");
            var transaction = await banking.StageAsync(new RecordBankTransactionCommand(
                difference > 0m ? BankTransactionKind.Receive : BankTransactionKind.Spend,
                fund.AccountId,
                command.Date,
                [new BankSplit(overShort.Id, Math.Abs(difference))],
                Payee: difference > 0m ? "Cash over" : "Cash short",
                Memo: "Petty cash count"), importBatchId: null, ct);
            adjustmentId = transaction.Id;
        }

        var count = CashCount.Create(fund.Id, command.Date, command.Counted, book, adjustmentId, breakdown, command.Note, user.Actor, clock.UtcNow);
        bank.Add(count);
        bank.Add(AuditRecord.Create(clock.UtcNow, user.Actor, AuditAction.Updated, EntityType, fund.Id, null, JsonSerializer.Serialize(new
        {
            Count = new { count.Date, count.Counted, count.BookBalance, Difference = difference, count.AdjustmentTransactionId },
        })));
        await bank.SaveChangesAsync(ct);
        return count;
    }

    /// <summary>Transfers the float less the balance on the date from a bank account, bringing the box back to its float.</summary>
    public async Task<BankTransaction> ReplenishAsync(Guid fundId, Guid fromAccountId, DateOnly date, CancellationToken ct = default)
    {
        var fund = await ActiveAsync(fundId, ct);
        var chart = await accounts.GetChartAsync(ct);
        var from = chart.Get(fromAccountId);
        if (from.Type != AccountType.Bank || !from.IsActive || from.Id == fund.AccountId)
            throw new DomainException("petty_cash.source_invalid", "Replenish the box from another active bank account.");
        var amount = fund.FloatAmount - await BalanceAsync(chart.Get(fund.AccountId), date, ct);
        if (amount <= 0m)
            throw new DomainException("petty_cash.full", "The box already holds its float; there's nothing to replenish.");
        return await banking.TransferAsync(new TransferCommand(from.Id, fund.AccountId, date, amount, "Replenish petty cash"), ct);
    }

    private async Task<PettyCashSummary> SummaryAsync(PettyCashFund fund, ChartOfAccounts chart, DateOnly today, CancellationToken ct)
    {
        var balance = await BalanceAsync(chart.Get(fund.AccountId), today, ct);
        var last = await bank.ListCashCountsAsync(fund.Id, ct) is { Count: > 0 } counts ? counts[0] : null;
        return new PettyCashSummary(fund, chart.FullName(fund.AccountId), balance, Math.Max(0m, fund.FloatAmount - balance), last);
    }

    /// <summary>The box's balance at the end of the date (voided entries and their reversals cancel out).</summary>
    private async Task<decimal> BalanceAsync(Account account, DateOnly date, CancellationToken ct) =>
        BankingRules.Natural(account, (await bank.GetAccountLinesAsync(account.Id, ct)).Where(l => l.Date <= date).Sum(l => l.Amount));

    private async Task<Account> OverShortAccountAsync(CancellationToken ct)
    {
        var chart = await accounts.GetChartAsync(ct);
        var existing = chart.Ordered(includeInactive: false).Select(x => x.Account)
            .FirstOrDefault(a => a.Type.ClassOf() == AccountClass.Expense && string.Equals(a.Name, OverShortName, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
            return existing;
        var name = chart.FindByFullName(OverShortName) is null ? OverShortName : $"{OverShortName} (Expense)";
        return await accounts.CreateAsync(new CreateAccountCommand(name, AccountType.Expense, Description: "Differences found when counting petty cash."), ct);
    }

    private async Task<PettyCashFund> RequiredAsync(Guid id, CancellationToken ct) =>
        await bank.GetPettyCashFundAsync(id, ct) ?? throw new EntityNotFoundException(EntityType, id);

    private async Task<PettyCashFund> ActiveAsync(Guid id, CancellationToken ct)
    {
        var fund = await RequiredAsync(id, ct);
        return fund.IsActive ? fund : throw new DomainException("petty_cash.inactive", "This petty cash box is closed.");
    }

    private DateOnly Today() => DateOnly.FromDateTime(clock.UtcNow.LocalDateTime);

    private static string Snapshot(PettyCashFund f) => JsonSerializer.Serialize(new { f.AccountId, f.FloatAmount, f.Custodian, f.IsActive });
}

/// <summary>"Value x count" pairs for a cash count, e.g. "20x5; 1x3; 0.25x8".</summary>
public static class Breakdown
{
    /// <summary>The pairs tidied ("20x5; 1x3"), checked to add up to the counted amount; null when none were given.</summary>
    public static string? Normalize(string? text, decimal counted)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        var pairs = Parse(text);
        var total = pairs.Sum(p => p.Value * p.Count);
        if (total != counted)
            throw new DomainException("petty_cash.breakdown_mismatch", FormattableString.Invariant($"The notes and coins add up to {total:0.00}, not {counted:0.00}."));
        return string.Join("; ", pairs.Select(p => FormattableString.Invariant($"{p.Value:0.##}x{p.Count}")));
    }

    public static IReadOnlyList<(decimal Value, int Count)> Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var pairs = new List<(decimal, int)>();
        foreach (var part in text.Split([';', ',', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var bits = part.Split(['x', 'X', '×', '*'], 2, StringSplitOptions.TrimEntries);
            if (bits.Length != 2
                || !decimal.TryParse(bits[0].TrimStart('$'), NumberStyles.Number, CultureInfo.InvariantCulture, out var value) || value <= 0m
                || !int.TryParse(bits[1], NumberStyles.None, CultureInfo.InvariantCulture, out var count))
                throw new DomainException("petty_cash.breakdown_invalid", $"Write each note or coin as value x count, e.g. 20x5 (\"{part}\" isn't).");
            if (count > 0)
                pairs.Add((value, count));
        }

        return pairs;
    }
}
