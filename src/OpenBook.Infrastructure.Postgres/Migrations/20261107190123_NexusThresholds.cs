using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenBook.Infrastructure.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class NexusThresholds : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "NexusThresholds",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Region = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    SalesAmountMinor = table.Column<long>(type: "bigint", nullable: false),
                    Transactions = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NexusThresholds", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_NexusThresholds_Region",
                table: "NexusThresholds",
                column: "Region",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "NexusThresholds");
        }
    }
}
