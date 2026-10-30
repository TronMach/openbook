using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenBook.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class NonprofitBasics : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "FunctionalArea",
                table: "Segments",
                type: "TEXT",
                maxLength: 30,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "DonorRestricted",
                table: "Accounts",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "RestrictionReleases",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Date = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    ClassId = table.Column<Guid>(type: "TEXT", nullable: true),
                    AmountMinor = table.Column<long>(type: "INTEGER", nullable: false),
                    Memo = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    Status = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    JournalEntryId = table.Column<Guid>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RestrictionReleases", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RestrictionReleases_JournalEntries_JournalEntryId",
                        column: x => x.JournalEntryId,
                        principalTable: "JournalEntries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_RestrictionReleases_Segments_ClassId",
                        column: x => x.ClassId,
                        principalTable: "Segments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_RestrictionReleases_ClassId",
                table: "RestrictionReleases",
                column: "ClassId");

            migrationBuilder.CreateIndex(
                name: "IX_RestrictionReleases_JournalEntryId",
                table: "RestrictionReleases",
                column: "JournalEntryId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "RestrictionReleases");

            migrationBuilder.DropColumn(
                name: "FunctionalArea",
                table: "Segments");

            migrationBuilder.DropColumn(
                name: "DonorRestricted",
                table: "Accounts");
        }
    }
}
