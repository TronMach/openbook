using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenBook.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class PledgeCancel : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "CancelEntryId",
                table: "Pledges",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "CancelledMinor",
                table: "Pledges",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<DateOnly>(
                name: "CancelledOn",
                table: "Pledges",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Pledges_CancelEntryId",
                table: "Pledges",
                column: "CancelEntryId");

            migrationBuilder.AddForeignKey(
                name: "FK_Pledges_JournalEntries_CancelEntryId",
                table: "Pledges",
                column: "CancelEntryId",
                principalTable: "JournalEntries",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Pledges_JournalEntries_CancelEntryId",
                table: "Pledges");

            migrationBuilder.DropIndex(
                name: "IX_Pledges_CancelEntryId",
                table: "Pledges");

            migrationBuilder.DropColumn(
                name: "CancelEntryId",
                table: "Pledges");

            migrationBuilder.DropColumn(
                name: "CancelledMinor",
                table: "Pledges");

            migrationBuilder.DropColumn(
                name: "CancelledOn",
                table: "Pledges");
        }
    }
}
