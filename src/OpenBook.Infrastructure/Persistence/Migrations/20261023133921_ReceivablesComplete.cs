using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenBook.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ReceivablesComplete : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "DepositId",
                table: "SalesReceipts",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "LastEmailedAt",
                table: "SalesReceipts",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Kind",
                table: "SalesReceiptLines",
                type: "TEXT",
                maxLength: 20,
                nullable: false,
                defaultValue: "Item");

            migrationBuilder.AddColumn<DateOnly>(
                name: "AppliedOn",
                table: "PaymentApplications",
                type: "TEXT",
                nullable: false,
                defaultValue: new DateOnly(1, 1, 1));

            migrationBuilder.AddColumn<long>(
                name: "DiscountMinor",
                table: "PaymentApplications",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<DateOnly>(
                name: "EarlyPaymentDiscountDate",
                table: "Invoices",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "EarlyPaymentDiscountPercentTenThousandths",
                table: "Invoices",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<Guid>(
                name: "EstimateId",
                table: "Invoices",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "LastEmailedAt",
                table: "Invoices",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "RecurringInvoiceId",
                table: "Invoices",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Kind",
                table: "InvoiceLines",
                type: "TEXT",
                maxLength: 20,
                nullable: false,
                defaultValue: "Item");

            migrationBuilder.AddColumn<int>(
                name: "EarlyPaymentDiscountDays",
                table: "Customers",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<long>(
                name: "EarlyPaymentDiscountPercentTenThousandths",
                table: "Customers",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<long>(
                name: "AmountRefundedMinor",
                table: "CustomerPayments",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<Guid>(
                name: "DepositId",
                table: "CustomerPayments",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "FormAccentColor",
                table: "Companies",
                type: "TEXT",
                maxLength: 7,
                nullable: false,
                defaultValue: "#1f6f5c");

            migrationBuilder.AddColumn<string>(
                name: "FormFooter",
                table: "Companies",
                type: "TEXT",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "FormTemplate",
                table: "Companies",
                type: "TEXT",
                maxLength: 20,
                nullable: false,
                defaultValue: "Classic");

            // Existing lines without an item are notes; payments applied on their own date.
            migrationBuilder.Sql("""UPDATE "InvoiceLines" SET "Kind" = 'Note' WHERE "ItemId" IS NULL;""");
            migrationBuilder.Sql("""UPDATE "SalesReceiptLines" SET "Kind" = 'Note' WHERE "ItemId" IS NULL;""");
            migrationBuilder.Sql("""
                UPDATE "PaymentApplications"
                SET "AppliedOn" = (SELECT p."Date" FROM "CustomerPayments" AS p WHERE p."Id" = "PaymentApplications"."CustomerPaymentId");
                """);

            migrationBuilder.CreateTable(
                name: "BankDeposits",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    BankAccountId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Date = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    Memo = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    TotalMinor = table.Column<long>(type: "INTEGER", nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    JournalEntryId = table.Column<Guid>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BankDeposits", x => x.Id);
                    table.ForeignKey(
                        name: "FK_BankDeposits_Accounts_BankAccountId",
                        column: x => x.BankAccountId,
                        principalTable: "Accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_BankDeposits_JournalEntries_JournalEntryId",
                        column: x => x.JournalEntryId,
                        principalTable: "JournalEntries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "CreditMemos",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Number = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    CustomerId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ReceivableAccountId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Date = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    Memo = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    TotalMinor = table.Column<long>(type: "INTEGER", nullable: false),
                    AmountRefundedMinor = table.Column<long>(type: "INTEGER", nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    JournalEntryId = table.Column<Guid>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CreditMemos", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CreditMemos_Accounts_ReceivableAccountId",
                        column: x => x.ReceivableAccountId,
                        principalTable: "Accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CreditMemos_Customers_CustomerId",
                        column: x => x.CustomerId,
                        principalTable: "Customers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CreditMemos_JournalEntries_JournalEntryId",
                        column: x => x.JournalEntryId,
                        principalTable: "JournalEntries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "EmailSettings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false),
                    Delivery = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    Host = table.Column<string>(type: "TEXT", maxLength: 255, nullable: true),
                    Port = table.Column<int>(type: "INTEGER", nullable: false),
                    Security = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    Username = table.Column<string>(type: "TEXT", maxLength: 320, nullable: true),
                    ProtectedPassword = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    FromAddress = table.Column<string>(type: "TEXT", maxLength: 320, nullable: true),
                    FromName = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    BccAddress = table.Column<string>(type: "TEXT", maxLength: 320, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EmailSettings", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Estimates",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Number = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    CustomerId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Date = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    ExpirationDate = table.Column<DateOnly>(type: "TEXT", nullable: true),
                    Memo = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    TotalMinor = table.Column<long>(type: "INTEGER", nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    InvoiceId = table.Column<Guid>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    LastEmailedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Estimates", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Estimates_Customers_CustomerId",
                        column: x => x.CustomerId,
                        principalTable: "Customers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "RecurringInvoices",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    CustomerId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Memo = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    Frequency = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    Interval = table.Column<int>(type: "INTEGER", nullable: false),
                    StartDate = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    EndDate = table.Column<DateOnly>(type: "TEXT", nullable: true),
                    MaxOccurrences = table.Column<int>(type: "INTEGER", nullable: true),
                    OccurrencesPosted = table.Column<int>(type: "INTEGER", nullable: false),
                    AutoCreate = table.Column<bool>(type: "INTEGER", nullable: false),
                    AutoEmail = table.Column<bool>(type: "INTEGER", nullable: false),
                    IsActive = table.Column<bool>(type: "INTEGER", nullable: false),
                    LastInvoiceId = table.Column<Guid>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RecurringInvoices", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RecurringInvoices_Customers_CustomerId",
                        column: x => x.CustomerId,
                        principalTable: "Customers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "BankDepositLines",
                columns: table => new
                {
                    LineNumber = table.Column<int>(type: "INTEGER", nullable: false),
                    BankDepositId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Kind = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    SourceId = table.Column<Guid>(type: "TEXT", nullable: false),
                    FromAccountId = table.Column<Guid>(type: "TEXT", nullable: false),
                    AmountMinor = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BankDepositLines", x => new { x.BankDepositId, x.LineNumber });
                    table.ForeignKey(
                        name: "FK_BankDepositLines_Accounts_FromAccountId",
                        column: x => x.FromAccountId,
                        principalTable: "Accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_BankDepositLines_BankDeposits_BankDepositId",
                        column: x => x.BankDepositId,
                        principalTable: "BankDeposits",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "CreditApplications",
                columns: table => new
                {
                    InvoiceId = table.Column<Guid>(type: "TEXT", nullable: false),
                    CreditMemoId = table.Column<Guid>(type: "TEXT", nullable: false),
                    AmountMinor = table.Column<long>(type: "INTEGER", nullable: false),
                    AppliedOn = table.Column<DateOnly>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CreditApplications", x => new { x.CreditMemoId, x.InvoiceId });
                    table.ForeignKey(
                        name: "FK_CreditApplications_CreditMemos_CreditMemoId",
                        column: x => x.CreditMemoId,
                        principalTable: "CreditMemos",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_CreditApplications_Invoices_InvoiceId",
                        column: x => x.InvoiceId,
                        principalTable: "Invoices",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "CreditMemoLines",
                columns: table => new
                {
                    LineNumber = table.Column<int>(type: "INTEGER", nullable: false),
                    CreditMemoId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Kind = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    ItemId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Description = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    QuantityTenThousandths = table.Column<long>(type: "INTEGER", nullable: false),
                    RateTenThousandths = table.Column<long>(type: "INTEGER", nullable: false),
                    AmountMinor = table.Column<long>(type: "INTEGER", nullable: false),
                    IncomeAccountId = table.Column<Guid>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CreditMemoLines", x => new { x.CreditMemoId, x.LineNumber });
                    table.ForeignKey(
                        name: "FK_CreditMemoLines_Accounts_IncomeAccountId",
                        column: x => x.IncomeAccountId,
                        principalTable: "Accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CreditMemoLines_CreditMemos_CreditMemoId",
                        column: x => x.CreditMemoId,
                        principalTable: "CreditMemos",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_CreditMemoLines_Items_ItemId",
                        column: x => x.ItemId,
                        principalTable: "Items",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "CustomerRefunds",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    CustomerId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Date = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    AmountMinor = table.Column<long>(type: "INTEGER", nullable: false),
                    PaymentAccountId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ReceivableAccountId = table.Column<Guid>(type: "TEXT", nullable: false),
                    CreditMemoId = table.Column<Guid>(type: "TEXT", nullable: true),
                    CustomerPaymentId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Reference = table.Column<string>(type: "TEXT", maxLength: 30, nullable: true),
                    Memo = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    Status = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    JournalEntryId = table.Column<Guid>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CustomerRefunds", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CustomerRefunds_Accounts_PaymentAccountId",
                        column: x => x.PaymentAccountId,
                        principalTable: "Accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CustomerRefunds_Accounts_ReceivableAccountId",
                        column: x => x.ReceivableAccountId,
                        principalTable: "Accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CustomerRefunds_CreditMemos_CreditMemoId",
                        column: x => x.CreditMemoId,
                        principalTable: "CreditMemos",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CustomerRefunds_CustomerPayments_CustomerPaymentId",
                        column: x => x.CustomerPaymentId,
                        principalTable: "CustomerPayments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CustomerRefunds_Customers_CustomerId",
                        column: x => x.CustomerId,
                        principalTable: "Customers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CustomerRefunds_JournalEntries_JournalEntryId",
                        column: x => x.JournalEntryId,
                        principalTable: "JournalEntries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "EstimateLines",
                columns: table => new
                {
                    LineNumber = table.Column<int>(type: "INTEGER", nullable: false),
                    EstimateId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Kind = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    ItemId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Description = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    QuantityTenThousandths = table.Column<long>(type: "INTEGER", nullable: false),
                    RateTenThousandths = table.Column<long>(type: "INTEGER", nullable: false),
                    AmountMinor = table.Column<long>(type: "INTEGER", nullable: false),
                    IncomeAccountId = table.Column<Guid>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EstimateLines", x => new { x.EstimateId, x.LineNumber });
                    table.ForeignKey(
                        name: "FK_EstimateLines_Accounts_IncomeAccountId",
                        column: x => x.IncomeAccountId,
                        principalTable: "Accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_EstimateLines_Estimates_EstimateId",
                        column: x => x.EstimateId,
                        principalTable: "Estimates",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_EstimateLines_Items_ItemId",
                        column: x => x.ItemId,
                        principalTable: "Items",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "RecurringInvoiceLines",
                columns: table => new
                {
                    LineNumber = table.Column<int>(type: "INTEGER", nullable: false),
                    RecurringInvoiceId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Kind = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    ItemId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Description = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    QuantityTenThousandths = table.Column<long>(type: "INTEGER", nullable: false),
                    RateTenThousandths = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RecurringInvoiceLines", x => new { x.RecurringInvoiceId, x.LineNumber });
                    table.ForeignKey(
                        name: "FK_RecurringInvoiceLines_Items_ItemId",
                        column: x => x.ItemId,
                        principalTable: "Items",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_RecurringInvoiceLines_RecurringInvoices_RecurringInvoiceId",
                        column: x => x.RecurringInvoiceId,
                        principalTable: "RecurringInvoices",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SalesReceipts_DepositId",
                table: "SalesReceipts",
                column: "DepositId");

            migrationBuilder.CreateIndex(
                name: "IX_Invoices_EstimateId",
                table: "Invoices",
                column: "EstimateId");

            migrationBuilder.CreateIndex(
                name: "IX_Invoices_RecurringInvoiceId",
                table: "Invoices",
                column: "RecurringInvoiceId");

            migrationBuilder.CreateIndex(
                name: "IX_CustomerPayments_DepositId",
                table: "CustomerPayments",
                column: "DepositId");

            migrationBuilder.CreateIndex(
                name: "IX_BankDepositLines_FromAccountId",
                table: "BankDepositLines",
                column: "FromAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_BankDepositLines_SourceId",
                table: "BankDepositLines",
                column: "SourceId");

            migrationBuilder.CreateIndex(
                name: "IX_BankDeposits_BankAccountId",
                table: "BankDeposits",
                column: "BankAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_BankDeposits_JournalEntryId",
                table: "BankDeposits",
                column: "JournalEntryId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CreditApplications_InvoiceId",
                table: "CreditApplications",
                column: "InvoiceId");

            migrationBuilder.CreateIndex(
                name: "IX_CreditMemoLines_IncomeAccountId",
                table: "CreditMemoLines",
                column: "IncomeAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_CreditMemoLines_ItemId",
                table: "CreditMemoLines",
                column: "ItemId");

            migrationBuilder.CreateIndex(
                name: "IX_CreditMemos_CustomerId_Date",
                table: "CreditMemos",
                columns: new[] { "CustomerId", "Date" });

            migrationBuilder.CreateIndex(
                name: "IX_CreditMemos_JournalEntryId",
                table: "CreditMemos",
                column: "JournalEntryId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CreditMemos_Number",
                table: "CreditMemos",
                column: "Number",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CreditMemos_ReceivableAccountId",
                table: "CreditMemos",
                column: "ReceivableAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_CustomerRefunds_CreditMemoId",
                table: "CustomerRefunds",
                column: "CreditMemoId");

            migrationBuilder.CreateIndex(
                name: "IX_CustomerRefunds_CustomerId_Date",
                table: "CustomerRefunds",
                columns: new[] { "CustomerId", "Date" });

            migrationBuilder.CreateIndex(
                name: "IX_CustomerRefunds_CustomerPaymentId",
                table: "CustomerRefunds",
                column: "CustomerPaymentId");

            migrationBuilder.CreateIndex(
                name: "IX_CustomerRefunds_JournalEntryId",
                table: "CustomerRefunds",
                column: "JournalEntryId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CustomerRefunds_PaymentAccountId",
                table: "CustomerRefunds",
                column: "PaymentAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_CustomerRefunds_ReceivableAccountId",
                table: "CustomerRefunds",
                column: "ReceivableAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_EstimateLines_IncomeAccountId",
                table: "EstimateLines",
                column: "IncomeAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_EstimateLines_ItemId",
                table: "EstimateLines",
                column: "ItemId");

            migrationBuilder.CreateIndex(
                name: "IX_Estimates_CustomerId_Date",
                table: "Estimates",
                columns: new[] { "CustomerId", "Date" });

            migrationBuilder.CreateIndex(
                name: "IX_Estimates_Number",
                table: "Estimates",
                column: "Number",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RecurringInvoiceLines_ItemId",
                table: "RecurringInvoiceLines",
                column: "ItemId");

            migrationBuilder.CreateIndex(
                name: "IX_RecurringInvoices_CustomerId",
                table: "RecurringInvoices",
                column: "CustomerId");

            migrationBuilder.AddForeignKey(
                name: "FK_CustomerPayments_BankDeposits_DepositId",
                table: "CustomerPayments",
                column: "DepositId",
                principalTable: "BankDeposits",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Invoices_Estimates_EstimateId",
                table: "Invoices",
                column: "EstimateId",
                principalTable: "Estimates",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Invoices_RecurringInvoices_RecurringInvoiceId",
                table: "Invoices",
                column: "RecurringInvoiceId",
                principalTable: "RecurringInvoices",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_SalesReceipts_BankDeposits_DepositId",
                table: "SalesReceipts",
                column: "DepositId",
                principalTable: "BankDeposits",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_CustomerPayments_BankDeposits_DepositId",
                table: "CustomerPayments");

            migrationBuilder.DropForeignKey(
                name: "FK_Invoices_Estimates_EstimateId",
                table: "Invoices");

            migrationBuilder.DropForeignKey(
                name: "FK_Invoices_RecurringInvoices_RecurringInvoiceId",
                table: "Invoices");

            migrationBuilder.DropForeignKey(
                name: "FK_SalesReceipts_BankDeposits_DepositId",
                table: "SalesReceipts");

            migrationBuilder.DropTable(
                name: "BankDepositLines");

            migrationBuilder.DropTable(
                name: "CreditApplications");

            migrationBuilder.DropTable(
                name: "CreditMemoLines");

            migrationBuilder.DropTable(
                name: "CustomerRefunds");

            migrationBuilder.DropTable(
                name: "EmailSettings");

            migrationBuilder.DropTable(
                name: "EstimateLines");

            migrationBuilder.DropTable(
                name: "RecurringInvoiceLines");

            migrationBuilder.DropTable(
                name: "BankDeposits");

            migrationBuilder.DropTable(
                name: "CreditMemos");

            migrationBuilder.DropTable(
                name: "Estimates");

            migrationBuilder.DropTable(
                name: "RecurringInvoices");

            migrationBuilder.DropIndex(
                name: "IX_SalesReceipts_DepositId",
                table: "SalesReceipts");

            migrationBuilder.DropIndex(
                name: "IX_Invoices_EstimateId",
                table: "Invoices");

            migrationBuilder.DropIndex(
                name: "IX_Invoices_RecurringInvoiceId",
                table: "Invoices");

            migrationBuilder.DropIndex(
                name: "IX_CustomerPayments_DepositId",
                table: "CustomerPayments");

            migrationBuilder.DropColumn(
                name: "DepositId",
                table: "SalesReceipts");

            migrationBuilder.DropColumn(
                name: "LastEmailedAt",
                table: "SalesReceipts");

            migrationBuilder.DropColumn(
                name: "Kind",
                table: "SalesReceiptLines");

            migrationBuilder.DropColumn(
                name: "AppliedOn",
                table: "PaymentApplications");

            migrationBuilder.DropColumn(
                name: "DiscountMinor",
                table: "PaymentApplications");

            migrationBuilder.DropColumn(
                name: "EarlyPaymentDiscountDate",
                table: "Invoices");

            migrationBuilder.DropColumn(
                name: "EarlyPaymentDiscountPercentTenThousandths",
                table: "Invoices");

            migrationBuilder.DropColumn(
                name: "EstimateId",
                table: "Invoices");

            migrationBuilder.DropColumn(
                name: "LastEmailedAt",
                table: "Invoices");

            migrationBuilder.DropColumn(
                name: "RecurringInvoiceId",
                table: "Invoices");

            migrationBuilder.DropColumn(
                name: "Kind",
                table: "InvoiceLines");

            migrationBuilder.DropColumn(
                name: "EarlyPaymentDiscountDays",
                table: "Customers");

            migrationBuilder.DropColumn(
                name: "EarlyPaymentDiscountPercentTenThousandths",
                table: "Customers");

            migrationBuilder.DropColumn(
                name: "AmountRefundedMinor",
                table: "CustomerPayments");

            migrationBuilder.DropColumn(
                name: "DepositId",
                table: "CustomerPayments");

            migrationBuilder.DropColumn(
                name: "FormAccentColor",
                table: "Companies");

            migrationBuilder.DropColumn(
                name: "FormFooter",
                table: "Companies");

            migrationBuilder.DropColumn(
                name: "FormTemplate",
                table: "Companies");
        }
    }
}
