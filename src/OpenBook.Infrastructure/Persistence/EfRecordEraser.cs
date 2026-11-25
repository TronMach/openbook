using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Storage;
using OpenBook.Application.Lists;
using OpenBook.Domain.Common;
using OpenBook.Domain.Nonprofit;
using OpenBook.Domain.Purchases;
using OpenBook.Domain.Sales;

namespace OpenBook.Infrastructure.Persistence;

/// <summary>
/// Takes away what's kept around an erased customer or vendor, inside the caller's transaction. Notes, files and field
/// values go; emails and texts keep that they were sent, when and by whom; links stop working. Documents, the ledger
/// and the audit log aren't touched. Changed households are left for the caller's save.
/// </summary>
internal sealed class EfRecordEraser(OpenBookDbContext db) : IRecordEraser
{
    /// <summary>What's left in place of an address or wording.</summary>
    internal const string Gone = "(erased)";

    public async Task<ErasureCounts> EraseAsync(ErasureKind kind, Guid id, IReadOnlyCollection<Guid> billed, string? email, DateTimeOffset at, CancellationToken ct)
    {
        var type = kind == ErasureKind.Customer ? "customer" : "vendor";
        var (emails, texts) = await BlankMessagesAsync(kind, billed, ct);

        var notes = await db.Notes.Where(n => n.EntityType == type && n.EntityId == id).ExecuteDeleteAsync(ct);
        var values = await db.CustomFieldValues.Where(v => v.EntityType == type && v.EntityId == id).ExecuteDeleteAsync(ct);
        var attachments = db.Attachments.Where(a => a.EntityType == type && a.EntityId == id);
        var fileIds = attachments.Select(a => a.Id);
        await db.AttachmentContents.Where(c => fileIds.Contains(c.AttachmentId)).ExecuteDeleteAsync(ct);
        var files = await attachments.ExecuteDeleteAsync(ct);
        var counts = new ErasureCounts(notes, files, values, emails, texts);
        if (kind == ErasureKind.Vendor)
            return counts with { BankAccounts = await db.VendorBankAccounts.Where(a => a.VendorId == id).ExecuteDeleteAsync(ct) };

        DateTimeOffset? now = at.ToUniversalTime();
        return counts with
        {
            ScheduledEmails = await BlankScheduledAsync([.. billed], ct),
            PortalLinks = await db.PortalAccess.Where(a => a.CustomerId == id && a.RevokedAt == null).ExecuteUpdateAsync(s => s.SetProperty(a => a.RevokedAt, now), ct),
            DocumentLinks = await db.DocumentLinks.Where(l => l.CustomerId == id && l.RevokedAt == null).ExecuteUpdateAsync(s => s.SetProperty(l => l.RevokedAt, now), ct),
            OnlineGifts = await BlankCheckoutsAsync(id, email, ct),
            Households = await LeaveHouseholdsAsync(id, ct),
        };
    }

    /// <summary>
    /// The emails and texts about the record and its documents: every table with a foreign key to it, as the EF model has
    /// them, so a new kind of document needs nothing here. A household's statements name its members, so they count too.
    /// </summary>
    private async Task<(int Emails, int Texts)> BlankMessagesAsync(ErasureKind kind, IReadOnlyCollection<Guid> billed, CancellationToken ct)
    {
        var principal = kind == ErasureKind.Customer ? typeof(Customer) : typeof(Vendor);
        var documents = new List<string>();
        foreach (var entity in db.Model.GetEntityTypes())
        {
            if (entity.IsOwned() || entity.ClrType == principal || entity.GetTableName() is not { } table
                || entity.FindPrimaryKey() is not { Properties: [var key] } || key.ClrType != typeof(Guid))
                continue;
            var store = StoreObjectIdentifier.Table(table, entity.GetSchema());
            foreach (var foreign in entity.GetForeignKeys().Where(f => f.PrincipalEntityType.ClrType == principal && f.Properties.Count == 1))
                documents.Add($"SELECT {Quote(key.GetColumnName(store)!)} FROM {Quote(table)} WHERE {Quote(foreign.Properties[0].GetColumnName(store)!)} = @id");
        }

        if (kind == ErasureKind.Customer)
            documents.Add("""SELECT "HouseholdId" FROM "HouseholdMembers" WHERE "DonorId" = @id""");

        var about = $"""WHERE "To" <> '{Gone}' AND ("EntityId" = @id OR "EntityId" IN ({string.Join(" UNION ", documents)}))""";
        var (emails, texts) = (0, 0);
        foreach (var id in billed)
        {
            emails += await RunAsync($"""
                UPDATE "SentEmails" SET "To" = '{Gone}', "Subject" = '{Gone}', "Body" = '{Gone}',
                "Cc" = CASE WHEN "Cc" IS NULL THEN NULL ELSE '{Gone}' END,
                "Bcc" = CASE WHEN "Bcc" IS NULL THEN NULL ELSE '{Gone}' END,
                "Attachment" = CASE WHEN "Attachment" IS NULL THEN NULL ELSE '{Gone}' END
                {about}
                """, id, ct);
            texts += await RunAsync($"""UPDATE "SentTexts" SET "To" = '{Gone}', "Body" = '{Gone}', "ProviderId" = NULL {about}""", id, ct);
        }

        return (emails, texts);
    }

    /// <summary>Scheduled emails and the addresses reminders went to; what hasn't gone yet is cancelled.</summary>
    private async Task<int> BlankScheduledAsync(List<Guid> billed, CancellationToken ct)
    {
        var invoices = db.Invoices.Where(i => billed.Contains(i.CustomerId)).Select(i => i.Id);
        await db.InvoiceReminders.Where(r => invoices.Contains(r.InvoiceId)).ExecuteUpdateAsync(s => s.SetProperty(r => r.To, Gone), ct);
        await db.ScheduledEmails.Where(e => invoices.Contains(e.InvoiceId) && (e.Status == ScheduledEmailStatus.Scheduled || e.Status == ScheduledEmailStatus.Failed))
            .ExecuteUpdateAsync(s => s.SetProperty(e => e.Status, ScheduledEmailStatus.Cancelled), ct);
        return await db.ScheduledEmails.Where(e => invoices.Contains(e.InvoiceId)).ExecuteUpdateAsync(s => s
            .SetProperty(e => e.To, Gone)
            .SetProperty(e => e.Subject, Gone)
            .SetProperty(e => e.Body, Gone)
            .SetProperty(e => e.Cc, (string?)null)
            .SetProperty(e => e.Bcc, (string?)null)
            .SetProperty(e => e.Error, (string?)null)
            // Someone sending or changing it at this moment is refused, rather than putting it back.
            .SetProperty(e => EF.Property<int>(e, OpenBookDbContext.VersionProperty), e => EF.Property<int>(e, OpenBookDbContext.VersionProperty) + 1), ct);
    }

    /// <summary>What they typed on the giving page: for their gifts, and for those given up under the same email.</summary>
#pragma warning disable CA1304, CA1311, CA1862 // Translated to SQL lower(): the culture overloads these rules suggest don't translate.
    private Task<int> BlankCheckoutsAsync(Guid donorId, string? email, CancellationToken ct)
    {
        var address = email?.ToLowerInvariant();
        return db.DonationCheckouts
            .Where(c => db.Donations.Any(d => d.Id == c.DonationId && d.DonorId == donorId)
                || (address != null && c.DonationId == null && c.Status == DonationCheckoutStatus.Expired && c.Email.ToLower() == address))
            .ExecuteUpdateAsync(s => s
                .SetProperty(c => c.Name, Gone)
                .SetProperty(c => c.Email, Gone)
                .SetProperty(c => c.Note, (string?)null)
                .SetProperty(c => EF.Property<int>(c, OpenBookDbContext.VersionProperty), c => EF.Property<int>(c, OpenBookDbContext.VersionProperty) + 1), ct);
    }
#pragma warning restore CA1304, CA1311, CA1862

    /// <summary>
    /// Out of the household, which is renamed after the member its statements go to, since its name and salutation may be
    /// made of the erased one's; a household of nobody else goes.
    /// </summary>
    private async Task<int> LeaveHouseholdsAsync(Guid donorId, CancellationToken ct)
    {
        var households = await db.Households.Where(h => h.Members.Any(m => m.DonorId == donorId)).ToListAsync(ct);
        foreach (var household in households)
        {
            if (household.Members.FirstOrDefault(m => m.DonorId != donorId) is not { } other)
            {
                db.Households.Remove(household);
                continue;
            }

            if (household.PrimaryDonorId == donorId)
                household.MakePrimary(other.DonorId);
            household.Remove(donorId);
            var primary = household.PrimaryDonorId;
            var name = await db.Customers.Where(c => c.Id == primary).Select(c => c.DisplayName).SingleAsync(ct);
            household.Rename($"{TextCleaning.Cut(name, Household.MaxNameLength - 10)} household", null);
        }

        return households.Count;
    }

    private async Task<int> RunAsync(string sql, Guid id, CancellationToken ct)
    {
        var transaction = db.Database.CurrentTransaction?.GetDbTransaction()
            ?? throw new InvalidOperationException("Erasing needs an open transaction.");
        var mapping = db.Model.FindEntityType(typeof(Customer))!.FindProperty(nameof(Customer.Id))!.GetRelationalTypeMapping();
        await using var command = transaction.Connection!.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.Parameters.Add(mapping.CreateParameter(command, "id", id, nullable: false));
        return await command.ExecuteNonQueryAsync(ct);
    }

    private static string Quote(string identifier) => "\"" + identifier.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
}
