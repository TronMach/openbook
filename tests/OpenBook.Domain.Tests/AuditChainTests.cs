using OpenBook.Domain.Auditing;

namespace OpenBook.Domain.Tests;

/// <summary>
/// The audit chain's hashing rule. The expected hashes were worked out apart from this code, from the rule as written
/// (fields with their lengths, null as "-", UTC to the microsecond, SHA-256 of the UTF-8): if one changes, every chain
/// already written stops checking, so the rule must never change.
/// </summary>
public sealed class AuditChainTests
{
    private static readonly Guid Entity = Guid.Parse("0193a3c4-0000-7000-8000-000000000001");

    private const string First = "1193121237513c4f29624b4bfd2aca60598645d5d006b27cddc60ea120697322";
    private const string Second = "f8c8b748a60e20488277a77756b4677c126b6398f335a9e21533c35bb525c1a1";

    private static readonly DateTimeOffset At = new DateTimeOffset(2026, 11, 10, 16, 14, 58, TimeSpan.Zero).AddTicks(1234567);

    [Fact]
    public void Hashes_follow_the_written_rule()
    {
        Assert.Equal(First, AuditChain.Hash(AuditChain.Genesis, 1, 42, At, "José", "Created", "invoice", Entity, null, """{"Total":100}"""));
        Assert.Equal(Second, AuditChain.Hash(First, 2, 43, new DateTimeOffset(2026, 11, 10, 16, 15, 0, TimeSpan.Zero), "system", "Voided", "invoice", Entity,
            """{"Total":100}""", ""));
    }

    [Fact]
    public void Time_is_utc_to_the_microsecond_whatever_the_offset()
    {
        Assert.Equal("2026-11-10T16:14:58.123456Z", AuditChain.Time(At));
        Assert.Equal(AuditChain.Time(At), AuditChain.Time(At.ToOffset(TimeSpan.FromHours(-5))));
        // What PostgreSQL keeps (whole microseconds) hashes the same as what SQLite keeps.
        Assert.Equal(AuditChain.Time(At), AuditChain.Time(At.AddTicks(-7)));
    }

    [Fact]
    public void Null_and_empty_and_fields_run_together_hash_differently()
    {
        var empty = AuditChain.Hash(AuditChain.Genesis, 1, 1, At, "a", "Created", "x", Entity, "", null);
        var none = AuditChain.Hash(AuditChain.Genesis, 1, 1, At, "a", "Created", "x", Entity, null, null);
        Assert.NotEqual(empty, none);
        Assert.NotEqual(
            AuditChain.Hash(AuditChain.Genesis, 1, 1, At, "ab", "Created", "x", Entity, null, null),
            AuditChain.Hash(AuditChain.Genesis, 1, 1, At, "a", "bCreated", "x", Entity, null, null));
    }

    [Fact]
    public void The_verifier_finds_a_change_once_and_carries_on()
    {
        var links = Chain(5);
        links[2] = links[2] with { Actor = "someone else" };
        var verifier = new AuditChainVerifier();
        foreach (var link in links)
            verifier.Add(link);

        var finding = Assert.Single(verifier.Findings);
        Assert.Equal(3, finding.Position);
        Assert.Equal(5, verifier.Checked);
    }

    [Fact]
    public void The_verifier_names_missing_positions()
    {
        var links = Chain(6);
        var verifier = new AuditChainVerifier();
        foreach (var link in links.Where(l => l.Position is not (3 or 4)))
            verifier.Add(link);

        var finding = Assert.Single(verifier.Findings);
        Assert.Contains("Positions 3 to 4 are missing", finding.Text, StringComparison.Ordinal);
        Assert.True(verifier.Broken);
    }

    private static List<ChainedRecord> Chain(int count)
    {
        var links = new List<ChainedRecord>();
        var previous = AuditChain.Genesis;
        for (var i = 1; i <= count; i++)
        {
            var hash = AuditChain.Hash(previous, i, i, At.AddMinutes(i), "owner", "Created", "account", Entity, null, $$"""{"N":{{i}}}""");
            links.Add(new ChainedRecord(i, hash, At, i, At.AddMinutes(i), "owner", "Created", "account", Entity, null, $$"""{"N":{{i}}}"""));
            previous = hash;
        }

        return links;
    }
}
