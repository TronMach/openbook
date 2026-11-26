using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenBook.Infrastructure.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class OwnerPayRules : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "PayBasis",
                table: "OwnerOperators",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "Typed");

            migrationBuilder.AddColumn<bool>(
                name: "PayEmptyMiles",
                table: "OwnerOperators",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<long>(
                name: "PayRateTenThousandths",
                table: "OwnerOperators",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.CreateTable(
                name: "OwnerPassThroughs",
                columns: table => new
                {
                    Charge = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    OwnerId = table.Column<Guid>(type: "uuid", nullable: false),
                    PercentTenThousandths = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OwnerPassThroughs", x => new { x.OwnerId, x.Charge });
                    table.ForeignKey(
                        name: "FK_OwnerPassThroughs_OwnerOperators_OwnerId",
                        column: x => x.OwnerId,
                        principalTable: "OwnerOperators",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "OwnerPassThroughs");

            migrationBuilder.DropColumn(
                name: "PayBasis",
                table: "OwnerOperators");

            migrationBuilder.DropColumn(
                name: "PayEmptyMiles",
                table: "OwnerOperators");

            migrationBuilder.DropColumn(
                name: "PayRateTenThousandths",
                table: "OwnerOperators");
        }
    }
}
