using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenBook.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Closes what the ledger triggers left open: an entry inserted already sealed, sealing with any value but 1, a line
    /// with no entry, and a sealed entry's void details changing by themselves. Account names become unique among
    /// siblings in the database too, so two first uses at once can't each make an account.
    /// </summary>
    public partial class LedgerGaps : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            foreach (var name in new[] { "TR_JournalEntries_Seal", "TR_JournalEntries_Immutable", "TR_JournalLines_NoInsertAfterSeal" })
                migrationBuilder.Sql($"""DROP TRIGGER "{name}";""");
            migrationBuilder.Sql(SealTrigger);
            migrationBuilder.Sql(ImmutableTrigger);
            migrationBuilder.Sql(LineTrigger);
            migrationBuilder.Sql(NoSealedInsertTrigger);

            // Two accounts the race did make keep working: the later one is told apart by a piece of its id.
            migrationBuilder.Sql("""
                UPDATE "Accounts" SET "Name" = substr("Name", 1, 88) || ' (' || substr("Id", 1, 8) || ')'
                WHERE EXISTS (
                    SELECT 1 FROM "Accounts" AS other
                    WHERE lower(other."Name") = lower("Accounts"."Name")
                      AND other."ParentId" IS "Accounts"."ParentId"
                      AND other."Id" < "Accounts"."Id");
                """);
            migrationBuilder.Sql("""CREATE UNIQUE INDEX "IX_Accounts_SiblingName" ON "Accounts" (COALESCE("ParentId", ''), lower("Name"));""");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""DROP INDEX "IX_Accounts_SiblingName";""");
            foreach (var name in new[] { "TR_JournalEntries_Seal", "TR_JournalEntries_Immutable", "TR_JournalLines_NoInsertAfterSeal", "TR_JournalEntries_NoSealedInsert" })
                migrationBuilder.Sql($"""DROP TRIGGER "{name}";""");
            migrationBuilder.Sql("""
                CREATE TRIGGER "TR_JournalEntries_Seal" BEFORE UPDATE OF "IsSealed" ON "JournalEntries"
                WHEN OLD."IsSealed" = 0 AND NEW."IsSealed" = 1
                BEGIN
                    SELECT RAISE(ABORT, 'ledger: journal entry needs at least two lines')
                    WHERE (SELECT COUNT(*) FROM "JournalLines" WHERE "JournalEntryId" = NEW."Id") < 2;
                    SELECT RAISE(ABORT, 'ledger: journal entry does not balance')
                    WHERE (SELECT SUM("AmountMinor") FROM "JournalLines" WHERE "JournalEntryId" = NEW."Id") <> 0;
                END;
                """);
            migrationBuilder.Sql("""
                CREATE TRIGGER "TR_JournalEntries_Immutable" BEFORE UPDATE ON "JournalEntries"
                WHEN OLD."IsSealed" = 1 AND (
                       NEW."IsSealed" <> 1
                    OR NEW."Number" IS NOT OLD."Number"
                    OR NEW."Date" IS NOT OLD."Date"
                    OR NEW."Currency" IS NOT OLD."Currency"
                    OR NEW."Memo" IS NOT OLD."Memo"
                    OR NEW."Kind" IS NOT OLD."Kind"
                    OR NEW."Basis" IS NOT OLD."Basis"
                    OR NEW."Source" IS NOT OLD."Source"
                    OR NEW."SourceId" IS NOT OLD."SourceId"
                    OR NEW."ReversesEntryId" IS NOT OLD."ReversesEntryId"
                    OR NEW."PostedAt" IS NOT OLD."PostedAt"
                    OR OLD."Status" = 'Voided'
                    OR (NEW."Status" IS NOT OLD."Status" AND (NEW."Status" <> 'Voided' OR OLD."Kind" = 'Reversal')))
                BEGIN
                    SELECT RAISE(ABORT, 'ledger: posted journal entries are immutable');
                END;
                """);
            migrationBuilder.Sql("""
                CREATE TRIGGER "TR_JournalLines_NoInsertAfterSeal" BEFORE INSERT ON "JournalLines"
                WHEN (SELECT "IsSealed" FROM "JournalEntries" WHERE "Id" = NEW."JournalEntryId") = 1
                BEGIN
                    SELECT RAISE(ABORT, 'ledger: cannot add lines to a posted journal entry');
                END;
                """);
        }

        // Any change from unsealed is a sealing, and only 1 is one: SQLite would take any number here.
        private const string SealTrigger = """
            CREATE TRIGGER "TR_JournalEntries_Seal" BEFORE UPDATE OF "IsSealed" ON "JournalEntries"
            WHEN OLD."IsSealed" = 0 AND NEW."IsSealed" <> 0
            BEGIN
                SELECT RAISE(ABORT, 'ledger: a journal entry is sealed with 1')
                WHERE NEW."IsSealed" <> 1;
                SELECT RAISE(ABORT, 'ledger: journal entry needs at least two lines')
                WHERE (SELECT COUNT(*) FROM "JournalLines" WHERE "JournalEntryId" = NEW."Id") < 2;
                SELECT RAISE(ABORT, 'ledger: journal entry does not balance')
                WHERE (SELECT SUM("AmountMinor") FROM "JournalLines" WHERE "JournalEntryId" = NEW."Id") <> 0;
            END;
            """;

        // As ChartAndSettings' version, plus the void details: they change only with the Posted -> Voided transition.
        private const string ImmutableTrigger = """
            CREATE TRIGGER "TR_JournalEntries_Immutable" BEFORE UPDATE ON "JournalEntries"
            WHEN OLD."IsSealed" = 1 AND (
                   NEW."IsSealed" <> 1
                OR NEW."Number" IS NOT OLD."Number"
                OR NEW."Date" IS NOT OLD."Date"
                OR NEW."Currency" IS NOT OLD."Currency"
                OR NEW."Memo" IS NOT OLD."Memo"
                OR NEW."Kind" IS NOT OLD."Kind"
                OR NEW."Basis" IS NOT OLD."Basis"
                OR NEW."Source" IS NOT OLD."Source"
                OR NEW."SourceId" IS NOT OLD."SourceId"
                OR NEW."ReversesEntryId" IS NOT OLD."ReversesEntryId"
                OR NEW."PostedAt" IS NOT OLD."PostedAt"
                OR OLD."Status" = 'Voided'
                OR (NEW."Status" IS NOT OLD."Status" AND (NEW."Status" <> 'Voided' OR OLD."Kind" = 'Reversal'))
                OR (NEW."Status" IS OLD."Status" AND (NEW."VoidedAt" IS NOT OLD."VoidedAt" OR NEW."VoidReason" IS NOT OLD."VoidReason")))
            BEGIN
                SELECT RAISE(ABORT, 'ledger: posted journal entries are immutable');
            END;
            """;

        // A line must belong to an unsealed entry: with no entry the subquery is NULL, which the old condition let by.
        private const string LineTrigger = """
            CREATE TRIGGER "TR_JournalLines_NoInsertAfterSeal" BEFORE INSERT ON "JournalLines"
            WHEN COALESCE((SELECT "IsSealed" FROM "JournalEntries" WHERE "Id" = NEW."JournalEntryId"), 1) <> 0
            BEGIN
                SELECT RAISE(ABORT, 'ledger: cannot add lines to a posted journal entry');
            END;
            """;

        // Sealing checks the lines, so an entry can't start out sealed.
        private const string NoSealedInsertTrigger = """
            CREATE TRIGGER "TR_JournalEntries_NoSealedInsert" BEFORE INSERT ON "JournalEntries"
            WHEN NEW."IsSealed" <> 0
            BEGIN
                SELECT RAISE(ABORT, 'ledger: a journal entry is inserted unsealed');
            END;
            """;
    }
}
