using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenBook.Infrastructure.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class ChannelFulfilment : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "MarketplaceWarehouseId",
                table: "SalesChannels",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "ByMarketplace",
                table: "ChannelOrderLines",
                type: "boolean",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "MarketplaceWarehouseId",
                table: "SalesChannels");

            migrationBuilder.DropColumn(
                name: "ByMarketplace",
                table: "ChannelOrderLines");
        }
    }
}
