using System.Globalization;
using System.Text.Json;
using OpenBook.Application.Accounts;
using OpenBook.Application.Ledger;
using OpenBook.Domain.Accounts;
using OpenBook.Domain.Auditing;
using OpenBook.Domain.Banking;
using OpenBook.Domain.Common;
using OpenBook.Domain.Ledger;

namespace OpenBook.Application.Banking;

public sealed record RecordBankTransactionCommand(
    BankTransactionKind Kind,
    Guid AccountId,
    DateOnly Date,
    IReadOnlyCollection<BankSplit> Splits,
    string? Payee = null,
    string? Reference = null,
    string? Memo = null,
    Guid? VendorId = null,
    Guid? LocationId = null,
    IReadOnlyCollection<Guid>? TagIds = null);

/// <summary>A correction to a posted spend or receive (<see cref="BankingService.ChangeAsync"/>). The account can't change.</summary>
/// <param name="Splits">Every category line the transaction should have.</param>
/// <param name="Reason">Why it's changed; kept in the audit log and on the reversed entry.</param>
/// <param name="Payee">Defaults to the vendor's name when a vendor is given.</param>
/// <param name="TagIds">Null keeps the entry's tags.</param>
public sealed record ChangeBankTransactionCommand(
    DateOnly Date,
    IReadOnlyCollection<BankSplit> Splits,
    string Reason,
    string? Payee = null,
    string? Reference = null,
    string? Memo = null,
    Guid? VendorId = null,
    Guid? LocationId = null,
    IReadOnlyCollection<Guid>? TagIds = null);

/// <summary>A correction to a posted transfer (<see cref="BankingService.ChangeTransferAsync"/>).</summary>
/// <param name="Reason">Why it's changed; kept in the audit log and on the reversed entry.</param>
public sealed record ChangeTransferCommand(Guid FromAccountId, Guid ToAccountId, DateOnly Date, decimal Amount, string Reason, string? Memo = null);

public sealed record TransferCommand(Guid FromAccountId, Guid ToAccountId, DateOnly Date, decimal Amount, string? Memo = null);

public sealed record BankAccountSummary(
    Account Account,
    string FullName,
    decimal Balance,
    decimal ReconciledBalance,
    DateOnly? LastReconciledDate,
    Guid? ReconciliationInProgressId);

public enum LineClearing
{
    None,
    Cleared,
    Reconciled,
}

/// <param name="NaturalAmount">Positive adds to the account's balance (deposit; card charge).</param>
/// <param name="Balance">Running balance after this line, natural sign.</param>
/// <param name="OtherAccountIds">Where the money went or came from.</param>
public sealed record RegisterLine(
    AccountLine Line,
    BankTransaction? Document,
    decimal NaturalAmount,
    decimal Balance,
    LineClearing Clearing,
    IReadOnlyList<Guid> OtherAccountIds);

public sealed record AccountRegister(Account Account, string FullName, decimal Balance, IReadOnlyList<RegisterLine> Lines, int TotalLines);

/// <summary>Checks, deposits, card charges and transfers: each a document that posts one journal entry.</summary>
public sealed class BankingService(
    IBankingStore bank,
    Purchases.IPurchasesStore purchases,
    ILedgerStore ledger,
    AccountService accounts,
    PostingEngine engine,
    Sales.ISalesStore sales,
    IClock clock,
    ICurrentUser user)
{
    internal const string EntityType = "bank_transaction";

    public async Task<IReadOnlyList<BankAccountSummary>> ListAccountsAsync(CancellationToken ct = default)
    {
        var chart = await accounts.GetChartAsync(ct);
        var result = new List<BankAccountSummary>();
        foreach (var (account, _) in chart.Ordered(includeInactive: false).Where(x => BankTransaction.IsBankingAccount(x.Account.Type)))
        {
            // Totals from the database: this runs on every banking page and the home page.
            var (balance, reconciled) = await bank.GetAccountTotalsAsync(account.Id, ct);
            var (lastCompleted, inProgress) = await bank.GetReconciliationStateAsync(account.Id, ct);

            result.Add(new BankAccountSummary(
                account,
                chart.FullName(account.Id),
                BankingRules.Natural(account, balance),
                BankingRules.Natural(account, reconciled),
                lastCompleted,
                inProgress));
        }

        return result;
    }

    /// <param name="take">Newest lines to return; balances are still computed over the whole history.</param>
    public async Task<AccountRegister> GetRegisterAsync(Guid accountId, int take = 500, CancellationToken ct = default)
    {
        var chart = await accounts.GetChartAsync(ct);
        var account = chart.Get(accountId);
        var lines = await bank.GetAccountLinesAsync(accountId, ct);
        var counterparts = await bank.GetCounterpartAccountsAsync(accountId, ct);
        // Only the lines shown need their documents; the balances run over all of them.
        var documents = await bank.GetTransactionsAsync(lines.TakeLast(take).Where(l => l.SourceId is not null).Select(l => l.SourceId!.Value).Distinct().ToList(), ct);
        var reconciliations = await bank.ListReconciliationsAsync(accountId, ct);
        var reconciled = reconciliations.Where(r => r.Status == ReconciliationStatus.Completed).SelectMany(r => r.ClearedLineIds).ToHashSet();
        var cleared = reconciliations.Where(r => r.Status == ReconciliationStatus.InProgress).SelectMany(r => r.ClearedLineIds).ToHashSet();

        var balance = 0m;
        var register = new List<RegisterLine>(lines.Count);
        foreach (var line in lines)
        {
            var natural = BankingRules.Natural(account, line.Amount);
            balance += natural;
            register.Add(new RegisterLine(
                line,
                line.SourceId is { } sourceId ? documents.GetValueOrDefault(sourceId) : null,
                natural,
                balance,
                reconciled.Contains(line.JournalLineId) ? LineClearing.Reconciled
                    : cleared.Contains(line.JournalLineId) ? LineClearing.Cleared
                    : LineClearing.None,
                counterparts.GetValueOrDefault(line.JournalEntryId) ?? []));
        }

        register.Reverse();
        return new AccountRegister(account, chart.FullName(accountId), balance, register.Take(take).ToList(), register.Count);
    }

    public Task<BankTransaction?> GetAsync(Guid id, CancellationToken ct = default) => bank.GetTransactionAsync(id, ct);

    public async Task<BankTransaction> RecordAsync(RecordBankTransactionCommand command, CancellationToken ct = default)
    {
        var transaction = await StageAsync(command, importBatchId: null, ct);
        await bank.SaveChangesAsync(ct);
        return transaction;
    }

    public async Task<BankTransaction> TransferAsync(TransferCommand command, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        var chart = await accounts.GetChartAsync(ct);
        var from = chart.Get(command.FromAccountId);
        var to = chart.Get(command.ToAccountId);
        BankingRules.EnsureBankingAccount(from);
        BankingRules.EnsureBankingAccount(to);

        var id = Guid.CreateVersion7();
        var memo = string.IsNullOrWhiteSpace(command.Memo) ? $"Transfer from {chart.FullName(from.Id)} to {chart.FullName(to.Id)}" : command.Memo;
        var entry = await engine.StageAsync(new PostJournalEntryCommand(
            command.Date,
            BankTransaction.TransferPostings(from.Id, to.Id, command.Amount),
            memo,
            BankTransaction.TransferSource,
            id), ct);

        var transaction = BankTransaction.Create(
            id, BankTransactionKind.Transfer, from.Id, to.Id, command.Date, command.Amount, entry.Id, clock.UtcNow, memo: command.Memo);
        bank.Add(transaction);
        bank.Add(Audit(AuditAction.Created, transaction, before: null));
        await bank.SaveChangesAsync(ct);
        return transaction;
    }

    public async Task<BankTransaction> VoidAsync(Guid id, string reason, CancellationToken ct = default)
    {
        var transaction = await bank.GetTransactionAsync(id, ct) ?? throw new EntityNotFoundException(EntityType, id);
        var before = Snapshot(transaction);
        // Refuses costs already billed to a customer.
        await Sales.BillableExpenseService.ReleaseAsync(sales, Domain.Sales.BillableSource.Expense, transaction.Id, clock, user, ct);
        // Entry next: it refuses reconciled transactions, and the transaction shouldn't change in memory before that.
        await engine.StageVoidAsync(transaction.JournalEntryId, reason, fromDocument: true, ct);
        transaction.MarkVoided();
        bank.Add(Audit(AuditAction.Voided, transaction, before));
        await bank.SaveChangesAsync(ct);
        return transaction;
    }

    /// <summary>
    /// Corrects a posted spend or receive in place, in one save: its entry is voided (the reversal dated the original date)
    /// and a new one posted on its date. It keeps its id, so attachments, notes and the bank feed line it came from stay.
    /// Refused once reconciled or ticked (as a void is), for transfers, and for a cash count's over/short. A transaction
    /// that came from or was matched to a bank feed line keeps its amount (the bank's figure); an issued check (printed,
    /// or sent in a positive pay file) keeps its amount, payee and number. Category lines marked billable to a customer
    /// are pointed at by line number, so they stay as they are, in their place.
    /// </summary>
    public async Task<BankTransaction> ChangeAsync(Guid id, ChangeBankTransactionCommand command, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        // The API may leave them out.
        if (command.Splits is not { Count: > 0 })
            throw new DomainException("bank.no_splits", "Choose at least one category.");
        if (string.IsNullOrWhiteSpace(command.Reason))
            throw new DomainException("bank.change_reason_required", "Say why the transaction is being changed.");

        var transaction = await bank.GetTransactionAsync(id, ct) ?? throw new EntityNotFoundException(EntityType, id);
        if (transaction.Status == BankTransactionStatus.Voided)
            throw new DomainException("bank.voided", "This transaction is voided, so it can't be changed.");
        if (transaction.Kind == BankTransactionKind.Transfer)
            throw new DomainException("bank.transfer_change", "Transfers can't be changed. Void it and record it again.");
        foreach (var fund in (await bank.ListPettyCashFundsAsync(ct)).Where(f => f.AccountId == transaction.AccountId))
        {
            if ((await bank.ListCashCountsAsync(fund.Id, ct)).Any(c => c.AdjustmentTransactionId == transaction.Id))
                throw new DomainException("bank.cash_count", "This is the over or short of a cash count, so it can't be changed.");
        }

        var payee = command.Payee;
        if (command.VendorId is { } vendorId)
        {
            if (transaction.Kind != BankTransactionKind.Spend)
                throw new DomainException("bank.vendor_on_spend_only", "Only payments and charges can name a vendor.");
            var vendor = await purchases.GetVendorAsync(vendorId, ct) ?? throw new EntityNotFoundException("vendor", vendorId);
            if (!vendor.IsActive && vendorId != transaction.VendorId)
                throw new DomainException("vendor.inactive", $"{vendor.DisplayName} is inactive.");
            if (string.IsNullOrWhiteSpace(payee))
                payee = vendor.DisplayName;
        }

        await EnsureNotReceivablesAsync(command.Splits, await SplitAccountsAsync(transaction, ct), ct);
        var postings = BankTransaction.SplitPostings(transaction.Kind, transaction.AccountId, command.Splits);
        var amount = command.Splits.Sum(s => s.Amount);
        var oldEntry = transaction.JournalEntryId;
        var feed = await BankFeedService.TiedToAsync(bank, transaction.AccountId, oldEntry, transaction.Id, ct);
        if (feed.Count > 0 && amount != transaction.Amount)
            throw new DomainException("bank.feed_amount",
                $"This transaction stands for a line from the bank feed, so its amount stays {transaction.Amount:N2}. Undo the feed line first to change it.");
        if (IsIssuedCheck(transaction)
            && (amount != transaction.Amount || !SameText(payee, transaction.Payee) || !SameText(command.Reference, transaction.Reference)))
            throw new DomainException("bank.check_issued",
                "This check has been printed or sent to the bank in a positive pay file, so its amount, payee and number stay. Void it and write a new one instead.");

        var old = await ledger.GetEntryAsync(transaction.JournalEntryId, ct) ?? throw new EntityNotFoundException("journal_entry", transaction.JournalEntryId);
        await EnsureMarkedLinesKeptAsync(transaction, old, postings, ct);
        var tagIds = command.TagIds ?? (await ledger.GetEntryTagsAsync([transaction.JournalEntryId], ct)).Select(t => t.TagId).ToList();
        var before = Snapshot(transaction);

        await engine.StageVoidAsync(transaction.JournalEntryId, $"Transaction changed: {command.Reason.Trim()}", fromDocument: true, keepFeedLines: true, ct);
        var entryMemo = JournalEntry.Fit(string.Join(" · ", new[] { payee, command.Memo }.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s!.Trim())));
        var entry = await engine.StageAsync(new PostJournalEntryCommand(
            command.Date,
            postings,
            entryMemo.Length == 0 ? null : entryMemo,
            BankTransaction.SourceFor(transaction.Kind),
            transaction.Id,
            LocationId: command.LocationId,
            TagIds: tagIds), ct);

        transaction.Change(command.Date, amount, entry.Id, payee, command.Reference, command.Memo, command.VendorId);
        transaction.SetLocation(command.LocationId);
        foreach (var item in feed.Where(i => i.Status == BankFeedStatus.Matched))
            item.FollowEntry(oldEntry, entry.Id);
        bank.Add(AuditRecord.Create(clock.UtcNow, user.Actor, AuditAction.Updated, EntityType, transaction.Id, before,
            DocumentChanges.WithReason(Snapshot(transaction), command.Reason)));
        await bank.SaveChangesAsync(ct);
        return transaction;
    }

    /// <summary>
    /// Corrects a posted transfer in place, in one save: its entry is voided (the reversal dated the original date) and a
    /// new one posted. Refused once either side is reconciled or ticked (as a void is). When a bank feed line stands for
    /// either side, the amount and both accounts stay; matched feed lines follow the new entry.
    /// </summary>
    public async Task<BankTransaction> ChangeTransferAsync(Guid id, ChangeTransferCommand command, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (string.IsNullOrWhiteSpace(command.Reason))
            throw new DomainException("bank.change_reason_required", "Say why the transaction is being changed.");

        var transaction = await bank.GetTransactionAsync(id, ct) ?? throw new EntityNotFoundException(EntityType, id);
        if (transaction.Status == BankTransactionStatus.Voided)
            throw new DomainException("bank.voided", "This transaction is voided, so it can't be changed.");
        if (transaction.Kind != BankTransactionKind.Transfer)
            throw new DomainException("bank.kind_invalid", "Only transfers change this way.");

        var chart = await accounts.GetChartAsync(ct);
        var from = chart.Get(command.FromAccountId);
        var to = chart.Get(command.ToAccountId);
        BankingRules.EnsureBankingAccount(from);
        BankingRules.EnsureBankingAccount(to);
        Money.EnsureCents(command.Amount);
        var postings = BankTransaction.TransferPostings(from.Id, to.Id, command.Amount);

        var oldEntry = transaction.JournalEntryId;
        var feed = new List<BankFeedItem>();
        foreach (var accountId in new[] { transaction.AccountId, transaction.TransferAccountId!.Value })
            feed.AddRange(await BankFeedService.TiedToAsync(bank, accountId, transaction.JournalEntryId, transaction.Id, ct));
        if (feed.Count > 0 && (command.Amount != transaction.Amount || from.Id != transaction.AccountId || to.Id != transaction.TransferAccountId))
            throw new DomainException("bank.feed_amount",
                $"This transfer stands for a line from the bank feed, so its amount ({transaction.Amount:N2}) and accounts stay. Undo the feed line first to change them.");

        var before = Snapshot(transaction);
        var (location, tags) = await engine.SegmentsOfAsync(transaction.JournalEntryId, ct);
        await engine.StageVoidAsync(transaction.JournalEntryId, $"Transfer changed: {command.Reason.Trim()}", fromDocument: true, keepFeedLines: true, ct);
        var memo = string.IsNullOrWhiteSpace(command.Memo) ? $"Transfer from {chart.FullName(from.Id)} to {chart.FullName(to.Id)}" : command.Memo;
        var entry = await engine.StageAsync(new PostJournalEntryCommand(command.Date, postings, memo, BankTransaction.TransferSource, transaction.Id, LocationId: location, TagIds: tags), ct);

        transaction.ChangeTransfer(from.Id, to.Id, command.Date, command.Amount, entry.Id, command.Memo);
        foreach (var item in feed.Where(i => i.Status == BankFeedStatus.Matched))
            item.FollowEntry(oldEntry, entry.Id);
        bank.Add(AuditRecord.Create(clock.UtcNow, user.Actor, AuditAction.Updated, EntityType, transaction.Id, before,
            DocumentChanges.WithReason(Snapshot(transaction), command.Reason)));
        await bank.SaveChangesAsync(ct);
        return transaction;
    }

    /// <summary>A spend's or receive's category lines as they stand, in order, in the form <see cref="ChangeAsync"/> takes.</summary>
    public async Task<IReadOnlyList<BankSplit>> SplitsAsync(BankTransaction transaction, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        var entry = await ledger.GetEntryAsync(transaction.JournalEntryId, ct) ?? throw new EntityNotFoundException("journal_entry", transaction.JournalEntryId);
        var classes = (await ledger.GetLineSegmentsAsync([entry.Id], ct)).ToDictionary(s => s.JournalLineId, s => s.ClassId);
        var sign = transaction.Kind == BankTransactionKind.Spend ? 1m : -1m;
        return entry.Lines.Where(l => l.LineNumber > 1).OrderBy(l => l.LineNumber)
            .Select(l => new BankSplit(l.AccountId, sign * l.Amount, l.Memo, classes.GetValueOrDefault(l.Id)))
            .ToList();
    }

    /// <summary>The transaction's corrections, oldest first.</summary>
    public Task<IReadOnlyList<DocumentChange>> ChangesAsync(Guid id, CancellationToken ct = default) =>
        DocumentChanges.ListAsync(ledger, EntityType, id, ct);

    /// <summary>A check the bank already knows of: printed, or sent in a positive pay file.</summary>
    private static bool IsIssuedCheck(BankTransaction transaction) =>
        transaction.Kind == BankTransactionKind.Spend && (transaction.PrintedAt is not null || transaction.PositivePaySentAt is not null);

    private static bool SameText(string? a, string? b) =>
        string.Equals(string.IsNullOrWhiteSpace(a) ? null : a.Trim(), string.IsNullOrWhiteSpace(b) ? null : b.Trim(), StringComparison.Ordinal);

    /// <summary>
    /// An expense line marked billable is pointed at by its journal line number (the bank line is line 1, the categories
    /// follow in order), so it must still be there, unchanged, at the same place (remove the mark first to change it).
    /// </summary>
    private async Task EnsureMarkedLinesKeptAsync(BankTransaction transaction, JournalEntry old, IReadOnlyList<JournalLineDraft> postings, CancellationToken ct)
    {
        if (transaction.Kind != BankTransactionKind.Spend)
            return;
        foreach (var mark in await sales.ListBillableForDocumentAsync(Domain.Sales.BillableSource.Expense, transaction.Id, ct))
        {
            var was = old.Lines.FirstOrDefault(l => l.LineNumber == mark.LineNumber);
            var now = mark.LineNumber >= 1 && mark.LineNumber <= postings.Count ? postings[mark.LineNumber - 1] : null;
            if (was is null || now is not null && (now.AccountId, now.Debit, now.Credit) == (was.AccountId, was.Debit, was.Credit) && SameText(now.Memo, was.Memo))
                continue;
            throw new DomainException("bank.line_locked",
                $"Line {mark.LineNumber - 1} ({was.Memo ?? mark.Description}) is marked billable to a customer, so it has to stay as it is, in its place. Remove the mark first to change it.");
        }
    }

    /// <summary>One more than the highest numeric check number used on the account, or null if none.</summary>
    public async Task<string?> SuggestCheckNumberAsync(Guid accountId, CancellationToken ct = default)
    {
        var numbers = (await bank.ListSpendReferencesAsync(accountId, ct))
            .Select(r => long.TryParse(r, NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : (long?)null)
            .Where(n => n is not null)
            .ToList();
        return numbers.Count == 0 ? null : (numbers.Max()!.Value + 1).ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// A/R and A/P move only with invoices and bills, or the agings stop tying to them and a bill stays open after it's
    /// been paid. An older line is left alone, so a correction of something else still goes through.
    /// </summary>
    private async Task EnsureNotReceivablesAsync(IEnumerable<BankSplit> splits, IReadOnlySet<Guid>? kept, CancellationToken ct)
    {
        var ids = splits.Select(s => s.AccountId).Where(id => kept is null || !kept.Contains(id)).Distinct().ToList();
        if ((await ledger.GetAccountsAsync(ids, ct)).Values.FirstOrDefault(a => a.Type is AccountType.AccountsReceivable or AccountType.AccountsPayable) is { } account)
            throw new DomainException("bank.category_receivable", account.Type == AccountType.AccountsPayable
                ? $"'{account.Name}' moves only with bills: use Pay bills to pay one."
                : $"'{account.Name}' moves only with invoices: use Receive payment for money from a customer.");
    }

    private async Task<IReadOnlySet<Guid>> SplitAccountsAsync(BankTransaction transaction, CancellationToken ct) =>
        (await SplitsAsync(transaction, ct)).Select(s => s.AccountId).ToHashSet();

    /// <summary>Adds the document and its entry without saving; imports stage many and save once.</summary>
    internal async Task<BankTransaction> StageAsync(RecordBankTransactionCommand command, Guid? importBatchId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (command.Kind == BankTransactionKind.Transfer)
            throw new DomainException("bank.kind_invalid", "Record transfers with TransferAsync.");

        var account = (await ledger.GetAccountsAsync([command.AccountId], ct)).GetValueOrDefault(command.AccountId)
            ?? throw new EntityNotFoundException("account", command.AccountId);
        BankingRules.EnsureBankingAccount(account);

        // An expense can name a vendor; the payee defaults to their name.
        var payee = command.Payee;
        if (command.VendorId is { } vendorId)
        {
            var vendor = await purchases.GetVendorAsync(vendorId, ct) ?? throw new EntityNotFoundException("vendor", vendorId);
            if (!vendor.IsActive)
                throw new DomainException("vendor.inactive", $"{vendor.DisplayName} is inactive.");
            if (string.IsNullOrWhiteSpace(payee))
                payee = vendor.DisplayName;
        }

        await EnsureNotReceivablesAsync(command.Splits, null, ct);
        var postings = BankTransaction.SplitPostings(command.Kind, account.Id, command.Splits);
        var id = Guid.CreateVersion7();
        var entryMemo = JournalEntry.Fit(string.Join(" · ", new[] { payee, command.Memo }.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s!.Trim())));
        var entry = await engine.StageAsync(new PostJournalEntryCommand(
            command.Date,
            postings,
            entryMemo.Length == 0 ? null : entryMemo,
            BankTransaction.SourceFor(command.Kind),
            id,
            LocationId: command.LocationId,
            TagIds: command.TagIds), ct);

        var transaction = BankTransaction.Create(
            id,
            command.Kind,
            account.Id,
            transferAccountId: null,
            command.Date,
            command.Splits.Sum(s => s.Amount),
            entry.Id,
            clock.UtcNow,
            payee,
            command.Reference,
            command.Memo,
            importBatchId,
            command.VendorId);
        transaction.SetLocation(command.LocationId);
        bank.Add(transaction);
        bank.Add(Audit(AuditAction.Created, transaction, before: null));
        return transaction;
    }

    private AuditRecord Audit(AuditAction action, BankTransaction t, string? before) =>
        AuditRecord.Create(clock.UtcNow, user.Actor, action, EntityType, t.Id, before, Snapshot(t));

    private static string Snapshot(BankTransaction t) => JsonSerializer.Serialize(new
    {
        Kind = t.Kind.ToString(),
        t.AccountId,
        t.TransferAccountId,
        t.Date,
        t.Payee,
        t.Reference,
        t.Memo,
        t.Amount,
        Status = t.Status.ToString(),
        t.JournalEntryId,
        t.ImportBatchId,
        t.VendorId,
    });
}
