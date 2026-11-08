using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenBook.Infrastructure.Postgres.Migrations
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
                    EffectiveFrom = table.Column<DateOnly>(type: "date", nullable: false),
                    LoanId = table.Column<Guid>(type: "uuid", nullable: false),
                    RatePercentTenThousandths = table.Column<long>(type: "bigint", nullable: false),
                    PaymentAmountMinor = table.Column<long>(type: "bigint", nullable: true)
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
