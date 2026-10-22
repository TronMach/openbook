namespace OpenBook.Infrastructure.Persistence;

/// <summary>
/// Database-level ledger invariants, so a bug in application code (or someone editing the file
/// with a SQLite browser) cannot unbalance or rewrite the books. Applied by the initial migration.
/// The PostgreSQL provider (V1.8) needs equivalents, ideally a deferred constraint trigger.
/// </summary>
public static class SqliteLedgerTriggers
{
    public static IReadOnlyList<string> Create { get; } =
    [
        // Sealing an entry is the moment its lines are checked as a whole.
        """
        CREATE TRIGGER "TR_JournalEntries_Seal" BEFORE UPDATE OF "IsSealed" ON "JournalEntries"
        WHEN OLD."IsSealed" = 0 AND NEW."IsSealed" = 1
        BEGIN
            SELECT RAISE(ABORT, 'ledger: journal entry needs at least two lines')
            WHERE (SELECT COUNT(*) FROM "JournalLines" WHERE "JournalEntryId" = NEW."Id") < 2;
            SELECT RAISE(ABORT, 'ledger: journal entry does not balance')
            WHERE (SELECT SUM("AmountMinor") FROM "JournalLines" WHERE "JournalEntryId" = NEW."Id") <> 0;
        END;
        """,

        // Once sealed, the only permitted change is a single Posted -> Voided transition on a standard entry.
        """
        CREATE TRIGGER "TR_JournalEntries_Immutable" BEFORE UPDATE ON "JournalEntries"
        WHEN OLD."IsSealed" = 1 AND (
               NEW."IsSealed" <> 1
            OR NEW."Number" IS NOT OLD."Number"
            OR NEW."Date" IS NOT OLD."Date"
            OR NEW."Currency" IS NOT OLD."Currency"
            OR NEW."Memo" IS NOT OLD."Memo"
            OR NEW."Kind" IS NOT OLD."Kind"
            OR NEW."Source" IS NOT OLD."Source"
            OR NEW."SourceId" IS NOT OLD."SourceId"
            OR NEW."ReversesEntryId" IS NOT OLD."ReversesEntryId"
            OR NEW."PostedAt" IS NOT OLD."PostedAt"
            OR OLD."Status" = 'Voided'
            OR (NEW."Status" IS NOT OLD."Status" AND (NEW."Status" <> 'Voided' OR OLD."Kind" = 'Reversal')))
        BEGIN
            SELECT RAISE(ABORT, 'ledger: posted journal entries are immutable');
        END;
        """,

        """
        CREATE TRIGGER "TR_JournalEntries_NoDelete" BEFORE DELETE ON "JournalEntries"
        WHEN OLD."IsSealed" = 1
        BEGIN
            SELECT RAISE(ABORT, 'ledger: posted journal entries cannot be deleted; void them instead');
        END;
        """,

        """
        CREATE TRIGGER "TR_JournalLines_NoInsertAfterSeal" BEFORE INSERT ON "JournalLines"
        WHEN (SELECT "IsSealed" FROM "JournalEntries" WHERE "Id" = NEW."JournalEntryId") = 1
        BEGIN
            SELECT RAISE(ABORT, 'ledger: cannot add lines to a posted journal entry');
        END;
        """,

        """
        CREATE TRIGGER "TR_JournalLines_NoUpdate" BEFORE UPDATE ON "JournalLines"
        BEGIN
            SELECT RAISE(ABORT, 'ledger: journal lines are immutable');
        END;
        """,

        """
        CREATE TRIGGER "TR_JournalLines_NoDelete" BEFORE DELETE ON "JournalLines"
        BEGIN
            SELECT RAISE(ABORT, 'ledger: journal lines cannot be deleted');
        END;
        """,

        """
        CREATE TRIGGER "TR_AuditRecords_NoUpdate" BEFORE UPDATE ON "AuditRecords"
        BEGIN
            SELECT RAISE(ABORT, 'audit: records are append-only');
        END;
        """,

        """
        CREATE TRIGGER "TR_AuditRecords_NoDelete" BEFORE DELETE ON "AuditRecords"
        BEGIN
            SELECT RAISE(ABORT, 'audit: records are append-only');
        END;
        """,
    ];

    /// <summary>Every trigger above; restore refuses a backup that is missing any of them.</summary>
    public static IReadOnlyList<string> Names { get; } =
    [
        "TR_JournalEntries_Seal",
        "TR_JournalEntries_Immutable",
        "TR_JournalEntries_NoDelete",
        "TR_JournalLines_NoInsertAfterSeal",
        "TR_JournalLines_NoUpdate",
        "TR_JournalLines_NoDelete",
        "TR_AuditRecords_NoUpdate",
        "TR_AuditRecords_NoDelete",
    ];

    public static IReadOnlyList<string> Drop { get; } = Names.Reverse().Select(n => $"""DROP TRIGGER IF EXISTS "{n}";""").ToList();
}
