using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using OpenBook.Domain.Accounts;
using OpenBook.Domain.Attachments;
using OpenBook.Domain.Auditing;
using OpenBook.Domain.Banking;
using OpenBook.Domain.Common;
using OpenBook.Domain.Companies;
using OpenBook.Domain.Inventory;
using OpenBook.Domain.Ledger;
using OpenBook.Domain.Notes;
using OpenBook.Domain.Pricing;
using OpenBook.Domain.Purchases;
using OpenBook.Domain.Reporting;
using OpenBook.Domain.Sales;
using OpenBook.Domain.SalesTax;
using OpenBook.Domain.Segments;
using OpenBook.Domain.Webhooks;

namespace OpenBook.Infrastructure.Persistence;

/// <summary>
/// New journal entries are inserted unsealed, then sealed in the same transaction. The seal is what
/// fires the database's balance check (see <see cref="SqliteLedgerTriggers"/>): SQLite has no deferred
/// constraints, so this is how the DB verifies a whole entry rather than one line at a time.
/// </summary>
public sealed class OpenBookDbContext(DbContextOptions<OpenBookDbContext> options) : DbContext(options)
{
    internal const string IsSealed = nameof(IsSealed);

    public DbSet<OpenBook.Domain.Channels.SalesChannel> SalesChannels => Set<OpenBook.Domain.Channels.SalesChannel>();
    public DbSet<OpenBook.Domain.Channels.ChannelPayout> ChannelPayouts => Set<OpenBook.Domain.Channels.ChannelPayout>();
    public DbSet<OpenBook.Domain.Channels.ChannelOrderBatch> ChannelOrderBatches => Set<OpenBook.Domain.Channels.ChannelOrderBatch>();
    public DbSet<OpenBook.Domain.Channels.ChannelSku> ChannelSkus => Set<OpenBook.Domain.Channels.ChannelSku>();
    public DbSet<OpenBook.Domain.Channels.ChannelReturn> ChannelReturns => Set<OpenBook.Domain.Channels.ChannelReturn>();
    public DbSet<OpenBook.Domain.Nonprofit.RestrictionRelease> RestrictionReleases => Set<OpenBook.Domain.Nonprofit.RestrictionRelease>();
    public DbSet<OpenBook.Domain.Nonprofit.Donation> Donations => Set<OpenBook.Domain.Nonprofit.Donation>();
    public DbSet<OpenBook.Domain.Nonprofit.Pledge> Pledges => Set<OpenBook.Domain.Nonprofit.Pledge>();
    public DbSet<OpenBook.Domain.Nonprofit.GiftSettings> GiftSettings => Set<OpenBook.Domain.Nonprofit.GiftSettings>();
    public DbSet<OpenBook.Domain.Nonprofit.Grant> Grants => Set<OpenBook.Domain.Nonprofit.Grant>();
    public DbSet<OpenBook.Domain.Nonprofit.RecurringGift> RecurringGifts => Set<OpenBook.Domain.Nonprofit.RecurringGift>();
    public DbSet<UserSession> UserSessions => Set<UserSession>();
    public DbSet<Account> Accounts => Set<Account>();
    public DbSet<JournalEntry> JournalEntries => Set<JournalEntry>();
    public DbSet<AuditRecord> AuditRecords => Set<AuditRecord>();
    public DbSet<Company> Companies => Set<Company>();
    public DbSet<User> Users => Set<User>();
    public DbSet<ApiKey> ApiKeys => Set<ApiKey>();
    public DbSet<Note> Notes => Set<Note>();
    public DbSet<WebhookSubscription> WebhookSubscriptions => Set<WebhookSubscription>();
    public DbSet<WebhookDelivery> WebhookDeliveries => Set<WebhookDelivery>();
    public DbSet<BankTransaction> BankTransactions => Set<BankTransaction>();
    public DbSet<Reconciliation> Reconciliations => Set<Reconciliation>();
    public DbSet<BankFeedItem> BankFeedItems => Set<BankFeedItem>();
    public DbSet<BankRule> BankRules => Set<BankRule>();
    public DbSet<BankConnection> BankConnections => Set<BankConnection>();
    public DbSet<PettyCashFund> PettyCashFunds => Set<PettyCashFund>();
    public DbSet<CashCount> CashCounts => Set<CashCount>();
    public DbSet<PositivePaySettings> PositivePaySettings => Set<PositivePaySettings>();

    public DbSet<Customer> Customers => Set<Customer>();
    public DbSet<Item> Items => Set<Item>();
    public DbSet<Invoice> Invoices => Set<Invoice>();
    public DbSet<CustomerPayment> CustomerPayments => Set<CustomerPayment>();
    public DbSet<SalesReceipt> SalesReceipts => Set<SalesReceipt>();
    public DbSet<Estimate> Estimates => Set<Estimate>();
    public DbSet<CreditMemo> CreditMemos => Set<CreditMemo>();
    public DbSet<CustomerRefund> CustomerRefunds => Set<CustomerRefund>();
    public DbSet<BadDebtWriteOff> BadDebtWriteOffs => Set<BadDebtWriteOff>();
    public DbSet<SalesOrder> SalesOrders => Set<SalesOrder>();
    public DbSet<PurchaseOrder> PurchaseOrders => Set<PurchaseOrder>();
    public DbSet<OnlinePaymentSettings> OnlinePaymentSettings => Set<OnlinePaymentSettings>();
    public DbSet<PaymentLink> PaymentLinks => Set<PaymentLink>();
    public DbSet<PortalAccess> PortalAccess => Set<PortalAccess>();
    public DbSet<ReceiptCapture> ReceiptCaptures => Set<ReceiptCapture>();
    public DbSet<OpenBook.Domain.Documents.CapturedDocument> CapturedDocuments => Set<OpenBook.Domain.Documents.CapturedDocument>();
    public DbSet<OpenBook.Domain.Documents.DocumentItemCode> DocumentItemCodes => Set<OpenBook.Domain.Documents.DocumentItemCode>();
    public DbSet<OpenBook.Domain.Documents.DocumentReadingSettings> DocumentReadingSettings => Set<OpenBook.Domain.Documents.DocumentReadingSettings>();
    public DbSet<OpenBook.Domain.CustomFields.CustomField> CustomFields => Set<OpenBook.Domain.CustomFields.CustomField>();
    public DbSet<FormDesign> FormDesigns => Set<FormDesign>();
    public DbSet<FormFont> FormFonts => Set<FormFont>();
    public DbSet<OpenBook.Domain.CustomFields.CustomFieldValue> CustomFieldValues => Set<OpenBook.Domain.CustomFields.CustomFieldValue>();
    public DbSet<PurchasingSettings> PurchasingSettings => Set<PurchasingSettings>();
    public DbSet<VendorBankAccount> VendorBankAccounts => Set<VendorBankAccount>();
    public DbSet<AchSettings> AchSettings => Set<AchSettings>();
    public DbSet<AchBatch> AchBatches => Set<AchBatch>();
    public DbSet<Retainer> Retainers => Set<Retainer>();
    public DbSet<BillableExpense> BillableExpenses => Set<BillableExpense>();
    public DbSet<OpenBook.Domain.Projects.Project> Projects => Set<OpenBook.Domain.Projects.Project>();
    public DbSet<OpenBook.Domain.Projects.Worker> Workers => Set<OpenBook.Domain.Projects.Worker>();
    public DbSet<OpenBook.Domain.Projects.TimeEntry> TimeEntries => Set<OpenBook.Domain.Projects.TimeEntry>();
    public DbSet<OpenBook.Domain.Projects.MileageTrip> MileageTrips => Set<OpenBook.Domain.Projects.MileageTrip>();
    public DbSet<OpenBook.Domain.Projects.MileageRate> MileageRates => Set<OpenBook.Domain.Projects.MileageRate>();
    public DbSet<OpenBook.Domain.Projects.BillingRate> BillingRates => Set<OpenBook.Domain.Projects.BillingRate>();
    public DbSet<OpenBook.Domain.Assets.FixedAsset> FixedAssets => Set<OpenBook.Domain.Assets.FixedAsset>();
    public DbSet<OpenBook.Domain.Assets.DepreciationRun> DepreciationRuns => Set<OpenBook.Domain.Assets.DepreciationRun>();
    public DbSet<OpenBook.Domain.Assets.Loan> Loans => Set<OpenBook.Domain.Assets.Loan>();
    public DbSet<ReportSubscription> ReportSubscriptions => Set<ReportSubscription>();
    public DbSet<DashboardPreference> DashboardPreferences => Set<DashboardPreference>();
    public DbSet<ForecastScenario> ForecastScenarios => Set<ForecastScenario>();
    public DbSet<AdjustingEntry> AdjustingEntries => Set<AdjustingEntry>();
    public DbSet<OpenBook.Domain.Capture.CaptureDevice> CaptureDevices => Set<OpenBook.Domain.Capture.CaptureDevice>();
    public DbSet<OpenBook.Domain.Capture.CaptureDraft> CaptureDrafts => Set<OpenBook.Domain.Capture.CaptureDraft>();
    public DbSet<CollectionSettings> CollectionSettings => Set<CollectionSettings>();
    public DbSet<InvoiceReminder> InvoiceReminders => Set<InvoiceReminder>();
    public DbSet<ScheduledEmail> ScheduledEmails => Set<ScheduledEmail>();
    public DbSet<RecurringInvoice> RecurringInvoices => Set<RecurringInvoice>();
    public DbSet<BankDeposit> BankDeposits => Set<BankDeposit>();
    public DbSet<EmailSettings> EmailConfigurations => Set<EmailSettings>();
    public DbSet<BackupSchedule> BackupSchedules => Set<BackupSchedule>();
    public DbSet<Vendor> Vendors => Set<Vendor>();
    public DbSet<RecurringEntry> RecurringEntries => Set<RecurringEntry>();
    public DbSet<Bill> Bills => Set<Bill>();
    public DbSet<BillPayment> BillPayments => Set<BillPayment>();
    public DbSet<VendorCredit> VendorCredits => Set<VendorCredit>();
    public DbSet<VendorRefund> VendorRefunds => Set<VendorRefund>();
    public DbSet<RecurringBill> RecurringBills => Set<RecurringBill>();
    public DbSet<Attachment> Attachments => Set<Attachment>();
    public DbSet<AttachmentContent> AttachmentContents => Set<AttachmentContent>();
    public DbSet<TaxAgency> TaxAgencies => Set<TaxAgency>();
    public DbSet<TaxRate> TaxRates => Set<TaxRate>();
    public DbSet<TaxCode> TaxCodes => Set<TaxCode>();
    public DbSet<SalesTaxPayment> SalesTaxPayments => Set<SalesTaxPayment>();
    public DbSet<Budget> Budgets => Set<Budget>();
    public DbSet<InventoryMovement> InventoryMovements => Set<InventoryMovement>();
    public DbSet<InventoryAdjustment> InventoryAdjustments => Set<InventoryAdjustment>();
    public DbSet<AssemblyBuild> AssemblyBuilds => Set<AssemblyBuild>();
    public DbSet<Warehouse> Warehouses => Set<Warehouse>();
    public DbSet<LotDetail> LotDetails => Set<LotDetail>();
    public DbSet<PendingBuild> PendingBuilds => Set<PendingBuild>();
    public DbSet<Bin> Bins => Set<Bin>();
    public DbSet<StockTransfer> StockTransfers => Set<StockTransfer>();
    public DbSet<LandedCost> LandedCosts => Set<LandedCost>();
    public DbSet<SavedReport> SavedReports => Set<SavedReport>();
    public DbSet<PriceLevel> PriceLevels => Set<PriceLevel>();
    public DbSet<PriceRule> PriceRules => Set<PriceRule>();
    public DbSet<Segment> Segments => Set<Segment>();
    public DbSet<LineSegment> LineSegments => Set<LineSegment>();
    public DbSet<EntryTag> EntryTags => Set<EntryTag>();

    /// <summary>Money columns are integer minor units, so SQL sums are exact on every provider.</summary>
    private static readonly ValueConverter<decimal, long> MinorUnits = new(amount => (long)(amount * 100m), minor => minor / 100m);

    /// <summary>Unit prices and quantities: integer ten-thousandths (4 decimal places).</summary>
    private static readonly ValueConverter<decimal?, long?> NullableMinorUnits =
        new(amount => amount == null ? null : (long)(amount.Value * 100m), minor => minor == null ? null : minor.Value / 100m);

    private static readonly ValueConverter<decimal, long> TenThousandths = new(value => (long)(value * 10000m), stored => stored / 10000m);

    private static readonly ValueConverter<decimal?, long?> NullableTenThousandths =
        new(value => value == null ? null : (long)(value.Value * 10000m), stored => stored == null ? null : stored.Value / 10000m);

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        if (ChangeTracker.Entries<JournalEntry>().Any(e => e.State == EntityState.Added))
            throw new NotSupportedException("Journal entries must be saved with SaveChangesAsync.");
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override async Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        var newEntryIds = ChangeTracker.Entries<JournalEntry>()
            .Where(e => e.State == EntityState.Added)
            .Select(e => e.Entity.Id)
            .ToList();
        if (newEntryIds.Count == 0)
            return await base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);

        var ownTransaction = Database.CurrentTransaction is null
            ? await Database.BeginTransactionAsync(cancellationToken)
            : null;
        try
        {
            var written = await base.SaveChangesAsync(acceptAllChangesOnSuccess: false, cancellationToken);
            await JournalEntries
                .IgnoreQueryFilters()
                .Where(e => newEntryIds.Contains(e.Id))
                .ExecuteUpdateAsync(s => s.SetProperty(e => EF.Property<bool>(e, IsSealed), true), cancellationToken);

            if (ownTransaction is not null)
                await ownTransaction.CommitAsync(cancellationToken);
            if (acceptAllChangesOnSuccess)
                ChangeTracker.AcceptAllChanges();
            return written;
        }
        finally
        {
            if (ownTransaction is not null)
                await ownTransaction.DisposeAsync();
        }
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Account>(b =>
        {
            b.ToTable("Accounts");
            b.HasKey(a => a.Id);
            b.Property(a => a.Name).HasMaxLength(200);
            b.Property(a => a.Number).HasMaxLength(20);
            b.Property(a => a.Description).HasMaxLength(500);
            b.Property(a => a.Type).HasConversion<string>().HasMaxLength(30);
            b.Property(a => a.TaxLineKey).HasMaxLength(40);
            b.HasIndex(a => a.Number).IsUnique();
            b.HasOne<Account>().WithMany().HasForeignKey(a => a.ParentId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<JournalEntry>(b =>
        {
            b.ToTable("JournalEntries");
            b.HasKey(e => e.Id);
            b.Property<bool>(IsSealed);
            b.HasQueryFilter(e => EF.Property<bool>(e, IsSealed));
            b.HasIndex(e => e.Number).IsUnique();
            b.HasIndex(e => e.Date);
            b.Property(e => e.Currency).HasConversion(c => c.Code, code => new Currency(code)).HasMaxLength(3);
            b.Property(e => e.Memo).HasMaxLength(1000);
            b.Property(e => e.Kind).HasConversion<string>().HasMaxLength(20);
            b.Property(e => e.Basis).HasConversion<string>().HasMaxLength(20);
            b.Property(e => e.Status).HasConversion<string>().HasMaxLength(20);
            b.Property(e => e.Source).HasMaxLength(40);
            b.Property(e => e.VoidReason).HasMaxLength(500);
            b.Ignore(e => e.TotalDebits);
            b.Ignore(e => e.TotalCredits);

            // At most one reversal per entry: a second void is rejected by the database too.
            b.HasIndex(e => e.ReversesEntryId).IsUnique();
            b.HasOne<JournalEntry>().WithOne().HasForeignKey<JournalEntry>(e => e.ReversesEntryId).OnDelete(DeleteBehavior.Restrict);

            b.HasMany(e => e.Lines).WithOne().HasForeignKey("JournalEntryId").OnDelete(DeleteBehavior.Restrict);
            b.Navigation(e => e.Lines).UsePropertyAccessMode(PropertyAccessMode.Field);
        });

        modelBuilder.Entity<JournalLine>(b =>
        {
            b.ToTable("JournalLines", t => t.HasCheckConstraint("CK_JournalLines_AmountMinor_NonZero", "\"AmountMinor\" <> 0"));
            b.HasKey(l => l.Id);

            // Stored as integer minor units so SUMs in SQL are exact on every provider.
            b.Property(l => l.Amount)
                .HasColumnName("AmountMinor")
                .HasConversion(MinorUnits);
            b.Property(l => l.Memo).HasMaxLength(1000);
            b.Ignore(l => l.Debit);
            b.Ignore(l => l.Credit);
            b.HasIndex("JournalEntryId", nameof(JournalLine.LineNumber)).IsUnique();
            b.HasIndex(l => l.AccountId);
            b.HasOne<Account>().WithMany().HasForeignKey(l => l.AccountId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<AuditRecord>(b =>
        {
            b.ToTable("AuditRecords");
            b.HasKey(r => r.Id);
            b.Property(r => r.Actor).HasMaxLength(200);
            b.Property(r => r.Action).HasConversion<string>().HasMaxLength(20);
            b.Property(r => r.EntityType).HasMaxLength(60);
            b.HasIndex(r => new { r.EntityType, r.EntityId });
        });

        modelBuilder.Entity<Company>(b =>
        {
            b.ToTable("Companies");
            b.HasKey(c => c.Id);
            b.Property(c => c.Name).HasMaxLength(200);
            b.Property(c => c.LegalName).HasMaxLength(200);
            b.Property(c => c.BusinessType).HasConversion<string>().HasMaxLength(30);
            b.Property(c => c.HomeCurrency).HasConversion(c => c.Code, code => new Currency(code)).HasMaxLength(3);
            b.Property(c => c.Email).HasMaxLength(320);
            b.Property(c => c.Phone).HasMaxLength(50);
            b.Property(c => c.Address).HasMaxLength(1000);
            b.Property(c => c.DefaultReportBasis).HasConversion<string>().HasMaxLength(20);
            b.Property(c => c.Edition).HasConversion<string>().HasMaxLength(30);
            b.Property(c => c.ClosingPasswordHash).HasMaxLength(500);
            b.Property(c => c.PriceRounding).HasConversion<string>().HasMaxLength(20);
            b.Property(c => c.LocationLabel).HasMaxLength(30);
        });

        modelBuilder.Entity<BackupSchedule>(b =>
        {
            b.ToTable("BackupSchedules");
            b.HasKey(s => s.Id);
            b.Property(s => s.Id).ValueGeneratedNever();
            b.Property(s => s.Frequency).HasConversion<string>().HasMaxLength(20);
            b.Property(s => s.Weekday).HasConversion<string>().HasMaxLength(20);
            b.Property(s => s.Destination).HasConversion<string>().HasMaxLength(20);
            b.Property(s => s.FolderPath).HasMaxLength(500);
            b.Property(s => s.ServiceUrl).HasMaxLength(300);
            b.Property(s => s.Region).HasMaxLength(50);
            b.Property(s => s.Bucket).HasMaxLength(63);
            b.Property(s => s.Prefix).HasMaxLength(200);
            b.Property(s => s.AccessKeyId).HasMaxLength(128);
            b.Property(s => s.ProtectedSecretKey).HasMaxLength(2000);
            b.Property(s => s.LastFileName).HasMaxLength(200);
            b.Property(s => s.LastError).HasMaxLength(1000);
        });

        modelBuilder.Entity<EmailSettings>(b =>
        {
            b.ToTable("EmailSettings");
            b.HasKey(s => s.Id);
            b.Property(s => s.Id).ValueGeneratedNever();
            b.Property(s => s.Delivery).HasConversion<string>().HasMaxLength(20);
            b.Property(s => s.Security).HasConversion<string>().HasMaxLength(20);
            b.Property(s => s.Host).HasMaxLength(255);
            b.Property(s => s.Username).HasMaxLength(320);
            b.Property(s => s.ProtectedPassword).HasMaxLength(2000);
            b.Property(s => s.FromAddress).HasMaxLength(320);
            b.Property(s => s.FromName).HasMaxLength(200);
            b.Property(s => s.BccAddress).HasMaxLength(320);
            b.Ignore(s => s.IsConfigured);
        });

        modelBuilder.Entity<BankTransaction>(b =>
        {
            b.ToTable("BankTransactions");
            b.HasKey(t => t.Id);
            b.Property(t => t.Kind).HasConversion<string>().HasMaxLength(20);
            b.Property(t => t.Status).HasConversion<string>().HasMaxLength(20);
            b.Property(t => t.Amount).HasColumnName("AmountMinor").HasConversion(MinorUnits);
            b.Property(t => t.Payee).HasMaxLength(200);
            b.Property(t => t.Reference).HasMaxLength(30);
            b.Property(t => t.Memo).HasMaxLength(1000);
            b.Ignore(t => t.SignedAmount);
            b.HasIndex(t => new { t.AccountId, t.Date });
            b.HasIndex(t => t.JournalEntryId).IsUnique();
            b.HasIndex(t => t.ImportBatchId);
            b.HasOne<Vendor>().WithMany().HasForeignKey(t => t.VendorId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<Account>().WithMany().HasForeignKey(t => t.AccountId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<Account>().WithMany().HasForeignKey(t => t.TransferAccountId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<JournalEntry>().WithMany().HasForeignKey(t => t.JournalEntryId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<BankFeedItem>(b =>
        {
            b.ToTable("BankFeedItems");
            b.HasKey(i => i.Id);
            b.Property(i => i.Amount).HasColumnName("AmountMinor").HasConversion(MinorUnits);
            b.Property(i => i.Payee).HasMaxLength(200);
            b.Property(i => i.Reference).HasMaxLength(30);
            b.Property(i => i.Memo).HasMaxLength(1000);
            b.Property(i => i.FitId).HasMaxLength(255);
            b.Property(i => i.DedupeKey).HasMaxLength(600);
            b.Property(i => i.Status).HasConversion<string>().HasMaxLength(20);
            b.Property(i => i.ResolvedBy).HasMaxLength(100);
            b.Ignore(i => i.IsMoneyIn);
            // The same statement line can't be queued twice for an account.
            b.HasIndex(i => new { i.AccountId, i.DedupeKey }).IsUnique();
            b.HasIndex(i => new { i.AccountId, i.Status });
            b.HasOne<Account>().WithMany().HasForeignKey(i => i.AccountId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<BankTransaction>().WithMany().HasForeignKey(i => i.BankTransactionId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<JournalEntry>().WithMany().HasForeignKey(i => i.MatchedJournalEntryId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<BankRule>().WithMany().HasForeignKey(i => i.RuleId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<BankConnection>(b =>
        {
            b.ToTable("BankConnections");
            b.HasKey(c => c.Id);
            b.Property(c => c.Provider).HasConversion<string>().HasMaxLength(20);
            b.Property(c => c.Name).HasMaxLength(BankConnection.MaxNameLength);
            b.Property(c => c.ProtectedAccess).HasMaxLength(4000);
            b.Property(c => c.LastError).HasMaxLength(1000);
            b.OwnsMany(c => c.Accounts, a =>
            {
                a.ToTable("BankConnectionAccounts");
                a.WithOwner().HasForeignKey(x => x.ConnectionId);
                a.HasKey(x => new { x.ConnectionId, x.RemoteId });
                a.Property(x => x.RemoteId).HasMaxLength(200);
                a.Property(x => x.Name).HasMaxLength(200);
                a.Property(x => x.Institution).HasMaxLength(200);
                a.Property(x => x.Currency).HasMaxLength(10);
                a.Property(x => x.Balance).HasColumnName("BalanceMinor").HasConversion(NullableMinorUnits);
                a.HasIndex(x => x.AccountId);
                a.HasOne<Account>().WithMany().HasForeignKey(x => x.AccountId).OnDelete(DeleteBehavior.Restrict);
            });
            b.Navigation(c => c.Accounts).UsePropertyAccessMode(PropertyAccessMode.Field);
        });

        modelBuilder.Entity<PettyCashFund>(b =>
        {
            b.ToTable("PettyCashFunds");
            b.HasKey(f => f.Id);
            b.Property(f => f.FloatAmount).HasColumnName("FloatMinor").HasConversion(MinorUnits);
            b.Property(f => f.Custodian).HasMaxLength(100);
            b.HasIndex(f => f.AccountId).IsUnique();
            b.HasOne<Account>().WithMany().HasForeignKey(f => f.AccountId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<CashCount>(b =>
        {
            b.ToTable("CashCounts");
            b.HasKey(c => c.Id);
            b.Property(c => c.Counted).HasColumnName("CountedMinor").HasConversion(MinorUnits);
            b.Property(c => c.BookBalance).HasColumnName("BookBalanceMinor").HasConversion(MinorUnits);
            b.Ignore(c => c.Difference);
            b.Property(c => c.Breakdown).HasMaxLength(1000);
            b.Property(c => c.Note).HasMaxLength(500);
            b.Property(c => c.CountedBy).HasMaxLength(100);
            b.HasIndex(c => new { c.FundId, c.Date });
            b.HasOne<PettyCashFund>().WithMany().HasForeignKey(c => c.FundId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<BankTransaction>().WithMany().HasForeignKey(c => c.AdjustmentTransactionId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<PositivePaySettings>(b =>
        {
            b.ToTable("PositivePaySettings");
            b.HasKey(s => s.AccountId);
            b.Property(s => s.BankAccountNumber).HasMaxLength(34);
            b.Property(s => s.DateFormat).HasConversion<string>().HasMaxLength(30);
            b.HasOne<Account>().WithMany().HasForeignKey(s => s.AccountId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<Segment>(b =>
        {
            b.ToTable("Segments");
            b.HasKey(s => s.Id);
            b.Property(s => s.Kind).HasConversion<string>().HasMaxLength(20);
            b.Property(s => s.Name).HasMaxLength(100);
            b.Property(s => s.FunctionalArea).HasConversion<string>().HasMaxLength(30);
            b.HasIndex(s => new { s.Kind, s.ParentId, s.Name });
            b.HasOne<Segment>().WithMany().HasForeignKey(s => s.ParentId).OnDelete(DeleteBehavior.Restrict);
        });

        // Beside the ledger, not in it: posted lines stay immutable while segments can be reclassified.
        modelBuilder.Entity<LineSegment>(b =>
        {
            b.ToTable("LineSegments");
            b.HasKey(s => s.JournalLineId);
            b.Ignore(s => s.IsEmpty);
            b.HasIndex(s => s.JournalEntryId);
            b.HasIndex(s => s.ClassId);
            b.HasIndex(s => s.LocationId);
            b.HasOne<JournalLine>().WithOne().HasForeignKey<LineSegment>(s => s.JournalLineId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<JournalEntry>().WithMany().HasForeignKey(s => s.JournalEntryId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<Segment>().WithMany().HasForeignKey(s => s.ClassId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<Segment>().WithMany().HasForeignKey(s => s.LocationId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<EntryTag>(b =>
        {
            b.ToTable("EntryTags");
            b.HasKey(t => new { t.JournalEntryId, t.TagId });
            b.HasIndex(t => t.TagId);
            b.HasOne<JournalEntry>().WithMany().HasForeignKey(t => t.JournalEntryId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<Segment>().WithMany().HasForeignKey(t => t.TagId).OnDelete(DeleteBehavior.Restrict);
        });

        foreach (var document in new[] { typeof(Invoice), typeof(SalesReceipt), typeof(CreditMemo), typeof(Estimate), typeof(Bill), typeof(VendorCredit), typeof(BankTransaction) })
            modelBuilder.Entity(document).HasOne(typeof(Segment)).WithMany().HasForeignKey("LocationId").OnDelete(DeleteBehavior.Restrict);

        foreach (var document in new[] { typeof(Invoice), typeof(SalesReceipt), typeof(CreditMemo), typeof(Bill) })
            modelBuilder.Entity(document).HasOne(typeof(Warehouse)).WithMany().HasForeignKey("WarehouseId").OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<Budget>(b =>
        {
            b.ToTable("Budgets");
            b.HasKey(x => x.Id);
            b.Property(x => x.Name).HasMaxLength(100);
            b.HasIndex(x => x.Name).IsUnique();
            b.HasOne<Segment>().WithMany().HasForeignKey(x => x.ClassId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<Segment>().WithMany().HasForeignKey(x => x.LocationId).OnDelete(DeleteBehavior.Restrict);
            b.Ignore(x => x.FiscalYearEnd);
            b.OwnsMany(x => x.Lines, l =>
            {
                l.ToTable("BudgetLines");
                l.WithOwner().HasForeignKey("BudgetId");
                l.HasKey("BudgetId", nameof(BudgetLine.AccountId), nameof(BudgetLine.Period));
                l.Property(x => x.Amount).HasColumnName("AmountMinor").HasConversion(MinorUnits);
                l.HasIndex(x => x.AccountId);
                l.HasOne<Account>().WithMany().HasForeignKey(x => x.AccountId).OnDelete(DeleteBehavior.Restrict);
            });
            b.Navigation(x => x.Lines).UsePropertyAccessMode(PropertyAccessMode.Field);
        });

        modelBuilder.Entity<SavedReport>(b =>
        {
            b.ToTable("SavedReports");
            b.HasKey(x => x.Id);
            b.Property(x => x.Name).HasMaxLength(100);
            b.HasIndex(x => x.Name).IsUnique();
            b.Property(x => x.ReportKey).HasMaxLength(40);
            b.Property(x => x.Query).HasMaxLength(SavedReport.MaxQueryLength);
            b.Property(x => x.CreatedBy).HasMaxLength(100);
        });

        modelBuilder.Entity<TaxAgency>(b =>
        {
            b.ToTable("TaxAgencies");
            b.HasKey(a => a.Id);
            b.Property(a => a.Name).HasMaxLength(100);
            b.HasIndex(a => a.Name).IsUnique();
            b.Property(a => a.RegistrationNumber).HasMaxLength(50);
            b.Property(a => a.FilingFrequency).HasConversion<string>().HasMaxLength(20);
            b.HasIndex(a => a.LiabilityAccountId).IsUnique();
            b.HasOne<Account>().WithMany().HasForeignKey(a => a.LiabilityAccountId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<TaxRate>(b =>
        {
            b.ToTable("TaxRates");
            b.HasKey(r => r.Id);
            b.Property(r => r.Name).HasMaxLength(100);
            b.HasIndex(r => r.Name).IsUnique();
            b.Property(r => r.Percent).HasColumnName("PercentTenThousandths").HasConversion(TenThousandths);
            b.HasOne<TaxAgency>().WithMany().HasForeignKey(r => r.AgencyId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<TaxCode>(b =>
        {
            b.ToTable("TaxCodes");
            b.HasKey(c => c.Id);
            b.Property(c => c.Name).HasMaxLength(100);
            b.HasIndex(c => c.Name).IsUnique();
            b.Property(c => c.Description).HasMaxLength(500);
            b.Ignore(c => c.RateIds);
            b.Ignore(c => c.IsCombined);
            b.OwnsMany(c => c.Rates, r =>
            {
                r.ToTable("TaxCodeRates");
                r.WithOwner().HasForeignKey("TaxCodeId");
                r.HasKey("TaxCodeId", nameof(TaxCodeRate.Position));
                r.HasIndex(x => x.TaxRateId);
                r.HasOne<TaxRate>().WithMany().HasForeignKey(x => x.TaxRateId).OnDelete(DeleteBehavior.Restrict);
            });
            b.Navigation(c => c.Rates).UsePropertyAccessMode(PropertyAccessMode.Field);
        });

        modelBuilder.Entity<SalesTaxPayment>(b =>
        {
            b.ToTable("SalesTaxPayments");
            b.HasKey(p => p.Id);
            b.Property(p => p.Amount).HasColumnName("AmountMinor").HasConversion(MinorUnits);
            b.Property(p => p.Adjustment).HasColumnName("AdjustmentMinor").HasConversion(MinorUnits);
            b.Property(p => p.Reference).HasMaxLength(30);
            b.Property(p => p.Memo).HasMaxLength(1000);
            b.Property(p => p.Status).HasConversion<string>().HasMaxLength(20);
            b.Ignore(p => p.LiabilitySettled);
            b.HasIndex(p => new { p.AgencyId, p.Date });
            b.HasIndex(p => p.JournalEntryId).IsUnique();
            b.HasOne<TaxAgency>().WithMany().HasForeignKey(p => p.AgencyId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<Account>().WithMany().HasForeignKey(p => p.PaymentAccountId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<Account>().WithMany().HasForeignKey(p => p.LiabilityAccountId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<Account>().WithMany().HasForeignKey(p => p.AdjustmentAccountId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<JournalEntry>().WithMany().HasForeignKey(p => p.JournalEntryId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<BankRule>(b =>
        {
            b.ToTable("BankRules");
            b.HasKey(r => r.Id);
            b.Property(r => r.Name).HasMaxLength(100);
            b.Property(r => r.Contains).HasMaxLength(200);
            b.Property(r => r.Payee).HasMaxLength(200);
            b.Property(r => r.Direction).HasConversion<string>().HasMaxLength(20);
            b.Property(r => r.MinAmount).HasColumnName("MinAmountMinor").HasConversion(NullableMinorUnits);
            b.Property(r => r.MaxAmount).HasColumnName("MaxAmountMinor").HasConversion(NullableMinorUnits);
            b.HasOne<Account>().WithMany().HasForeignKey(r => r.AccountId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<Account>().WithMany().HasForeignKey(r => r.CategoryAccountId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<Vendor>().WithMany().HasForeignKey(r => r.VendorId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<Reconciliation>(b =>
        {
            b.ToTable("Reconciliations");
            b.HasKey(r => r.Id);
            b.Property(r => r.StatementEndingBalance).HasColumnName("StatementEndingBalanceMinor").HasConversion(MinorUnits);
            b.Property(r => r.BeginningBalance).HasColumnName("BeginningBalanceMinor").HasConversion(MinorUnits);
            b.Property(r => r.Status).HasConversion<string>().HasMaxLength(20);
            b.Property(r => r.StartedBy).HasMaxLength(200);
            b.Property(r => r.CompletedBy).HasMaxLength(200);
            b.HasIndex(r => new { r.AccountId, r.StatementDate });
            b.HasOne<Account>().WithMany().HasForeignKey(r => r.AccountId).OnDelete(DeleteBehavior.Restrict);
            b.HasMany(r => r.Lines).WithOne().HasForeignKey("ReconciliationId").OnDelete(DeleteBehavior.Cascade);
            b.Navigation(r => r.Lines).UsePropertyAccessMode(PropertyAccessMode.Field);
        });

        modelBuilder.Entity<ReconciliationLine>(b =>
        {
            b.ToTable("ReconciliationLines");
            b.Property<Guid>("ReconciliationId");
            b.HasKey("ReconciliationId", nameof(ReconciliationLine.JournalLineId));
            // A ledger line is ticked in at most one reconciliation.
            b.HasIndex(l => l.JournalLineId).IsUnique();
            b.HasOne<JournalLine>().WithMany().HasForeignKey(l => l.JournalLineId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<RecurringEntry>(b =>
        {
            b.ToTable("RecurringEntries");
            b.Property(r => r.Tags).HasMaxLength(800);
            b.Ignore(r => r.TagIds);
            b.HasOne<Segment>().WithMany().HasForeignKey(r => r.LocationId).OnDelete(DeleteBehavior.Restrict);
            b.HasKey(r => r.Id);
            b.Property(r => r.Name).HasMaxLength(100);
            b.Property(r => r.Memo).HasMaxLength(1000);
            b.Property(r => r.Basis).HasConversion<string>().HasMaxLength(20);
            b.Property(r => r.Frequency).HasConversion<string>().HasMaxLength(20);
            b.Ignore(r => r.NextDate);
            b.OwnsMany(r => r.Lines, l =>
            {
                l.ToTable("RecurringEntryLines");
                l.HasOne<Segment>().WithMany().HasForeignKey(x => x.ClassId).OnDelete(DeleteBehavior.Restrict);
                l.WithOwner().HasForeignKey("RecurringEntryId");
                l.HasKey("RecurringEntryId", nameof(RecurringEntryLine.LineNumber));
                l.Property(x => x.Debit).HasColumnName("DebitMinor").HasConversion(MinorUnits);
                l.Property(x => x.Credit).HasColumnName("CreditMinor").HasConversion(MinorUnits);
                l.Property(x => x.Memo).HasMaxLength(1000);
                l.HasOne<Account>().WithMany().HasForeignKey(x => x.AccountId).OnDelete(DeleteBehavior.Restrict);
            });
            b.Navigation(r => r.Lines).UsePropertyAccessMode(PropertyAccessMode.Field);
        });

        ConfigureSales(modelBuilder);
        ConfigurePurchases(modelBuilder);

        modelBuilder.Entity<User>(b =>
        {
            b.ToTable("Users");
            b.HasKey(u => u.Id);
            b.Property(u => u.Username).HasMaxLength(64);
            b.HasIndex(u => u.Username).IsUnique();
            b.Property(u => u.PasswordHash).HasMaxLength(500);
            b.Property(u => u.SecurityStamp).HasMaxLength(64);
            b.Property(u => u.Role).HasConversion<string>().HasMaxLength(20);
        });

        modelBuilder.Entity<Note>(b =>
        {
            b.ToTable("Notes");
            b.HasKey(n => n.Id);
            b.Property(n => n.EntityType).HasMaxLength(40);
            b.Property(n => n.Text).HasMaxLength(Note.MaxLength);
            b.Property(n => n.CreatedBy).HasMaxLength(100);
            b.HasIndex(n => new { n.EntityType, n.EntityId });
        });

        modelBuilder.Entity<UserSession>(b =>
        {
            b.ToTable("UserSessions");
            b.HasKey(s => s.Id);
            b.Property(s => s.SecurityStamp).HasMaxLength(64);
            b.Property(s => s.Device).HasMaxLength(200);
            b.Property(s => s.Address).HasMaxLength(45);
            b.HasIndex(s => s.UserId);
            b.HasOne<User>().WithMany().HasForeignKey(s => s.UserId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<ApiKey>(b =>
        {
            b.ToTable("ApiKeys");
            b.HasKey(k => k.Id);
            b.Property(k => k.Name).HasMaxLength(100);
            b.Property(k => k.TokenHash).HasMaxLength(64);
            b.HasIndex(k => k.TokenHash).IsUnique();
            b.Property(k => k.Hint).HasMaxLength(20);
            b.HasIndex(k => k.UserId);
            b.HasOne<User>().WithMany().HasForeignKey(k => k.UserId).OnDelete(DeleteBehavior.Restrict);
            b.Property(k => k.Scopes).HasMaxLength(500);
            b.Ignore(k => k.ScopeList);
        });

        modelBuilder.Entity<WebhookSubscription>(b =>
        {
            b.ToTable("WebhookSubscriptions");
            b.HasKey(s => s.Id);
            b.Property(s => s.Url).HasMaxLength(500);
            b.Property(s => s.Description).HasMaxLength(200);
            b.Property(s => s.Events).HasMaxLength(2000);
            b.Property(s => s.ProtectedSecret).HasMaxLength(2000);
        });

        modelBuilder.Entity<WebhookDelivery>(b =>
        {
            b.ToTable("WebhookDeliveries");
            b.HasKey(d => d.Id);
            b.Property(d => d.EventType).HasMaxLength(100);
            b.Property(d => d.Status).HasConversion<string>().HasMaxLength(20);
            b.Property(d => d.LastError).HasMaxLength(500);
            b.HasIndex(d => new { d.Status, d.NextAttemptAt });
            b.HasIndex(d => new { d.SubscriptionId, d.CreatedAt });
            b.HasOne<WebhookSubscription>().WithMany().HasForeignKey(d => d.SubscriptionId).OnDelete(DeleteBehavior.Cascade);
        });
    }

    private static void ConfigureAssets(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<OpenBook.Domain.Assets.FixedAsset>(b =>
        {
            b.ToTable("FixedAssets");
            b.HasKey(a => a.Id);
            b.Property(a => a.Name).HasMaxLength(200);
            b.Property(a => a.Number).HasMaxLength(30);
            b.Property(a => a.Description).HasMaxLength(1000);
            b.Property(a => a.Cost).HasColumnName("CostMinor").HasConversion(MinorUnits);
            b.Property(a => a.Salvage).HasColumnName("SalvageMinor").HasConversion(MinorUnits);
            b.Property(a => a.PriorDepreciation).HasColumnName("PriorDepreciationMinor").HasConversion(MinorUnits);
            b.Property(a => a.Proceeds).HasColumnName("ProceedsMinor").HasConversion(MinorUnits);
            b.Property(a => a.DisposalDepreciation).HasColumnName("DisposalDepreciationMinor").HasConversion(MinorUnits);
            b.Property(a => a.DecliningPercent).HasColumnName("DecliningPercentTenThousandths").HasConversion(TenThousandths);
            b.Property(a => a.Method).HasConversion<string>().HasMaxLength(20);
            b.Property(a => a.MacrsClass).HasConversion<string>().HasMaxLength(20);
            b.Property(a => a.Status).HasConversion<string>().HasMaxLength(20);
            b.Ignore(a => a.IsDisposed);
            b.Ignore(a => a.Base);
            b.HasOne<Account>().WithMany().HasForeignKey(a => a.AssetAccountId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<Account>().WithMany().HasForeignKey(a => a.AccumulatedAccountId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<Account>().WithMany().HasForeignKey(a => a.ExpenseAccountId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<Account>().WithMany().HasForeignKey(a => a.ProceedsAccountId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<JournalEntry>().WithMany().HasForeignKey(a => a.DisposalEntryId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<OpenBook.Domain.Assets.DepreciationRun>(b =>
        {
            b.ToTable("DepreciationRuns");
            b.HasKey(r => r.Id);
            b.HasIndex(r => r.JournalEntryId).IsUnique();
            b.Property(r => r.Status).HasConversion<string>().HasMaxLength(20);
            b.Ignore(r => r.Total);
            b.HasOne<JournalEntry>().WithMany().HasForeignKey(r => r.JournalEntryId).OnDelete(DeleteBehavior.Restrict);
            b.OwnsMany(r => r.Lines, l =>
            {
                l.ToTable("DepreciationRunLines");
                l.WithOwner().HasForeignKey("DepreciationRunId");
                l.HasKey("DepreciationRunId", nameof(OpenBook.Domain.Assets.DepreciationRunLine.LineNumber));
                l.Property(x => x.Amount).HasColumnName("AmountMinor").HasConversion(MinorUnits);
                l.HasIndex(x => x.AssetId);
                l.HasOne<OpenBook.Domain.Assets.FixedAsset>().WithMany().HasForeignKey(x => x.AssetId).OnDelete(DeleteBehavior.Restrict);
            });
            b.Navigation(r => r.Lines).UsePropertyAccessMode(PropertyAccessMode.Field);
        });

        modelBuilder.Entity<OpenBook.Domain.Assets.Loan>(b =>
        {
            b.ToTable("Loans");
            b.HasKey(l => l.Id);
            b.Property(l => l.Name).HasMaxLength(200);
            b.Property(l => l.Notes).HasMaxLength(1000);
            b.Property(l => l.Principal).HasColumnName("PrincipalMinor").HasConversion(MinorUnits);
            b.Property(l => l.PaymentAmount).HasColumnName("PaymentAmountMinor").HasConversion(MinorUnits);
            b.Property(l => l.AnnualRatePercent).HasColumnName("AnnualRatePercentTenThousandths").HasConversion(TenThousandths);
            b.Ignore(l => l.MonthlyRate);
            b.Ignore(l => l.Balance);
            b.Ignore(l => l.IsPaidOff);
            b.Ignore(l => l.NextPayment);
            b.Ignore(l => l.LatestPayment);
            b.HasOne<Account>().WithMany().HasForeignKey(l => l.LiabilityAccountId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<Account>().WithMany().HasForeignKey(l => l.InterestAccountId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<Account>().WithMany().HasForeignKey(l => l.ProceedsAccountId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<Vendor>().WithMany().HasForeignKey(l => l.LenderVendorId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<JournalEntry>().WithMany().HasForeignKey(l => l.ProceedsEntryId).OnDelete(DeleteBehavior.Restrict);
            b.OwnsMany(l => l.PaymentsMade, p =>
            {
                p.ToTable("LoanPayments");
                p.WithOwner().HasForeignKey("LoanId");
                p.HasKey(x => x.Id);
                p.Property(x => x.Id).ValueGeneratedNever();
                p.Property(x => x.Amount).HasColumnName("AmountMinor").HasConversion(MinorUnits);
                p.Property(x => x.Interest).HasColumnName("InterestMinor").HasConversion(MinorUnits);
                p.Property(x => x.Principal).HasColumnName("PrincipalMinor").HasConversion(MinorUnits);
                p.HasIndex(x => x.JournalEntryId).IsUnique();
                p.HasOne<Account>().WithMany().HasForeignKey(x => x.PaymentAccountId).OnDelete(DeleteBehavior.Restrict);
                p.HasOne<JournalEntry>().WithMany().HasForeignKey(x => x.JournalEntryId).OnDelete(DeleteBehavior.Restrict);
            });
            b.Navigation(l => l.PaymentsMade).HasField("_payments").UsePropertyAccessMode(PropertyAccessMode.Field);
        });
    }

    private static void ConfigureProjects(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<OpenBook.Domain.Projects.Project>(b =>
        {
            b.ToTable("Projects");
            b.HasKey(p => p.Id);
            b.HasIndex(p => p.CustomerId).IsUnique();
            b.Property(p => p.Status).HasConversion<string>().HasMaxLength(20);
            b.Property(p => p.BudgetRevenue).HasColumnName("BudgetRevenueMinor").HasConversion(NullableMinorUnits);
            b.Property(p => p.BudgetCost).HasColumnName("BudgetCostMinor").HasConversion(NullableMinorUnits);
            b.Property(p => p.BudgetHours).HasColumnName("BudgetHoursTenThousandths").HasConversion(NullableTenThousandths);
            b.Property(p => p.Description).HasMaxLength(2000);
            b.Ignore(p => p.IsOpen);
            b.HasOne<Customer>().WithMany().HasForeignKey(p => p.CustomerId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<OpenBook.Domain.Projects.Worker>(b =>
        {
            b.ToTable("Workers");
            b.HasKey(w => w.Id);
            b.Property(w => w.Name).HasMaxLength(100);
            b.Property(w => w.Username).HasMaxLength(100);
            b.Property(w => w.CostRate).HasColumnName("CostRateTenThousandths").HasConversion(TenThousandths);
            b.Property(w => w.BillRate).HasColumnName("BillRateTenThousandths").HasConversion(NullableTenThousandths);
            b.Property(w => w.Role).HasMaxLength(OpenBook.Domain.Projects.Worker.MaxRoleLength);
            b.Property(w => w.WeeklyHours).HasColumnName("WeeklyHoursTenThousandths").HasConversion(NullableTenThousandths);
            b.HasOne<Vendor>().WithMany().HasForeignKey(w => w.VendorId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<OpenBook.Domain.Channels.SalesChannel>(b =>
        {
            b.ToTable("SalesChannels");
            b.HasKey(c => c.Id);
            b.Property(c => c.Name).HasMaxLength(100);
            b.Property(c => c.Kind).HasConversion<string>().HasMaxLength(20);
            b.HasOne<Account>().WithMany().HasForeignKey(c => c.DepositAccountId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<Account>().WithMany().HasForeignKey(c => c.SalesAccountId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<Account>().WithMany().HasForeignKey(c => c.ShippingAccountId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<Account>().WithMany().HasForeignKey(c => c.RefundsAccountId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<Account>().WithMany().HasForeignKey(c => c.FeesAccountId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<Account>().WithMany().HasForeignKey(c => c.TaxAccountId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<Account>().WithMany().HasForeignKey(c => c.AdjustmentsAccountId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<Account>().WithMany().HasForeignKey(c => c.ClearingAccountId).OnDelete(DeleteBehavior.Restrict);
            b.Property(c => c.ShopDomain).HasMaxLength(200);
            b.Property(c => c.ProtectedCredential).HasMaxLength(2000);
            b.Property(c => c.LastError).HasMaxLength(500);
            b.Ignore(c => c.IsConnected);
        });

        modelBuilder.Entity<OpenBook.Domain.Channels.ChannelPayout>(b =>
        {
            b.ToTable("ChannelPayouts");
            b.HasKey(p => p.Id);
            b.Property(p => p.ExternalId).HasMaxLength(200);
            b.HasIndex(p => new { p.ChannelId, p.ExternalId }).IsUnique();
            b.Property(p => p.Status).HasConversion<string>().HasMaxLength(20);
            b.Property(p => p.ReportedNet).HasColumnName("ReportedNetMinor").HasConversion(NullableMinorUnits);
            b.Ignore(p => p.Net);
            b.Ignore(p => p.Difference);
            b.HasOne<OpenBook.Domain.Channels.SalesChannel>().WithMany().HasForeignKey(p => p.ChannelId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<JournalEntry>().WithMany().HasForeignKey(p => p.JournalEntryId).OnDelete(DeleteBehavior.Restrict);
            b.OwnsMany(p => p.Lines, l =>
            {
                l.ToTable("ChannelPayoutLines");
                l.WithOwner().HasForeignKey("ChannelPayoutId");
                l.HasKey("ChannelPayoutId", nameof(OpenBook.Domain.Channels.ChannelPayoutLine.LineNumber));
                l.Property(x => x.Type).HasConversion<string>().HasMaxLength(20);
                l.Property(x => x.Amount).HasColumnName("AmountMinor").HasConversion(MinorUnits);
                l.Property(x => x.Reference).HasMaxLength(100);
                l.Property(x => x.Description).HasMaxLength(300);
                l.Property(x => x.Region).HasMaxLength(50);
            });
            b.Navigation(p => p.Lines).UsePropertyAccessMode(PropertyAccessMode.Field);
        });

        modelBuilder.Entity<OpenBook.Domain.Channels.ChannelOrderBatch>(b =>
        {
            b.ToTable("ChannelOrderBatches");
            b.HasKey(x => x.Id);
            b.HasIndex(x => new { x.ChannelId, x.Date });
            b.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
            b.Ignore(x => x.Orders);
            b.HasOne<OpenBook.Domain.Channels.SalesChannel>().WithMany().HasForeignKey(x => x.ChannelId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<JournalEntry>().WithMany().HasForeignKey(x => x.JournalEntryId).OnDelete(DeleteBehavior.Restrict);
            b.OwnsMany(x => x.Lines, l =>
            {
                l.ToTable("ChannelOrderLines");
                l.WithOwner().HasForeignKey("ChannelOrderBatchId");
                l.HasKey("ChannelOrderBatchId", nameof(OpenBook.Domain.Channels.ChannelOrderLine.LineNumber));
                l.HasIndex(x => x.OrderId);
                l.Property(x => x.OrderId).HasMaxLength(100);
                l.Property(x => x.Sku).HasMaxLength(100);
                l.Property(x => x.Title).HasMaxLength(300);
                l.Property(x => x.Region).HasMaxLength(50);
                l.Property(x => x.Quantity).HasColumnName("QuantityTenThousandths").HasConversion(TenThousandths);
                l.Property(x => x.Sales).HasColumnName("SalesMinor").HasConversion(MinorUnits);
                l.Property(x => x.Shipping).HasColumnName("ShippingMinor").HasConversion(MinorUnits);
                l.Property(x => x.Tax).HasColumnName("TaxMinor").HasConversion(MinorUnits);
            });
            b.Navigation(x => x.Lines).UsePropertyAccessMode(PropertyAccessMode.Field);
        });

        modelBuilder.Entity<OpenBook.Domain.Nonprofit.RestrictionRelease>(b =>
        {
            b.ToTable("RestrictionReleases");
            b.HasKey(r => r.Id);
            b.HasIndex(r => r.JournalEntryId).IsUnique();
            b.Property(r => r.Amount).HasColumnName("AmountMinor").HasConversion(MinorUnits);
            b.Property(r => r.Memo).HasMaxLength(OpenBook.Domain.Nonprofit.RestrictionRelease.MaxMemoLength);
            b.Property(r => r.Status).HasConversion<string>().HasMaxLength(20);
            b.HasOne<JournalEntry>().WithMany().HasForeignKey(r => r.JournalEntryId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<Segment>().WithMany().HasForeignKey(r => r.ClassId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<OpenBook.Domain.Nonprofit.Donation>(b =>
        {
            b.ToTable("Donations");
            b.HasKey(d => d.Id);
            b.HasIndex(d => d.Number).IsUnique();
            b.HasIndex(d => d.DonorId);
            b.HasIndex(d => d.PledgeId);
            b.HasIndex(d => d.JournalEntryId).IsUnique();
            b.Property(d => d.Kind).HasConversion<string>().HasMaxLength(20);
            b.Property(d => d.Status).HasConversion<string>().HasMaxLength(20);
            b.Property(d => d.Amount).HasColumnName("AmountMinor").HasConversion(MinorUnits);
            b.Property(d => d.GoodsValue).HasColumnName("GoodsValueMinor").HasConversion(MinorUnits);
            b.Property(d => d.Reference).HasMaxLength(OpenBook.Domain.Nonprofit.Donation.MaxReferenceLength);
            b.Property(d => d.Memo).HasMaxLength(OpenBook.Domain.Nonprofit.Donation.MaxTextLength);
            b.Property(d => d.Description).HasMaxLength(OpenBook.Domain.Nonprofit.Donation.MaxTextLength);
            b.Property(d => d.GoodsDescription).HasMaxLength(OpenBook.Domain.Nonprofit.Donation.MaxTextLength);
            b.Ignore(d => d.Deductible);
            b.HasOne<Customer>().WithMany().HasForeignKey(d => d.DonorId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<Account>().WithMany().HasForeignKey(d => d.DebitAccountId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<Account>().WithMany().HasForeignKey(d => d.IncomeAccountId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<OpenBook.Domain.Nonprofit.Pledge>().WithMany().HasForeignKey(d => d.PledgeId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<Segment>().WithMany().HasForeignKey(d => d.ClassId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<JournalEntry>().WithMany().HasForeignKey(d => d.JournalEntryId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<JournalEntry>().WithMany().HasForeignKey(d => d.CashEntryId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<OpenBook.Domain.Nonprofit.Pledge>(b =>
        {
            b.ToTable("Pledges");
            b.HasKey(p => p.Id);
            b.HasIndex(p => p.Number).IsUnique();
            b.HasIndex(p => p.DonorId);
            b.HasIndex(p => p.JournalEntryId).IsUnique();
            b.Property(p => p.Status).HasConversion<string>().HasMaxLength(20);
            b.Property(p => p.Amount).HasColumnName("AmountMinor").HasConversion(MinorUnits);
            b.Property(p => p.CancelledAmount).HasColumnName("CancelledMinor").HasConversion(MinorUnits);
            b.HasOne<JournalEntry>().WithMany().HasForeignKey(p => p.CancelEntryId).OnDelete(DeleteBehavior.Restrict);
            b.Property(p => p.Memo).HasMaxLength(OpenBook.Domain.Nonprofit.Donation.MaxTextLength);
            b.HasOne<Customer>().WithMany().HasForeignKey(p => p.DonorId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<Account>().WithMany().HasForeignKey(p => p.ReceivableAccountId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<Account>().WithMany().HasForeignKey(p => p.IncomeAccountId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<Segment>().WithMany().HasForeignKey(p => p.ClassId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<JournalEntry>().WithMany().HasForeignKey(p => p.JournalEntryId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<OpenBook.Domain.Nonprofit.Grant>(b =>
        {
            b.ToTable("Grants");
            b.HasKey(g => g.Id);
            b.Property(g => g.Name).HasMaxLength(OpenBook.Domain.Nonprofit.Grant.MaxNameLength);
            b.Property(g => g.Reference).HasMaxLength(50);
            b.Property(g => g.Terms).HasMaxLength(OpenBook.Domain.Nonprofit.Grant.MaxTextLength);
            b.Property(g => g.Amount).HasColumnName("AmountMinor").HasConversion(MinorUnits);
            b.Property(g => g.Status).HasConversion<string>().HasMaxLength(20);
            b.Ignore(g => g.Received);
            b.Ignore(g => g.Recognized);
            b.Ignore(g => g.Due);
            b.Ignore(g => g.Advance);
            b.Ignore(g => g.Receivable);
            b.HasOne<Account>().WithMany().HasForeignKey(g => g.AdvanceAccountId).OnDelete(DeleteBehavior.Restrict);
            b.HasIndex(g => g.FunderId);
            b.HasOne<Customer>().WithMany().HasForeignKey(g => g.FunderId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<Segment>().WithMany().HasForeignKey(g => g.ClassId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<Account>().WithMany().HasForeignKey(g => g.IncomeAccountId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<Account>().WithMany().HasForeignKey(g => g.ReceivableAccountId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<JournalEntry>().WithMany().HasForeignKey(g => g.AwardEntryId).OnDelete(DeleteBehavior.Restrict);
            b.OwnsMany(g => g.Budget, l =>
            {
                l.ToTable("GrantBudgetLines");
                l.WithOwner().HasForeignKey("GrantId");
                l.HasKey("GrantId", nameof(OpenBook.Domain.Nonprofit.GrantBudgetLine.LineNumber));
                l.Property(x => x.Amount).HasColumnName("AmountMinor").HasConversion(MinorUnits);
                l.HasOne<Account>().WithMany().HasForeignKey(x => x.AccountId).OnDelete(DeleteBehavior.Restrict);
            });
            b.OwnsMany(g => g.Receipts, l =>
            {
                l.ToTable("GrantReceipts");
                l.WithOwner().HasForeignKey("GrantId");
                l.HasKey("GrantId", nameof(OpenBook.Domain.Nonprofit.GrantReceipt.LineNumber));
                l.Property(x => x.Amount).HasColumnName("AmountMinor").HasConversion(MinorUnits);
                l.Property(x => x.AdvancePart).HasColumnName("AdvancePartMinor").HasConversion(MinorUnits);
                l.HasOne<Account>().WithMany().HasForeignKey(x => x.BankAccountId).OnDelete(DeleteBehavior.Restrict);
                l.HasOne<JournalEntry>().WithMany().HasForeignKey(x => x.EntryId).OnDelete(DeleteBehavior.Restrict);
            });
            b.OwnsMany(g => g.Recognitions, l =>
            {
                l.ToTable("GrantRecognitions");
                l.WithOwner().HasForeignKey("GrantId");
                l.HasKey("GrantId", nameof(OpenBook.Domain.Nonprofit.GrantRecognition.LineNumber));
                l.Property(x => x.Amount).HasColumnName("AmountMinor").HasConversion(MinorUnits);
                l.Property(x => x.AdvancePart).HasColumnName("AdvancePartMinor").HasConversion(MinorUnits);
                l.HasOne<JournalEntry>().WithMany().HasForeignKey(x => x.EntryId).OnDelete(DeleteBehavior.Restrict);
            });
            b.Navigation(g => g.Budget).UsePropertyAccessMode(PropertyAccessMode.Field);
            b.Navigation(g => g.Receipts).UsePropertyAccessMode(PropertyAccessMode.Field);
            b.Navigation(g => g.Recognitions).UsePropertyAccessMode(PropertyAccessMode.Field);
        });

        modelBuilder.Entity<OpenBook.Domain.Nonprofit.RecurringGift>(b =>
        {
            b.ToTable("RecurringGifts");
            b.HasKey(t => t.Id);
            b.Property(t => t.Amount).HasColumnName("AmountMinor").HasConversion(MinorUnits);
            b.Property(t => t.Frequency).HasConversion<string>().HasMaxLength(20);
            b.Property(t => t.Memo).HasMaxLength(OpenBook.Domain.Nonprofit.Donation.MaxTextLength);
            b.Ignore(t => t.NextDate);
            b.HasIndex(t => t.DonorId);
            b.HasOne<Customer>().WithMany().HasForeignKey(t => t.DonorId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<Account>().WithMany().HasForeignKey(t => t.DepositAccountId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<Account>().WithMany().HasForeignKey(t => t.IncomeAccountId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<Segment>().WithMany().HasForeignKey(t => t.ClassId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<OpenBook.Domain.Nonprofit.Pledge>().WithMany().HasForeignKey(t => t.PledgeId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<OpenBook.Domain.Nonprofit.GiftSettings>(b =>
        {
            b.ToTable("GiftSettings");
            b.HasKey(s => s.Id);
            b.Property(s => s.Ein).HasMaxLength(10);
            b.Property(s => s.SignerName).HasMaxLength(100);
            b.Property(s => s.SignerTitle).HasMaxLength(100);
            b.Property(s => s.Intro).HasMaxLength(OpenBook.Domain.Nonprofit.GiftSettings.MaxTextLength);
        });

        modelBuilder.Entity<OpenBook.Domain.Channels.ChannelReturn>(b =>
        {
            b.ToTable("ChannelReturns");
            b.HasKey(r => r.Id);
            b.HasIndex(r => r.BatchId);
            b.Property(r => r.Memo).HasMaxLength(OpenBook.Domain.Channels.ChannelReturn.MaxMemoLength);
            b.Property(r => r.Status).HasConversion<string>().HasMaxLength(20);
            b.HasOne<OpenBook.Domain.Channels.SalesChannel>().WithMany().HasForeignKey(r => r.ChannelId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<OpenBook.Domain.Channels.ChannelOrderBatch>().WithMany().HasForeignKey(r => r.BatchId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<JournalEntry>().WithMany().HasForeignKey(r => r.JournalEntryId).OnDelete(DeleteBehavior.Restrict);
            b.OwnsMany(r => r.Lines, l =>
            {
                l.ToTable("ChannelReturnLines");
                l.WithOwner().HasForeignKey("ChannelReturnId");
                l.HasKey("ChannelReturnId", nameof(OpenBook.Domain.Channels.ChannelReturnLine.LineNumber));
                l.Property(x => x.Quantity).HasColumnName("QuantityTenThousandths").HasConversion(TenThousandths);
            });
            b.Navigation(r => r.Lines).UsePropertyAccessMode(PropertyAccessMode.Field);
        });

        modelBuilder.Entity<OpenBook.Domain.Channels.ChannelSku>(b =>
        {
            b.ToTable("ChannelSkus");
            b.HasKey(x => x.Id);
            b.Property(x => x.Sku).HasMaxLength(100);
            b.HasIndex(x => new { x.ChannelId, x.Sku }).IsUnique();
            b.HasOne<OpenBook.Domain.Channels.SalesChannel>().WithMany().HasForeignKey(x => x.ChannelId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<Item>().WithMany().HasForeignKey(x => x.ItemId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<OpenBook.Domain.Projects.BillingRate>(b =>
        {
            b.ToTable("BillingRates");
            b.HasKey(r => r.Id);
            b.Property(r => r.Role).HasMaxLength(OpenBook.Domain.Projects.Worker.MaxRoleLength);
            b.Property(r => r.Rate).HasColumnName("RateTenThousandths").HasConversion(TenThousandths);
            b.HasIndex(r => r.CustomerId);
            b.HasOne<Customer>().WithMany().HasForeignKey(r => r.CustomerId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<OpenBook.Domain.Projects.Worker>().WithMany().HasForeignKey(r => r.WorkerId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<Item>().WithMany().HasForeignKey(r => r.ItemId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<OpenBook.Domain.Projects.TimeEntry>(b =>
        {
            b.ToTable("TimeEntries");
            b.HasKey(t => t.Id);
            b.HasIndex(t => new { t.WorkerId, t.Date });
            b.HasIndex(t => t.CustomerId);
            b.HasIndex(t => t.InvoiceId);
            b.Property(t => t.Hours).HasColumnName("HoursTenThousandths").HasConversion(TenThousandths);
            b.Property(t => t.BillRate).HasColumnName("BillRateTenThousandths").HasConversion(TenThousandths);
            b.Property(t => t.CostRate).HasColumnName("CostRateTenThousandths").HasConversion(TenThousandths);
            b.Property(t => t.Notes).HasMaxLength(1000);
            b.Property(t => t.RateSource).HasMaxLength(100);
            b.Ignore(t => t.IsRunning);
            b.Ignore(t => t.Amount);
            b.Ignore(t => t.Cost);
            b.HasOne<OpenBook.Domain.Projects.Worker>().WithMany().HasForeignKey(t => t.WorkerId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<Customer>().WithMany().HasForeignKey(t => t.CustomerId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<Item>().WithMany().HasForeignKey(t => t.ItemId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<Invoice>().WithMany().HasForeignKey(t => t.InvoiceId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<OpenBook.Domain.Projects.MileageTrip>(b =>
        {
            b.ToTable("MileageTrips");
            b.HasKey(t => t.Id);
            b.HasIndex(t => t.Date);
            b.HasIndex(t => t.CustomerId);
            b.HasIndex(t => t.InvoiceId);
            b.Property(t => t.Miles).HasColumnName("MilesTenThousandths").HasConversion(TenThousandths);
            b.Property(t => t.BillRate).HasColumnName("BillRateTenThousandths").HasConversion(TenThousandths);
            b.Property(t => t.Purpose).HasMaxLength(500);
            b.Property(t => t.Vehicle).HasMaxLength(100);
            b.Property(t => t.FromPlace).HasMaxLength(200);
            b.Property(t => t.ToPlace).HasMaxLength(200);
            b.Ignore(t => t.Amount);
            b.HasOne<OpenBook.Domain.Projects.Worker>().WithMany().HasForeignKey(t => t.WorkerId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<Customer>().WithMany().HasForeignKey(t => t.CustomerId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<Invoice>().WithMany().HasForeignKey(t => t.InvoiceId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<OpenBook.Domain.Projects.MileageRate>(b =>
        {
            b.ToTable("MileageRates");
            b.HasKey(r => r.Year);
            b.Property(r => r.Year).ValueGeneratedNever();
            b.Property(r => r.Rate).HasColumnName("RateTenThousandths").HasConversion(TenThousandths);
        });
    }

    private static void ConfigureSales(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Customer>(b =>
        {
            b.ToTable("Customers");
            b.HasKey(c => c.Id);
            b.Property(c => c.DisplayName).HasMaxLength(200);
            b.HasIndex(c => c.DisplayName).IsUnique();
            b.Property(c => c.CompanyName).HasMaxLength(200);
            b.Property(c => c.Email).HasMaxLength(320);
            b.Property(c => c.Phone).HasMaxLength(50);
            b.Property(c => c.BillingAddress).HasMaxLength(500);
            b.Property(c => c.ShippingAddress).HasMaxLength(500);
            b.Property(c => c.Notes).HasMaxLength(2000);
            b.Property(c => c.EarlyPaymentDiscountPercent).HasColumnName("EarlyPaymentDiscountPercentTenThousandths").HasConversion(TenThousandths);
            b.Ignore(c => c.Terms);
            b.HasOne<TaxCode>().WithMany().HasForeignKey(c => c.TaxCodeId).OnDelete(DeleteBehavior.Restrict);
            b.Property(c => c.ExemptionCertificate).HasMaxLength(50);
            b.Property(c => c.ExemptionReason).HasConversion<string>().HasMaxLength(20);
            b.HasOne<PriceLevel>().WithMany().HasForeignKey(c => c.PriceLevelId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<FormDesign>().WithMany().HasForeignKey(c => c.FormDesignId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<Customer>().WithMany().HasForeignKey(c => c.ParentId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<PriceLevel>(b =>
        {
            b.ToTable("PriceLevels");
            b.HasKey(l => l.Id);
            b.Property(l => l.Name).HasMaxLength(100);
            b.HasIndex(l => l.Name).IsUnique();
            b.Property(l => l.Kind).HasConversion<string>().HasMaxLength(20);
            b.Property(l => l.Percent).HasColumnName("PercentTenThousandths").HasConversion(TenThousandths);
            b.OwnsMany(l => l.Items, i =>
            {
                i.ToTable("PriceLevelItems");
                i.WithOwner().HasForeignKey("PriceLevelId");
                i.HasKey("PriceLevelId", nameof(PriceLevelItem.ItemId));
                i.Property(x => x.Price).HasColumnName("PriceTenThousandths").HasConversion(TenThousandths);
                i.HasOne<Item>().WithMany().HasForeignKey(x => x.ItemId).OnDelete(DeleteBehavior.Restrict);
            });
            b.Navigation(l => l.Items).UsePropertyAccessMode(PropertyAccessMode.Field);
        });

        modelBuilder.Entity<PriceRule>(b =>
        {
            b.ToTable("PriceRules");
            b.HasKey(r => r.Id);
            b.Property(r => r.Name).HasMaxLength(100);
            b.HasIndex(r => r.Name).IsUnique();
            b.Property(r => r.Customers).HasConversion<string>().HasMaxLength(20);
            b.Property(r => r.Adjustment).HasConversion<string>().HasMaxLength(20);
            b.Property(r => r.Rounding).HasConversion<string>().HasMaxLength(20);
            b.Property(r => r.Value).HasColumnName("ValueTenThousandths").HasConversion(TenThousandths);
            b.Property(r => r.MinQuantity).HasColumnName("MinQuantityTenThousandths").HasConversion(NullableTenThousandths);
            b.Property(r => r.MaxQuantity).HasColumnName("MaxQuantityTenThousandths").HasConversion(NullableTenThousandths);
            b.Ignore(r => r.AllItems);
            b.OwnsMany(r => r.Targets, t =>
            {
                t.ToTable("PriceRuleTargets");
                t.WithOwner().HasForeignKey("PriceRuleId");
                t.HasKey("PriceRuleId", nameof(PriceRuleTarget.Kind), nameof(PriceRuleTarget.TargetId));
                t.Property(x => x.Kind).HasConversion<string>().HasMaxLength(20);
            });
            b.Navigation(r => r.Targets).UsePropertyAccessMode(PropertyAccessMode.Field);
        });

        modelBuilder.Entity<Item>(b =>
        {
            b.ToTable("Items");
            b.HasKey(i => i.Id);
            b.Property(i => i.Name).HasMaxLength(100);
            b.HasIndex(i => i.Name).IsUnique();
            b.Property(i => i.Type).HasConversion<string>().HasMaxLength(20);
            b.Property(i => i.Description).HasMaxLength(1000);
            b.Property(i => i.Rate).HasColumnName("RateTenThousandths").HasConversion(TenThousandths);
            b.Property(i => i.PurchaseCost).HasColumnName("PurchaseCostTenThousandths").HasConversion(TenThousandths);
            b.Property(i => i.ReorderPoint).HasColumnName("ReorderPointTenThousandths").HasConversion(NullableTenThousandths);
            b.Ignore(i => i.IsInventory);
            b.OwnsMany(i => i.QuantityBreaks, q =>
            {
                q.ToTable("ItemQuantityBreaks");
                q.WithOwner().HasForeignKey("ItemId");
                q.HasKey("ItemId", nameof(QuantityBreak.FromQuantity));
                q.Property(x => x.FromQuantity).HasColumnName("FromQuantityTenThousandths").HasConversion(TenThousandths);
                q.Property(x => x.Kind).HasConversion<string>().HasMaxLength(20);
                q.Property(x => x.Value).HasColumnName("ValueTenThousandths").HasConversion(TenThousandths);
            });
            b.Navigation(i => i.QuantityBreaks).UsePropertyAccessMode(PropertyAccessMode.Field);
            b.Property(i => i.CostMethod).HasConversion<string>().HasMaxLength(20);
            b.Property(i => i.Tracking).HasConversion<string>().HasMaxLength(20);
            b.Property(i => i.Barcode).HasMaxLength(50);
            b.HasIndex(i => i.Barcode).IsUnique();
            b.Ignore(i => i.IsBundle);
            b.Ignore(i => i.IsAssembly);
            b.OwnsMany(i => i.Components, c =>
            {
                c.ToTable("ItemComponents");
                c.WithOwner().HasForeignKey("ItemId");
                c.HasKey("ItemId", nameof(ItemComponent.ComponentItemId));
                c.Property(x => x.Quantity).HasColumnName("QuantityTenThousandths").HasConversion(TenThousandths);
                c.HasIndex(x => x.ComponentItemId);
                c.HasOne<Item>().WithMany().HasForeignKey(x => x.ComponentItemId).OnDelete(DeleteBehavior.Restrict);
            });
            b.Navigation(i => i.Components).UsePropertyAccessMode(PropertyAccessMode.Field);
            b.HasOne<Account>().WithMany().HasForeignKey(i => i.IncomeAccountId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<Account>().WithMany().HasForeignKey(i => i.InventoryAssetAccountId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<Account>().WithMany().HasForeignKey(i => i.CostAccountId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<Warehouse>(b =>
        {
            b.ToTable("Warehouses");
            b.HasKey(x => x.Id);
            b.Property(x => x.Name).HasMaxLength(Warehouse.MaxNameLength);
            b.HasIndex(x => x.Name).IsUnique();
            b.Property(x => x.Address).HasMaxLength(500);
            b.Ignore(x => x.Key);
        });

        modelBuilder.Entity<PendingBuild>(b =>
        {
            b.ToTable("PendingBuilds");
            b.HasKey(x => x.Id);
            b.Property(x => x.Quantity).HasColumnName("QuantityTenThousandths").HasConversion(TenThousandths);
            b.Property(x => x.Reference).HasMaxLength(30);
            b.Property(x => x.Memo).HasMaxLength(1000);
            b.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
            b.Property(x => x.CreatedBy).HasMaxLength(100);
            b.HasIndex(x => x.Status);
            b.HasOne<Item>().WithMany().HasForeignKey(x => x.ItemId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<Warehouse>().WithMany().HasForeignKey(x => x.WarehouseId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<AssemblyBuild>().WithMany().HasForeignKey(x => x.BuildId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<LotDetail>(b =>
        {
            b.ToTable("LotDetails");
            b.HasKey(x => new { x.ItemId, x.Lot });
            b.Property(x => x.Lot).HasMaxLength(LotText.MaxLotLength);
            b.HasIndex(x => x.ExpiresOn);
            b.HasOne<Item>().WithMany().HasForeignKey(x => x.ItemId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<Bin>(b =>
        {
            b.ToTable("Bins");
            b.HasKey(x => x.Id);
            b.Property(x => x.Name).HasMaxLength(Bin.MaxNameLength);
            b.HasIndex(x => new { x.WarehouseId, x.Name }).IsUnique();
            b.HasOne<Warehouse>().WithMany().HasForeignKey(x => x.WarehouseId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<StockTransfer>(b =>
        {
            b.ToTable("StockTransfers");
            b.HasKey(x => x.Id);
            b.Property(x => x.Reference).HasMaxLength(30);
            b.Property(x => x.Memo).HasMaxLength(1000);
            b.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
            b.OwnsMany(x => x.Lines, l =>
            {
                l.ToTable("StockTransferLines");
                l.WithOwner().HasForeignKey("StockTransferId");
                l.HasKey("StockTransferId", nameof(StockTransferLine.LineNumber));
                l.Property(x => x.Quantity).HasColumnName("QuantityTenThousandths").HasConversion(TenThousandths);
                l.Property(x => x.Lots).HasMaxLength(1000);
                l.Ignore(x => x.From);
                l.Ignore(x => x.To);
                l.HasOne<Item>().WithMany().HasForeignKey(x => x.ItemId).OnDelete(DeleteBehavior.Restrict);
                l.HasOne<Warehouse>().WithMany().HasForeignKey(x => x.FromWarehouseId).OnDelete(DeleteBehavior.Restrict);
                l.HasOne<Warehouse>().WithMany().HasForeignKey(x => x.ToWarehouseId).OnDelete(DeleteBehavior.Restrict);
                l.HasOne<Bin>().WithMany().HasForeignKey(x => x.FromBinId).OnDelete(DeleteBehavior.Restrict);
                l.HasOne<Bin>().WithMany().HasForeignKey(x => x.ToBinId).OnDelete(DeleteBehavior.Restrict);
            });
            b.Navigation(x => x.Lines).UsePropertyAccessMode(PropertyAccessMode.Field);
        });

        modelBuilder.Entity<AssemblyBuild>(b =>
        {
            b.ToTable("AssemblyBuilds");
            b.HasKey(x => x.Id);
            b.Property(x => x.Quantity).HasColumnName("QuantityTenThousandths").HasConversion(TenThousandths);
            b.Property(x => x.Reference).HasMaxLength(30);
            b.Property(x => x.Memo).HasMaxLength(1000);
            b.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
            b.Ignore(x => x.Cost);
            b.Property(x => x.Lots).HasMaxLength(1000);
            b.HasOne<Warehouse>().WithMany().HasForeignKey(x => x.WarehouseId).OnDelete(DeleteBehavior.Restrict);
            b.HasIndex(x => x.ItemId);
            b.HasOne<Item>().WithMany().HasForeignKey(x => x.ItemId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<JournalEntry>().WithMany().HasForeignKey(x => x.JournalEntryId).OnDelete(DeleteBehavior.Restrict);
            b.OwnsMany(x => x.Lines, l =>
            {
                l.ToTable("AssemblyBuildLines");
                l.Property(x => x.Lots).HasMaxLength(1000);
                l.WithOwner().HasForeignKey("AssemblyBuildId");
                l.HasKey("AssemblyBuildId", nameof(AssemblyBuildLine.LineNumber));
                l.Property(x => x.Quantity).HasColumnName("QuantityTenThousandths").HasConversion(TenThousandths);
                l.Property(x => x.Cost).HasColumnName("CostMinor").HasConversion(MinorUnits);
                l.HasOne<Item>().WithMany().HasForeignKey(x => x.ItemId).OnDelete(DeleteBehavior.Restrict);
            });
            b.Navigation(x => x.Lines).UsePropertyAccessMode(PropertyAccessMode.Field);
        });

        modelBuilder.Entity<LandedCost>(b =>
        {
            b.ToTable("LandedCosts");
            b.HasKey(x => x.Id);
            b.Property(x => x.Amount).HasColumnName("AmountMinor").HasConversion(MinorUnits);
            b.Property(x => x.Method).HasConversion<string>().HasMaxLength(20);
            b.Property(x => x.Memo).HasMaxLength(1000);
            b.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
            b.Ignore(x => x.BillIds);
            b.HasOne<Account>().WithMany().HasForeignKey(x => x.FromAccountId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<JournalEntry>().WithMany().HasForeignKey(x => x.JournalEntryId).OnDelete(DeleteBehavior.Restrict);
            b.OwnsMany(x => x.Lines, l =>
            {
                l.ToTable("LandedCostLines");
                l.WithOwner().HasForeignKey("LandedCostId");
                l.HasKey("LandedCostId", nameof(LandedCostLine.LineNumber));
                l.Property(x => x.Amount).HasColumnName("AmountMinor").HasConversion(MinorUnits);
                l.Property(x => x.ToStock).HasColumnName("ToStockMinor").HasConversion(MinorUnits);
                l.Ignore(x => x.ToCostOfSales);
                l.HasIndex(x => x.BillId);
                l.HasOne<Bill>().WithMany().HasForeignKey(x => x.BillId).OnDelete(DeleteBehavior.Restrict);
                l.HasOne<InventoryMovement>().WithMany().HasForeignKey(x => x.LayerId).OnDelete(DeleteBehavior.Restrict);
                l.HasOne<Item>().WithMany().HasForeignKey(x => x.ItemId).OnDelete(DeleteBehavior.Restrict);
            });
            b.Navigation(x => x.Lines).UsePropertyAccessMode(PropertyAccessMode.Field);
        });

        modelBuilder.Entity<InventoryMovement>(b =>
        {
            b.ToTable("InventoryMovements");
            b.HasKey(m => m.Id);
            b.Property(m => m.SourceType).HasMaxLength(40);
            b.Property(m => m.Quantity).HasColumnName("QuantityTenThousandths").HasConversion(TenThousandths);
            b.Property(m => m.Cost).HasColumnName("CostMinor").HasConversion(MinorUnits);
            b.Ignore(m => m.IsReceipt);
            b.Ignore(m => m.Place);
            b.Property(m => m.Lot).HasMaxLength(50);
            b.HasIndex(m => new { m.ItemId, m.Lot });
            b.HasOne<Warehouse>().WithMany().HasForeignKey(m => m.WarehouseId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<Bin>().WithMany().HasForeignKey(m => m.BinId).OnDelete(DeleteBehavior.Restrict);
            b.HasIndex(m => new { m.ItemId, m.Date });
            b.HasIndex(m => m.LayerId);
            b.HasIndex(m => new { m.SourceType, m.SourceId });
            b.HasOne<Item>().WithMany().HasForeignKey(m => m.ItemId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<InventoryAdjustment>(b =>
        {
            b.ToTable("InventoryAdjustments");
            b.HasKey(a => a.Id);
            b.Property(a => a.Reference).HasMaxLength(30);
            b.Property(a => a.Memo).HasMaxLength(1000);
            b.Property(a => a.Status).HasConversion<string>().HasMaxLength(20);
            b.Ignore(a => a.ValueChange);
            b.HasIndex(a => a.JournalEntryId).IsUnique();
            b.HasOne<Account>().WithMany().HasForeignKey(a => a.AdjustmentAccountId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<JournalEntry>().WithMany().HasForeignKey(a => a.JournalEntryId).OnDelete(DeleteBehavior.Restrict);
            b.OwnsMany(a => a.Lines, l =>
            {
                l.ToTable("InventoryAdjustmentLines");
                l.WithOwner().HasForeignKey("InventoryAdjustmentId");
                l.HasKey("InventoryAdjustmentId", nameof(InventoryAdjustmentLine.LineNumber));
                l.Property(x => x.QuantityChange).HasColumnName("QuantityChangeTenThousandths").HasConversion(TenThousandths);
                l.Property(x => x.ValueChange).HasColumnName("ValueChangeMinor").HasConversion(MinorUnits);
                l.Property(x => x.Lots).HasMaxLength(1000);
                l.HasOne<Item>().WithMany().HasForeignKey(x => x.ItemId).OnDelete(DeleteBehavior.Restrict);
                l.HasOne<Warehouse>().WithMany().HasForeignKey(x => x.WarehouseId).OnDelete(DeleteBehavior.Restrict);
                l.HasOne<Bin>().WithMany().HasForeignKey(x => x.BinId).OnDelete(DeleteBehavior.Restrict);
            });
            b.Navigation(a => a.Lines).UsePropertyAccessMode(PropertyAccessMode.Field);
        });

        void SalesLines<TOwner>(OwnedNavigationBuilder<TOwner, SalesLine> l, string table, string ownerKey)
            where TOwner : class
        {
            l.ToTable(table);
            l.WithOwner().HasForeignKey(ownerKey);
            l.HasKey(ownerKey, nameof(SalesLine.LineNumber));
            l.Property(x => x.Kind).HasConversion<string>().HasMaxLength(20);
            l.Property(x => x.Description).HasMaxLength(1000);
            l.Property(x => x.Quantity).HasColumnName("QuantityTenThousandths").HasConversion(TenThousandths);
            l.Property(x => x.Rate).HasColumnName("RateTenThousandths").HasConversion(TenThousandths);
            l.Property(x => x.Amount).HasColumnName("AmountMinor").HasConversion(MinorUnits);
            l.Property(x => x.ListRate).HasColumnName("ListRateTenThousandths").HasConversion(NullableTenThousandths);
            l.Property(x => x.PriceSource).HasMaxLength(200);
            l.Property(x => x.Lots).HasMaxLength(1000);
            l.Property(x => x.Group).HasMaxLength(100);
            l.Ignore(x => x.Saved);
            l.HasOne<Item>().WithMany().HasForeignKey(x => x.ItemId).OnDelete(DeleteBehavior.Restrict);
            l.HasOne<Account>().WithMany().HasForeignKey(x => x.IncomeAccountId).OnDelete(DeleteBehavior.Restrict);
            l.HasOne<TaxRate>().WithMany().HasForeignKey(x => x.TaxRateId).OnDelete(DeleteBehavior.Restrict);
            l.HasOne<Segment>().WithMany().HasForeignKey(x => x.ClassId).OnDelete(DeleteBehavior.Restrict);
        }

        void TaxedDocument<TDocument>(EntityTypeBuilder<TDocument> b)
            where TDocument : class
        {
            b.HasOne<TaxCode>().WithMany().HasForeignKey("TaxCodeId").OnDelete(DeleteBehavior.Restrict);
            b.Property<string?>("TaxExemption").HasMaxLength(200);
            b.Ignore("TaxTotal");
            b.Ignore("Subtotal");
        }

        modelBuilder.Entity<Invoice>(b =>
        {
            b.ToTable("Invoices");
            TaxedDocument(b);
            b.HasKey(i => i.Id);
            b.Property(i => i.Number).HasMaxLength(20);
            b.HasIndex(i => i.Number).IsUnique();
            b.HasIndex(i => new { i.CustomerId, i.Date });
            b.Property(i => i.Memo).HasMaxLength(1000);
            b.Property(i => i.Total).HasColumnName("TotalMinor").HasConversion(MinorUnits);
            b.Property(i => i.AmountPaid).HasColumnName("AmountPaidMinor").HasConversion(MinorUnits);
            b.Property(i => i.Status).HasConversion<string>().HasMaxLength(20);
            b.Ignore(i => i.Balance);
            b.Ignore(i => i.IsOpen);
            b.Property(i => i.EarlyPaymentDiscountPercent).HasColumnName("EarlyPaymentDiscountPercentTenThousandths").HasConversion(TenThousandths);
            b.HasOne<Estimate>().WithMany().HasForeignKey(i => i.EstimateId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<RecurringInvoice>().WithMany().HasForeignKey(i => i.RecurringInvoiceId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<Invoice>().WithMany().HasForeignKey(i => i.LateFeeForInvoiceId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<SalesOrder>().WithMany().HasForeignKey(i => i.SalesOrderId).OnDelete(DeleteBehavior.Restrict);
            b.OwnsMany(i => i.SourceLines, l =>
            {
                l.ToTable("InvoiceSourceLines");
                l.WithOwner().HasForeignKey("InvoiceId");
                l.HasKey("InvoiceId", nameof(InvoiceSourceLine.LineNumber));
                l.Property(x => x.Quantity).HasColumnName("QuantityTenThousandths").HasConversion(TenThousandths);
            });
            b.Navigation(i => i.SourceLines).UsePropertyAccessMode(PropertyAccessMode.Field);
            b.HasIndex(i => i.JournalEntryId).IsUnique();
            b.HasOne<Customer>().WithMany().HasForeignKey(i => i.CustomerId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<Account>().WithMany().HasForeignKey(i => i.ReceivableAccountId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<JournalEntry>().WithMany().HasForeignKey(i => i.JournalEntryId).OnDelete(DeleteBehavior.Restrict);
            b.OwnsMany(i => i.Lines, l => SalesLines(l, "InvoiceLines", "InvoiceId"));
            b.Navigation(i => i.Lines).UsePropertyAccessMode(PropertyAccessMode.Field);
        });

        modelBuilder.Entity<SalesReceipt>(b =>
        {
            b.ToTable("SalesReceipts");
            TaxedDocument(b);
            b.HasKey(r => r.Id);
            b.Property(r => r.Number).HasMaxLength(20);
            b.HasIndex(r => r.Number).IsUnique();
            b.Property(r => r.Reference).HasMaxLength(30);
            b.Property(r => r.Memo).HasMaxLength(1000);
            b.Property(r => r.Total).HasColumnName("TotalMinor").HasConversion(MinorUnits);
            b.Property(r => r.Status).HasConversion<string>().HasMaxLength(20);
            b.HasIndex(r => r.JournalEntryId).IsUnique();
            b.HasOne<Customer>().WithMany().HasForeignKey(r => r.CustomerId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<Account>().WithMany().HasForeignKey(r => r.DepositAccountId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<BankDeposit>().WithMany().HasForeignKey(r => r.DepositId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<JournalEntry>().WithMany().HasForeignKey(r => r.JournalEntryId).OnDelete(DeleteBehavior.Restrict);
            b.OwnsMany(r => r.Lines, l => SalesLines(l, "SalesReceiptLines", "SalesReceiptId"));
            b.Navigation(r => r.Lines).UsePropertyAccessMode(PropertyAccessMode.Field);
        });

        modelBuilder.Entity<CustomerPayment>(b =>
        {
            b.ToTable("CustomerPayments");
            b.HasKey(p => p.Id);
            b.Property(p => p.Amount).HasColumnName("AmountMinor").HasConversion(MinorUnits);
            b.Property(p => p.Reference).HasMaxLength(30);
            b.Property(p => p.Memo).HasMaxLength(1000);
            b.Property(p => p.Status).HasConversion<string>().HasMaxLength(20);
            b.Property(p => p.AmountRefunded).HasColumnName("AmountRefundedMinor").HasConversion(MinorUnits);
            b.Ignore(p => p.AmountApplied);
            b.Ignore(p => p.DiscountTaken);
            b.Ignore(p => p.Unapplied);
            b.HasOne<BankDeposit>().WithMany().HasForeignKey(p => p.DepositId).OnDelete(DeleteBehavior.Restrict);
            b.HasIndex(p => new { p.CustomerId, p.Date });
            b.HasIndex(p => p.JournalEntryId).IsUnique();
            b.HasOne<Customer>().WithMany().HasForeignKey(p => p.CustomerId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<Account>().WithMany().HasForeignKey(p => p.DepositAccountId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<Account>().WithMany().HasForeignKey(p => p.ReceivableAccountId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<JournalEntry>().WithMany().HasForeignKey(p => p.JournalEntryId).OnDelete(DeleteBehavior.Restrict);
            b.OwnsMany(p => p.Applications, a =>
            {
                a.ToTable("PaymentApplications");
                a.WithOwner().HasForeignKey("CustomerPaymentId");
                a.HasKey("CustomerPaymentId", nameof(PaymentApplication.InvoiceId));
                a.Property(x => x.Amount).HasColumnName("AmountMinor").HasConversion(MinorUnits);
                a.Property(x => x.Discount).HasColumnName("DiscountMinor").HasConversion(MinorUnits);
                a.Ignore(x => x.Settled);
                a.HasIndex(x => x.InvoiceId);
                a.HasOne<Invoice>().WithMany().HasForeignKey(x => x.InvoiceId).OnDelete(DeleteBehavior.Restrict);
            });
            b.Navigation(p => p.Applications).UsePropertyAccessMode(PropertyAccessMode.Field);
        });

        modelBuilder.Entity<Estimate>(b =>
        {
            b.ToTable("Estimates");
            TaxedDocument(b);
            b.HasKey(e => e.Id);
            b.Property(e => e.Number).HasMaxLength(20);
            b.HasIndex(e => e.Number).IsUnique();
            b.HasIndex(e => new { e.CustomerId, e.Date });
            b.Property(e => e.Memo).HasMaxLength(1000);
            b.Property(e => e.Total).HasColumnName("TotalMinor").HasConversion(MinorUnits);
            b.Property(e => e.Status).HasConversion<string>().HasMaxLength(20);
            b.HasOne<Customer>().WithMany().HasForeignKey(e => e.CustomerId).OnDelete(DeleteBehavior.Restrict);
            // No FK to Invoices: invoices already reference their estimate, and the two would form a cycle.
            b.OwnsMany(e => e.Lines, l => SalesLines(l, "EstimateLines", "EstimateId"));
            b.Navigation(e => e.Lines).UsePropertyAccessMode(PropertyAccessMode.Field);
        });

        modelBuilder.Entity<CreditMemo>(b =>
        {
            b.ToTable("CreditMemos");
            TaxedDocument(b);
            b.HasKey(c => c.Id);
            b.Property(c => c.Number).HasMaxLength(20);
            b.HasIndex(c => c.Number).IsUnique();
            b.HasIndex(c => new { c.CustomerId, c.Date });
            b.Property(c => c.Memo).HasMaxLength(1000);
            b.Property(c => c.Total).HasColumnName("TotalMinor").HasConversion(MinorUnits);
            b.Property(c => c.AmountRefunded).HasColumnName("AmountRefundedMinor").HasConversion(MinorUnits);
            b.Property(c => c.Status).HasConversion<string>().HasMaxLength(20);
            b.Ignore(c => c.AmountApplied);
            b.Ignore(c => c.Balance);
            b.HasIndex(c => c.JournalEntryId).IsUnique();
            b.HasOne<Customer>().WithMany().HasForeignKey(c => c.CustomerId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<Account>().WithMany().HasForeignKey(c => c.ReceivableAccountId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<JournalEntry>().WithMany().HasForeignKey(c => c.JournalEntryId).OnDelete(DeleteBehavior.Restrict);
            b.OwnsMany(c => c.Lines, l => SalesLines(l, "CreditMemoLines", "CreditMemoId"));
            b.Navigation(c => c.Lines).UsePropertyAccessMode(PropertyAccessMode.Field);
            b.OwnsMany(c => c.Applications, a =>
            {
                a.ToTable("CreditApplications");
                a.WithOwner().HasForeignKey("CreditMemoId");
                a.HasKey("CreditMemoId", nameof(CreditApplication.InvoiceId));
                a.Property(x => x.Amount).HasColumnName("AmountMinor").HasConversion(MinorUnits);
                a.HasIndex(x => x.InvoiceId);
                a.HasOne<Invoice>().WithMany().HasForeignKey(x => x.InvoiceId).OnDelete(DeleteBehavior.Restrict);
            });
            b.Navigation(c => c.Applications).UsePropertyAccessMode(PropertyAccessMode.Field);
        });

        modelBuilder.Entity<CustomerRefund>(b =>
        {
            b.ToTable("CustomerRefunds");
            b.HasKey(r => r.Id);
            b.Property(r => r.Amount).HasColumnName("AmountMinor").HasConversion(MinorUnits);
            b.Property(r => r.Reference).HasMaxLength(30);
            b.Property(r => r.Memo).HasMaxLength(1000);
            b.Property(r => r.Status).HasConversion<string>().HasMaxLength(20);
            b.HasIndex(r => new { r.CustomerId, r.Date });
            b.HasIndex(r => r.JournalEntryId).IsUnique();
            b.HasOne<Customer>().WithMany().HasForeignKey(r => r.CustomerId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<Account>().WithMany().HasForeignKey(r => r.PaymentAccountId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<Account>().WithMany().HasForeignKey(r => r.ReceivableAccountId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<CreditMemo>().WithMany().HasForeignKey(r => r.CreditMemoId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<CustomerPayment>().WithMany().HasForeignKey(r => r.CustomerPaymentId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<JournalEntry>().WithMany().HasForeignKey(r => r.JournalEntryId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<BadDebtWriteOff>(b =>
        {
            b.ToTable("BadDebtWriteOffs");
            b.HasKey(w => w.Id);
            b.Property(w => w.Amount).HasColumnName("AmountMinor").HasConversion(MinorUnits);
            b.Property(w => w.Memo).HasMaxLength(1000);
            b.Property(w => w.Status).HasConversion<string>().HasMaxLength(20);
            b.HasIndex(w => new { w.CustomerId, w.Date });
            b.HasIndex(w => w.InvoiceId);
            b.HasIndex(w => w.JournalEntryId).IsUnique();
            b.HasOne<Customer>().WithMany().HasForeignKey(w => w.CustomerId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<Invoice>().WithMany().HasForeignKey(w => w.InvoiceId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<Account>().WithMany().HasForeignKey(w => w.ExpenseAccountId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<Account>().WithMany().HasForeignKey(w => w.ReceivableAccountId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<JournalEntry>().WithMany().HasForeignKey(w => w.JournalEntryId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<OnlinePaymentSettings>(b =>
        {
            b.ToTable("OnlinePaymentSettings");
            b.HasKey(s => s.Id);
            b.Property(s => s.Provider).HasConversion<string>().HasMaxLength(20);
            b.Property(s => s.ProtectedSecretKey).HasMaxLength(2000);
            b.Property(s => s.ProtectedWebhookSecret).HasMaxLength(2000);
            b.Property(s => s.PublicUrl).HasMaxLength(200);
            b.Ignore(s => s.IsReady);
            b.HasOne<Account>().WithMany().HasForeignKey(s => s.DepositAccountId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<PaymentLink>(b =>
        {
            b.ToTable("PaymentLinks");
            b.HasKey(l => l.Id);
            b.Property(l => l.Provider).HasConversion<string>().HasMaxLength(20);
            b.Property(l => l.SessionId).HasMaxLength(200);
            b.HasIndex(l => l.SessionId).IsUnique();
            b.Property(l => l.Url).HasMaxLength(2000);
            b.Property(l => l.Amount).HasColumnName("AmountMinor").HasConversion(MinorUnits);
            b.Property(l => l.Status).HasConversion<string>().HasMaxLength(20);
            b.Property(l => l.PaymentReference).HasMaxLength(200);
            b.HasIndex(l => l.InvoiceId);
            b.HasOne<Invoice>().WithMany().HasForeignKey(l => l.InvoiceId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<CustomerPayment>().WithMany().HasForeignKey(l => l.CustomerPaymentId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<PortalAccess>(b =>
        {
            b.ToTable("PortalAccess");
            b.HasKey(a => a.Id);
            b.Property(a => a.TokenHash).HasMaxLength(64);
            b.HasIndex(a => a.TokenHash).IsUnique();
            b.Property(a => a.Hint).HasMaxLength(10);
            b.Property(a => a.CreatedBy).HasMaxLength(200);
            b.Ignore(a => a.IsActive);
            b.HasIndex(a => a.CustomerId);
            b.HasOne<Customer>().WithMany().HasForeignKey(a => a.CustomerId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<ReceiptCapture>(b =>
        {
            b.ToTable("ReceiptCaptures");
            b.HasKey(r => r.Id);
            b.Property(r => r.FileName).HasMaxLength(255);
            b.Property(r => r.CreatedBy).HasMaxLength(200);
            b.Property(r => r.Status).HasConversion<string>().HasMaxLength(20);
            b.Property(r => r.Text).HasMaxLength(ReceiptCapture.MaxTextLength);
            b.Property(r => r.VendorName).HasMaxLength(200);
            b.Property(r => r.Total).HasColumnName("TotalMinor").HasConversion(NullableMinorUnits);
            b.Property(r => r.Tax).HasColumnName("TaxMinor").HasConversion(NullableMinorUnits);
            b.HasOne<Vendor>().WithMany().HasForeignKey(r => r.VendorId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<BankTransaction>().WithMany().HasForeignKey(r => r.BankTransactionId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<OpenBook.Domain.Documents.CapturedDocument>(b =>
        {
            b.ToTable("CapturedDocuments");
            b.HasKey(d => d.Id);
            b.HasIndex(d => d.Status);
            b.Property(d => d.Kind).HasConversion<string>().HasMaxLength(20);
            b.Property(d => d.Status).HasConversion<string>().HasMaxLength(20);
            b.Property(d => d.FileName).HasMaxLength(255);
            b.Property(d => d.CreatedBy).HasMaxLength(200);
            b.Property(d => d.Text).HasMaxLength(OpenBook.Domain.Documents.CapturedDocument.MaxTextLength);
            b.Property(d => d.ReadBy).HasMaxLength(20);
            b.Property(d => d.ReadNote).HasMaxLength(1000);
            b.Property(d => d.Party).HasMaxLength(200);
            b.Property(d => d.Number).HasMaxLength(50);
            b.Property(d => d.OrderReference).HasMaxLength(50);
            b.Property(d => d.Subtotal).HasColumnName("SubtotalMinor").HasConversion(NullableMinorUnits);
            b.Property(d => d.Tax).HasColumnName("TaxMinor").HasConversion(NullableMinorUnits);
            b.Property(d => d.Shipping).HasColumnName("ShippingMinor").HasConversion(NullableMinorUnits);
            b.Property(d => d.Total).HasColumnName("TotalMinor").HasConversion(NullableMinorUnits);
            b.Property(d => d.ResultType).HasMaxLength(50);
            b.HasOne<Vendor>().WithMany().HasForeignKey(d => d.VendorId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<Customer>().WithMany().HasForeignKey(d => d.CustomerId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<PurchaseOrder>().WithMany().HasForeignKey(d => d.PurchaseOrderId).OnDelete(DeleteBehavior.Restrict);
            b.OwnsMany(d => d.Lines, l =>
            {
                l.ToTable("CapturedDocumentLines");
                l.WithOwner().HasForeignKey("CapturedDocumentId");
                l.HasKey("CapturedDocumentId", nameof(OpenBook.Domain.Documents.CapturedDocumentLine.LineNumber));
                l.Property(x => x.LineNumber).ValueGeneratedNever();
                l.Property(x => x.Description).HasMaxLength(500);
                l.Property(x => x.Sku).HasMaxLength(50);
                l.Property(x => x.Quantity).HasColumnName("QuantityTenThousandths").HasConversion(NullableTenThousandths);
                l.Property(x => x.UnitPrice).HasColumnName("UnitPriceTenThousandths").HasConversion(NullableTenThousandths);
                l.Property(x => x.Amount).HasColumnName("AmountMinor").HasConversion(NullableMinorUnits);
                l.HasOne<Item>().WithMany().HasForeignKey(x => x.ItemId).OnDelete(DeleteBehavior.Restrict);
            });
            b.Navigation(d => d.Lines).UsePropertyAccessMode(PropertyAccessMode.Field);
        });

        modelBuilder.Entity<OpenBook.Domain.Documents.DocumentItemCode>(b =>
        {
            b.ToTable("DocumentItemCodes");
            b.HasKey(c => c.Id);
            b.Property(c => c.Code).HasMaxLength(50);
            b.HasIndex(c => new { c.PartyId, c.Code }).IsUnique();
            b.HasOne<Item>().WithMany().HasForeignKey(c => c.ItemId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<OpenBook.Domain.Documents.DocumentReadingSettings>(b =>
        {
            b.ToTable("DocumentReadingSettings");
            b.HasKey(s => s.Id);
            b.Property(s => s.ProtectedApiKey).HasMaxLength(2000);
            b.Property(s => s.Model).HasMaxLength(100);
            b.Ignore(s => s.IsReady);
        });

        modelBuilder.Entity<FormFont>(b =>
        {
            b.ToTable("FormFonts");
            b.HasKey(f => f.Id);
            b.Property(f => f.Key).HasMaxLength(FormDesign.MaxFontKeyLength);
            b.HasIndex(f => f.Key).IsUnique();
            b.Property(f => f.Family).HasMaxLength(FormFont.MaxFamilyLength);
            b.Property(f => f.Origin).HasConversion<string>().HasMaxLength(20);
        });

        modelBuilder.Entity<FormDesign>(b =>
        {
            b.ToTable("FormDesigns");
            b.HasKey(d => d.Id);
            b.Property(d => d.Name).HasMaxLength(FormDesign.MaxNameLength);
            b.HasIndex(d => d.Name).IsUnique();
            b.Property(d => d.Template).HasConversion<string>().HasMaxLength(20);
            b.Property(d => d.AccentColor).HasMaxLength(7);
            b.Property(d => d.Footer).HasMaxLength(1000);
            b.Property(d => d.FontKey).HasMaxLength(FormDesign.MaxFontKeyLength);
            b.Property(d => d.PaperSize).HasConversion<string>().HasMaxLength(10);
            b.Property(d => d.LogoContentType).HasMaxLength(20);
            b.Property(d => d.LogoPosition).HasConversion<string>().HasMaxLength(10);
            b.Property(d => d.LogoSize).HasConversion<string>().HasMaxLength(10);
            b.Property(d => d.DescriptionLabel).HasMaxLength(FormColumnLabels.MaxLength);
            b.Property(d => d.QuantityLabel).HasMaxLength(FormColumnLabels.MaxLength);
            b.Property(d => d.RateLabel).HasMaxLength(FormColumnLabels.MaxLength);
            b.Property(d => d.AmountLabel).HasMaxLength(FormColumnLabels.MaxLength);
            b.Ignore(d => d.ColumnLabels);
            b.OwnsMany(d => d.Texts, t =>
            {
                t.ToTable("FormDesignTexts");
                t.WithOwner().HasForeignKey("FormDesignId");
                t.Property(x => x.Document).HasConversion<string>().HasMaxLength(20);
                t.HasKey("FormDesignId", nameof(FormDesignText.Document));
                t.Property(x => x.Title).HasMaxLength(FormDesignText.MaxTitleLength);
                t.Property(x => x.Message).HasMaxLength(FormDesignText.MaxMessageLength);
            });
            b.Navigation(d => d.Texts).UsePropertyAccessMode(PropertyAccessMode.Field);
        });

        modelBuilder.Entity<OpenBook.Domain.CustomFields.CustomField>(b =>
        {
            b.ToTable("CustomFields");
            b.HasKey(f => f.Id);
            b.Property(f => f.Target).HasConversion<string>().HasMaxLength(20);
            b.Property(f => f.Type).HasConversion<string>().HasMaxLength(20);
            b.Property(f => f.Placement).HasConversion<string>().HasMaxLength(20);
            b.Property(f => f.Name).HasMaxLength(OpenBook.Domain.CustomFields.CustomField.MaxNameLength);
            b.Property(f => f.HelpText).HasMaxLength(OpenBook.Domain.CustomFields.CustomField.MaxHelpLength);
            b.Property(f => f.Options).HasMaxLength(4000);
            b.Property(f => f.DefaultValue).HasMaxLength(OpenBook.Domain.CustomFields.CustomField.MaxValueLength);
            b.Property(f => f.Minimum).HasColumnName("MinimumTenThousandths").HasConversion(NullableTenThousandths);
            b.Property(f => f.Maximum).HasColumnName("MaximumTenThousandths").HasConversion(NullableTenThousandths);
            b.Property(f => f.PresetKey).HasMaxLength(50);
            b.Ignore(f => f.OptionList);
            b.Ignore(f => f.Limit);
        });

        modelBuilder.Entity<OpenBook.Domain.CustomFields.CustomFieldValue>(b =>
        {
            b.ToTable("CustomFieldValues");
            b.HasKey(v => v.Id);
            b.Property(v => v.EntityType).HasMaxLength(40);
            b.Property(v => v.Value).HasMaxLength(OpenBook.Domain.CustomFields.CustomField.MaxValueLength);
            b.HasIndex(v => new { v.EntityId, v.FieldId, v.LineNumber }).IsUnique();
            b.HasIndex(v => new { v.EntityType, v.LineNumber });
            b.HasIndex(v => v.FieldId);
            b.HasOne<OpenBook.Domain.CustomFields.CustomField>().WithMany().HasForeignKey(v => v.FieldId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<PurchaseOrder>(b =>
        {
            b.ToTable("PurchaseOrders");
            b.HasKey(o => o.Id);
            b.Property(o => o.Number).HasMaxLength(20);
            b.HasIndex(o => o.Number).IsUnique();
            b.HasIndex(o => new { o.VendorId, o.Date });
            b.Property(o => o.Memo).HasMaxLength(1000);
            b.Property(o => o.Total).HasColumnName("TotalMinor").HasConversion(MinorUnits);
            b.Property(o => o.Status).HasConversion<string>().HasMaxLength(20);
            b.Ignore(o => o.Received);
            b.HasOne<Vendor>().WithMany().HasForeignKey(o => o.VendorId).OnDelete(DeleteBehavior.Restrict);
            b.OwnsMany(o => o.Lines, l =>
            {
                l.ToTable("PurchaseOrderLines");
                l.WithOwner().HasForeignKey("PurchaseOrderId");
                l.HasKey("PurchaseOrderId", nameof(PurchaseOrderLine.LineNumber));
                l.Property(x => x.Description).HasMaxLength(1000);
                l.Property(x => x.Quantity).HasColumnName("QuantityTenThousandths").HasConversion(TenThousandths);
                l.Property(x => x.UnitCost).HasColumnName("UnitCostTenThousandths").HasConversion(TenThousandths);
                l.Property(x => x.Amount).HasColumnName("AmountMinor").HasConversion(MinorUnits);
                l.Ignore(x => x.IsItem);
                l.HasOne<Item>().WithMany().HasForeignKey(x => x.ItemId).OnDelete(DeleteBehavior.Restrict);
                l.HasOne<Account>().WithMany().HasForeignKey(x => x.AccountId).OnDelete(DeleteBehavior.Restrict);
            });
            b.Navigation(o => o.Lines).UsePropertyAccessMode(PropertyAccessMode.Field);
            b.OwnsMany(o => o.Receipts, r =>
            {
                r.ToTable("PurchaseOrderReceipts");
                r.WithOwner().HasForeignKey("PurchaseOrderId");
                r.HasKey(x => x.Id);
                r.Property(x => x.Id).ValueGeneratedNever();
                r.Property(x => x.Quantity).HasColumnName("QuantityTenThousandths").HasConversion(TenThousandths);
                r.Property(x => x.Note).HasMaxLength(500);
                r.Property(x => x.Value).HasColumnName("ValueMinor").HasConversion(MinorUnits);
                r.HasOne<JournalEntry>().WithMany().HasForeignKey(x => x.JournalEntryId).OnDelete(DeleteBehavior.Restrict);
            });
            b.Navigation(o => o.Receipts).UsePropertyAccessMode(PropertyAccessMode.Field);
        });

        modelBuilder.Entity<PurchasingSettings>(b =>
        {
            b.ToTable("PurchasingSettings");
            b.HasKey(s => s.Id);
            b.Property(s => s.ApprovalThreshold).HasColumnName("ApprovalThresholdMinor").HasConversion(NullableMinorUnits);
            b.Property(s => s.PriceTolerancePercent).HasColumnName("PriceTolerancePercentTenThousandths").HasConversion(TenThousandths);
        });

        modelBuilder.Entity<VendorBankAccount>(b =>
        {
            b.ToTable("VendorBankAccounts");
            b.HasKey(a => a.VendorId);
            b.Property(a => a.RoutingNumber).HasMaxLength(9);
            b.Property(a => a.ProtectedAccountNumber).HasMaxLength(2000);
            b.Property(a => a.AccountLast4).HasMaxLength(4);
            b.Property(a => a.Kind).HasConversion<string>().HasMaxLength(20);
            b.Ignore(a => a.Masked);
            b.HasOne<Vendor>().WithMany().HasForeignKey(a => a.VendorId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<AchSettings>(b =>
        {
            b.ToTable("AchSettings");
            b.HasKey(a => a.AccountId);
            b.Property(a => a.DestinationRouting).HasMaxLength(9);
            b.Property(a => a.DestinationName).HasMaxLength(23);
            b.Property(a => a.OriginId).HasMaxLength(10);
            b.Property(a => a.OriginName).HasMaxLength(23);
            b.Property(a => a.CompanyId).HasMaxLength(10);
            b.Property(a => a.CompanyName).HasMaxLength(16);
            b.Property(a => a.EntryDescription).HasMaxLength(10);
            b.HasOne<Account>().WithMany().HasForeignKey(a => a.AccountId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<AchBatch>(b =>
        {
            b.ToTable("AchBatches");
            b.HasKey(a => a.Id);
            b.HasIndex(a => a.AccountId);
            b.Property(a => a.Total).HasColumnName("TotalMinor").HasConversion(MinorUnits);
            b.Property(a => a.FileIdModifier).HasConversion(c => c.ToString(), s => s[0]).HasMaxLength(1);
            b.HasOne<Account>().WithMany().HasForeignKey(a => a.AccountId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<SalesOrder>(b =>
        {
            b.ToTable("SalesOrders");
            TaxedDocument(b);
            b.HasKey(o => o.Id);
            b.Property(o => o.Number).HasMaxLength(20);
            b.HasIndex(o => o.Number).IsUnique();
            b.HasIndex(o => new { o.CustomerId, o.Date });
            b.Property(o => o.CustomerReference).HasMaxLength(30);
            b.Property(o => o.Memo).HasMaxLength(1000);
            b.Property(o => o.Total).HasColumnName("TotalMinor").HasConversion(MinorUnits);
            b.Property(o => o.Status).HasConversion<string>().HasMaxLength(20);
            b.HasOne<Customer>().WithMany().HasForeignKey(o => o.CustomerId).OnDelete(DeleteBehavior.Restrict);
            b.OwnsMany(o => o.Lines, l => SalesLines(l, "SalesOrderLines", "SalesOrderId"));
            b.Navigation(o => o.Lines).UsePropertyAccessMode(PropertyAccessMode.Field);
        });

        modelBuilder.Entity<Retainer>(b =>
        {
            b.ToTable("Retainers");
            b.HasKey(r => r.Id);
            b.Property(r => r.Amount).HasColumnName("AmountMinor").HasConversion(MinorUnits);
            b.Property(r => r.Reference).HasMaxLength(30);
            b.Property(r => r.Memo).HasMaxLength(1000);
            b.Property(r => r.Status).HasConversion<string>().HasMaxLength(20);
            b.Ignore(r => r.AmountApplied);
            b.Ignore(r => r.AmountRefunded);
            b.Ignore(r => r.Balance);
            b.HasIndex(r => new { r.CustomerId, r.Date });
            b.HasIndex(r => r.JournalEntryId).IsUnique();
            b.HasOne<Customer>().WithMany().HasForeignKey(r => r.CustomerId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<Account>().WithMany().HasForeignKey(r => r.DepositAccountId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<Account>().WithMany().HasForeignKey(r => r.LiabilityAccountId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<JournalEntry>().WithMany().HasForeignKey(r => r.JournalEntryId).OnDelete(DeleteBehavior.Restrict);
            b.OwnsMany(r => r.Applications, a =>
            {
                a.ToTable("RetainerApplications");
                a.WithOwner().HasForeignKey("RetainerId");
                a.HasKey(x => x.Id);
                a.Property(x => x.Id).ValueGeneratedNever();
                a.Property(x => x.Amount).HasColumnName("AmountMinor").HasConversion(MinorUnits);
                a.HasIndex(x => x.InvoiceId);
                a.HasIndex(x => x.JournalEntryId).IsUnique();
                a.HasOne<Invoice>().WithMany().HasForeignKey(x => x.InvoiceId).OnDelete(DeleteBehavior.Restrict);
                a.HasOne<JournalEntry>().WithMany().HasForeignKey(x => x.JournalEntryId).OnDelete(DeleteBehavior.Restrict);
            });
            b.Navigation(r => r.Applications).UsePropertyAccessMode(PropertyAccessMode.Field);
            b.OwnsMany(r => r.Refunds, f =>
            {
                f.ToTable("RetainerRefunds");
                f.WithOwner().HasForeignKey("RetainerId");
                f.HasKey(x => x.Id);
                f.Property(x => x.Id).ValueGeneratedNever();
                f.Property(x => x.Amount).HasColumnName("AmountMinor").HasConversion(MinorUnits);
                f.Property(x => x.Reference).HasMaxLength(30);
                f.HasIndex(x => x.JournalEntryId).IsUnique();
                f.HasOne<Account>().WithMany().HasForeignKey(x => x.PaymentAccountId).OnDelete(DeleteBehavior.Restrict);
                f.HasOne<JournalEntry>().WithMany().HasForeignKey(x => x.JournalEntryId).OnDelete(DeleteBehavior.Restrict);
            });
            b.Navigation(r => r.Refunds).UsePropertyAccessMode(PropertyAccessMode.Field);
        });

        modelBuilder.Entity<BillableExpense>(b =>
        {
            b.ToTable("BillableExpenses");
            b.HasKey(x => x.Id);
            b.Property(x => x.Kind).HasConversion<string>().HasMaxLength(20);
            b.HasIndex(x => new { x.Kind, x.DocumentId, x.LineNumber }).IsUnique();
            b.HasIndex(x => x.CustomerId);
            b.HasIndex(x => x.InvoiceId);
            b.Property(x => x.Description).HasMaxLength(1000);
            b.Property(x => x.Cost).HasColumnName("CostMinor").HasConversion(MinorUnits);
            b.Property(x => x.MarkupPercent).HasColumnName("MarkupPercentTenThousandths").HasConversion(TenThousandths);
            b.Ignore(x => x.Amount);
            b.HasOne<Customer>().WithMany().HasForeignKey(x => x.CustomerId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<Invoice>().WithMany().HasForeignKey(x => x.InvoiceId).OnDelete(DeleteBehavior.Restrict);
        });

        ConfigureProjects(modelBuilder);
        ConfigureAssets(modelBuilder);

        modelBuilder.Entity<ReportSubscription>(b =>
        {
            b.ToTable("ReportSubscriptions");
            b.HasKey(s => s.Id);
            b.Property(s => s.Recipients).HasMaxLength(4000);
            b.Property(s => s.Frequency).HasConversion<string>().HasMaxLength(20);
            b.Property(s => s.Format).HasConversion<string>().HasMaxLength(20);
            b.Property(s => s.LastError).HasMaxLength(500);
            b.Property(s => s.CreatedBy).HasMaxLength(100);
            b.Ignore(s => s.RecipientList);
            b.HasOne<SavedReport>().WithMany().HasForeignKey(s => s.SavedReportId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ForecastScenario>(b =>
        {
            b.ToTable("ForecastScenarios");
            b.HasKey(s => s.Id);
            b.Property(s => s.Name).HasMaxLength(100);
            b.Property(s => s.Notes).HasMaxLength(1000);
            b.OwnsMany(s => s.Items, i =>
            {
                i.ToTable("ForecastScenarioItems");
                i.WithOwner().HasForeignKey("ForecastScenarioId");
                i.HasKey(x => x.Id);
                i.Property(x => x.Id).ValueGeneratedNever();
                i.Property(x => x.Description).HasMaxLength(200);
                i.Property(x => x.Amount).HasColumnName("AmountMinor").HasConversion(MinorUnits);
                i.Property(x => x.Recurrence).HasConversion<string>().HasMaxLength(20);
            });
            b.Navigation(s => s.Items).UsePropertyAccessMode(PropertyAccessMode.Field);
        });

        modelBuilder.Entity<OpenBook.Domain.Capture.CaptureDevice>(b =>
        {
            b.ToTable("CaptureDevices");
            b.HasKey(d => d.Id);
            b.HasIndex(d => d.UserId);
            b.Property(d => d.Name).HasMaxLength(100);
            b.Ignore(d => d.IsActive);
            b.HasOne<User>().WithMany().HasForeignKey(d => d.UserId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<OpenBook.Domain.Capture.CaptureDraft>(b =>
        {
            b.ToTable("CaptureDrafts");
            b.HasKey(d => d.Id);
            b.Property(d => d.Id).ValueGeneratedNever();
            b.HasIndex(d => new { d.DeviceId, d.Status });
            b.Property(d => d.Kind).HasConversion<string>().HasMaxLength(20);
            b.Property(d => d.Status).HasConversion<string>().HasMaxLength(20);
            b.Property(d => d.Error).HasMaxLength(500);
            b.Property(d => d.ResultType).HasMaxLength(50);
            b.HasOne<OpenBook.Domain.Capture.CaptureDevice>().WithMany().HasForeignKey(d => d.DeviceId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<AdjustingEntry>(b =>
        {
            b.ToTable("AdjustingEntries");
            b.HasKey(a => a.JournalEntryId);
            b.Property(a => a.MarkedBy).HasMaxLength(100);
            b.HasOne<JournalEntry>().WithMany().HasForeignKey(a => a.JournalEntryId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<DashboardPreference>(b =>
        {
            b.ToTable("DashboardPreferences");
            b.HasKey(p => p.Username);
            b.Property(p => p.Username).HasMaxLength(100);
            b.Property(p => p.HiddenPanels).HasMaxLength(1000);
            b.Ignore(p => p.Hidden);
        });

        modelBuilder.Entity<CollectionSettings>(b =>
        {
            b.ToTable("CollectionSettings");
            b.HasKey(c => c.Id);
            b.Property(c => c.LateFeePercent).HasColumnName("LateFeePercentTenThousandths").HasConversion(TenThousandths);
            b.Property(c => c.LateFeeAmount).HasColumnName("LateFeeAmountMinor").HasConversion(MinorUnits);
            b.HasOne<Item>().WithMany().HasForeignKey(c => c.LateFeeItemId).OnDelete(DeleteBehavior.Restrict);
            b.Property(c => c.ReminderSchedule).HasMaxLength(100);
            b.Property(c => c.ReminderSubject).HasMaxLength(300);
            b.Property(c => c.ReminderBody).HasMaxLength(4000);
            b.Ignore(c => c.ReminderSteps);
        });

        modelBuilder.Entity<InvoiceReminder>(b =>
        {
            b.ToTable("InvoiceReminders");
            b.HasKey(r => r.Id);
            b.HasIndex(r => new { r.InvoiceId, r.Step }).IsUnique();
            b.Property(r => r.To).HasMaxLength(320);
            b.HasOne<Invoice>().WithMany().HasForeignKey(r => r.InvoiceId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<ScheduledEmail>(b =>
        {
            b.ToTable("ScheduledEmails");
            b.HasKey(e => e.Id);
            b.HasIndex(e => e.InvoiceId);
            b.HasIndex(e => e.Status);
            b.Property(e => e.Status).HasConversion<string>().HasMaxLength(20);
            b.Property(e => e.To).HasMaxLength(320);
            b.Property(e => e.Subject).HasMaxLength(ScheduledEmail.MaxSubject);
            b.Property(e => e.Body).HasMaxLength(ScheduledEmail.MaxBody);
            b.Property(e => e.ScheduledBy).HasMaxLength(200);
            b.Property(e => e.Error).HasMaxLength(1000);
            b.Ignore(e => e.IsPending);
            b.HasOne<Invoice>().WithMany().HasForeignKey(e => e.InvoiceId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<RecurringInvoice>(b =>
        {
            b.ToTable("RecurringInvoices");
            b.Property(r => r.Tags).HasMaxLength(800);
            b.Ignore(r => r.TagIds);
            b.HasOne<Segment>().WithMany().HasForeignKey(r => r.LocationId).OnDelete(DeleteBehavior.Restrict);
            b.HasKey(r => r.Id);
            b.Property(r => r.Name).HasMaxLength(100);
            b.Property(r => r.Memo).HasMaxLength(1000);
            b.Property(r => r.Frequency).HasConversion<string>().HasMaxLength(20);
            b.Ignore(r => r.NextDate);
            b.HasOne<Customer>().WithMany().HasForeignKey(r => r.CustomerId).OnDelete(DeleteBehavior.Restrict);
            b.OwnsMany(r => r.Lines, l =>
            {
                l.ToTable("RecurringInvoiceLines");
                l.HasOne<Segment>().WithMany().HasForeignKey(x => x.ClassId).OnDelete(DeleteBehavior.Restrict);
                l.WithOwner().HasForeignKey("RecurringInvoiceId");
                l.HasKey("RecurringInvoiceId", nameof(RecurringInvoiceLine.LineNumber));
                l.Property(x => x.Kind).HasConversion<string>().HasMaxLength(20);
                l.Property(x => x.Description).HasMaxLength(1000);
                l.Property(x => x.Quantity).HasColumnName("QuantityTenThousandths").HasConversion(TenThousandths);
                l.Property(x => x.Rate).HasColumnName("RateTenThousandths").HasConversion(TenThousandths);
                l.HasOne<Item>().WithMany().HasForeignKey(x => x.ItemId).OnDelete(DeleteBehavior.Restrict);
            });
            b.Navigation(r => r.Lines).UsePropertyAccessMode(PropertyAccessMode.Field);
        });

        modelBuilder.Entity<BankDeposit>(b =>
        {
            b.ToTable("BankDeposits");
            b.HasKey(d => d.Id);
            b.Property(d => d.Memo).HasMaxLength(1000);
            b.Property(d => d.Total).HasColumnName("TotalMinor").HasConversion(MinorUnits);
            b.Property(d => d.Status).HasConversion<string>().HasMaxLength(20);
            b.HasIndex(d => d.JournalEntryId).IsUnique();
            b.HasOne<Account>().WithMany().HasForeignKey(d => d.BankAccountId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<JournalEntry>().WithMany().HasForeignKey(d => d.JournalEntryId).OnDelete(DeleteBehavior.Restrict);
            b.OwnsMany(d => d.Lines, l =>
            {
                l.ToTable("BankDepositLines");
                l.WithOwner().HasForeignKey("BankDepositId");
                l.HasKey("BankDepositId", nameof(DepositLine.LineNumber));
                l.Property(x => x.Kind).HasConversion<string>().HasMaxLength(20);
                l.Property(x => x.Amount).HasColumnName("AmountMinor").HasConversion(MinorUnits);
                l.HasIndex(x => x.SourceId);
                l.HasOne<Account>().WithMany().HasForeignKey(x => x.FromAccountId).OnDelete(DeleteBehavior.Restrict);
            });
            b.Navigation(d => d.Lines).UsePropertyAccessMode(PropertyAccessMode.Field);
        });
    }

    private static void ConfigurePurchases(ModelBuilder modelBuilder)
    {
        void BillLines<TOwner>(OwnedNavigationBuilder<TOwner, BillLine> l, string table, string ownerKey)
            where TOwner : class
        {
            l.ToTable(table);
            l.WithOwner().HasForeignKey(ownerKey);
            l.HasKey(ownerKey, nameof(BillLine.LineNumber));
            l.Property(x => x.Description).HasMaxLength(1000);
            l.Property(x => x.Amount).HasColumnName("AmountMinor").HasConversion(MinorUnits);
            l.Property(x => x.Quantity).HasColumnName("QuantityTenThousandths").HasConversion(NullableTenThousandths);
            l.Property(x => x.Lots).HasMaxLength(1000);
            l.HasOne<Account>().WithMany().HasForeignKey(x => x.AccountId).OnDelete(DeleteBehavior.Restrict);
            l.HasOne<Item>().WithMany().HasForeignKey(x => x.ItemId).OnDelete(DeleteBehavior.Restrict);
            l.HasOne<Segment>().WithMany().HasForeignKey(x => x.ClassId).OnDelete(DeleteBehavior.Restrict);
        }

        modelBuilder.Entity<Vendor>(b =>
        {
            b.ToTable("Vendors");
            b.HasKey(v => v.Id);
            b.Property(v => v.DisplayName).HasMaxLength(200);
            b.HasIndex(v => v.DisplayName).IsUnique();
            b.Property(v => v.CompanyName).HasMaxLength(200);
            b.Property(v => v.Email).HasMaxLength(320);
            b.Property(v => v.Phone).HasMaxLength(50);
            b.Property(v => v.Address).HasMaxLength(500);
            b.Property(v => v.AccountNumber).HasMaxLength(50);
            b.Property(v => v.Notes).HasMaxLength(2000);
            b.Property(v => v.TaxId).HasMaxLength(20);
            b.Property(v => v.Form1099Box).HasConversion<string>().HasMaxLength(40);
            b.Ignore(v => v.MaskedTaxId);
            b.HasOne<Account>().WithMany().HasForeignKey(v => v.DefaultExpenseAccountId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<Bill>(b =>
        {
            b.ToTable("Bills");
            b.HasKey(x => x.Id);
            b.Property(x => x.Reference).HasMaxLength(30);
            b.HasIndex(x => new { x.VendorId, x.Date });
            b.Property(x => x.Memo).HasMaxLength(1000);
            b.Property(x => x.Total).HasColumnName("TotalMinor").HasConversion(MinorUnits);
            b.Property(x => x.AmountPaid).HasColumnName("AmountPaidMinor").HasConversion(MinorUnits);
            b.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
            b.Ignore(x => x.Balance);
            b.Ignore(x => x.IsOpen);
            b.HasIndex(x => x.JournalEntryId).IsUnique();
            b.HasOne<Vendor>().WithMany().HasForeignKey(x => x.VendorId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<Account>().WithMany().HasForeignKey(x => x.PayableAccountId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<JournalEntry>().WithMany().HasForeignKey(x => x.JournalEntryId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<RecurringBill>().WithMany().HasForeignKey(x => x.RecurringBillId).OnDelete(DeleteBehavior.Restrict);
            b.OwnsMany(x => x.Lines, l => BillLines(l, "BillLines", "BillId"));
            b.Navigation(x => x.Lines).UsePropertyAccessMode(PropertyAccessMode.Field);
            b.Property(x => x.Approval).HasConversion<string>().HasMaxLength(20);
            b.Property(x => x.HoldReason).HasMaxLength(2000);
            b.Property(x => x.EnteredBy).HasMaxLength(200);
            b.Property(x => x.ApprovedBy).HasMaxLength(200);
            b.Property(x => x.ApprovalNote).HasMaxLength(500);
            b.Ignore(x => x.CanBePaid);
            b.HasOne<PurchaseOrder>().WithMany().HasForeignKey(x => x.PurchaseOrderId).OnDelete(DeleteBehavior.Restrict);
            b.OwnsMany(x => x.SourceLines, l =>
            {
                l.ToTable("BillSourceLines");
                l.WithOwner().HasForeignKey("BillId");
                l.HasKey("BillId", nameof(BillSourceLine.LineNumber));
                l.Property(x => x.Quantity).HasColumnName("QuantityTenThousandths").HasConversion(TenThousandths);
                l.Property(x => x.Amount).HasColumnName("AmountMinor").HasConversion(MinorUnits);
                l.Property(x => x.AccruedQuantity).HasColumnName("AccruedQuantityTenThousandths").HasConversion(TenThousandths);
                l.Property(x => x.AccruedAmount).HasColumnName("AccruedAmountMinor").HasConversion(MinorUnits);
            });
            b.Navigation(x => x.SourceLines).UsePropertyAccessMode(PropertyAccessMode.Field);
        });

        modelBuilder.Entity<VendorCredit>(b =>
        {
            b.ToTable("VendorCredits");
            b.HasKey(x => x.Id);
            b.Property(x => x.Reference).HasMaxLength(30);
            b.HasIndex(x => new { x.VendorId, x.Date });
            b.Property(x => x.Memo).HasMaxLength(1000);
            b.Property(x => x.Total).HasColumnName("TotalMinor").HasConversion(MinorUnits);
            b.Property(x => x.AmountRefunded).HasColumnName("AmountRefundedMinor").HasConversion(MinorUnits);
            b.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
            b.Ignore(x => x.AmountApplied);
            b.Ignore(x => x.Balance);
            b.HasIndex(x => x.JournalEntryId).IsUnique();
            b.HasOne<Vendor>().WithMany().HasForeignKey(x => x.VendorId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<Account>().WithMany().HasForeignKey(x => x.PayableAccountId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<JournalEntry>().WithMany().HasForeignKey(x => x.JournalEntryId).OnDelete(DeleteBehavior.Restrict);
            b.OwnsMany(x => x.Lines, l => BillLines(l, "VendorCreditLines", "VendorCreditId"));
            b.Navigation(x => x.Lines).UsePropertyAccessMode(PropertyAccessMode.Field);
            b.OwnsMany(x => x.Applications, a =>
            {
                a.ToTable("VendorCreditApplications");
                a.WithOwner().HasForeignKey("VendorCreditId");
                a.HasKey("VendorCreditId", nameof(VendorCreditApplication.BillId));
                a.Property(x => x.Amount).HasColumnName("AmountMinor").HasConversion(MinorUnits);
                a.HasIndex(x => x.BillId);
                a.HasOne<Bill>().WithMany().HasForeignKey(x => x.BillId).OnDelete(DeleteBehavior.Restrict);
            });
            b.Navigation(x => x.Applications).UsePropertyAccessMode(PropertyAccessMode.Field);
        });

        modelBuilder.Entity<VendorRefund>(b =>
        {
            b.ToTable("VendorRefunds");
            b.HasKey(x => x.Id);
            b.Property(x => x.Amount).HasColumnName("AmountMinor").HasConversion(MinorUnits);
            b.Property(x => x.Reference).HasMaxLength(30);
            b.Property(x => x.Memo).HasMaxLength(1000);
            b.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
            b.HasIndex(x => new { x.VendorId, x.Date });
            b.HasIndex(x => x.JournalEntryId).IsUnique();
            b.HasOne<Vendor>().WithMany().HasForeignKey(x => x.VendorId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<VendorCredit>().WithMany().HasForeignKey(x => x.VendorCreditId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<Account>().WithMany().HasForeignKey(x => x.DepositAccountId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<Account>().WithMany().HasForeignKey(x => x.PayableAccountId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<JournalEntry>().WithMany().HasForeignKey(x => x.JournalEntryId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<RecurringBill>(b =>
        {
            b.ToTable("RecurringBills");
            b.Property(x => x.Tags).HasMaxLength(800);
            b.Ignore(x => x.TagIds);
            b.HasOne<Segment>().WithMany().HasForeignKey(x => x.LocationId).OnDelete(DeleteBehavior.Restrict);
            b.HasKey(x => x.Id);
            b.Property(x => x.Name).HasMaxLength(100);
            b.Property(x => x.Memo).HasMaxLength(1000);
            b.Property(x => x.Frequency).HasConversion<string>().HasMaxLength(20);
            b.Ignore(x => x.NextDate);
            b.HasOne<Vendor>().WithMany().HasForeignKey(x => x.VendorId).OnDelete(DeleteBehavior.Restrict);
            b.OwnsMany(x => x.Lines, l =>
            {
                l.ToTable("RecurringBillLines");
                l.HasOne<Segment>().WithMany().HasForeignKey(x => x.ClassId).OnDelete(DeleteBehavior.Restrict);
                l.WithOwner().HasForeignKey("RecurringBillId");
                l.HasKey("RecurringBillId", nameof(RecurringBillLine.LineNumber));
                l.Property(x => x.Description).HasMaxLength(1000);
                l.Property(x => x.Amount).HasColumnName("AmountMinor").HasConversion(MinorUnits);
                l.HasOne<Account>().WithMany().HasForeignKey(x => x.AccountId).OnDelete(DeleteBehavior.Restrict);
            });
            b.Navigation(x => x.Lines).UsePropertyAccessMode(PropertyAccessMode.Field);
        });

        modelBuilder.Entity<Attachment>(b =>
        {
            b.ToTable("Attachments");
            b.HasKey(x => x.Id);
            b.Property(x => x.EntityType).HasMaxLength(30);
            b.HasIndex(x => new { x.EntityType, x.EntityId });
            b.Property(x => x.FileName).HasMaxLength(255);
            b.Property(x => x.ContentType).HasMaxLength(100);
            b.Property(x => x.AddedBy).HasMaxLength(100);
            b.Ignore(x => x.ShowsInline);
        });

        modelBuilder.Entity<AttachmentContent>(b =>
        {
            b.ToTable("AttachmentContents");
            b.HasKey(x => x.AttachmentId);
            b.HasOne<Attachment>().WithOne().HasForeignKey<AttachmentContent>(x => x.AttachmentId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<BillPayment>(b =>
        {
            b.ToTable("BillPayments");
            b.HasKey(p => p.Id);
            b.HasOne<AchBatch>().WithMany().HasForeignKey(p => p.AchBatchId).OnDelete(DeleteBehavior.Restrict);
            b.Property(p => p.Amount).HasColumnName("AmountMinor").HasConversion(MinorUnits);
            b.Property(p => p.Reference).HasMaxLength(30);
            b.Property(p => p.Memo).HasMaxLength(1000);
            b.Property(p => p.Status).HasConversion<string>().HasMaxLength(20);
            b.HasIndex(p => new { p.VendorId, p.Date });
            b.HasIndex(p => new { p.PaymentAccountId, p.Reference });
            b.HasIndex(p => p.JournalEntryId).IsUnique();
            b.HasOne<Vendor>().WithMany().HasForeignKey(p => p.VendorId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<Account>().WithMany().HasForeignKey(p => p.PaymentAccountId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<Account>().WithMany().HasForeignKey(p => p.PayableAccountId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<JournalEntry>().WithMany().HasForeignKey(p => p.JournalEntryId).OnDelete(DeleteBehavior.Restrict);
            b.OwnsMany(p => p.Applications, a =>
            {
                a.ToTable("BillPaymentApplications");
                a.WithOwner().HasForeignKey("BillPaymentId");
                a.HasKey("BillPaymentId", nameof(BillPaymentApplication.BillId));
                a.Property(x => x.Amount).HasColumnName("AmountMinor").HasConversion(MinorUnits);
                a.HasIndex(x => x.BillId);
                a.HasOne<Bill>().WithMany().HasForeignKey(x => x.BillId).OnDelete(DeleteBehavior.Restrict);
            });
            b.Navigation(p => p.Applications).UsePropertyAccessMode(PropertyAccessMode.Field);
        });
    }
}
