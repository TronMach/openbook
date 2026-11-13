using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenBook.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// The tamper-evident audit trail: a link per audit record with a SHA-256 hash over the one before, filled in by
    /// a background job. Append-only like the audit log, so links can't be rewritten without dropping the triggers.
    /// </summary>
    public partial class AuditChain : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AuditChain",
                columns: table => new
                {
                    Position = table.Column<long>(type: "INTEGER", nullable: false),
                    AuditRecordId = table.Column<long>(type: "INTEGER", nullable: false),
                    Hash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    LinkedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AuditChain", x => x.Position);
                    table.ForeignKey(
                        name: "FK_AuditChain_AuditRecords_AuditRecordId",
                        column: x => x.AuditRecordId,
                        principalTable: "AuditRecords",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AuditChain_AuditRecordId",
                table: "AuditChain",
                column: "AuditRecordId",
                unique: true);

            migrationBuilder.Sql("""
                CREATE TRIGGER "TR_AuditChain_NoUpdate" BEFORE UPDATE ON "AuditChain"
                BEGIN
                    SELECT RAISE(ABORT, 'audit: the chain is append-only');
                END;
                """);
            migrationBuilder.Sql("""
                CREATE TRIGGER "TR_AuditChain_NoDelete" BEFORE DELETE ON "AuditChain"
                BEGIN
                    SELECT RAISE(ABORT, 'audit: the chain is append-only');
                END;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""DROP TRIGGER "TR_AuditChain_NoDelete";""");
            migrationBuilder.Sql("""DROP TRIGGER "TR_AuditChain_NoUpdate";""");
            migrationBuilder.DropTable(
                name: "AuditChain");
        }
    }
}
