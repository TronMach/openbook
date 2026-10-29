using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace OpenBook.Infrastructure.Postgres.Migrations
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
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "NotRequired");

            migrationBuilder.AddColumn<string>(
                name: "ApprovalNote",
                table: "Bills",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ApprovedAt",
                table: "Bills",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ApprovedBy",
                table: "Bills",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "EnteredBy",
                table: "Bills",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "HoldReason",
                table: "Bills",
                type: "character varying(2000)",
                maxLength: 2000,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "PurchaseOrderId",
                table: "Bills",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "AchBatchId",
                table: "BillPayments",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "AchBatches",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    EffectiveDate = table.Column<DateOnly>(type: "date", nullable: false),
                    FileIdModifier = table.Column<string>(type: "character varying(1)", maxLength: 1, nullable: false),
                    EntryCount = table.Column<int>(type: "integer", nullable: false),
                    TotalMinor = table.Column<long>(type: "bigint", nullable: false),
                    Content = table.Column<string>(type: "text", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
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
                    AccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    DestinationRouting = table.Column<string>(type: "character varying(9)", maxLength: 9, nullable: false),
                    DestinationName = table.Column<string>(type: "character varying(23)", maxLength: 23, nullable: false),
                    OriginId = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    OriginName = table.Column<string>(type: "character varying(23)", maxLength: 23, nullable: false),
                    CompanyId = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    CompanyName = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    EntryDescription = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false)
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
                    LineNumber = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    BillId = table.Column<Guid>(type: "uuid", nullable: false),
                    QuantityTenThousandths = table.Column<long>(type: "bigint", nullable: false),
                    AmountMinor = table.Column<long>(type: "bigint", nullable: false)
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
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Number = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    VendorId = table.Column<Guid>(type: "uuid", nullable: false),
                    Date = table.Column<DateOnly>(type: "date", nullable: false),
                    ExpectedDate = table.Column<DateOnly>(type: "date", nullable: true),
                    Memo = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    WarehouseId = table.Column<Guid>(type: "uuid", nullable: true),
                    LocationId = table.Column<Guid>(type: "uuid", nullable: true),
                    TotalMinor = table.Column<long>(type: "bigint", nullable: false),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
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
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ApprovalThresholdMinor = table.Column<long>(type: "bigint", nullable: true),
                    PriceTolerancePercentTenThousandths = table.Column<long>(type: "bigint", nullable: false),
                    RequireReceipt = table.Column<bool>(type: "boolean", nullable: false),
                    AllowSelfApproval = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PurchasingSettings", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "VendorBankAccounts",
                columns: table => new
                {
                    VendorId = table.Column<Guid>(type: "uuid", nullable: false),
                    RoutingNumber = table.Column<string>(type: "character varying(9)", maxLength: 9, nullable: false),
                    ProtectedAccountNumber = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    AccountLast4 = table.Column<string>(type: "character varying(4)", maxLength: 4, nullable: false),
                    Kind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
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
                    LineNumber = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    PurchaseOrderId = table.Column<Guid>(type: "uuid", nullable: false),
                    ItemId = table.Column<Guid>(type: "uuid", nullable: true),
                    AccountId = table.Column<Guid>(type: "uuid", nullable: true),
                    Description = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    QuantityTenThousandths = table.Column<long>(type: "bigint", nullable: false),
                    UnitCostTenThousandths = table.Column<long>(type: "bigint", nullable: false),
                    AmountMinor = table.Column<long>(type: "bigint", nullable: false)
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
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ReceiptNumber = table.Column<int>(type: "integer", nullable: false),
                    Date = table.Column<DateOnly>(type: "date", nullable: false),
                    LineNumber = table.Column<int>(type: "integer", nullable: false),
                    QuantityTenThousandths = table.Column<long>(type: "bigint", nullable: false),
                    Note = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    IsVoided = table.Column<bool>(type: "boolean", nullable: false),
                    PurchaseOrderId = table.Column<Guid>(type: "uuid", nullable: false)
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
