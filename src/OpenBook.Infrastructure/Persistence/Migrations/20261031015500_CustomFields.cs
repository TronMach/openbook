using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenBook.Infrastructure.Persistence.Migrations
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
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Target = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 60, nullable: false),
                    HelpText = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    Type = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    Options = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: true),
                    IsRequired = table.Column<bool>(type: "INTEGER", nullable: false),
                    DefaultValue = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: true),
                    Documents = table.Column<int>(type: "INTEGER", nullable: false),
                    Placement = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    ShowInList = table.Column<bool>(type: "INTEGER", nullable: false),
                    PrintOnForms = table.Column<bool>(type: "INTEGER", nullable: false),
                    MaxLength = table.Column<int>(type: "INTEGER", nullable: true),
                    MinimumTenThousandths = table.Column<long>(type: "INTEGER", nullable: true),
                    MaximumTenThousandths = table.Column<long>(type: "INTEGER", nullable: true),
                    Position = table.Column<int>(type: "INTEGER", nullable: false),
                    IsActive = table.Column<bool>(type: "INTEGER", nullable: false),
                    PresetKey = table.Column<string>(type: "TEXT", maxLength: 50, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CustomFields", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "CustomFieldValues",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    FieldId = table.Column<Guid>(type: "TEXT", nullable: false),
                    EntityType = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    EntityId = table.Column<Guid>(type: "TEXT", nullable: false),
                    LineNumber = table.Column<int>(type: "INTEGER", nullable: false),
                    Value = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: false)
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
