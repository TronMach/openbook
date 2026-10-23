using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenBook.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class PayablesComplete : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Form1099Box",
                table: "Vendors",
                type: "TEXT",
                maxLength: 40,
                nullable: false,
                defaultValue: "NecNonemployeeCompensation");

            migrationBuilder.AddColumn<string>(
                name: "TaxId",
                table: "Vendors",
                type: "TEXT",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "Track1099",
                table: "Vendors",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<Guid>(
                name: "RecurringBillId",
                table: "Bills",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "Attachments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    EntityType = table.Column<string>(type: "TEXT", maxLength: 30, nullable: false),
                    EntityId = table.Column<Guid>(type: "TEXT", nullable: false),
                    FileName = table.Column<string>(type: "TEXT", maxLength: 255, nullable: false),
                    ContentType = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Size = table.Column<long>(type: "INTEGER", nullable: false),
                    AddedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    AddedBy = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Attachments", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "RecurringBills",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    VendorId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Memo = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    Frequency = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    Interval = table.Column<int>(type: "INTEGER", nullable: false),
                    StartDate = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    EndDate = table.Column<DateOnly>(type: "TEXT", nullable: true),
                    MaxOccurrences = table.Column<int>(type: "INTEGER", nullable: true),
                    OccurrencesPosted = table.Column<int>(type: "INTEGER", nullable: false),
                    AutoCreate = table.Column<bool>(type: "INTEGER", nullable: false),
                    IsActive = table.Column<bool>(type: "INTEGER", nullable: false),
                    LastBillId = table.Column<Guid>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RecurringBills", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RecurringBills_Vendors_VendorId",
                        column: x => x.VendorId,
                        principalTable: "Vendors",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "VendorCredits",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    VendorId = table.Column<Guid>(type: "TEXT", nullable: false),
                    PayableAccountId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Reference = table.Column<string>(type: "TEXT", maxLength: 30, nullable: true),
                    Date = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    Memo = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    TotalMinor = table.Column<long>(type: "INTEGER", nullable: false),
                    AmountRefundedMinor = table.Column<long>(type: "INTEGER", nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    JournalEntryId = table.Column<Guid>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VendorCredits", x => x.Id);
                    table.ForeignKey(
                        name: "FK_VendorCredits_Accounts_PayableAccountId",
                        column: x => x.PayableAccountId,
                        principalTable: "Accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_VendorCredits_JournalEntries_JournalEntryId",
                        column: x => x.JournalEntryId,
                        principalTable: "JournalEntries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_VendorCredits_Vendors_VendorId",
                        column: x => x.VendorId,
                        principalTable: "Vendors",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "AttachmentContents",
                columns: table => new
                {
                    AttachmentId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Data = table.Column<byte[]>(type: "BLOB", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AttachmentContents", x => x.AttachmentId);
                    table.ForeignKey(
                        name: "FK_AttachmentContents_Attachments_AttachmentId",
                        column: x => x.AttachmentId,
                        principalTable: "Attachments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "RecurringBillLines",
                columns: table => new
                {
                    LineNumber = table.Column<int>(type: "INTEGER", nullable: false),
                    RecurringBillId = table.Column<Guid>(type: "TEXT", nullable: false),
                    AccountId = table.Column<Guid>(type: "TEXT", nullable: false),
                    AmountMinor = table.Column<long>(type: "INTEGER", nullable: false),
                    Description = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RecurringBillLines", x => new { x.RecurringBillId, x.LineNumber });
                    table.ForeignKey(
                        name: "FK_RecurringBillLines_Accounts_AccountId",
                        column: x => x.AccountId,
                        principalTable: "Accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_RecurringBillLines_RecurringBills_RecurringBillId",
                        column: x => x.RecurringBillId,
                        principalTable: "RecurringBills",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "VendorCreditApplications",
                columns: table => new
                {
                    BillId = table.Column<Guid>(type: "TEXT", nullable: false),
                    VendorCreditId = table.Column<Guid>(type: "TEXT", nullable: false),
                    AmountMinor = table.Column<long>(type: "INTEGER", nullable: false),
                    AppliedOn = table.Column<DateOnly>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VendorCreditApplications", x => new { x.VendorCreditId, x.BillId });
                    table.ForeignKey(
                        name: "FK_VendorCreditApplications_Bills_BillId",
                        column: x => x.BillId,
                        principalTable: "Bills",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_VendorCreditApplications_VendorCredits_VendorCreditId",
                        column: x => x.VendorCreditId,
                        principalTable: "VendorCredits",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "VendorCreditLines",
                columns: table => new
                {
                    LineNumber = table.Column<int>(type: "INTEGER", nullable: false),
                    VendorCreditId = table.Column<Guid>(type: "TEXT", nullable: false),
                    AccountId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Description = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    AmountMinor = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VendorCreditLines", x => new { x.VendorCreditId, x.LineNumber });
                    table.ForeignKey(
                        name: "FK_VendorCreditLines_Accounts_AccountId",
                        column: x => x.AccountId,
                        principalTable: "Accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_VendorCreditLines_VendorCredits_VendorCreditId",
                        column: x => x.VendorCreditId,
                        principalTable: "VendorCredits",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "VendorRefunds",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    VendorId = table.Column<Guid>(type: "TEXT", nullable: false),
                    VendorCreditId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Date = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    AmountMinor = table.Column<long>(type: "INTEGER", nullable: false),
                    DepositAccountId = table.Column<Guid>(type: "TEXT", nullable: false),
                    PayableAccountId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Reference = table.Column<string>(type: "TEXT", maxLength: 30, nullable: true),
                    Memo = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    Status = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    JournalEntryId = table.Column<Guid>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VendorRefunds", x => x.Id);
                    table.ForeignKey(
                        name: "FK_VendorRefunds_Accounts_DepositAccountId",
                        column: x => x.DepositAccountId,
                        principalTable: "Accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_VendorRefunds_Accounts_PayableAccountId",
                        column: x => x.PayableAccountId,
                        principalTable: "Accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_VendorRefunds_JournalEntries_JournalEntryId",
                        column: x => x.JournalEntryId,
                        principalTable: "JournalEntries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_VendorRefunds_VendorCredits_VendorCreditId",
                        column: x => x.VendorCreditId,
                        principalTable: "VendorCredits",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_VendorRefunds_Vendors_VendorId",
                        column: x => x.VendorId,
                        principalTable: "Vendors",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Bills_RecurringBillId",
                table: "Bills",
                column: "RecurringBillId");

            migrationBuilder.CreateIndex(
                name: "IX_Attachments_EntityType_EntityId",
                table: "Attachments",
                columns: new[] { "EntityType", "EntityId" });

            migrationBuilder.CreateIndex(
                name: "IX_RecurringBillLines_AccountId",
                table: "RecurringBillLines",
                column: "AccountId");

            migrationBuilder.CreateIndex(
                name: "IX_RecurringBills_VendorId",
                table: "RecurringBills",
                column: "VendorId");

            migrationBuilder.CreateIndex(
                name: "IX_VendorCreditApplications_BillId",
                table: "VendorCreditApplications",
                column: "BillId");

            migrationBuilder.CreateIndex(
                name: "IX_VendorCreditLines_AccountId",
                table: "VendorCreditLines",
                column: "AccountId");

            migrationBuilder.CreateIndex(
                name: "IX_VendorCredits_JournalEntryId",
                table: "VendorCredits",
                column: "JournalEntryId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_VendorCredits_PayableAccountId",
                table: "VendorCredits",
                column: "PayableAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_VendorCredits_VendorId_Date",
                table: "VendorCredits",
                columns: new[] { "VendorId", "Date" });

            migrationBuilder.CreateIndex(
                name: "IX_VendorRefunds_DepositAccountId",
                table: "VendorRefunds",
                column: "DepositAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_VendorRefunds_JournalEntryId",
                table: "VendorRefunds",
                column: "JournalEntryId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_VendorRefunds_PayableAccountId",
                table: "VendorRefunds",
                column: "PayableAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_VendorRefunds_VendorCreditId",
                table: "VendorRefunds",
                column: "VendorCreditId");

            migrationBuilder.CreateIndex(
                name: "IX_VendorRefunds_VendorId_Date",
                table: "VendorRefunds",
                columns: new[] { "VendorId", "Date" });

            migrationBuilder.AddForeignKey(
                name: "FK_Bills_RecurringBills_RecurringBillId",
                table: "Bills",
                column: "RecurringBillId",
                principalTable: "RecurringBills",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Bills_RecurringBills_RecurringBillId",
                table: "Bills");

            migrationBuilder.DropTable(
                name: "AttachmentContents");

            migrationBuilder.DropTable(
                name: "RecurringBillLines");

            migrationBuilder.DropTable(
                name: "VendorCreditApplications");

            migrationBuilder.DropTable(
                name: "VendorCreditLines");

            migrationBuilder.DropTable(
                name: "VendorRefunds");

            migrationBuilder.DropTable(
                name: "Attachments");

            migrationBuilder.DropTable(
                name: "RecurringBills");

            migrationBuilder.DropTable(
                name: "VendorCredits");

            migrationBuilder.DropIndex(
                name: "IX_Bills_RecurringBillId",
                table: "Bills");

            migrationBuilder.DropColumn(
                name: "Form1099Box",
                table: "Vendors");

            migrationBuilder.DropColumn(
                name: "TaxId",
                table: "Vendors");

            migrationBuilder.DropColumn(
                name: "Track1099",
                table: "Vendors");

            migrationBuilder.DropColumn(
                name: "RecurringBillId",
                table: "Bills");
        }
    }
}
