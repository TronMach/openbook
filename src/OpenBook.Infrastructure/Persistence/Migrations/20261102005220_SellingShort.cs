using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenBook.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class SellingShort : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "SellShort",
                table: "Items",
                type: "TEXT",
                maxLength: 20,
                nullable: false,
                defaultValue: "Company");

            migrationBuilder.AddColumn<bool>(
                name: "AllowSellingShort",
                table: "Companies",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SellShort",
                table: "Items");

            migrationBuilder.DropColumn(
                name: "AllowSellingShort",
                table: "Companies");
        }
    }
}
