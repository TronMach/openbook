using Microsoft.EntityFrameworkCore;
using OpenBook.Domain.Accounts;
using OpenBook.Domain.Auditing;
using OpenBook.Domain.Common;
using OpenBook.Domain.Ledger;

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
            b.Property(a => a.Type).HasConversion<string>().HasMaxLength(20);
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
                .HasConversion(amount => (long)(amount * 100m), minor => minor / 100m);
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
    }
}
