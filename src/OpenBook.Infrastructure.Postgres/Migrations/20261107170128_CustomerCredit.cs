using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenBook.Infrastructure.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class CustomerCredit : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "CreditHold",
                table: "Customers",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<long>(
                name: "CreditLimitMinor",
                table: "Customers",
                type: "bigint",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CreditHold",
                table: "Customers");

            migrationBuilder.DropColumn(
                name: "CreditLimitMinor",
                table: "Customers");
        }
    }
}
