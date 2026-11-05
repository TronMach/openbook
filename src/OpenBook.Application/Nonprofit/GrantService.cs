using System.Text.Json;
using OpenBook.Application.Accounts;
using OpenBook.Application.Ledger;
using OpenBook.Application.Reporting;
using OpenBook.Application.Sales;
using OpenBook.Application.Segments;
using OpenBook.Domain.Accounts;
using OpenBook.Domain.Auditing;
using OpenBook.Domain.Common;
using OpenBook.Domain.Ledger;
using OpenBook.Domain.Nonprofit;
using OpenBook.Domain.Segments;

namespace OpenBook.Application.Nonprofit;

/// <param name="ClassId">The grant's class; null creates one named after the grant, counting as program services.</param>
/// <param name="IncomeAccountId">Null: "Grants", created on first use.</param>
public sealed record GrantCommand(
    string Name,
    Guid FunderId,
    decimal Amount,
    DateOnly AwardedOn,
    DateOnly StartsOn,
    DateOnly EndsOn,
    Guid? ClassId = null,
    Guid? IncomeAccountId = null,
    bool Conditional = false,
    string? Reference = null,
    string? Terms = null);

public sealed record GrantDetailsCommand(string Name, string? Reference, DateOnly StartsOn, DateOnly EndsOn, string? Terms);

/// <summary>One budget line against what's been spent on it; <paramref name="Budget"/> is zero for accounts spent on without a budget.</summary>
public sealed record GrantBudgetRow(Guid AccountId, string Account, decimal Budget, decimal Spent)
{
    public decimal Left => Budget - Spent;
}

/// <summary>A grant with what's been spent on its class in its period (through a date).</summary>
public sealed record GrantPosition(Grant Grant, string Funder, string ClassName, decimal Spent, IReadOnlyList<GrantBudgetRow> Rows)
{
    /// <summary>A conditional grant's spending not yet recognized as income, up to the award.</summary>
    public decimal ReadyToRecognize => Grant.Conditional && Grant.Status == GrantStatus.Active
        ? Math.Max(0m, Math.Min(Spent, Grant.Amount) - Grant.Recognized)
        : 0m;

    public decimal Unspent => Grant.Amount - Spent;
}

/// <summary>Grants: awards, their budgets, spending against them (by class), money from funders and, for conditional grants, income as it's earned.</summary>
public sealed class GrantService(
    INonprofitStore store, ISalesStore sales, AccountService accounts, SegmentService segments, ReportLedger ledger, PostingEngine engine,
    IClock clock, ICurrentUser user)
{
    public const string EntityType = "grant";
    public const string GrantsIncomeName = "Grants";
    public const string ReceivableName = "Grants Receivable";
    public const string AdvanceName = "Refundable Advances";

    public async Task<IReadOnlyList<Grant>> ListAsync(CancellationToken ct = default) =>
        (await store.ListGrantsAsync(ct)).OrderBy(g => g.Status).ThenByDescending(g => g.StartsOn).ThenBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase).ToList();

    public Task<Grant?> GetAsync(Guid id, CancellationToken ct = default) => store.GetGrantAsync(id, ct);

    /// <summary>Every grant's position through the date (spending on accrual basis).</summary>
    public async Task<IReadOnlyList<GrantPosition>> PositionsAsync(DateOnly asOf, CancellationToken ct = default)
    {
        var grants = await ListAsync(ct);
        if (grants.Count == 0)
            return [];
        var context = await ContextAsync(asOf, ct);
        return grants.Select(g => Position(g, context)).ToList();
    }

    public async Task<GrantPosition?> PositionAsync(Guid id, DateOnly asOf, CancellationToken ct = default) =>
        await store.GetGrantAsync(id, ct) is { } grant ? Position(grant, await ContextAsync(asOf, ct)) : null;

    public async Task<Grant> CreateAsync(GrantCommand command, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        Grant.Check(command.Name, command.Amount, command.StartsOn, command.EndsOn);
        var funder = await sales.GetCustomerAsync(command.FunderId, ct) ?? throw new DomainException("grant.funder_required", "Choose the funder.");
        var chart = await accounts.GetChartAsync(ct);
        var income = await IncomeAccountAsync(chart, command.IncomeAccountId, ct);
        var receivable = await ReceivableAsync(chart, ct);

        Guid classId;
        if (command.ClassId is { } chosen && chosen != Guid.Empty)
        {
            var segment = await segments.GetAsync(chosen, ct) ?? throw new EntityNotFoundException("segment", chosen);
            if (segment.Kind != SegmentKind.Class)
                throw new DomainException("grant.class_kind", "Choose a class for the grant.");
            classId = segment.Id;
        }
        else
        {
            var created = await segments.CreateAsync(SegmentKind.Class, command.Name.Trim(), null, ct);
            await segments.SetFunctionalAreaAsync(created.Id, FunctionalArea.Program, ct);
            classId = created.Id;
        }

        var id = Guid.CreateVersion7();
        Guid? awardEntry = null;
        if (!command.Conditional)
        {
            var memo = $"Grant: {command.Name.Trim()} ({funder.DisplayName})";
            awardEntry = (await engine.StageAsync(new PostJournalEntryCommand(command.AwardedOn,
                [JournalLineDraft.Dr(receivable.Id, command.Amount, memo, classId), JournalLineDraft.Cr(income.Id, command.Amount, memo, classId)],
                memo, Grant.AwardSource, id, EntryBasis.AccrualOnly), ct)).Id;
        }

        var grant = Grant.Create(id, command.Name, funder.Id, command.Reference, command.Amount, command.AwardedOn, command.StartsOn, command.EndsOn,
            classId, income.Id, receivable.Id, command.Conditional, command.Terms, awardEntry, clock.UtcNow);
        store.Add(grant);
        store.Add(Audit(AuditAction.Created, grant, null));
        await store.SaveChangesAsync(ct);
        return grant;
    }

    public Task<Grant> UpdateAsync(Guid id, GrantDetailsCommand command, CancellationToken ct = default) =>
        ChangeAsync(id, AuditAction.Updated, g => g.Update(command.Name, command.Reference, command.StartsOn, command.EndsOn, command.Terms), ct);

    /// <summary>Replaces the budget: an amount per expense account.</summary>
    public async Task<Grant> SetBudgetAsync(Guid id, IReadOnlyList<(Guid AccountId, decimal Amount)> lines, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(lines);
        var chart = await accounts.GetChartAsync(ct);
        foreach (var (accountId, _) in lines)
        {
            var account = chart.Find(accountId) ?? throw new EntityNotFoundException("account", accountId);
            if (account.Type.ClassOf() != AccountClass.Expense)
                throw new DomainException("grant.budget_account", $"'{account.Name}' isn't an expense account.");
        }

        return await ChangeAsync(id, AuditAction.Updated, g => g.SetBudget(lines), ct);
    }

    /// <summary>Money from the funder into a bank account: Dr bank / Cr the grant's receivable.</summary>
    public async Task<Grant> ReceiveAsync(Guid id, DateOnly date, decimal amount, Guid bankAccountId, CancellationToken ct = default)
    {
        var grant = await RequiredAsync(id, ct);
        var bank = (await accounts.GetChartAsync(ct)).Find(bankAccountId);
        if (bank is not { Type: AccountType.Bank, IsActive: true })
            throw new DomainException("grant.bank", "Choose the bank account it went into.");
        grant.CheckReceipt(amount);
        var before = Snapshot(grant);
        var memo = $"Grant payment: {grant.Name}";
        var advance = grant.AdvancePartOf(amount);
        var lines = new List<JournalLineDraft> { JournalLineDraft.Dr(bank.Id, amount, memo) };
        if (amount - advance > 0m)
            lines.Add(JournalLineDraft.Cr(grant.ReceivableAccountId, amount - advance, memo, grant.ClassId));
        if (advance > 0m)
        {
            grant.UseAdvanceAccount((await AdvanceAccountAsync(ct)).Id);
            lines.Add(JournalLineDraft.Cr(grant.AdvanceAccountId!.Value, advance, $"{memo} (paid ahead)", grant.ClassId));
        }

        var entry = await engine.StageAsync(new PostJournalEntryCommand(date, lines, memo, Grant.ReceiptSource, grant.Id), ct);

        // On the cash basis the money is the income: this clears the receivable and advance the entry above credited.
        Guid? cashEntry = null;
        if (grant.CashOnReceipt)
        {
            var cash = new List<JournalLineDraft>();
            if (amount - advance > 0m)
                cash.Add(JournalLineDraft.Dr(grant.ReceivableAccountId, amount - advance, memo, grant.ClassId));
            if (advance > 0m)
                cash.Add(JournalLineDraft.Dr(grant.AdvanceAccountId!.Value, advance, memo, grant.ClassId));
            cash.Add(JournalLineDraft.Cr(grant.IncomeAccountId, amount, memo, grant.ClassId));
            cashEntry = (await engine.StageAsync(new PostJournalEntryCommand(date, cash, $"{memo} (cash basis)", Grant.ReceiptSource, grant.Id, EntryBasis.CashOnly), ct)).Id;
        }

        grant.AddReceipt(date, amount, bank.Id, entry.Id, cashEntry);
        store.Add(Audit(AuditAction.Updated, grant, before));
        await store.SaveChangesAsync(ct);
        return grant;
    }

    /// <summary>Conditional grants: income for what's been spent through the date and not recognized yet, up to the award.</summary>
    public async Task<Grant> RecognizeAsync(Guid id, DateOnly through, CancellationToken ct = default)
    {
        var position = await PositionAsync(id, through, ct) ?? throw new EntityNotFoundException(EntityType, id);
        var grant = position.Grant;
        if (!grant.Conditional)
            throw new DomainException("grant.unconditional", "An unconditional grant was income when awarded.");
        if (position.ReadyToRecognize <= 0m)
            throw new DomainException("grant.nothing_to_recognize", "Nothing spent on this grant is waiting to be recognized.");
        var before = Snapshot(grant);
        var memo = $"Grant income earned: {grant.Name}";
        var amount = position.ReadyToRecognize;
        var fromAdvance = grant.AdvanceUsedBy(amount);
        var lines = new List<JournalLineDraft>();
        if (fromAdvance > 0m)
            lines.Add(JournalLineDraft.Dr(grant.AdvanceAccountId!.Value, fromAdvance, memo, grant.ClassId));
        if (amount - fromAdvance > 0m)
            lines.Add(JournalLineDraft.Dr(grant.ReceivableAccountId, amount - fromAdvance, memo, grant.ClassId));
        lines.Add(JournalLineDraft.Cr(grant.IncomeAccountId, amount, memo, grant.ClassId));
        var entry = await engine.StageAsync(new PostJournalEntryCommand(through, lines, memo, Grant.RecognitionSource, grant.Id,
            grant.CashOnReceipt ? EntryBasis.AccrualOnly : EntryBasis.Both), ct);
        grant.AddRecognition(through, position.ReadyToRecognize, entry.Id);
        store.Add(Audit(AuditAction.Updated, grant, before));
        await store.SaveChangesAsync(ct);
        return grant;
    }

    public async Task<Grant> VoidReceiptAsync(Guid id, int line, string reason, CancellationToken ct = default)
    {
        var grant = await RequiredAsync(id, ct, activeOnly: false);
        var receipt = grant.Receipts.FirstOrDefault(r => r.LineNumber == line) ?? throw new EntityNotFoundException("grant_receipt", id);
        var before = Snapshot(grant);
        grant.VoidReceipt(line); // checks before the reversal is staged
        await engine.StageVoidAsync(receipt.EntryId, reason, fromDocument: true, ct);
        if (receipt.CashEntryId is { } cashEntry)
            await engine.StageVoidAsync(cashEntry, reason, fromDocument: true, ct);
        store.Add(Audit(AuditAction.Updated, grant, before));
        await store.SaveChangesAsync(ct);
        return grant;
    }

    public async Task<Grant> VoidRecognitionAsync(Guid id, int line, string reason, CancellationToken ct = default)
    {
        var grant = await RequiredAsync(id, ct, activeOnly: false);
        var recognition = grant.Recognitions.FirstOrDefault(r => r.LineNumber == line) ?? throw new EntityNotFoundException("grant_recognition", id);
        var before = Snapshot(grant);
        grant.VoidRecognition(line); // checks before the reversal is staged
        await engine.StageVoidAsync(recognition.EntryId, reason, fromDocument: true, ct);
        store.Add(Audit(AuditAction.Updated, grant, before));
        await store.SaveChangesAsync(ct);
        return grant;
    }

    public Task<Grant> CloseAsync(Guid id, CancellationToken ct = default) => ChangeAsync(id, AuditAction.Updated, g => g.Close(), ct);

    public Task<Grant> ReopenAsync(Guid id, CancellationToken ct = default) => ChangeAsync(id, AuditAction.Updated, g => g.Reopen(), ct);

    /// <summary>A grant recorded by mistake: reverses the award entry. Refused while payments or recognized income stand.</summary>
    public async Task<Grant> VoidAsync(Guid id, string reason, CancellationToken ct = default)
    {
        var grant = await RequiredAsync(id, ct, activeOnly: false);
        var before = Snapshot(grant);
        grant.Void();
        if (grant.AwardEntryId is { } entry)
            await engine.StageVoidAsync(entry, reason, fromDocument: true, ct);
        store.Add(Audit(AuditAction.Voided, grant, before));
        await store.SaveChangesAsync(ct);
        return grant;
    }

    private async Task<Grant> ChangeAsync(Guid id, AuditAction action, Action<Grant> change, CancellationToken ct)
    {
        var grant = await RequiredAsync(id, ct, activeOnly: false);
        var before = Snapshot(grant);
        change(grant);
        store.Add(Audit(action, grant, before));
        await store.SaveChangesAsync(ct);
        return grant;
    }

    private async Task<Grant> RequiredAsync(Guid id, CancellationToken ct, bool activeOnly = true)
    {
        var grant = await store.GetGrantAsync(id, ct) ?? throw new EntityNotFoundException(EntityType, id);
        if (activeOnly && grant.Status == GrantStatus.Voided)
            throw new DomainException("grant.not_active", "This grant is voided.");
        return grant;
    }

    private sealed record Context(ChartOfAccounts Chart, IReadOnlyList<LedgerLine> Expenses, IReadOnlyList<SegmentChoice> Classes, IReadOnlyDictionary<Guid, string> Funders);

    private async Task<Context> ContextAsync(DateOnly asOf, CancellationToken ct)
    {
        var chart = await accounts.GetChartAsync(ct);
        var lines = (await ledger.LinesAsync(ReportBasis.Accrual, null, asOf, ct)).Where(l => l.ClassId is not null && chart.Get(l.AccountId).Type.ClassOf() == AccountClass.Expense).ToList();
        return new Context(chart, lines, await segments.ListAsync(SegmentKind.Class, includeInactive: true, ct),
            (await sales.ListCustomersAsync(ct)).ToDictionary(c => c.Id, c => c.DisplayName));
    }

    private static GrantPosition Position(Grant grant, Context context)
    {
        // The grant's class and every class under it.
        var parents = context.Classes.ToDictionary(c => c.Segment.Id, c => c.Segment.ParentId);
        bool Under(Guid id)
        {
            for (Guid? at = id; at is { } current; at = parents.GetValueOrDefault(current))
            {
                if (current == grant.ClassId)
                    return true;
            }

            return false;
        }

        var spent = context.Expenses.Where(l => l.Date >= grant.StartsOn && l.Date <= grant.EndsOn && Under(l.ClassId!.Value))
            .GroupBy(l => l.AccountId).ToDictionary(g => g.Key, g => g.Sum(l => l.Amount));
        var rows = grant.Budget.Select(b => new GrantBudgetRow(b.AccountId, context.Chart.FullName(b.AccountId), b.Amount, spent.GetValueOrDefault(b.AccountId)))
            .Concat(spent.Where(s => grant.Budget.All(b => b.AccountId != s.Key) && s.Value != 0m)
                .Select(s => new GrantBudgetRow(s.Key, context.Chart.FullName(s.Key), 0m, s.Value)))
            .ToList();
        var className = context.Classes.FirstOrDefault(c => c.Segment.Id == grant.ClassId)?.FullName ?? "?";
        return new GrantPosition(grant, context.Funders.GetValueOrDefault(grant.FunderId, "?"), className, spent.Values.Sum(), rows);
    }

    private async Task<Account> IncomeAccountAsync(ChartOfAccounts chart, Guid? id, CancellationToken ct)
    {
        if (id is { } chosen && chosen != Guid.Empty)
        {
            var account = chart.Find(chosen) ?? throw new EntityNotFoundException("account", chosen);
            if (account.Type is not (AccountType.Income or AccountType.OtherIncome) || !account.IsActive)
                throw new DomainException("grant.income_account", $"'{account.Name}' isn't an active income account.");
            return account;
        }

        return chart.Ordered(includeInactive: false).Select(x => x.Account)
                   .FirstOrDefault(a => a.Type == AccountType.Income && a.ParentId is null && string.Equals(a.Name, GrantsIncomeName, StringComparison.OrdinalIgnoreCase))
               ?? await accounts.CreateAsync(new CreateAccountCommand(GrantsIncomeName, AccountType.Income), ct);
    }

    private async Task<Account> AdvanceAccountAsync(CancellationToken ct) =>
        (await accounts.GetChartAsync(ct)).Ordered(includeInactive: false).Select(x => x.Account)
            .FirstOrDefault(a => a.Type == AccountType.OtherCurrentLiability && string.Equals(a.Name, AdvanceName, StringComparison.OrdinalIgnoreCase))
        ?? await accounts.CreateAsync(new CreateAccountCommand(AdvanceName, AccountType.OtherCurrentLiability,
            Description: "Conditional grant money received before it was earned; refundable if the conditions aren't met."), ct);

    /// <summary>"Grants Receivable": an existing one (other current asset or A/R), else a new other current asset.</summary>
    private async Task<Account> ReceivableAsync(ChartOfAccounts chart, CancellationToken ct) =>
        chart.Ordered(includeInactive: false).Select(x => x.Account)
            .FirstOrDefault(a => a.Type is AccountType.OtherCurrentAsset or AccountType.AccountsReceivable && string.Equals(a.Name, ReceivableName, StringComparison.OrdinalIgnoreCase))
        ?? await accounts.CreateAsync(new CreateAccountCommand(ReceivableName, AccountType.OtherCurrentAsset, Description: "Grant income earned or awarded, not yet paid by the funder."), ct);

    private AuditRecord Audit(AuditAction action, Grant g, string? before) =>
        AuditRecord.Create(clock.UtcNow, user.Actor, action, EntityType, g.Id, before, Snapshot(g));

    private static string Snapshot(Grant g) => JsonSerializer.Serialize(new
    {
        g.Name,
        g.FunderId,
        g.Reference,
        g.Amount,
        g.AwardedOn,
        g.StartsOn,
        g.EndsOn,
        g.ClassId,
        g.IncomeAccountId,
        g.Conditional,
        g.Terms,
        Status = g.Status.ToString(),
        Budget = g.Budget.Select(b => new { b.AccountId, b.Amount }),
        Receipts = g.Receipts.Select(r => new { r.Date, r.Amount, r.AdvancePart, r.IsVoided }),
        Recognitions = g.Recognitions.Select(r => new { r.Date, r.Amount, r.AdvancePart, r.IsVoided }),
    });
}
