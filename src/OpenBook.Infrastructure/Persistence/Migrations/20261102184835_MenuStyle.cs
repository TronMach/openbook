using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenBook.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class MenuStyle : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "Mode",
                table: "ViewPreferences",
                type: "TEXT",
                maxLength: 20,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "TEXT",
                oldMaxLength: 20);

            migrationBuilder.AddColumn<string>(
                name: "Menu",
                table: "ViewPreferences",
                type: "TEXT",
                maxLength: 20,
                nullable: false,
                defaultValue: "Bar");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Rows that only held a menu style have no view to keep.
            migrationBuilder.Sql("DELETE FROM \"ViewPreferences\" WHERE \"Mode\" IS NULL;");

            migrationBuilder.DropColumn(
                name: "Menu",
                table: "ViewPreferences");

            migrationBuilder.AlterColumn<string>(
                name: "Mode",
                table: "ViewPreferences",
                type: "TEXT",
                maxLength: 20,
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "TEXT",
                oldMaxLength: 20,
                oldNullable: true);
        }
    }
}
