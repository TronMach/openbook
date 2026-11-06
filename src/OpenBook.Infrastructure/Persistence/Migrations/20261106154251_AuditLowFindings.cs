using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenBook.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AuditLowFindings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Pdf",
                table: "ScheduledEmails",
                type: "TEXT",
                maxLength: 10,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Version",
                table: "Reconciliations",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<long>(
                name: "WrittenOffMinor",
                table: "Invoices",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<Guid>(
                name: "DesignId",
                table: "DocumentLinks",
                type: "TEXT",
                nullable: true);

            // What standing write-offs have settled, for lists to say "Written off" rather than "Paid".
            migrationBuilder.Sql("""
                UPDATE "Invoices" SET "WrittenOffMinor" = COALESCE((SELECT SUM(w."AmountMinor") FROM "BadDebtWriteOffs" w WHERE w."InvoiceId" = "Invoices"."Id" AND w."Status" = 'Posted'), 0);
                """);

            // Only one can be in progress from here on: two started at the same moment keep the first.
            migrationBuilder.Sql("""
                DELETE FROM "Reconciliations" WHERE "Status" = 'InProgress' AND EXISTS (
                    SELECT 1 FROM "Reconciliations" o WHERE o."AccountId" = "Reconciliations"."AccountId" AND o."Status" = 'InProgress' AND o."Id" < "Reconciliations"."Id");
                """);

            migrationBuilder.CreateIndex(
                name: "IX_Reconciliations_OneInProgress",
                table: "Reconciliations",
                column: "AccountId",
                unique: true,
                filter: "\"Status\" = 'InProgress'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Reconciliations_OneInProgress",
                table: "Reconciliations");

            migrationBuilder.DropColumn(
                name: "Pdf",
                table: "ScheduledEmails");

            migrationBuilder.DropColumn(
                name: "Version",
                table: "Reconciliations");

            migrationBuilder.DropColumn(
                name: "WrittenOffMinor",
                table: "Invoices");

            migrationBuilder.DropColumn(
                name: "DesignId",
                table: "DocumentLinks");
        }
    }
}
