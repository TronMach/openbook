using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenBook.Infrastructure.Persistence.Migrations
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
                type: "TEXT",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Group",
                table: "SalesOrderLines",
                type: "TEXT",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Group",
                table: "InvoiceLines",
                type: "TEXT",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Group",
                table: "EstimateLines",
                type: "TEXT",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Group",
                table: "CreditMemoLines",
                type: "TEXT",
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
