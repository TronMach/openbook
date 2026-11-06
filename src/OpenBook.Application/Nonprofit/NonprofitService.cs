using System.Text.Json;
using OpenBook.Application.Accounts;
using OpenBook.Application.Companies;
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

public interface INonprofitStore
{
    Task<IReadOnlyList<RestrictionRelease>> ListReleasesAsync(CancellationToken ct);
    Task<RestrictionRelease?> GetReleaseAsync(Guid id, CancellationToken ct);

    Task<IReadOnlyList<Donation>> ListDonationsAsync(CancellationToken ct);
    Task<Donation?> GetDonationAsync(Guid id, CancellationToken ct);
    Task<IReadOnlyList<Pledge>> ListPledgesAsync(CancellationToken ct);
    Task<Pledge?> GetPledgeAsync(Guid id, CancellationToken ct);
    Task<GiftSettings?> GetSettingsAsync(CancellationToken ct);
    Task<IReadOnlyList<Grant>> ListGrantsAsync(CancellationToken ct);
    Task<IReadOnlyList<RecurringGift>> ListRecurringGiftsAsync(CancellationToken ct);
    Task<RecurringGift?> GetRecurringGiftAsync(Guid id, CancellationToken ct);
    Task<Grant?> GetGrantAsync(Guid id, CancellationToken ct);

    void Add(RestrictionRelease release);
    void Add(Donation donation);
    void Add(Pledge pledge);
    void Add(GiftSettings settings);
    void Add(Grant grant);
    void Add(RecurringGift recurringGift);
    void Add(AuditRecord record);

    Task SaveChangesAsync(CancellationToken ct);
}

/// <summary>
/// Restricted money for one purpose (a class; null = no class, such as a time restriction) as of a date.
/// <paramref name="Spent"/> is what the purpose's expenses came to since its first restricted gift, and
/// <paramref name="Suggested"/> the part of that not yet released, up to the balance.
/// </summary>
public sealed record RestrictedPurpose(Guid? ClassId, string Name, decimal Received, decimal Released, decimal Balance, decimal Spent, decimal Suggested);

/// <param name="ClassId">The purpose; null or <see cref="Guid.Empty"/> for one without a class.</param>
public sealed record ReleaseCommand(DateOnly Date, Guid? ClassId, decimal Amount, string? Memo = null);

/// <summary>
/// Donor restrictions: what's held for each purpose and releasing it once spent (or once its time comes). The
/// restriction lives on the accounts (<see cref="Account.DonorRestricted"/>) and the purpose on the class, so
/// gifts, grants and opening balances need nothing new to record it.
/// </summary>
public sealed class NonprofitService(
    INonprofitStore store, ILedgerStore ledgerStore, AccountService accounts, SegmentService segments, ReportLedger ledger, PostingEngine engine,
    IClock clock, ICurrentUser user)
{
    public const string EntityType = "restriction_release";
    public const string ReleasedParentName = "Net Assets Released from Restrictions";
    public const string ReleasedFromName = "Released From Restricted";
    public const string ReleasedToName = "Released To Unrestricted";

    public async Task<IReadOnlyList<RestrictionRelease>> ListReleasesAsync(CancellationToken ct = default) =>
        (await store.ListReleasesAsync(ct)).OrderByDescending(r => r.Date).ThenByDescending(r => r.CreatedAt).ToList();

    public Task<RestrictionRelease?> GetReleaseAsync(Guid id, CancellationToken ct = default) => store.GetReleaseAsync(id, ct);

    /// <summary>Every purpose with restricted activity through the date (accrual: restrictions follow the gifts as recorded).</summary>
    public async Task<IReadOnlyList<RestrictedPurpose>> PurposesAsync(DateOnly asOf, CancellationToken ct = default)
    {
        var chart = await accounts.GetChartAsync(ct);
        var releasedFrom = FindReleaseAccount(chart, restricted: true)?.Id;
        var names = await segments.NamesAsync(ct);
        var lines = await ledger.LinesAsync(ReportBasis.Accrual, null, asOf, ct);

        // A gift voided since never was one: it doesn't start the count of spending against the purpose.
        var undone = (await ledgerStore.ListEntryHeadersAsync(ct))
            .Where(e => e.Status == JournalEntryStatus.Voided || e.Kind == JournalEntryKind.Reversal).Select(e => e.Id).ToHashSet();
        var held = new List<(Guid? ClassId, decimal Received, decimal Released, DateOnly? FirstGift)>();
        foreach (var group in lines.GroupBy(l => l.ClassId))
        {
            decimal received = 0m, released = 0m;
            DateOnly? firstGift = null;
            foreach (var line in group)
            {
                var account = chart.Get(line.AccountId);
                if (line.AccountId == releasedFrom)
                    released += line.Amount;
                else if (account.DonorRestricted)
                {
                    received -= line.Amount;
                    if (line.Amount < 0m && !undone.Contains(line.JournalEntryId) && (firstGift is null || line.Date < firstGift))
                        firstGift = line.Date;
                }
            }

            if (received != 0m || released != 0m)
                held.Add((group.Key, received, released, firstGift));
        }

        // Spending on a class counts for the nearest class up its tree that holds restricted money, so nothing counts twice.
        var parents = (await segments.ListAsync(SegmentKind.Class, includeInactive: true, ct)).ToDictionary(c => c.Segment.Id, c => c.Segment.ParentId);
        var since = held.Where(h => h.FirstGift is not null).ToDictionary(h => h.ClassId ?? Guid.Empty, h => h.FirstGift!.Value);
        Guid? PurposeOf(Guid? classId)
        {
            for (var at = classId; at is { } id; at = parents.GetValueOrDefault(id))
            {
                if (since.ContainsKey(id))
                    return id;
            }

            return null;
        }

        var spentOn = lines
            .Where(l => chart.Get(l.AccountId).Type.ClassOf() == AccountClass.Expense)
            .Select(l => (Line: l, Purpose: l.ClassId is null ? (since.ContainsKey(Guid.Empty) ? Guid.Empty : (Guid?)null) : PurposeOf(l.ClassId)))
            .Where(x => x.Purpose is { } p && x.Line.Date >= since[p])
            .GroupBy(x => x.Purpose!.Value)
            .ToDictionary(g => g.Key, g => g.Sum(x => x.Line.Amount));

        var purposes = new List<RestrictedPurpose>();
        foreach (var (classId, received, released, _) in held)
        {
            var spent = spentOn.GetValueOrDefault(classId ?? Guid.Empty);
            var balance = received - released;
            var suggested = classId is null ? 0m : Math.Max(0m, Math.Min(balance, spent - released));
            var name = classId is { } id ? names.GetValueOrDefault(id) ?? "?" : SegmentRules.NotSpecified;
            purposes.Add(new RestrictedPurpose(classId, name, received, released, balance, spent, suggested));
        }

        return purposes.OrderBy(p => p.ClassId is null).ThenBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    /// <summary>
    /// Releases restricted money for a purpose: Dr "Released From Restricted", Cr "Released To Unrestricted", both on
    /// the class. Refused beyond what the purpose holds on the date.
    /// </summary>
    public async Task<RestrictionRelease> ReleaseAsync(ReleaseCommand command, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        var classId = command.ClassId == Guid.Empty ? null : command.ClassId;
        // Later releases count too: a backdated one mustn't take what's been released since.
        var today = CompanyTime.TodayAt(clock.UtcNow, CompanyTime.Zone);
        var held = (await PurposesAsync(command.Date, ct)).FirstOrDefault(p => p.ClassId == classId)?.Balance ?? 0m;
        if (command.Date < today)
            held = Math.Min(held, (await PurposesAsync(today, ct)).FirstOrDefault(p => p.ClassId == classId)?.Balance ?? 0m);
        if (command.Amount > held)
            throw new DomainException("release.more_than_restricted",
                held <= 0m ? "Nothing is held with donor restrictions for this purpose on that date."
                : $"Only {held:N2} is held with donor restrictions for this purpose on that date.");

        var (from, to) = await ReleaseAccountsAsync(ct);
        var id = Guid.CreateVersion7();
        if (command.Memo?.Trim().Length > RestrictionRelease.MaxMemoLength)
            throw new DomainException("release.memo_too_long", $"Memo can be at most {RestrictionRelease.MaxMemoLength} characters.");
        var memo = string.IsNullOrWhiteSpace(command.Memo) ? "Released from donor restrictions" : command.Memo.Trim();
        var entry = await engine.StageAsync(new PostJournalEntryCommand(command.Date,
            [JournalLineDraft.Dr(from.Id, command.Amount, memo, classId), JournalLineDraft.Cr(to.Id, command.Amount, memo, classId)],
            memo, RestrictionRelease.Source, id), ct);
        var release = RestrictionRelease.Create(id, command.Date, classId, command.Amount, command.Memo, entry.Id, clock.UtcNow);
        store.Add(release);
        store.Add(Audit(AuditAction.Posted, release, null));
        await store.SaveChangesAsync(ct);
        return release;
    }

    public async Task<RestrictionRelease> VoidAsync(Guid id, string reason, CancellationToken ct = default)
    {
        var release = await store.GetReleaseAsync(id, ct) ?? throw new EntityNotFoundException(EntityType, id);
        if (release.Status == DocumentStatus.Voided)
            throw new DomainException("release.already_voided", "This release is already voided.");
        var before = Snapshot(release);
        await engine.StageVoidAsync(release.JournalEntryId, reason, fromDocument: true, ct);
        release.Void();
        store.Add(Audit(AuditAction.Voided, release, before));
        await store.SaveChangesAsync(ct);
        return release;
    }

    /// <summary>
    /// The usual functional classes: "Programs" (program services; add each program under it), "Management and
    /// General" and "Fundraising", created where missing (an existing class of the same name gets the area if it has
    /// none), and class tracking turned on. Returns how many were created or changed.
    /// </summary>
    public async Task<int> SetUpFunctionalClassesAsync(CancellationToken ct = default)
    {
        var changed = 0;
        var existing = await segments.ListAsync(SegmentKind.Class, includeInactive: true, ct);
        foreach (var (name, area) in new[] { ("Programs", FunctionalArea.Program), ("Management and General", FunctionalArea.ManagementAndGeneral), ("Fundraising", FunctionalArea.Fundraising) })
        {
            var segment = existing.Select(c => c.Segment).FirstOrDefault(s => s.ParentId is null && string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));
            if (segment is null)
            {
                segment = await segments.CreateAsync(SegmentKind.Class, name, null, ct);
                changed++;
            }
            else if (segment.FunctionalArea is not null)
                continue;
            else
                changed++;
            await segments.SetFunctionalAreaAsync(segment.Id, area, ct);
        }

        var tracking = await segments.GetTrackingAsync(ct);
        if (!tracking.Classes)
            await segments.SetTrackingAsync(tracking with { Classes = true }, ct);
        return changed;
    }

    /// <summary>The release pair under "Net Assets Released from Restrictions" (income), created on first use.</summary>
    public async Task<(Account From, Account To)> ReleaseAccountsAsync(CancellationToken ct = default)
    {
        var chart = await accounts.GetChartAsync(ct);
        if (FindReleaseAccount(chart, restricted: true) is { } from && FindReleaseAccount(chart, restricted: false) is { } to)
            return (from, to);

        var parent = chart.Ordered(includeInactive: false).Select(x => x.Account)
                         .FirstOrDefault(a => a.ParentId is null && a.Type == AccountType.Income && string.Equals(a.Name, ReleasedParentName, StringComparison.OrdinalIgnoreCase))
                     ?? await accounts.CreateAsync(new CreateAccountCommand(ReleasedParentName, AccountType.Income,
                         Description: "Moves restricted amounts to unrestricted once the purpose is met"), ct);
        from = FindReleaseAccount(chart, restricted: true)
               ?? await accounts.CreateAsync(new CreateAccountCommand(ReleasedFromName, AccountType.Income, ParentId: parent.Id, DonorRestricted: true), ct);
        to = FindReleaseAccount(chart, restricted: false)
             ?? await accounts.CreateAsync(new CreateAccountCommand(ReleasedToName, AccountType.Income, ParentId: parent.Id), ct);
        return (from, to);
    }

    /// <summary>An active child of "Net Assets Released from Restrictions" with the given restriction.</summary>
    public static Account? FindReleaseAccount(ChartOfAccounts chart, bool restricted) =>
        chart.Ordered(includeInactive: false).Select(x => x.Account)
            .FirstOrDefault(a => a.ParentId is { } p && a.DonorRestricted == restricted && a.Type == AccountType.Income
                                 && string.Equals(chart.Get(p).Name, ReleasedParentName, StringComparison.OrdinalIgnoreCase));

    private AuditRecord Audit(AuditAction action, RestrictionRelease r, string? before) =>
        AuditRecord.Create(clock.UtcNow, user.Actor, action, EntityType, r.Id, before, Snapshot(r));

    private static string Snapshot(RestrictionRelease r) => JsonSerializer.Serialize(new
    {
        r.Date,
        r.ClassId,
        r.Amount,
        r.Memo,
        Status = r.Status.ToString(),
    });
}
