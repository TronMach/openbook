using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenBook.Infrastructure.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class TextMessages : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "TextNumber",
                table: "Customers",
                type: "character varying(16)",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "TextsAllowedAt",
                table: "Customers",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "SentTexts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    EntityType = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    EntityId = table.Column<Guid>(type: "uuid", nullable: false),
                    SentAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    SentBy = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    To = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Body = table.Column<string>(type: "character varying(1600)", maxLength: 1600, nullable: false),
                    DocumentLinkId = table.Column<Guid>(type: "uuid", nullable: true),
                    ProviderId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SentTexts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SentTexts_DocumentLinks_DocumentLinkId",
                        column: x => x.DocumentLinkId,
                        principalTable: "DocumentLinks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "TextSettings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AccountSid = table.Column<string>(type: "character varying(34)", maxLength: 34, nullable: true),
                    ProtectedAuthToken = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    Sender = table.Column<string>(type: "character varying(34)", maxLength: 34, nullable: true),
                    Version = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TextSettings", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SentTexts_DocumentLinkId",
                table: "SentTexts",
                column: "DocumentLinkId");

            migrationBuilder.CreateIndex(
                name: "IX_SentTexts_EntityType_EntityId",
                table: "SentTexts",
                columns: new[] { "EntityType", "EntityId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SentTexts");

            migrationBuilder.DropTable(
                name: "TextSettings");

            migrationBuilder.DropColumn(
                name: "TextNumber",
                table: "Customers");

            migrationBuilder.DropColumn(
                name: "TextsAllowedAt",
                table: "Customers");
        }
    }
}
