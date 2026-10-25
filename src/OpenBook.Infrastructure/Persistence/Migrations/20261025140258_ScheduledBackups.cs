using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenBook.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ScheduledBackups : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "BackupSchedules",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    Frequency = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    Weekday = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    Hour = table.Column<int>(type: "INTEGER", nullable: false),
                    Destination = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    FolderPath = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    ServiceUrl = table.Column<string>(type: "TEXT", maxLength: 300, nullable: true),
                    Region = table.Column<string>(type: "TEXT", maxLength: 50, nullable: true),
                    Bucket = table.Column<string>(type: "TEXT", maxLength: 63, nullable: true),
                    Prefix = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    AccessKeyId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    ProtectedSecretKey = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    Keep = table.Column<int>(type: "INTEGER", nullable: false),
                    LastAttemptAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    LastSuccessAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    LastFileName = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    LastError = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BackupSchedules", x => x.Id);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "BackupSchedules");
        }
    }
}
