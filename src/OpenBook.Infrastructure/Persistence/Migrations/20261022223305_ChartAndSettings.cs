using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenBook.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ChartAndSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Basis",
                table: "JournalEntries",
                type: "TEXT",
                maxLength: 20,
                nullable: false,
                defaultValue: "Both");

            migrationBuilder.AddColumn<string>(
                name: "DefaultReportBasis",
                table: "Companies",
                type: "TEXT",
                maxLength: 20,
                nullable: false,
                defaultValue: "Accrual");

            migrationBuilder.AddColumn<bool>(
                name: "UseAccountNumbers",
                table: "Companies",
                type: "INTEGER",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<string>(
                name: "Description",
                table: "Accounts",
                type: "TEXT",
                maxLength: 500,
                nullable: true);

            // M0's generic types become QuickBooks' specific ones; "current" is the safe guess.
            migrationBuilder.Sql("""UPDATE "Accounts" SET "Type" = 'OtherCurrentAsset' WHERE "Type" = 'Asset';""");
            migrationBuilder.Sql("""UPDATE "Accounts" SET "Type" = 'OtherCurrentLiability' WHERE "Type" = 'Liability';""");

            migrationBuilder.Sql("""DROP TRIGGER "TR_JournalEntries_Immutable";""");
            migrationBuilder.Sql(ImmutableTrigger);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Not migrationBuilder.DropColumn: on SQLite, EF implements it as a table rebuild whose swap runs
            // after every Sql() step here, which fails (and would lose triggers) while triggers reference the
            // table. SQLite's native DROP COLUMN alters in place; it only needs the triggers out of the way.
            DropLedgerTriggers(migrationBuilder);
            migrationBuilder.Sql("""ALTER TABLE "JournalEntries" DROP COLUMN "Basis";""");
            migrationBuilder.Sql("""ALTER TABLE "Companies" DROP COLUMN "DefaultReportBasis";""");
            migrationBuilder.Sql("""ALTER TABLE "Companies" DROP COLUMN "UseAccountNumbers";""");
            migrationBuilder.Sql("""ALTER TABLE "Accounts" DROP COLUMN "Description";""");

            migrationBuilder.Sql("""
                UPDATE "Accounts" SET "Type" = 'Asset'
                WHERE "Type" IN ('Bank', 'AccountsReceivable', 'OtherCurrentAsset', 'FixedAsset', 'OtherAsset');
                """);
            migrationBuilder.Sql("""
                UPDATE "Accounts" SET "Type" = 'Liability'
                WHERE "Type" IN ('AccountsPayable', 'CreditCard', 'OtherCurrentLiability', 'LongTermLiability');
                """);

            foreach (var sql in InitialLedger.Triggers)
                migrationBuilder.Sql(sql);
        }

        // Down puts M0's set back at the end.
        private static void DropLedgerTriggers(MigrationBuilder migrationBuilder)
        {
            foreach (var name in new[]
            {
                "TR_JournalEntries_Seal", "TR_JournalEntries_Immutable", "TR_JournalEntries_NoDelete",
                "TR_JournalLines_NoInsertAfterSeal", "TR_JournalLines_NoUpdate", "TR_JournalLines_NoDelete",
                "TR_AuditRecords_NoUpdate", "TR_AuditRecords_NoDelete",
            })
                migrationBuilder.Sql($"""DROP TRIGGER IF EXISTS "{name}";""");
        }

        // As M0's version, plus "Basis".
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
                OR (NEW."Status" IS NOT OLD."Status" AND (NEW."Status" <> 'Voided' OR OLD."Kind" = 'Reversal')))
            BEGIN
                SELECT RAISE(ABORT, 'ledger: posted journal entries are immutable');
            END;
            """;
    }
}
