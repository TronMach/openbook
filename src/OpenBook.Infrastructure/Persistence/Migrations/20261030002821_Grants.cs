using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenBook.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Grants : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Grants",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    FunderId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Reference = table.Column<string>(type: "TEXT", maxLength: 50, nullable: true),
                    AmountMinor = table.Column<long>(type: "INTEGER", nullable: false),
                    AwardedOn = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    StartsOn = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    EndsOn = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    ClassId = table.Column<Guid>(type: "TEXT", nullable: false),
                    IncomeAccountId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ReceivableAccountId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Conditional = table.Column<bool>(type: "INTEGER", nullable: false),
                    Terms = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    Status = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    AwardEntryId = table.Column<Guid>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Grants", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Grants_Accounts_IncomeAccountId",
                        column: x => x.IncomeAccountId,
                        principalTable: "Accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Grants_Accounts_ReceivableAccountId",
                        column: x => x.ReceivableAccountId,
                        principalTable: "Accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Grants_Customers_FunderId",
                        column: x => x.FunderId,
                        principalTable: "Customers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Grants_JournalEntries_AwardEntryId",
                        column: x => x.AwardEntryId,
                        principalTable: "JournalEntries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Grants_Segments_ClassId",
                        column: x => x.ClassId,
                        principalTable: "Segments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "GrantBudgetLines",
                columns: table => new
                {
                    LineNumber = table.Column<int>(type: "INTEGER", nullable: false),
                    GrantId = table.Column<Guid>(type: "TEXT", nullable: false),
                    AccountId = table.Column<Guid>(type: "TEXT", nullable: false),
                    AmountMinor = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GrantBudgetLines", x => new { x.GrantId, x.LineNumber });
                    table.ForeignKey(
                        name: "FK_GrantBudgetLines_Accounts_AccountId",
                        column: x => x.AccountId,
                        principalTable: "Accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_GrantBudgetLines_Grants_GrantId",
                        column: x => x.GrantId,
                        principalTable: "Grants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "GrantReceipts",
                columns: table => new
                {
                    LineNumber = table.Column<int>(type: "INTEGER", nullable: false),
                    GrantId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Date = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    AmountMinor = table.Column<long>(type: "INTEGER", nullable: false),
                    BankAccountId = table.Column<Guid>(type: "TEXT", nullable: false),
                    EntryId = table.Column<Guid>(type: "TEXT", nullable: false),
                    IsVoided = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GrantReceipts", x => new { x.GrantId, x.LineNumber });
                    table.ForeignKey(
                        name: "FK_GrantReceipts_Accounts_BankAccountId",
                        column: x => x.BankAccountId,
                        principalTable: "Accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_GrantReceipts_Grants_GrantId",
                        column: x => x.GrantId,
                        principalTable: "Grants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_GrantReceipts_JournalEntries_EntryId",
                        column: x => x.EntryId,
                        principalTable: "JournalEntries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "GrantRecognitions",
                columns: table => new
                {
                    LineNumber = table.Column<int>(type: "INTEGER", nullable: false),
                    GrantId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Date = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    AmountMinor = table.Column<long>(type: "INTEGER", nullable: false),
                    EntryId = table.Column<Guid>(type: "TEXT", nullable: false),
                    IsVoided = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GrantRecognitions", x => new { x.GrantId, x.LineNumber });
                    table.ForeignKey(
                        name: "FK_GrantRecognitions_Grants_GrantId",
                        column: x => x.GrantId,
                        principalTable: "Grants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_GrantRecognitions_JournalEntries_EntryId",
                        column: x => x.EntryId,
                        principalTable: "JournalEntries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_GrantBudgetLines_AccountId",
                table: "GrantBudgetLines",
                column: "AccountId");

            migrationBuilder.CreateIndex(
                name: "IX_GrantReceipts_BankAccountId",
                table: "GrantReceipts",
                column: "BankAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_GrantReceipts_EntryId",
                table: "GrantReceipts",
                column: "EntryId");

            migrationBuilder.CreateIndex(
                name: "IX_GrantRecognitions_EntryId",
                table: "GrantRecognitions",
                column: "EntryId");

            migrationBuilder.CreateIndex(
                name: "IX_Grants_AwardEntryId",
                table: "Grants",
                column: "AwardEntryId");

            migrationBuilder.CreateIndex(
                name: "IX_Grants_ClassId",
                table: "Grants",
                column: "ClassId");

            migrationBuilder.CreateIndex(
                name: "IX_Grants_FunderId",
                table: "Grants",
                column: "FunderId");

            migrationBuilder.CreateIndex(
                name: "IX_Grants_IncomeAccountId",
                table: "Grants",
                column: "IncomeAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_Grants_ReceivableAccountId",
                table: "Grants",
                column: "ReceivableAccountId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "GrantBudgetLines");

            migrationBuilder.DropTable(
                name: "GrantReceipts");

            migrationBuilder.DropTable(
                name: "GrantRecognitions");

            migrationBuilder.DropTable(
                name: "Grants");
        }
    }
}
