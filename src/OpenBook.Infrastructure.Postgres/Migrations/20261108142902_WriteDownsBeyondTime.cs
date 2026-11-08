using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenBook.Infrastructure.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class WriteDownsBeyondTime : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "BilledAmountMinor",
                table: "MileageTrips",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "WriteDownReason",
                table: "MileageTrips",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "WrittenDownBy",
                table: "MileageTrips",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<DateOnly>(
                name: "WrittenDownOn",
                table: "MileageTrips",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "BilledAmountMinor",
                table: "BillableExpenses",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "WriteDownReason",
                table: "BillableExpenses",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "WrittenDownBy",
                table: "BillableExpenses",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<DateOnly>(
                name: "WrittenDownOn",
                table: "BillableExpenses",
                type: "date",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "BilledAmountMinor",
                table: "MileageTrips");

            migrationBuilder.DropColumn(
                name: "WriteDownReason",
                table: "MileageTrips");

            migrationBuilder.DropColumn(
                name: "WrittenDownBy",
                table: "MileageTrips");

            migrationBuilder.DropColumn(
                name: "WrittenDownOn",
                table: "MileageTrips");

            migrationBuilder.DropColumn(
                name: "BilledAmountMinor",
                table: "BillableExpenses");

            migrationBuilder.DropColumn(
                name: "WriteDownReason",
                table: "BillableExpenses");

            migrationBuilder.DropColumn(
                name: "WrittenDownBy",
                table: "BillableExpenses");

            migrationBuilder.DropColumn(
                name: "WrittenDownOn",
                table: "BillableExpenses");
        }
    }
}
