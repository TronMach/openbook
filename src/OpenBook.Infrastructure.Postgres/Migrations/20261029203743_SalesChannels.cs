using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace OpenBook.Infrastructure.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class SalesChannels : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SalesChannels",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Kind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    DepositAccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    SalesAccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    ShippingAccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    RefundsAccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    FeesAccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    TaxAccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    AdjustmentsAccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SalesChannels", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SalesChannels_Accounts_AdjustmentsAccountId",
                        column: x => x.AdjustmentsAccountId,
                        principalTable: "Accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SalesChannels_Accounts_DepositAccountId",
                        column: x => x.DepositAccountId,
                        principalTable: "Accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SalesChannels_Accounts_FeesAccountId",
                        column: x => x.FeesAccountId,
                        principalTable: "Accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SalesChannels_Accounts_RefundsAccountId",
                        column: x => x.RefundsAccountId,
                        principalTable: "Accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SalesChannels_Accounts_SalesAccountId",
                        column: x => x.SalesAccountId,
                        principalTable: "Accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SalesChannels_Accounts_ShippingAccountId",
                        column: x => x.ShippingAccountId,
                        principalTable: "Accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SalesChannels_Accounts_TaxAccountId",
                        column: x => x.TaxAccountId,
                        principalTable: "Accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ChannelPayouts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ChannelId = table.Column<Guid>(type: "uuid", nullable: false),
                    ExternalId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Date = table.Column<DateOnly>(type: "date", nullable: false),
                    PeriodStart = table.Column<DateOnly>(type: "date", nullable: true),
                    PeriodEnd = table.Column<DateOnly>(type: "date", nullable: true),
                    ReportedNetMinor = table.Column<long>(type: "bigint", nullable: true),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    JournalEntryId = table.Column<Guid>(type: "uuid", nullable: true),
                    ImportedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ChannelPayouts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ChannelPayouts_JournalEntries_JournalEntryId",
                        column: x => x.JournalEntryId,
                        principalTable: "JournalEntries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ChannelPayouts_SalesChannels_ChannelId",
                        column: x => x.ChannelId,
                        principalTable: "SalesChannels",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ChannelPayoutLines",
                columns: table => new
                {
                    LineNumber = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ChannelPayoutId = table.Column<Guid>(type: "uuid", nullable: false),
                    Date = table.Column<DateOnly>(type: "date", nullable: false),
                    Type = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    AmountMinor = table.Column<long>(type: "bigint", nullable: false),
                    Reference = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    Description = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    Region = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ChannelPayoutLines", x => new { x.ChannelPayoutId, x.LineNumber });
                    table.ForeignKey(
                        name: "FK_ChannelPayoutLines_ChannelPayouts_ChannelPayoutId",
                        column: x => x.ChannelPayoutId,
                        principalTable: "ChannelPayouts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ChannelPayouts_ChannelId_ExternalId",
                table: "ChannelPayouts",
                columns: new[] { "ChannelId", "ExternalId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ChannelPayouts_JournalEntryId",
                table: "ChannelPayouts",
                column: "JournalEntryId");

            migrationBuilder.CreateIndex(
                name: "IX_SalesChannels_AdjustmentsAccountId",
                table: "SalesChannels",
                column: "AdjustmentsAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_SalesChannels_DepositAccountId",
                table: "SalesChannels",
                column: "DepositAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_SalesChannels_FeesAccountId",
                table: "SalesChannels",
                column: "FeesAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_SalesChannels_RefundsAccountId",
                table: "SalesChannels",
                column: "RefundsAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_SalesChannels_SalesAccountId",
                table: "SalesChannels",
                column: "SalesAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_SalesChannels_ShippingAccountId",
                table: "SalesChannels",
                column: "ShippingAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_SalesChannels_TaxAccountId",
                table: "SalesChannels",
                column: "TaxAccountId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ChannelPayoutLines");

            migrationBuilder.DropTable(
                name: "ChannelPayouts");

            migrationBuilder.DropTable(
                name: "SalesChannels");
        }
    }
}
