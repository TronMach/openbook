using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenBook.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AchWithdraw : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "WithdrawReason",
                table: "AchBatches",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "WithdrawnAt",
                table: "AchBatches",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "WithdrawReason",
                table: "AchBatches");

            migrationBuilder.DropColumn(
                name: "WithdrawnAt",
                table: "AchBatches");
        }
    }
}
