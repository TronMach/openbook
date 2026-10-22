using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenBook.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialLedger : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Accounts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Number = table.Column<string>(type: "TEXT", maxLength: 20, nullable: true),
                    Name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Type = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    ParentId = table.Column<Guid>(type: "TEXT", nullable: true),
                    IsActive = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Accounts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Accounts_Accounts_ParentId",
                        column: x => x.ParentId,
                        principalTable: "Accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "AuditRecords",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    OccurredAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    Actor = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Action = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    EntityType = table.Column<string>(type: "TEXT", maxLength: 60, nullable: false),
                    EntityId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Before = table.Column<string>(type: "TEXT", nullable: true),
                    After = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AuditRecords", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "JournalEntries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Number = table.Column<long>(type: "INTEGER", nullable: false),
                    Date = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    Currency = table.Column<string>(type: "TEXT", maxLength: 3, nullable: false),
                    Memo = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    Kind = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    Source = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    SourceId = table.Column<Guid>(type: "TEXT", nullable: true),
                    ReversesEntryId = table.Column<Guid>(type: "TEXT", nullable: true),
                    PostedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    VoidedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    VoidReason = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    IsSealed = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_JournalEntries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_JournalEntries_JournalEntries_ReversesEntryId",
                        column: x => x.ReversesEntryId,
                        principalTable: "JournalEntries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "JournalLines",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    LineNumber = table.Column<int>(type: "INTEGER", nullable: false),
                    AccountId = table.Column<Guid>(type: "TEXT", nullable: false),
                    AmountMinor = table.Column<long>(type: "INTEGER", nullable: false),
                    Memo = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    JournalEntryId = table.Column<Guid>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_JournalLines", x => x.Id);
                    table.CheckConstraint("CK_JournalLines_AmountMinor_NonZero", "\"AmountMinor\" <> 0");
                    table.ForeignKey(
                        name: "FK_JournalLines_Accounts_AccountId",
                        column: x => x.AccountId,
                        principalTable: "Accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_JournalLines_JournalEntries_JournalEntryId",
                        column: x => x.JournalEntryId,
                        principalTable: "JournalEntries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Accounts_Number",
                table: "Accounts",
                column: "Number",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Accounts_ParentId",
                table: "Accounts",
                column: "ParentId");

            migrationBuilder.CreateIndex(
                name: "IX_AuditRecords_EntityType_EntityId",
                table: "AuditRecords",
                columns: new[] { "EntityType", "EntityId" });

            migrationBuilder.CreateIndex(
                name: "IX_JournalEntries_Date",
                table: "JournalEntries",
                column: "Date");

            migrationBuilder.CreateIndex(
                name: "IX_JournalEntries_Number",
                table: "JournalEntries",
                column: "Number",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_JournalEntries_ReversesEntryId",
                table: "JournalEntries",
                column: "ReversesEntryId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_JournalLines_AccountId",
                table: "JournalLines",
                column: "AccountId");

            migrationBuilder.CreateIndex(
                name: "IX_JournalLines_JournalEntryId_LineNumber",
                table: "JournalLines",
                columns: new[] { "JournalEntryId", "LineNumber" },
                unique: true);

            foreach (var sql in Triggers)
                migrationBuilder.Sql(sql);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            foreach (var sql in TriggerNames.Select(n => $"DROP TRIGGER IF EXISTS \"{n}\";"))
                migrationBuilder.Sql(sql);

            migrationBuilder.DropTable(
                name: "AuditRecords");

            migrationBuilder.DropTable(
                name: "JournalLines");

            migrationBuilder.DropTable(
                name: "Accounts");

            migrationBuilder.DropTable(
                name: "JournalEntries");
        }
    }
}
