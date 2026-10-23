using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenBook.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class BankingPro : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "PrintedAt",
                table: "BillPayments",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "PrintedAt",
                table: "BankTransactions",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "BankRules",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Priority = table.Column<int>(type: "INTEGER", nullable: false),
                    AccountId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Direction = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    Contains = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    MinAmountMinor = table.Column<long>(type: "INTEGER", nullable: true),
                    MaxAmountMinor = table.Column<long>(type: "INTEGER", nullable: true),
                    CategoryAccountId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Payee = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    VendorId = table.Column<Guid>(type: "TEXT", nullable: true),
                    AutoAdd = table.Column<bool>(type: "INTEGER", nullable: false),
                    IsActive = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BankRules", x => x.Id);
                    table.ForeignKey(
                        name: "FK_BankRules_Accounts_AccountId",
                        column: x => x.AccountId,
                        principalTable: "Accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_BankRules_Accounts_CategoryAccountId",
                        column: x => x.CategoryAccountId,
                        principalTable: "Accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_BankRules_Vendors_VendorId",
                        column: x => x.VendorId,
                        principalTable: "Vendors",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "BankFeedItems",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    AccountId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Date = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    AmountMinor = table.Column<long>(type: "INTEGER", nullable: false),
                    Payee = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    Reference = table.Column<string>(type: "TEXT", maxLength: 30, nullable: true),
                    Memo = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    FitId = table.Column<string>(type: "TEXT", maxLength: 255, nullable: true),
                    DedupeKey = table.Column<string>(type: "TEXT", maxLength: 600, nullable: false),
                    ImportBatchId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ImportedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    BankTransactionId = table.Column<Guid>(type: "TEXT", nullable: true),
                    MatchedJournalEntryId = table.Column<Guid>(type: "TEXT", nullable: true),
                    RuleId = table.Column<Guid>(type: "TEXT", nullable: true),
                    ResolvedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    ResolvedBy = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BankFeedItems", x => x.Id);
                    table.ForeignKey(
                        name: "FK_BankFeedItems_Accounts_AccountId",
                        column: x => x.AccountId,
                        principalTable: "Accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_BankFeedItems_BankRules_RuleId",
                        column: x => x.RuleId,
                        principalTable: "BankRules",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_BankFeedItems_BankTransactions_BankTransactionId",
                        column: x => x.BankTransactionId,
                        principalTable: "BankTransactions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_BankFeedItems_JournalEntries_MatchedJournalEntryId",
                        column: x => x.MatchedJournalEntryId,
                        principalTable: "JournalEntries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_BankFeedItems_AccountId_DedupeKey",
                table: "BankFeedItems",
                columns: new[] { "AccountId", "DedupeKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_BankFeedItems_AccountId_Status",
                table: "BankFeedItems",
                columns: new[] { "AccountId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_BankFeedItems_BankTransactionId",
                table: "BankFeedItems",
                column: "BankTransactionId");

            migrationBuilder.CreateIndex(
                name: "IX_BankFeedItems_MatchedJournalEntryId",
                table: "BankFeedItems",
                column: "MatchedJournalEntryId");

            migrationBuilder.CreateIndex(
                name: "IX_BankFeedItems_RuleId",
                table: "BankFeedItems",
                column: "RuleId");

            migrationBuilder.CreateIndex(
                name: "IX_BankRules_AccountId",
                table: "BankRules",
                column: "AccountId");

            migrationBuilder.CreateIndex(
                name: "IX_BankRules_CategoryAccountId",
                table: "BankRules",
                column: "CategoryAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_BankRules_VendorId",
                table: "BankRules",
                column: "VendorId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "BankFeedItems");

            migrationBuilder.DropTable(
                name: "BankRules");

            migrationBuilder.DropColumn(
                name: "PrintedAt",
                table: "BillPayments");

            migrationBuilder.DropColumn(
                name: "PrintedAt",
                table: "BankTransactions");
        }
    }
}
