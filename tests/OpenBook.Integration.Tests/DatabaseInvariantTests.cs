using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OpenBook.Application.Ledger;
using OpenBook.Domain.Accounts;
using OpenBook.Domain.Ledger;
using static OpenBook.Domain.Ledger.JournalLineDraft;

namespace OpenBook.Integration.Tests;

/// <summary>
/// The triggers must hold even when application code is bypassed, e.g. raw SQL or a
/// SQLite browser pointed at the company file.
/// </summary>
public sealed class DatabaseInvariantTests : IAsyncLifetime
{
    private readonly LedgerDatabase _db = new();
    private Account _cash = null!;
    private Account _sales = null!;
    private JournalEntry _entry = null!;

    public async Task InitializeAsync()
    {
        await _db.InitializeAsync();
        _cash = await _db.CreateAccount("Checking", AccountType.Bank);
        _sales = await _db.CreateAccount("Sales", AccountType.Income);
        _entry = await _db.InScope(sp => sp.GetRequiredService<PostingEngine>()
            .PostAsync(new PostJournalEntryCommand(new DateOnly(2026, 1, 15), [Dr(_cash.Id, 10m), Cr(_sales.Id, 10m)])));
    }

    public Task DisposeAsync() => _db.DisposeAsync();

    private static string Id(Guid id) => id.ToString().ToUpperInvariant();

    // Any provider's error (SqliteException, PostgresException): the message says which safeguard fired.
    private async Task<string> Rejected(string sql)
    {
        var ex = await Assert.ThrowsAnyAsync<System.Data.Common.DbException>(() => _db.InDb(db => db.Database.ExecuteSqlRawAsync(sql)));
        return ex.Message;
    }

    [Fact]
    public async Task Unbalanced_entry_cannot_be_sealed()
    {
        var entryId = Id(Guid.NewGuid());
        var message = await Rejected($"""
            INSERT INTO "JournalEntries" ("Id","Number","Date","Currency","Kind","Status","Source","PostedAt","IsSealed","Basis")
                VALUES ('{entryId}', 99, '2026-01-20', 'USD', 'Standard', 'Posted', 'manual', '2026-01-20 00:00:00+00:00', FALSE, 'Both');
            INSERT INTO "JournalLines" ("Id","LineNumber","AccountId","AmountMinor","JournalEntryId")
                VALUES ('{Id(Guid.NewGuid())}', 1, '{Id(_cash.Id)}', 1000, '{entryId}'),
                       ('{Id(Guid.NewGuid())}', 2, '{Id(_sales.Id)}', -999, '{entryId}');
            UPDATE "JournalEntries" SET "IsSealed" = TRUE WHERE "Id" = '{entryId}';
            """);

        Assert.Contains("does not balance", message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Single_line_entry_cannot_be_sealed()
    {
        var entryId = Id(Guid.NewGuid());
        var message = await Rejected($"""
            INSERT INTO "JournalEntries" ("Id","Number","Date","Currency","Kind","Status","Source","PostedAt","IsSealed","Basis")
                VALUES ('{entryId}', 99, '2026-01-20', 'USD', 'Standard', 'Posted', 'manual', '2026-01-20 00:00:00+00:00', FALSE, 'Both');
            UPDATE "JournalEntries" SET "IsSealed" = TRUE WHERE "Id" = '{entryId}';
            """);

        Assert.Contains("at least two lines", message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_entry_cannot_be_inserted_already_sealed()
    {
        var message = await Rejected($"""
            INSERT INTO "JournalEntries" ("Id","Number","Date","Currency","Kind","Status","Source","PostedAt","IsSealed","Basis")
                VALUES ('{Id(Guid.NewGuid())}', 99, '2026-01-20', 'USD', 'Standard', 'Posted', 'manual', '2026-01-20 00:00:00+00:00', TRUE, 'Both');
            """);

        Assert.Contains("inserted unsealed", message, StringComparison.Ordinal);
    }

    [SqliteFact]
    public async Task Only_one_value_seals_an_entry()
    {
        var entryId = Id(Guid.NewGuid());
        var message = await Rejected($"""
            INSERT INTO "JournalEntries" ("Id","Number","Date","Currency","Kind","Status","Source","PostedAt","IsSealed","Basis")
                VALUES ('{entryId}', 99, '2026-01-20', 'USD', 'Standard', 'Posted', 'manual', '2026-01-20 00:00:00+00:00', 0, 'Both');
            UPDATE "JournalEntries" SET "IsSealed" = 2 WHERE "Id" = '{entryId}';
            """);

        Assert.Contains("sealed with 1", message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_line_needs_an_entry() =>
        Assert.NotEmpty(await Rejected($"""
            INSERT INTO "JournalLines" ("Id","LineNumber","AccountId","AmountMinor","JournalEntryId")
                VALUES ('{Id(Guid.NewGuid())}', 1, '{Id(_cash.Id)}', 500, NULL);
            """));

    [Fact]
    public async Task A_posted_entry_can_t_take_void_details_without_being_voided() =>
        Assert.Contains("immutable", await Rejected($"""UPDATE "JournalEntries" SET "VoidReason" = 'Edited by hand' WHERE "Id" = '{Id(_entry.Id)}';"""), StringComparison.Ordinal);

    [Fact]
    public async Task Two_accounts_can_t_share_a_name_under_one_parent_whatever_the_case()
    {
        // As two first uses at once: each read the chart before the other saved, so neither was stopped by the rule in memory.
        var thrown = await Assert.ThrowsAsync<OpenBook.Domain.Common.ConcurrencyConflictException>(() => _db.InDb(async db =>
        {
            db.Accounts.Add(Account.Create("CHECKING", AccountType.Bank, null, null));
            return await db.SaveChangesAsync();
        }));
        Assert.Equal("save.conflict", thrown.Code);

        // Under another parent the name is free.
        var sub = await _db.InDb(async db =>
        {
            db.Accounts.Add(Account.Create("Checking", AccountType.Income, null, _sales.Id));
            return await db.SaveChangesAsync();
        });
        Assert.Equal(1, sub);
    }

    [Fact]
    public async Task Posted_line_amounts_cannot_change() =>
        Assert.Contains("immutable", await Rejected("""UPDATE "JournalLines" SET "AmountMinor" = "AmountMinor" * 2;"""), StringComparison.Ordinal);

    [Fact]
    public async Task Posted_lines_cannot_be_deleted() =>
        Assert.Contains("cannot be deleted", await Rejected("""DELETE FROM "JournalLines";"""), StringComparison.Ordinal);

    [Fact]
    public async Task Lines_cannot_be_added_to_posted_entry() =>
        Assert.Contains("cannot add lines", await Rejected($"""
            INSERT INTO "JournalLines" ("Id","LineNumber","AccountId","AmountMinor","JournalEntryId")
                VALUES ('{Id(Guid.NewGuid())}', 3, '{Id(_cash.Id)}', 500, '{Id(_entry.Id)}');
            """), StringComparison.Ordinal);

    [Fact]
    public async Task Posted_entry_date_cannot_change() =>
        Assert.Contains("immutable", await Rejected("""UPDATE "JournalEntries" SET "Date" = '2025-12-31';"""), StringComparison.Ordinal);

    [Fact]
    public async Task Posted_entry_cannot_be_deleted() =>
        Assert.Contains("void them instead", await Rejected("""DELETE FROM "JournalEntries";"""), StringComparison.Ordinal);

    [Fact]
    public async Task Voided_entry_cannot_be_reinstated()
    {
        await _db.InScope(sp => sp.GetRequiredService<PostingEngine>().VoidAsync(_entry.Id, "test"));

        Assert.Contains("immutable", await Rejected($"""UPDATE "JournalEntries" SET "Status" = 'Posted' WHERE "Id" = '{Id(_entry.Id)}';"""), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Zero_amount_line_violates_check_constraint() =>
        Assert.Contains("check", await Rejected($"""
            INSERT INTO "JournalEntries" ("Id","Number","Date","Currency","Kind","Status","Source","PostedAt","IsSealed","Basis")
                VALUES ('{Id(Guid.NewGuid())}', 98, '2026-01-20', 'USD', 'Standard', 'Posted', 'manual', '2026-01-20 00:00:00+00:00', FALSE, 'Both');
            INSERT INTO "JournalLines" ("Id","LineNumber","AccountId","AmountMinor","JournalEntryId")
                VALUES ('{Id(Guid.NewGuid())}', 1, '{Id(_cash.Id)}', 0, (SELECT "Id" FROM "JournalEntries" WHERE "Number" = 98));
            """), StringComparison.OrdinalIgnoreCase);

    [Fact]
    public async Task Audit_records_cannot_be_edited_or_deleted()
    {
        Assert.Contains("append-only", await Rejected("""UPDATE "AuditRecords" SET "Actor" = 'someone-else';"""), StringComparison.Ordinal);
        Assert.Contains("append-only", await Rejected("""DELETE FROM "AuditRecords";"""), StringComparison.Ordinal);
    }
}
