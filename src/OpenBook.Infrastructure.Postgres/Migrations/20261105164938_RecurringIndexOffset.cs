using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenBook.Infrastructure.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class RecurringIndexOffset : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "IndexOffset",
                table: "RecurringInvoices",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "IndexOffset",
                table: "RecurringGifts",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "IndexOffset",
                table: "RecurringExpenses",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "IndexOffset",
                table: "RecurringEntries",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "IndexOffset",
                table: "RecurringBills",
                type: "integer",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "IndexOffset",
                table: "RecurringInvoices");

            migrationBuilder.DropColumn(
                name: "IndexOffset",
                table: "RecurringGifts");

            migrationBuilder.DropColumn(
                name: "IndexOffset",
                table: "RecurringExpenses");

            migrationBuilder.DropColumn(
                name: "IndexOffset",
                table: "RecurringEntries");

            migrationBuilder.DropColumn(
                name: "IndexOffset",
                table: "RecurringBills");
        }
    }
}
