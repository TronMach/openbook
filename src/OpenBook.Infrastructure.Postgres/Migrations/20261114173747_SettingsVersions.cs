using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenBook.Infrastructure.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class SettingsVersions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "Version",
                table: "SignInSettings",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "Version",
                table: "PurchasingSettings",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "Version",
                table: "OnlinePaymentSettings",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "Version",
                table: "GiftSettings",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "Version",
                table: "FormDesigns",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "Version",
                table: "EmailSettings",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "Version",
                table: "DocumentReadingSettings",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "Version",
                table: "Companies",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "Version",
                table: "CollectionSettings",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "Version",
                table: "BackupSchedules",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "Version",
                table: "AchSettings",
                type: "integer",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Version",
                table: "SignInSettings");

            migrationBuilder.DropColumn(
                name: "Version",
                table: "PurchasingSettings");

            migrationBuilder.DropColumn(
                name: "Version",
                table: "OnlinePaymentSettings");

            migrationBuilder.DropColumn(
                name: "Version",
                table: "GiftSettings");

            migrationBuilder.DropColumn(
                name: "Version",
                table: "FormDesigns");

            migrationBuilder.DropColumn(
                name: "Version",
                table: "EmailSettings");

            migrationBuilder.DropColumn(
                name: "Version",
                table: "DocumentReadingSettings");

            migrationBuilder.DropColumn(
                name: "Version",
                table: "Companies");

            migrationBuilder.DropColumn(
                name: "Version",
                table: "CollectionSettings");

            migrationBuilder.DropColumn(
                name: "Version",
                table: "BackupSchedules");

            migrationBuilder.DropColumn(
                name: "Version",
                table: "AchSettings");
        }
    }
}
