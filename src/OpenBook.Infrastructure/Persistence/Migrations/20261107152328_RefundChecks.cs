using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenBook.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class RefundChecks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "PositivePaySentAt",
                table: "RetainerRefunds",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "PositivePayVoidSentAt",
                table: "RetainerRefunds",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "PrintedAt",
                table: "RetainerRefunds",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "PositivePaySentAt",
                table: "CustomerRefunds",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "PositivePayVoidSentAt",
                table: "CustomerRefunds",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "PrintedAt",
                table: "CustomerRefunds",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "PositivePaySentAt",
                table: "RetainerRefunds");

            migrationBuilder.DropColumn(
                name: "PositivePayVoidSentAt",
                table: "RetainerRefunds");

            migrationBuilder.DropColumn(
                name: "PrintedAt",
                table: "RetainerRefunds");

            migrationBuilder.DropColumn(
                name: "PositivePaySentAt",
                table: "CustomerRefunds");

            migrationBuilder.DropColumn(
                name: "PositivePayVoidSentAt",
                table: "CustomerRefunds");

            migrationBuilder.DropColumn(
                name: "PrintedAt",
                table: "CustomerRefunds");
        }
    }
}
