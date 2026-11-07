using OpenBook.Application.Accounts;
using OpenBook.Application.Purchases;
using OpenBook.Application.Sales;
using OpenBook.Domain.Accounts;
using OpenBook.Domain.Banking;
using OpenBook.Domain.Common;

namespace OpenBook.Application.Banking;

public enum FeedDocumentKind
{
    Invoice,
    Bill,
}

/// <summary>An open invoice or bill that a bank line could be the payment of: it owes exactly the line's amount.</summary>
public sealed record FeedDocument(FeedDocumentKind Kind, Guid Id, string Label, string Party, DateOnly Date, DateOnly DueDate, decimal Balance);

/// <summary>
/// A bank line that is a customer paying an invoice, or the business paying a bill: instead of leaving the feed to
/// record the payment and coming back to match it, the line records it and matches it in one go. Bank accounts only
/// (a bill paid by card is a charge on the card, and invoices aren't paid into one).
/// </summary>
public sealed class FeedDocumentService(
    IBankingStore bank,
    AccountService accounts,
    ISalesStore sales,
    IPurchasesStore purchases,
    PaymentService payments,
    BillPaymentService billPayments,
    BankFeedService feed,
    IUnitOfWork unitOfWork)
{
    /// <summary>At most this many candidates per line, oldest due first.</summary>
    public const int MostPerLine = 5;

    /// <summary>For each pending line of the account, the open invoices (money in) or bills (money out) owing its amount.</summary>
    public async Task<IReadOnlyDictionary<Guid, IReadOnlyList<FeedDocument>>> CandidatesAsync(Guid accountId, IReadOnlyCollection<BankFeedItem> items, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(items);
        var pending = items.Where(i => i.Status == BankFeedStatus.Pending).ToList();
        var result = new Dictionary<Guid, IReadOnlyList<FeedDocument>>();
        if (pending.Count == 0 || (await accounts.GetChartAsync(ct)).Find(accountId)?.Type != AccountType.Bank)
            return result;

        var invoices = pending.Any(i => i.Amount > 0m) ? await sales.ListOpenInvoicesAsync(null, ct) : [];
        var bills = pending.Any(i => i.Amount < 0m) ? await purchases.ListOpenBillsAsync(null, ct) : [];
        var customers = invoices.Count == 0 ? new Dictionary<Guid, string>() : (await sales.ListCustomersAsync(ct)).ToDictionary(c => c.Id, c => c.DisplayName);
        var vendors = bills.Count == 0 ? new Dictionary<Guid, string>() : (await purchases.ListVendorsAsync(ct)).ToDictionary(v => v.Id, v => v.DisplayName);
        foreach (var item in pending)
        {
            var found = item.Amount > 0m
                ? invoices.Where(i => i.Balance == item.Amount && i.Date <= item.Date)
                    .OrderBy(i => i.DueDate).Take(MostPerLine)
                    .Select(i => new FeedDocument(FeedDocumentKind.Invoice, i.Id, $"Invoice {i.Number}", customers.GetValueOrDefault(i.CustomerId) ?? "", i.Date, i.DueDate, i.Balance)).ToList()
                : bills.Where(b => b.Balance == -item.Amount && b.CanBePaid && b.Date <= item.Date)
                    .OrderBy(b => b.DueDate).Take(MostPerLine)
                    .Select(b => new FeedDocument(FeedDocumentKind.Bill, b.Id, b.Reference is null ? "Bill" : $"Bill {b.Reference}", vendors.GetValueOrDefault(b.VendorId) ?? "", b.Date, b.DueDate, b.Balance)).ToList();
            if (found.Count > 0)
                result[item.Id] = found;
        }

        return result;
    }

    /// <summary>
    /// Records the payment of the invoice (received into this bank account) or of the bill (paid from it), on the line's
    /// date with its reference, for its whole amount, and matches the line to it: in one transaction, so a refusal leaves
    /// neither. The invoice or bill must owe exactly the line's amount.
    /// </summary>
    public async Task<Guid> ApplyAsync(Guid itemId, FeedDocumentKind kind, Guid documentId, CancellationToken ct = default)
    {
        var item = await bank.GetFeedItemAsync(itemId, ct) ?? throw new EntityNotFoundException("bank_feed_item", itemId);
        if (item.Status != BankFeedStatus.Pending)
            throw new DomainException("feed.not_pending", "This bank line has already been dealt with.");
        if ((await accounts.GetChartAsync(ct)).Find(item.AccountId)?.Type != AccountType.Bank)
            throw new DomainException("feed.document_bank_only", "Invoices and bills can be paid from a bank line of a bank account only.");
        var reference = item.Reference is { Length: > 0 and <= 30 } r ? r : null;

        await using var transaction = await unitOfWork.BeginAsync(ct);
        Guid entryId;
        if (kind == FeedDocumentKind.Invoice)
        {
            if (item.Amount <= 0m)
                throw new DomainException("feed.document_direction", "Money leaving the bank can't be a customer's payment.");
            var invoice = await sales.GetInvoiceAsync(documentId, ct) ?? throw new EntityNotFoundException("invoice", documentId);
            if (invoice.Balance != item.Amount)
                throw new DomainException("feed.document_amount", $"Invoice {invoice.Number} owes {invoice.Balance:N2}, not {item.Amount:N2}.");
            var payment = await payments.ReceiveAsync(new ReceivePaymentCommand(invoice.CustomerId, item.Date, item.AccountId,
                [new ApplyToInvoice(invoice.Id, item.Amount)], Reference: reference, Memo: "From the bank feed"), ct);
            entryId = payment.JournalEntryId;
        }
        else
        {
            if (item.Amount >= 0m)
                throw new DomainException("feed.document_direction", "Money coming into the bank can't be a bill payment.");
            var bill = await purchases.GetBillAsync(documentId, ct) ?? throw new EntityNotFoundException("bill", documentId);
            if (bill.Balance != -item.Amount)
                throw new DomainException("feed.document_amount", $"That bill owes {bill.Balance:N2}, not {-item.Amount:N2}.");
            var paid = await billPayments.PayAsync(new PayBillsCommand(item.Date, item.AccountId, [new ApplyToBill(bill.Id, -item.Amount)], reference, "From the bank feed"), ct);
            entryId = paid.Single().JournalEntryId;
        }

        await feed.MatchAsync(item.Id, entryId, ct);
        await transaction.CommitAsync(ct);
        return entryId;
    }
}
