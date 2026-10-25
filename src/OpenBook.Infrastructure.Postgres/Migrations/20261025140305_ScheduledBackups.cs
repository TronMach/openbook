using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenBook.Infrastructure.Postgres.Migrations
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
                    Id = table.Column<int>(type: "integer", nullable: false),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false),
                    Frequency = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Weekday = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Hour = table.Column<int>(type: "integer", nullable: false),
                    Destination = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    FolderPath = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    ServiceUrl = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    Region = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    Bucket = table.Column<string>(type: "character varying(63)", maxLength: 63, nullable: true),
                    Prefix = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    AccessKeyId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    ProtectedSecretKey = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    Keep = table.Column<int>(type: "integer", nullable: false),
                    LastAttemptAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LastSuccessAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LastFileName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    LastError = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true)
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
