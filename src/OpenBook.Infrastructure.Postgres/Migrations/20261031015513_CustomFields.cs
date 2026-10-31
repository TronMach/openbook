using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenBook.Infrastructure.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class CustomFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CustomFields",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Target = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Name = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    HelpText = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    Type = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Options = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    IsRequired = table.Column<bool>(type: "boolean", nullable: false),
                    DefaultValue = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    Documents = table.Column<int>(type: "integer", nullable: false),
                    Placement = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    ShowInList = table.Column<bool>(type: "boolean", nullable: false),
                    PrintOnForms = table.Column<bool>(type: "boolean", nullable: false),
                    MaxLength = table.Column<int>(type: "integer", nullable: true),
                    MinimumTenThousandths = table.Column<long>(type: "bigint", nullable: true),
                    MaximumTenThousandths = table.Column<long>(type: "bigint", nullable: true),
                    Position = table.Column<int>(type: "integer", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    PresetKey = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CustomFields", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "CustomFieldValues",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    FieldId = table.Column<Guid>(type: "uuid", nullable: false),
                    EntityType = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    EntityId = table.Column<Guid>(type: "uuid", nullable: false),
                    LineNumber = table.Column<int>(type: "integer", nullable: false),
                    Value = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CustomFieldValues", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CustomFieldValues_CustomFields_FieldId",
                        column: x => x.FieldId,
                        principalTable: "CustomFields",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CustomFieldValues_EntityId_FieldId_LineNumber",
                table: "CustomFieldValues",
                columns: new[] { "EntityId", "FieldId", "LineNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CustomFieldValues_EntityType_LineNumber",
                table: "CustomFieldValues",
                columns: new[] { "EntityType", "LineNumber" });

            migrationBuilder.CreateIndex(
                name: "IX_CustomFieldValues_FieldId",
                table: "CustomFieldValues",
                column: "FieldId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CustomFieldValues");

            migrationBuilder.DropTable(
                name: "CustomFields");
        }
    }
}
