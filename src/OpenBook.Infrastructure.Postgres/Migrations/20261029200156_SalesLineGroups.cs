using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenBook.Infrastructure.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class SalesLineGroups : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Group",
                table: "SalesReceiptLines",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Group",
                table: "SalesOrderLines",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Group",
                table: "InvoiceLines",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Group",
                table: "EstimateLines",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Group",
                table: "CreditMemoLines",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Group",
                table: "SalesReceiptLines");

            migrationBuilder.DropColumn(
                name: "Group",
                table: "SalesOrderLines");

            migrationBuilder.DropColumn(
                name: "Group",
                table: "InvoiceLines");

            migrationBuilder.DropColumn(
                name: "Group",
                table: "EstimateLines");

            migrationBuilder.DropColumn(
                name: "Group",
                table: "CreditMemoLines");
        }
    }
}
