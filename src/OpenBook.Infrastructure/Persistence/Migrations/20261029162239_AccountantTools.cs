using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenBook.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AccountantTools : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AdjustingEntries",
                columns: table => new
                {
                    JournalEntryId = table.Column<Guid>(type: "TEXT", nullable: false),
                    MarkedBy = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    MarkedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AdjustingEntries", x => x.JournalEntryId);
                    table.ForeignKey(
                        name: "FK_AdjustingEntries_JournalEntries_JournalEntryId",
                        column: x => x.JournalEntryId,
                        principalTable: "JournalEntries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ForecastScenarios",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    CollectionDelayDays = table.Column<int>(type: "INTEGER", nullable: false),
                    PaymentDelayDays = table.Column<int>(type: "INTEGER", nullable: false),
                    Notes = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ForecastScenarios", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ForecastScenarioItems",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Description = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    AmountMinor = table.Column<long>(type: "INTEGER", nullable: false),
                    StartDate = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    Recurrence = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    EndDate = table.Column<DateOnly>(type: "TEXT", nullable: true),
                    ForecastScenarioId = table.Column<Guid>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ForecastScenarioItems", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ForecastScenarioItems_ForecastScenarios_ForecastScenarioId",
                        column: x => x.ForecastScenarioId,
                        principalTable: "ForecastScenarios",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ForecastScenarioItems_ForecastScenarioId",
                table: "ForecastScenarioItems",
                column: "ForecastScenarioId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AdjustingEntries");

            migrationBuilder.DropTable(
                name: "ForecastScenarioItems");

            migrationBuilder.DropTable(
                name: "ForecastScenarios");
        }
    }
}
