using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenBook.Infrastructure.Persistence.Migrations
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
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "IndexOffset",
                table: "RecurringGifts",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "IndexOffset",
                table: "RecurringExpenses",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "IndexOffset",
                table: "RecurringEntries",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "IndexOffset",
                table: "RecurringBills",
                type: "INTEGER",
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
