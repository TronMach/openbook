using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenBook.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ChannelReturns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ChannelReturns",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ChannelId = table.Column<Guid>(type: "TEXT", nullable: false),
                    BatchId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Date = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    Memo = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    Status = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    JournalEntryId = table.Column<Guid>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ChannelReturns", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ChannelReturns_ChannelOrderBatches_BatchId",
                        column: x => x.BatchId,
                        principalTable: "ChannelOrderBatches",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ChannelReturns_JournalEntries_JournalEntryId",
                        column: x => x.JournalEntryId,
                        principalTable: "JournalEntries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ChannelReturns_SalesChannels_ChannelId",
                        column: x => x.ChannelId,
                        principalTable: "SalesChannels",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ChannelReturnLines",
                columns: table => new
                {
                    LineNumber = table.Column<int>(type: "INTEGER", nullable: false),
                    ChannelReturnId = table.Column<Guid>(type: "TEXT", nullable: false),
                    OrderLine = table.Column<int>(type: "INTEGER", nullable: false),
                    QuantityTenThousandths = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ChannelReturnLines", x => new { x.ChannelReturnId, x.LineNumber });
                    table.ForeignKey(
                        name: "FK_ChannelReturnLines_ChannelReturns_ChannelReturnId",
                        column: x => x.ChannelReturnId,
                        principalTable: "ChannelReturns",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ChannelReturns_BatchId",
                table: "ChannelReturns",
                column: "BatchId");

            migrationBuilder.CreateIndex(
                name: "IX_ChannelReturns_ChannelId",
                table: "ChannelReturns",
                column: "ChannelId");

            migrationBuilder.CreateIndex(
                name: "IX_ChannelReturns_JournalEntryId",
                table: "ChannelReturns",
                column: "JournalEntryId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ChannelReturnLines");

            migrationBuilder.DropTable(
                name: "ChannelReturns");
        }
    }
}
