using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenBook.Infrastructure.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class ItemUnitsAndVariants : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "PackSizeTenThousandths",
                table: "Items",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PackUnit",
                table: "Items",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Unit",
                table: "Items",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "VariantOfId",
                table: "Items",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "VariantOptions",
                table: "Items",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Items_VariantOfId",
                table: "Items",
                column: "VariantOfId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Items_VariantOfId",
                table: "Items");

            migrationBuilder.DropColumn(
                name: "PackSizeTenThousandths",
                table: "Items");

            migrationBuilder.DropColumn(
                name: "PackUnit",
                table: "Items");

            migrationBuilder.DropColumn(
                name: "Unit",
                table: "Items");

            migrationBuilder.DropColumn(
                name: "VariantOfId",
                table: "Items");

            migrationBuilder.DropColumn(
                name: "VariantOptions",
                table: "Items");
        }
    }
}
