using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenBook.Infrastructure.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class ProfessionalServices : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Role",
                table: "Workers",
                type: "character varying(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "WeeklyHoursTenThousandths",
                table: "Workers",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RateSource",
                table: "TimeEntries",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Edition",
                table: "Companies",
                type: "character varying(30)",
                maxLength: 30,
                nullable: false,
                defaultValue: "General");

            migrationBuilder.CreateTable(
                name: "BillingRates",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CustomerId = table.Column<Guid>(type: "uuid", nullable: true),
                    WorkerId = table.Column<Guid>(type: "uuid", nullable: true),
                    Role = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    ItemId = table.Column<Guid>(type: "uuid", nullable: true),
                    RateTenThousandths = table.Column<long>(type: "bigint", nullable: false),
                    StartsOn = table.Column<DateOnly>(type: "date", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BillingRates", x => x.Id);
                    table.ForeignKey(
                        name: "FK_BillingRates_Customers_CustomerId",
                        column: x => x.CustomerId,
                        principalTable: "Customers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_BillingRates_Items_ItemId",
                        column: x => x.ItemId,
                        principalTable: "Items",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_BillingRates_Workers_WorkerId",
                        column: x => x.WorkerId,
                        principalTable: "Workers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_BillingRates_CustomerId",
                table: "BillingRates",
                column: "CustomerId");

            migrationBuilder.CreateIndex(
                name: "IX_BillingRates_ItemId",
                table: "BillingRates",
                column: "ItemId");

            migrationBuilder.CreateIndex(
                name: "IX_BillingRates_WorkerId",
                table: "BillingRates",
                column: "WorkerId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "BillingRates");

            migrationBuilder.DropColumn(
                name: "Role",
                table: "Workers");

            migrationBuilder.DropColumn(
                name: "WeeklyHoursTenThousandths",
                table: "Workers");

            migrationBuilder.DropColumn(
                name: "RateSource",
                table: "TimeEntries");

            migrationBuilder.DropColumn(
                name: "Edition",
                table: "Companies");
        }
    }
}
