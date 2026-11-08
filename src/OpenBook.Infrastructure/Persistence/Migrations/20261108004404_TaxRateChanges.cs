using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenBook.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class TaxRateChanges : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "TaxRateChanges",
                columns: table => new
                {
                    EffectiveFrom = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    TaxRateId = table.Column<Guid>(type: "TEXT", nullable: false),
                    PercentTenThousandths = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TaxRateChanges", x => new { x.TaxRateId, x.EffectiveFrom });
                    table.ForeignKey(
                        name: "FK_TaxRateChanges_TaxRates_TaxRateId",
                        column: x => x.TaxRateId,
                        principalTable: "TaxRates",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TaxRateChanges");
        }
    }
}
