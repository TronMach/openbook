using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenBook.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class SettlementLoads : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Charge",
                table: "CarrierCodes",
                type: "TEXT",
                maxLength: 20,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "CarrierShares",
                columns: table => new
                {
                    Charge = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    CarrierId = table.Column<Guid>(type: "TEXT", nullable: false),
                    PercentTenThousandths = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CarrierShares", x => new { x.CarrierId, x.Charge });
                    table.ForeignKey(
                        name: "FK_CarrierShares_Carriers_CarrierId",
                        column: x => x.CarrierId,
                        principalTable: "Carriers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SettlementBilled",
                columns: table => new
                {
                    LineNumber = table.Column<int>(type: "INTEGER", nullable: false),
                    SettlementId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Load = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    Charge = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    AmountMinor = table.Column<long>(type: "INTEGER", nullable: false),
                    MilesTenThousandths = table.Column<long>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SettlementBilled", x => new { x.SettlementId, x.LineNumber });
                    table.ForeignKey(
                        name: "FK_SettlementBilled_Settlements_SettlementId",
                        column: x => x.SettlementId,
                        principalTable: "Settlements",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CarrierShares");

            migrationBuilder.DropTable(
                name: "SettlementBilled");

            migrationBuilder.DropColumn(
                name: "Charge",
                table: "CarrierCodes");
        }
    }
}
