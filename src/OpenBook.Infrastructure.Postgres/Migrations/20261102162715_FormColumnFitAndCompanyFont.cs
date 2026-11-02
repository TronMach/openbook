using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenBook.Infrastructure.Postgres.Migrations
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
                type: "character varying(80)",
                maxLength: 80,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Align",
                table: "FormDesignColumns",
                type: "character varying(10)",
                maxLength: 10,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "AutoWidth",
                table: "FormDesignColumns",
                type: "boolean",
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
