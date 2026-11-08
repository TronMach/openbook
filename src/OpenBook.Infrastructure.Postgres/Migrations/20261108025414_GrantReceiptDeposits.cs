using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenBook.Infrastructure.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class GrantReceiptDeposits : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "DepositId",
                table: "GrantReceipts",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_GrantReceipts_DepositId",
                table: "GrantReceipts",
                column: "DepositId");

            migrationBuilder.AddForeignKey(
                name: "FK_GrantReceipts_BankDeposits_DepositId",
                table: "GrantReceipts",
                column: "DepositId",
                principalTable: "BankDeposits",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_GrantReceipts_BankDeposits_DepositId",
                table: "GrantReceipts");

            migrationBuilder.DropIndex(
                name: "IX_GrantReceipts_DepositId",
                table: "GrantReceipts");

            migrationBuilder.DropColumn(
                name: "DepositId",
                table: "GrantReceipts");
        }
    }
}
