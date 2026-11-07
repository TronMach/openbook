using System.Text.Json;
using OpenBook.Application.Accounts;
using OpenBook.Application.Ledger;
using OpenBook.Domain.Accounts;
using OpenBook.Domain.Auditing;
using OpenBook.Domain.Common;
using OpenBook.Domain.Ledger;
using OpenBook.Domain.Purchases;

namespace OpenBook.Application.Purchases;

/// <param name="PaidFromAccountId">The bank or card account the deposit was paid from.</param>
/// <param name="PurchaseOrderId">The order it was paid against, for reference.</param>
public sealed record CreateVendorDepositCommand(
    Guid VendorId, DateOnly Date, decimal Amount, Guid PaidFromAccountId, Guid? PurchaseOrderId = null, string? Reference = null, string? Memo = null);

/// <summary>A vendor deposit with what's been used of it.</summary>
public sealed record VendorDepositBalance(VendorDeposit Deposit, decimal Applied, IReadOnlyList<BillPayment> Payments)
{
    public decimal Remaining => Deposit.Status == DocumentStatus.Posted ? Deposit.Amount - Applied : 0m;
}

/// <summary>
/// Deposits paid to vendors ahead of their bills. Paying one posts Dr "Vendor Deposits" (other current asset, created
/// on first use) / Cr the bank or card; applying it writes ordinary bill payments from that asset account
/// (<see cref="BillPaymentService.PayFromDepositAsync"/>), each recording the deposit. Voiding such a payment frees the
/// amount; the deposit itself is voided only while nothing of it is applied.
/// </summary>
public sealed class VendorDepositService(
    IPurchasesStore store,
    PurchaseAccounts purchaseAccounts,
    AccountService accounts,
    PostingEngine engine,
    BillPaymentService payments,
    IUnitOfWork unitOfWork,
    IClock clock,
    ICurrentUser user)
{
    public const string EntityType = "vendor_deposit";
    public const string DepositsAccountName = "Vendor Deposits";

    public async Task<IReadOnlyList<VendorDepositBalance>> ListAsync(Guid? vendorId = null, CancellationToken ct = default)
    {
        var deposits = await store.ListVendorDepositsAsync(vendorId, ct);
        var used = (await store.ListPaymentsAsync(vendorId, ct)).Where(p => p.VendorDepositId is not null).ToLookup(p => p.VendorDepositId!.Value);
        return [.. deposits.Select(d => Balance(d, used[d.Id]))];
    }

    public async Task<VendorDepositBalance?> GetAsync(Guid id, CancellationToken ct = default)
    {
        var deposit = await store.GetVendorDepositAsync(id, ct);
        return deposit is null ? null : Balance(deposit, (await store.ListPaymentsAsync(deposit.VendorId, ct)).Where(p => p.VendorDepositId == id));
    }

    private static VendorDepositBalance Balance(VendorDeposit deposit, IEnumerable<BillPayment> payments)
    {
        var list = payments.OrderBy(p => p.Date).ToList();
        return new VendorDepositBalance(deposit, list.Where(p => p.Status == DocumentStatus.Posted).Sum(p => p.Amount), list);
    }

    public async Task<VendorDeposit> CreateAsync(CreateVendorDepositCommand command, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        VendorDeposit.Check(command.Amount);
        var vendor = await store.GetVendorAsync(command.VendorId, ct) ?? throw new EntityNotFoundException("vendor", command.VendorId);
        if (!vendor.IsActive)
            throw new DomainException("vendor.inactive", $"{vendor.DisplayName} is inactive.");
        if (command.PurchaseOrderId is { } orderId)
        {
            var order = await store.GetPurchaseOrderAsync(orderId, ct) ?? throw new EntityNotFoundException("purchase_order", orderId);
            if (order.VendorId != vendor.Id)
                throw new DomainException("vendordeposit.order_vendor", $"Purchase order {order.Number} is for another vendor.");
        }

        var paidFrom = await purchaseAccounts.GetPaymentAccountAsync(command.PaidFromAccountId, ct);
        var holding = await DepositsAccountAsync(ct);
        var id = Guid.CreateVersion7();
        var memo = $"Deposit paid to {vendor.DisplayName}";
        var entry = await engine.StageAsync(new PostJournalEntryCommand(command.Date,
            [JournalLineDraft.Dr(holding.Id, command.Amount, memo), JournalLineDraft.Cr(paidFrom.Id, command.Amount, memo)],
            command.Memo ?? memo, VendorDeposit.Source, id), ct);
        var deposit = VendorDeposit.Create(id, vendor.Id, command.PurchaseOrderId, command.Date, command.Amount, paidFrom.Id, holding.Id,
            command.Reference, command.Memo, entry.Id, clock.UtcNow);
        store.Add(deposit);
        store.Add(Audit(AuditAction.Created, deposit, null));
        await store.SaveChangesAsync(ct);
        return deposit;
    }

    /// <summary>Uses some of the deposit to pay the vendor's bills, dated on or after it; one transaction.</summary>
    public async Task<IReadOnlyList<BillPayment>> ApplyAsync(Guid id, DateOnly date, IReadOnlyList<ApplyToBill> applications, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(applications);
        await using var transaction = await unitOfWork.BeginAsync(ct);
        var balance = await GetAsync(id, ct) ?? throw new EntityNotFoundException(EntityType, id);
        var deposit = balance.Deposit;
        if (deposit.Status != DocumentStatus.Posted)
            throw new DomainException("vendordeposit.voided", "This deposit is voided.");
        if (date < deposit.Date)
            throw new DomainException("vendordeposit.before_deposit", "A deposit can't pay bills before the date it was paid.");
        var total = applications.Sum(a => a.Amount);
        if (total > balance.Remaining)
            throw new DomainException("vendordeposit.too_much", $"Only {balance.Remaining:N2} of this deposit is left to use.");
        var made = await payments.PayFromDepositAsync(deposit, date, applications, ct);
        store.Add(AuditRecord.Create(clock.UtcNow, user.Actor, AuditAction.Updated, EntityType, deposit.Id, null,
            JsonSerializer.Serialize(new { Applied = total, OnDate = date, Payments = made.Select(p => p.Id).ToList() })));
        await store.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return made;
    }

    public async Task<VendorDeposit> VoidAsync(Guid id, string reason, CancellationToken ct = default)
    {
        var balance = await GetAsync(id, ct) ?? throw new EntityNotFoundException(EntityType, id);
        if (balance.Applied > 0m)
            throw new DomainException("vendordeposit.applied", "Some of this deposit pays bills. Void those payments first.");
        var deposit = await store.GetVendorDepositAsync(id, ct) ?? throw new EntityNotFoundException(EntityType, id);
        var before = Snapshot(deposit);
        deposit.Void();
        await engine.StageVoidAsync(deposit.JournalEntryId, reason, fromDocument: true, ct);
        store.Add(Audit(AuditAction.Voided, deposit, before));
        await store.SaveChangesAsync(ct);
        return deposit;
    }

    private async Task<Account> DepositsAccountAsync(CancellationToken ct) =>
        (await accounts.GetChartAsync(ct)).Ordered(includeInactive: false).Select(x => x.Account)
            .FirstOrDefault(a => a.Type == AccountType.OtherCurrentAsset && string.Equals(a.Name, DepositsAccountName, StringComparison.OrdinalIgnoreCase))
        ?? await accounts.CreateUsualAsync(new CreateAccountCommand(DepositsAccountName, AccountType.OtherCurrentAsset,
            Description: "Money paid to vendors ahead of their bills."), ct);

    private AuditRecord Audit(AuditAction action, VendorDeposit d, string? before) =>
        AuditRecord.Create(clock.UtcNow, user.Actor, action, EntityType, d.Id, before, Snapshot(d));

    private static string Snapshot(VendorDeposit d) => JsonSerializer.Serialize(new
    {
        d.VendorId,
        d.PurchaseOrderId,
        d.Date,
        d.Amount,
        d.PaidFromAccountId,
        d.DepositAccountId,
        d.Reference,
        d.Memo,
        Status = d.Status.ToString(),
        d.JournalEntryId,
    });
}
