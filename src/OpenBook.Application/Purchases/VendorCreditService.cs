using System.Text.Json;
using OpenBook.Application.Accounts;
using OpenBook.Application.Ledger;
using OpenBook.Domain.Accounts;
using OpenBook.Domain.Auditing;
using OpenBook.Domain.Common;
using OpenBook.Domain.Ledger;
using OpenBook.Domain.Purchases;

namespace OpenBook.Application.Purchases;

/// <param name="Reference">The vendor's credit memo number.</param>
public sealed record CreateVendorCreditCommand(
    Guid VendorId,
    DateOnly Date,
    IReadOnlyList<BillLineDraft> Lines,
    string? Reference = null,
    string? Memo = null);

public sealed record ApplyVendorCredit(Guid VendorCreditId, Guid BillId, decimal Amount);

public sealed record RecordVendorRefundCommand(
    Guid VendorCreditId,
    DateOnly Date,
    Guid DepositAccountId,
    decimal Amount,
    string? Reference = null,
    string? Memo = null);

/// <summary>
/// Vendor credits: entering them, applying them to the vendor's bills (which posts nothing: both sides sit
/// in accounts payable), and recording money the vendor pays back.
/// </summary>
public sealed class VendorCreditService(
    IPurchasesStore store,
    PurchaseAccounts purchaseAccounts,
    AccountService accounts,
    PostingEngine engine,
    IClock clock,
    ICurrentUser user)
{
    internal const string EntityType = "vendor_credit";
    private const string RefundType = "vendor_refund";

    public Task<VendorCredit?> GetAsync(Guid id, CancellationToken ct = default) => store.GetVendorCreditAsync(id, ct);

    public Task<IReadOnlyList<VendorCredit>> ListAsync(Guid? vendorId = null, CancellationToken ct = default) => store.ListVendorCreditsAsync(vendorId, ct);

    public Task<VendorRefund?> GetRefundAsync(Guid id, CancellationToken ct = default) => store.GetVendorRefundAsync(id, ct);

    public Task<IReadOnlyList<VendorRefund>> ListRefundsAsync(Guid? vendorId = null, CancellationToken ct = default) => store.ListVendorRefundsAsync(vendorId, ct);

    /// <summary>The vendor's credits with something left, oldest first.</summary>
    public async Task<IReadOnlyList<VendorCredit>> AvailableAsync(Guid vendorId, CancellationToken ct = default) =>
        (await store.ListVendorCreditsAsync(vendorId, ct)).Where(c => c.Balance > 0m).OrderBy(c => c.Date).ToList();

    public async Task<VendorCredit> CreateAsync(CreateVendorCreditCommand command, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        var vendor = await store.GetVendorAsync(command.VendorId, ct) ?? throw new EntityNotFoundException("vendor", command.VendorId);
        if (!vendor.IsActive)
            throw new DomainException("vendor.inactive", $"{vendor.DisplayName} is inactive.");

        if ((command.Lines ?? []).Any(l => l.ItemId is not null))
            throw new DomainException("vendorcredit.no_items", "Vendor credits are for categories only. To send stock back, record a quantity adjustment.");
        var lines = Bill.LinesFrom(command.Lines ?? []);
        var chart = await accounts.GetChartAsync(ct);
        foreach (var accountId in lines.Select(l => l.AccountId).Distinct())
            Bill.EnsureCategoryAccount(chart.Get(accountId));
        var payables = await purchaseAccounts.GetOrCreatePayablesAsync(ct);

        var id = Guid.CreateVersion7();
        var reference = string.IsNullOrWhiteSpace(command.Reference) ? null : command.Reference.Trim();
        var memo = reference is null ? $"Vendor credit · {vendor.DisplayName}" : $"Vendor credit {reference} · {vendor.DisplayName}";
        var entry = await engine.StageAsync(new PostJournalEntryCommand(
            command.Date, VendorCredit.Postings(payables.Id, lines, memo), memo, VendorCredit.Source, id), ct);

        var credit = VendorCredit.Create(id, vendor.Id, payables.Id, command.Date, lines, entry.Id, clock.UtcNow, reference, command.Memo);
        store.Add(credit);
        store.Add(Audit(AuditAction.Created, EntityType, credit.Id, null, Snapshot(credit)));
        await store.SaveChangesAsync(ct);
        return credit;
    }

    public async Task<VendorCredit> VoidAsync(Guid id, string reason, CancellationToken ct = default)
    {
        var credit = await store.GetVendorCreditAsync(id, ct) ?? throw new EntityNotFoundException(EntityType, id);
        if (credit.Status == DocumentStatus.Voided)
            throw new DomainException("vendorcredit.already_voided", "This vendor credit is already voided.");
        if (credit.AmountApplied > 0m)
            throw new DomainException("vendorcredit.applied", "This credit is applied to bills, so it can't be voided.");
        if (credit.AmountRefunded > 0m)
            throw new DomainException("vendorcredit.refunded", "This credit was refunded. Void the refund first.");

        var before = Snapshot(credit);
        await engine.StageVoidAsync(credit.JournalEntryId, reason, fromDocument: true, ct);
        credit.Void();
        store.Add(Audit(AuditAction.Voided, EntityType, credit.Id, before, Snapshot(credit)));
        await store.SaveChangesAsync(ct);
        return credit;
    }

    /// <summary>Applies credit to the vendor's open bills, in one save. Applied credit can't be taken back.</summary>
    public async Task ApplyAsync(Guid vendorId, DateOnly date, IReadOnlyList<ApplyVendorCredit> applications, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(applications);
        var lines = applications.Where(a => a.Amount != 0m).ToList();
        if (lines.Count == 0)
            throw new DomainException("credit.nothing_applied", "Enter an amount to apply.");

        var bills = (await store.GetBillsAsync(lines.Select(a => a.BillId).Distinct().ToList(), ct)).ToDictionary(b => b.Id);
        var credits = new Dictionary<Guid, VendorCredit>();
        foreach (var creditId in lines.Select(a => a.VendorCreditId).Distinct())
            credits[creditId] = await store.GetVendorCreditAsync(creditId, ct) ?? throw new EntityNotFoundException(EntityType, creditId);

        // Check everything first, so a refusal leaves nothing changed in memory.
        var billTotals = new Dictionary<Guid, decimal>();
        var creditTotals = new Dictionary<Guid, decimal>();
        foreach (var line in lines)
        {
            Money.EnsureCents(line.Amount);
            if (line.Amount < 0m)
                throw new DomainException("credit.amount_not_positive", "Enter an amount above zero.");
            var bill = bills.GetValueOrDefault(line.BillId) ?? throw new EntityNotFoundException(BillService.EntityType, line.BillId);
            var credit = credits[line.VendorCreditId];
            if (bill.VendorId != vendorId || bill.Status != DocumentStatus.Posted || credit.VendorId != vendorId)
                throw new DomainException("credit.wrong_vendor", "That credit and bill must both be this vendor's.");
            if (bill.PayableAccountId != credit.PayableAccountId)
                throw new DomainException("credit.mixed_payables", "That credit and bill post to different payable accounts.");
            if (date < credit.Date)
                throw new DomainException("credit.applied_before_payment", "Credit can't be applied before the credit's date.");

            billTotals[bill.Id] = billTotals.GetValueOrDefault(bill.Id) + line.Amount;
            creditTotals[credit.Id] = creditTotals.GetValueOrDefault(credit.Id) + line.Amount;
            if (billTotals[bill.Id] > bill.Balance)
                throw new DomainException("payment.exceeds_balance", $"That bill has only {bill.Balance:N2} left to pay.");
            if (creditTotals[credit.Id] > credit.Balance)
                throw new DomainException("credit.exceeds_available", $"That credit has only {credit.Balance:N2} left.");
        }

        var billsBefore = bills.Values.ToDictionary(b => b.Id, BillService.Snapshot);
        var creditsBefore = credits.Values.ToDictionary(c => c.Id, Snapshot);
        foreach (var line in lines)
        {
            bills[line.BillId].ApplyPayment(line.Amount);
            credits[line.VendorCreditId].Apply(line.BillId, line.Amount, date);
        }

        foreach (var bill in bills.Values)
            store.Add(Audit(AuditAction.Updated, BillService.EntityType, bill.Id, billsBefore[bill.Id], BillService.Snapshot(bill)));
        foreach (var credit in credits.Values)
            store.Add(Audit(AuditAction.Updated, EntityType, credit.Id, creditsBefore[credit.Id], Snapshot(credit)));
        await store.SaveChangesAsync(ct);
    }

    /// <summary>Money the vendor paid back: debits the bank, credits accounts payable.</summary>
    public async Task<VendorRefund> RecordRefundAsync(RecordVendorRefundCommand command, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        var credit = await store.GetVendorCreditAsync(command.VendorCreditId, ct) ?? throw new EntityNotFoundException(EntityType, command.VendorCreditId);
        Money.EnsureCents(command.Amount);
        if (command.Amount <= 0m)
            throw new DomainException("refund.amount_not_positive", "Enter an amount above zero.");
        if (command.Amount > credit.Balance)
            throw new DomainException("refund.exceeds_credit", $"This credit has only {credit.Balance:N2} left.");
        var account = (await accounts.GetChartAsync(ct)).Get(command.DepositAccountId);
        if (account.Type is not (AccountType.Bank or AccountType.CreditCard or AccountType.OtherCurrentAsset) || !account.IsActive)
            throw new DomainException("refund.account_type", $"'{account.Name}' can't receive a refund; choose a bank, card or Undeposited Funds account.");
        var vendor = await store.GetVendorAsync(credit.VendorId, ct) ?? throw new EntityNotFoundException("vendor", credit.VendorId);

        var id = Guid.CreateVersion7();
        var memo = $"Refund from {vendor.DisplayName}";
        var entry = await engine.StageAsync(new PostJournalEntryCommand(
            command.Date,
            [JournalLineDraft.Dr(account.Id, command.Amount), JournalLineDraft.Cr(credit.PayableAccountId, command.Amount)],
            memo,
            VendorRefund.Source,
            id), ct);

        var refund = VendorRefund.Create(id, credit, command.Date, command.Amount, account.Id, entry.Id, clock.UtcNow, command.Reference, command.Memo);
        var before = Snapshot(credit);
        credit.RecordRefund(command.Amount);
        store.Add(refund);
        store.Add(Audit(AuditAction.Updated, EntityType, credit.Id, before, Snapshot(credit)));
        store.Add(Audit(AuditAction.Created, RefundType, refund.Id, null, Snapshot(refund)));
        await store.SaveChangesAsync(ct);
        return refund;
    }

    public async Task<VendorRefund> VoidRefundAsync(Guid id, string reason, CancellationToken ct = default)
    {
        var refund = await store.GetVendorRefundAsync(id, ct) ?? throw new EntityNotFoundException(RefundType, id);
        if (refund.Status == DocumentStatus.Voided)
            throw new DomainException("refund.already_voided", "This refund is already voided.");

        var before = Snapshot(refund);
        await engine.StageVoidAsync(refund.JournalEntryId, reason, fromDocument: true, ct);
        refund.Void();
        if (await store.GetVendorCreditAsync(refund.VendorCreditId, ct) is { } credit)
            credit.RemoveRefund(refund.Amount);
        store.Add(Audit(AuditAction.Voided, RefundType, refund.Id, before, Snapshot(refund)));
        await store.SaveChangesAsync(ct);
        return refund;
    }

    private AuditRecord Audit(AuditAction action, string type, Guid id, string? before, string after) =>
        AuditRecord.Create(clock.UtcNow, user.Actor, action, type, id, before, after);

    private static string Snapshot(VendorCredit c) => JsonSerializer.Serialize(new
    {
        c.VendorId,
        c.Reference,
        c.Date,
        c.Total,
        c.AmountRefunded,
        Status = c.Status.ToString(),
        c.Memo,
        Lines = c.Lines.Select(l => new { l.AccountId, l.Amount, l.Description }),
        Applications = c.Applications.Select(a => new { a.BillId, a.Amount, a.AppliedOn }),
    });

    private static string Snapshot(VendorRefund r) => JsonSerializer.Serialize(new
    {
        r.VendorId,
        r.VendorCreditId,
        r.Date,
        r.Amount,
        r.DepositAccountId,
        r.Reference,
        r.Memo,
        Status = r.Status.ToString(),
    });
}
