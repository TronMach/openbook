using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenBook.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class GrantAdvances : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "AdvanceAccountId",
                table: "Grants",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "AdvancePartMinor",
                table: "GrantRecognitions",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<long>(
                name: "AdvancePartMinor",
                table: "GrantReceipts",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.CreateIndex(
                name: "IX_Grants_AdvanceAccountId",
                table: "Grants",
                column: "AdvanceAccountId");

            migrationBuilder.AddForeignKey(
                name: "FK_Grants_Accounts_AdvanceAccountId",
                table: "Grants",
                column: "AdvanceAccountId",
                principalTable: "Accounts",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Grants_Accounts_AdvanceAccountId",
                table: "Grants");

            migrationBuilder.DropIndex(
                name: "IX_Grants_AdvanceAccountId",
                table: "Grants");

            migrationBuilder.DropColumn(
                name: "AdvanceAccountId",
                table: "Grants");

            migrationBuilder.DropColumn(
                name: "AdvancePartMinor",
                table: "GrantRecognitions");

            migrationBuilder.DropColumn(
                name: "AdvancePartMinor",
                table: "GrantReceipts");
        }
    }
}
