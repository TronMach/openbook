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

/// <summary>A correction to a posted bill payment (<see cref="BillPaymentService.ChangeAsync"/>). The vendor can't change.</summary>
/// <param name="Applications">Every bill of the vendor's the payment should pay; ones left out are no longer paid by it.</param>
/// <param name="Reason">Why it's changed; kept in the audit log and on the reversed entry.</param>
/// <param name="PaymentAccountId">Null keeps the account it was paid from.</param>
/// <param name="Reference">Null keeps the check number or reference; blank clears it.</param>
/// <param name="Memo">Null keeps the memo.</param>
public sealed record ChangeBillPaymentCommand(
    DateOnly Date,
    IReadOnlyList<ApplyToBill> Applications,
    string Reason,
    Guid? PaymentAccountId = null,
    string? Reference = null,
    string? Memo = null);

/// <summary>
/// Pay bills: choose any open bills, in full or part, across vendors. Writes one payment per vendor
/// (one check each), all in one save.
/// </summary>
public sealed class BillPaymentService(
    IPurchasesStore store,
    PurchaseAccounts purchaseAccounts,
    PostingEngine engine,
    Banking.IBankingStore bank,
    ILedgerStore ledger,
    IClock clock,
    ICurrentUser user)
{
    private const string EntityType = "bill_payment";

    public Task<BillPayment?> GetAsync(Guid id, CancellationToken ct = default) => store.GetPaymentAsync(id, ct);

    public Task<IReadOnlyList<BillPayment>> ListAsync(Guid? vendorId = null, CancellationToken ct = default) => store.ListPaymentsAsync(vendorId, ct);

    /// <summary>Every open bill, oldest due first.</summary>
    public async Task<IReadOnlyList<Bill>> OpenBillsAsync(Guid? vendorId = null, CancellationToken ct = default) =>
        (await store.ListOpenBillsAsync(vendorId, ct)).OrderBy(b => b.DueDate).ThenBy(b => b.Date).ToList();

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
            if (!bill.CanBePaid)
                throw new DomainException("bill.not_approved",
                    $"The bill {bill.Reference} from {vendors[bill.VendorId].DisplayName} is {(bill.Approval == BillApproval.Rejected ? "rejected" : "waiting for approval")}, so it can't be paid.");
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

    /// <summary>
    /// Corrects a posted bill payment in place, in one save: its entry is voided (the reversal dated the original date)
    /// and a new one posted, and the bills it pays are put back and paid again. Refused once reconciled (as a void is).
    /// A check already issued (printed, sent by ACH, or within a positive pay file) keeps its amount, account and number;
    /// one a bank feed line stands for keeps its amount and account (matched lines follow the new entry).
    /// </summary>
    public async Task<BillPayment> ChangeAsync(Guid id, ChangeBillPaymentCommand command, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(command.Applications);
        if (string.IsNullOrWhiteSpace(command.Reason))
            throw new DomainException("payment.change_reason_required", "Say why the payment is being changed.");

        var payment = await store.GetPaymentAsync(id, ct) ?? throw new EntityNotFoundException(EntityType, id);
        if (payment.Status == DocumentStatus.Voided)
            throw new DomainException("payment.voided", "This payment is voided, so it can't be changed.");
        var vendor = await store.GetVendorAsync(payment.VendorId, ct) ?? throw new EntityNotFoundException("vendor", payment.VendorId);

        var applications = command.Applications.Where(a => a.Amount != 0m).ToList();
        if (applications.Count == 0)
            throw new DomainException("payment.no_bills", "Choose at least one bill to pay. To pay nothing, void the payment.");
        var accountId = command.PaymentAccountId is { } given && given != payment.PaymentAccountId
            ? (await purchaseAccounts.GetPaymentAccountAsync(given, ct)).Id
            : payment.PaymentAccountId;
        var reference = command.Reference is null ? payment.Reference : string.IsNullOrWhiteSpace(command.Reference) ? null : command.Reference.Trim();
        var memo = command.Memo ?? payment.Memo;

        var old = payment.Applications.ToDictionary(a => a.BillId, a => a.Amount);
        var bills = (await store.GetBillsAsync(applications.Select(a => a.BillId).Concat(old.Keys).Distinct().ToList(), ct)).ToDictionary(b => b.Id);
        foreach (var application in applications)
        {
            if (!bills.TryGetValue(application.BillId, out var bill))
                throw new EntityNotFoundException("bill", application.BillId);
            if (bill.VendorId != payment.VendorId || bill.PayableAccountId != payment.PayableAccountId)
                throw new DomainException("payment.wrong_vendor", $"Bill {bill.Reference} isn't {vendor.DisplayName}'s, or it's kept in a different account for money you owe than the others. Pay it separately.");
            if (bill.Status != DocumentStatus.Posted)
                throw new DomainException("bill.voided", $"The bill {bill.Reference} is voided.");
            if (!bill.CanBePaid && !old.ContainsKey(bill.Id))
                throw new DomainException("bill.not_approved", $"The bill {bill.Reference} is waiting for approval or rejected, so it can't be paid.");
            if (application.Amount > bill.Balance + old.GetValueOrDefault(bill.Id))
                throw new DomainException("payment.exceeds_balance", $"The bill {bill.Reference} has only {bill.Balance + old.GetValueOrDefault(bill.Id):N2} left to pay.");
        }

        // Validates the amounts before anything is staged.
        var draft = BillPayment.Create(payment.Id, payment.VendorId, command.Date, accountId, payment.PayableAccountId,
            applications.Select(a => (a.BillId, a.Amount)).ToList(), Guid.Empty, clock.UtcNow, reference, memo);
        var moneyChanged = draft.Amount != payment.Amount || accountId != payment.PaymentAccountId;
        if (await IsIssuedAsync(payment, ct) && (moneyChanged || !string.Equals(reference, payment.Reference, StringComparison.Ordinal)))
            throw new DomainException("payment.check_issued",
                "This payment has been printed as a check, sent by ACH or in a positive pay file, so its amount, account and number stay. Void it and pay again instead.");
        var feed = await Banking.BankFeedService.TiedToAsync(bank, payment.PaymentAccountId, payment.JournalEntryId, null, ct);
        if (feed.Count > 0 && moneyChanged)
            throw new DomainException("payment.feed_matched", "This payment is matched to a line from the bank feed, so its amount and account stay. Undo the match first to change them.");

        var before = Snapshot(payment);
        var billsBefore = bills.Values.ToDictionary(b => b.Id, BillService.Snapshot);
        await engine.StageVoidAsync(payment.JournalEntryId, $"Payment changed: {command.Reason.Trim()}", fromDocument: true, ct);
        var entryMemo = string.IsNullOrEmpty(reference) ? $"Bill payment to {vendor.DisplayName}" : $"Bill payment to {vendor.DisplayName} · {reference}";
        var entry = await engine.StageAsync(new PostJournalEntryCommand(
            command.Date,
            [JournalLineDraft.Dr(payment.PayableAccountId, draft.Amount), JournalLineDraft.Cr(accountId, draft.Amount)],
            entryMemo,
            BillPayment.Source,
            payment.Id), ct);

        foreach (var (billId, amount) in old)
            bills[billId].RemovePayment(amount);
        payment.Change(command.Date, accountId, applications.Select(a => (a.BillId, a.Amount)).ToList(), entry.Id, reference, memo);
        foreach (var application in payment.Applications)
            bills[application.BillId].ApplyPayment(application.Amount);
        foreach (var item in feed)
            item.FollowEntry(entry.Id);

        foreach (var bill in bills.Values.Where(b => BillService.Snapshot(b) != billsBefore[b.Id]))
            store.Add(AuditRecord.Create(clock.UtcNow, user.Actor, AuditAction.Updated, BillService.EntityType, bill.Id, billsBefore[bill.Id], BillService.Snapshot(bill)));
        store.Add(AuditRecord.Create(clock.UtcNow, user.Actor, AuditAction.Updated, EntityType, payment.Id, before,
            DocumentChanges.WithReason(Snapshot(payment), command.Reason)));
        await store.SaveChangesAsync(ct);
        return payment;
    }

    /// <summary>The payment's corrections, oldest first.</summary>
    public Task<IReadOnlyList<DocumentChange>> ChangesAsync(Guid id, CancellationToken ct = default) =>
        DocumentChanges.ListAsync(ledger, EntityType, id, ct);

    /// <summary>A payment the bank or vendor already knows of: printed, sent by ACH, or numbered and dated within what positive pay has sent.</summary>
    private async Task<bool> IsIssuedAsync(BillPayment payment, CancellationToken ct) =>
        payment.PrintedAt is not null || payment.AchBatchId is not null
        || Banking.PositivePayService.IsCheckNumber(payment.Reference)
        && (await bank.GetPositivePaySettingsAsync(payment.PaymentAccountId, ct))?.LastExportedThrough is { } through && payment.Date <= through;

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
