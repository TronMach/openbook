using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenBook.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class TemplateSegments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "LocationId",
                table: "RecurringInvoices",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Tags",
                table: "RecurringInvoices",
                type: "TEXT",
                maxLength: 800,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ClassId",
                table: "RecurringInvoiceLines",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ClassId",
                table: "RecurringEntryLines",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "LocationId",
                table: "RecurringEntries",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Tags",
                table: "RecurringEntries",
                type: "TEXT",
                maxLength: 800,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "LocationId",
                table: "RecurringBills",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Tags",
                table: "RecurringBills",
                type: "TEXT",
                maxLength: 800,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ClassId",
                table: "RecurringBillLines",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_RecurringInvoices_LocationId",
                table: "RecurringInvoices",
                column: "LocationId");

            migrationBuilder.CreateIndex(
                name: "IX_RecurringInvoiceLines_ClassId",
                table: "RecurringInvoiceLines",
                column: "ClassId");

            migrationBuilder.CreateIndex(
                name: "IX_RecurringEntryLines_ClassId",
                table: "RecurringEntryLines",
                column: "ClassId");

            migrationBuilder.CreateIndex(
                name: "IX_RecurringEntries_LocationId",
                table: "RecurringEntries",
                column: "LocationId");

            migrationBuilder.CreateIndex(
                name: "IX_RecurringBills_LocationId",
                table: "RecurringBills",
                column: "LocationId");

            migrationBuilder.CreateIndex(
                name: "IX_RecurringBillLines_ClassId",
                table: "RecurringBillLines",
                column: "ClassId");

            migrationBuilder.AddForeignKey(
                name: "FK_RecurringBillLines_Segments_ClassId",
                table: "RecurringBillLines",
                column: "ClassId",
                principalTable: "Segments",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_RecurringBills_Segments_LocationId",
                table: "RecurringBills",
                column: "LocationId",
                principalTable: "Segments",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_RecurringEntries_Segments_LocationId",
                table: "RecurringEntries",
                column: "LocationId",
                principalTable: "Segments",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_RecurringEntryLines_Segments_ClassId",
                table: "RecurringEntryLines",
                column: "ClassId",
                principalTable: "Segments",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_RecurringInvoiceLines_Segments_ClassId",
                table: "RecurringInvoiceLines",
                column: "ClassId",
                principalTable: "Segments",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_RecurringInvoices_Segments_LocationId",
                table: "RecurringInvoices",
                column: "LocationId",
                principalTable: "Segments",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_RecurringBillLines_Segments_ClassId",
                table: "RecurringBillLines");

            migrationBuilder.DropForeignKey(
                name: "FK_RecurringBills_Segments_LocationId",
                table: "RecurringBills");

            migrationBuilder.DropForeignKey(
                name: "FK_RecurringEntries_Segments_LocationId",
                table: "RecurringEntries");

            migrationBuilder.DropForeignKey(
                name: "FK_RecurringEntryLines_Segments_ClassId",
                table: "RecurringEntryLines");

            migrationBuilder.DropForeignKey(
                name: "FK_RecurringInvoiceLines_Segments_ClassId",
                table: "RecurringInvoiceLines");

            migrationBuilder.DropForeignKey(
                name: "FK_RecurringInvoices_Segments_LocationId",
                table: "RecurringInvoices");

            migrationBuilder.DropIndex(
                name: "IX_RecurringInvoices_LocationId",
                table: "RecurringInvoices");

            migrationBuilder.DropIndex(
                name: "IX_RecurringInvoiceLines_ClassId",
                table: "RecurringInvoiceLines");

            migrationBuilder.DropIndex(
                name: "IX_RecurringEntryLines_ClassId",
                table: "RecurringEntryLines");

            migrationBuilder.DropIndex(
                name: "IX_RecurringEntries_LocationId",
                table: "RecurringEntries");

            migrationBuilder.DropIndex(
                name: "IX_RecurringBills_LocationId",
                table: "RecurringBills");

            migrationBuilder.DropIndex(
                name: "IX_RecurringBillLines_ClassId",
                table: "RecurringBillLines");

            migrationBuilder.DropColumn(
                name: "LocationId",
                table: "RecurringInvoices");

            migrationBuilder.DropColumn(
                name: "Tags",
                table: "RecurringInvoices");

            migrationBuilder.DropColumn(
                name: "ClassId",
                table: "RecurringInvoiceLines");

            migrationBuilder.DropColumn(
                name: "ClassId",
                table: "RecurringEntryLines");

            migrationBuilder.DropColumn(
                name: "LocationId",
                table: "RecurringEntries");

            migrationBuilder.DropColumn(
                name: "Tags",
                table: "RecurringEntries");

            migrationBuilder.DropColumn(
                name: "LocationId",
                table: "RecurringBills");

            migrationBuilder.DropColumn(
                name: "Tags",
                table: "RecurringBills");

            migrationBuilder.DropColumn(
                name: "ClassId",
                table: "RecurringBillLines");
        }
    }
}
