using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenBook.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class GrantAdjustments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "GrantAdjustments",
                columns: table => new
                {
                    LineNumber = table.Column<int>(type: "INTEGER", nullable: false),
                    GrantId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Kind = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    Date = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    AmountMinor = table.Column<long>(type: "INTEGER", nullable: false),
                    BankAccountId = table.Column<Guid>(type: "TEXT", nullable: true),
                    EntryId = table.Column<Guid>(type: "TEXT", nullable: true),
                    CashEntryId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Reason = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    IsVoided = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GrantAdjustments", x => new { x.GrantId, x.LineNumber });
                    table.ForeignKey(
                        name: "FK_GrantAdjustments_Accounts_BankAccountId",
                        column: x => x.BankAccountId,
                        principalTable: "Accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_GrantAdjustments_Grants_GrantId",
                        column: x => x.GrantId,
                        principalTable: "Grants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_GrantAdjustments_JournalEntries_EntryId",
                        column: x => x.EntryId,
                        principalTable: "JournalEntries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_GrantAdjustments_BankAccountId",
                table: "GrantAdjustments",
                column: "BankAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_GrantAdjustments_EntryId",
                table: "GrantAdjustments",
                column: "EntryId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "GrantAdjustments");
        }
    }
}
