using Microsoft.EntityFrameworkCore;
using OpenBook.Application.Attachments;
using OpenBook.Domain.Attachments;
using OpenBook.Domain.Auditing;
using OpenBook.Domain.Common;

namespace OpenBook.Infrastructure.Persistence;

internal sealed class EfAttachmentStore(OpenBookDbContext db) : IAttachmentStore
{
    public async Task<IReadOnlyList<Attachment>> ListAsync(string entityType, Guid entityId, CancellationToken ct) =>
        (await db.Attachments.AsNoTracking().Where(a => a.EntityType == entityType && a.EntityId == entityId).ToListAsync(ct))
            .OrderBy(a => a.AddedAt)
            .ToList();

    public Task<Attachment?> GetAsync(Guid id, CancellationToken ct) => db.Attachments.SingleOrDefaultAsync(a => a.Id == id, ct);

    public async Task<byte[]?> GetContentAsync(Guid id, CancellationToken ct) =>
        await db.AttachmentContents.AsNoTracking().Where(c => c.AttachmentId == id).Select(c => c.Data).SingleOrDefaultAsync(ct);

    public Task<bool> ExistsAsync(string entityType, Guid entityId, CancellationToken ct) => entityType switch
    {
        "invoice" => db.Invoices.AnyAsync(x => x.Id == entityId, ct),
        "payment" => db.CustomerPayments.AnyAsync(x => x.Id == entityId, ct),
        "sales_receipt" => db.SalesReceipts.AnyAsync(x => x.Id == entityId, ct),
        "estimate" => db.Estimates.AnyAsync(x => x.Id == entityId, ct),
        "credit_memo" => db.CreditMemos.AnyAsync(x => x.Id == entityId, ct),
        "refund" => db.CustomerRefunds.AnyAsync(x => x.Id == entityId, ct),
        "write_off" => db.BadDebtWriteOffs.AnyAsync(x => x.Id == entityId, ct),
        "sales_order" => db.SalesOrders.AnyAsync(x => x.Id == entityId, ct),
        "retainer" => db.Retainers.AnyAsync(x => x.Id == entityId, ct),
        "purchase_order" => db.PurchaseOrders.AnyAsync(x => x.Id == entityId, ct),
        "receipt" => db.ReceiptCaptures.AnyAsync(x => x.Id == entityId, ct),
        "captured_document" => db.CapturedDocuments.AnyAsync(x => x.Id == entityId, ct),
        "deposit" => db.BankDeposits.AnyAsync(x => x.Id == entityId, ct),
        "bill" => db.Bills.AnyAsync(x => x.Id == entityId, ct),
        "expense_claim" => db.ExpenseClaims.AnyAsync(x => x.Id == entityId, ct),
        "settlement" => db.Settlements.AnyAsync(x => x.Id == entityId, ct),
        "owner_operator" => db.OwnerOperators.AnyAsync(x => x.Id == entityId, ct),
        "truck" => db.Trucks.AnyAsync(x => x.Id == entityId, ct),
        "owner_charge" => db.OwnerCharges.AnyAsync(x => x.Id == entityId, ct),
        "owner_settlement" => db.OwnerSettlements.AnyAsync(x => x.Id == entityId, ct),
        "bill_payment" => db.BillPayments.AnyAsync(x => x.Id == entityId, ct),
        "sales_tax_payment" => db.SalesTaxPayments.AnyAsync(x => x.Id == entityId, ct),
        "vendor_credit" => db.VendorCredits.AnyAsync(x => x.Id == entityId, ct),
        "vendor_refund" => db.VendorRefunds.AnyAsync(x => x.Id == entityId, ct),
        "bank_transaction" => db.BankTransactions.AnyAsync(x => x.Id == entityId, ct),
        "journal_entry" => db.JournalEntries.AnyAsync(x => x.Id == entityId, ct),
        "reconciliation" => db.Reconciliations.AnyAsync(x => x.Id == entityId, ct),
        "customer" => NotErasedAsync(db.Customers.Where(x => x.Id == entityId).Select(x => x.ErasedAt), ct),
        "vendor" => NotErasedAsync(db.Vendors.Where(x => x.Id == entityId).Select(x => x.ErasedAt), ct),
        "inventory_adjustment" => db.InventoryAdjustments.AnyAsync(x => x.Id == entityId, ct),
        "assembly_build" => db.AssemblyBuilds.AnyAsync(x => x.Id == entityId, ct),
        "landed_cost" => db.LandedCosts.AnyAsync(x => x.Id == entityId, ct),
        "stock_transfer" => db.StockTransfers.AnyAsync(x => x.Id == entityId, ct),
        "grant" => db.Grants.AnyAsync(x => x.Id == entityId, ct),
        "donation" => db.Donations.AnyAsync(x => x.Id == entityId, ct),
        "pledge" => db.Pledges.AnyAsync(x => x.Id == entityId, ct),
        "fixed_asset" => db.FixedAssets.AnyAsync(x => x.Id == entityId, ct),
        "loan" => db.Loans.AnyAsync(x => x.Id == entityId, ct),
        "project" => db.Projects.AnyAsync(x => x.Id == entityId, ct),
        "account" => db.Accounts.AnyAsync(x => x.Id == entityId, ct),
        "item" => db.Items.AnyAsync(x => x.Id == entityId, ct),
        _ => Task.FromResult(false),
    };

    /// <summary>A customer or vendor whose details were erased takes no new files, notes or field values.</summary>
    private static async Task<bool> NotErasedAsync(IQueryable<DateTimeOffset?> erasedAt, CancellationToken ct)
    {
        var found = await erasedAt.ToListAsync(ct);
        if (found.Any(at => at is not null))
            throw new DomainException("record.erased", "The personal details of this record were erased, so nothing can be added to it.");
        return found.Count > 0;
    }

    public void Add(Attachment attachment, AttachmentContent content)
    {
        db.Attachments.Add(attachment);
        db.AttachmentContents.Add(content);
    }

    public void Remove(Attachment attachment) => db.Attachments.Remove(attachment);

    public void Add(AuditRecord record) => db.AuditRecords.Add(record);

    public Task SaveChangesAsync(CancellationToken ct) => db.SaveChangesAsync(ct);
}
