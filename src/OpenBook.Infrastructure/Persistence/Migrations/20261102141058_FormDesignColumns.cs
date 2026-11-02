using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenBook.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Designs choose their line columns (FormDesignColumns), what prints (Show, a FormParts number) and their layout.
    /// Existing designs keep what they printed: description, quantity, rate and amount (or description and amount) with their
    /// headings, and ship-to and terms as they were.
    /// </summary>
    public partial class FormDesignColumns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // FormParts.Default: everything but the acceptance lines and striped rows.
            migrationBuilder.AddColumn<int>(name: "Show", table: "FormDesigns", type: "INTEGER", nullable: false, defaultValue: 511);
            migrationBuilder.AddColumn<bool>(name: "AutoFieldColumns", table: "FormDesigns", type: "INTEGER", nullable: false, defaultValue: true);
            migrationBuilder.AddColumn<bool>(name: "Landscape", table: "FormDesigns", type: "INTEGER", nullable: false, defaultValue: false);
            migrationBuilder.AddColumn<string>(name: "Margins", table: "FormDesigns", type: "TEXT", maxLength: 10, nullable: false, defaultValue: "Normal");
            migrationBuilder.AddColumn<string>(name: "TextSize", table: "FormDesigns", type: "TEXT", maxLength: 10, nullable: false, defaultValue: "Medium");

            migrationBuilder.CreateTable(
                name: "FormDesignColumns",
                columns: table => new
                {
                    Key = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    FormDesignId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Label = table.Column<string>(type: "TEXT", maxLength: 30, nullable: true),
                    Width = table.Column<int>(type: "INTEGER", nullable: false),
                    Position = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FormDesignColumns", x => new { x.FormDesignId, x.Key });
                    table.ForeignKey(
                        name: "FK_FormDesignColumns_FormDesigns_FormDesignId",
                        column: x => x.FormDesignId,
                        principalTable: "FormDesigns",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.Sql("""
                UPDATE "FormDesigns" SET "Show" = "Show" - 16 WHERE "ShowShipTo" = FALSE;
                UPDATE "FormDesigns" SET "Show" = "Show" - 32 WHERE "ShowTerms" = FALSE;
                INSERT INTO "FormDesignColumns" ("FormDesignId", "Key", "Label", "Width", "Position")
                    SELECT "Id", 'description', "DescriptionLabel", CASE WHEN "ShowQuantityAndRate" = TRUE THEN 50 ELSE 80 END, 1 FROM "FormDesigns";
                INSERT INTO "FormDesignColumns" ("FormDesignId", "Key", "Label", "Width", "Position")
                    SELECT "Id", 'quantity', "QuantityLabel", 10, 2 FROM "FormDesigns" WHERE "ShowQuantityAndRate" = TRUE;
                INSERT INTO "FormDesignColumns" ("FormDesignId", "Key", "Label", "Width", "Position")
                    SELECT "Id", 'rate', "RateLabel", 14, 3 FROM "FormDesigns" WHERE "ShowQuantityAndRate" = TRUE;
                INSERT INTO "FormDesignColumns" ("FormDesignId", "Key", "Label", "Width", "Position")
                    SELECT "Id", 'amount', "AmountLabel", CASE WHEN "ShowQuantityAndRate" = TRUE THEN 14 ELSE 20 END,
                        CASE WHEN "ShowQuantityAndRate" = TRUE THEN 4 ELSE 2 END FROM "FormDesigns";
                """);

            foreach (var column in new[] { "ShowShipTo", "ShowTerms", "ShowQuantityAndRate", "DescriptionLabel", "QuantityLabel", "RateLabel", "AmountLabel" })
                migrationBuilder.DropColumn(name: column, table: "FormDesigns");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(name: "ShowShipTo", table: "FormDesigns", type: "INTEGER", nullable: false, defaultValue: true);
            migrationBuilder.AddColumn<bool>(name: "ShowTerms", table: "FormDesigns", type: "INTEGER", nullable: false, defaultValue: true);
            migrationBuilder.AddColumn<bool>(name: "ShowQuantityAndRate", table: "FormDesigns", type: "INTEGER", nullable: false, defaultValue: true);
            foreach (var column in new[] { "DescriptionLabel", "QuantityLabel", "RateLabel", "AmountLabel" })
                migrationBuilder.AddColumn<string>(name: column, table: "FormDesigns", type: "TEXT", maxLength: 20, nullable: true);

            migrationBuilder.Sql("""
                UPDATE "FormDesigns" SET "ShowShipTo" = FALSE WHERE ("Show" & 16) = 0;
                UPDATE "FormDesigns" SET "ShowTerms" = FALSE WHERE ("Show" & 32) = 0;
                UPDATE "FormDesigns" SET "ShowQuantityAndRate" = FALSE
                    WHERE NOT EXISTS (SELECT 1 FROM "FormDesignColumns" c WHERE c."FormDesignId" = "FormDesigns"."Id" AND c."Key" = 'quantity');
                UPDATE "FormDesigns" SET
                    "DescriptionLabel" = (SELECT SUBSTR(c."Label", 1, 20) FROM "FormDesignColumns" c WHERE c."FormDesignId" = "FormDesigns"."Id" AND c."Key" = 'description'),
                    "QuantityLabel" = (SELECT SUBSTR(c."Label", 1, 20) FROM "FormDesignColumns" c WHERE c."FormDesignId" = "FormDesigns"."Id" AND c."Key" = 'quantity'),
                    "RateLabel" = (SELECT SUBSTR(c."Label", 1, 20) FROM "FormDesignColumns" c WHERE c."FormDesignId" = "FormDesigns"."Id" AND c."Key" = 'rate'),
                    "AmountLabel" = (SELECT SUBSTR(c."Label", 1, 20) FROM "FormDesignColumns" c WHERE c."FormDesignId" = "FormDesigns"."Id" AND c."Key" = 'amount');
                """);

            migrationBuilder.DropTable(name: "FormDesignColumns");
            foreach (var column in new[] { "Show", "AutoFieldColumns", "Landscape", "Margins", "TextSize" })
                migrationBuilder.DropColumn(name: column, table: "FormDesigns");
        }
    }
}
