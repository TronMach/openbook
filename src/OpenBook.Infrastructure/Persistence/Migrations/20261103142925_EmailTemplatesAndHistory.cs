using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenBook.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class EmailTemplatesAndHistory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Bcc",
                table: "ScheduledEmails",
                type: "TEXT",
                maxLength: 4000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Cc",
                table: "ScheduledEmails",
                type: "TEXT",
                maxLength: 4000,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "DesignId",
                table: "ScheduledEmails",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "EmailStyle",
                table: "FormDesigns",
                type: "TEXT",
                maxLength: 10,
                nullable: false,
                defaultValue: "Branded");

            migrationBuilder.AddColumn<string>(
                name: "LinkAddress",
                table: "EmailSettings",
                type: "TEXT",
                maxLength: 300,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "EmailCc",
                table: "Customers",
                type: "TEXT",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "EmailPdf",
                table: "Customers",
                type: "TEXT",
                maxLength: 10,
                nullable: false,
                defaultValue: "Attach");

            migrationBuilder.CreateTable(
                name: "DocumentLinks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Kind = table.Column<string>(type: "TEXT", maxLength: 30, nullable: false),
                    DocumentId = table.Column<Guid>(type: "TEXT", nullable: false),
                    CustomerId = table.Column<Guid>(type: "TEXT", nullable: false),
                    TokenHash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    CreatedBy = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    RevokedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    FirstOpenedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    LastOpenedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DocumentLinks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DocumentLinks_Customers_CustomerId",
                        column: x => x.CustomerId,
                        principalTable: "Customers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "EmailTemplates",
                columns: table => new
                {
                    Kind = table.Column<string>(type: "TEXT", maxLength: 30, nullable: false),
                    Subject = table.Column<string>(type: "TEXT", maxLength: 300, nullable: false),
                    Body = table.Column<string>(type: "TEXT", maxLength: 20000, nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    UpdatedBy = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EmailTemplates", x => x.Kind);
                });

            migrationBuilder.CreateTable(
                name: "SentEmails",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Kind = table.Column<string>(type: "TEXT", maxLength: 30, nullable: false),
                    EntityType = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    EntityId = table.Column<Guid>(type: "TEXT", nullable: false),
                    SentAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    SentBy = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    To = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: false),
                    Cc = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: true),
                    Bcc = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: true),
                    Subject = table.Column<string>(type: "TEXT", maxLength: 300, nullable: false),
                    Body = table.Column<string>(type: "TEXT", maxLength: 20000, nullable: false),
                    Attachment = table.Column<string>(type: "TEXT", maxLength: 255, nullable: true),
                    DocumentLinkId = table.Column<Guid>(type: "TEXT", nullable: true),
                    DesignId = table.Column<Guid>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SentEmails", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SentEmails_DocumentLinks_DocumentLinkId",
                        column: x => x.DocumentLinkId,
                        principalTable: "DocumentLinks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SentEmails_FormDesigns_DesignId",
                        column: x => x.DesignId,
                        principalTable: "FormDesigns",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DocumentLinks_CustomerId",
                table: "DocumentLinks",
                column: "CustomerId");

            migrationBuilder.CreateIndex(
                name: "IX_DocumentLinks_DocumentId",
                table: "DocumentLinks",
                column: "DocumentId");

            migrationBuilder.CreateIndex(
                name: "IX_DocumentLinks_TokenHash",
                table: "DocumentLinks",
                column: "TokenHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SentEmails_DesignId",
                table: "SentEmails",
                column: "DesignId");

            migrationBuilder.CreateIndex(
                name: "IX_SentEmails_DocumentLinkId",
                table: "SentEmails",
                column: "DocumentLinkId");

            migrationBuilder.CreateIndex(
                name: "IX_SentEmails_EntityType_EntityId",
                table: "SentEmails",
                columns: new[] { "EntityType", "EntityId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "EmailTemplates");

            migrationBuilder.DropTable(
                name: "SentEmails");

            migrationBuilder.DropTable(
                name: "DocumentLinks");

            migrationBuilder.DropColumn(
                name: "Bcc",
                table: "ScheduledEmails");

            migrationBuilder.DropColumn(
                name: "Cc",
                table: "ScheduledEmails");

            migrationBuilder.DropColumn(
                name: "DesignId",
                table: "ScheduledEmails");

            migrationBuilder.DropColumn(
                name: "EmailStyle",
                table: "FormDesigns");

            migrationBuilder.DropColumn(
                name: "LinkAddress",
                table: "EmailSettings");

            migrationBuilder.DropColumn(
                name: "EmailCc",
                table: "Customers");

            migrationBuilder.DropColumn(
                name: "EmailPdf",
                table: "Customers");
        }
    }
}
