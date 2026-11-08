using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenBook.Infrastructure.Persistence.Migrations
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
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    AgencyId = table.Column<Guid>(type: "TEXT", nullable: false),
                    PeriodFrom = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    PeriodTo = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    FiledOn = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    AmountReportedMinor = table.Column<long>(type: "INTEGER", nullable: false),
                    Reference = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    FiledBy = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
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
