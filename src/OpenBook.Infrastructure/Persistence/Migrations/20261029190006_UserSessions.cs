using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenBook.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class UserSessions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "SessionId",
                table: "CaptureDevices",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "UserSessions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    UserId = table.Column<Guid>(type: "TEXT", nullable: false),
                    SecurityStamp = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Device = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    Address = table.Column<string>(type: "TEXT", maxLength: 45, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    LastSeenAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    EndedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserSessions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_UserSessions_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_UserSessions_UserId",
                table: "UserSessions",
                column: "UserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "UserSessions");

            migrationBuilder.DropColumn(
                name: "SessionId",
                table: "CaptureDevices");
        }
    }
}
