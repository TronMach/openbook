using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenBook.Infrastructure.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class OnlinePaymentProviders : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ProviderLinkId",
                table: "PaymentLinks",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ClientId",
                table: "OnlinePaymentSettings",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "Sandbox",
                table: "OnlinePaymentSettings",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "WebhookId",
                table: "OnlinePaymentSettings",
                type: "character varying(50)",
                maxLength: 50,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ProviderLinkId",
                table: "PaymentLinks");

            migrationBuilder.DropColumn(
                name: "ClientId",
                table: "OnlinePaymentSettings");

            migrationBuilder.DropColumn(
                name: "Sandbox",
                table: "OnlinePaymentSettings");

            migrationBuilder.DropColumn(
                name: "WebhookId",
                table: "OnlinePaymentSettings");
        }
    }
}
