using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenBook.Infrastructure.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class SalesTaxFilings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SalesTaxFilings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AgencyId = table.Column<Guid>(type: "uuid", nullable: false),
                    PeriodFrom = table.Column<DateOnly>(type: "date", nullable: false),
                    PeriodTo = table.Column<DateOnly>(type: "date", nullable: false),
                    FiledOn = table.Column<DateOnly>(type: "date", nullable: false),
                    AmountReportedMinor = table.Column<long>(type: "bigint", nullable: false),
                    Reference = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    FiledBy = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SalesTaxFilings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SalesTaxFilings_TaxAgencies_AgencyId",
                        column: x => x.AgencyId,
                        principalTable: "TaxAgencies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SalesTaxFilings_AgencyId_PeriodTo",
                table: "SalesTaxFilings",
                columns: new[] { "AgencyId", "PeriodTo" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SalesTaxFilings");
        }
    }
}
