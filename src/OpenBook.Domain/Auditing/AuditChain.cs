using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace OpenBook.Domain.Auditing;

/// <summary>
/// One audit record's place in the chain: its position, and a SHA-256 hash over the previous link's hash and the
/// record's own fields. Changing, removing or reordering a record already linked breaks every hash after it. Links are
/// added a few seconds after the record is saved, by a background job, and the database refuses to change or delete
/// them. Positions count up from 1 in the order records were linked, which is the order of their ids except for a
/// record whose save committed late (linked at the end).
/// </summary>
public sealed class AuditLink
{
    private AuditLink() => Hash = string.Empty;

    public long Position { get; private set; }
    public long AuditRecordId { get; private set; }

    /// <summary>Lower-case hex SHA-256 (<see cref="AuditChain.Hash"/>).</summary>
    public string Hash { get; private set; }

    /// <summary>When the record joined the chain (UTC).</summary>
    public DateTimeOffset LinkedAt { get; private set; }

    public static AuditLink Create(long position, long auditRecordId, string hash, DateTimeOffset linkedAt) =>
        new() { Position = position, AuditRecordId = auditRecordId, Hash = hash, LinkedAt = linkedAt.ToUniversalTime() };
}

/// <summary>
/// A link with the fields of the record it covers, as read back from a database or a backup file. <see cref="Found"/>
/// is false when the record the link names is gone (the other record fields are then empty).
/// </summary>
public sealed record ChainedRecord(
    long Position, string Hash, DateTimeOffset LinkedAt,
    long RecordId, DateTimeOffset OccurredAt, string Actor, string Action, string EntityType, Guid EntityId, string? Before, string? After,
    bool Found = true);

/// <summary>What a chain check found at one place: a broken link, missing links, a record gone, or a record linked late.</summary>
public sealed record AuditChainFinding(long Position, long? RecordId, string Text, bool Serious = true);

/// <summary>
/// Walks a chain in position order, a batch at a time, recomputing each hash. After a break it carries on from the
/// stored hash, so one changed record is one finding rather than everything after it.
/// </summary>
public sealed class AuditChainVerifier
{
    private string _previous = AuditChain.Genesis;
    private long _expected = 1;
    private long _highestRecordId;
    private readonly List<AuditChainFinding> _findings = [];

    public long Checked { get; private set; }
    public ChainedRecord? Last { get; private set; }
    public IReadOnlyList<AuditChainFinding> Findings => _findings;
    public bool Broken => _findings.Any(f => f.Serious);

    public void Add(ChainedRecord link)
    {
        Checked++;
        var afterGap = false;
        if (link.Position != _expected)
        {
            afterGap = true;
            _findings.Add(link.Position > _expected
                ? new AuditChainFinding(_expected, null, link.Position == _expected + 1
                    ? $"Position {_expected:N0} is missing from the chain: a link was deleted."
                    : $"Positions {_expected:N0} to {link.Position - 1:N0} are missing from the chain: links were deleted.")
                : new AuditChainFinding(link.Position, link.RecordId, $"Position {link.Position:N0} comes again, out of order."));
        }

        if (!link.Found)
            _findings.Add(new AuditChainFinding(link.Position, link.RecordId,
                $"Audit record #{link.RecordId:N0} (position {link.Position:N0}) is gone: it was deleted after it was written."));
        // After missing links the previous hash isn't known, so this one can't be checked; the next one can.
        else if (!afterGap && AuditChain.Hash(_previous, link) != link.Hash)
            _findings.Add(new AuditChainFinding(link.Position, link.RecordId,
                $"The chain breaks at audit record #{link.RecordId:N0} (position {link.Position:N0}): that record, or the link before it, was changed after it was written."));
        else if (link.RecordId < _highestRecordId)
            _findings.Add(new AuditChainFinding(link.Position, link.RecordId,
                $"Audit record #{link.RecordId:N0} joined the chain after later ones: the save that wrote it took more than a few minutes to finish.", Serious: false));

        _highestRecordId = Math.Max(_highestRecordId, link.RecordId);
        _previous = link.Hash;
        _expected = Math.Max(_expected, link.Position + 1);
        Last = link;
    }
}

/// <summary>The hashing rule of the audit chain. Never change it: every chain ever written depends on it.</summary>
public static class AuditChain
{
    /// <summary>What the first link chains from.</summary>
    public static readonly string Genesis = new('0', 64);

    private const string Version = "openbook-audit-chain-v1";

    /// <summary>
    /// The hash of the link at <paramref name="position"/> for this record, after <paramref name="previous"/>. Every
    /// field goes in with its length, so no two records can run together into the same text; the time goes in as UTC
    /// to the microsecond, since PostgreSQL keeps no more than that and a company moved there must check the same.
    /// </summary>
    public static string Hash(string previous, long position, long recordId, DateTimeOffset occurredAt, string actor, string action,
        string entityType, Guid entityId, string? before, string? after)
    {
        var text = new StringBuilder(256 + (before?.Length ?? 0) + (after?.Length ?? 0));
        Field(text, Version);
        Field(text, previous);
        Field(text, position.ToString(CultureInfo.InvariantCulture));
        Field(text, recordId.ToString(CultureInfo.InvariantCulture));
        Field(text, Time(occurredAt));
        Field(text, actor);
        Field(text, action);
        Field(text, entityType);
        Field(text, entityId.ToString("D"));
        Field(text, before);
        Field(text, after);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())));
    }

    public static string Hash(string previous, long position, AuditRecord record) =>
        Hash(previous, position, record.Id, record.OccurredAt, record.Actor, record.Action.ToString(), record.EntityType, record.EntityId, record.Before, record.After);

    public static string Hash(string previous, ChainedRecord link) =>
        Hash(previous, link.Position, link.RecordId, link.OccurredAt, link.Actor, link.Action, link.EntityType, link.EntityId, link.Before, link.After);

    /// <summary>UTC, to the microsecond (ticks below it dropped, as Npgsql drops them).</summary>
    public static string Time(DateTimeOffset value)
    {
        var utc = value.UtcTicks;
        return new DateTime(utc - utc % 10, DateTimeKind.Utc).ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'", CultureInfo.InvariantCulture);
    }

    /// <summary>The first few characters of a hash, enough to compare by eye or write down.</summary>
    public static string Fingerprint(string hash) => hash.Length <= 16 ? hash : hash[..16];

    private static void Field(StringBuilder text, string? value)
    {
        // Null and empty differ: "-" for null, the length then the text otherwise.
        if (value is null)
            text.Append('-');
        else
            text.Append(value.Length.ToString(CultureInfo.InvariantCulture)).Append(':').Append(value);
        text.Append('\n');
    }
}
