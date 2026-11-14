using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenBook.Infrastructure.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class ExpenseClaims : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ExpenseClaims",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Number = table.Column<int>(type: "integer", nullable: false),
                    WorkerId = table.Column<Guid>(type: "uuid", nullable: false),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Memo = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    SubmittedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    SubmittedBy = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    ReviewedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ReviewedBy = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    Note = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    BillId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ExpenseClaims", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ExpenseClaims_Bills_BillId",
                        column: x => x.BillId,
                        principalTable: "Bills",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ExpenseClaims_Workers_WorkerId",
                        column: x => x.WorkerId,
                        principalTable: "Workers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ExpenseClaimLines",
                columns: table => new
                {
                    LineNumber = table.Column<int>(type: "integer", nullable: false),
                    ExpenseClaimId = table.Column<Guid>(type: "uuid", nullable: false),
                    Date = table.Column<DateOnly>(type: "date", nullable: false),
                    AccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    Description = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    AmountMinor = table.Column<long>(type: "bigint", nullable: false),
                    CustomerId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ExpenseClaimLines", x => new { x.ExpenseClaimId, x.LineNumber });
                    table.ForeignKey(
                        name: "FK_ExpenseClaimLines_Accounts_AccountId",
                        column: x => x.AccountId,
                        principalTable: "Accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ExpenseClaimLines_Customers_CustomerId",
                        column: x => x.CustomerId,
                        principalTable: "Customers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ExpenseClaimLines_ExpenseClaims_ExpenseClaimId",
                        column: x => x.ExpenseClaimId,
                        principalTable: "ExpenseClaims",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ExpenseClaimLines_AccountId",
                table: "ExpenseClaimLines",
                column: "AccountId");

            migrationBuilder.CreateIndex(
                name: "IX_ExpenseClaimLines_CustomerId",
                table: "ExpenseClaimLines",
                column: "CustomerId");

            migrationBuilder.CreateIndex(
                name: "IX_ExpenseClaims_BillId",
                table: "ExpenseClaims",
                column: "BillId");

            migrationBuilder.CreateIndex(
                name: "IX_ExpenseClaims_Number",
                table: "ExpenseClaims",
                column: "Number",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ExpenseClaims_Status",
                table: "ExpenseClaims",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_ExpenseClaims_WorkerId",
                table: "ExpenseClaims",
                column: "WorkerId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ExpenseClaimLines");

            migrationBuilder.DropTable(
                name: "ExpenseClaims");
        }
    }
}
