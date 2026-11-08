using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenBook.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class LoanRateChanges : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "LoanRateChanges",
                columns: table => new
                {
                    EffectiveFrom = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    LoanId = table.Column<Guid>(type: "TEXT", nullable: false),
                    RatePercentTenThousandths = table.Column<long>(type: "INTEGER", nullable: false),
                    PaymentAmountMinor = table.Column<long>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LoanRateChanges", x => new { x.LoanId, x.EffectiveFrom });
                    table.ForeignKey(
                        name: "FK_LoanRateChanges_Loans_LoanId",
                        column: x => x.LoanId,
                        principalTable: "Loans",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "LoanRateChanges");
        }
    }
}
