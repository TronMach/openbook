using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenBook.Infrastructure.Postgres.Migrations
{
    /// <summary>The SQLite migration of the same name, in PL/pgSQL, plus a TRUNCATE guard as the audit log has.</summary>
    public partial class AuditChain : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AuditChain",
                columns: table => new
                {
                    Position = table.Column<long>(type: "bigint", nullable: false),
                    AuditRecordId = table.Column<long>(type: "bigint", nullable: false),
                    Hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    LinkedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
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
                CREATE FUNCTION "ob_audit_chain_append_only"() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                    RAISE EXCEPTION 'audit: the chain is append-only';
                END $$;
                """);
            migrationBuilder.Sql("""
                CREATE TRIGGER "TR_AuditChain_NoUpdate" BEFORE UPDATE ON "AuditChain"
                FOR EACH ROW EXECUTE FUNCTION "ob_audit_chain_append_only"();
                """);
            migrationBuilder.Sql("""
                CREATE TRIGGER "TR_AuditChain_NoDelete" BEFORE DELETE ON "AuditChain"
                FOR EACH ROW EXECUTE FUNCTION "ob_audit_chain_append_only"();
                """);
            migrationBuilder.Sql("""
                CREATE TRIGGER "TR_AuditChain_NoTruncate" BEFORE TRUNCATE ON "AuditChain"
                FOR EACH STATEMENT EXECUTE FUNCTION "ob_audit_chain_append_only"();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""DROP TRIGGER "TR_AuditChain_NoTruncate" ON "AuditChain";""");
            migrationBuilder.Sql("""DROP TRIGGER "TR_AuditChain_NoDelete" ON "AuditChain";""");
            migrationBuilder.Sql("""DROP TRIGGER "TR_AuditChain_NoUpdate" ON "AuditChain";""");
            migrationBuilder.DropTable(
                name: "AuditChain");
            migrationBuilder.Sql("""DROP FUNCTION "ob_audit_chain_append_only"();""");
        }
    }
}
