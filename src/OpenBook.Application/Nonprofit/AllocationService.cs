using System.Text.Json;
using OpenBook.Application.Accounts;
using OpenBook.Application.Ledger;
using OpenBook.Application.Reporting;
using OpenBook.Application.Segments;
using OpenBook.Domain.Accounts;
using OpenBook.Domain.Auditing;
using OpenBook.Domain.Common;
using OpenBook.Domain.Ledger;
using OpenBook.Domain.Nonprofit;
using OpenBook.Domain.Segments;

namespace OpenBook.Application.Nonprofit;

/// <summary>What a rule would move for a period.</summary>
public sealed record AllocationPreview(AllocationRule Rule, string Account, decimal Amount);

/// <summary>Spreads shared costs across programs and functions by rule, month by month, in one entry per rule.</summary>
public sealed class AllocationService(INonprofitStore store, AccountService accounts, SegmentService segments, ReportLedger ledger, PostingEngine engine, IClock clock, ICurrentUser user)
{
    public const string RuleEntity = "allocation_rule";
    public const string RunEntity = "allocation_run";

    public Task<IReadOnlyList<AllocationRule>> RulesAsync(CancellationToken ct = default) => store.ListAllocationRulesAsync(ct);

    public Task<IReadOnlyList<AllocationRun>> RunsAsync(CancellationToken ct = default) => store.ListAllocationRunsAsync(ct);

    public async Task<AllocationRule> SaveAsync(Guid? id, AllocationRuleDetails details, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(details);
        await CheckAsync(details, ct);
        var existing = id is { } known ? await store.GetAllocationRuleAsync(known, ct) ?? throw new EntityNotFoundException(RuleEntity, known) : null;
        if (existing is not { IsActive: false })
            await EnsurePoolFreeAsync(details.AccountId, details.PoolClassId, existing?.Id, ct);
        var rule = existing ?? AllocationRule.Create(details);
        var before = existing is null ? null : Snapshot(existing);
        existing?.Update(details);
        if (existing is null)
            store.Add(rule);
        store.Add(AuditRecord.Create(clock.UtcNow, user.Actor, existing is null ? AuditAction.Created : AuditAction.Updated, RuleEntity, rule.Id, before, Snapshot(rule)));
        await store.SaveChangesAsync(ct);
        return rule;
    }

    public async Task<AllocationRule> SetActiveAsync(Guid id, bool active, CancellationToken ct = default)
    {
        var rule = await store.GetAllocationRuleAsync(id, ct) ?? throw new EntityNotFoundException(RuleEntity, id);
        if (active && !rule.IsActive)
            await EnsurePoolFreeAsync(rule.AccountId, rule.PoolClassId, rule.Id, ct);
        var before = Snapshot(rule);
        rule.SetActive(active);
        store.Add(AuditRecord.Create(clock.UtcNow, user.Actor, AuditAction.Updated, RuleEntity, id, before, Snapshot(rule)));
        await store.SaveChangesAsync(ct);
        return rule;
    }

    /// <summary>A rule moves all of what's in its pool, so two on one pool would move it twice.</summary>
    private async Task EnsurePoolFreeAsync(Guid accountId, Guid? poolClassId, Guid? except, CancellationToken ct)
    {
        var pool = poolClassId == Guid.Empty ? null : poolClassId;
        if ((await store.ListAllocationRulesAsync(ct)).FirstOrDefault(r => r.Id != except && r.IsActive && r.AccountId == accountId && r.PoolClassId == pool) is { } other)
            throw new DomainException("allocation.pool_taken", $"\"{other.Name}\" already spreads that account from the same pool, and each would move all of it. Change that rule, or switch it off first.");
    }

    private async Task CheckAsync(AllocationRuleDetails details, CancellationToken ct)
    {
        var chart = await accounts.GetChartAsync(ct);
        if (chart.Find(details.AccountId) is not { } account || account.Type.ClassOf() != AccountClass.Expense)
            throw new DomainException("allocation.account_expense", "Choose an expense account to spread.");
        var classes = (await segments.ListAsync(SegmentKind.Class, includeInactive: false, ct)).Select(c => c.Segment.Id).ToHashSet();
        if (details.Shares?.Any(s => s.Percent != 0m && !classes.Contains(s.ClassId)) == true)
            throw new DomainException("allocation.share_class", "Choose an active class for each share.");
    }

    /// <summary>What each active rule would move: the cost still in its pool for the period, on the accrual basis.</summary>
    public async Task<IReadOnlyList<AllocationPreview>> PreviewAsync(DateOnly from, DateOnly to, CancellationToken ct = default)
    {
        var chart = await accounts.GetChartAsync(ct);
        var lines = await ledger.LinesAsync(ReportBasis.Accrual, from, to, ct);
        return (await store.ListAllocationRulesAsync(ct)).Where(r => r.IsActive)
            .Select(r => new AllocationPreview(r, chart.FullName(r.AccountId), lines.Where(l => l.AccountId == r.AccountId && l.ClassId == r.PoolClassId).Sum(l => l.Amount)))
            .ToList();
    }

    /// <summary>Runs the active rules for a period, each its own entry dated at the end of it, in one save. What's not in a pool any more isn't moved again.</summary>
    public async Task<IReadOnlyList<AllocationRun>> RunAsync(DateOnly from, DateOnly to, IReadOnlyCollection<Guid>? ruleIds = null, CancellationToken ct = default)
    {
        if (to < from)
            throw new DomainException("report.range_invalid", "The start date is after the end date.");
        var runs = new List<AllocationRun>();
        var moved = new HashSet<(Guid, Guid?)>();
        foreach (var preview in await PreviewAsync(from, to, ct))
        {
            if (preview.Amount <= 0m || ruleIds?.Contains(preview.Rule.Id) == false)
                continue;
            // Two rules on one pool, from before they were refused: the first one empties it.
            if (!moved.Add((preview.Rule.AccountId, preview.Rule.PoolClassId)))
                continue;
            var rule = preview.Rule;
            var shares = Shares.ByWeight(preview.Amount, rule.Shares.Select(s => s.Percent).ToList());
            var memo = $"Allocation: {rule.Name}";
            var lines = new List<JournalLineDraft> { JournalLineDraft.Cr(rule.AccountId, preview.Amount, memo, rule.PoolClassId) };
            lines.AddRange(rule.Shares.Select((s, i) => JournalLineDraft.Dr(rule.AccountId, shares[i], memo, s.ClassId)).Where(l => l.Debit != 0m));
            var id = Guid.CreateVersion7();
            var entry = await engine.StageAsync(new PostJournalEntryCommand(to, lines, memo, AllocationRun.Source, id), ct);
            var run = AllocationRun.Create(id, rule.Id, from, to, preview.Amount, entry.Id, clock.UtcNow);
            store.Add(run);
            store.Add(AuditRecord.Create(clock.UtcNow, user.Actor, AuditAction.Posted, RunEntity, id, null, Snapshot(run)));
            runs.Add(run);
        }

        await store.SaveChangesAsync(ct);
        return runs;
    }

    /// <summary>
    /// The rules set to run each month end (C088), for the calendar month before <paramref name="today"/>'s: each one that
    /// has no standing run ending that month is run, on its own, so one refused (a closed month, say) leaves the rest.
    /// Returns the runs made and the refusals, by rule name.
    /// </summary>
    public async Task<(IReadOnlyList<AllocationRun> Runs, IReadOnlyList<string> Refused)> RunDueAsync(DateOnly today, CancellationToken ct = default)
    {
        var to = new DateOnly(today.Year, today.Month, 1).AddDays(-1);
        var from = new DateOnly(to.Year, to.Month, 1);
        var done = (await store.ListAllocationRunsAsync(ct)).Where(r => r.Status == DocumentStatus.Posted && r.To == to).Select(r => r.RuleId).ToHashSet();
        var due = (await store.ListAllocationRulesAsync(ct)).Where(r => r.IsActive && r.RunsMonthly && !done.Contains(r.Id)).ToList();
        var runs = new List<AllocationRun>();
        var refused = new List<string>();
        foreach (var rule in due)
        {
            try
            {
                runs.AddRange(await RunAsync(from, to, [rule.Id], ct));
            }
            catch (DomainException ex)
            {
                refused.Add($"{rule.Name}: {ex.Message}");
            }
        }

        return (runs, refused);
    }

    public async Task<AllocationRun> VoidAsync(Guid id, string reason, CancellationToken ct = default)
    {
        var run = await store.GetAllocationRunAsync(id, ct) ?? throw new EntityNotFoundException(RunEntity, id);
        var before = Snapshot(run);
        await engine.StageVoidAsync(run.JournalEntryId, reason, fromDocument: true, ct);
        run.Void();
        store.Add(AuditRecord.Create(clock.UtcNow, user.Actor, AuditAction.Voided, RunEntity, id, before, Snapshot(run)));
        await store.SaveChangesAsync(ct);
        return run;
    }

    private static string Snapshot(AllocationRule r) => JsonSerializer.Serialize(new
    {
        r.Name,
        r.AccountId,
        r.PoolClassId,
        r.IsActive,
        r.RunsMonthly,
        Shares = r.Shares.Select(s => new { s.ClassId, s.Percent }),
    });

    private static string Snapshot(AllocationRun r) => JsonSerializer.Serialize(new { r.RuleId, r.From, r.To, r.Amount, r.JournalEntryId, Status = r.Status.ToString() });
}
