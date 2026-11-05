using System.Text.Json;
using OpenBook.Application.Accounts;
using OpenBook.Application.Import;
using OpenBook.Domain.Auditing;
using OpenBook.Domain.Banking;
using OpenBook.Domain.Common;
using OpenBook.Domain.Ledger;

namespace OpenBook.Application.Banking;

/// <summary>A statement line to put in the review queue. Positive is money in.</summary>
public sealed record FeedLine(DateOnly Date, decimal Amount, string? Payee, string? Reference, string? Memo, string? FitId = null);

/// <param name="Queued">New lines now waiting for review.</param>
/// <param name="AutoAdded">Lines a rule added to the books straight away.</param>
/// <param name="Skipped">Lines already imported before.</param>
public sealed record FeedImportResult(int Queued, int AutoAdded, int Skipped);

/// <summary>What a rule says to do with a line.</summary>
public sealed record FeedSuggestion(Guid CategoryAccountId, string? Payee, Guid? VendorId, BankRule Rule);

/// <summary>A transaction already in the books that looks like the line: same amount, within a week.</summary>
public sealed record FeedMatch(Guid JournalEntryId, long EntryNumber, DateOnly Date, decimal Amount, string Description, string Source, Guid? SourceId);

public sealed record FeedReviewItem(BankFeedItem Item, FeedSuggestion? Suggestion, IReadOnlyList<FeedMatch> Matches);

/// <summary>Add a line as a new spend or receive in this category.</summary>
public sealed record AddFeedItemCommand(Guid CategoryAccountId, string? Payee = null, Guid? VendorId = null, string? Memo = null);

/// <summary>
/// The bank feed: imported statement lines wait in a review queue, where each is added as a new transaction,
/// matched to one already in the books, or excluded. Bank rules suggest categories (or add lines outright).
/// Re-importing the same file skips lines already in the queue.
/// </summary>
public sealed class BankFeedService(
    IBankingStore bank,
    AccountService accounts,
    BankingService banking,
    BankImportService csvImport,
    IClock clock,
    ICurrentUser user)
{
    private const string EntityType = "bank_feed_item";
    public const int MatchWindowDays = 7;

    /// <summary>Reads a CSV or OFX/QFX/QBO statement and flags lines already imported or already in the books.</summary>
    public async Task<BankImportPreview> PreviewAsync(Guid accountId, string text, BankImportOptions options, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(text);
        BankImportPreview preview;
        if (Ofx.LooksLikeOfx(text))
        {
            BankingRules.EnsureBankingAccount((await accounts.GetChartAsync(ct)).Get(accountId));
            var statement = Ofx.Parse(text);
            if (statement.Transactions.Count > BankImportService.MaxRows)
                throw new DomainException("import.too_many_rows", $"Import at most {BankImportService.MaxRows} transactions at a time.");
            var rows = statement.Transactions.Select((t, i) => new BankImportRow(
                i + 1, t.Date, t.Amount, t.Name, t.CheckNumber, t.Memo, false, t.Amount == 0m ? "The amount is zero." : null, t.FitId)).ToList();
            rows = await csvImport.FlagDuplicatesAsync(accountId, rows, ct);
            var used = new List<string> { "OFX statement" };
            if (statement.AccountId is { } number)
                used.Add($"Account ending {Last4(number)}");
            if (statement.LedgerBalance is { } balance)
                used.Add(FormattableString.Invariant($"Statement balance {balance:N2}{(statement.LedgerBalanceDate is { } d ? $" on {d:yyyy-MM-dd}" : "")}"));
            preview = new BankImportPreview(rows, DateOrder.YearFirst, false, false, used);
        }
        else
        {
            preview = await csvImport.PreviewAsync(accountId, text, options, ct);
        }

        var known = await KnownKeysAsync(accountId, ct);
        var seen = new Dictionary<string, int>(StringComparer.Ordinal);
        return preview with
        {
            Rows = preview.Rows.Select(r =>
            {
                if (r.Date is not { } date || r.Amount is not { } amount)
                    return r;
                var key = BankFeedItem.KeyFor(date, amount, r.Payee, r.Reference, r.FitId, Occurrence(seen, BankFeedItem.KeyFor(date, amount, r.Payee, r.Reference, r.FitId)));
                return known.Contains(key) ? r with { AlreadyImported = true } : r;
            }).ToList(),
        };
    }

    /// <summary>Queues new lines (skipping ones imported before) and runs the bank rules over them.</summary>
    public async Task<FeedImportResult> QueueAsync(Guid accountId, IReadOnlyCollection<FeedLine> lines, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(lines);
        BankingRules.EnsureBankingAccount((await accounts.GetChartAsync(ct)).Get(accountId));
        if (lines.Count == 0)
            throw new DomainException("import.nothing_selected", "Choose at least one transaction to import.");
        if (lines.Count > BankImportService.MaxRows)
            throw new DomainException("import.too_many_rows", $"Import at most {BankImportService.MaxRows} transactions at a time.");

        var known = await KnownKeysAsync(accountId, ct);
        var batchId = Guid.CreateVersion7();
        var rules = await ActiveRulesAsync(ct);
        int queued = 0, autoAdded = 0, skipped = 0;
        var seen = new Dictionary<string, int>(StringComparer.Ordinal);
        List<FeedMatch>? inTheBooks = null;
        foreach (var line in lines)
        {
            var occurrence = Occurrence(seen, BankFeedItem.KeyFor(line.Date, line.Amount, line.Payee, line.Reference, line.FitId));
            var item = BankFeedItem.Create(accountId, line.Date, line.Amount, line.Payee, line.Reference, line.Memo, line.FitId, batchId, clock.UtcNow, occurrence);
            if (!known.Add(item.DedupeKey))
            {
                skipped++;
                continue;
            }

            bank.Add(item);
            var rule = rules.FirstOrDefault(r => r.Fits(item));
            item.SuggestedBy(rule?.Id);
            // A line that looks like something already entered waits for review, as the page's bulk Add holds it back:
            // added, it would be in the books twice.
            inTheBooks ??= rule is { AutoAdd: true } ? await MatchCandidatesAsync(accountId, ct) : null;
            if (rule is { AutoAdd: true } && !inTheBooks!.Any(c => c.Amount == item.Amount && Math.Abs(c.Date.DayNumber - item.Date.DayNumber) <= MatchWindowDays))
            {
                var transaction = await banking.StageAsync(Command(item, new AddFeedItemCommand(rule.CategoryAccountId, rule.Payee, rule.VendorId)), batchId, ct);
                item.MarkAdded(transaction.Id, rule.Id, clock.UtcNow, user.Actor);
                autoAdded++;
            }
            else
            {
                queued++;
            }
        }

        bank.Add(AuditRecord.Create(clock.UtcNow, user.Actor, AuditAction.Created, "bank_feed_import", batchId, null,
            JsonSerializer.Serialize(new { AccountId = accountId, Queued = queued, AutoAdded = autoAdded, Skipped = skipped })));
        await bank.SaveChangesAsync(ct);
        return new FeedImportResult(queued, autoAdded, skipped);
    }

    /// <summary>The queue for an account, oldest first, with rule suggestions and possible matches.</summary>
    public async Task<IReadOnlyList<FeedReviewItem>> ReviewAsync(Guid accountId, BankFeedStatus status = BankFeedStatus.Pending, CancellationToken ct = default)
    {
        var items = (await bank.ListFeedItemsAsync(accountId, ct)).Where(i => i.Status == status).OrderBy(i => i.Date).ThenBy(i => i.Id).ToList();
        if (items.Count == 0)
            return [];
        var rules = await ActiveRulesAsync(ct);
        var candidates = await MatchCandidatesAsync(accountId, ct);
        return items.Select(item =>
        {
            var rule = item.Status == BankFeedStatus.Pending ? rules.FirstOrDefault(r => r.Fits(item)) : null;
            var suggestion = rule is null ? null : new FeedSuggestion(rule.CategoryAccountId, rule.Payee, rule.VendorId, rule);
            var matches = item.Status == BankFeedStatus.Pending
                ? candidates.Where(c => c.Amount == item.Amount && Math.Abs(c.Date.DayNumber - item.Date.DayNumber) <= MatchWindowDays)
                    .OrderBy(c => Math.Abs(c.Date.DayNumber - item.Date.DayNumber)).Take(5).ToList()
                : [];
            return new FeedReviewItem(item, suggestion, matches);
        }).ToList();
    }

    /// <summary>
    /// Where a feed line (by its id) or an import (by its batch id, as the audit log names them) is reviewed: its
    /// account and the line's status. Null when there's neither, or an import queued no lines.
    /// </summary>
    public async Task<(Guid AccountId, BankFeedStatus Status)?> LocateAsync(Guid id, CancellationToken ct = default)
    {
        if (await bank.GetFeedItemAsync(id, ct) is { } item)
            return (item.AccountId, item.Status);
        return await bank.FindFeedItemInBatchAsync(id, ct) is { } first ? (first.AccountId, BankFeedStatus.Pending) : null;
    }

    public async Task<int> PendingCountAsync(Guid accountId, CancellationToken ct = default) =>
        (await bank.ListFeedItemsAsync(accountId, ct)).Count(i => i.Status == BankFeedStatus.Pending);

    /// <summary>Records the line as a new spend (money out) or receive (money in) in the chosen category.</summary>
    public async Task<BankTransaction> AddAsync(Guid itemId, AddFeedItemCommand command, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        var item = await PendingAsync(itemId, ct);
        var before = Snapshot(item);
        var transaction = await banking.StageAsync(Command(item, command), item.ImportBatchId, ct);
        item.MarkAdded(transaction.Id, ruleId: null, clock.UtcNow, user.Actor);
        bank.Add(Audit(item, before));
        await bank.SaveChangesAsync(ct);
        return transaction;
    }

    /// <summary>The line is already in the books (a deposit, a check, a bill payment): link it rather than adding it again.</summary>
    public async Task MatchAsync(Guid itemId, Guid journalEntryId, CancellationToken ct = default)
    {
        var item = await PendingAsync(itemId, ct);
        var candidate = (await MatchCandidatesAsync(item.AccountId, ct)).FirstOrDefault(c => c.JournalEntryId == journalEntryId)
            ?? throw new DomainException("feed.match_not_found", "That transaction isn't on this account, is voided, or is already matched.");
        if (candidate.Amount != item.Amount)
            throw new DomainException("feed.match_amount", $"That transaction is for {candidate.Amount:N2}, not {item.Amount:N2}.");

        var before = Snapshot(item);
        item.MarkMatched(journalEntryId, clock.UtcNow, user.Actor);
        bank.Add(Audit(item, before));
        await bank.SaveChangesAsync(ct);
    }

    public async Task ExcludeAsync(Guid itemId, CancellationToken ct = default)
    {
        var item = await PendingAsync(itemId, ct);
        var before = Snapshot(item);
        item.Exclude(clock.UtcNow, user.Actor);
        bank.Add(Audit(item, before));
        await bank.SaveChangesAsync(ct);
    }

    /// <summary>Puts a reviewed line back in the queue. If it was added, its transaction is voided first.</summary>
    public async Task ReopenAsync(Guid itemId, CancellationToken ct = default)
    {
        var item = await bank.GetFeedItemAsync(itemId, ct) ?? throw new EntityNotFoundException(EntityType, itemId);
        if (item.Status == BankFeedStatus.Added && item.BankTransactionId is { } transactionId
            && await bank.GetTransactionAsync(transactionId, ct) is { Status: BankTransactionStatus.Posted })
            await banking.VoidAsync(transactionId, "Sent back to the bank feed for review", ct);

        var before = Snapshot(item);
        item.Reopen();
        bank.Add(Audit(item, before));
        await bank.SaveChangesAsync(ct);
    }

    private static RecordBankTransactionCommand Command(BankFeedItem item, AddFeedItemCommand command) => new(
        item.IsMoneyIn ? BankTransactionKind.Receive : BankTransactionKind.Spend,
        item.AccountId,
        item.Date,
        [new BankSplit(command.CategoryAccountId, Math.Abs(item.Amount))],
        Clean(command.Payee) ?? (command.VendorId is null ? item.Payee : null),
        item.Reference,
        Clean(command.Memo) ?? item.Memo,
        item.IsMoneyIn ? null : command.VendorId);

    private async Task<BankFeedItem> PendingAsync(Guid itemId, CancellationToken ct)
    {
        var item = await bank.GetFeedItemAsync(itemId, ct) ?? throw new EntityNotFoundException(EntityType, itemId);
        return item.Status == BankFeedStatus.Pending ? item : throw new DomainException("feed.not_pending", "This item has already been reviewed.");
    }

    private async Task<HashSet<string>> KnownKeysAsync(Guid accountId, CancellationToken ct) =>
        (await bank.ListFeedItemsAsync(accountId, ct)).Select(i => i.DedupeKey).ToHashSet(StringComparer.Ordinal);

    /// <summary>
    /// The account's feed lines standing for a document about to be corrected: matched to its entry, or added as the
    /// bank transaction <paramref name="transactionId"/>. They're what the bank reported, so the document's money in
    /// the account has to stay the same; matched ones then follow the new entry (<see cref="BankFeedItem.FollowEntry"/>).
    /// </summary>
    internal static async Task<IReadOnlyList<BankFeedItem>> TiedToAsync(IBankingStore bank, Guid accountId, Guid entryId, Guid? transactionId, CancellationToken ct) =>
        (await bank.ListFeedItemsAsync(accountId, ct))
            .Where(i => i.Status == BankFeedStatus.Matched && i.MatchedJournalEntryId == entryId
                || i.Status == BankFeedStatus.Added && transactionId is not null && i.BankTransactionId == transactionId)
            .ToList();

    private async Task<List<BankRule>> ActiveRulesAsync(CancellationToken ct) =>
        (await bank.ListRulesAsync(ct)).Where(r => r.IsActive).OrderBy(r => r.Priority).ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>
    /// Posted entries touching the account and not yet matched to a feed line. The amount is from the
    /// holder's view, which is the debit-positive ledger amount for both banks and cards.
    /// </summary>
    private async Task<List<FeedMatch>> MatchCandidatesAsync(Guid accountId, CancellationToken ct)
    {
        var chart = await accounts.GetChartAsync(ct);
        var matched = (await bank.ListFeedItemsAsync(accountId, ct))
            .Where(i => i.Status == BankFeedStatus.Matched && i.MatchedJournalEntryId is not null).Select(i => i.MatchedJournalEntryId!.Value)
            .Concat((await bank.ListFeedItemsAsync(accountId, ct)).Where(i => i.Status == BankFeedStatus.Added && i.BankTransactionId is not null)
                .Select(i => i.BankTransactionId!.Value))
            .ToHashSet();
        var lines = (await bank.GetAccountLinesAsync(accountId, ct)).Where(l => !l.IsVoidPair).ToList();
        var documents = await bank.GetTransactionsAsync(lines.Where(l => l.SourceId is not null).Select(l => l.SourceId!.Value).Distinct().ToList(), ct);
        return lines
            .GroupBy(l => l.JournalEntryId)
            .Select(g => (Entry: g.Key, First: g.First(), Amount: g.Sum(l => l.Amount)))
            .Where(e => !matched.Contains(e.Entry) && !(e.First.SourceId is { } s && matched.Contains(s)) && e.Amount != 0m)
            .Select(e => new FeedMatch(e.Entry, e.First.EntryNumber, e.First.Date, e.Amount,
                BankingRules.Describe(chart, accountId, e.First, e.First.SourceId is { } id ? documents.GetValueOrDefault(id) : null),
                e.First.Source, e.First.SourceId))
            .ToList();
    }

    /// <summary>1 for the first line with this key in the file, 2 for the next identical one, and so on.</summary>
    private static int Occurrence(Dictionary<string, int> seen, string baseKey) =>
        seen[baseKey] = seen.GetValueOrDefault(baseKey) + 1;

    private static string? Clean(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    private static string Last4(string number) => number.Length <= 4 ? number : number[^4..];

    private AuditRecord Audit(BankFeedItem item, string before) =>
        AuditRecord.Create(clock.UtcNow, user.Actor, AuditAction.Updated, EntityType, item.Id, before, Snapshot(item));

    private static string Snapshot(BankFeedItem i) => JsonSerializer.Serialize(new
    {
        i.AccountId,
        i.Date,
        i.Amount,
        i.Payee,
        i.Reference,
        Status = i.Status.ToString(),
        i.BankTransactionId,
        i.MatchedJournalEntryId,
        i.RuleId,
    });
}
