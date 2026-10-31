using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenBook.Infrastructure.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class ConcurrencyVersions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "Version",
                table: "VendorCredits",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "Version",
                table: "ScheduledEmails",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "Version",
                table: "SalesOrders",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "Version",
                table: "Retainers",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "Version",
                table: "RecurringInvoices",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "Version",
                table: "RecurringGifts",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "Version",
                table: "RecurringEntries",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "Version",
                table: "RecurringBills",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "Version",
                table: "PurchaseOrders",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "Version",
                table: "Pledges",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "Version",
                table: "PaymentLinks",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "Version",
                table: "Invoices",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "Version",
                table: "Grants",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "Version",
                table: "Estimates",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "Version",
                table: "CustomerPayments",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "Version",
                table: "CreditMemos",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "Version",
                table: "ChannelPayouts",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "Version",
                table: "ChannelOrderBatches",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "Version",
                table: "Bills",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "Version",
                table: "BankFeedItems",
                type: "integer",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Version",
                table: "VendorCredits");

            migrationBuilder.DropColumn(
                name: "Version",
                table: "ScheduledEmails");

            migrationBuilder.DropColumn(
                name: "Version",
                table: "SalesOrders");

            migrationBuilder.DropColumn(
                name: "Version",
                table: "Retainers");

            migrationBuilder.DropColumn(
                name: "Version",
                table: "RecurringInvoices");

            migrationBuilder.DropColumn(
                name: "Version",
                table: "RecurringGifts");

            migrationBuilder.DropColumn(
                name: "Version",
                table: "RecurringEntries");

            migrationBuilder.DropColumn(
                name: "Version",
                table: "RecurringBills");

            migrationBuilder.DropColumn(
                name: "Version",
                table: "PurchaseOrders");

            migrationBuilder.DropColumn(
                name: "Version",
                table: "Pledges");

            migrationBuilder.DropColumn(
                name: "Version",
                table: "PaymentLinks");

            migrationBuilder.DropColumn(
                name: "Version",
                table: "Invoices");

            migrationBuilder.DropColumn(
                name: "Version",
                table: "Grants");

            migrationBuilder.DropColumn(
                name: "Version",
                table: "Estimates");

            migrationBuilder.DropColumn(
                name: "Version",
                table: "CustomerPayments");

            migrationBuilder.DropColumn(
                name: "Version",
                table: "CreditMemos");

            migrationBuilder.DropColumn(
                name: "Version",
                table: "ChannelPayouts");

            migrationBuilder.DropColumn(
                name: "Version",
                table: "ChannelOrderBatches");

            migrationBuilder.DropColumn(
                name: "Version",
                table: "Bills");

            migrationBuilder.DropColumn(
                name: "Version",
                table: "BankFeedItems");
        }
    }
}
