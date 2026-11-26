using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenBook.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class OwnerDeductions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "OwnerDeductions",
                columns: table => new
                {
                    Number = table.Column<int>(type: "INTEGER", nullable: false),
                    OwnerId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 60, nullable: false),
                    AccountId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Method = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    RateTenThousandths = table.Column<long>(type: "INTEGER", nullable: false),
                    UpToMinor = table.Column<long>(type: "INTEGER", nullable: true),
                    Priority = table.Column<int>(type: "INTEGER", nullable: false),
                    StartsOn = table.Column<DateOnly>(type: "TEXT", nullable: true),
                    EndsOn = table.Column<DateOnly>(type: "TEXT", nullable: true),
                    CarryShort = table.Column<bool>(type: "INTEGER", nullable: false),
                    IsActive = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OwnerDeductions", x => new { x.OwnerId, x.Number });
                    table.ForeignKey(
                        name: "FK_OwnerDeductions_Accounts_AccountId",
                        column: x => x.AccountId,
                        principalTable: "Accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_OwnerDeductions_OwnerOperators_OwnerId",
                        column: x => x.OwnerId,
                        principalTable: "OwnerOperators",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ReserveInterestRuns",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Date = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    ExpenseAccountId = table.Column<Guid>(type: "TEXT", nullable: false),
                    JournalEntryId = table.Column<Guid>(type: "TEXT", nullable: false),
                    IsVoided = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedBy = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    Version = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReserveInterestRuns", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ReserveInterestRuns_Accounts_ExpenseAccountId",
                        column: x => x.ExpenseAccountId,
                        principalTable: "Accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ReserveInterestRuns_JournalEntries_JournalEntryId",
                        column: x => x.JournalEntryId,
                        principalTable: "JournalEntries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ReserveInterestLines",
                columns: table => new
                {
                    LineNumber = table.Column<int>(type: "INTEGER", nullable: false),
                    RunId = table.Column<Guid>(type: "TEXT", nullable: false),
                    OwnerId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ReserveNumber = table.Column<int>(type: "INTEGER", nullable: false),
                    BalanceMinor = table.Column<long>(type: "INTEGER", nullable: false),
                    AmountMinor = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReserveInterestLines", x => new { x.RunId, x.LineNumber });
                    table.ForeignKey(
                        name: "FK_ReserveInterestLines_OwnerOperators_OwnerId",
                        column: x => x.OwnerId,
                        principalTable: "OwnerOperators",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ReserveInterestLines_ReserveInterestRuns_RunId",
                        column: x => x.RunId,
                        principalTable: "ReserveInterestRuns",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_OwnerDeductions_AccountId",
                table: "OwnerDeductions",
                column: "AccountId");

            migrationBuilder.CreateIndex(
                name: "IX_ReserveInterestLines_OwnerId",
                table: "ReserveInterestLines",
                column: "OwnerId");

            migrationBuilder.CreateIndex(
                name: "IX_ReserveInterestRuns_Date",
                table: "ReserveInterestRuns",
                column: "Date");

            migrationBuilder.CreateIndex(
                name: "IX_ReserveInterestRuns_ExpenseAccountId",
                table: "ReserveInterestRuns",
                column: "ExpenseAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_ReserveInterestRuns_JournalEntryId",
                table: "ReserveInterestRuns",
                column: "JournalEntryId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "OwnerDeductions");

            migrationBuilder.DropTable(
                name: "ReserveInterestLines");

            migrationBuilder.DropTable(
                name: "ReserveInterestRuns");
        }
    }
}
