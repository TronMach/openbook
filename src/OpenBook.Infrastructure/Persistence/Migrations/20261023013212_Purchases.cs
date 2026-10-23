using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenBook.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Purchases : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "VendorId",
                table: "BankTransactions",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "Vendors",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    DisplayName = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    CompanyName = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    Email = table.Column<string>(type: "TEXT", maxLength: 320, nullable: true),
                    Phone = table.Column<string>(type: "TEXT", maxLength: 50, nullable: true),
                    Address = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    AccountNumber = table.Column<string>(type: "TEXT", maxLength: 50, nullable: true),
                    PaymentTermsDays = table.Column<int>(type: "INTEGER", nullable: false),
                    DefaultExpenseAccountId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Notes = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    IsActive = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Vendors", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Vendors_Accounts_DefaultExpenseAccountId",
                        column: x => x.DefaultExpenseAccountId,
                        principalTable: "Accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "BillPayments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    VendorId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Date = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    AmountMinor = table.Column<long>(type: "INTEGER", nullable: false),
                    PaymentAccountId = table.Column<Guid>(type: "TEXT", nullable: false),
                    PayableAccountId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Reference = table.Column<string>(type: "TEXT", maxLength: 30, nullable: true),
                    Memo = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    Status = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    JournalEntryId = table.Column<Guid>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BillPayments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_BillPayments_Accounts_PayableAccountId",
                        column: x => x.PayableAccountId,
                        principalTable: "Accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_BillPayments_Accounts_PaymentAccountId",
                        column: x => x.PaymentAccountId,
                        principalTable: "Accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_BillPayments_JournalEntries_JournalEntryId",
                        column: x => x.JournalEntryId,
                        principalTable: "JournalEntries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_BillPayments_Vendors_VendorId",
                        column: x => x.VendorId,
                        principalTable: "Vendors",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Bills",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    VendorId = table.Column<Guid>(type: "TEXT", nullable: false),
                    PayableAccountId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Reference = table.Column<string>(type: "TEXT", maxLength: 30, nullable: true),
                    Date = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    DueDate = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    Memo = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    TotalMinor = table.Column<long>(type: "INTEGER", nullable: false),
                    AmountPaidMinor = table.Column<long>(type: "INTEGER", nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    JournalEntryId = table.Column<Guid>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Bills", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Bills_Accounts_PayableAccountId",
                        column: x => x.PayableAccountId,
                        principalTable: "Accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Bills_JournalEntries_JournalEntryId",
                        column: x => x.JournalEntryId,
                        principalTable: "JournalEntries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Bills_Vendors_VendorId",
                        column: x => x.VendorId,
                        principalTable: "Vendors",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "BillLines",
                columns: table => new
                {
                    LineNumber = table.Column<int>(type: "INTEGER", nullable: false),
                    BillId = table.Column<Guid>(type: "TEXT", nullable: false),
                    AccountId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Description = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    AmountMinor = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BillLines", x => new { x.BillId, x.LineNumber });
                    table.ForeignKey(
                        name: "FK_BillLines_Accounts_AccountId",
                        column: x => x.AccountId,
                        principalTable: "Accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_BillLines_Bills_BillId",
                        column: x => x.BillId,
                        principalTable: "Bills",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "BillPaymentApplications",
                columns: table => new
                {
                    BillId = table.Column<Guid>(type: "TEXT", nullable: false),
                    BillPaymentId = table.Column<Guid>(type: "TEXT", nullable: false),
                    AmountMinor = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BillPaymentApplications", x => new { x.BillPaymentId, x.BillId });
                    table.ForeignKey(
                        name: "FK_BillPaymentApplications_BillPayments_BillPaymentId",
                        column: x => x.BillPaymentId,
                        principalTable: "BillPayments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_BillPaymentApplications_Bills_BillId",
                        column: x => x.BillId,
                        principalTable: "Bills",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_BankTransactions_VendorId",
                table: "BankTransactions",
                column: "VendorId");

            migrationBuilder.CreateIndex(
                name: "IX_BillLines_AccountId",
                table: "BillLines",
                column: "AccountId");

            migrationBuilder.CreateIndex(
                name: "IX_BillPaymentApplications_BillId",
                table: "BillPaymentApplications",
                column: "BillId");

            migrationBuilder.CreateIndex(
                name: "IX_BillPayments_JournalEntryId",
                table: "BillPayments",
                column: "JournalEntryId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_BillPayments_PayableAccountId",
                table: "BillPayments",
                column: "PayableAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_BillPayments_PaymentAccountId_Reference",
                table: "BillPayments",
                columns: new[] { "PaymentAccountId", "Reference" });

            migrationBuilder.CreateIndex(
                name: "IX_BillPayments_VendorId_Date",
                table: "BillPayments",
                columns: new[] { "VendorId", "Date" });

            migrationBuilder.CreateIndex(
                name: "IX_Bills_JournalEntryId",
                table: "Bills",
                column: "JournalEntryId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Bills_PayableAccountId",
                table: "Bills",
                column: "PayableAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_Bills_VendorId_Date",
                table: "Bills",
                columns: new[] { "VendorId", "Date" });

            migrationBuilder.CreateIndex(
                name: "IX_Vendors_DefaultExpenseAccountId",
                table: "Vendors",
                column: "DefaultExpenseAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_Vendors_DisplayName",
                table: "Vendors",
                column: "DisplayName",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_BankTransactions_Vendors_VendorId",
                table: "BankTransactions",
                column: "VendorId",
                principalTable: "Vendors",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_BankTransactions_Vendors_VendorId",
                table: "BankTransactions");

            migrationBuilder.DropTable(
                name: "BillLines");

            migrationBuilder.DropTable(
                name: "BillPaymentApplications");

            migrationBuilder.DropTable(
                name: "BillPayments");

            migrationBuilder.DropTable(
                name: "Bills");

            migrationBuilder.DropTable(
                name: "Vendors");

            migrationBuilder.DropIndex(
                name: "IX_BankTransactions_VendorId",
                table: "BankTransactions");

            migrationBuilder.DropColumn(
                name: "VendorId",
                table: "BankTransactions");
        }
    }
}
