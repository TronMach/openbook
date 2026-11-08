using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenBook.Infrastructure.Postgres.Migrations
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
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<DateOnly>(
                name: "ServiceDate",
                table: "SalesReceiptLines",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Person",
                table: "SalesOrderLines",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<DateOnly>(
                name: "ServiceDate",
                table: "SalesOrderLines",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Person",
                table: "InvoiceLines",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<DateOnly>(
                name: "ServiceDate",
                table: "InvoiceLines",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Person",
                table: "EstimateLines",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<DateOnly>(
                name: "ServiceDate",
                table: "EstimateLines",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Person",
                table: "CreditMemoLines",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<DateOnly>(
                name: "ServiceDate",
                table: "CreditMemoLines",
                type: "date",
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
