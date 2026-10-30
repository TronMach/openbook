using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenBook.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class RecurringGifts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "RecurringGifts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    DonorId = table.Column<Guid>(type: "TEXT", nullable: false),
                    AmountMinor = table.Column<long>(type: "INTEGER", nullable: false),
                    DepositAccountId = table.Column<Guid>(type: "TEXT", nullable: false),
                    IncomeAccountId = table.Column<Guid>(type: "TEXT", nullable: true),
                    ClassId = table.Column<Guid>(type: "TEXT", nullable: true),
                    PledgeId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Memo = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    Frequency = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    Interval = table.Column<int>(type: "INTEGER", nullable: false),
                    StartDate = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    EndDate = table.Column<DateOnly>(type: "TEXT", nullable: true),
                    MaxOccurrences = table.Column<int>(type: "INTEGER", nullable: true),
                    OccurrencesPosted = table.Column<int>(type: "INTEGER", nullable: false),
                    AutoCreate = table.Column<bool>(type: "INTEGER", nullable: false),
                    IsActive = table.Column<bool>(type: "INTEGER", nullable: false),
                    LastGiftId = table.Column<Guid>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RecurringGifts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RecurringGifts_Accounts_DepositAccountId",
                        column: x => x.DepositAccountId,
                        principalTable: "Accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_RecurringGifts_Accounts_IncomeAccountId",
                        column: x => x.IncomeAccountId,
                        principalTable: "Accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_RecurringGifts_Customers_DonorId",
                        column: x => x.DonorId,
                        principalTable: "Customers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_RecurringGifts_Pledges_PledgeId",
                        column: x => x.PledgeId,
                        principalTable: "Pledges",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_RecurringGifts_Segments_ClassId",
                        column: x => x.ClassId,
                        principalTable: "Segments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_RecurringGifts_ClassId",
                table: "RecurringGifts",
                column: "ClassId");

            migrationBuilder.CreateIndex(
                name: "IX_RecurringGifts_DepositAccountId",
                table: "RecurringGifts",
                column: "DepositAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_RecurringGifts_DonorId",
                table: "RecurringGifts",
                column: "DonorId");

            migrationBuilder.CreateIndex(
                name: "IX_RecurringGifts_IncomeAccountId",
                table: "RecurringGifts",
                column: "IncomeAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_RecurringGifts_PledgeId",
                table: "RecurringGifts",
                column: "PledgeId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "RecurringGifts");
        }
    }
}
