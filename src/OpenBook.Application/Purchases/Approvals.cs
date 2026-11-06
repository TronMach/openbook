using System.Text.Json;
using OpenBook.Application.Companies;
using OpenBook.Application.Forms;
using OpenBook.Domain.Accounts;
using OpenBook.Domain.Auditing;
using OpenBook.Domain.Common;
using OpenBook.Domain.Purchases;

namespace OpenBook.Application.Purchases;

/// <param name="ReceiveIntoStock">Null keeps the current setting.</param>
public sealed record PurchasingSettingsCommand(decimal? ApprovalThreshold, decimal PriceTolerancePercent, bool RequireReceipt, bool AllowSelfApproval, bool? ReceiveIntoStock = null, bool? Net1099Refunds = null);

/// <summary>
/// Bill approvals: bills at or over the threshold, or with 3-way match differences, wait for an accountant's approval
/// before they can be paid. The person who entered a bill can't approve it unless the settings allow it.
/// </summary>
public sealed class BillApprovalService(IPurchasesStore store, ICompanyStore companies, IClock clock, ICurrentUser user)
{
    public const string SettingsEntity = "purchasing_settings";

    public async Task<PurchasingSettings> GetSettingsAsync(CancellationToken ct = default) =>
        await store.GetPurchasingSettingsAsync(ct) ?? PurchasingSettings.Create();

    public async Task<PurchasingSettings> SaveSettingsAsync(PurchasingSettingsCommand command, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        var company = await companies.GetRequiredCompanyAsync(ct);
        var settings = await store.GetPurchasingSettingsAsync(ct);
        if (settings is null)
        {
            settings = PurchasingSettings.Create();
            store.Add(settings);
        }

        var before = Snapshot(settings);
        settings.Update(command.ApprovalThreshold, command.PriceTolerancePercent, command.RequireReceipt, command.AllowSelfApproval, command.ReceiveIntoStock, command.Net1099Refunds);
        store.Add(AuditRecord.Create(clock.UtcNow, user.Actor, AuditAction.Updated, SettingsEntity, company.Id, before, Snapshot(settings)));
        await store.SaveChangesAsync(ct);
        return settings;
    }

    /// <summary>Bills waiting for approval (and rejected ones not voided yet), oldest due first.</summary>
    public async Task<IReadOnlyList<Bill>> PendingAsync(CancellationToken ct = default) =>
        (await store.ListBillsAsync(null, ct))
            .Where(b => b.Status == DocumentStatus.Posted && b.Approval is BillApproval.Pending or BillApproval.Rejected)
            .OrderBy(b => b.Approval).ThenBy(b => b.DueDate).ToList();

    public Task<Bill> ApproveAsync(Guid billId, string? note, CancellationToken ct = default) =>
        DecideAsync(billId, (bill, by, at) => bill.Approve(by, at, note), ct);

    public Task<Bill> RejectAsync(Guid billId, string? note, CancellationToken ct = default) =>
        DecideAsync(billId, (bill, by, at) => bill.Reject(by, at, note), ct);

    private async Task<Bill> DecideAsync(Guid billId, Action<Bill, string, DateTimeOffset> decide, CancellationToken ct)
    {
        var bill = await store.GetBillAsync(billId, ct) ?? throw new EntityNotFoundException("bill", billId);
        var settings = await GetSettingsAsync(ct);
        if (!settings.AllowSelfApproval && string.Equals(bill.EnteredBy, user.Actor, StringComparison.OrdinalIgnoreCase))
            throw new DomainException("approval.own_bill", "You entered this bill, so someone else has to approve or reject it.");
        var before = BillService.Snapshot(bill);
        decide(bill, user.Actor, clock.UtcNow);
        store.Add(BillService.Audit(clock, user, AuditAction.Updated, bill, before));
        await store.SaveChangesAsync(ct);
        return bill;
    }

    private static string Snapshot(PurchasingSettings s) => JsonSerializer.Serialize(new
    {
        s.ApprovalThreshold,
        s.PriceTolerancePercent,
        s.RequireReceipt,
        s.AllowSelfApproval,
        s.ReceiveIntoStock,
        s.Net1099Refunds,
    });
}

public sealed record VendorBankAccountCommand(string RoutingNumber, string AccountNumber, BankAccountKind Kind);

public sealed record AchSettingsCommand(
    string DestinationRouting, string DestinationName, string OriginId, string OriginName, string CompanyId, string CompanyName, string? EntryDescription = null);

/// <summary>A bill payment that can go in an ACH file: the vendor has bank details and it hasn't been sent or printed as a check.</summary>
public sealed record AchCandidate(BillPayment Payment, Vendor Vendor, VendorBankAccount BankAccount);

/// <summary>
/// ACH (NACHA) files for paying vendors: vendors' bank details (account numbers encrypted, shown as last four), the
/// bank's settings per bank account, and files of chosen bill payments, each payment sent once.
/// </summary>
public sealed class AchService(IPurchasesStore store, Accounts.AccountService accounts, ILedgerStore ledger, Banking.IBankingStore bank, ISecretProtector secrets, IClock clock, ICurrentUser user)
{
    public const string BatchEntity = "ach_batch";
    public const string SettingsEntity = "ach_settings";

    public Task<VendorBankAccount?> GetVendorBankAccountAsync(Guid vendorId, CancellationToken ct = default) => store.GetVendorBankAccountAsync(vendorId, ct);

    public async Task<VendorBankAccount> SetVendorBankAccountAsync(Guid vendorId, VendorBankAccountCommand command, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        var vendor = await store.GetVendorAsync(vendorId, ct) ?? throw new EntityNotFoundException("vendor", vendorId);
        var number = Ach.AccountNumber(command.AccountNumber);
        var existing = await store.GetVendorBankAccountAsync(vendor.Id, ct);
        var before = existing is null ? null : Describe(existing);
        if (existing is null)
        {
            existing = VendorBankAccount.Create(vendor.Id, command.RoutingNumber, number, secrets.Protect(number), command.Kind, clock.UtcNow);
            store.Add(existing);
        }
        else
            existing.Update(command.RoutingNumber, number, secrets.Protect(number), command.Kind, clock.UtcNow);

        // The vendor's history shows the change, never the full account number.
        store.Add(AuditRecord.Create(clock.UtcNow, user.Actor, AuditAction.Updated, "vendor", vendor.Id, before, Describe(existing)));
        await store.SaveChangesAsync(ct);
        return existing;
    }

    public async Task RemoveVendorBankAccountAsync(Guid vendorId, CancellationToken ct = default)
    {
        var existing = await store.GetVendorBankAccountAsync(vendorId, ct) ?? throw new EntityNotFoundException("vendor_bank_account", vendorId);
        store.Remove(existing);
        store.Add(AuditRecord.Create(clock.UtcNow, user.Actor, AuditAction.Updated, "vendor", vendorId, Describe(existing), JsonSerializer.Serialize(new { BankAccount = (string?)null })));
        await store.SaveChangesAsync(ct);
    }

    public Task<AchSettings?> GetSettingsAsync(Guid accountId, CancellationToken ct = default) => store.GetAchSettingsAsync(accountId, ct);

    public async Task<AchSettings> SaveSettingsAsync(Guid accountId, AchSettingsCommand command, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        await BankAccountAsync(accountId, ct);
        var settings = await store.GetAchSettingsAsync(accountId, ct);
        var before = settings is null ? null : JsonSerializer.Serialize(settings);
        if (settings is null)
        {
            settings = AchSettings.For(accountId);
            store.Add(settings);
        }

        settings.Update(command.DestinationRouting, command.DestinationName, command.OriginId, command.OriginName, command.CompanyId, command.CompanyName,
            command.EntryDescription);
        store.Add(AuditRecord.Create(clock.UtcNow, user.Actor, AuditAction.Updated, SettingsEntity, accountId, before, JsonSerializer.Serialize(settings)));
        await store.SaveChangesAsync(ct);
        return settings;
    }

    /// <summary>
    /// Posted bill payments from the account to vendors with bank details, not yet sent by ACH or printed as checks, and
    /// not already through the bank (matched to a bank line, or reconciled): sending those would pay them twice.
    /// </summary>
    public async Task<IReadOnlyList<AchCandidate>> CandidatesAsync(Guid accountId, CancellationToken ct = default)
    {
        var banks = (await store.ListVendorBankAccountsAsync(ct)).ToDictionary(b => b.VendorId);
        var vendors = (await store.ListVendorsAsync(ct)).ToDictionary(v => v.Id);
        var cleared = (await bank.ListFeedItemsAsync(accountId, ct)).Where(i => i.MatchedJournalEntryId is not null).Select(i => i.MatchedJournalEntryId!.Value).ToHashSet();
        var ready = (await store.ListPaymentsAsync(null, ct))
            .Where(p => p.PaymentAccountId == accountId && p.Status == DocumentStatus.Posted && p.AchBatchId is null && p.PrintedAt is null
                && banks.ContainsKey(p.VendorId) && !cleared.Contains(p.JournalEntryId))
            .OrderBy(p => p.Date).ThenBy(p => vendors[p.VendorId].DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        var candidates = new List<AchCandidate>();
        foreach (var payment in ready)
        {
            if (!await ledger.IsInReconciliationAsync(payment.JournalEntryId, ct))
                candidates.Add(new AchCandidate(payment, vendors[payment.VendorId], banks[payment.VendorId]));
        }

        return candidates;
    }

    public Task<IReadOnlyList<AchBatch>> ListBatchesAsync(Guid accountId, CancellationToken ct = default) => store.ListAchBatchesAsync(accountId, ct);

    public Task<AchBatch?> GetBatchAsync(Guid id, CancellationToken ct = default) => store.GetAchBatchAsync(id, ct);

    /// <summary>The file itself, to send to the bank.</summary>
    public string FileText(AchBatch batch)
    {
        ArgumentNullException.ThrowIfNull(batch);
        return batch.ProtectedContent is not { } file
            ? batch.UnprotectedContent
            : secrets.Unprotect(file) ?? throw new DomainException("ach.file_unreadable", "This ACH file can't be read on this computer, because the keys it was encrypted with aren't here. Restore those keys, or make the file again at the bank.");
    }

    /// <summary>
    /// Encrypts files stored in clear (from before they were encrypted, or a restored backup), then has the database
    /// wipe what they left behind, which a backup would otherwise copy. Only their form changes, so there's nothing to audit.
    /// </summary>
    /// <returns>How many it encrypted.</returns>
    public async Task<int> ProtectStoredFilesAsync(CancellationToken ct = default)
    {
        var files = await store.ListUnprotectedAchBatchesAsync(ct);
        if (files.Count == 0)
            return 0;
        foreach (var file in files)
            file.Protect(secrets.Protect(file.UnprotectedContent));
        await store.SaveChangesAsync(ct);
        await store.WipeDeletedAchFilesAsync(ct);
        return files.Count;
    }

    /// <summary>Builds the file for the chosen payments and marks them sent; the file is kept on the batch.</summary>
    public async Task<AchBatch> CreateFileAsync(Guid accountId, DateOnly effectiveDate, IReadOnlyCollection<Guid> paymentIds, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(paymentIds);
        await BankAccountAsync(accountId, ct);
        var settings = await store.GetAchSettingsAsync(accountId, ct)
            ?? throw new DomainException("ach.not_set_up", "Enter the bank's ACH details for this account first.");
        if (effectiveDate < CompanyTime.TodayAt(clock.UtcNow, CompanyTime.Zone))
            throw new DomainException("ach.date_past", "The effective date can't be in the past.");
        if (effectiveDate.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
            throw new DomainException("ach.date_weekend", "Banks don't settle on weekends. Choose a weekday.");
        var candidates = (await CandidatesAsync(accountId, ct)).ToDictionary(c => c.Payment.Id);
        if (paymentIds.Count == 0)
            throw new DomainException("ach.no_entries", "Choose at least one payment.");
        var chosen = paymentIds.Distinct().Select(id => candidates.GetValueOrDefault(id)
            ?? throw new DomainException("ach.payment_not_eligible", "One of those payments can't go by ACH: it's from another account, already sent, printed as a check, voided, or its vendor has no bank details."))
            .ToList();

        var entries = chosen.Select(c => new AchEntry(
            c.BankAccount.RoutingNumber,
            secrets.Unprotect(c.BankAccount.ProtectedAccountNumber)
                ?? throw new DomainException("ach.account_unreadable", $"{c.Vendor.DisplayName}'s bank account number can't be read on this computer. Enter it again."),
            c.BankAccount.Kind,
            c.Payment.Amount,
            c.Vendor.AccountNumber ?? c.Vendor.Id.ToString("N")[..15],
            c.Vendor.CompanyName ?? c.Vendor.DisplayName)).ToList();

        var now = clock.UtcNow;
        var today = CompanyTime.TodayAt(now, CompanyTime.Zone);
        // The bank tells a day's files apart by origin and modifier, so accounts sharing an origin count together.
        var sentToday = 0;
        foreach (var sharing in await store.ListAccountsSharingOriginAsync(settings, ct))
            sentToday += (await store.ListAchBatchesAsync(sharing, ct)).Count(b => CompanyTime.TodayAt(b.CreatedAt, CompanyTime.Zone) == today);
        var modifier = sentToday < 26 ? (char)('A' + sentToday) : sentToday < 36 ? (char)('0' + sentToday - 26)
            : throw new DomainException("ach.too_many_files", "A bank account can send at most 36 ACH files a day.");
        var content = Ach.Build(settings, entries, effectiveDate, now.InCompanyTime().DateTime, modifier);

        var batch = AchBatch.Create(accountId, effectiveDate, modifier, entries.Count, entries.Sum(e => e.Amount), secrets.Protect(content), now);
        store.Add(batch);
        // The candidates were read untracked; mark the tracked payments.
        foreach (var c in chosen)
            (await store.GetPaymentAsync(c.Payment.Id, ct))!.MarkSentByAch(batch.Id);
        store.Add(AuditRecord.Create(now, user.Actor, AuditAction.Created, BatchEntity, batch.Id, null, JsonSerializer.Serialize(new
        {
            batch.AccountId,
            batch.EffectiveDate,
            batch.EntryCount,
            batch.Total,
            Payments = chosen.Select(c => c.Payment.Id),
        })));
        await store.SaveChangesAsync(ct);
        return batch;
    }

    private async Task<Account> BankAccountAsync(Guid accountId, CancellationToken ct)
    {
        var account = (await accounts.GetChartAsync(ct)).Get(accountId);
        return account.Type == AccountType.Bank
            ? account
            : throw new DomainException("ach.bank_account_required", $"'{account.Name}' isn't a bank account; ACH payments come out of a bank account.");
    }

    private static string Describe(VendorBankAccount a) => JsonSerializer.Serialize(new
    {
        BankAccount = $"{a.Kind} ending {a.AccountLast4}",
        a.RoutingNumber,
    });
}
