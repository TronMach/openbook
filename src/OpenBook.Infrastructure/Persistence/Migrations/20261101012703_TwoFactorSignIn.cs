using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenBook.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class TwoFactorSignIn : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "LastTwoFactorStep",
                table: "Users",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ProtectedTwoFactorSecret",
                table: "Users",
                type: "TEXT",
                maxLength: 2000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RecoveryCodeHashes",
                table: "Users",
                type: "TEXT",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "TwoFactorEnabledAt",
                table: "Users",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "SignInSettings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    TwoFactorRequiredFrom = table.Column<string>(type: "TEXT", maxLength: 20, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SignInSettings", x => x.Id);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SignInSettings");

            migrationBuilder.DropColumn(
                name: "LastTwoFactorStep",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "ProtectedTwoFactorSecret",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "RecoveryCodeHashes",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "TwoFactorEnabledAt",
                table: "Users");
        }
    }
}
