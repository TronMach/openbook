using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenBook.Infrastructure.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class ReceiptsIntoStock : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "ReceiveIntoStock",
                table: "PurchasingSettings",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<Guid>(
                name: "JournalEntryId",
                table: "PurchaseOrderReceipts",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "StockSourceId",
                table: "PurchaseOrderReceipts",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "ValueMinor",
                table: "PurchaseOrderReceipts",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<long>(
                name: "AccruedAmountMinor",
                table: "BillSourceLines",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<long>(
                name: "AccruedQuantityTenThousandths",
                table: "BillSourceLines",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.CreateIndex(
                name: "IX_PurchaseOrderReceipts_JournalEntryId",
                table: "PurchaseOrderReceipts",
                column: "JournalEntryId");

            migrationBuilder.AddForeignKey(
                name: "FK_PurchaseOrderReceipts_JournalEntries_JournalEntryId",
                table: "PurchaseOrderReceipts",
                column: "JournalEntryId",
                principalTable: "JournalEntries",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_PurchaseOrderReceipts_JournalEntries_JournalEntryId",
                table: "PurchaseOrderReceipts");

            migrationBuilder.DropIndex(
                name: "IX_PurchaseOrderReceipts_JournalEntryId",
                table: "PurchaseOrderReceipts");

            migrationBuilder.DropColumn(
                name: "ReceiveIntoStock",
                table: "PurchasingSettings");

            migrationBuilder.DropColumn(
                name: "JournalEntryId",
                table: "PurchaseOrderReceipts");

            migrationBuilder.DropColumn(
                name: "StockSourceId",
                table: "PurchaseOrderReceipts");

            migrationBuilder.DropColumn(
                name: "ValueMinor",
                table: "PurchaseOrderReceipts");

            migrationBuilder.DropColumn(
                name: "AccruedAmountMinor",
                table: "BillSourceLines");

            migrationBuilder.DropColumn(
                name: "AccruedQuantityTenThousandths",
                table: "BillSourceLines");
        }
    }
}
