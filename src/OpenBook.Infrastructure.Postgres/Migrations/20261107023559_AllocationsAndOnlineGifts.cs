using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace OpenBook.Infrastructure.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class AllocationsAndOnlineGifts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "PayoutId",
                table: "Donations",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "AllocationRules",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    AccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    PoolClassId = table.Column<Guid>(type: "uuid", nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AllocationRules", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AllocationRules_Accounts_AccountId",
                        column: x => x.AccountId,
                        principalTable: "Accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_AllocationRules_Segments_PoolClassId",
                        column: x => x.PoolClassId,
                        principalTable: "Segments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "AllocationRuns",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    RuleId = table.Column<Guid>(type: "uuid", nullable: false),
                    From = table.Column<DateOnly>(type: "date", nullable: false),
                    To = table.Column<DateOnly>(type: "date", nullable: false),
                    AmountMinor = table.Column<long>(type: "bigint", nullable: false),
                    JournalEntryId = table.Column<Guid>(type: "uuid", nullable: false),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AllocationRuns", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AllocationRuns_AllocationRules_RuleId",
                        column: x => x.RuleId,
                        principalTable: "AllocationRules",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_AllocationRuns_JournalEntries_JournalEntryId",
                        column: x => x.JournalEntryId,
                        principalTable: "JournalEntries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "AllocationShares",
                columns: table => new
                {
                    LineNumber = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    RuleId = table.Column<Guid>(type: "uuid", nullable: false),
                    ClassId = table.Column<Guid>(type: "uuid", nullable: false),
                    PercentTenThousandths = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AllocationShares", x => new { x.RuleId, x.LineNumber });
                    table.ForeignKey(
                        name: "FK_AllocationShares_AllocationRules_RuleId",
                        column: x => x.RuleId,
                        principalTable: "AllocationRules",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_AllocationShares_Segments_ClassId",
                        column: x => x.ClassId,
                        principalTable: "Segments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AllocationRules_AccountId",
                table: "AllocationRules",
                column: "AccountId");

            migrationBuilder.CreateIndex(
                name: "IX_AllocationRules_PoolClassId",
                table: "AllocationRules",
                column: "PoolClassId");

            migrationBuilder.CreateIndex(
                name: "IX_AllocationRuns_JournalEntryId",
                table: "AllocationRuns",
                column: "JournalEntryId");

            migrationBuilder.CreateIndex(
                name: "IX_AllocationRuns_RuleId",
                table: "AllocationRuns",
                column: "RuleId");

            migrationBuilder.CreateIndex(
                name: "IX_AllocationShares_ClassId",
                table: "AllocationShares",
                column: "ClassId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AllocationRuns");

            migrationBuilder.DropTable(
                name: "AllocationShares");

            migrationBuilder.DropTable(
                name: "AllocationRules");

            migrationBuilder.DropColumn(
                name: "PayoutId",
                table: "Donations");
        }
    }
}
