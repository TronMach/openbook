using System.Text.Json;
using OpenBook.Application.Accounts;
using OpenBook.Application.Ledger;
using OpenBook.Domain.Auditing;
using OpenBook.Domain.Common;
using OpenBook.Domain.Purchases;

namespace OpenBook.Application.Purchases;

/// <param name="DueDate">Defaults to the bill date plus the vendor's payment terms.</param>
/// <param name="Reference">The vendor's invoice number; unique per vendor.</param>
public sealed record CreateBillCommand(
    Guid VendorId,
    DateOnly Date,
    IReadOnlyList<BillLineDraft> Lines,
    DateOnly? DueDate = null,
    string? Reference = null,
    string? Memo = null);

public sealed class BillService(
    IPurchasesStore store,
    PurchaseAccounts purchaseAccounts,
    AccountService accounts,
    PostingEngine engine,
    IClock clock,
    ICurrentUser user)
{
    private const string EntityType = "bill";

    public Task<Bill?> GetAsync(Guid id, CancellationToken ct = default) => store.GetBillAsync(id, ct);

    public Task<IReadOnlyList<Bill>> ListAsync(Guid? vendorId = null, CancellationToken ct = default) => store.ListBillsAsync(vendorId, ct);

    public Task<IReadOnlyList<BillPayment>> ListPaymentsAsync(Guid billId, CancellationToken ct = default) => store.ListPaymentsForBillAsync(billId, ct);

    public async Task<Bill> CreateAsync(CreateBillCommand command, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        var vendor = await store.GetVendorAsync(command.VendorId, ct) ?? throw new EntityNotFoundException("vendor", command.VendorId);
        if (!vendor.IsActive)
            throw new DomainException("vendor.inactive", $"{vendor.DisplayName} is inactive.");

        var reference = string.IsNullOrWhiteSpace(command.Reference) ? null : command.Reference.Trim();
        if (reference is not null && (await store.ListBillsAsync(vendor.Id, ct))
                .Any(b => b.Status == DocumentStatus.Posted && string.Equals(b.Reference, reference, StringComparison.OrdinalIgnoreCase)))
            throw new DomainException("bill.reference_taken", $"There's already a bill {reference} from {vendor.DisplayName}.");

        var lines = Bill.LinesFrom(command.Lines);
        var chart = await accounts.GetChartAsync(ct);
        foreach (var accountId in lines.Select(l => l.AccountId).Distinct())
            Bill.EnsureCategoryAccount(chart.Get(accountId));
        var payables = await purchaseAccounts.GetOrCreatePayablesAsync(ct);

        var id = Guid.CreateVersion7();
        var memo = reference is null ? $"Bill · {vendor.DisplayName}" : $"Bill {reference} · {vendor.DisplayName}";
        var entry = await engine.StageAsync(new PostJournalEntryCommand(command.Date, Bill.Postings(payables.Id, lines, memo), memo, Bill.Source, id), ct);

        var bill = Bill.Create(id, vendor.Id, payables.Id, command.Date, command.DueDate ?? vendor.DueDateFor(command.Date),
            lines, entry.Id, clock.UtcNow, reference, command.Memo);
        store.Add(bill);
        store.Add(Audit(AuditAction.Created, bill, before: null));
        await store.SaveChangesAsync(ct);
        return bill;
    }

    public async Task<Bill> VoidAsync(Guid id, string reason, CancellationToken ct = default)
    {
        var bill = await store.GetBillAsync(id, ct) ?? throw new EntityNotFoundException(EntityType, id);
        if (bill.Status == DocumentStatus.Voided)
            throw new DomainException("bill.already_voided", "This bill is already voided.");
        if (bill.AmountPaid > 0m)
            throw new DomainException("bill.has_payments", "This bill has payments applied. Void those payments first.");

        var before = Snapshot(bill);
        await engine.StageVoidAsync(bill.JournalEntryId, reason, fromDocument: true, ct);
        bill.Void();
        store.Add(Audit(AuditAction.Voided, bill, before));
        await store.SaveChangesAsync(ct);
        return bill;
    }

    private AuditRecord Audit(AuditAction action, Bill b, string? before) =>
        AuditRecord.Create(clock.UtcNow, user.Actor, action, EntityType, b.Id, before, Snapshot(b));

    private static string Snapshot(Bill b) => JsonSerializer.Serialize(new
    {
        b.VendorId,
        b.Reference,
        b.Date,
        b.DueDate,
        b.Total,
        b.AmountPaid,
        Status = b.Status.ToString(),
        b.Memo,
        Lines = b.Lines.Select(l => new { l.AccountId, l.Description, l.Amount }),
    });
}
