using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenBook.Infrastructure.Postgres.Migrations
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
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    DonorId = table.Column<Guid>(type: "uuid", nullable: false),
                    AmountMinor = table.Column<long>(type: "bigint", nullable: false),
                    DepositAccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    IncomeAccountId = table.Column<Guid>(type: "uuid", nullable: true),
                    ClassId = table.Column<Guid>(type: "uuid", nullable: true),
                    PledgeId = table.Column<Guid>(type: "uuid", nullable: true),
                    Memo = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    Frequency = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Interval = table.Column<int>(type: "integer", nullable: false),
                    StartDate = table.Column<DateOnly>(type: "date", nullable: false),
                    EndDate = table.Column<DateOnly>(type: "date", nullable: true),
                    MaxOccurrences = table.Column<int>(type: "integer", nullable: true),
                    OccurrencesPosted = table.Column<int>(type: "integer", nullable: false),
                    AutoCreate = table.Column<bool>(type: "boolean", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    LastGiftId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
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
