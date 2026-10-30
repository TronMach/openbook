using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenBook.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class CapturedDocuments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CapturedDocuments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Kind = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    FileName = table.Column<string>(type: "TEXT", maxLength: 255, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    CreatedBy = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    Status = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    Text = table.Column<string>(type: "TEXT", maxLength: 20000, nullable: true),
                    ReadBy = table.Column<string>(type: "TEXT", maxLength: 20, nullable: true),
                    ReadNote = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    Party = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    VendorId = table.Column<Guid>(type: "TEXT", nullable: true),
                    CustomerId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Number = table.Column<string>(type: "TEXT", maxLength: 50, nullable: true),
                    Date = table.Column<DateOnly>(type: "TEXT", nullable: true),
                    DueDate = table.Column<DateOnly>(type: "TEXT", nullable: true),
                    OrderReference = table.Column<string>(type: "TEXT", maxLength: 50, nullable: true),
                    PurchaseOrderId = table.Column<Guid>(type: "TEXT", nullable: true),
                    SubtotalMinor = table.Column<long>(type: "INTEGER", nullable: true),
                    TaxMinor = table.Column<long>(type: "INTEGER", nullable: true),
                    ShippingMinor = table.Column<long>(type: "INTEGER", nullable: true),
                    TotalMinor = table.Column<long>(type: "INTEGER", nullable: true),
                    ResultType = table.Column<string>(type: "TEXT", maxLength: 50, nullable: true),
                    ResultId = table.Column<Guid>(type: "TEXT", nullable: true),
                    DoneAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CapturedDocuments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CapturedDocuments_Customers_CustomerId",
                        column: x => x.CustomerId,
                        principalTable: "Customers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CapturedDocuments_PurchaseOrders_PurchaseOrderId",
                        column: x => x.PurchaseOrderId,
                        principalTable: "PurchaseOrders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CapturedDocuments_Vendors_VendorId",
                        column: x => x.VendorId,
                        principalTable: "Vendors",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "DocumentReadingSettings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    UseClaude = table.Column<bool>(type: "INTEGER", nullable: false),
                    ProtectedApiKey = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    Model = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DocumentReadingSettings", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "CapturedDocumentLines",
                columns: table => new
                {
                    LineNumber = table.Column<int>(type: "INTEGER", nullable: false),
                    CapturedDocumentId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Description = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    Sku = table.Column<string>(type: "TEXT", maxLength: 50, nullable: true),
                    QuantityTenThousandths = table.Column<long>(type: "INTEGER", nullable: true),
                    UnitPriceTenThousandths = table.Column<long>(type: "INTEGER", nullable: true),
                    AmountMinor = table.Column<long>(type: "INTEGER", nullable: true),
                    ItemId = table.Column<Guid>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CapturedDocumentLines", x => new { x.CapturedDocumentId, x.LineNumber });
                    table.ForeignKey(
                        name: "FK_CapturedDocumentLines_CapturedDocuments_CapturedDocumentId",
                        column: x => x.CapturedDocumentId,
                        principalTable: "CapturedDocuments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_CapturedDocumentLines_Items_ItemId",
                        column: x => x.ItemId,
                        principalTable: "Items",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CapturedDocumentLines_ItemId",
                table: "CapturedDocumentLines",
                column: "ItemId");

            migrationBuilder.CreateIndex(
                name: "IX_CapturedDocuments_CustomerId",
                table: "CapturedDocuments",
                column: "CustomerId");

            migrationBuilder.CreateIndex(
                name: "IX_CapturedDocuments_PurchaseOrderId",
                table: "CapturedDocuments",
                column: "PurchaseOrderId");

            migrationBuilder.CreateIndex(
                name: "IX_CapturedDocuments_Status",
                table: "CapturedDocuments",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_CapturedDocuments_VendorId",
                table: "CapturedDocuments",
                column: "VendorId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CapturedDocumentLines");

            migrationBuilder.DropTable(
                name: "DocumentReadingSettings");

            migrationBuilder.DropTable(
                name: "CapturedDocuments");
        }
    }
}
