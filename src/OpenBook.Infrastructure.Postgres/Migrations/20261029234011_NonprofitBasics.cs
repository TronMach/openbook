using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenBook.Infrastructure.Postgres.Migrations
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
                type: "character varying(30)",
                maxLength: 30,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "DonorRestricted",
                table: "Accounts",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "RestrictionReleases",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Date = table.Column<DateOnly>(type: "date", nullable: false),
                    ClassId = table.Column<Guid>(type: "uuid", nullable: true),
                    AmountMinor = table.Column<long>(type: "bigint", nullable: false),
                    Memo = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    JournalEntryId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
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
