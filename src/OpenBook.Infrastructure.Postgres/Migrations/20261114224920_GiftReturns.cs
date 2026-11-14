using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenBook.Infrastructure.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class GiftReturns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "ReturnAccountId",
                table: "Donations",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ReturnCashEntryId",
                table: "Donations",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ReturnEntryId",
                table: "Donations",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "ReturnFeeMinor",
                table: "Donations",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<string>(
                name: "ReturnReason",
                table: "Donations",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<DateOnly>(
                name: "ReturnedOn",
                table: "Donations",
                type: "date",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Donations_ReturnAccountId",
                table: "Donations",
                column: "ReturnAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_Donations_ReturnCashEntryId",
                table: "Donations",
                column: "ReturnCashEntryId");

            migrationBuilder.CreateIndex(
                name: "IX_Donations_ReturnEntryId",
                table: "Donations",
                column: "ReturnEntryId");

            migrationBuilder.AddForeignKey(
                name: "FK_Donations_Accounts_ReturnAccountId",
                table: "Donations",
                column: "ReturnAccountId",
                principalTable: "Accounts",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Donations_JournalEntries_ReturnCashEntryId",
                table: "Donations",
                column: "ReturnCashEntryId",
                principalTable: "JournalEntries",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Donations_JournalEntries_ReturnEntryId",
                table: "Donations",
                column: "ReturnEntryId",
                principalTable: "JournalEntries",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Donations_Accounts_ReturnAccountId",
                table: "Donations");

            migrationBuilder.DropForeignKey(
                name: "FK_Donations_JournalEntries_ReturnCashEntryId",
                table: "Donations");

            migrationBuilder.DropForeignKey(
                name: "FK_Donations_JournalEntries_ReturnEntryId",
                table: "Donations");

            migrationBuilder.DropIndex(
                name: "IX_Donations_ReturnAccountId",
                table: "Donations");

            migrationBuilder.DropIndex(
                name: "IX_Donations_ReturnCashEntryId",
                table: "Donations");

            migrationBuilder.DropIndex(
                name: "IX_Donations_ReturnEntryId",
                table: "Donations");

            migrationBuilder.DropColumn(
                name: "ReturnAccountId",
                table: "Donations");

            migrationBuilder.DropColumn(
                name: "ReturnCashEntryId",
                table: "Donations");

            migrationBuilder.DropColumn(
                name: "ReturnEntryId",
                table: "Donations");

            migrationBuilder.DropColumn(
                name: "ReturnFeeMinor",
                table: "Donations");

            migrationBuilder.DropColumn(
                name: "ReturnReason",
                table: "Donations");

            migrationBuilder.DropColumn(
                name: "ReturnedOn",
                table: "Donations");
        }
    }
}
