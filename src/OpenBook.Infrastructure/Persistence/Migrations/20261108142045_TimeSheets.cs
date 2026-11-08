using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenBook.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class TimeSheets : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "TimeSheets",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    WorkerId = table.Column<Guid>(type: "TEXT", nullable: false),
                    WeekStart = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    HoursTenThousandths = table.Column<long>(type: "INTEGER", nullable: false),
                    SubmittedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    SubmittedBy = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    ReviewedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    ReviewedBy = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    Note = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TimeSheets", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TimeSheets_Workers_WorkerId",
                        column: x => x.WorkerId,
                        principalTable: "Workers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_TimeSheets_Status",
                table: "TimeSheets",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_TimeSheets_WorkerId_WeekStart",
                table: "TimeSheets",
                columns: new[] { "WorkerId", "WeekStart" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TimeSheets");
        }
    }
}
