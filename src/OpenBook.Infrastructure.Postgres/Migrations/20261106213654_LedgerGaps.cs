using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenBook.Infrastructure.Postgres.Migrations
{
    /// <summary>
    /// The SQLite migration of the same name, in PL/pgSQL, plus a TRUNCATE guard on the journal tables as the audit log
    /// has. <c>IsSealed</c> is a boolean here, so no other value can seal an entry.
    /// </summary>
    public partial class LedgerGaps : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                CREATE OR REPLACE FUNCTION "ob_journal_entry_immutable"() RETURNS trigger LANGUAGE plpgsql AS $$
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
                        OR (NEW."Status" IS DISTINCT FROM OLD."Status" AND (NEW."Status" <> 'Voided' OR OLD."Kind" = 'Reversal'))
                        OR (NEW."Status" IS NOT DISTINCT FROM OLD."Status"
                            AND (NEW."VoidedAt" IS DISTINCT FROM OLD."VoidedAt" OR NEW."VoidReason" IS DISTINCT FROM OLD."VoidReason")) THEN
                        RAISE EXCEPTION 'ledger: posted journal entries are immutable';
                    END IF;
                    RETURN NEW;
                END $$;
                """);
            migrationBuilder.Sql("""
                CREATE OR REPLACE FUNCTION "ob_journal_line_no_insert_after_seal"() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                    IF COALESCE((SELECT "IsSealed" FROM "JournalEntries" WHERE "Id" = NEW."JournalEntryId"), true) THEN
                        RAISE EXCEPTION 'ledger: cannot add lines to a posted journal entry';
                    END IF;
                    RETURN NEW;
                END $$;
                """);
            migrationBuilder.Sql("""
                CREATE FUNCTION "ob_journal_entry_no_sealed_insert"() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                    RAISE EXCEPTION 'ledger: a journal entry is inserted unsealed';
                END $$;
                """);
            migrationBuilder.Sql("""
                CREATE TRIGGER "TR_JournalEntries_NoSealedInsert" BEFORE INSERT ON "JournalEntries"
                FOR EACH ROW WHEN (NEW."IsSealed")
                EXECUTE FUNCTION "ob_journal_entry_no_sealed_insert"();
                """);
            migrationBuilder.Sql("""
                CREATE FUNCTION "ob_journal_no_truncate"() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                    RAISE EXCEPTION 'ledger: journal tables cannot be emptied';
                END $$;
                """);
            migrationBuilder.Sql("""
                CREATE TRIGGER "TR_JournalEntries_NoTruncate" BEFORE TRUNCATE ON "JournalEntries"
                FOR EACH STATEMENT EXECUTE FUNCTION "ob_journal_no_truncate"();
                """);
            migrationBuilder.Sql("""
                CREATE TRIGGER "TR_JournalLines_NoTruncate" BEFORE TRUNCATE ON "JournalLines"
                FOR EACH STATEMENT EXECUTE FUNCTION "ob_journal_no_truncate"();
                """);

            // Two accounts the race did make keep working: the later one is told apart by a piece of its id.
            migrationBuilder.Sql("""
                UPDATE "Accounts" AS a SET "Name" = substr(a."Name", 1, 88) || ' (' || substr(a."Id"::text, 1, 8) || ')'
                WHERE EXISTS (
                    SELECT 1 FROM "Accounts" AS other
                    WHERE lower(other."Name") = lower(a."Name")
                      AND other."ParentId" IS NOT DISTINCT FROM a."ParentId"
                      AND other."Id" < a."Id");
                """);
            migrationBuilder.Sql("""CREATE UNIQUE INDEX "IX_Accounts_SiblingName" ON "Accounts" ((COALESCE("ParentId"::text, '')), (lower("Name")));""");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""DROP INDEX "IX_Accounts_SiblingName";""");
            migrationBuilder.Sql("""DROP TRIGGER "TR_JournalLines_NoTruncate" ON "JournalLines";""");
            migrationBuilder.Sql("""DROP TRIGGER "TR_JournalEntries_NoTruncate" ON "JournalEntries";""");
            migrationBuilder.Sql("""DROP FUNCTION "ob_journal_no_truncate"();""");
            migrationBuilder.Sql("""DROP TRIGGER "TR_JournalEntries_NoSealedInsert" ON "JournalEntries";""");
            migrationBuilder.Sql("""DROP FUNCTION "ob_journal_entry_no_sealed_insert"();""");
            migrationBuilder.Sql("""
                CREATE OR REPLACE FUNCTION "ob_journal_line_no_insert_after_seal"() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                    IF (SELECT "IsSealed" FROM "JournalEntries" WHERE "Id" = NEW."JournalEntryId") THEN
                        RAISE EXCEPTION 'ledger: cannot add lines to a posted journal entry';
                    END IF;
                    RETURN NEW;
                END $$;
                """);
            migrationBuilder.Sql("""
                CREATE OR REPLACE FUNCTION "ob_journal_entry_immutable"() RETURNS trigger LANGUAGE plpgsql AS $$
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
                """);
        }
    }
}
