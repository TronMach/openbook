using System.Globalization;
using System.Text.Json;
using OpenBook.Application.Ledger;
using OpenBook.Domain.Auditing;
using OpenBook.Domain.Common;
using OpenBook.Domain.Ledger;
using OpenBook.Domain.Purchases;

namespace OpenBook.Application.Purchases;

public sealed record ApplyToBill(Guid BillId, decimal Amount);

/// <param name="FirstReference">Check number for the first payment; numeric references count up for each vendor after it.</param>
public sealed record PayBillsCommand(
    DateOnly Date,
    Guid PaymentAccountId,
    IReadOnlyList<ApplyToBill> Applications,
    string? FirstReference = null,
    string? Memo = null);

/// <summary>
/// Pay bills: choose any open bills, in full or part, across vendors. Writes one payment per vendor
/// (one check each), all in one save.
/// </summary>
public sealed class BillPaymentService(
    IPurchasesStore store,
    PurchaseAccounts purchaseAccounts,
    PostingEngine engine,
    IClock clock,
    ICurrentUser user)
{
    private const string EntityType = "bill_payment";

    public Task<BillPayment?> GetAsync(Guid id, CancellationToken ct = default) => store.GetPaymentAsync(id, ct);

    public Task<IReadOnlyList<BillPayment>> ListAsync(Guid? vendorId = null, CancellationToken ct = default) => store.ListPaymentsAsync(vendorId, ct);

    /// <summary>Every open bill, oldest due first.</summary>
    public async Task<IReadOnlyList<Bill>> OpenBillsAsync(Guid? vendorId = null, CancellationToken ct = default) =>
        (await store.ListBillsAsync(vendorId, ct)).Where(b => b.IsOpen).OrderBy(b => b.DueDate).ThenBy(b => b.Date).ToList();

    /// <returns>The payments written, one per vendor, in vendor name order.</returns>
    public async Task<IReadOnlyList<BillPayment>> PayAsync(PayBillsCommand command, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(command.Applications);

        var applications = command.Applications.Where(a => a.Amount != 0m).ToList();
        if (applications.Count == 0)
            throw new DomainException("payment.no_bills", "Choose at least one bill to pay.");
        if (applications.Any(a => a.Amount < 0m))
            throw new DomainException("payment.amount_not_positive", "Payment amounts must be more than zero.");
        if (applications.Select(a => a.BillId).Distinct().Count() != applications.Count)
            throw new DomainException("payment.duplicate_bill", "Each bill can appear only once.");

        var account = await purchaseAccounts.GetPaymentAccountAsync(command.PaymentAccountId, ct);
        var bills = (await store.GetBillsAsync(applications.Select(a => a.BillId).ToList(), ct)).ToDictionary(b => b.Id);
        var vendors = (await store.ListVendorsAsync(ct)).ToDictionary(v => v.Id);

        // Check everything before posting anything.
        foreach (var application in applications)
        {
            if (!bills.TryGetValue(application.BillId, out var bill))
                throw new EntityNotFoundException("bill", application.BillId);
            if (bill.Status != DocumentStatus.Posted)
                throw new DomainException("bill.voided", $"A bill from {vendors[bill.VendorId].DisplayName} is voided.");
            if (application.Amount > bill.Balance)
                throw new DomainException("payment.exceeds_balance",
                    $"The bill {bill.Reference} from {vendors[bill.VendorId].DisplayName} has only {bill.Balance:N2} left to pay.");
        }

        var groups = applications
            .GroupBy(a => (bills[a.BillId].VendorId, bills[a.BillId].PayableAccountId))
            .OrderBy(g => vendors[g.Key.VendorId].DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        var payments = new List<BillPayment>();
        var reference = command.FirstReference?.Trim();
        foreach (var group in groups)
        {
            var vendor = vendors[group.Key.VendorId];
            var total = group.Sum(a => a.Amount);
            var id = Guid.CreateVersion7();
            var memo = string.IsNullOrEmpty(reference) ? $"Bill payment to {vendor.DisplayName}" : $"Bill payment to {vendor.DisplayName} · {reference}";
            var entry = await engine.StageAsync(new PostJournalEntryCommand(
                command.Date,
                [JournalLineDraft.Dr(group.Key.PayableAccountId, total), JournalLineDraft.Cr(account.Id, total)],
                memo,
                BillPayment.Source,
                id), ct);

            var payment = BillPayment.Create(id, vendor.Id, command.Date, account.Id, group.Key.PayableAccountId,
                group.Select(a => (a.BillId, a.Amount)).ToList(), entry.Id, clock.UtcNow, reference, command.Memo ?? vendor.AccountNumber);
            foreach (var application in payment.Applications)
                bills[application.BillId].ApplyPayment(application.Amount);

            store.Add(payment);
            store.Add(Audit(AuditAction.Created, payment, before: null));
            payments.Add(payment);
            reference = Next(reference);
        }

        await store.SaveChangesAsync(ct);
        return payments;
    }

    public async Task<BillPayment> VoidAsync(Guid id, string reason, CancellationToken ct = default)
    {
        var payment = await store.GetPaymentAsync(id, ct) ?? throw new EntityNotFoundException(EntityType, id);
        if (payment.Status == DocumentStatus.Voided)
            throw new DomainException("payment.already_voided", "This payment is already voided.");

        var before = Snapshot(payment);
        // Refuses payments already reconciled at the bank, before anything changes.
        await engine.StageVoidAsync(payment.JournalEntryId, reason, fromDocument: true, ct);
        payment.Void();
        var bills = (await store.GetBillsAsync(payment.Applications.Select(a => a.BillId).ToList(), ct)).ToDictionary(b => b.Id);
        foreach (var application in payment.Applications)
            bills[application.BillId].RemovePayment(application.Amount);

        store.Add(Audit(AuditAction.Voided, payment, before));
        await store.SaveChangesAsync(ct);
        return payment;
    }

    /// <summary>"1042" → "1043"; anything non-numeric repeats as-is.</summary>
    private static string? Next(string? reference) =>
        long.TryParse(reference, NumberStyles.None, CultureInfo.InvariantCulture, out var n)
            ? (n + 1).ToString(CultureInfo.InvariantCulture)
            : reference;

    private AuditRecord Audit(AuditAction action, BillPayment p, string? before) =>
        AuditRecord.Create(clock.UtcNow, user.Actor, action, EntityType, p.Id, before, Snapshot(p));

    private static string Snapshot(BillPayment p) => JsonSerializer.Serialize(new
    {
        p.VendorId,
        p.Date,
        p.Amount,
        p.PaymentAccountId,
        p.Reference,
        p.Memo,
        Status = p.Status.ToString(),
        Applications = p.Applications.Select(a => new { a.BillId, a.Amount }),
    });
}
