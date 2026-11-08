using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenBook.Infrastructure.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class BankRuleSplits : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "BankRuleSplits",
                columns: table => new
                {
                    Position = table.Column<int>(type: "integer", nullable: false),
                    BankRuleId = table.Column<Guid>(type: "uuid", nullable: false),
                    AccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    PercentTenThousandths = table.Column<long>(type: "bigint", nullable: false),
                    ClassId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BankRuleSplits", x => new { x.BankRuleId, x.Position });
                    table.ForeignKey(
                        name: "FK_BankRuleSplits_Accounts_AccountId",
                        column: x => x.AccountId,
                        principalTable: "Accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_BankRuleSplits_BankRules_BankRuleId",
                        column: x => x.BankRuleId,
                        principalTable: "BankRules",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_BankRuleSplits_AccountId",
                table: "BankRuleSplits",
                column: "AccountId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "BankRuleSplits");
        }
    }
}
