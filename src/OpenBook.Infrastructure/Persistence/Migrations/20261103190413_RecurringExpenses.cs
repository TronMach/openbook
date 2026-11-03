using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenBook.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class RecurringExpenses : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "RecurringExpenses",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    AccountId = table.Column<Guid>(type: "TEXT", nullable: false),
                    VendorId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Payee = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    Memo = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    Frequency = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    Interval = table.Column<int>(type: "INTEGER", nullable: false),
                    StartDate = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    EndDate = table.Column<DateOnly>(type: "TEXT", nullable: true),
                    MaxOccurrences = table.Column<int>(type: "INTEGER", nullable: true),
                    OccurrencesPosted = table.Column<int>(type: "INTEGER", nullable: false),
                    AutoCreate = table.Column<bool>(type: "INTEGER", nullable: false),
                    IsActive = table.Column<bool>(type: "INTEGER", nullable: false),
                    LastTransactionId = table.Column<Guid>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    LocationId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Tags = table.Column<string>(type: "TEXT", maxLength: 800, nullable: true),
                    Version = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RecurringExpenses", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RecurringExpenses_Accounts_AccountId",
                        column: x => x.AccountId,
                        principalTable: "Accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_RecurringExpenses_Segments_LocationId",
                        column: x => x.LocationId,
                        principalTable: "Segments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_RecurringExpenses_Vendors_VendorId",
                        column: x => x.VendorId,
                        principalTable: "Vendors",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "RecurringExpenseLines",
                columns: table => new
                {
                    LineNumber = table.Column<int>(type: "INTEGER", nullable: false),
                    RecurringExpenseId = table.Column<Guid>(type: "TEXT", nullable: false),
                    AccountId = table.Column<Guid>(type: "TEXT", nullable: false),
                    AmountMinor = table.Column<long>(type: "INTEGER", nullable: false),
                    Description = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    ClassId = table.Column<Guid>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RecurringExpenseLines", x => new { x.RecurringExpenseId, x.LineNumber });
                    table.ForeignKey(
                        name: "FK_RecurringExpenseLines_Accounts_AccountId",
                        column: x => x.AccountId,
                        principalTable: "Accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_RecurringExpenseLines_RecurringExpenses_RecurringExpenseId",
                        column: x => x.RecurringExpenseId,
                        principalTable: "RecurringExpenses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_RecurringExpenseLines_Segments_ClassId",
                        column: x => x.ClassId,
                        principalTable: "Segments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_RecurringExpenseLines_AccountId",
                table: "RecurringExpenseLines",
                column: "AccountId");

            migrationBuilder.CreateIndex(
                name: "IX_RecurringExpenseLines_ClassId",
                table: "RecurringExpenseLines",
                column: "ClassId");

            migrationBuilder.CreateIndex(
                name: "IX_RecurringExpenses_AccountId",
                table: "RecurringExpenses",
                column: "AccountId");

            migrationBuilder.CreateIndex(
                name: "IX_RecurringExpenses_LocationId",
                table: "RecurringExpenses",
                column: "LocationId");

            migrationBuilder.CreateIndex(
                name: "IX_RecurringExpenses_VendorId",
                table: "RecurringExpenses",
                column: "VendorId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "RecurringExpenseLines");

            migrationBuilder.DropTable(
                name: "RecurringExpenses");
        }
    }
}
