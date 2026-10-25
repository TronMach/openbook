using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenBook.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Segments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "LocationId",
                table: "VendorCredits",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ClassId",
                table: "VendorCreditLines",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "LocationId",
                table: "SalesReceipts",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ClassId",
                table: "SalesReceiptLines",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "LocationId",
                table: "Invoices",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ClassId",
                table: "InvoiceLines",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "LocationId",
                table: "Estimates",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ClassId",
                table: "EstimateLines",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "LocationId",
                table: "CreditMemos",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ClassId",
                table: "CreditMemoLines",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LocationLabel",
                table: "Companies",
                type: "TEXT",
                maxLength: 30,
                nullable: false,
                defaultValue: "Location");

            migrationBuilder.AddColumn<bool>(
                name: "TrackClasses",
                table: "Companies",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "TrackLocations",
                table: "Companies",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<Guid>(
                name: "ClassId",
                table: "Budgets",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "LocationId",
                table: "Budgets",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "LocationId",
                table: "Bills",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ClassId",
                table: "BillLines",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "LocationId",
                table: "BankTransactions",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "Segments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Kind = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    ParentId = table.Column<Guid>(type: "TEXT", nullable: true),
                    IsActive = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Segments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Segments_Segments_ParentId",
                        column: x => x.ParentId,
                        principalTable: "Segments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "EntryTags",
                columns: table => new
                {
                    JournalEntryId = table.Column<Guid>(type: "TEXT", nullable: false),
                    TagId = table.Column<Guid>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EntryTags", x => new { x.JournalEntryId, x.TagId });
                    table.ForeignKey(
                        name: "FK_EntryTags_JournalEntries_JournalEntryId",
                        column: x => x.JournalEntryId,
                        principalTable: "JournalEntries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_EntryTags_Segments_TagId",
                        column: x => x.TagId,
                        principalTable: "Segments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "LineSegments",
                columns: table => new
                {
                    JournalLineId = table.Column<Guid>(type: "TEXT", nullable: false),
                    JournalEntryId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ClassId = table.Column<Guid>(type: "TEXT", nullable: true),
                    LocationId = table.Column<Guid>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LineSegments", x => x.JournalLineId);
                    table.ForeignKey(
                        name: "FK_LineSegments_JournalEntries_JournalEntryId",
                        column: x => x.JournalEntryId,
                        principalTable: "JournalEntries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_LineSegments_JournalLines_JournalLineId",
                        column: x => x.JournalLineId,
                        principalTable: "JournalLines",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_LineSegments_Segments_ClassId",
                        column: x => x.ClassId,
                        principalTable: "Segments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_LineSegments_Segments_LocationId",
                        column: x => x.LocationId,
                        principalTable: "Segments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_VendorCredits_LocationId",
                table: "VendorCredits",
                column: "LocationId");

            migrationBuilder.CreateIndex(
                name: "IX_VendorCreditLines_ClassId",
                table: "VendorCreditLines",
                column: "ClassId");

            migrationBuilder.CreateIndex(
                name: "IX_SalesReceipts_LocationId",
                table: "SalesReceipts",
                column: "LocationId");

            migrationBuilder.CreateIndex(
                name: "IX_SalesReceiptLines_ClassId",
                table: "SalesReceiptLines",
                column: "ClassId");

            migrationBuilder.CreateIndex(
                name: "IX_Invoices_LocationId",
                table: "Invoices",
                column: "LocationId");

            migrationBuilder.CreateIndex(
                name: "IX_InvoiceLines_ClassId",
                table: "InvoiceLines",
                column: "ClassId");

            migrationBuilder.CreateIndex(
                name: "IX_Estimates_LocationId",
                table: "Estimates",
                column: "LocationId");

            migrationBuilder.CreateIndex(
                name: "IX_EstimateLines_ClassId",
                table: "EstimateLines",
                column: "ClassId");

            migrationBuilder.CreateIndex(
                name: "IX_CreditMemos_LocationId",
                table: "CreditMemos",
                column: "LocationId");

            migrationBuilder.CreateIndex(
                name: "IX_CreditMemoLines_ClassId",
                table: "CreditMemoLines",
                column: "ClassId");

            migrationBuilder.CreateIndex(
                name: "IX_Budgets_ClassId",
                table: "Budgets",
                column: "ClassId");

            migrationBuilder.CreateIndex(
                name: "IX_Budgets_LocationId",
                table: "Budgets",
                column: "LocationId");

            migrationBuilder.CreateIndex(
                name: "IX_Bills_LocationId",
                table: "Bills",
                column: "LocationId");

            migrationBuilder.CreateIndex(
                name: "IX_BillLines_ClassId",
                table: "BillLines",
                column: "ClassId");

            migrationBuilder.CreateIndex(
                name: "IX_BankTransactions_LocationId",
                table: "BankTransactions",
                column: "LocationId");

            migrationBuilder.CreateIndex(
                name: "IX_EntryTags_TagId",
                table: "EntryTags",
                column: "TagId");

            migrationBuilder.CreateIndex(
                name: "IX_LineSegments_ClassId",
                table: "LineSegments",
                column: "ClassId");

            migrationBuilder.CreateIndex(
                name: "IX_LineSegments_JournalEntryId",
                table: "LineSegments",
                column: "JournalEntryId");

            migrationBuilder.CreateIndex(
                name: "IX_LineSegments_LocationId",
                table: "LineSegments",
                column: "LocationId");

            migrationBuilder.CreateIndex(
                name: "IX_Segments_Kind_ParentId_Name",
                table: "Segments",
                columns: new[] { "Kind", "ParentId", "Name" });

            migrationBuilder.CreateIndex(
                name: "IX_Segments_ParentId",
                table: "Segments",
                column: "ParentId");

            migrationBuilder.AddForeignKey(
                name: "FK_BankTransactions_Segments_LocationId",
                table: "BankTransactions",
                column: "LocationId",
                principalTable: "Segments",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_BillLines_Segments_ClassId",
                table: "BillLines",
                column: "ClassId",
                principalTable: "Segments",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Bills_Segments_LocationId",
                table: "Bills",
                column: "LocationId",
                principalTable: "Segments",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Budgets_Segments_ClassId",
                table: "Budgets",
                column: "ClassId",
                principalTable: "Segments",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Budgets_Segments_LocationId",
                table: "Budgets",
                column: "LocationId",
                principalTable: "Segments",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_CreditMemoLines_Segments_ClassId",
                table: "CreditMemoLines",
                column: "ClassId",
                principalTable: "Segments",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_CreditMemos_Segments_LocationId",
                table: "CreditMemos",
                column: "LocationId",
                principalTable: "Segments",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_EstimateLines_Segments_ClassId",
                table: "EstimateLines",
                column: "ClassId",
                principalTable: "Segments",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Estimates_Segments_LocationId",
                table: "Estimates",
                column: "LocationId",
                principalTable: "Segments",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_InvoiceLines_Segments_ClassId",
                table: "InvoiceLines",
                column: "ClassId",
                principalTable: "Segments",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Invoices_Segments_LocationId",
                table: "Invoices",
                column: "LocationId",
                principalTable: "Segments",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_SalesReceiptLines_Segments_ClassId",
                table: "SalesReceiptLines",
                column: "ClassId",
                principalTable: "Segments",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_SalesReceipts_Segments_LocationId",
                table: "SalesReceipts",
                column: "LocationId",
                principalTable: "Segments",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_VendorCreditLines_Segments_ClassId",
                table: "VendorCreditLines",
                column: "ClassId",
                principalTable: "Segments",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_VendorCredits_Segments_LocationId",
                table: "VendorCredits",
                column: "LocationId",
                principalTable: "Segments",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_BankTransactions_Segments_LocationId",
                table: "BankTransactions");

            migrationBuilder.DropForeignKey(
                name: "FK_BillLines_Segments_ClassId",
                table: "BillLines");

            migrationBuilder.DropForeignKey(
                name: "FK_Bills_Segments_LocationId",
                table: "Bills");

            migrationBuilder.DropForeignKey(
                name: "FK_Budgets_Segments_ClassId",
                table: "Budgets");

            migrationBuilder.DropForeignKey(
                name: "FK_Budgets_Segments_LocationId",
                table: "Budgets");

            migrationBuilder.DropForeignKey(
                name: "FK_CreditMemoLines_Segments_ClassId",
                table: "CreditMemoLines");

            migrationBuilder.DropForeignKey(
                name: "FK_CreditMemos_Segments_LocationId",
                table: "CreditMemos");

            migrationBuilder.DropForeignKey(
                name: "FK_EstimateLines_Segments_ClassId",
                table: "EstimateLines");

            migrationBuilder.DropForeignKey(
                name: "FK_Estimates_Segments_LocationId",
                table: "Estimates");

            migrationBuilder.DropForeignKey(
                name: "FK_InvoiceLines_Segments_ClassId",
                table: "InvoiceLines");

            migrationBuilder.DropForeignKey(
                name: "FK_Invoices_Segments_LocationId",
                table: "Invoices");

            migrationBuilder.DropForeignKey(
                name: "FK_SalesReceiptLines_Segments_ClassId",
                table: "SalesReceiptLines");

            migrationBuilder.DropForeignKey(
                name: "FK_SalesReceipts_Segments_LocationId",
                table: "SalesReceipts");

            migrationBuilder.DropForeignKey(
                name: "FK_VendorCreditLines_Segments_ClassId",
                table: "VendorCreditLines");

            migrationBuilder.DropForeignKey(
                name: "FK_VendorCredits_Segments_LocationId",
                table: "VendorCredits");

            migrationBuilder.DropTable(
                name: "EntryTags");

            migrationBuilder.DropTable(
                name: "LineSegments");

            migrationBuilder.DropTable(
                name: "Segments");

            migrationBuilder.DropIndex(
                name: "IX_VendorCredits_LocationId",
                table: "VendorCredits");

            migrationBuilder.DropIndex(
                name: "IX_VendorCreditLines_ClassId",
                table: "VendorCreditLines");

            migrationBuilder.DropIndex(
                name: "IX_SalesReceipts_LocationId",
                table: "SalesReceipts");

            migrationBuilder.DropIndex(
                name: "IX_SalesReceiptLines_ClassId",
                table: "SalesReceiptLines");

            migrationBuilder.DropIndex(
                name: "IX_Invoices_LocationId",
                table: "Invoices");

            migrationBuilder.DropIndex(
                name: "IX_InvoiceLines_ClassId",
                table: "InvoiceLines");

            migrationBuilder.DropIndex(
                name: "IX_Estimates_LocationId",
                table: "Estimates");

            migrationBuilder.DropIndex(
                name: "IX_EstimateLines_ClassId",
                table: "EstimateLines");

            migrationBuilder.DropIndex(
                name: "IX_CreditMemos_LocationId",
                table: "CreditMemos");

            migrationBuilder.DropIndex(
                name: "IX_CreditMemoLines_ClassId",
                table: "CreditMemoLines");

            migrationBuilder.DropIndex(
                name: "IX_Budgets_ClassId",
                table: "Budgets");

            migrationBuilder.DropIndex(
                name: "IX_Budgets_LocationId",
                table: "Budgets");

            migrationBuilder.DropIndex(
                name: "IX_Bills_LocationId",
                table: "Bills");

            migrationBuilder.DropIndex(
                name: "IX_BillLines_ClassId",
                table: "BillLines");

            migrationBuilder.DropIndex(
                name: "IX_BankTransactions_LocationId",
                table: "BankTransactions");

            migrationBuilder.DropColumn(
                name: "LocationId",
                table: "VendorCredits");

            migrationBuilder.DropColumn(
                name: "ClassId",
                table: "VendorCreditLines");

            migrationBuilder.DropColumn(
                name: "LocationId",
                table: "SalesReceipts");

            migrationBuilder.DropColumn(
                name: "ClassId",
                table: "SalesReceiptLines");

            migrationBuilder.DropColumn(
                name: "LocationId",
                table: "Invoices");

            migrationBuilder.DropColumn(
                name: "ClassId",
                table: "InvoiceLines");

            migrationBuilder.DropColumn(
                name: "LocationId",
                table: "Estimates");

            migrationBuilder.DropColumn(
                name: "ClassId",
                table: "EstimateLines");

            migrationBuilder.DropColumn(
                name: "LocationId",
                table: "CreditMemos");

            migrationBuilder.DropColumn(
                name: "ClassId",
                table: "CreditMemoLines");

            migrationBuilder.DropColumn(
                name: "LocationLabel",
                table: "Companies");

            migrationBuilder.DropColumn(
                name: "TrackClasses",
                table: "Companies");

            migrationBuilder.DropColumn(
                name: "TrackLocations",
                table: "Companies");

            migrationBuilder.DropColumn(
                name: "ClassId",
                table: "Budgets");

            migrationBuilder.DropColumn(
                name: "LocationId",
                table: "Budgets");

            migrationBuilder.DropColumn(
                name: "LocationId",
                table: "Bills");

            migrationBuilder.DropColumn(
                name: "ClassId",
                table: "BillLines");

            migrationBuilder.DropColumn(
                name: "LocationId",
                table: "BankTransactions");
        }
    }
}
