using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenBook.Infrastructure.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class SingleSignOn : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ExternalLogin",
                table: "Users",
                type: "character varying(400)",
                maxLength: 400,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ProtectedSsoClientSecret",
                table: "SignInSettings",
                type: "character varying(2000)",
                maxLength: 2000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SsoAuthority",
                table: "SignInSettings",
                type: "character varying(300)",
                maxLength: 300,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SsoButtonLabel",
                table: "SignInSettings",
                type: "character varying(60)",
                maxLength: 60,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SsoClientId",
                table: "SignInSettings",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "SsoEnabled",
                table: "SignInSettings",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "SsoOnly",
                table: "SignInSettings",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "SsoProvider",
                table: "SignInSettings",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "Microsoft");

            migrationBuilder.AddColumn<string>(
                name: "SsoTenant",
                table: "SignInSettings",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Users_ExternalLogin",
                table: "Users",
                column: "ExternalLogin",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Users_ExternalLogin",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "ExternalLogin",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "ProtectedSsoClientSecret",
                table: "SignInSettings");

            migrationBuilder.DropColumn(
                name: "SsoAuthority",
                table: "SignInSettings");

            migrationBuilder.DropColumn(
                name: "SsoButtonLabel",
                table: "SignInSettings");

            migrationBuilder.DropColumn(
                name: "SsoClientId",
                table: "SignInSettings");

            migrationBuilder.DropColumn(
                name: "SsoEnabled",
                table: "SignInSettings");

            migrationBuilder.DropColumn(
                name: "SsoOnly",
                table: "SignInSettings");

            migrationBuilder.DropColumn(
                name: "SsoProvider",
                table: "SignInSettings");

            migrationBuilder.DropColumn(
                name: "SsoTenant",
                table: "SignInSettings");
        }
    }
}
