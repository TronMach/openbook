using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using OpenBook.Domain.Accounts;
using OpenBook.Domain.Auditing;
using OpenBook.Domain.Banking;
using OpenBook.Domain.Common;
using OpenBook.Domain.Companies;
using OpenBook.Domain.Ledger;
using OpenBook.Domain.Purchases;
using OpenBook.Domain.Sales;

namespace OpenBook.Infrastructure.Persistence;

/// <summary>
/// New journal entries are inserted unsealed, then sealed in the same transaction. The seal is what
/// fires the database's balance check (see <see cref="SqliteLedgerTriggers"/>): SQLite has no deferred
/// constraints, so this is how the DB verifies a whole entry rather than one line at a time.
/// </summary>
public sealed class OpenBookDbContext(DbContextOptions<OpenBookDbContext> options) : DbContext(options)
{
    internal const string IsSealed = nameof(IsSealed);

    public DbSet<Account> Accounts => Set<Account>();
    public DbSet<JournalEntry> JournalEntries => Set<JournalEntry>();
    public DbSet<AuditRecord> AuditRecords => Set<AuditRecord>();
    public DbSet<Company> Companies => Set<Company>();
    public DbSet<User> Users => Set<User>();
    public DbSet<BankTransaction> BankTransactions => Set<BankTransaction>();
    public DbSet<Reconciliation> Reconciliations => Set<Reconciliation>();

    public DbSet<Customer> Customers => Set<Customer>();
    public DbSet<Item> Items => Set<Item>();
    public DbSet<Invoice> Invoices => Set<Invoice>();
    public DbSet<CustomerPayment> CustomerPayments => Set<CustomerPayment>();
    public DbSet<SalesReceipt> SalesReceipts => Set<SalesReceipt>();
    public DbSet<Vendor> Vendors => Set<Vendor>();
    public DbSet<Bill> Bills => Set<Bill>();
    public DbSet<BillPayment> BillPayments => Set<BillPayment>();

    /// <summary>Money columns are integer minor units, so SQL sums are exact on every provider.</summary>
    private static readonly ValueConverter<decimal, long> MinorUnits = new(amount => (long)(amount * 100m), minor => minor / 100m);

    /// <summary>Unit prices and quantities: integer ten-thousandths (4 decimal places).</summary>
    private static readonly ValueConverter<decimal, long> TenThousandths = new(value => (long)(value * 10000m), stored => stored / 10000m);

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
            b.HasOne<Account>().WithMany().HasForeignKey(i => i.IncomeAccountId).OnDelete(DeleteBehavior.Restrict);
        });

        void SalesLines<TOwner>(OwnedNavigationBuilder<TOwner, SalesLine> l, string table, string ownerKey)
            where TOwner : class
        {
            l.ToTable(table);
            l.WithOwner().HasForeignKey(ownerKey);
            l.HasKey(ownerKey, nameof(SalesLine.LineNumber));
            l.Property(x => x.Description).HasMaxLength(1000);
            l.Property(x => x.Quantity).HasColumnName("QuantityTenThousandths").HasConversion(TenThousandths);
            l.Property(x => x.Rate).HasColumnName("RateTenThousandths").HasConversion(TenThousandths);
            l.Property(x => x.Amount).HasColumnName("AmountMinor").HasConversion(MinorUnits);
            l.HasOne<Item>().WithMany().HasForeignKey(x => x.ItemId).OnDelete(DeleteBehavior.Restrict);
            l.HasOne<Account>().WithMany().HasForeignKey(x => x.IncomeAccountId).OnDelete(DeleteBehavior.Restrict);
        }

        modelBuilder.Entity<Invoice>(b =>
        {
            b.ToTable("Invoices");
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
                a.HasIndex(x => x.InvoiceId);
                a.HasOne<Invoice>().WithMany().HasForeignKey(x => x.InvoiceId).OnDelete(DeleteBehavior.Restrict);
            });
            b.Navigation(p => p.Applications).UsePropertyAccessMode(PropertyAccessMode.Field);
        });
    }

    private static void ConfigurePurchases(ModelBuilder modelBuilder)
    {
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
            b.OwnsMany(x => x.Lines, l =>
            {
                l.ToTable("BillLines");
                l.WithOwner().HasForeignKey("BillId");
                l.HasKey("BillId", nameof(BillLine.LineNumber));
                l.Property(x => x.Description).HasMaxLength(1000);
                l.Property(x => x.Amount).HasColumnName("AmountMinor").HasConversion(MinorUnits);
                l.HasOne<Account>().WithMany().HasForeignKey(x => x.AccountId).OnDelete(DeleteBehavior.Restrict);
            });
            b.Navigation(x => x.Lines).UsePropertyAccessMode(PropertyAccessMode.Field);
        });

        modelBuilder.Entity<BillPayment>(b =>
        {
            b.ToTable("BillPayments");
            b.HasKey(p => p.Id);
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
