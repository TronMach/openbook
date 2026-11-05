using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenBook.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AuditHighFindings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "OwnerCustomerId",
                table: "SalesReceiptLines",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "OwnerCustomerId",
                table: "SalesOrderLines",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "OwnerCustomerId",
                table: "InvoiceLines",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "CashOnReceipt",
                table: "Grants",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<Guid>(
                name: "CashEntryId",
                table: "GrantReceipts",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "OwnerCustomerId",
                table: "EstimateLines",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "OwnerCustomerId",
                table: "CreditMemoLines",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "PositivePaySentAt",
                table: "BillPayments",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "PositivePayVoidSentAt",
                table: "BillPayments",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "PositivePaySentAt",
                table: "BankTransactions",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "PositivePayVoidSentAt",
                table: "BankTransactions",
                type: "TEXT",
                nullable: true);

            // What positive pay had sent by date is now recorded check by check.
            migrationBuilder.Sql("""
                UPDATE "BankTransactions" SET "PositivePaySentAt" = (SELECT s."LastExportedAt" FROM "PositivePaySettings" s WHERE s."AccountId" = "BankTransactions"."AccountId")
                WHERE "Reference" IS NOT NULL AND EXISTS (SELECT 1 FROM "PositivePaySettings" s WHERE s."AccountId" = "BankTransactions"."AccountId" AND s."LastExportedAt" IS NOT NULL AND s."LastExportedThrough" >= "BankTransactions"."Date")
                """);
            migrationBuilder.Sql("""
                UPDATE "BillPayments" SET "PositivePaySentAt" = (SELECT s."LastExportedAt" FROM "PositivePaySettings" s WHERE s."AccountId" = "BillPayments"."PaymentAccountId")
                WHERE "Reference" IS NOT NULL AND "AchBatchId" IS NULL AND EXISTS (SELECT 1 FROM "PositivePaySettings" s WHERE s."AccountId" = "BillPayments"."PaymentAccountId" AND s."LastExportedAt" IS NOT NULL AND s."LastExportedThrough" >= "BillPayments"."Date")
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "OwnerCustomerId",
                table: "SalesReceiptLines");

            migrationBuilder.DropColumn(
                name: "OwnerCustomerId",
                table: "SalesOrderLines");

            migrationBuilder.DropColumn(
                name: "OwnerCustomerId",
                table: "InvoiceLines");

            migrationBuilder.DropColumn(
                name: "CashOnReceipt",
                table: "Grants");

            migrationBuilder.DropColumn(
                name: "CashEntryId",
                table: "GrantReceipts");

            migrationBuilder.DropColumn(
                name: "OwnerCustomerId",
                table: "EstimateLines");

            migrationBuilder.DropColumn(
                name: "OwnerCustomerId",
                table: "CreditMemoLines");

            migrationBuilder.DropColumn(
                name: "PositivePaySentAt",
                table: "BillPayments");

            migrationBuilder.DropColumn(
                name: "PositivePayVoidSentAt",
                table: "BillPayments");

            migrationBuilder.DropColumn(
                name: "PositivePaySentAt",
                table: "BankTransactions");

            migrationBuilder.DropColumn(
                name: "PositivePayVoidSentAt",
                table: "BankTransactions");
        }
    }
}
