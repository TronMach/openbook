namespace OpenBook.Infrastructure.Persistence;

/// <summary>
/// Database-level ledger invariants, so a bug in application code (or someone editing the file
/// with a SQLite browser) cannot unbalance or rewrite the books:
/// <list type="bullet">
/// <item>an entry starts unsealed, and sealing it checks it has two or more lines that sum to zero;</item>
/// <item>sealed entries can't change, except one Posted → Voided transition on a standard entry;</item>
/// <item>lines can only be added to an unsealed entry, and can't be changed or deleted at all;</item>
/// <item>audit records are append-only.</item>
/// </list>
/// Someone who can edit the file can drop the triggers too. Restore refuses a file that has lost one, by name; it
/// doesn't compare what they say. The SQL lives in the migrations that create or replace each trigger (InitialLedger,
/// ChartAndSettings, LedgerGaps), frozen there so that older migrations never change. To change a trigger, add a migration that drops
/// and recreates it. Beware EF Core's SQLite table rebuilds (column type changes, drops): a rebuild of
/// JournalEntries, JournalLines or AuditRecords silently loses its triggers. MigrationTests catch that.
/// The PostgreSQL equivalents live in OpenBook.Infrastructure.Postgres (InitialPostgres.Triggers.cs), with the same names.
/// </summary>
public static class SqliteLedgerTriggers
{
    /// <summary>Every trigger the current schema must have; restore refuses a backup that is missing one.</summary>
    public static IReadOnlyList<string> Names { get; } =
    [
        "TR_JournalEntries_Seal",
        "TR_JournalEntries_NoSealedInsert",
        "TR_JournalEntries_Immutable",
        "TR_JournalEntries_NoDelete",
        "TR_JournalLines_NoInsertAfterSeal",
        "TR_JournalLines_NoUpdate",
        "TR_JournalLines_NoDelete",
        "TR_AuditRecords_NoUpdate",
        "TR_AuditRecords_NoDelete",
    ];

    /// <summary>The migration that added a trigger, when it's not there from the first: a file from before it hasn't got one yet, and gets it on migrating.</summary>
    public static IReadOnlyDictionary<string, string> AddedBy { get; } = new Dictionary<string, string>
    {
        ["TR_JournalEntries_NoSealedInsert"] = "20261106213648_LedgerGaps",
    };
}
