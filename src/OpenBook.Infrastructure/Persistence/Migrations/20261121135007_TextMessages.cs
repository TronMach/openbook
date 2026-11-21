using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenBook.Infrastructure.Persistence.Migrations
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
                type: "TEXT",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "TextsAllowedAt",
                table: "Customers",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "SentTexts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Kind = table.Column<string>(type: "TEXT", maxLength: 30, nullable: false),
                    EntityType = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    EntityId = table.Column<Guid>(type: "TEXT", nullable: false),
                    SentAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    SentBy = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    To = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    Body = table.Column<string>(type: "TEXT", maxLength: 1600, nullable: false),
                    DocumentLinkId = table.Column<Guid>(type: "TEXT", nullable: true),
                    ProviderId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true)
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
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    AccountSid = table.Column<string>(type: "TEXT", maxLength: 34, nullable: true),
                    ProtectedAuthToken = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    Sender = table.Column<string>(type: "TEXT", maxLength: 34, nullable: true),
                    Version = table.Column<int>(type: "INTEGER", nullable: false)
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
