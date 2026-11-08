using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenBook.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class SalesLineServiceDatePerson : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Person",
                table: "SalesReceiptLines",
                type: "TEXT",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<DateOnly>(
                name: "ServiceDate",
                table: "SalesReceiptLines",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Person",
                table: "SalesOrderLines",
                type: "TEXT",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<DateOnly>(
                name: "ServiceDate",
                table: "SalesOrderLines",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Person",
                table: "InvoiceLines",
                type: "TEXT",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<DateOnly>(
                name: "ServiceDate",
                table: "InvoiceLines",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Person",
                table: "EstimateLines",
                type: "TEXT",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<DateOnly>(
                name: "ServiceDate",
                table: "EstimateLines",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Person",
                table: "CreditMemoLines",
                type: "TEXT",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<DateOnly>(
                name: "ServiceDate",
                table: "CreditMemoLines",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Person",
                table: "SalesReceiptLines");

            migrationBuilder.DropColumn(
                name: "ServiceDate",
                table: "SalesReceiptLines");

            migrationBuilder.DropColumn(
                name: "Person",
                table: "SalesOrderLines");

            migrationBuilder.DropColumn(
                name: "ServiceDate",
                table: "SalesOrderLines");

            migrationBuilder.DropColumn(
                name: "Person",
                table: "InvoiceLines");

            migrationBuilder.DropColumn(
                name: "ServiceDate",
                table: "InvoiceLines");

            migrationBuilder.DropColumn(
                name: "Person",
                table: "EstimateLines");

            migrationBuilder.DropColumn(
                name: "ServiceDate",
                table: "EstimateLines");

            migrationBuilder.DropColumn(
                name: "Person",
                table: "CreditMemoLines");

            migrationBuilder.DropColumn(
                name: "ServiceDate",
                table: "CreditMemoLines");
        }
    }
}
