using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenBook.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AdvancedPricing : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "ListRateTenThousandths",
                table: "SalesReceiptLines",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PriceSource",
                table: "SalesReceiptLines",
                type: "TEXT",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "ListRateTenThousandths",
                table: "InvoiceLines",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PriceSource",
                table: "InvoiceLines",
                type: "TEXT",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "ListRateTenThousandths",
                table: "EstimateLines",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PriceSource",
                table: "EstimateLines",
                type: "TEXT",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "PriceLevelId",
                table: "Customers",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "ListRateTenThousandths",
                table: "CreditMemoLines",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PriceSource",
                table: "CreditMemoLines",
                type: "TEXT",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PriceRounding",
                table: "Companies",
                type: "TEXT",
                maxLength: 20,
                nullable: false,
                defaultValue: "Cent");

            migrationBuilder.CreateTable(
                name: "ItemQuantityBreaks",
                columns: table => new
                {
                    FromQuantityTenThousandths = table.Column<long>(type: "INTEGER", nullable: false),
                    ItemId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Kind = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    ValueTenThousandths = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ItemQuantityBreaks", x => new { x.ItemId, x.FromQuantityTenThousandths });
                    table.ForeignKey(
                        name: "FK_ItemQuantityBreaks_Items_ItemId",
                        column: x => x.ItemId,
                        principalTable: "Items",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PriceLevels",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Kind = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    PercentTenThousandths = table.Column<long>(type: "INTEGER", nullable: false),
                    IsActive = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PriceLevels", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PriceRules",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Priority = table.Column<int>(type: "INTEGER", nullable: false),
                    IsActive = table.Column<bool>(type: "INTEGER", nullable: false),
                    IsExclusive = table.Column<bool>(type: "INTEGER", nullable: false),
                    StartsOn = table.Column<DateOnly>(type: "TEXT", nullable: true),
                    EndsOn = table.Column<DateOnly>(type: "TEXT", nullable: true),
                    Customers = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    MinQuantityTenThousandths = table.Column<long>(type: "INTEGER", nullable: true),
                    MaxQuantityTenThousandths = table.Column<long>(type: "INTEGER", nullable: true),
                    Adjustment = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    ValueTenThousandths = table.Column<long>(type: "INTEGER", nullable: false),
                    Rounding = table.Column<string>(type: "TEXT", maxLength: 20, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PriceRules", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PriceLevelItems",
                columns: table => new
                {
                    ItemId = table.Column<Guid>(type: "TEXT", nullable: false),
                    PriceLevelId = table.Column<Guid>(type: "TEXT", nullable: false),
                    PriceTenThousandths = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PriceLevelItems", x => new { x.PriceLevelId, x.ItemId });
                    table.ForeignKey(
                        name: "FK_PriceLevelItems_Items_ItemId",
                        column: x => x.ItemId,
                        principalTable: "Items",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PriceLevelItems_PriceLevels_PriceLevelId",
                        column: x => x.PriceLevelId,
                        principalTable: "PriceLevels",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PriceRuleTargets",
                columns: table => new
                {
                    Kind = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    TargetId = table.Column<Guid>(type: "TEXT", nullable: false),
                    PriceRuleId = table.Column<Guid>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PriceRuleTargets", x => new { x.PriceRuleId, x.Kind, x.TargetId });
                    table.ForeignKey(
                        name: "FK_PriceRuleTargets_PriceRules_PriceRuleId",
                        column: x => x.PriceRuleId,
                        principalTable: "PriceRules",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Customers_PriceLevelId",
                table: "Customers",
                column: "PriceLevelId");

            migrationBuilder.CreateIndex(
                name: "IX_PriceLevelItems_ItemId",
                table: "PriceLevelItems",
                column: "ItemId");

            migrationBuilder.CreateIndex(
                name: "IX_PriceLevels_Name",
                table: "PriceLevels",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PriceRules_Name",
                table: "PriceRules",
                column: "Name",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_Customers_PriceLevels_PriceLevelId",
                table: "Customers",
                column: "PriceLevelId",
                principalTable: "PriceLevels",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Customers_PriceLevels_PriceLevelId",
                table: "Customers");

            migrationBuilder.DropTable(
                name: "ItemQuantityBreaks");

            migrationBuilder.DropTable(
                name: "PriceLevelItems");

            migrationBuilder.DropTable(
                name: "PriceRuleTargets");

            migrationBuilder.DropTable(
                name: "PriceLevels");

            migrationBuilder.DropTable(
                name: "PriceRules");

            migrationBuilder.DropIndex(
                name: "IX_Customers_PriceLevelId",
                table: "Customers");

            migrationBuilder.DropColumn(
                name: "ListRateTenThousandths",
                table: "SalesReceiptLines");

            migrationBuilder.DropColumn(
                name: "PriceSource",
                table: "SalesReceiptLines");

            migrationBuilder.DropColumn(
                name: "ListRateTenThousandths",
                table: "InvoiceLines");

            migrationBuilder.DropColumn(
                name: "PriceSource",
                table: "InvoiceLines");

            migrationBuilder.DropColumn(
                name: "ListRateTenThousandths",
                table: "EstimateLines");

            migrationBuilder.DropColumn(
                name: "PriceSource",
                table: "EstimateLines");

            migrationBuilder.DropColumn(
                name: "PriceLevelId",
                table: "Customers");

            migrationBuilder.DropColumn(
                name: "ListRateTenThousandths",
                table: "CreditMemoLines");

            migrationBuilder.DropColumn(
                name: "PriceSource",
                table: "CreditMemoLines");

            migrationBuilder.DropColumn(
                name: "PriceRounding",
                table: "Companies");
        }
    }
}
