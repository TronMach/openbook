using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenBook.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ChannelOrders : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "MarketplaceCollectsTax",
                table: "SalesChannels",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            // Marketplaces collect and pay sales tax on their orders: existing Amazon, eBay and Etsy channels say so.
            migrationBuilder.Sql("UPDATE \"SalesChannels\" SET \"MarketplaceCollectsTax\" = TRUE WHERE \"Kind\" IN ('Amazon', 'Ebay', 'Etsy');");

            migrationBuilder.CreateTable(
                name: "ChannelOrderBatches",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ChannelId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Date = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    JournalEntryId = table.Column<Guid>(type: "TEXT", nullable: true),
                    ImportedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ChannelOrderBatches", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ChannelOrderBatches_JournalEntries_JournalEntryId",
                        column: x => x.JournalEntryId,
                        principalTable: "JournalEntries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ChannelOrderBatches_SalesChannels_ChannelId",
                        column: x => x.ChannelId,
                        principalTable: "SalesChannels",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ChannelSkus",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ChannelId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Sku = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    ItemId = table.Column<Guid>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ChannelSkus", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ChannelSkus_Items_ItemId",
                        column: x => x.ItemId,
                        principalTable: "Items",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ChannelSkus_SalesChannels_ChannelId",
                        column: x => x.ChannelId,
                        principalTable: "SalesChannels",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ChannelOrderLines",
                columns: table => new
                {
                    LineNumber = table.Column<int>(type: "INTEGER", nullable: false),
                    ChannelOrderBatchId = table.Column<Guid>(type: "TEXT", nullable: false),
                    OrderId = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Date = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    Sku = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    Title = table.Column<string>(type: "TEXT", maxLength: 300, nullable: true),
                    QuantityTenThousandths = table.Column<long>(type: "INTEGER", nullable: false),
                    SalesMinor = table.Column<long>(type: "INTEGER", nullable: false),
                    ShippingMinor = table.Column<long>(type: "INTEGER", nullable: false),
                    TaxMinor = table.Column<long>(type: "INTEGER", nullable: false),
                    Region = table.Column<string>(type: "TEXT", maxLength: 50, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ChannelOrderLines", x => new { x.ChannelOrderBatchId, x.LineNumber });
                    table.ForeignKey(
                        name: "FK_ChannelOrderLines_ChannelOrderBatches_ChannelOrderBatchId",
                        column: x => x.ChannelOrderBatchId,
                        principalTable: "ChannelOrderBatches",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ChannelOrderBatches_ChannelId_Date",
                table: "ChannelOrderBatches",
                columns: new[] { "ChannelId", "Date" });

            migrationBuilder.CreateIndex(
                name: "IX_ChannelOrderBatches_JournalEntryId",
                table: "ChannelOrderBatches",
                column: "JournalEntryId");

            migrationBuilder.CreateIndex(
                name: "IX_ChannelOrderLines_OrderId",
                table: "ChannelOrderLines",
                column: "OrderId");

            migrationBuilder.CreateIndex(
                name: "IX_ChannelSkus_ChannelId_Sku",
                table: "ChannelSkus",
                columns: new[] { "ChannelId", "Sku" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ChannelSkus_ItemId",
                table: "ChannelSkus",
                column: "ItemId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ChannelOrderLines");

            migrationBuilder.DropTable(
                name: "ChannelSkus");

            migrationBuilder.DropTable(
                name: "ChannelOrderBatches");

            migrationBuilder.DropColumn(
                name: "MarketplaceCollectsTax",
                table: "SalesChannels");
        }
    }
}
