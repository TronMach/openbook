using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenBook.Infrastructure.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class ItemReordering : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "PreferredVendorId",
                table: "Items",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "ReorderQuantityTenThousandths",
                table: "Items",
                type: "bigint",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "PreferredVendorId",
                table: "Items");

            migrationBuilder.DropColumn(
                name: "ReorderQuantityTenThousandths",
                table: "Items");
        }
    }
}
