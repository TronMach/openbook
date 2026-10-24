using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenBook.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Inventory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "ItemId",
                table: "VendorCreditLines",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "QuantityTenThousandths",
                table: "VendorCreditLines",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "CostAccountId",
                table: "Items",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "InventoryAssetAccountId",
                table: "Items",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "PurchaseCostTenThousandths",
                table: "Items",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<long>(
                name: "ReorderPointTenThousandths",
                table: "Items",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ItemId",
                table: "BillLines",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "QuantityTenThousandths",
                table: "BillLines",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "InventoryAdjustments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Date = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    Reference = table.Column<string>(type: "TEXT", maxLength: 30, nullable: true),
                    AdjustmentAccountId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Memo = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    Status = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    JournalEntryId = table.Column<Guid>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InventoryAdjustments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_InventoryAdjustments_Accounts_AdjustmentAccountId",
                        column: x => x.AdjustmentAccountId,
                        principalTable: "Accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_InventoryAdjustments_JournalEntries_JournalEntryId",
                        column: x => x.JournalEntryId,
                        principalTable: "JournalEntries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "InventoryMovements",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ItemId = table.Column<Guid>(type: "TEXT", nullable: false),
                    LayerId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Date = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    SourceType = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    SourceId = table.Column<Guid>(type: "TEXT", nullable: false),
                    QuantityTenThousandths = table.Column<long>(type: "INTEGER", nullable: false),
                    CostMinor = table.Column<long>(type: "INTEGER", nullable: false),
                    ReversalOf = table.Column<Guid>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InventoryMovements", x => x.Id);
                    table.ForeignKey(
                        name: "FK_InventoryMovements_Items_ItemId",
                        column: x => x.ItemId,
                        principalTable: "Items",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "InventoryAdjustmentLines",
                columns: table => new
                {
                    LineNumber = table.Column<int>(type: "INTEGER", nullable: false),
                    InventoryAdjustmentId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ItemId = table.Column<Guid>(type: "TEXT", nullable: false),
                    QuantityChangeTenThousandths = table.Column<long>(type: "INTEGER", nullable: false),
                    ValueChangeMinor = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InventoryAdjustmentLines", x => new { x.InventoryAdjustmentId, x.LineNumber });
                    table.ForeignKey(
                        name: "FK_InventoryAdjustmentLines_InventoryAdjustments_InventoryAdjustmentId",
                        column: x => x.InventoryAdjustmentId,
                        principalTable: "InventoryAdjustments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_InventoryAdjustmentLines_Items_ItemId",
                        column: x => x.ItemId,
                        principalTable: "Items",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_VendorCreditLines_ItemId",
                table: "VendorCreditLines",
                column: "ItemId");

            migrationBuilder.CreateIndex(
                name: "IX_Items_CostAccountId",
                table: "Items",
                column: "CostAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_Items_InventoryAssetAccountId",
                table: "Items",
                column: "InventoryAssetAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_BillLines_ItemId",
                table: "BillLines",
                column: "ItemId");

            migrationBuilder.CreateIndex(
                name: "IX_InventoryAdjustmentLines_ItemId",
                table: "InventoryAdjustmentLines",
                column: "ItemId");

            migrationBuilder.CreateIndex(
                name: "IX_InventoryAdjustments_AdjustmentAccountId",
                table: "InventoryAdjustments",
                column: "AdjustmentAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_InventoryAdjustments_JournalEntryId",
                table: "InventoryAdjustments",
                column: "JournalEntryId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_InventoryMovements_ItemId_Date",
                table: "InventoryMovements",
                columns: new[] { "ItemId", "Date" });

            migrationBuilder.CreateIndex(
                name: "IX_InventoryMovements_LayerId",
                table: "InventoryMovements",
                column: "LayerId");

            migrationBuilder.CreateIndex(
                name: "IX_InventoryMovements_SourceType_SourceId",
                table: "InventoryMovements",
                columns: new[] { "SourceType", "SourceId" });

            migrationBuilder.AddForeignKey(
                name: "FK_BillLines_Items_ItemId",
                table: "BillLines",
                column: "ItemId",
                principalTable: "Items",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Items_Accounts_CostAccountId",
                table: "Items",
                column: "CostAccountId",
                principalTable: "Accounts",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Items_Accounts_InventoryAssetAccountId",
                table: "Items",
                column: "InventoryAssetAccountId",
                principalTable: "Accounts",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_VendorCreditLines_Items_ItemId",
                table: "VendorCreditLines",
                column: "ItemId",
                principalTable: "Items",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_BillLines_Items_ItemId",
                table: "BillLines");

            migrationBuilder.DropForeignKey(
                name: "FK_Items_Accounts_CostAccountId",
                table: "Items");

            migrationBuilder.DropForeignKey(
                name: "FK_Items_Accounts_InventoryAssetAccountId",
                table: "Items");

            migrationBuilder.DropForeignKey(
                name: "FK_VendorCreditLines_Items_ItemId",
                table: "VendorCreditLines");

            migrationBuilder.DropTable(
                name: "InventoryAdjustmentLines");

            migrationBuilder.DropTable(
                name: "InventoryMovements");

            migrationBuilder.DropTable(
                name: "InventoryAdjustments");

            migrationBuilder.DropIndex(
                name: "IX_VendorCreditLines_ItemId",
                table: "VendorCreditLines");

            migrationBuilder.DropIndex(
                name: "IX_Items_CostAccountId",
                table: "Items");

            migrationBuilder.DropIndex(
                name: "IX_Items_InventoryAssetAccountId",
                table: "Items");

            migrationBuilder.DropIndex(
                name: "IX_BillLines_ItemId",
                table: "BillLines");

            migrationBuilder.DropColumn(
                name: "ItemId",
                table: "VendorCreditLines");

            migrationBuilder.DropColumn(
                name: "QuantityTenThousandths",
                table: "VendorCreditLines");

            migrationBuilder.DropColumn(
                name: "CostAccountId",
                table: "Items");

            migrationBuilder.DropColumn(
                name: "InventoryAssetAccountId",
                table: "Items");

            migrationBuilder.DropColumn(
                name: "PurchaseCostTenThousandths",
                table: "Items");

            migrationBuilder.DropColumn(
                name: "ReorderPointTenThousandths",
                table: "Items");

            migrationBuilder.DropColumn(
                name: "ItemId",
                table: "BillLines");

            migrationBuilder.DropColumn(
                name: "QuantityTenThousandths",
                table: "BillLines");
        }
    }
}
