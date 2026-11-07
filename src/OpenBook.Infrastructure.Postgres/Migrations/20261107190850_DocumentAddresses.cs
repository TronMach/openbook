using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenBook.Infrastructure.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class DocumentAddresses : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "BillToAddress",
                table: "SalesReceipts",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ShipToAddress",
                table: "SalesReceipts",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "BillToAddress",
                table: "Invoices",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ShipToAddress",
                table: "Invoices",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "BillToAddress",
                table: "Estimates",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ShipToAddress",
                table: "Estimates",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "BillToAddress",
                table: "CreditMemos",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ShipToAddress",
                table: "CreditMemos",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "BillToAddress",
                table: "SalesReceipts");

            migrationBuilder.DropColumn(
                name: "ShipToAddress",
                table: "SalesReceipts");

            migrationBuilder.DropColumn(
                name: "BillToAddress",
                table: "Invoices");

            migrationBuilder.DropColumn(
                name: "ShipToAddress",
                table: "Invoices");

            migrationBuilder.DropColumn(
                name: "BillToAddress",
                table: "Estimates");

            migrationBuilder.DropColumn(
                name: "ShipToAddress",
                table: "Estimates");

            migrationBuilder.DropColumn(
                name: "BillToAddress",
                table: "CreditMemos");

            migrationBuilder.DropColumn(
                name: "ShipToAddress",
                table: "CreditMemos");
        }
    }
}
