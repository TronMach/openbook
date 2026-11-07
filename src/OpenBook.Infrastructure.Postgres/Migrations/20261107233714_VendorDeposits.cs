using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenBook.Infrastructure.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class VendorDeposits : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "VendorDepositId",
                table: "BillPayments",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "VendorDeposits",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    VendorId = table.Column<Guid>(type: "uuid", nullable: false),
                    PurchaseOrderId = table.Column<Guid>(type: "uuid", nullable: true),
                    Date = table.Column<DateOnly>(type: "date", nullable: false),
                    AmountMinor = table.Column<long>(type: "bigint", nullable: false),
                    PaidFromAccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    DepositAccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    Reference = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    Memo = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    JournalEntryId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Version = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VendorDeposits", x => x.Id);
                    table.ForeignKey(
                        name: "FK_VendorDeposits_Accounts_DepositAccountId",
                        column: x => x.DepositAccountId,
                        principalTable: "Accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_VendorDeposits_Accounts_PaidFromAccountId",
                        column: x => x.PaidFromAccountId,
                        principalTable: "Accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_VendorDeposits_JournalEntries_JournalEntryId",
                        column: x => x.JournalEntryId,
                        principalTable: "JournalEntries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_VendorDeposits_PurchaseOrders_PurchaseOrderId",
                        column: x => x.PurchaseOrderId,
                        principalTable: "PurchaseOrders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_VendorDeposits_Vendors_VendorId",
                        column: x => x.VendorId,
                        principalTable: "Vendors",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_VendorDeposits_DepositAccountId",
                table: "VendorDeposits",
                column: "DepositAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_VendorDeposits_JournalEntryId",
                table: "VendorDeposits",
                column: "JournalEntryId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_VendorDeposits_PaidFromAccountId",
                table: "VendorDeposits",
                column: "PaidFromAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_VendorDeposits_PurchaseOrderId",
                table: "VendorDeposits",
                column: "PurchaseOrderId");

            migrationBuilder.CreateIndex(
                name: "IX_VendorDeposits_VendorId",
                table: "VendorDeposits",
                column: "VendorId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "VendorDeposits");

            migrationBuilder.DropColumn(
                name: "VendorDepositId",
                table: "BillPayments");
        }
    }
}
