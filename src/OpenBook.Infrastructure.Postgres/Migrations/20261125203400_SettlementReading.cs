using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenBook.Infrastructure.Postgres.Migrations
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
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CheckedBy",
                table: "Settlements",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ReadMethod",
                table: "Settlements",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ReadNote",
                table: "Settlements",
                type: "character varying(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Doubt",
                table: "SettlementLines",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "DoubtCleared",
                table: "SettlementLines",
                type: "boolean",
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
