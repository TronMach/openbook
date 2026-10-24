namespace OpenBook.Infrastructure.Postgres.Migrations;

// The SQLite ledger triggers (see SqliteLedgerTriggers), in PL/pgSQL, as of this migration. Frozen like the
// SQLite ones: to change a trigger, add a migration that replaces its function. Same names, same messages.
public partial class InitialPostgres
{
    internal static readonly string[] Triggers =
    [
        // Sealing an entry is the moment its lines are checked as a whole.
        """
        CREATE FUNCTION "ob_journal_entry_seal"() RETURNS trigger LANGUAGE plpgsql AS $$
        BEGIN
            IF (SELECT COUNT(*) FROM "JournalLines" WHERE "JournalEntryId" = NEW."Id") < 2 THEN
                RAISE EXCEPTION 'ledger: journal entry needs at least two lines';
            END IF;
            IF (SELECT COALESCE(SUM("AmountMinor"), 0) FROM "JournalLines" WHERE "JournalEntryId" = NEW."Id") <> 0 THEN
                RAISE EXCEPTION 'ledger: journal entry does not balance';
            END IF;
            RETURN NEW;
        END $$;
        """,
        """
        CREATE TRIGGER "TR_JournalEntries_Seal" BEFORE UPDATE OF "IsSealed" ON "JournalEntries"
        FOR EACH ROW WHEN (OLD."IsSealed" = false AND NEW."IsSealed" = true)
        EXECUTE FUNCTION "ob_journal_entry_seal"();
        """,

        // Once sealed, the only permitted change is a single Posted -> Voided transition on a standard entry.
        """
        CREATE FUNCTION "ob_journal_entry_immutable"() RETURNS trigger LANGUAGE plpgsql AS $$
        BEGIN
            IF NEW."IsSealed" <> true
                OR NEW."Number" IS DISTINCT FROM OLD."Number"
                OR NEW."Date" IS DISTINCT FROM OLD."Date"
                OR NEW."Currency" IS DISTINCT FROM OLD."Currency"
                OR NEW."Memo" IS DISTINCT FROM OLD."Memo"
                OR NEW."Kind" IS DISTINCT FROM OLD."Kind"
                OR NEW."Basis" IS DISTINCT FROM OLD."Basis"
                OR NEW."Source" IS DISTINCT FROM OLD."Source"
                OR NEW."SourceId" IS DISTINCT FROM OLD."SourceId"
                OR NEW."ReversesEntryId" IS DISTINCT FROM OLD."ReversesEntryId"
                OR NEW."PostedAt" IS DISTINCT FROM OLD."PostedAt"
                OR OLD."Status" = 'Voided'
                OR (NEW."Status" IS DISTINCT FROM OLD."Status" AND (NEW."Status" <> 'Voided' OR OLD."Kind" = 'Reversal')) THEN
                RAISE EXCEPTION 'ledger: posted journal entries are immutable';
            END IF;
            RETURN NEW;
        END $$;
        """,
        """
        CREATE TRIGGER "TR_JournalEntries_Immutable" BEFORE UPDATE ON "JournalEntries"
        FOR EACH ROW WHEN (OLD."IsSealed" = true)
        EXECUTE FUNCTION "ob_journal_entry_immutable"();
        """,

        """
        CREATE FUNCTION "ob_journal_entry_no_delete"() RETURNS trigger LANGUAGE plpgsql AS $$
        BEGIN
            RAISE EXCEPTION 'ledger: posted journal entries cannot be deleted; void them instead';
        END $$;
        """,
        """
        CREATE TRIGGER "TR_JournalEntries_NoDelete" BEFORE DELETE ON "JournalEntries"
        FOR EACH ROW WHEN (OLD."IsSealed" = true)
        EXECUTE FUNCTION "ob_journal_entry_no_delete"();
        """,

        """
        CREATE FUNCTION "ob_journal_line_no_insert_after_seal"() RETURNS trigger LANGUAGE plpgsql AS $$
        BEGIN
            IF (SELECT "IsSealed" FROM "JournalEntries" WHERE "Id" = NEW."JournalEntryId") THEN
                RAISE EXCEPTION 'ledger: cannot add lines to a posted journal entry';
            END IF;
            RETURN NEW;
        END $$;
        """,
        """
        CREATE TRIGGER "TR_JournalLines_NoInsertAfterSeal" BEFORE INSERT ON "JournalLines"
        FOR EACH ROW EXECUTE FUNCTION "ob_journal_line_no_insert_after_seal"();
        """,

        """
        CREATE FUNCTION "ob_journal_line_immutable"() RETURNS trigger LANGUAGE plpgsql AS $$
        BEGIN
            RAISE EXCEPTION 'ledger: journal lines are immutable';
        END $$;
        """,
        """
        CREATE TRIGGER "TR_JournalLines_NoUpdate" BEFORE UPDATE ON "JournalLines"
        FOR EACH ROW EXECUTE FUNCTION "ob_journal_line_immutable"();
        """,

        """
        CREATE FUNCTION "ob_journal_line_no_delete"() RETURNS trigger LANGUAGE plpgsql AS $$
        BEGIN
            RAISE EXCEPTION 'ledger: journal lines cannot be deleted';
        END $$;
        """,
        """
        CREATE TRIGGER "TR_JournalLines_NoDelete" BEFORE DELETE ON "JournalLines"
        FOR EACH ROW EXECUTE FUNCTION "ob_journal_line_no_delete"();
        """,

        """
        CREATE FUNCTION "ob_audit_append_only"() RETURNS trigger LANGUAGE plpgsql AS $$
        BEGIN
            RAISE EXCEPTION 'audit: records are append-only';
        END $$;
        """,
        """
        CREATE TRIGGER "TR_AuditRecords_NoUpdate" BEFORE UPDATE ON "AuditRecords"
        FOR EACH ROW EXECUTE FUNCTION "ob_audit_append_only"();
        """,
        """
        CREATE TRIGGER "TR_AuditRecords_NoDelete" BEFORE DELETE ON "AuditRecords"
        FOR EACH ROW EXECUTE FUNCTION "ob_audit_append_only"();
        """,
        // TRUNCATE skips row triggers, so it's blocked separately.
        """
        CREATE TRIGGER "TR_AuditRecords_NoTruncate" BEFORE TRUNCATE ON "AuditRecords"
        FOR EACH STATEMENT EXECUTE FUNCTION "ob_audit_append_only"();
        """,
    ];

    internal static readonly string[] Functions =
    [
        "ob_journal_entry_seal",
        "ob_journal_entry_immutable",
        "ob_journal_entry_no_delete",
        "ob_journal_line_no_insert_after_seal",
        "ob_journal_line_immutable",
        "ob_journal_line_no_delete",
        "ob_audit_append_only",
    ];
}
