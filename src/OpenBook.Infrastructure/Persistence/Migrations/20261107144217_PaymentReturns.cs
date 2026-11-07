using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenBook.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class PaymentReturns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "ReturnAccountId",
                table: "CustomerPayments",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ReturnEntryId",
                table: "CustomerPayments",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "ReturnFeeMinor",
                table: "CustomerPayments",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<string>(
                name: "ReturnReason",
                table: "CustomerPayments",
                type: "TEXT",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<DateOnly>(
                name: "ReturnedOn",
                table: "CustomerPayments",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_CustomerPayments_ReturnAccountId",
                table: "CustomerPayments",
                column: "ReturnAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_CustomerPayments_ReturnedOn",
                table: "CustomerPayments",
                column: "ReturnedOn");

            migrationBuilder.CreateIndex(
                name: "IX_CustomerPayments_ReturnEntryId",
                table: "CustomerPayments",
                column: "ReturnEntryId");

            migrationBuilder.AddForeignKey(
                name: "FK_CustomerPayments_Accounts_ReturnAccountId",
                table: "CustomerPayments",
                column: "ReturnAccountId",
                principalTable: "Accounts",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_CustomerPayments_JournalEntries_ReturnEntryId",
                table: "CustomerPayments",
                column: "ReturnEntryId",
                principalTable: "JournalEntries",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_CustomerPayments_Accounts_ReturnAccountId",
                table: "CustomerPayments");

            migrationBuilder.DropForeignKey(
                name: "FK_CustomerPayments_JournalEntries_ReturnEntryId",
                table: "CustomerPayments");

            migrationBuilder.DropIndex(
                name: "IX_CustomerPayments_ReturnAccountId",
                table: "CustomerPayments");

            migrationBuilder.DropIndex(
                name: "IX_CustomerPayments_ReturnedOn",
                table: "CustomerPayments");

            migrationBuilder.DropIndex(
                name: "IX_CustomerPayments_ReturnEntryId",
                table: "CustomerPayments");

            migrationBuilder.DropColumn(
                name: "ReturnAccountId",
                table: "CustomerPayments");

            migrationBuilder.DropColumn(
                name: "ReturnEntryId",
                table: "CustomerPayments");

            migrationBuilder.DropColumn(
                name: "ReturnFeeMinor",
                table: "CustomerPayments");

            migrationBuilder.DropColumn(
                name: "ReturnReason",
                table: "CustomerPayments");

            migrationBuilder.DropColumn(
                name: "ReturnedOn",
                table: "CustomerPayments");
        }
    }
}
