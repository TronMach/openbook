using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenBook.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class EstimateResponses : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "RespondedAt",
                table: "Estimates",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RespondedBy",
                table: "Estimates",
                type: "TEXT",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ResponseNote",
                table: "Estimates",
                type: "TEXT",
                maxLength: 500,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "RespondedAt",
                table: "Estimates");

            migrationBuilder.DropColumn(
                name: "RespondedBy",
                table: "Estimates");

            migrationBuilder.DropColumn(
                name: "ResponseNote",
                table: "Estimates");
        }
    }
}
