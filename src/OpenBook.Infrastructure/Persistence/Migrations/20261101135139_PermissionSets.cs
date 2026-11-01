using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenBook.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class PermissionSets : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "PermissionSetId",
                table: "Users",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "PermissionSets",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 60, nullable: false),
                    Description = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    Levels = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    IsActive = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PermissionSets", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Users_PermissionSetId",
                table: "Users",
                column: "PermissionSetId");

            migrationBuilder.AddForeignKey(
                name: "FK_Users_PermissionSets_PermissionSetId",
                table: "Users",
                column: "PermissionSetId",
                principalTable: "PermissionSets",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Users_PermissionSets_PermissionSetId",
                table: "Users");

            migrationBuilder.DropTable(
                name: "PermissionSets");

            migrationBuilder.DropIndex(
                name: "IX_Users_PermissionSetId",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "PermissionSetId",
                table: "Users");
        }
    }
}
