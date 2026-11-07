using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenBook.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class FeedSplits : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "ClassId",
                table: "BankRules",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "LocationId",
                table: "BankRules",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "BankFeedItemEntries",
                columns: table => new
                {
                    JournalEntryId = table.Column<Guid>(type: "TEXT", nullable: false),
                    BankFeedItemId = table.Column<Guid>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BankFeedItemEntries", x => new { x.BankFeedItemId, x.JournalEntryId });
                    table.ForeignKey(
                        name: "FK_BankFeedItemEntries_BankFeedItems_BankFeedItemId",
                        column: x => x.BankFeedItemId,
                        principalTable: "BankFeedItems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_BankFeedItemEntries_JournalEntries_JournalEntryId",
                        column: x => x.JournalEntryId,
                        principalTable: "JournalEntries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_BankFeedItemEntries_JournalEntryId",
                table: "BankFeedItemEntries",
                column: "JournalEntryId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "BankFeedItemEntries");

            migrationBuilder.DropColumn(
                name: "ClassId",
                table: "BankRules");

            migrationBuilder.DropColumn(
                name: "LocationId",
                table: "BankRules");
        }
    }
}
