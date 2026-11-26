using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenBook.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class OwnerOperators : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "PaysOwnerOperators",
                table: "Companies",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<Guid>(
                name: "OwnerSettlementId",
                table: "Bills",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "OwnerOperators",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    VendorId = table.Column<Guid>(type: "TEXT", nullable: false),
                    PayAccountId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ReceivableAccountId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ReserveAccountId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ClassId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Memo = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    IsActive = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    Version = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OwnerOperators", x => x.Id);
                    table.ForeignKey(
                        name: "FK_OwnerOperators_Accounts_PayAccountId",
                        column: x => x.PayAccountId,
                        principalTable: "Accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_OwnerOperators_Accounts_ReceivableAccountId",
                        column: x => x.ReceivableAccountId,
                        principalTable: "Accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_OwnerOperators_Accounts_ReserveAccountId",
                        column: x => x.ReserveAccountId,
                        principalTable: "Accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_OwnerOperators_Vendors_VendorId",
                        column: x => x.VendorId,
                        principalTable: "Vendors",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "OwnerReserves",
                columns: table => new
                {
                    Number = table.Column<int>(type: "INTEGER", nullable: false),
                    OwnerId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 60, nullable: false),
                    Method = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    RateTenThousandths = table.Column<long>(type: "INTEGER", nullable: false),
                    TargetMinor = table.Column<long>(type: "INTEGER", nullable: true),
                    OpeningMinor = table.Column<long>(type: "INTEGER", nullable: false),
                    InterestTenThousandths = table.Column<long>(type: "INTEGER", nullable: true),
                    IsActive = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OwnerReserves", x => new { x.OwnerId, x.Number });
                    table.ForeignKey(
                        name: "FK_OwnerReserves_OwnerOperators_OwnerId",
                        column: x => x.OwnerId,
                        principalTable: "OwnerOperators",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "OwnerSettlements",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Number = table.Column<int>(type: "INTEGER", nullable: false),
                    OwnerId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Date = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    PeriodStart = table.Column<DateOnly>(type: "TEXT", nullable: true),
                    PeriodEnd = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    Memo = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    Status = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    BillId = table.Column<Guid>(type: "TEXT", nullable: true),
                    JournalEntryId = table.Column<Guid>(type: "TEXT", nullable: true),
                    CreatedBy = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    VoidReason = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    Version = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OwnerSettlements", x => x.Id);
                    table.ForeignKey(
                        name: "FK_OwnerSettlements_Bills_BillId",
                        column: x => x.BillId,
                        principalTable: "Bills",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_OwnerSettlements_JournalEntries_JournalEntryId",
                        column: x => x.JournalEntryId,
                        principalTable: "JournalEntries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_OwnerSettlements_OwnerOperators_OwnerId",
                        column: x => x.OwnerId,
                        principalTable: "OwnerOperators",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Trucks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Unit = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    OwnerId = table.Column<Guid>(type: "TEXT", nullable: true),
                    ClassId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Vin = table.Column<string>(type: "TEXT", maxLength: 17, nullable: true),
                    Plate = table.Column<string>(type: "TEXT", maxLength: 20, nullable: true),
                    Year = table.Column<int>(type: "INTEGER", nullable: true),
                    Make = table.Column<string>(type: "TEXT", maxLength: 60, nullable: true),
                    IsActive = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    Version = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Trucks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Trucks_OwnerOperators_OwnerId",
                        column: x => x.OwnerId,
                        principalTable: "OwnerOperators",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "OwnerSettlementLines",
                columns: table => new
                {
                    LineNumber = table.Column<int>(type: "INTEGER", nullable: false),
                    SettlementId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Kind = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    Description = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    AmountMinor = table.Column<long>(type: "INTEGER", nullable: false),
                    AccountId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ClassId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Load = table.Column<string>(type: "TEXT", maxLength: 40, nullable: true),
                    TruckId = table.Column<Guid>(type: "TEXT", nullable: true),
                    LoadedMilesTenThousandths = table.Column<long>(type: "INTEGER", nullable: true),
                    EmptyMilesTenThousandths = table.Column<long>(type: "INTEGER", nullable: true),
                    Reportable = table.Column<bool>(type: "INTEGER", nullable: false),
                    ChargeId = table.Column<Guid>(type: "TEXT", nullable: true),
                    ReserveNumber = table.Column<int>(type: "INTEGER", nullable: true),
                    DeductionNumber = table.Column<int>(type: "INTEGER", nullable: true),
                    LoadId = table.Column<Guid>(type: "TEXT", nullable: true),
                    DispatchNumber = table.Column<int>(type: "INTEGER", nullable: true),
                    QuantityTenThousandths = table.Column<long>(type: "INTEGER", nullable: true),
                    RateTenThousandths = table.Column<long>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OwnerSettlementLines", x => new { x.SettlementId, x.LineNumber });
                    table.ForeignKey(
                        name: "FK_OwnerSettlementLines_Accounts_AccountId",
                        column: x => x.AccountId,
                        principalTable: "Accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_OwnerSettlementLines_OwnerSettlements_SettlementId",
                        column: x => x.SettlementId,
                        principalTable: "OwnerSettlements",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "OwnerCharges",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    OwnerId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Date = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    Kind = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    Description = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    Reference = table.Column<string>(type: "TEXT", maxLength: 60, nullable: true),
                    TruckId = table.Column<Guid>(type: "TEXT", nullable: true),
                    AmountMinor = table.Column<long>(type: "INTEGER", nullable: false),
                    GallonsTenThousandths = table.Column<long>(type: "INTEGER", nullable: true),
                    PaidFromAccountId = table.Column<Guid>(type: "TEXT", nullable: true),
                    JournalEntryId = table.Column<Guid>(type: "TEXT", nullable: true),
                    FromSettlementId = table.Column<Guid>(type: "TEXT", nullable: true),
                    RecoveredMinor = table.Column<long>(type: "INTEGER", nullable: false),
                    Counted1099 = table.Column<bool>(type: "INTEGER", nullable: false),
                    IsVoided = table.Column<bool>(type: "INTEGER", nullable: false),
                    ImportHash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    CreatedBy = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    Version = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OwnerCharges", x => x.Id);
                    table.ForeignKey(
                        name: "FK_OwnerCharges_Accounts_PaidFromAccountId",
                        column: x => x.PaidFromAccountId,
                        principalTable: "Accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_OwnerCharges_JournalEntries_JournalEntryId",
                        column: x => x.JournalEntryId,
                        principalTable: "JournalEntries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_OwnerCharges_OwnerOperators_OwnerId",
                        column: x => x.OwnerId,
                        principalTable: "OwnerOperators",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_OwnerCharges_Trucks_TruckId",
                        column: x => x.TruckId,
                        principalTable: "Trucks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_OwnerCharges_JournalEntryId",
                table: "OwnerCharges",
                column: "JournalEntryId");

            migrationBuilder.CreateIndex(
                name: "IX_OwnerCharges_OwnerId_Date",
                table: "OwnerCharges",
                columns: new[] { "OwnerId", "Date" });

            migrationBuilder.CreateIndex(
                name: "IX_OwnerCharges_PaidFromAccountId",
                table: "OwnerCharges",
                column: "PaidFromAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_OwnerCharges_TruckId",
                table: "OwnerCharges",
                column: "TruckId");

            migrationBuilder.CreateIndex(
                name: "IX_OwnerOperators_PayAccountId",
                table: "OwnerOperators",
                column: "PayAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_OwnerOperators_ReceivableAccountId",
                table: "OwnerOperators",
                column: "ReceivableAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_OwnerOperators_ReserveAccountId",
                table: "OwnerOperators",
                column: "ReserveAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_OwnerOperators_VendorId",
                table: "OwnerOperators",
                column: "VendorId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_OwnerSettlementLines_AccountId",
                table: "OwnerSettlementLines",
                column: "AccountId");

            migrationBuilder.CreateIndex(
                name: "IX_OwnerSettlementLines_ChargeId",
                table: "OwnerSettlementLines",
                column: "ChargeId");

            migrationBuilder.CreateIndex(
                name: "IX_OwnerSettlements_BillId",
                table: "OwnerSettlements",
                column: "BillId");

            migrationBuilder.CreateIndex(
                name: "IX_OwnerSettlements_JournalEntryId",
                table: "OwnerSettlements",
                column: "JournalEntryId");

            migrationBuilder.CreateIndex(
                name: "IX_OwnerSettlements_Number",
                table: "OwnerSettlements",
                column: "Number",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_OwnerSettlements_OwnerId_Number",
                table: "OwnerSettlements",
                columns: new[] { "OwnerId", "Number" });

            migrationBuilder.CreateIndex(
                name: "IX_Trucks_OwnerId",
                table: "Trucks",
                column: "OwnerId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "OwnerCharges");

            migrationBuilder.DropTable(
                name: "OwnerReserves");

            migrationBuilder.DropTable(
                name: "OwnerSettlementLines");

            migrationBuilder.DropTable(
                name: "Trucks");

            migrationBuilder.DropTable(
                name: "OwnerSettlements");

            migrationBuilder.DropTable(
                name: "OwnerOperators");

            migrationBuilder.DropColumn(
                name: "PaysOwnerOperators",
                table: "Companies");

            migrationBuilder.DropColumn(
                name: "OwnerSettlementId",
                table: "Bills");
        }
    }
}
