using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenBook.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class PaymentMethods : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Method",
                table: "SalesReceipts",
                type: "TEXT",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Method",
                table: "CustomerPayments",
                type: "TEXT",
                maxLength: 20,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Method",
                table: "SalesReceipts");

            migrationBuilder.DropColumn(
                name: "Method",
                table: "CustomerPayments");
        }
    }
}
