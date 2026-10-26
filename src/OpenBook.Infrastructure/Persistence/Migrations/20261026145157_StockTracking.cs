using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenBook.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class StockTracking : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Lots",
                table: "VendorCreditLines",
                type: "TEXT",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "WarehouseId",
                table: "SalesReceipts",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Lots",
                table: "SalesReceiptLines",
                type: "TEXT",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Barcode",
                table: "Items",
                type: "TEXT",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Tracking",
                table: "Items",
                type: "TEXT",
                maxLength: 20,
                nullable: false,
                defaultValue: "None");

            migrationBuilder.AddColumn<Guid>(
                name: "WarehouseId",
                table: "Invoices",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Lots",
                table: "InvoiceLines",
                type: "TEXT",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "BinId",
                table: "InventoryMovements",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Lot",
                table: "InventoryMovements",
                type: "TEXT",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "WarehouseId",
                table: "InventoryMovements",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "BinId",
                table: "InventoryAdjustmentLines",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Lots",
                table: "InventoryAdjustmentLines",
                type: "TEXT",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "WarehouseId",
                table: "InventoryAdjustmentLines",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Lots",
                table: "EstimateLines",
                type: "TEXT",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "WarehouseId",
                table: "CreditMemos",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Lots",
                table: "CreditMemoLines",
                type: "TEXT",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "WarehouseId",
                table: "Bills",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Lots",
                table: "BillLines",
                type: "TEXT",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Lots",
                table: "AssemblyBuilds",
                type: "TEXT",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "WarehouseId",
                table: "AssemblyBuilds",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Lots",
                table: "AssemblyBuildLines",
                type: "TEXT",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "StockTransfers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Date = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    Reference = table.Column<string>(type: "TEXT", maxLength: 30, nullable: true),
                    Memo = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    Status = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StockTransfers", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Warehouses",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    IsDefault = table.Column<bool>(type: "INTEGER", nullable: false),
                    Address = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    IsActive = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Warehouses", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Bins",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    WarehouseId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    IsActive = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Bins", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Bins_Warehouses_WarehouseId",
                        column: x => x.WarehouseId,
                        principalTable: "Warehouses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "StockTransferLines",
                columns: table => new
                {
                    LineNumber = table.Column<int>(type: "INTEGER", nullable: false),
                    StockTransferId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ItemId = table.Column<Guid>(type: "TEXT", nullable: false),
                    QuantityTenThousandths = table.Column<long>(type: "INTEGER", nullable: false),
                    FromWarehouseId = table.Column<Guid>(type: "TEXT", nullable: true),
                    FromBinId = table.Column<Guid>(type: "TEXT", nullable: true),
                    ToWarehouseId = table.Column<Guid>(type: "TEXT", nullable: true),
                    ToBinId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Lots = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StockTransferLines", x => new { x.StockTransferId, x.LineNumber });
                    table.ForeignKey(
                        name: "FK_StockTransferLines_Bins_FromBinId",
                        column: x => x.FromBinId,
                        principalTable: "Bins",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_StockTransferLines_Bins_ToBinId",
                        column: x => x.ToBinId,
                        principalTable: "Bins",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_StockTransferLines_Items_ItemId",
                        column: x => x.ItemId,
                        principalTable: "Items",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_StockTransferLines_StockTransfers_StockTransferId",
                        column: x => x.StockTransferId,
                        principalTable: "StockTransfers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_StockTransferLines_Warehouses_FromWarehouseId",
                        column: x => x.FromWarehouseId,
                        principalTable: "Warehouses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_StockTransferLines_Warehouses_ToWarehouseId",
                        column: x => x.ToWarehouseId,
                        principalTable: "Warehouses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SalesReceipts_WarehouseId",
                table: "SalesReceipts",
                column: "WarehouseId");

            migrationBuilder.CreateIndex(
                name: "IX_Items_Barcode",
                table: "Items",
                column: "Barcode",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Invoices_WarehouseId",
                table: "Invoices",
                column: "WarehouseId");

            migrationBuilder.CreateIndex(
                name: "IX_InventoryMovements_BinId",
                table: "InventoryMovements",
                column: "BinId");

            migrationBuilder.CreateIndex(
                name: "IX_InventoryMovements_ItemId_Lot",
                table: "InventoryMovements",
                columns: new[] { "ItemId", "Lot" });

            migrationBuilder.CreateIndex(
                name: "IX_InventoryMovements_WarehouseId",
                table: "InventoryMovements",
                column: "WarehouseId");

            migrationBuilder.CreateIndex(
                name: "IX_InventoryAdjustmentLines_BinId",
                table: "InventoryAdjustmentLines",
                column: "BinId");

            migrationBuilder.CreateIndex(
                name: "IX_InventoryAdjustmentLines_WarehouseId",
                table: "InventoryAdjustmentLines",
                column: "WarehouseId");

            migrationBuilder.CreateIndex(
                name: "IX_CreditMemos_WarehouseId",
                table: "CreditMemos",
                column: "WarehouseId");

            migrationBuilder.CreateIndex(
                name: "IX_Bills_WarehouseId",
                table: "Bills",
                column: "WarehouseId");

            migrationBuilder.CreateIndex(
                name: "IX_AssemblyBuilds_WarehouseId",
                table: "AssemblyBuilds",
                column: "WarehouseId");

            migrationBuilder.CreateIndex(
                name: "IX_Bins_WarehouseId_Name",
                table: "Bins",
                columns: new[] { "WarehouseId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_StockTransferLines_FromBinId",
                table: "StockTransferLines",
                column: "FromBinId");

            migrationBuilder.CreateIndex(
                name: "IX_StockTransferLines_FromWarehouseId",
                table: "StockTransferLines",
                column: "FromWarehouseId");

            migrationBuilder.CreateIndex(
                name: "IX_StockTransferLines_ItemId",
                table: "StockTransferLines",
                column: "ItemId");

            migrationBuilder.CreateIndex(
                name: "IX_StockTransferLines_ToBinId",
                table: "StockTransferLines",
                column: "ToBinId");

            migrationBuilder.CreateIndex(
                name: "IX_StockTransferLines_ToWarehouseId",
                table: "StockTransferLines",
                column: "ToWarehouseId");

            migrationBuilder.CreateIndex(
                name: "IX_Warehouses_Name",
                table: "Warehouses",
                column: "Name",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_AssemblyBuilds_Warehouses_WarehouseId",
                table: "AssemblyBuilds",
                column: "WarehouseId",
                principalTable: "Warehouses",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Bills_Warehouses_WarehouseId",
                table: "Bills",
                column: "WarehouseId",
                principalTable: "Warehouses",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_CreditMemos_Warehouses_WarehouseId",
                table: "CreditMemos",
                column: "WarehouseId",
                principalTable: "Warehouses",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_InventoryAdjustmentLines_Bins_BinId",
                table: "InventoryAdjustmentLines",
                column: "BinId",
                principalTable: "Bins",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_InventoryAdjustmentLines_Warehouses_WarehouseId",
                table: "InventoryAdjustmentLines",
                column: "WarehouseId",
                principalTable: "Warehouses",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_InventoryMovements_Bins_BinId",
                table: "InventoryMovements",
                column: "BinId",
                principalTable: "Bins",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_InventoryMovements_Warehouses_WarehouseId",
                table: "InventoryMovements",
                column: "WarehouseId",
                principalTable: "Warehouses",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Invoices_Warehouses_WarehouseId",
                table: "Invoices",
                column: "WarehouseId",
                principalTable: "Warehouses",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_SalesReceipts_Warehouses_WarehouseId",
                table: "SalesReceipts",
                column: "WarehouseId",
                principalTable: "Warehouses",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_AssemblyBuilds_Warehouses_WarehouseId",
                table: "AssemblyBuilds");

            migrationBuilder.DropForeignKey(
                name: "FK_Bills_Warehouses_WarehouseId",
                table: "Bills");

            migrationBuilder.DropForeignKey(
                name: "FK_CreditMemos_Warehouses_WarehouseId",
                table: "CreditMemos");

            migrationBuilder.DropForeignKey(
                name: "FK_InventoryAdjustmentLines_Bins_BinId",
                table: "InventoryAdjustmentLines");

            migrationBuilder.DropForeignKey(
                name: "FK_InventoryAdjustmentLines_Warehouses_WarehouseId",
                table: "InventoryAdjustmentLines");

            migrationBuilder.DropForeignKey(
                name: "FK_InventoryMovements_Bins_BinId",
                table: "InventoryMovements");

            migrationBuilder.DropForeignKey(
                name: "FK_InventoryMovements_Warehouses_WarehouseId",
                table: "InventoryMovements");

            migrationBuilder.DropForeignKey(
                name: "FK_Invoices_Warehouses_WarehouseId",
                table: "Invoices");

            migrationBuilder.DropForeignKey(
                name: "FK_SalesReceipts_Warehouses_WarehouseId",
                table: "SalesReceipts");

            migrationBuilder.DropTable(
                name: "StockTransferLines");

            migrationBuilder.DropTable(
                name: "Bins");

            migrationBuilder.DropTable(
                name: "StockTransfers");

            migrationBuilder.DropTable(
                name: "Warehouses");

            migrationBuilder.DropIndex(
                name: "IX_SalesReceipts_WarehouseId",
                table: "SalesReceipts");

            migrationBuilder.DropIndex(
                name: "IX_Items_Barcode",
                table: "Items");

            migrationBuilder.DropIndex(
                name: "IX_Invoices_WarehouseId",
                table: "Invoices");

            migrationBuilder.DropIndex(
                name: "IX_InventoryMovements_BinId",
                table: "InventoryMovements");

            migrationBuilder.DropIndex(
                name: "IX_InventoryMovements_ItemId_Lot",
                table: "InventoryMovements");

            migrationBuilder.DropIndex(
                name: "IX_InventoryMovements_WarehouseId",
                table: "InventoryMovements");

            migrationBuilder.DropIndex(
                name: "IX_InventoryAdjustmentLines_BinId",
                table: "InventoryAdjustmentLines");

            migrationBuilder.DropIndex(
                name: "IX_InventoryAdjustmentLines_WarehouseId",
                table: "InventoryAdjustmentLines");

            migrationBuilder.DropIndex(
                name: "IX_CreditMemos_WarehouseId",
                table: "CreditMemos");

            migrationBuilder.DropIndex(
                name: "IX_Bills_WarehouseId",
                table: "Bills");

            migrationBuilder.DropIndex(
                name: "IX_AssemblyBuilds_WarehouseId",
                table: "AssemblyBuilds");

            migrationBuilder.DropColumn(
                name: "Lots",
                table: "VendorCreditLines");

            migrationBuilder.DropColumn(
                name: "WarehouseId",
                table: "SalesReceipts");

            migrationBuilder.DropColumn(
                name: "Lots",
                table: "SalesReceiptLines");

            migrationBuilder.DropColumn(
                name: "Barcode",
                table: "Items");

            migrationBuilder.DropColumn(
                name: "Tracking",
                table: "Items");

            migrationBuilder.DropColumn(
                name: "WarehouseId",
                table: "Invoices");

            migrationBuilder.DropColumn(
                name: "Lots",
                table: "InvoiceLines");

            migrationBuilder.DropColumn(
                name: "BinId",
                table: "InventoryMovements");

            migrationBuilder.DropColumn(
                name: "Lot",
                table: "InventoryMovements");

            migrationBuilder.DropColumn(
                name: "WarehouseId",
                table: "InventoryMovements");

            migrationBuilder.DropColumn(
                name: "BinId",
                table: "InventoryAdjustmentLines");

            migrationBuilder.DropColumn(
                name: "Lots",
                table: "InventoryAdjustmentLines");

            migrationBuilder.DropColumn(
                name: "WarehouseId",
                table: "InventoryAdjustmentLines");

            migrationBuilder.DropColumn(
                name: "Lots",
                table: "EstimateLines");

            migrationBuilder.DropColumn(
                name: "WarehouseId",
                table: "CreditMemos");

            migrationBuilder.DropColumn(
                name: "Lots",
                table: "CreditMemoLines");

            migrationBuilder.DropColumn(
                name: "WarehouseId",
                table: "Bills");

            migrationBuilder.DropColumn(
                name: "Lots",
                table: "BillLines");

            migrationBuilder.DropColumn(
                name: "Lots",
                table: "AssemblyBuilds");

            migrationBuilder.DropColumn(
                name: "WarehouseId",
                table: "AssemblyBuilds");

            migrationBuilder.DropColumn(
                name: "Lots",
                table: "AssemblyBuildLines");
        }
    }
}
