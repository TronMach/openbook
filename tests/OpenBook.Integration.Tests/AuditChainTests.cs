using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OpenBook.Application.Auditing;
using OpenBook.Application.Companies;
using OpenBook.Application.Ledger;
using OpenBook.Domain.Accounts;
using OpenBook.Domain.Auditing;
using OpenBook.Infrastructure.Persistence;

namespace OpenBook.Integration.Tests;

/// <summary>
/// The tamper-evident audit trail: records are chained in order, a record changed or a link deleted afterwards breaks
/// the chain, a chain rewritten from the change on still differs from a backup taken before, and a record whose save
/// committed late joins at the end. On both providers, except the backup file (SQLite).
/// </summary>
public sealed class AuditChainTests : IAsyncLifetime
{
    private readonly LedgerDatabase _db = new();
    private readonly List<string> _files = [];

    public async Task InitializeAsync()
    {
        await _db.InitializeAsync();
        await _db.CreateAccount("Checking", AccountType.Bank, "1000");
        await _db.CreateAccount("Supplies", AccountType.Expense, "6100");
    }

    public async Task DisposeAsync()
    {
        await _db.DisposeAsync();
        foreach (var file in _files)
            File.Delete(file);
    }

    private Task<int> Extend(bool late = false) => _db.InScope(sp => sp.GetRequiredService<AuditChainService>().ExtendAsync(late));

    private Task<AuditChainReport> Verify() => _db.InScope(sp => sp.GetRequiredService<AuditChainService>().VerifyAsync());

    private Task<long> RecordCount() => _db.InDb(db => db.AuditRecords.LongCountAsync());

    private Task<List<AuditLink>> Links() => _db.InDb(db => db.AuditLinks.AsNoTracking().OrderBy(l => l.Position).ToListAsync());

    private Task<int> Sql(string sql) => _db.InDb(db => db.Database.ExecuteSqlRawAsync(sql));

    private Task<int> DropTrigger(string name, string table) =>
        Sql(LedgerDatabase.OnPostgres ? $"""DROP TRIGGER "{name}" ON "{table}";""" : $"""DROP TRIGGER "{name}";""");

    [Fact]
    public async Task Links_every_record_in_order_and_the_chain_checks()
    {
        var linked = await Extend();

        Assert.Equal(await RecordCount(), linked);
        var links = await Links();
        Assert.Equal(Enumerable.Range(1, links.Count).Select(i => (long)i), links.Select(l => l.Position));
        Assert.Equal(links.Select(l => l.AuditRecordId).Order(), links.Select(l => l.AuditRecordId));

        var report = await Verify();
        Assert.False(report.Broken, string.Join("; ", report.Findings.Select(f => f.Text)));
        Assert.Empty(report.Findings);
        Assert.Equal(links.Count, report.Checked);
        Assert.Equal(links[^1].Hash, report.Head!.Hash);

        var check = await _db.InScope(sp => sp.GetRequiredService<BooksCheckService>().RunAsync());
        var trail = Assert.Single(check.Results, r => r.Key == "audit-trail");
        Assert.Equal(CheckOutcome.Ok, trail.Outcome);
        Assert.Contains(AuditChain.Fingerprint(links[^1].Hash), trail.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Extending_again_adds_only_what_was_saved_since()
    {
        await Extend();
        var before = await Links();

        Assert.Equal(0, await Extend());
        await _db.CreateAccount("Rent", AccountType.Expense, "6200");
        Assert.True(await Extend() > 0);

        var after = await Links();
        Assert.Equal(before.Select(l => l.Hash), after.Take(before.Count).Select(l => l.Hash));
        Assert.Equal(await RecordCount(), after.Count);
        Assert.False((await Verify()).Broken);
    }

    [Fact]
    public async Task A_record_changed_afterwards_breaks_the_chain_there()
    {
        await Extend();
        var links = await Links();
        var target = links[links.Count / 2];

        await DropTrigger("TR_AuditRecords_NoUpdate", "AuditRecords");
        await Sql($"""UPDATE "AuditRecords" SET "Actor" = 'someone else' WHERE "Id" = {target.AuditRecordId};""");

        var report = await Verify();
        Assert.True(report.Broken);
        var finding = Assert.Single(report.Findings);
        Assert.Equal(target.Position, finding.Position);
        Assert.Contains($"#{target.AuditRecordId}", finding.Text, StringComparison.Ordinal);

        var check = await _db.InScope(sp => sp.GetRequiredService<BooksCheckService>().RunAsync());
        Assert.Equal(CheckOutcome.Problem, check.Results.Single(r => r.Key == "audit-trail").Outcome);
    }

    [Fact]
    public async Task A_deleted_link_leaves_a_gap_the_check_finds()
    {
        await Extend();
        await DropTrigger("TR_AuditChain_NoDelete", "AuditChain");
        await Sql("""DELETE FROM "AuditChain" WHERE "Position" = 2;""");

        var report = await Verify();
        Assert.True(report.Broken);
        var finding = Assert.Single(report.Findings);
        Assert.Equal(2, finding.Position);
        Assert.Contains("Position 2 is missing", finding.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_chain_can_be_neither_changed_nor_deleted()
    {
        await Extend();

        await Assert.ThrowsAnyAsync<DbException>(() => Sql("""UPDATE "AuditChain" SET "Hash" = 'x' WHERE "Position" = 1;"""));
        await Assert.ThrowsAnyAsync<DbException>(() => Sql("""DELETE FROM "AuditChain" WHERE "Position" = 1;"""));
        if (LedgerDatabase.OnPostgres)
            await Assert.ThrowsAnyAsync<DbException>(() => Sql("""TRUNCATE "AuditChain";"""));
        Assert.False((await Verify()).Broken);
    }

    [Fact]
    public async Task A_record_saved_late_waits_for_its_gap_then_joins_at_the_end()
    {
        await Extend();
        var highest = await _db.InDb(db => db.AuditRecords.MaxAsync(r => r.Id));

        // A save still running holds the next id; a later one has committed with the one after.
        await InsertRecord(highest + 2);
        Assert.Equal(0, await Extend());

        // Five minutes on, the gap is taken for a save that was rolled back, and linking goes past it.
        _db.Clock.UtcNow = _db.Clock.UtcNow.AddMinutes(6);
        Assert.Equal(1, await Extend());

        // Then the long save commits after all: it joins on the hourly look behind the gap, at the end.
        await InsertRecord(highest + 1);
        Assert.Equal(0, await Extend());
        Assert.Equal(1, await Extend(late: true));

        var links = await Links();
        Assert.Equal(highest + 1, links[^1].AuditRecordId);
        var report = await Verify();
        Assert.False(report.Broken);
        var note = Assert.Single(report.Findings);
        Assert.False(note.Serious);
        Assert.Contains($"#{highest + 1}", note.Text, StringComparison.Ordinal);
    }

    [SqliteFact]
    public async Task A_chain_rewritten_after_a_backup_differs_from_the_backup()
    {
        await Extend();
        var backup = await _db.InScope(sp => sp.GetRequiredService<IBackupService>().CreateAsync(CancellationToken.None));
        _files.Add(backup.Path);
        await _db.CreateAccount("Rent", AccountType.Expense, "6200");
        await Extend();

        // Unchanged: everything the backup holds is still in the company's chain.
        var same = await Compare(backup.Path);
        Assert.True(same.Same);
        Assert.True(same.Compared > 0);

        // Someone who can edit the database changes a record and rebuilds every link from it on.
        var links = await Links();
        var target = links[2];
        await DropTrigger("TR_AuditRecords_NoUpdate", "AuditRecords");
        await DropTrigger("TR_AuditChain_NoDelete", "AuditChain");
        await Sql($"""UPDATE "AuditRecords" SET "Actor" = 'someone else' WHERE "Id" = {target.AuditRecordId};""");
        await Sql($"""DELETE FROM "AuditChain" WHERE "Position" >= {target.Position};""");
        await Extend(late: true);

        Assert.False((await Verify()).Broken);
        var compared = await Compare(backup.Path);
        Assert.False(compared.Same);
        Assert.Equal(target.Position, compared.Differences[0].Position);
    }

    private Task<AuditChainComparison> Compare(string path) => _db.InScope(async sp =>
    {
        await using var file = await sp.GetRequiredService<IAuditChainFiles>().OpenAsync(path, CancellationToken.None);
        Assert.True(file.HasChain);
        Assert.False((await AuditChainService.VerifyAsync(file)).Broken);
        return await sp.GetRequiredService<AuditChainService>().CompareAsync(file);
    });

    private Task<int> InsertRecord(long id) => _db.InDb(db =>
    {
        var at = _db.Clock.UtcNow;
        var entity = Guid.NewGuid();
        // EF writes GUIDs as upper-case text on SQLite, and PostgreSQL has a uuid type.
        return LedgerDatabase.OnPostgres
            ? db.Database.ExecuteSqlInterpolatedAsync($"""INSERT INTO "AuditRecords" ("Id", "OccurredAt", "Actor", "Action", "EntityType", "EntityId") VALUES ({id}, {at}, {"test"}, {"Updated"}, {"company"}, {entity})""")
            : db.Database.ExecuteSqlInterpolatedAsync($"""INSERT INTO "AuditRecords" ("Id", "OccurredAt", "Actor", "Action", "EntityType", "EntityId") VALUES ({id}, {at}, {"test"}, {"Updated"}, {"company"}, {entity.ToString().ToUpperInvariant()})""");
    });
}
