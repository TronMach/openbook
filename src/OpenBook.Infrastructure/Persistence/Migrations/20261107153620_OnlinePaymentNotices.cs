using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpenBook.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class OnlinePaymentNotices : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "OnlinePaymentNotices",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    EventId = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Kind = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    PaymentLinkId = table.Column<Guid>(type: "TEXT", nullable: false),
                    InvoiceId = table.Column<Guid>(type: "TEXT", nullable: false),
                    CustomerPaymentId = table.Column<Guid>(type: "TEXT", nullable: true),
                    AmountMinor = table.Column<long>(type: "INTEGER", nullable: false),
                    Reason = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    ReceivedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    ResolvedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    ResolvedBy = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OnlinePaymentNotices", x => x.Id);
                    table.ForeignKey(
                        name: "FK_OnlinePaymentNotices_Invoices_InvoiceId",
                        column: x => x.InvoiceId,
                        principalTable: "Invoices",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_OnlinePaymentNotices_PaymentLinks_PaymentLinkId",
                        column: x => x.PaymentLinkId,
                        principalTable: "PaymentLinks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_OnlinePaymentNotices_EventId",
                table: "OnlinePaymentNotices",
                column: "EventId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_OnlinePaymentNotices_InvoiceId",
                table: "OnlinePaymentNotices",
                column: "InvoiceId");

            migrationBuilder.CreateIndex(
                name: "IX_OnlinePaymentNotices_PaymentLinkId",
                table: "OnlinePaymentNotices",
                column: "PaymentLinkId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "OnlinePaymentNotices");
        }
    }
}
