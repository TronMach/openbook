using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenBook.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class DonorsAndGifts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "GiftSettings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Ein = table.Column<string>(type: "TEXT", maxLength: 10, nullable: true),
                    SignerName = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    SignerTitle = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    Intro = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GiftSettings", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Pledges",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Number = table.Column<int>(type: "INTEGER", nullable: false),
                    DonorId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Date = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    AmountMinor = table.Column<long>(type: "INTEGER", nullable: false),
                    ReceivableAccountId = table.Column<Guid>(type: "TEXT", nullable: false),
                    IncomeAccountId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ClassId = table.Column<Guid>(type: "TEXT", nullable: true),
                    DueDate = table.Column<DateOnly>(type: "TEXT", nullable: true),
                    Memo = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    Status = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    JournalEntryId = table.Column<Guid>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Pledges", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Pledges_Accounts_IncomeAccountId",
                        column: x => x.IncomeAccountId,
                        principalTable: "Accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Pledges_Accounts_ReceivableAccountId",
                        column: x => x.ReceivableAccountId,
                        principalTable: "Accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Pledges_Customers_DonorId",
                        column: x => x.DonorId,
                        principalTable: "Customers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Pledges_JournalEntries_JournalEntryId",
                        column: x => x.JournalEntryId,
                        principalTable: "JournalEntries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Pledges_Segments_ClassId",
                        column: x => x.ClassId,
                        principalTable: "Segments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Donations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Number = table.Column<int>(type: "INTEGER", nullable: false),
                    DonorId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Date = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    Kind = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    AmountMinor = table.Column<long>(type: "INTEGER", nullable: false),
                    DebitAccountId = table.Column<Guid>(type: "TEXT", nullable: false),
                    IncomeAccountId = table.Column<Guid>(type: "TEXT", nullable: false),
                    PledgeId = table.Column<Guid>(type: "TEXT", nullable: true),
                    ClassId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Reference = table.Column<string>(type: "TEXT", maxLength: 50, nullable: true),
                    Memo = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    Description = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    GoodsValueMinor = table.Column<long>(type: "INTEGER", nullable: false),
                    GoodsDescription = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    Status = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    JournalEntryId = table.Column<Guid>(type: "TEXT", nullable: false),
                    CashEntryId = table.Column<Guid>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    AcknowledgedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Donations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Donations_Accounts_DebitAccountId",
                        column: x => x.DebitAccountId,
                        principalTable: "Accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Donations_Accounts_IncomeAccountId",
                        column: x => x.IncomeAccountId,
                        principalTable: "Accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Donations_Customers_DonorId",
                        column: x => x.DonorId,
                        principalTable: "Customers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Donations_JournalEntries_CashEntryId",
                        column: x => x.CashEntryId,
                        principalTable: "JournalEntries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Donations_JournalEntries_JournalEntryId",
                        column: x => x.JournalEntryId,
                        principalTable: "JournalEntries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Donations_Pledges_PledgeId",
                        column: x => x.PledgeId,
                        principalTable: "Pledges",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Donations_Segments_ClassId",
                        column: x => x.ClassId,
                        principalTable: "Segments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Donations_CashEntryId",
                table: "Donations",
                column: "CashEntryId");

            migrationBuilder.CreateIndex(
                name: "IX_Donations_ClassId",
                table: "Donations",
                column: "ClassId");

            migrationBuilder.CreateIndex(
                name: "IX_Donations_DebitAccountId",
                table: "Donations",
                column: "DebitAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_Donations_DonorId",
                table: "Donations",
                column: "DonorId");

            migrationBuilder.CreateIndex(
                name: "IX_Donations_IncomeAccountId",
                table: "Donations",
                column: "IncomeAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_Donations_JournalEntryId",
                table: "Donations",
                column: "JournalEntryId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Donations_Number",
                table: "Donations",
                column: "Number",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Donations_PledgeId",
                table: "Donations",
                column: "PledgeId");

            migrationBuilder.CreateIndex(
                name: "IX_Pledges_ClassId",
                table: "Pledges",
                column: "ClassId");

            migrationBuilder.CreateIndex(
                name: "IX_Pledges_DonorId",
                table: "Pledges",
                column: "DonorId");

            migrationBuilder.CreateIndex(
                name: "IX_Pledges_IncomeAccountId",
                table: "Pledges",
                column: "IncomeAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_Pledges_JournalEntryId",
                table: "Pledges",
                column: "JournalEntryId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Pledges_Number",
                table: "Pledges",
                column: "Number",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Pledges_ReceivableAccountId",
                table: "Pledges",
                column: "ReceivableAccountId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Donations");

            migrationBuilder.DropTable(
                name: "GiftSettings");

            migrationBuilder.DropTable(
                name: "Pledges");
        }
    }
}
