using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenBook.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class SettlementReading : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "CheckedAt",
                table: "Settlements",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CheckedBy",
                table: "Settlements",
                type: "TEXT",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ReadMethod",
                table: "Settlements",
                type: "TEXT",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ReadNote",
                table: "Settlements",
                type: "TEXT",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Doubt",
                table: "SettlementLines",
                type: "TEXT",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "DoubtCleared",
                table: "SettlementLines",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CheckedAt",
                table: "Settlements");

            migrationBuilder.DropColumn(
                name: "CheckedBy",
                table: "Settlements");

            migrationBuilder.DropColumn(
                name: "ReadMethod",
                table: "Settlements");

            migrationBuilder.DropColumn(
                name: "ReadNote",
                table: "Settlements");

            migrationBuilder.DropColumn(
                name: "Doubt",
                table: "SettlementLines");

            migrationBuilder.DropColumn(
                name: "DoubtCleared",
                table: "SettlementLines");
        }
    }
}
