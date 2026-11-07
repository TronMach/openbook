using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenBook.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class LoanDrawsAndAdvances : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "CreditLimitMinor",
                table: "Loans",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "FactorFeeMinor",
                table: "Loans",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "LoanDraws",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Date = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    AmountMinor = table.Column<long>(type: "INTEGER", nullable: false),
                    BankAccountId = table.Column<Guid>(type: "TEXT", nullable: false),
                    EntryId = table.Column<Guid>(type: "TEXT", nullable: false),
                    IsVoided = table.Column<bool>(type: "INTEGER", nullable: false),
                    LoanId = table.Column<Guid>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LoanDraws", x => x.Id);
                    table.ForeignKey(
                        name: "FK_LoanDraws_Accounts_BankAccountId",
                        column: x => x.BankAccountId,
                        principalTable: "Accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_LoanDraws_JournalEntries_EntryId",
                        column: x => x.EntryId,
                        principalTable: "JournalEntries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_LoanDraws_Loans_LoanId",
                        column: x => x.LoanId,
                        principalTable: "Loans",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_LoanDraws_BankAccountId",
                table: "LoanDraws",
                column: "BankAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_LoanDraws_EntryId",
                table: "LoanDraws",
                column: "EntryId");

            migrationBuilder.CreateIndex(
                name: "IX_LoanDraws_LoanId",
                table: "LoanDraws",
                column: "LoanId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "LoanDraws");

            migrationBuilder.DropColumn(
                name: "CreditLimitMinor",
                table: "Loans");

            migrationBuilder.DropColumn(
                name: "FactorFeeMinor",
                table: "Loans");
        }
    }
}
