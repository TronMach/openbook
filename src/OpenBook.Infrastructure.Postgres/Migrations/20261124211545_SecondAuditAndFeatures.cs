using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace OpenBook.Infrastructure.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class SecondAuditAndFeatures : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateOnly>(
                name: "BackupWithholdingFrom",
                table: "Vendors",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "BackupWithholdingNote",
                table: "Vendors",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ErasedAt",
                table: "Vendors",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "PositivePaySentAt",
                table: "VendorDeposits",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "PositivePayVoidSentAt",
                table: "VendorDeposits",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "PrintedAt",
                table: "VendorDeposits",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "TakenBackAmountMinor",
                table: "TimeEntries",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "TakenBackInvoiceId",
                table: "TimeEntries",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "TakenBackMemoId",
                table: "TimeEntries",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "SalesRepId",
                table: "SalesReceipts",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "BackupWithholdingAccountId",
                table: "PurchasingSettings",
                type: "uuid",
                nullable: true);

            migrationBuilder.AlterColumn<Guid>(
                name: "PaymentLinkId",
                table: "OnlinePaymentNotices",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.AddColumn<Guid>(
                name: "CardChargeId",
                table: "OnlinePaymentNotices",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "HoldAutoCharge",
                table: "Invoices",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<Guid>(
                name: "SalesRepId",
                table: "Invoices",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Version",
                table: "ExpenseClaims",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "SignIn",
                table: "EmailSettings",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "Password");

            migrationBuilder.AddColumn<string>(
                name: "Provider",
                table: "DonationCheckouts",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ProviderLinkId",
                table: "DonationCheckouts",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Version",
                table: "DonationCheckouts",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ErasedAt",
                table: "Customers",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "SalesRepId",
                table: "CreditMemos",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ReceivedFrom",
                table: "CapturedDocuments",
                type: "character varying(400)",
                maxLength: 400,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ImportBatchId",
                table: "BillPayments",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "WithheldMinor",
                table: "BillPayments",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<bool>(
                name: "PossibleDuplicate",
                table: "BankFeedItems",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "BillsMailboxes",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false),
                    SignIn = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Host = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    Port = table.Column<int>(type: "integer", nullable: false),
                    Username = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true),
                    ProtectedPassword = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    Folder = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    AllowedSenders = table.Column<string>(type: "text", nullable: true),
                    UseClaude = table.Column<bool>(type: "boolean", nullable: false),
                    Version = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BillsMailboxes", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "BillsMailboxProgress",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false),
                    Source = table.Column<string>(type: "character varying(900)", maxLength: 900, nullable: true),
                    UidValidity = table.Column<long>(type: "bigint", nullable: true),
                    LastUid = table.Column<long>(type: "bigint", nullable: false),
                    LastCheckedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LastReceivedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LastError = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BillsMailboxProgress", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "CommissionRates",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkerId = table.Column<Guid>(type: "uuid", nullable: false),
                    Category = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    PercentTenThousandths = table.Column<long>(type: "bigint", nullable: false),
                    StartsOn = table.Column<DateOnly>(type: "date", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CommissionRates", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CommissionRates_Workers_WorkerId",
                        column: x => x.WorkerId,
                        principalTable: "Workers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "CommissionRuns",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Number = table.Column<int>(type: "integer", nullable: false),
                    WorkerId = table.Column<Guid>(type: "uuid", nullable: false),
                    Through = table.Column<DateOnly>(type: "date", nullable: false),
                    BillId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CommissionRuns", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CommissionRuns_Bills_BillId",
                        column: x => x.BillId,
                        principalTable: "Bills",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CommissionRuns_Workers_WorkerId",
                        column: x => x.WorkerId,
                        principalTable: "Workers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "CommissionSettings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Basis = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    ExpenseAccountId = table.Column<Guid>(type: "uuid", nullable: true),
                    Version = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CommissionSettings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CommissionSettings_Accounts_ExpenseAccountId",
                        column: x => x.ExpenseAccountId,
                        principalTable: "Accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "MicrosoftMail",
                columns: table => new
                {
                    Purpose = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    ClientId = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Tenant = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    ProtectedSecret = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    ProtectedRefreshToken = table.Column<string>(type: "text", nullable: true),
                    Mailbox = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true),
                    ConnectedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MicrosoftMail", x => x.Purpose);
                });

            migrationBuilder.CreateTable(
                name: "OnlineRefunds",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CustomerPaymentId = table.Column<Guid>(type: "uuid", nullable: false),
                    Number = table.Column<int>(type: "integer", nullable: false),
                    Provider = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    PaymentReference = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    CheckoutId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    AmountMinor = table.Column<long>(type: "bigint", nullable: false),
                    PaymentAccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    Date = table.Column<DateOnly>(type: "date", nullable: false),
                    Reason = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    ProviderRefundId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    CustomerRefundId = table.Column<Guid>(type: "uuid", nullable: true),
                    Failure = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CreatedBy = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    SettledAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Version = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OnlineRefunds", x => x.Id);
                    table.ForeignKey(
                        name: "FK_OnlineRefunds_Accounts_PaymentAccountId",
                        column: x => x.PaymentAccountId,
                        principalTable: "Accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_OnlineRefunds_CustomerPayments_CustomerPaymentId",
                        column: x => x.CustomerPaymentId,
                        principalTable: "CustomerPayments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_OnlineRefunds_CustomerRefunds_CustomerRefundId",
                        column: x => x.CustomerRefundId,
                        principalTable: "CustomerRefunds",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "SavedCards",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CustomerId = table.Column<Guid>(type: "uuid", nullable: false),
                    Provider = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    SessionId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    How = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    AgreedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    ProviderCustomerId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    PaymentMethodId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    Brand = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    LastFour = table.Column<string>(type: "character varying(4)", maxLength: 4, nullable: true),
                    ExpiryMonth = table.Column<int>(type: "integer", nullable: true),
                    ExpiryYear = table.Column<int>(type: "integer", nullable: true),
                    SavedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    RemovedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    RemovedReason = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    DetachedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Version = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SavedCards", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SavedCards_Customers_CustomerId",
                        column: x => x.CustomerId,
                        principalTable: "Customers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "UserPasskeys",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    CredentialId = table.Column<string>(type: "character varying(1400)", maxLength: 1400, nullable: false),
                    PublicKey = table.Column<byte[]>(type: "bytea", nullable: false),
                    Algorithm = table.Column<int>(type: "integer", nullable: false),
                    SignCount = table.Column<long>(type: "bigint", nullable: false),
                    Name = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    Site = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LastUsedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserPasskeys", x => x.Id);
                    table.ForeignKey(
                        name: "FK_UserPasskeys_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "UseTaxes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    DocumentId = table.Column<Guid>(type: "uuid", nullable: false),
                    Date = table.Column<DateOnly>(type: "date", nullable: false),
                    TaxCodeId = table.Column<Guid>(type: "uuid", nullable: false),
                    TaxableMinor = table.Column<long>(type: "bigint", nullable: false),
                    JournalEntryId = table.Column<Guid>(type: "uuid", nullable: false),
                    FirstLine = table.Column<int>(type: "integer", nullable: false),
                    IsVoided = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UseTaxes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_UseTaxes_JournalEntries_JournalEntryId",
                        column: x => x.JournalEntryId,
                        principalTable: "JournalEntries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_UseTaxes_TaxCodes_TaxCodeId",
                        column: x => x.TaxCodeId,
                        principalTable: "TaxCodes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "VendorDepositRefunds",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Date = table.Column<DateOnly>(type: "date", nullable: false),
                    AmountMinor = table.Column<long>(type: "bigint", nullable: false),
                    AccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    JournalEntryId = table.Column<Guid>(type: "uuid", nullable: false),
                    Reference = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    IsVoided = table.Column<bool>(type: "boolean", nullable: false),
                    VendorDepositId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VendorDepositRefunds", x => x.Id);
                    table.ForeignKey(
                        name: "FK_VendorDepositRefunds_Accounts_AccountId",
                        column: x => x.AccountId,
                        principalTable: "Accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_VendorDepositRefunds_JournalEntries_JournalEntryId",
                        column: x => x.JournalEntryId,
                        principalTable: "JournalEntries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_VendorDepositRefunds_VendorDeposits_VendorDepositId",
                        column: x => x.VendorDepositId,
                        principalTable: "VendorDeposits",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "CommissionRunLines",
                columns: table => new
                {
                    LineNumber = table.Column<int>(type: "integer", nullable: false),
                    CommissionRunId = table.Column<Guid>(type: "uuid", nullable: false),
                    Source = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    DocumentId = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    SettlementId = table.Column<Guid>(type: "uuid", nullable: false),
                    Date = table.Column<DateOnly>(type: "date", nullable: false),
                    Document = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    CountedMinor = table.Column<long>(type: "bigint", nullable: false),
                    PercentTenThousandths = table.Column<long>(type: "bigint", nullable: true),
                    CommissionMinor = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CommissionRunLines", x => new { x.CommissionRunId, x.LineNumber });
                    table.ForeignKey(
                        name: "FK_CommissionRunLines_CommissionRuns_CommissionRunId",
                        column: x => x.CommissionRunId,
                        principalTable: "CommissionRuns",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "CardCharges",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    InvoiceId = table.Column<Guid>(type: "uuid", nullable: false),
                    CustomerId = table.Column<Guid>(type: "uuid", nullable: false),
                    SavedCardId = table.Column<Guid>(type: "uuid", nullable: false),
                    Attempt = table.Column<int>(type: "integer", nullable: false),
                    Automatic = table.Column<bool>(type: "boolean", nullable: false),
                    AmountMinor = table.Column<long>(type: "bigint", nullable: false),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    IntentId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    Failure = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CreatedBy = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    SettledAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CustomerPaymentId = table.Column<Guid>(type: "uuid", nullable: true),
                    Version = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CardCharges", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CardCharges_CustomerPayments_CustomerPaymentId",
                        column: x => x.CustomerPaymentId,
                        principalTable: "CustomerPayments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CardCharges_Customers_CustomerId",
                        column: x => x.CustomerId,
                        principalTable: "Customers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CardCharges_Invoices_InvoiceId",
                        column: x => x.InvoiceId,
                        principalTable: "Invoices",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CardCharges_SavedCards_SavedCardId",
                        column: x => x.SavedCardId,
                        principalTable: "SavedCards",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "UseTaxLines",
                columns: table => new
                {
                    Position = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    UseTaxId = table.Column<Guid>(type: "uuid", nullable: false),
                    TaxRateId = table.Column<Guid>(type: "uuid", nullable: false),
                    RateName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    PercentTenThousandths = table.Column<long>(type: "bigint", nullable: false),
                    LiabilityAccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    AmountMinor = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UseTaxLines", x => new { x.UseTaxId, x.Position });
                    table.ForeignKey(
                        name: "FK_UseTaxLines_Accounts_LiabilityAccountId",
                        column: x => x.LiabilityAccountId,
                        principalTable: "Accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_UseTaxLines_TaxRates_TaxRateId",
                        column: x => x.TaxRateId,
                        principalTable: "TaxRates",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_UseTaxLines_UseTaxes_UseTaxId",
                        column: x => x.UseTaxId,
                        principalTable: "UseTaxes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SalesReceipts_SalesRepId",
                table: "SalesReceipts",
                column: "SalesRepId");

            migrationBuilder.CreateIndex(
                name: "IX_PurchasingSettings_BackupWithholdingAccountId",
                table: "PurchasingSettings",
                column: "BackupWithholdingAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_OnlinePaymentNotices_CardChargeId",
                table: "OnlinePaymentNotices",
                column: "CardChargeId");

            migrationBuilder.CreateIndex(
                name: "IX_Invoices_SalesRepId",
                table: "Invoices",
                column: "SalesRepId");

            migrationBuilder.CreateIndex(
                name: "IX_CreditMemos_SalesRepId",
                table: "CreditMemos",
                column: "SalesRepId");

            migrationBuilder.CreateIndex(
                name: "IX_CardCharges_CustomerId",
                table: "CardCharges",
                column: "CustomerId");

            migrationBuilder.CreateIndex(
                name: "IX_CardCharges_CustomerPaymentId",
                table: "CardCharges",
                column: "CustomerPaymentId");

            migrationBuilder.CreateIndex(
                name: "IX_CardCharges_IntentId",
                table: "CardCharges",
                column: "IntentId");

            migrationBuilder.CreateIndex(
                name: "IX_CardCharges_InvoiceId_Attempt",
                table: "CardCharges",
                columns: new[] { "InvoiceId", "Attempt" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CardCharges_SavedCardId",
                table: "CardCharges",
                column: "SavedCardId");

            migrationBuilder.CreateIndex(
                name: "IX_CardCharges_Status",
                table: "CardCharges",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_CommissionRates_WorkerId",
                table: "CommissionRates",
                column: "WorkerId");

            migrationBuilder.CreateIndex(
                name: "IX_CommissionRuns_BillId",
                table: "CommissionRuns",
                column: "BillId");

            migrationBuilder.CreateIndex(
                name: "IX_CommissionRuns_Number",
                table: "CommissionRuns",
                column: "Number",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CommissionRuns_WorkerId",
                table: "CommissionRuns",
                column: "WorkerId");

            migrationBuilder.CreateIndex(
                name: "IX_CommissionSettings_ExpenseAccountId",
                table: "CommissionSettings",
                column: "ExpenseAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_OnlineRefunds_CustomerPaymentId_Number",
                table: "OnlineRefunds",
                columns: new[] { "CustomerPaymentId", "Number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_OnlineRefunds_CustomerRefundId",
                table: "OnlineRefunds",
                column: "CustomerRefundId");

            migrationBuilder.CreateIndex(
                name: "IX_OnlineRefunds_PaymentAccountId",
                table: "OnlineRefunds",
                column: "PaymentAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_OnlineRefunds_PaymentReference",
                table: "OnlineRefunds",
                column: "PaymentReference");

            migrationBuilder.CreateIndex(
                name: "IX_OnlineRefunds_ProviderRefundId",
                table: "OnlineRefunds",
                column: "ProviderRefundId");

            migrationBuilder.CreateIndex(
                name: "IX_OnlineRefunds_Status",
                table: "OnlineRefunds",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_SavedCards_CustomerId",
                table: "SavedCards",
                column: "CustomerId");

            migrationBuilder.CreateIndex(
                name: "IX_SavedCards_SessionId",
                table: "SavedCards",
                column: "SessionId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SavedCards_Status",
                table: "SavedCards",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_UserPasskeys_CredentialId",
                table: "UserPasskeys",
                column: "CredentialId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_UserPasskeys_UserId",
                table: "UserPasskeys",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_UseTaxes_Date",
                table: "UseTaxes",
                column: "Date");

            migrationBuilder.CreateIndex(
                name: "IX_UseTaxes_DocumentId",
                table: "UseTaxes",
                column: "DocumentId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_UseTaxes_JournalEntryId",
                table: "UseTaxes",
                column: "JournalEntryId");

            migrationBuilder.CreateIndex(
                name: "IX_UseTaxes_TaxCodeId",
                table: "UseTaxes",
                column: "TaxCodeId");

            migrationBuilder.CreateIndex(
                name: "IX_UseTaxLines_LiabilityAccountId",
                table: "UseTaxLines",
                column: "LiabilityAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_UseTaxLines_TaxRateId",
                table: "UseTaxLines",
                column: "TaxRateId");

            migrationBuilder.CreateIndex(
                name: "IX_VendorDepositRefunds_AccountId",
                table: "VendorDepositRefunds",
                column: "AccountId");

            migrationBuilder.CreateIndex(
                name: "IX_VendorDepositRefunds_JournalEntryId",
                table: "VendorDepositRefunds",
                column: "JournalEntryId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_VendorDepositRefunds_VendorDepositId",
                table: "VendorDepositRefunds",
                column: "VendorDepositId");

            migrationBuilder.AddForeignKey(
                name: "FK_CreditMemos_Workers_SalesRepId",
                table: "CreditMemos",
                column: "SalesRepId",
                principalTable: "Workers",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Invoices_Workers_SalesRepId",
                table: "Invoices",
                column: "SalesRepId",
                principalTable: "Workers",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_OnlinePaymentNotices_CardCharges_CardChargeId",
                table: "OnlinePaymentNotices",
                column: "CardChargeId",
                principalTable: "CardCharges",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_PurchasingSettings_Accounts_BackupWithholdingAccountId",
                table: "PurchasingSettings",
                column: "BackupWithholdingAccountId",
                principalTable: "Accounts",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_SalesReceipts_Workers_SalesRepId",
                table: "SalesReceipts",
                column: "SalesRepId",
                principalTable: "Workers",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_CreditMemos_Workers_SalesRepId",
                table: "CreditMemos");

            migrationBuilder.DropForeignKey(
                name: "FK_Invoices_Workers_SalesRepId",
                table: "Invoices");

            migrationBuilder.DropForeignKey(
                name: "FK_OnlinePaymentNotices_CardCharges_CardChargeId",
                table: "OnlinePaymentNotices");

            migrationBuilder.DropForeignKey(
                name: "FK_PurchasingSettings_Accounts_BackupWithholdingAccountId",
                table: "PurchasingSettings");

            migrationBuilder.DropForeignKey(
                name: "FK_SalesReceipts_Workers_SalesRepId",
                table: "SalesReceipts");

            migrationBuilder.DropTable(
                name: "BillsMailboxes");

            migrationBuilder.DropTable(
                name: "BillsMailboxProgress");

            migrationBuilder.DropTable(
                name: "CardCharges");

            migrationBuilder.DropTable(
                name: "CommissionRates");

            migrationBuilder.DropTable(
                name: "CommissionRunLines");

            migrationBuilder.DropTable(
                name: "CommissionSettings");

            migrationBuilder.DropTable(
                name: "MicrosoftMail");

            migrationBuilder.DropTable(
                name: "OnlineRefunds");

            migrationBuilder.DropTable(
                name: "UserPasskeys");

            migrationBuilder.DropTable(
                name: "UseTaxLines");

            migrationBuilder.DropTable(
                name: "VendorDepositRefunds");

            migrationBuilder.DropTable(
                name: "SavedCards");

            migrationBuilder.DropTable(
                name: "CommissionRuns");

            migrationBuilder.DropTable(
                name: "UseTaxes");

            migrationBuilder.DropIndex(
                name: "IX_SalesReceipts_SalesRepId",
                table: "SalesReceipts");

            migrationBuilder.DropIndex(
                name: "IX_PurchasingSettings_BackupWithholdingAccountId",
                table: "PurchasingSettings");

            migrationBuilder.DropIndex(
                name: "IX_OnlinePaymentNotices_CardChargeId",
                table: "OnlinePaymentNotices");

            migrationBuilder.DropIndex(
                name: "IX_Invoices_SalesRepId",
                table: "Invoices");

            migrationBuilder.DropIndex(
                name: "IX_CreditMemos_SalesRepId",
                table: "CreditMemos");

            migrationBuilder.DropColumn(
                name: "BackupWithholdingFrom",
                table: "Vendors");

            migrationBuilder.DropColumn(
                name: "BackupWithholdingNote",
                table: "Vendors");

            migrationBuilder.DropColumn(
                name: "ErasedAt",
                table: "Vendors");

            migrationBuilder.DropColumn(
                name: "PositivePaySentAt",
                table: "VendorDeposits");

            migrationBuilder.DropColumn(
                name: "PositivePayVoidSentAt",
                table: "VendorDeposits");

            migrationBuilder.DropColumn(
                name: "PrintedAt",
                table: "VendorDeposits");

            migrationBuilder.DropColumn(
                name: "TakenBackAmountMinor",
                table: "TimeEntries");

            migrationBuilder.DropColumn(
                name: "TakenBackInvoiceId",
                table: "TimeEntries");

            migrationBuilder.DropColumn(
                name: "TakenBackMemoId",
                table: "TimeEntries");

            migrationBuilder.DropColumn(
                name: "SalesRepId",
                table: "SalesReceipts");

            migrationBuilder.DropColumn(
                name: "BackupWithholdingAccountId",
                table: "PurchasingSettings");

            migrationBuilder.DropColumn(
                name: "CardChargeId",
                table: "OnlinePaymentNotices");

            migrationBuilder.DropColumn(
                name: "HoldAutoCharge",
                table: "Invoices");

            migrationBuilder.DropColumn(
                name: "SalesRepId",
                table: "Invoices");

            migrationBuilder.DropColumn(
                name: "Version",
                table: "ExpenseClaims");

            migrationBuilder.DropColumn(
                name: "SignIn",
                table: "EmailSettings");

            migrationBuilder.DropColumn(
                name: "Provider",
                table: "DonationCheckouts");

            migrationBuilder.DropColumn(
                name: "ProviderLinkId",
                table: "DonationCheckouts");

            migrationBuilder.DropColumn(
                name: "Version",
                table: "DonationCheckouts");

            migrationBuilder.DropColumn(
                name: "ErasedAt",
                table: "Customers");

            migrationBuilder.DropColumn(
                name: "SalesRepId",
                table: "CreditMemos");

            migrationBuilder.DropColumn(
                name: "ReceivedFrom",
                table: "CapturedDocuments");

            migrationBuilder.DropColumn(
                name: "ImportBatchId",
                table: "BillPayments");

            migrationBuilder.DropColumn(
                name: "WithheldMinor",
                table: "BillPayments");

            migrationBuilder.DropColumn(
                name: "PossibleDuplicate",
                table: "BankFeedItems");

            migrationBuilder.AlterColumn<Guid>(
                name: "PaymentLinkId",
                table: "OnlinePaymentNotices",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);
        }
    }
}
