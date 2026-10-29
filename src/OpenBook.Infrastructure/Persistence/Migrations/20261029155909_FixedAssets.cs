using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenBook.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class FixedAssets : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "DepreciationRuns",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Date = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    JournalEntryId = table.Column<Guid>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DepreciationRuns", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DepreciationRuns_JournalEntries_JournalEntryId",
                        column: x => x.JournalEntryId,
                        principalTable: "JournalEntries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "FixedAssets",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Number = table.Column<string>(type: "TEXT", maxLength: 30, nullable: true),
                    Description = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    AssetAccountId = table.Column<Guid>(type: "TEXT", nullable: false),
                    AccumulatedAccountId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ExpenseAccountId = table.Column<Guid>(type: "TEXT", nullable: false),
                    InServiceDate = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    CostMinor = table.Column<long>(type: "INTEGER", nullable: false),
                    SalvageMinor = table.Column<long>(type: "INTEGER", nullable: false),
                    Method = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    LifeMonths = table.Column<int>(type: "INTEGER", nullable: false),
                    DecliningPercentTenThousandths = table.Column<long>(type: "INTEGER", nullable: false),
                    MacrsClass = table.Column<string>(type: "TEXT", maxLength: 20, nullable: true),
                    PriorDepreciationMinor = table.Column<long>(type: "INTEGER", nullable: false),
                    PriorThrough = table.Column<DateOnly>(type: "TEXT", nullable: true),
                    Status = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    DisposedOn = table.Column<DateOnly>(type: "TEXT", nullable: true),
                    ProceedsMinor = table.Column<long>(type: "INTEGER", nullable: false),
                    ProceedsAccountId = table.Column<Guid>(type: "TEXT", nullable: true),
                    DisposalDepreciationMinor = table.Column<long>(type: "INTEGER", nullable: false),
                    DisposalEntryId = table.Column<Guid>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FixedAssets", x => x.Id);
                    table.ForeignKey(
                        name: "FK_FixedAssets_Accounts_AccumulatedAccountId",
                        column: x => x.AccumulatedAccountId,
                        principalTable: "Accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_FixedAssets_Accounts_AssetAccountId",
                        column: x => x.AssetAccountId,
                        principalTable: "Accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_FixedAssets_Accounts_ExpenseAccountId",
                        column: x => x.ExpenseAccountId,
                        principalTable: "Accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_FixedAssets_Accounts_ProceedsAccountId",
                        column: x => x.ProceedsAccountId,
                        principalTable: "Accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_FixedAssets_JournalEntries_DisposalEntryId",
                        column: x => x.DisposalEntryId,
                        principalTable: "JournalEntries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Loans",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    LiabilityAccountId = table.Column<Guid>(type: "TEXT", nullable: false),
                    InterestAccountId = table.Column<Guid>(type: "TEXT", nullable: false),
                    PrincipalMinor = table.Column<long>(type: "INTEGER", nullable: false),
                    AnnualRatePercentTenThousandths = table.Column<long>(type: "INTEGER", nullable: false),
                    Payments = table.Column<int>(type: "INTEGER", nullable: false),
                    FirstPaymentDate = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    PaymentAmountMinor = table.Column<long>(type: "INTEGER", nullable: false),
                    LenderVendorId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Notes = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    ProceedsAccountId = table.Column<Guid>(type: "TEXT", nullable: true),
                    ProceedsDate = table.Column<DateOnly>(type: "TEXT", nullable: true),
                    ProceedsEntryId = table.Column<Guid>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Loans", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Loans_Accounts_InterestAccountId",
                        column: x => x.InterestAccountId,
                        principalTable: "Accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Loans_Accounts_LiabilityAccountId",
                        column: x => x.LiabilityAccountId,
                        principalTable: "Accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Loans_Accounts_ProceedsAccountId",
                        column: x => x.ProceedsAccountId,
                        principalTable: "Accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Loans_JournalEntries_ProceedsEntryId",
                        column: x => x.ProceedsEntryId,
                        principalTable: "JournalEntries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Loans_Vendors_LenderVendorId",
                        column: x => x.LenderVendorId,
                        principalTable: "Vendors",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "DepreciationRunLines",
                columns: table => new
                {
                    LineNumber = table.Column<int>(type: "INTEGER", nullable: false),
                    DepreciationRunId = table.Column<Guid>(type: "TEXT", nullable: false),
                    AssetId = table.Column<Guid>(type: "TEXT", nullable: false),
                    AmountMinor = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DepreciationRunLines", x => new { x.DepreciationRunId, x.LineNumber });
                    table.ForeignKey(
                        name: "FK_DepreciationRunLines_DepreciationRuns_DepreciationRunId",
                        column: x => x.DepreciationRunId,
                        principalTable: "DepreciationRuns",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_DepreciationRunLines_FixedAssets_AssetId",
                        column: x => x.AssetId,
                        principalTable: "FixedAssets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "LoanPayments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Date = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    AmountMinor = table.Column<long>(type: "INTEGER", nullable: false),
                    InterestMinor = table.Column<long>(type: "INTEGER", nullable: false),
                    PrincipalMinor = table.Column<long>(type: "INTEGER", nullable: false),
                    PaymentAccountId = table.Column<Guid>(type: "TEXT", nullable: false),
                    JournalEntryId = table.Column<Guid>(type: "TEXT", nullable: false),
                    IsVoided = table.Column<bool>(type: "INTEGER", nullable: false),
                    LoanId = table.Column<Guid>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LoanPayments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_LoanPayments_Accounts_PaymentAccountId",
                        column: x => x.PaymentAccountId,
                        principalTable: "Accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_LoanPayments_JournalEntries_JournalEntryId",
                        column: x => x.JournalEntryId,
                        principalTable: "JournalEntries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_LoanPayments_Loans_LoanId",
                        column: x => x.LoanId,
                        principalTable: "Loans",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DepreciationRunLines_AssetId",
                table: "DepreciationRunLines",
                column: "AssetId");

            migrationBuilder.CreateIndex(
                name: "IX_DepreciationRuns_JournalEntryId",
                table: "DepreciationRuns",
                column: "JournalEntryId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_FixedAssets_AccumulatedAccountId",
                table: "FixedAssets",
                column: "AccumulatedAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_FixedAssets_AssetAccountId",
                table: "FixedAssets",
                column: "AssetAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_FixedAssets_DisposalEntryId",
                table: "FixedAssets",
                column: "DisposalEntryId");

            migrationBuilder.CreateIndex(
                name: "IX_FixedAssets_ExpenseAccountId",
                table: "FixedAssets",
                column: "ExpenseAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_FixedAssets_ProceedsAccountId",
                table: "FixedAssets",
                column: "ProceedsAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_LoanPayments_JournalEntryId",
                table: "LoanPayments",
                column: "JournalEntryId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_LoanPayments_LoanId",
                table: "LoanPayments",
                column: "LoanId");

            migrationBuilder.CreateIndex(
                name: "IX_LoanPayments_PaymentAccountId",
                table: "LoanPayments",
                column: "PaymentAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_Loans_InterestAccountId",
                table: "Loans",
                column: "InterestAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_Loans_LenderVendorId",
                table: "Loans",
                column: "LenderVendorId");

            migrationBuilder.CreateIndex(
                name: "IX_Loans_LiabilityAccountId",
                table: "Loans",
                column: "LiabilityAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_Loans_ProceedsAccountId",
                table: "Loans",
                column: "ProceedsAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_Loans_ProceedsEntryId",
                table: "Loans",
                column: "ProceedsEntryId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DepreciationRunLines");

            migrationBuilder.DropTable(
                name: "LoanPayments");

            migrationBuilder.DropTable(
                name: "DepreciationRuns");

            migrationBuilder.DropTable(
                name: "FixedAssets");

            migrationBuilder.DropTable(
                name: "Loans");
        }
    }
}
