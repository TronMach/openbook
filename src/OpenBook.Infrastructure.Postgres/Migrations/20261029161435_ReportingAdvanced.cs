using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenBook.Infrastructure.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class ReportingAdvanced : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "DashboardPreferences",
                columns: table => new
                {
                    Username = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    HiddenPanels = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DashboardPreferences", x => x.Username);
                });

            migrationBuilder.CreateTable(
                name: "ReportSubscriptions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SavedReportId = table.Column<Guid>(type: "uuid", nullable: false),
                    Recipients = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    Frequency = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Day = table.Column<int>(type: "integer", nullable: false),
                    Hour = table.Column<int>(type: "integer", nullable: false),
                    Format = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    LastRunAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LastRunFailed = table.Column<bool>(type: "boolean", nullable: false),
                    LastError = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    CreatedBy = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReportSubscriptions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ReportSubscriptions_SavedReports_SavedReportId",
                        column: x => x.SavedReportId,
                        principalTable: "SavedReports",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ReportSubscriptions_SavedReportId",
                table: "ReportSubscriptions",
                column: "SavedReportId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DashboardPreferences");

            migrationBuilder.DropTable(
                name: "ReportSubscriptions");
        }
    }
}
