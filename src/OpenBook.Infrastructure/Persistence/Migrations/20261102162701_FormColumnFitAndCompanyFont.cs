using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenBook.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class FormColumnFitAndCompanyFont : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CompanyFontKey",
                table: "FormDesigns",
                type: "TEXT",
                maxLength: 80,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Align",
                table: "FormDesignColumns",
                type: "TEXT",
                maxLength: 10,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "AutoWidth",
                table: "FormDesignColumns",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CompanyFontKey",
                table: "FormDesigns");

            migrationBuilder.DropColumn(
                name: "Align",
                table: "FormDesignColumns");

            migrationBuilder.DropColumn(
                name: "AutoWidth",
                table: "FormDesignColumns");
        }
    }
}
