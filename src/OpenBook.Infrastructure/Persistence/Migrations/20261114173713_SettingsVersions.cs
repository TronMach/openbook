using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenBook.Infrastructure.Persistence.Migrations
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
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "Version",
                table: "PurchasingSettings",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "Version",
                table: "OnlinePaymentSettings",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "Version",
                table: "GiftSettings",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "Version",
                table: "FormDesigns",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "Version",
                table: "EmailSettings",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "Version",
                table: "DocumentReadingSettings",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "Version",
                table: "Companies",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "Version",
                table: "CollectionSettings",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "Version",
                table: "BackupSchedules",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "Version",
                table: "AchSettings",
                type: "INTEGER",
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
