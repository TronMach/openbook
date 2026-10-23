using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenBook.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class SalesTax : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "TaxCodeId",
                table: "SalesReceipts",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TaxExemption",
                table: "SalesReceipts",
                type: "TEXT",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "TaxRateId",
                table: "SalesReceiptLines",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "Taxable",
                table: "SalesReceiptLines",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "Taxable",
                table: "Items",
                type: "INTEGER",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<Guid>(
                name: "TaxCodeId",
                table: "Invoices",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TaxExemption",
                table: "Invoices",
                type: "TEXT",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "TaxRateId",
                table: "InvoiceLines",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "Taxable",
                table: "InvoiceLines",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<Guid>(
                name: "TaxCodeId",
                table: "Estimates",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TaxExemption",
                table: "Estimates",
                type: "TEXT",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "TaxRateId",
                table: "EstimateLines",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "Taxable",
                table: "EstimateLines",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "ExemptionCertificate",
                table: "Customers",
                type: "TEXT",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<DateOnly>(
                name: "ExemptionExpiresOn",
                table: "Customers",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ExemptionReason",
                table: "Customers",
                type: "TEXT",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "TaxCodeId",
                table: "Customers",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "TaxCodeId",
                table: "CreditMemos",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TaxExemption",
                table: "CreditMemos",
                type: "TEXT",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "TaxRateId",
                table: "CreditMemoLines",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "Taxable",
                table: "CreditMemoLines",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "TaxAgencies",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    LiabilityAccountId = table.Column<Guid>(type: "TEXT", nullable: false),
                    RegistrationNumber = table.Column<string>(type: "TEXT", maxLength: 50, nullable: true),
                    FilingFrequency = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    IsActive = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TaxAgencies", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TaxAgencies_Accounts_LiabilityAccountId",
                        column: x => x.LiabilityAccountId,
                        principalTable: "Accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "TaxCodes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Description = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    IsActive = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TaxCodes", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SalesTaxPayments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    AgencyId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Date = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    PeriodEnd = table.Column<DateOnly>(type: "TEXT", nullable: true),
                    AmountMinor = table.Column<long>(type: "INTEGER", nullable: false),
                    PaymentAccountId = table.Column<Guid>(type: "TEXT", nullable: false),
                    LiabilityAccountId = table.Column<Guid>(type: "TEXT", nullable: false),
                    AdjustmentMinor = table.Column<long>(type: "INTEGER", nullable: false),
                    AdjustmentAccountId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Reference = table.Column<string>(type: "TEXT", maxLength: 30, nullable: true),
                    Memo = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    Status = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    JournalEntryId = table.Column<Guid>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SalesTaxPayments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SalesTaxPayments_Accounts_AdjustmentAccountId",
                        column: x => x.AdjustmentAccountId,
                        principalTable: "Accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SalesTaxPayments_Accounts_LiabilityAccountId",
                        column: x => x.LiabilityAccountId,
                        principalTable: "Accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SalesTaxPayments_Accounts_PaymentAccountId",
                        column: x => x.PaymentAccountId,
                        principalTable: "Accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SalesTaxPayments_JournalEntries_JournalEntryId",
                        column: x => x.JournalEntryId,
                        principalTable: "JournalEntries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SalesTaxPayments_TaxAgencies_AgencyId",
                        column: x => x.AgencyId,
                        principalTable: "TaxAgencies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "TaxRates",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    AgencyId = table.Column<Guid>(type: "TEXT", nullable: false),
                    PercentTenThousandths = table.Column<long>(type: "INTEGER", nullable: false),
                    IsActive = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TaxRates", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TaxRates_TaxAgencies_AgencyId",
                        column: x => x.AgencyId,
                        principalTable: "TaxAgencies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "TaxCodeRates",
                columns: table => new
                {
                    Position = table.Column<int>(type: "INTEGER", nullable: false),
                    TaxCodeId = table.Column<Guid>(type: "TEXT", nullable: false),
                    TaxRateId = table.Column<Guid>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TaxCodeRates", x => new { x.TaxCodeId, x.Position });
                    table.ForeignKey(
                        name: "FK_TaxCodeRates_TaxCodes_TaxCodeId",
                        column: x => x.TaxCodeId,
                        principalTable: "TaxCodes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_TaxCodeRates_TaxRates_TaxRateId",
                        column: x => x.TaxRateId,
                        principalTable: "TaxRates",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SalesReceipts_TaxCodeId",
                table: "SalesReceipts",
                column: "TaxCodeId");

            migrationBuilder.CreateIndex(
                name: "IX_SalesReceiptLines_TaxRateId",
                table: "SalesReceiptLines",
                column: "TaxRateId");

            migrationBuilder.CreateIndex(
                name: "IX_Invoices_TaxCodeId",
                table: "Invoices",
                column: "TaxCodeId");

            migrationBuilder.CreateIndex(
                name: "IX_InvoiceLines_TaxRateId",
                table: "InvoiceLines",
                column: "TaxRateId");

            migrationBuilder.CreateIndex(
                name: "IX_Estimates_TaxCodeId",
                table: "Estimates",
                column: "TaxCodeId");

            migrationBuilder.CreateIndex(
                name: "IX_EstimateLines_TaxRateId",
                table: "EstimateLines",
                column: "TaxRateId");

            migrationBuilder.CreateIndex(
                name: "IX_Customers_TaxCodeId",
                table: "Customers",
                column: "TaxCodeId");

            migrationBuilder.CreateIndex(
                name: "IX_CreditMemos_TaxCodeId",
                table: "CreditMemos",
                column: "TaxCodeId");

            migrationBuilder.CreateIndex(
                name: "IX_CreditMemoLines_TaxRateId",
                table: "CreditMemoLines",
                column: "TaxRateId");

            migrationBuilder.CreateIndex(
                name: "IX_SalesTaxPayments_AdjustmentAccountId",
                table: "SalesTaxPayments",
                column: "AdjustmentAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_SalesTaxPayments_AgencyId_Date",
                table: "SalesTaxPayments",
                columns: new[] { "AgencyId", "Date" });

            migrationBuilder.CreateIndex(
                name: "IX_SalesTaxPayments_JournalEntryId",
                table: "SalesTaxPayments",
                column: "JournalEntryId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SalesTaxPayments_LiabilityAccountId",
                table: "SalesTaxPayments",
                column: "LiabilityAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_SalesTaxPayments_PaymentAccountId",
                table: "SalesTaxPayments",
                column: "PaymentAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_TaxAgencies_LiabilityAccountId",
                table: "TaxAgencies",
                column: "LiabilityAccountId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TaxAgencies_Name",
                table: "TaxAgencies",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TaxCodeRates_TaxRateId",
                table: "TaxCodeRates",
                column: "TaxRateId");

            migrationBuilder.CreateIndex(
                name: "IX_TaxCodes_Name",
                table: "TaxCodes",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TaxRates_AgencyId",
                table: "TaxRates",
                column: "AgencyId");

            migrationBuilder.CreateIndex(
                name: "IX_TaxRates_Name",
                table: "TaxRates",
                column: "Name",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_CreditMemoLines_TaxRates_TaxRateId",
                table: "CreditMemoLines",
                column: "TaxRateId",
                principalTable: "TaxRates",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_CreditMemos_TaxCodes_TaxCodeId",
                table: "CreditMemos",
                column: "TaxCodeId",
                principalTable: "TaxCodes",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Customers_TaxCodes_TaxCodeId",
                table: "Customers",
                column: "TaxCodeId",
                principalTable: "TaxCodes",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_EstimateLines_TaxRates_TaxRateId",
                table: "EstimateLines",
                column: "TaxRateId",
                principalTable: "TaxRates",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Estimates_TaxCodes_TaxCodeId",
                table: "Estimates",
                column: "TaxCodeId",
                principalTable: "TaxCodes",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_InvoiceLines_TaxRates_TaxRateId",
                table: "InvoiceLines",
                column: "TaxRateId",
                principalTable: "TaxRates",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Invoices_TaxCodes_TaxCodeId",
                table: "Invoices",
                column: "TaxCodeId",
                principalTable: "TaxCodes",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_SalesReceiptLines_TaxRates_TaxRateId",
                table: "SalesReceiptLines",
                column: "TaxRateId",
                principalTable: "TaxRates",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_SalesReceipts_TaxCodes_TaxCodeId",
                table: "SalesReceipts",
                column: "TaxCodeId",
                principalTable: "TaxCodes",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_CreditMemoLines_TaxRates_TaxRateId",
                table: "CreditMemoLines");

            migrationBuilder.DropForeignKey(
                name: "FK_CreditMemos_TaxCodes_TaxCodeId",
                table: "CreditMemos");

            migrationBuilder.DropForeignKey(
                name: "FK_Customers_TaxCodes_TaxCodeId",
                table: "Customers");

            migrationBuilder.DropForeignKey(
                name: "FK_EstimateLines_TaxRates_TaxRateId",
                table: "EstimateLines");

            migrationBuilder.DropForeignKey(
                name: "FK_Estimates_TaxCodes_TaxCodeId",
                table: "Estimates");

            migrationBuilder.DropForeignKey(
                name: "FK_InvoiceLines_TaxRates_TaxRateId",
                table: "InvoiceLines");

            migrationBuilder.DropForeignKey(
                name: "FK_Invoices_TaxCodes_TaxCodeId",
                table: "Invoices");

            migrationBuilder.DropForeignKey(
                name: "FK_SalesReceiptLines_TaxRates_TaxRateId",
                table: "SalesReceiptLines");

            migrationBuilder.DropForeignKey(
                name: "FK_SalesReceipts_TaxCodes_TaxCodeId",
                table: "SalesReceipts");

            migrationBuilder.DropTable(
                name: "SalesTaxPayments");

            migrationBuilder.DropTable(
                name: "TaxCodeRates");

            migrationBuilder.DropTable(
                name: "TaxCodes");

            migrationBuilder.DropTable(
                name: "TaxRates");

            migrationBuilder.DropTable(
                name: "TaxAgencies");

            migrationBuilder.DropIndex(
                name: "IX_SalesReceipts_TaxCodeId",
                table: "SalesReceipts");

            migrationBuilder.DropIndex(
                name: "IX_SalesReceiptLines_TaxRateId",
                table: "SalesReceiptLines");

            migrationBuilder.DropIndex(
                name: "IX_Invoices_TaxCodeId",
                table: "Invoices");

            migrationBuilder.DropIndex(
                name: "IX_InvoiceLines_TaxRateId",
                table: "InvoiceLines");

            migrationBuilder.DropIndex(
                name: "IX_Estimates_TaxCodeId",
                table: "Estimates");

            migrationBuilder.DropIndex(
                name: "IX_EstimateLines_TaxRateId",
                table: "EstimateLines");

            migrationBuilder.DropIndex(
                name: "IX_Customers_TaxCodeId",
                table: "Customers");

            migrationBuilder.DropIndex(
                name: "IX_CreditMemos_TaxCodeId",
                table: "CreditMemos");

            migrationBuilder.DropIndex(
                name: "IX_CreditMemoLines_TaxRateId",
                table: "CreditMemoLines");

            migrationBuilder.DropColumn(
                name: "TaxCodeId",
                table: "SalesReceipts");

            migrationBuilder.DropColumn(
                name: "TaxExemption",
                table: "SalesReceipts");

            migrationBuilder.DropColumn(
                name: "TaxRateId",
                table: "SalesReceiptLines");

            migrationBuilder.DropColumn(
                name: "Taxable",
                table: "SalesReceiptLines");

            migrationBuilder.DropColumn(
                name: "Taxable",
                table: "Items");

            migrationBuilder.DropColumn(
                name: "TaxCodeId",
                table: "Invoices");

            migrationBuilder.DropColumn(
                name: "TaxExemption",
                table: "Invoices");

            migrationBuilder.DropColumn(
                name: "TaxRateId",
                table: "InvoiceLines");

            migrationBuilder.DropColumn(
                name: "Taxable",
                table: "InvoiceLines");

            migrationBuilder.DropColumn(
                name: "TaxCodeId",
                table: "Estimates");

            migrationBuilder.DropColumn(
                name: "TaxExemption",
                table: "Estimates");

            migrationBuilder.DropColumn(
                name: "TaxRateId",
                table: "EstimateLines");

            migrationBuilder.DropColumn(
                name: "Taxable",
                table: "EstimateLines");

            migrationBuilder.DropColumn(
                name: "ExemptionCertificate",
                table: "Customers");

            migrationBuilder.DropColumn(
                name: "ExemptionExpiresOn",
                table: "Customers");

            migrationBuilder.DropColumn(
                name: "ExemptionReason",
                table: "Customers");

            migrationBuilder.DropColumn(
                name: "TaxCodeId",
                table: "Customers");

            migrationBuilder.DropColumn(
                name: "TaxCodeId",
                table: "CreditMemos");

            migrationBuilder.DropColumn(
                name: "TaxExemption",
                table: "CreditMemos");

            migrationBuilder.DropColumn(
                name: "TaxRateId",
                table: "CreditMemoLines");

            migrationBuilder.DropColumn(
                name: "Taxable",
                table: "CreditMemoLines");
        }
    }
}
