using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenBook.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ChannelConnections : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "ClearingAccountId",
                table: "SalesChannels",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LastError",
                table: "SalesChannels",
                type: "TEXT",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "LastSyncAt",
                table: "SalesChannels",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "LastSyncFailed",
                table: "SalesChannels",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "ProtectedCredential",
                table: "SalesChannels",
                type: "TEXT",
                maxLength: 2000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ShopDomain",
                table: "SalesChannels",
                type: "TEXT",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<DateOnly>(
                name: "SyncedThrough",
                table: "SalesChannels",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_SalesChannels_ClearingAccountId",
                table: "SalesChannels",
                column: "ClearingAccountId");

            migrationBuilder.AddForeignKey(
                name: "FK_SalesChannels_Accounts_ClearingAccountId",
                table: "SalesChannels",
                column: "ClearingAccountId",
                principalTable: "Accounts",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_SalesChannels_Accounts_ClearingAccountId",
                table: "SalesChannels");

            migrationBuilder.DropIndex(
                name: "IX_SalesChannels_ClearingAccountId",
                table: "SalesChannels");

            migrationBuilder.DropColumn(
                name: "ClearingAccountId",
                table: "SalesChannels");

            migrationBuilder.DropColumn(
                name: "LastError",
                table: "SalesChannels");

            migrationBuilder.DropColumn(
                name: "LastSyncAt",
                table: "SalesChannels");

            migrationBuilder.DropColumn(
                name: "LastSyncFailed",
                table: "SalesChannels");

            migrationBuilder.DropColumn(
                name: "ProtectedCredential",
                table: "SalesChannels");

            migrationBuilder.DropColumn(
                name: "ShopDomain",
                table: "SalesChannels");

            migrationBuilder.DropColumn(
                name: "SyncedThrough",
                table: "SalesChannels");
        }
    }
}
