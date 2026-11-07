using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenBook.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Filing1099 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "TaxId",
                table: "Companies",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "Filings1099",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Year = table.Column<int>(type: "INTEGER", nullable: false),
                    VendorId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Box = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    AmountMinor = table.Column<long>(type: "INTEGER", nullable: false),
                    FiledOn = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    FiledBy = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    CopySentAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Filings1099", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Filings1099_Vendors_VendorId",
                        column: x => x.VendorId,
                        principalTable: "Vendors",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Filings1099_VendorId",
                table: "Filings1099",
                column: "VendorId");

            migrationBuilder.CreateIndex(
                name: "IX_Filings1099_Year_VendorId",
                table: "Filings1099",
                columns: new[] { "Year", "VendorId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Filings1099");

            migrationBuilder.DropColumn(
                name: "TaxId",
                table: "Companies");
        }
    }
}
