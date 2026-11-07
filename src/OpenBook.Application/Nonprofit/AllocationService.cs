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
        var before = Snapshot(rule);
        rule.SetActive(active);
        store.Add(AuditRecord.Create(clock.UtcNow, user.Actor, AuditAction.Updated, RuleEntity, id, before, Snapshot(rule)));
        await store.SaveChangesAsync(ct);
        return rule;
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
        foreach (var preview in await PreviewAsync(from, to, ct))
        {
            if (preview.Amount <= 0m || ruleIds?.Contains(preview.Rule.Id) == false)
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
        Shares = r.Shares.Select(s => new { s.ClassId, s.Percent }),
    });

    private static string Snapshot(AllocationRun r) => JsonSerializer.Serialize(new { r.RuleId, r.From, r.To, r.Amount, r.JournalEntryId, Status = r.Status.ToString() });
}
