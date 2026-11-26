using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenBook.Infrastructure.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class SettlementReview : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "FileHash",
                table: "Settlements",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "SettlementStatedBalances",
                columns: table => new
                {
                    LineNumber = table.Column<int>(type: "integer", nullable: false),
                    SettlementId = table.Column<Guid>(type: "uuid", nullable: false),
                    AccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    AmountMinor = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SettlementStatedBalances", x => new { x.SettlementId, x.LineNumber });
                    table.ForeignKey(
                        name: "FK_SettlementStatedBalances_Accounts_AccountId",
                        column: x => x.AccountId,
                        principalTable: "Accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SettlementStatedBalances_Settlements_SettlementId",
                        column: x => x.SettlementId,
                        principalTable: "Settlements",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SettlementStatedTotals",
                columns: table => new
                {
                    Section = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    SettlementId = table.Column<Guid>(type: "uuid", nullable: false),
                    AmountMinor = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SettlementStatedTotals", x => new { x.SettlementId, x.Section });
                    table.ForeignKey(
                        name: "FK_SettlementStatedTotals_Settlements_SettlementId",
                        column: x => x.SettlementId,
                        principalTable: "Settlements",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SettlementStatedBalances_AccountId",
                table: "SettlementStatedBalances",
                column: "AccountId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SettlementStatedBalances");

            migrationBuilder.DropTable(
                name: "SettlementStatedTotals");

            migrationBuilder.DropColumn(
                name: "FileHash",
                table: "Settlements");
        }
    }
}
