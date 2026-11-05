using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenBook.Infrastructure.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class AuditMediumFindings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Invoices_LateFeeForInvoiceId",
                table: "Invoices");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "VoidedAt",
                table: "Loans",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateOnly>(
                name: "LateFeeOn",
                table: "Invoices",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Version",
                table: "BillPayments",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateIndex(
                name: "IX_Invoices_LateFeeForInvoiceId_LateFeeOn",
                table: "Invoices",
                columns: new[] { "LateFeeForInvoiceId", "LateFeeOn" },
                unique: true);

            // Invitation links are kept only in the email itself now; these could still be live.
            migrationBuilder.Sql("""UPDATE "SentEmails" SET "Body" = '(link removed)' WHERE "Kind" = 'Invitation';""");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Invoices_LateFeeForInvoiceId_LateFeeOn",
                table: "Invoices");

            migrationBuilder.DropColumn(
                name: "VoidedAt",
                table: "Loans");

            migrationBuilder.DropColumn(
                name: "LateFeeOn",
                table: "Invoices");

            migrationBuilder.DropColumn(
                name: "Version",
                table: "BillPayments");

            migrationBuilder.CreateIndex(
                name: "IX_Invoices_LateFeeForInvoiceId",
                table: "Invoices",
                column: "LateFeeForInvoiceId");
        }
    }
}
