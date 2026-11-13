using OpenBook.Application.Webhooks;
using OpenBook.Domain.Auditing;

namespace OpenBook.Application.Auditing;

/// <summary>A chain of audit links to read in position order: the company's own, or a backup file's.</summary>
public interface IAuditChainSource
{
    /// <summary>The links after a position, in position order, with their records.</summary>
    Task<IReadOnlyList<ChainedRecord>> ReadAsync(long afterPosition, int take, CancellationToken ct);
}

/// <summary>A backup file's chain, opened read-only with the company file's key.</summary>
public interface IAuditChainFile : IAuditChainSource, IAsyncDisposable
{
    /// <summary>Whether the file has the chain at all (backups from before it don't).</summary>
    bool HasChain { get; }
}

public interface IAuditChainFiles
{
    Task<IAuditChainFile> OpenAsync(string path, CancellationToken ct);
}

public interface IAuditChainStore : IAuditChainSource
{
    /// <summary>The link at the highest position, or null before the first.</summary>
    Task<AuditLink?> HeadAsync(CancellationToken ct);

    /// <summary>The highest audit record id with a link (0 before the first).</summary>
    Task<long> HighestLinkedRecordAsync(CancellationToken ct);

    /// <summary>Audit records after an id, in id order (linked or not: the caller asks after the highest linked).</summary>
    Task<IReadOnlyList<AuditRecord>> ListRecordsAfterAsync(long afterId, int take, CancellationToken ct);

    /// <summary>Audit records with no link, in id order, up to an id.</summary>
    Task<IReadOnlyList<AuditRecord>> ListUnlinkedAsync(long upToId, int take, CancellationToken ct);

    void Add(AuditLink link);

    Task SaveChangesAsync(CancellationToken ct);
}

/// <summary>The outcome of checking a chain: how much was checked, the latest link, and what's wrong.</summary>
public sealed record AuditChainReport(long Checked, ChainedRecord? Head, IReadOnlyList<AuditChainFinding> Findings, IReadOnlyList<AuditRecord> Waiting)
{
    public bool Broken => Findings.Any(f => f.Serious);
}

/// <summary>Where a backup's chain and the company's part ways, if they do.</summary>
public sealed record AuditChainComparison(long Compared, ChainedRecord? BackupHead, IReadOnlyList<AuditChainFinding> Differences)
{
    public bool Same => Differences.Count == 0;
}

/// <summary>
/// The tamper-evident audit trail (2026-11-10). Each audit record is linked, a few seconds after it's saved, to the one
/// before it by a SHA-256 hash (<see cref="AuditChain"/>), so a record changed or deleted later shows when the chain is
/// checked. Someone who can edit the database could rewrite every hash after a change too; a backup's chain shows that,
/// since everything it holds must still be in the company's chain unchanged (<see cref="CompareAsync"/>).
/// </summary>
public sealed class AuditChainService(IAuditChainStore store, IClock clock)
{
    private const int Batch = 1000;

    /// <summary>Most findings kept from a check; the counts stay whole.</summary>
    public const int MaxFindings = 50;

    /// <summary>How long an unlinked record may wait before a check mentions it (the job runs every half minute).</summary>
    public static readonly TimeSpan WaitingAfter = TimeSpan.FromMinutes(15);

    /// <summary>
    /// Links what's been saved since the last run, oldest first. A gap in the ids may be a save still running (its
    /// ids show only when it commits), so linking stops there until the gap is <see cref="WebhookDispatcher.GapWait"/>
    /// old, as webhooks do. With <paramref name="late"/>, records behind the highest linked one that are still unlinked
    /// (a save that took longer than that) go on the end first; that reads every record, so the job asks hourly.
    /// </summary>
    /// <returns>How many records were linked.</returns>
    public async Task<int> ExtendAsync(bool late, CancellationToken ct = default)
    {
        var linked = 0;
        var head = await store.HeadAsync(ct);
        var previous = head?.Hash ?? AuditChain.Genesis;
        var position = head?.Position ?? 0;
        var highest = await store.HighestLinkedRecordAsync(ct);

        if (late && highest > 0)
        {
            while (true)
            {
                var waiting = await store.ListUnlinkedAsync(highest, Batch, ct);
                if (waiting.Count == 0)
                    break;
                foreach (var record in waiting)
                    Link(record);
                await store.SaveChangesAsync(ct);
                if (waiting.Count < Batch)
                    break;
            }
        }

        while (true)
        {
            var records = await store.ListRecordsAfterAsync(highest, Batch, ct);
            var settled = WebhookDispatcher.Settled(highest, records, clock.UtcNow);
            if (settled.Count == 0)
                break;
            foreach (var record in settled)
            {
                Link(record);
                highest = record.Id;
            }

            await store.SaveChangesAsync(ct);
            if (settled.Count < records.Count || records.Count < Batch)
                break;
        }

        return linked;

        void Link(AuditRecord record)
        {
            position++;
            previous = AuditChain.Hash(previous, position, record);
            store.Add(AuditLink.Create(position, record.Id, previous, clock.UtcNow));
            linked++;
        }
    }

    /// <summary>Checks the company's chain from the start, and lists records still waiting to join it.</summary>
    public async Task<AuditChainReport> VerifyAsync(CancellationToken ct = default)
    {
        var verifier = await WalkAsync(store, ct);
        var now = clock.UtcNow;
        var waiting = (await store.ListUnlinkedAsync(long.MaxValue, MaxFindings + 1, ct)).Where(r => now - r.OccurredAt > WaitingAfter).ToList();
        return new AuditChainReport(verifier.Checked, verifier.Last, verifier.Findings.Take(MaxFindings).ToList(), waiting);
    }

    /// <summary>Checks any chain on its own (a backup's, say).</summary>
    public static async Task<AuditChainReport> VerifyAsync(IAuditChainSource source, CancellationToken ct = default)
    {
        var verifier = await WalkAsync(source, ct);
        return new AuditChainReport(verifier.Checked, verifier.Last, verifier.Findings.Take(MaxFindings).ToList(), []);
    }

    /// <summary>
    /// Compares a backup's chain with the company's: every link the backup holds must be in the company's chain at the
    /// same position, for the same record, with the same hash. A difference means the company's audit trail was
    /// rewritten after the backup was taken, or that the company was restored from an older backup since (the audit
    /// log shows a restore).
    /// </summary>
    public async Task<AuditChainComparison> CompareAsync(IAuditChainSource backup, CancellationToken ct = default)
    {
        var differences = new List<AuditChainFinding>();
        long compared = 0;
        ChainedRecord? backupHead = null;
        long after = 0;
        var different = 0;
        while (true)
        {
            var theirs = await backup.ReadAsync(after, Batch, ct);
            if (theirs.Count == 0)
                break;
            var ours = (await store.ReadAsync(after, theirs.Count, ct)).ToDictionary(l => l.Position);
            foreach (var link in theirs)
            {
                compared++;
                backupHead = link;
                string? text = null;
                if (!ours.TryGetValue(link.Position, out var mine))
                    text = $"Position {link.Position:N0} (audit record #{link.RecordId:N0}) is in the backup but not in the company's chain.";
                else if (mine.RecordId != link.RecordId || mine.Hash != link.Hash)
                    text = $"Position {link.Position:N0} differs: the backup links audit record #{link.RecordId:N0} ({AuditChain.Fingerprint(link.Hash)}), the company audit record #{mine.RecordId:N0} ({AuditChain.Fingerprint(mine.Hash)}).";
                if (text is not null && ++different <= MaxFindings)
                    differences.Add(new AuditChainFinding(link.Position, link.RecordId, text));
            }

            after = theirs[^1].Position;
            if (theirs.Count < Batch)
                break;
        }

        if (different > MaxFindings)
            differences.Add(new AuditChainFinding(after, null, $"{different - MaxFindings:N0} more positions differ."));
        return new AuditChainComparison(compared, backupHead, differences);
    }

    private static async Task<AuditChainVerifier> WalkAsync(IAuditChainSource source, CancellationToken ct)
    {
        var verifier = new AuditChainVerifier();
        long after = 0;
        while (true)
        {
            var links = await source.ReadAsync(after, Batch, ct);
            foreach (var link in links)
                verifier.Add(link);
            if (links.Count < Batch)
                break;
            after = links[^1].Position;
        }

        return verifier;
    }
}
