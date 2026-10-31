using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenBook.Infrastructure.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class FormDesigns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "FormDesignId",
                table: "Customers",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "FormDesigns",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    IsDefault = table.Column<bool>(type: "boolean", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    DefaultFor = table.Column<int>(type: "integer", nullable: false),
                    Template = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    AccentColor = table.Column<string>(type: "character varying(7)", maxLength: 7, nullable: false),
                    Footer = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    FontKey = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true),
                    PaperSize = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    Logo = table.Column<byte[]>(type: "bytea", nullable: true),
                    LogoContentType = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    LogoPosition = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    LogoSize = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    ShowShipTo = table.Column<bool>(type: "boolean", nullable: false),
                    ShowTerms = table.Column<bool>(type: "boolean", nullable: false),
                    ShowQuantityAndRate = table.Column<bool>(type: "boolean", nullable: false),
                    DescriptionLabel = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    QuantityLabel = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    RateLabel = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    AmountLabel = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FormDesigns", x => x.Id);
                });

            // The company's one style becomes the default design ("Standard"), before its columns go.
            migrationBuilder.Sql("""
                INSERT INTO "FormDesigns" ("Id", "Name", "IsDefault", "IsActive", "DefaultFor", "Template", "AccentColor", "Footer", "PaperSize",
                    "LogoPosition", "LogoSize", "ShowShipTo", "ShowTerms", "ShowQuantityAndRate", "CreatedAt")
                SELECT '01929a30-0000-7000-8000-00000000f0a1', 'Standard', TRUE, TRUE, 0, "FormTemplate", "FormAccentColor", "FormFooter", 'Letter',
                    'Left', 'Medium', TRUE, TRUE, TRUE, "CreatedAt"
                FROM "Companies";
                """);

            migrationBuilder.DropColumn(
                name: "FormAccentColor",
                table: "Companies");

            migrationBuilder.DropColumn(
                name: "FormFooter",
                table: "Companies");

            migrationBuilder.DropColumn(
                name: "FormTemplate",
                table: "Companies");

            migrationBuilder.CreateTable(
                name: "FormDesignTexts",
                columns: table => new
                {
                    Document = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    FormDesignId = table.Column<Guid>(type: "uuid", nullable: false),
                    Title = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    Message = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FormDesignTexts", x => new { x.FormDesignId, x.Document });
                    table.ForeignKey(
                        name: "FK_FormDesignTexts_FormDesigns_FormDesignId",
                        column: x => x.FormDesignId,
                        principalTable: "FormDesigns",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Customers_FormDesignId",
                table: "Customers",
                column: "FormDesignId");

            migrationBuilder.CreateIndex(
                name: "IX_FormDesigns_Name",
                table: "FormDesigns",
                column: "Name",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_Customers_FormDesigns_FormDesignId",
                table: "Customers",
                column: "FormDesignId",
                principalTable: "FormDesigns",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Customers_FormDesigns_FormDesignId",
                table: "Customers");

            migrationBuilder.DropTable(
                name: "FormDesignTexts");

            migrationBuilder.DropTable(
                name: "FormDesigns");

            migrationBuilder.DropIndex(
                name: "IX_Customers_FormDesignId",
                table: "Customers");

            migrationBuilder.DropColumn(
                name: "FormDesignId",
                table: "Customers");

            migrationBuilder.AddColumn<string>(
                name: "FormAccentColor",
                table: "Companies",
                type: "character varying(7)",
                maxLength: 7,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "FormFooter",
                table: "Companies",
                type: "character varying(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "FormTemplate",
                table: "Companies",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "");
        }
    }
}
