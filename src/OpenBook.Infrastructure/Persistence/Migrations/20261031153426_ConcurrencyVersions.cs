using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenBook.Infrastructure.Persistence.Migrations
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
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "Version",
                table: "ScheduledEmails",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "Version",
                table: "SalesOrders",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "Version",
                table: "Retainers",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "Version",
                table: "RecurringInvoices",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "Version",
                table: "RecurringGifts",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "Version",
                table: "RecurringEntries",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "Version",
                table: "RecurringBills",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "Version",
                table: "PurchaseOrders",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "Version",
                table: "Pledges",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "Version",
                table: "PaymentLinks",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "Version",
                table: "Invoices",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "Version",
                table: "Grants",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "Version",
                table: "Estimates",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "Version",
                table: "CustomerPayments",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "Version",
                table: "CreditMemos",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "Version",
                table: "ChannelPayouts",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "Version",
                table: "ChannelOrderBatches",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "Version",
                table: "Bills",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "Version",
                table: "BankFeedItems",
                type: "INTEGER",
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
