using System.Text.Json;
using OpenBook.Application.Assets;
using OpenBook.Application.Banking;
using OpenBook.Application.Nonprofit;
using OpenBook.Application.Projects;
using OpenBook.Application.Purchases;
using OpenBook.Application.Sales;
using OpenBook.Domain.Auditing;
using OpenBook.Domain.Common;
using OpenBook.Domain.Nonprofit;
using OpenBook.Domain.Purchases;
using OpenBook.Domain.Sales;

namespace OpenBook.Application.Lists;

public enum ErasureKind
{
    Customer,
    Vendor,
}

/// <summary>What an erasure took away, as counts: all the audit log says of it.</summary>
/// <param name="Emails">Sent emails whose recipients and wording were blanked; that they were sent stays.</param>
/// <param name="ScheduledEmails">Scheduled emails blanked the same way; those not sent yet are cancelled.</param>
/// <param name="OnlineGifts">Checkouts of the giving page whose name, email and note were blanked.</param>
public sealed record ErasureCounts(
    int Notes, int Files, int FieldValues, int Emails, int Texts, int ScheduledEmails = 0, int PortalLinks = 0, int DocumentLinks = 0,
    int OnlineGifts = 0, int Households = 0, int BankAccounts = 0);

/// <summary>Takes away what's kept around a customer or vendor, in the database, inside the caller's transaction (which saves).</summary>
public interface IRecordEraser
{
    /// <param name="billed">The customer and those billed with it, whose emails went to its address; for a vendor, just the vendor.</param>
    /// <param name="email">The record's email address, which finds giving page checkouts that never became a gift.</param>
    Task<ErasureCounts> EraseAsync(ErasureKind kind, Guid id, IReadOnlyCollection<Guid> billed, string? email, DateTimeOffset at, CancellationToken ct);
}

/// <summary>A record somebody asked to have erased: what stands in the way, if anything.</summary>
/// <param name="Blockers">What has to be settled first; empty when it can be erased.</param>
public sealed record ErasureReview(string Name, DateTimeOffset? ErasedAt, IReadOnlyList<string> Blockers);

/// <param name="Name">What the record is called from now on.</param>
public sealed record ErasureResult(Guid Id, string Name, ErasureCounts Erased);

/// <summary>
/// Erasing the personal details of a customer (or donor) or vendor who asked for it. The documents, the ledger and the
/// audit log stay as they are, since the law makes the business keep them; what says who the person is goes from the
/// record and from what's kept around it. Only for a record that's done with, and there's no way back.
/// </summary>
public sealed class ErasureService(
    ISalesStore sales,
    IPurchasesStore purchases,
    INonprofitStore nonprofit,
    IProjectStore projects,
    IBankingStore banking,
    IAssetStore assets,
    IOnlineMoneyStore cards,
    GiftService gifts,
    VendorDepositService deposits,
    IRecordEraser eraser,
    IUnitOfWork unitOfWork,
    IClock clock,
    ICurrentUser user)
{
    private const int MostNamed = 5;

    public async Task<ErasureReview?> ReviewAsync(ErasureKind kind, Guid id, CancellationToken ct = default)
    {
        if (kind == ErasureKind.Customer)
        {
            return await sales.GetCustomerAsync(id, ct) is { } customer
                ? new ErasureReview(customer.DisplayName, customer.ErasedAt, customer.ErasedAt is null ? await BlockersAsync(customer, ct) : [])
                : null;
        }

        return await purchases.GetVendorAsync(id, ct) is { } vendor
            ? new ErasureReview(vendor.DisplayName, vendor.ErasedAt, vendor.ErasedAt is null ? await BlockersAsync(vendor, ct) : [])
            : null;
    }

    /// <param name="confirmation">The customer's name, typed to confirm.</param>
    public async Task<ErasureResult> EraseCustomerAsync(Guid id, string? confirmation, CancellationToken ct = default)
    {
        await using var transaction = await unitOfWork.BeginAsync(ct);
        var customer = await sales.GetCustomerAsync(id, ct) ?? throw new EntityNotFoundException("customer", id);
        Check(customer.DisplayName, customer.ErasedAt, confirmation);
        Refuse(await BlockersAsync(customer, ct));

        var all = await sales.ListCustomersAsync(ct);
        var name = Placeholder("customer", id, all.Select(c => c.DisplayName));
        var billed = CustomerTree.Billed(id, all.ToDictionary(c => c.Id));
        var at = clock.UtcNow;
        var counts = await eraser.EraseAsync(ErasureKind.Customer, id, [.. billed], customer.Email, at, ct);
        customer.Erase(name, at);
        sales.Add(Audit("customer", id, at, counts));
        await sales.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return new ErasureResult(id, name, counts);
    }

    /// <param name="confirmation">The vendor's name, typed to confirm.</param>
    public async Task<ErasureResult> EraseVendorAsync(Guid id, string? confirmation, CancellationToken ct = default)
    {
        await using var transaction = await unitOfWork.BeginAsync(ct);
        var vendor = await purchases.GetVendorAsync(id, ct) ?? throw new EntityNotFoundException("vendor", id);
        Check(vendor.DisplayName, vendor.ErasedAt, confirmation);
        Refuse(await BlockersAsync(vendor, ct));

        var name = Placeholder("vendor", id, (await purchases.ListVendorsAsync(ct)).Select(v => v.DisplayName));
        var at = clock.UtcNow;
        var counts = await eraser.EraseAsync(ErasureKind.Vendor, id, [id], vendor.Email, at, ct);
        vendor.Erase(name, at);
        purchases.Add(Audit("vendor", id, at, counts));
        await purchases.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return new ErasureResult(id, name, counts);
    }

    /// <summary>"Erased customer 4F2A91": the end of its id, as much of it as it takes to be the only one.</summary>
    internal static string Placeholder(string noun, Guid id, IEnumerable<string> taken)
    {
        var used = taken.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var digits = $"{id:N}".ToUpperInvariant();
        for (var length = 6; ; length += 2)
        {
            var name = $"Erased {noun} {digits[^length..]}";
            if (!used.Contains(name) || length == digits.Length)
                return name;
        }
    }

    private static void Check(string name, DateTimeOffset? erasedAt, string? confirmation)
    {
        if (erasedAt is not null)
            throw new DomainException("erase.already", "These personal details were erased already.");
        if (!string.Equals(confirmation?.Trim(), name, StringComparison.OrdinalIgnoreCase))
            throw new DomainException("erase.confirm", "To confirm, type the name exactly as it's shown.");
    }

    private static void Refuse(List<string> blockers)
    {
        if (blockers.Count > 0)
            throw new DomainException("erase.not_ready", $"Nothing was erased, because this has to be settled first. {string.Join(" ", blockers)}");
    }

    private async Task<List<string>> BlockersAsync(Customer customer, CancellationToken ct)
    {
        var id = customer.Id;
        var found = new List<string>();
        if (customer.IsActive)
            found.Add("It's still active: make it inactive.");
        // Taken off at Stripe as it's removed here, which an erasure couldn't put right if Stripe didn't answer.
        if ((await cards.ListCardsAsync(id, ct)).Any(c => c.Status is SavedCardStatus.Active or SavedCardStatus.Waiting))
            found.Add("It has a saved card: remove it on the customer's page.");
        Name(found, "Unpaid invoices", (await sales.ListOpenInvoicesAsync(id, ct)).Select(i => i.Number));
        Amount(found, "Credit not used yet", (await sales.ListCreditMemosAsync(id, ct)).Sum(c => c.Balance)
            + (await sales.ListPaymentsAsync(id, ct)).Sum(p => p.Unapplied));
        Amount(found, "Retainers and deposits not used yet", (await sales.ListRetainersAsync(id, ct)).Sum(r => r.Balance));
        Name(found, "Estimates still pending", (await sales.ListEstimatesAsync(id, ct)).Where(e => e.Status == EstimateStatus.Pending).Select(e => e.Number));
        Name(found, "Open sales orders", (await sales.ListSalesOrdersAsync(id, ct)).Where(o => o.Status == SalesOrderStatus.Open).Select(o => o.Number));
        Name(found, "Active recurring invoices", (await sales.ListRecurringInvoicesAsync(ct)).Where(r => r.CustomerId == id && r.IsActive).Select(r => r.Name));
        Amount(found, "Active recurring gifts", (await nonprofit.ListRecurringGiftsAsync(ct)).Where(r => r.DonorId == id && r.IsActive).Sum(r => r.Amount));
        Amount(found, "Still to come on pledges", (await gifts.ListPledgesAsync(id, ct)).Where(p => p.Pledge.Status == DocumentStatus.Posted).Sum(p => Math.Max(0m, p.Balance)));
        Name(found, "Active grants", (await nonprofit.ListGrantsAsync(ct)).Where(g => g.FunderId == id && g.Status == GrantStatus.Active).Select(g => g.Name));
        if (await projects.GetProjectForCustomerAsync(id, ct) is { IsOpen: true })
            found.Add("It's an open project: complete or cancel it.");
        Name(found, "Active sub-customers", (await sales.ListCustomersAsync(ct)).Where(c => c.ParentId == id && c.IsActive).Select(c => c.DisplayName));
        return found;
    }

    private async Task<List<string>> BlockersAsync(Vendor vendor, CancellationToken ct)
    {
        var id = vendor.Id;
        var found = new List<string>();
        if (vendor.IsActive)
            found.Add("It's still active: make it inactive.");
        if (vendor.Track1099)
            found.Add("Its payments are tracked for a 1099, which the payer has to keep the details of for years: switch that off once you no longer need them.");
        Name(found, "Unpaid bills", (await purchases.ListOpenBillsAsync(id, ct)).Select(b => b.Reference ?? "(no number)"));
        Amount(found, "Vendor credits not used yet", (await purchases.ListVendorCreditsAsync(id, ct)).Sum(c => c.Balance));
        Amount(found, "Deposits not used yet", (await deposits.ListAsync(id, ct)).Sum(d => d.Remaining));
        Name(found, "Open purchase orders", (await purchases.ListPurchaseOrdersAsync(id, ct)).Where(o => o.Status == PurchaseOrderStatus.Open).Select(o => o.Number));
        Name(found, "Active recurring bills", (await purchases.ListRecurringBillsAsync(ct)).Where(r => r.VendorId == id && r.IsActive).Select(r => r.Name));
        Name(found, "Active recurring expenses", (await banking.ListRecurringExpensesAsync(ct)).Where(r => r.VendorId == id && r.IsActive).Select(r => r.Name));
        Name(found, "Loans still owed to them", (await assets.ListLoansAsync(ct)).Where(l => l.LenderVendorId == id && !l.IsVoided && !l.IsPaidOff).Select(l => l.Name));
        return found;
    }

    private static void Name(List<string> found, string what, IEnumerable<string> which)
    {
        var all = which.ToList();
        if (all.Count > 0)
            found.Add($"{what}: {string.Join(", ", all.Take(MostNamed))}{(all.Count > MostNamed ? $" and {all.Count - MostNamed} more" : "")}.");
    }

    private static void Amount(List<string> found, string what, decimal amount)
    {
        if (amount > 0m)
            found.Add($"{what}: {amount:N2}.");
    }

    /// <summary>The counts and nothing else: the log can't be changed later, so nothing erased may be written in it again.</summary>
    private AuditRecord Audit(string entityType, Guid id, DateTimeOffset at, ErasureCounts counts) =>
        AuditRecord.Create(at, user.Actor, AuditAction.Updated, entityType, id, null, JsonSerializer.Serialize(new
        {
            PersonalDetailsErased = true,
            counts.Notes,
            counts.Files,
            counts.FieldValues,
            counts.Emails,
            counts.Texts,
            counts.ScheduledEmails,
            counts.PortalLinks,
            counts.DocumentLinks,
            counts.OnlineGifts,
            counts.Households,
            counts.BankAccounts,
        }));
}
