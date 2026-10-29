using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenBook.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class PurchasingControls : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Approval",
                table: "Bills",
                type: "TEXT",
                maxLength: 20,
                nullable: false,
                defaultValue: "NotRequired");

            migrationBuilder.AddColumn<string>(
                name: "ApprovalNote",
                table: "Bills",
                type: "TEXT",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ApprovedAt",
                table: "Bills",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ApprovedBy",
                table: "Bills",
                type: "TEXT",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "EnteredBy",
                table: "Bills",
                type: "TEXT",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "HoldReason",
                table: "Bills",
                type: "TEXT",
                maxLength: 2000,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "PurchaseOrderId",
                table: "Bills",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "AchBatchId",
                table: "BillPayments",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "AchBatches",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    AccountId = table.Column<Guid>(type: "TEXT", nullable: false),
                    EffectiveDate = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    FileIdModifier = table.Column<string>(type: "TEXT", maxLength: 1, nullable: false),
                    EntryCount = table.Column<int>(type: "INTEGER", nullable: false),
                    TotalMinor = table.Column<long>(type: "INTEGER", nullable: false),
                    Content = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AchBatches", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AchBatches_Accounts_AccountId",
                        column: x => x.AccountId,
                        principalTable: "Accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "AchSettings",
                columns: table => new
                {
                    AccountId = table.Column<Guid>(type: "TEXT", nullable: false),
                    DestinationRouting = table.Column<string>(type: "TEXT", maxLength: 9, nullable: false),
                    DestinationName = table.Column<string>(type: "TEXT", maxLength: 23, nullable: false),
                    OriginId = table.Column<string>(type: "TEXT", maxLength: 10, nullable: false),
                    OriginName = table.Column<string>(type: "TEXT", maxLength: 23, nullable: false),
                    CompanyId = table.Column<string>(type: "TEXT", maxLength: 10, nullable: false),
                    CompanyName = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    EntryDescription = table.Column<string>(type: "TEXT", maxLength: 10, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AchSettings", x => x.AccountId);
                    table.ForeignKey(
                        name: "FK_AchSettings_Accounts_AccountId",
                        column: x => x.AccountId,
                        principalTable: "Accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "BillSourceLines",
                columns: table => new
                {
                    LineNumber = table.Column<int>(type: "INTEGER", nullable: false),
                    BillId = table.Column<Guid>(type: "TEXT", nullable: false),
                    QuantityTenThousandths = table.Column<long>(type: "INTEGER", nullable: false),
                    AmountMinor = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BillSourceLines", x => new { x.BillId, x.LineNumber });
                    table.ForeignKey(
                        name: "FK_BillSourceLines_Bills_BillId",
                        column: x => x.BillId,
                        principalTable: "Bills",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PurchaseOrders",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Number = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    VendorId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Date = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    ExpectedDate = table.Column<DateOnly>(type: "TEXT", nullable: true),
                    Memo = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    WarehouseId = table.Column<Guid>(type: "TEXT", nullable: true),
                    LocationId = table.Column<Guid>(type: "TEXT", nullable: true),
                    TotalMinor = table.Column<long>(type: "INTEGER", nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PurchaseOrders", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PurchaseOrders_Vendors_VendorId",
                        column: x => x.VendorId,
                        principalTable: "Vendors",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "PurchasingSettings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ApprovalThresholdMinor = table.Column<long>(type: "INTEGER", nullable: true),
                    PriceTolerancePercentTenThousandths = table.Column<long>(type: "INTEGER", nullable: false),
                    RequireReceipt = table.Column<bool>(type: "INTEGER", nullable: false),
                    AllowSelfApproval = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PurchasingSettings", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "VendorBankAccounts",
                columns: table => new
                {
                    VendorId = table.Column<Guid>(type: "TEXT", nullable: false),
                    RoutingNumber = table.Column<string>(type: "TEXT", maxLength: 9, nullable: false),
                    ProtectedAccountNumber = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: false),
                    AccountLast4 = table.Column<string>(type: "TEXT", maxLength: 4, nullable: false),
                    Kind = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VendorBankAccounts", x => x.VendorId);
                    table.ForeignKey(
                        name: "FK_VendorBankAccounts_Vendors_VendorId",
                        column: x => x.VendorId,
                        principalTable: "Vendors",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "PurchaseOrderLines",
                columns: table => new
                {
                    LineNumber = table.Column<int>(type: "INTEGER", nullable: false),
                    PurchaseOrderId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ItemId = table.Column<Guid>(type: "TEXT", nullable: true),
                    AccountId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Description = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    QuantityTenThousandths = table.Column<long>(type: "INTEGER", nullable: false),
                    UnitCostTenThousandths = table.Column<long>(type: "INTEGER", nullable: false),
                    AmountMinor = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PurchaseOrderLines", x => new { x.PurchaseOrderId, x.LineNumber });
                    table.ForeignKey(
                        name: "FK_PurchaseOrderLines_Accounts_AccountId",
                        column: x => x.AccountId,
                        principalTable: "Accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PurchaseOrderLines_Items_ItemId",
                        column: x => x.ItemId,
                        principalTable: "Items",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PurchaseOrderLines_PurchaseOrders_PurchaseOrderId",
                        column: x => x.PurchaseOrderId,
                        principalTable: "PurchaseOrders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PurchaseOrderReceipts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ReceiptNumber = table.Column<int>(type: "INTEGER", nullable: false),
                    Date = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    LineNumber = table.Column<int>(type: "INTEGER", nullable: false),
                    QuantityTenThousandths = table.Column<long>(type: "INTEGER", nullable: false),
                    Note = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    IsVoided = table.Column<bool>(type: "INTEGER", nullable: false),
                    PurchaseOrderId = table.Column<Guid>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PurchaseOrderReceipts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PurchaseOrderReceipts_PurchaseOrders_PurchaseOrderId",
                        column: x => x.PurchaseOrderId,
                        principalTable: "PurchaseOrders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Bills_PurchaseOrderId",
                table: "Bills",
                column: "PurchaseOrderId");

            migrationBuilder.CreateIndex(
                name: "IX_BillPayments_AchBatchId",
                table: "BillPayments",
                column: "AchBatchId");

            migrationBuilder.CreateIndex(
                name: "IX_AchBatches_AccountId",
                table: "AchBatches",
                column: "AccountId");

            migrationBuilder.CreateIndex(
                name: "IX_PurchaseOrderLines_AccountId",
                table: "PurchaseOrderLines",
                column: "AccountId");

            migrationBuilder.CreateIndex(
                name: "IX_PurchaseOrderLines_ItemId",
                table: "PurchaseOrderLines",
                column: "ItemId");

            migrationBuilder.CreateIndex(
                name: "IX_PurchaseOrderReceipts_PurchaseOrderId",
                table: "PurchaseOrderReceipts",
                column: "PurchaseOrderId");

            migrationBuilder.CreateIndex(
                name: "IX_PurchaseOrders_Number",
                table: "PurchaseOrders",
                column: "Number",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PurchaseOrders_VendorId_Date",
                table: "PurchaseOrders",
                columns: new[] { "VendorId", "Date" });

            migrationBuilder.AddForeignKey(
                name: "FK_BillPayments_AchBatches_AchBatchId",
                table: "BillPayments",
                column: "AchBatchId",
                principalTable: "AchBatches",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Bills_PurchaseOrders_PurchaseOrderId",
                table: "Bills",
                column: "PurchaseOrderId",
                principalTable: "PurchaseOrders",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_BillPayments_AchBatches_AchBatchId",
                table: "BillPayments");

            migrationBuilder.DropForeignKey(
                name: "FK_Bills_PurchaseOrders_PurchaseOrderId",
                table: "Bills");

            migrationBuilder.DropTable(
                name: "AchBatches");

            migrationBuilder.DropTable(
                name: "AchSettings");

            migrationBuilder.DropTable(
                name: "BillSourceLines");

            migrationBuilder.DropTable(
                name: "PurchaseOrderLines");

            migrationBuilder.DropTable(
                name: "PurchaseOrderReceipts");

            migrationBuilder.DropTable(
                name: "PurchasingSettings");

            migrationBuilder.DropTable(
                name: "VendorBankAccounts");

            migrationBuilder.DropTable(
                name: "PurchaseOrders");

            migrationBuilder.DropIndex(
                name: "IX_Bills_PurchaseOrderId",
                table: "Bills");

            migrationBuilder.DropIndex(
                name: "IX_BillPayments_AchBatchId",
                table: "BillPayments");

            migrationBuilder.DropColumn(
                name: "Approval",
                table: "Bills");

            migrationBuilder.DropColumn(
                name: "ApprovalNote",
                table: "Bills");

            migrationBuilder.DropColumn(
                name: "ApprovedAt",
                table: "Bills");

            migrationBuilder.DropColumn(
                name: "ApprovedBy",
                table: "Bills");

            migrationBuilder.DropColumn(
                name: "EnteredBy",
                table: "Bills");

            migrationBuilder.DropColumn(
                name: "HoldReason",
                table: "Bills");

            migrationBuilder.DropColumn(
                name: "PurchaseOrderId",
                table: "Bills");

            migrationBuilder.DropColumn(
                name: "AchBatchId",
                table: "BillPayments");
        }
    }
}
