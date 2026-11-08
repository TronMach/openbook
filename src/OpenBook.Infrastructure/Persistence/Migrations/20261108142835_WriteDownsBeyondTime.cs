using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenBook.Infrastructure.Persistence.Migrations
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
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "WriteDownReason",
                table: "MileageTrips",
                type: "TEXT",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "WrittenDownBy",
                table: "MileageTrips",
                type: "TEXT",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<DateOnly>(
                name: "WrittenDownOn",
                table: "MileageTrips",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "BilledAmountMinor",
                table: "BillableExpenses",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "WriteDownReason",
                table: "BillableExpenses",
                type: "TEXT",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "WrittenDownBy",
                table: "BillableExpenses",
                type: "TEXT",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<DateOnly>(
                name: "WrittenDownOn",
                table: "BillableExpenses",
                type: "TEXT",
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
