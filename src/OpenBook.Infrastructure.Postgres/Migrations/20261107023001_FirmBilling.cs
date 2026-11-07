using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace OpenBook.Infrastructure.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class FirmBilling : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "BilledAmountMinor",
                table: "TimeEntries",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "WriteDownReason",
                table: "TimeEntries",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "WrittenDownBy",
                table: "TimeEntries",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<DateOnly>(
                name: "WrittenDownOn",
                table: "TimeEntries",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Arrangement",
                table: "Projects",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "TimeAndMaterials");

            migrationBuilder.AddColumn<long>(
                name: "FeeMinor",
                table: "Projects",
                type: "bigint",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "PreBills",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CustomerId = table.Column<Guid>(type: "uuid", nullable: false),
                    Through = table.Column<DateOnly>(type: "date", nullable: false),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Memo = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    InvoiceId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CreatedBy = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PreBills", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PreBills_Customers_CustomerId",
                        column: x => x.CustomerId,
                        principalTable: "Customers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PreBills_Invoices_InvoiceId",
                        column: x => x.InvoiceId,
                        principalTable: "Invoices",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "PreBillLines",
                columns: table => new
                {
                    LineNumber = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    PreBillId = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    SourceId = table.Column<Guid>(type: "uuid", nullable: false),
                    Date = table.Column<DateOnly>(type: "date", nullable: false),
                    Description = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    HoursTenThousandths = table.Column<long>(type: "bigint", nullable: false),
                    WorkedMinor = table.Column<long>(type: "bigint", nullable: false),
                    BilledMinor = table.Column<long>(type: "bigint", nullable: false),
                    Reason = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PreBillLines", x => new { x.PreBillId, x.LineNumber });
                    table.ForeignKey(
                        name: "FK_PreBillLines_PreBills_PreBillId",
                        column: x => x.PreBillId,
                        principalTable: "PreBills",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PreBills_CustomerId",
                table: "PreBills",
                column: "CustomerId");

            migrationBuilder.CreateIndex(
                name: "IX_PreBills_InvoiceId",
                table: "PreBills",
                column: "InvoiceId");

            migrationBuilder.CreateIndex(
                name: "IX_PreBills_Status",
                table: "PreBills",
                column: "Status");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PreBillLines");

            migrationBuilder.DropTable(
                name: "PreBills");

            migrationBuilder.DropColumn(
                name: "BilledAmountMinor",
                table: "TimeEntries");

            migrationBuilder.DropColumn(
                name: "WriteDownReason",
                table: "TimeEntries");

            migrationBuilder.DropColumn(
                name: "WrittenDownBy",
                table: "TimeEntries");

            migrationBuilder.DropColumn(
                name: "WrittenDownOn",
                table: "TimeEntries");

            migrationBuilder.DropColumn(
                name: "Arrangement",
                table: "Projects");

            migrationBuilder.DropColumn(
                name: "FeeMinor",
                table: "Projects");
        }
    }
}
