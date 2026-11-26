using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenBook.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class CarrierSettlements : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "UsesSettlements",
                table: "Companies",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "Carriers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Unit = table.Column<string>(type: "TEXT", maxLength: 40, nullable: true),
                    DepositAccountId = table.Column<Guid>(type: "TEXT", nullable: false),
                    VendorId = table.Column<Guid>(type: "TEXT", nullable: true),
                    DueAccountId = table.Column<Guid>(type: "TEXT", nullable: true),
                    ClassId = table.Column<Guid>(type: "TEXT", nullable: true),
                    LocationId = table.Column<Guid>(type: "TEXT", nullable: true),
                    IsActive = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    Version = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Carriers", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Carriers_Accounts_DepositAccountId",
                        column: x => x.DepositAccountId,
                        principalTable: "Accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Carriers_Accounts_DueAccountId",
                        column: x => x.DueAccountId,
                        principalTable: "Accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Carriers_Vendors_VendorId",
                        column: x => x.VendorId,
                        principalTable: "Vendors",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "CarrierCodes",
                columns: table => new
                {
                    Section = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    Code = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    CarrierId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    AccountId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ClassId = table.Column<Guid>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CarrierCodes", x => new { x.CarrierId, x.Section, x.Code });
                    table.ForeignKey(
                        name: "FK_CarrierCodes_Accounts_AccountId",
                        column: x => x.AccountId,
                        principalTable: "Accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CarrierCodes_Carriers_CarrierId",
                        column: x => x.CarrierId,
                        principalTable: "Carriers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Settlements",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    CarrierId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Number = table.Column<string>(type: "TEXT", maxLength: 60, nullable: false),
                    Date = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    PeriodStart = table.Column<DateOnly>(type: "TEXT", nullable: true),
                    PeriodEnd = table.Column<DateOnly>(type: "TEXT", nullable: true),
                    NetPaidMinor = table.Column<long>(type: "INTEGER", nullable: false),
                    CarriedForwardMinor = table.Column<long>(type: "INTEGER", nullable: false),
                    Memo = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    Status = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    JournalEntryId = table.Column<Guid>(type: "TEXT", nullable: true),
                    LocationId = table.Column<Guid>(type: "TEXT", nullable: true),
                    CreatedBy = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    Version = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Settlements", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Settlements_Carriers_CarrierId",
                        column: x => x.CarrierId,
                        principalTable: "Carriers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Settlements_JournalEntries_JournalEntryId",
                        column: x => x.JournalEntryId,
                        principalTable: "JournalEntries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "SettlementDeposits",
                columns: table => new
                {
                    LineNumber = table.Column<int>(type: "INTEGER", nullable: false),
                    SettlementId = table.Column<Guid>(type: "TEXT", nullable: false),
                    AccountId = table.Column<Guid>(type: "TEXT", nullable: false),
                    AmountMinor = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SettlementDeposits", x => new { x.SettlementId, x.LineNumber });
                    table.ForeignKey(
                        name: "FK_SettlementDeposits_Accounts_AccountId",
                        column: x => x.AccountId,
                        principalTable: "Accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SettlementDeposits_Settlements_SettlementId",
                        column: x => x.SettlementId,
                        principalTable: "Settlements",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SettlementLines",
                columns: table => new
                {
                    LineNumber = table.Column<int>(type: "INTEGER", nullable: false),
                    SettlementId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Section = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    Code = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    Date = table.Column<DateOnly>(type: "TEXT", nullable: true),
                    Load = table.Column<string>(type: "TEXT", maxLength: 40, nullable: true),
                    Dispatch = table.Column<string>(type: "TEXT", maxLength: 20, nullable: true),
                    Description = table.Column<string>(type: "TEXT", maxLength: 300, nullable: true),
                    AmountMinor = table.Column<long>(type: "INTEGER", nullable: false),
                    AccountId = table.Column<Guid>(type: "TEXT", nullable: true),
                    ClassId = table.Column<Guid>(type: "TEXT", nullable: true),
                    LoadedMilesTenThousandths = table.Column<long>(type: "INTEGER", nullable: true),
                    EmptyMilesTenThousandths = table.Column<long>(type: "INTEGER", nullable: true),
                    GallonsTenThousandths = table.Column<long>(type: "INTEGER", nullable: true),
                    PostedAccountId = table.Column<Guid>(type: "TEXT", nullable: true),
                    PostedClassId = table.Column<Guid>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SettlementLines", x => new { x.SettlementId, x.LineNumber });
                    table.ForeignKey(
                        name: "FK_SettlementLines_Accounts_AccountId",
                        column: x => x.AccountId,
                        principalTable: "Accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SettlementLines_Accounts_PostedAccountId",
                        column: x => x.PostedAccountId,
                        principalTable: "Accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SettlementLines_Settlements_SettlementId",
                        column: x => x.SettlementId,
                        principalTable: "Settlements",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CarrierCodes_AccountId",
                table: "CarrierCodes",
                column: "AccountId");

            migrationBuilder.CreateIndex(
                name: "IX_Carriers_DepositAccountId",
                table: "Carriers",
                column: "DepositAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_Carriers_DueAccountId",
                table: "Carriers",
                column: "DueAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_Carriers_VendorId",
                table: "Carriers",
                column: "VendorId");

            migrationBuilder.CreateIndex(
                name: "IX_SettlementDeposits_AccountId",
                table: "SettlementDeposits",
                column: "AccountId");

            migrationBuilder.CreateIndex(
                name: "IX_SettlementLines_AccountId",
                table: "SettlementLines",
                column: "AccountId");

            migrationBuilder.CreateIndex(
                name: "IX_SettlementLines_PostedAccountId",
                table: "SettlementLines",
                column: "PostedAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_Settlements_CarrierId_Number",
                table: "Settlements",
                columns: new[] { "CarrierId", "Number" },
                unique: true,
                filter: "\"Status\" <> 'Dismissed'");

            migrationBuilder.CreateIndex(
                name: "IX_Settlements_JournalEntryId",
                table: "Settlements",
                column: "JournalEntryId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CarrierCodes");

            migrationBuilder.DropTable(
                name: "SettlementDeposits");

            migrationBuilder.DropTable(
                name: "SettlementLines");

            migrationBuilder.DropTable(
                name: "Settlements");

            migrationBuilder.DropTable(
                name: "Carriers");

            migrationBuilder.DropColumn(
                name: "UsesSettlements",
                table: "Companies");
        }
    }
}
