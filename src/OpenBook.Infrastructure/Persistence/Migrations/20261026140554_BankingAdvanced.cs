using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenBook.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class BankingAdvanced : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "BankConnections",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Provider = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    ProtectedAccess = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: false),
                    IsActive = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    LastSyncAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    LastSuccessAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    LastSyncFailed = table.Column<bool>(type: "INTEGER", nullable: false),
                    LastError = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BankConnections", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PettyCashFunds",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    AccountId = table.Column<Guid>(type: "TEXT", nullable: false),
                    FloatMinor = table.Column<long>(type: "INTEGER", nullable: false),
                    Custodian = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    IsActive = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PettyCashFunds", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PettyCashFunds_Accounts_AccountId",
                        column: x => x.AccountId,
                        principalTable: "Accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "PositivePaySettings",
                columns: table => new
                {
                    AccountId = table.Column<Guid>(type: "TEXT", nullable: false),
                    BankAccountNumber = table.Column<string>(type: "TEXT", maxLength: 34, nullable: true),
                    DateFormat = table.Column<string>(type: "TEXT", maxLength: 30, nullable: false),
                    IncludeHeader = table.Column<bool>(type: "INTEGER", nullable: false),
                    LastExportedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    LastExportedThrough = table.Column<DateOnly>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PositivePaySettings", x => x.AccountId);
                    table.ForeignKey(
                        name: "FK_PositivePaySettings_Accounts_AccountId",
                        column: x => x.AccountId,
                        principalTable: "Accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "BankConnectionAccounts",
                columns: table => new
                {
                    ConnectionId = table.Column<Guid>(type: "TEXT", nullable: false),
                    RemoteId = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Institution = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    Currency = table.Column<string>(type: "TEXT", maxLength: 10, nullable: true),
                    BalanceMinor = table.Column<long>(type: "INTEGER", nullable: true),
                    BalanceDate = table.Column<DateOnly>(type: "TEXT", nullable: true),
                    AccountId = table.Column<Guid>(type: "TEXT", nullable: true),
                    ImportFrom = table.Column<DateOnly>(type: "TEXT", nullable: true),
                    FetchedThrough = table.Column<DateOnly>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BankConnectionAccounts", x => new { x.ConnectionId, x.RemoteId });
                    table.ForeignKey(
                        name: "FK_BankConnectionAccounts_Accounts_AccountId",
                        column: x => x.AccountId,
                        principalTable: "Accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_BankConnectionAccounts_BankConnections_ConnectionId",
                        column: x => x.ConnectionId,
                        principalTable: "BankConnections",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "CashCounts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    FundId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Date = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    CountedMinor = table.Column<long>(type: "INTEGER", nullable: false),
                    BookBalanceMinor = table.Column<long>(type: "INTEGER", nullable: false),
                    AdjustmentTransactionId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Breakdown = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    Note = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    CountedBy = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    CountedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CashCounts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CashCounts_BankTransactions_AdjustmentTransactionId",
                        column: x => x.AdjustmentTransactionId,
                        principalTable: "BankTransactions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CashCounts_PettyCashFunds_FundId",
                        column: x => x.FundId,
                        principalTable: "PettyCashFunds",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_BankConnectionAccounts_AccountId",
                table: "BankConnectionAccounts",
                column: "AccountId");

            migrationBuilder.CreateIndex(
                name: "IX_CashCounts_AdjustmentTransactionId",
                table: "CashCounts",
                column: "AdjustmentTransactionId");

            migrationBuilder.CreateIndex(
                name: "IX_CashCounts_FundId_Date",
                table: "CashCounts",
                columns: new[] { "FundId", "Date" });

            migrationBuilder.CreateIndex(
                name: "IX_PettyCashFunds_AccountId",
                table: "PettyCashFunds",
                column: "AccountId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "BankConnectionAccounts");

            migrationBuilder.DropTable(
                name: "CashCounts");

            migrationBuilder.DropTable(
                name: "PositivePaySettings");

            migrationBuilder.DropTable(
                name: "BankConnections");

            migrationBuilder.DropTable(
                name: "PettyCashFunds");
        }
    }
}
