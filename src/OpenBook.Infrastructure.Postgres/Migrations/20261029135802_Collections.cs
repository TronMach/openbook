using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenBook.Infrastructure.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class Collections : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "LateFeeForInvoiceId",
                table: "Invoices",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "BillWithParent",
                table: "Customers",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "NoLateFees",
                table: "Customers",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "NoReminders",
                table: "Customers",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<Guid>(
                name: "ParentId",
                table: "Customers",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "BadDebtWriteOffs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CustomerId = table.Column<Guid>(type: "uuid", nullable: false),
                    InvoiceId = table.Column<Guid>(type: "uuid", nullable: false),
                    Date = table.Column<DateOnly>(type: "date", nullable: false),
                    AmountMinor = table.Column<long>(type: "bigint", nullable: false),
                    ExpenseAccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    ReceivableAccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    Memo = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    JournalEntryId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BadDebtWriteOffs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_BadDebtWriteOffs_Accounts_ExpenseAccountId",
                        column: x => x.ExpenseAccountId,
                        principalTable: "Accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_BadDebtWriteOffs_Accounts_ReceivableAccountId",
                        column: x => x.ReceivableAccountId,
                        principalTable: "Accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_BadDebtWriteOffs_Customers_CustomerId",
                        column: x => x.CustomerId,
                        principalTable: "Customers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_BadDebtWriteOffs_Invoices_InvoiceId",
                        column: x => x.InvoiceId,
                        principalTable: "Invoices",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_BadDebtWriteOffs_JournalEntries_JournalEntryId",
                        column: x => x.JournalEntryId,
                        principalTable: "JournalEntries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "CollectionSettings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    LateFeesEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    LateFeesAutomatic = table.Column<bool>(type: "boolean", nullable: false),
                    LateFeePercentTenThousandths = table.Column<long>(type: "bigint", nullable: false),
                    LateFeeAmountMinor = table.Column<long>(type: "bigint", nullable: false),
                    LateFeeGraceDays = table.Column<int>(type: "integer", nullable: false),
                    LateFeeRepeatDays = table.Column<int>(type: "integer", nullable: false),
                    LateFeeItemId = table.Column<Guid>(type: "uuid", nullable: true),
                    RemindersEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    ReminderSchedule = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    ReminderSubject = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    ReminderBody = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CollectionSettings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CollectionSettings_Items_LateFeeItemId",
                        column: x => x.LateFeeItemId,
                        principalTable: "Items",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "InvoiceReminders",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    InvoiceId = table.Column<Guid>(type: "uuid", nullable: false),
                    Step = table.Column<int>(type: "integer", nullable: false),
                    SentAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    To = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InvoiceReminders", x => x.Id);
                    table.ForeignKey(
                        name: "FK_InvoiceReminders_Invoices_InvoiceId",
                        column: x => x.InvoiceId,
                        principalTable: "Invoices",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Invoices_LateFeeForInvoiceId",
                table: "Invoices",
                column: "LateFeeForInvoiceId");

            migrationBuilder.CreateIndex(
                name: "IX_Customers_ParentId",
                table: "Customers",
                column: "ParentId");

            migrationBuilder.CreateIndex(
                name: "IX_BadDebtWriteOffs_CustomerId_Date",
                table: "BadDebtWriteOffs",
                columns: new[] { "CustomerId", "Date" });

            migrationBuilder.CreateIndex(
                name: "IX_BadDebtWriteOffs_ExpenseAccountId",
                table: "BadDebtWriteOffs",
                column: "ExpenseAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_BadDebtWriteOffs_InvoiceId",
                table: "BadDebtWriteOffs",
                column: "InvoiceId");

            migrationBuilder.CreateIndex(
                name: "IX_BadDebtWriteOffs_JournalEntryId",
                table: "BadDebtWriteOffs",
                column: "JournalEntryId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_BadDebtWriteOffs_ReceivableAccountId",
                table: "BadDebtWriteOffs",
                column: "ReceivableAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_CollectionSettings_LateFeeItemId",
                table: "CollectionSettings",
                column: "LateFeeItemId");

            migrationBuilder.CreateIndex(
                name: "IX_InvoiceReminders_InvoiceId_Step",
                table: "InvoiceReminders",
                columns: new[] { "InvoiceId", "Step" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_Customers_Customers_ParentId",
                table: "Customers",
                column: "ParentId",
                principalTable: "Customers",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Invoices_Invoices_LateFeeForInvoiceId",
                table: "Invoices",
                column: "LateFeeForInvoiceId",
                principalTable: "Invoices",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Customers_Customers_ParentId",
                table: "Customers");

            migrationBuilder.DropForeignKey(
                name: "FK_Invoices_Invoices_LateFeeForInvoiceId",
                table: "Invoices");

            migrationBuilder.DropTable(
                name: "BadDebtWriteOffs");

            migrationBuilder.DropTable(
                name: "CollectionSettings");

            migrationBuilder.DropTable(
                name: "InvoiceReminders");

            migrationBuilder.DropIndex(
                name: "IX_Invoices_LateFeeForInvoiceId",
                table: "Invoices");

            migrationBuilder.DropIndex(
                name: "IX_Customers_ParentId",
                table: "Customers");

            migrationBuilder.DropColumn(
                name: "LateFeeForInvoiceId",
                table: "Invoices");

            migrationBuilder.DropColumn(
                name: "BillWithParent",
                table: "Customers");

            migrationBuilder.DropColumn(
                name: "NoLateFees",
                table: "Customers");

            migrationBuilder.DropColumn(
                name: "NoReminders",
                table: "Customers");

            migrationBuilder.DropColumn(
                name: "ParentId",
                table: "Customers");
        }
    }
}
