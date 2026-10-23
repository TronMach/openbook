using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenBook.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Sales : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Customers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    DisplayName = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    CompanyName = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    Email = table.Column<string>(type: "TEXT", maxLength: 320, nullable: true),
                    Phone = table.Column<string>(type: "TEXT", maxLength: 50, nullable: true),
                    BillingAddress = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    ShippingAddress = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    PaymentTermsDays = table.Column<int>(type: "INTEGER", nullable: false),
                    Notes = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    IsActive = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Customers", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Items",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Type = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    Description = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    RateTenThousandths = table.Column<long>(type: "INTEGER", nullable: false),
                    IncomeAccountId = table.Column<Guid>(type: "TEXT", nullable: false),
                    IsActive = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Items", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Items_Accounts_IncomeAccountId",
                        column: x => x.IncomeAccountId,
                        principalTable: "Accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "CustomerPayments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    CustomerId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Date = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    AmountMinor = table.Column<long>(type: "INTEGER", nullable: false),
                    DepositAccountId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ReceivableAccountId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Reference = table.Column<string>(type: "TEXT", maxLength: 30, nullable: true),
                    Memo = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    Status = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    JournalEntryId = table.Column<Guid>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CustomerPayments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CustomerPayments_Accounts_DepositAccountId",
                        column: x => x.DepositAccountId,
                        principalTable: "Accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CustomerPayments_Accounts_ReceivableAccountId",
                        column: x => x.ReceivableAccountId,
                        principalTable: "Accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CustomerPayments_Customers_CustomerId",
                        column: x => x.CustomerId,
                        principalTable: "Customers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CustomerPayments_JournalEntries_JournalEntryId",
                        column: x => x.JournalEntryId,
                        principalTable: "JournalEntries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Invoices",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Number = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    CustomerId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ReceivableAccountId = table.Column<Guid>(type: "TEXT", nullable: false),
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
                    table.PrimaryKey("PK_Invoices", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Invoices_Accounts_ReceivableAccountId",
                        column: x => x.ReceivableAccountId,
                        principalTable: "Accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Invoices_Customers_CustomerId",
                        column: x => x.CustomerId,
                        principalTable: "Customers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Invoices_JournalEntries_JournalEntryId",
                        column: x => x.JournalEntryId,
                        principalTable: "JournalEntries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "SalesReceipts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Number = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    CustomerId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Date = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    DepositAccountId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Reference = table.Column<string>(type: "TEXT", maxLength: 30, nullable: true),
                    Memo = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    TotalMinor = table.Column<long>(type: "INTEGER", nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    JournalEntryId = table.Column<Guid>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SalesReceipts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SalesReceipts_Accounts_DepositAccountId",
                        column: x => x.DepositAccountId,
                        principalTable: "Accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SalesReceipts_Customers_CustomerId",
                        column: x => x.CustomerId,
                        principalTable: "Customers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SalesReceipts_JournalEntries_JournalEntryId",
                        column: x => x.JournalEntryId,
                        principalTable: "JournalEntries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "InvoiceLines",
                columns: table => new
                {
                    LineNumber = table.Column<int>(type: "INTEGER", nullable: false),
                    InvoiceId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ItemId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Description = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    QuantityTenThousandths = table.Column<long>(type: "INTEGER", nullable: false),
                    RateTenThousandths = table.Column<long>(type: "INTEGER", nullable: false),
                    AmountMinor = table.Column<long>(type: "INTEGER", nullable: false),
                    IncomeAccountId = table.Column<Guid>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InvoiceLines", x => new { x.InvoiceId, x.LineNumber });
                    table.ForeignKey(
                        name: "FK_InvoiceLines_Accounts_IncomeAccountId",
                        column: x => x.IncomeAccountId,
                        principalTable: "Accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_InvoiceLines_Invoices_InvoiceId",
                        column: x => x.InvoiceId,
                        principalTable: "Invoices",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_InvoiceLines_Items_ItemId",
                        column: x => x.ItemId,
                        principalTable: "Items",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "PaymentApplications",
                columns: table => new
                {
                    InvoiceId = table.Column<Guid>(type: "TEXT", nullable: false),
                    CustomerPaymentId = table.Column<Guid>(type: "TEXT", nullable: false),
                    AmountMinor = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PaymentApplications", x => new { x.CustomerPaymentId, x.InvoiceId });
                    table.ForeignKey(
                        name: "FK_PaymentApplications_CustomerPayments_CustomerPaymentId",
                        column: x => x.CustomerPaymentId,
                        principalTable: "CustomerPayments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_PaymentApplications_Invoices_InvoiceId",
                        column: x => x.InvoiceId,
                        principalTable: "Invoices",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "SalesReceiptLines",
                columns: table => new
                {
                    LineNumber = table.Column<int>(type: "INTEGER", nullable: false),
                    SalesReceiptId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ItemId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Description = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    QuantityTenThousandths = table.Column<long>(type: "INTEGER", nullable: false),
                    RateTenThousandths = table.Column<long>(type: "INTEGER", nullable: false),
                    AmountMinor = table.Column<long>(type: "INTEGER", nullable: false),
                    IncomeAccountId = table.Column<Guid>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SalesReceiptLines", x => new { x.SalesReceiptId, x.LineNumber });
                    table.ForeignKey(
                        name: "FK_SalesReceiptLines_Accounts_IncomeAccountId",
                        column: x => x.IncomeAccountId,
                        principalTable: "Accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SalesReceiptLines_Items_ItemId",
                        column: x => x.ItemId,
                        principalTable: "Items",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SalesReceiptLines_SalesReceipts_SalesReceiptId",
                        column: x => x.SalesReceiptId,
                        principalTable: "SalesReceipts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CustomerPayments_CustomerId_Date",
                table: "CustomerPayments",
                columns: new[] { "CustomerId", "Date" });

            migrationBuilder.CreateIndex(
                name: "IX_CustomerPayments_DepositAccountId",
                table: "CustomerPayments",
                column: "DepositAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_CustomerPayments_JournalEntryId",
                table: "CustomerPayments",
                column: "JournalEntryId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CustomerPayments_ReceivableAccountId",
                table: "CustomerPayments",
                column: "ReceivableAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_Customers_DisplayName",
                table: "Customers",
                column: "DisplayName",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_InvoiceLines_IncomeAccountId",
                table: "InvoiceLines",
                column: "IncomeAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_InvoiceLines_ItemId",
                table: "InvoiceLines",
                column: "ItemId");

            migrationBuilder.CreateIndex(
                name: "IX_Invoices_CustomerId_Date",
                table: "Invoices",
                columns: new[] { "CustomerId", "Date" });

            migrationBuilder.CreateIndex(
                name: "IX_Invoices_JournalEntryId",
                table: "Invoices",
                column: "JournalEntryId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Invoices_Number",
                table: "Invoices",
                column: "Number",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Invoices_ReceivableAccountId",
                table: "Invoices",
                column: "ReceivableAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_Items_IncomeAccountId",
                table: "Items",
                column: "IncomeAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_Items_Name",
                table: "Items",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PaymentApplications_InvoiceId",
                table: "PaymentApplications",
                column: "InvoiceId");

            migrationBuilder.CreateIndex(
                name: "IX_SalesReceiptLines_IncomeAccountId",
                table: "SalesReceiptLines",
                column: "IncomeAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_SalesReceiptLines_ItemId",
                table: "SalesReceiptLines",
                column: "ItemId");

            migrationBuilder.CreateIndex(
                name: "IX_SalesReceipts_CustomerId",
                table: "SalesReceipts",
                column: "CustomerId");

            migrationBuilder.CreateIndex(
                name: "IX_SalesReceipts_DepositAccountId",
                table: "SalesReceipts",
                column: "DepositAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_SalesReceipts_JournalEntryId",
                table: "SalesReceipts",
                column: "JournalEntryId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SalesReceipts_Number",
                table: "SalesReceipts",
                column: "Number",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "InvoiceLines");

            migrationBuilder.DropTable(
                name: "PaymentApplications");

            migrationBuilder.DropTable(
                name: "SalesReceiptLines");

            migrationBuilder.DropTable(
                name: "CustomerPayments");

            migrationBuilder.DropTable(
                name: "Invoices");

            migrationBuilder.DropTable(
                name: "Items");

            migrationBuilder.DropTable(
                name: "SalesReceipts");

            migrationBuilder.DropTable(
                name: "Customers");
        }
    }
}
