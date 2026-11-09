using System.Globalization;
using OpenBook.Application.Companies;
using OpenBook.Application.Purchases;
using OpenBook.Application.Sales;
using OpenBook.Domain.Common;
using OpenBook.Domain.Companies;
using OpenBook.Domain.Sales;

namespace OpenBook.Application.Forms;

/// <summary>The print queue. Changes become visible only after <see cref="SaveChangesAsync"/>.</summary>
public interface IPrintQueueStore
{
    /// <summary>Tracked; waiting items and those printed since <paramref name="printedSince"/>.</summary>
    Task<IReadOnlyList<PrintQueueItem>> ListAsync(DateTimeOffset printedSince, CancellationToken ct);

    void Add(PrintQueueItem item);
    void Remove(PrintQueueItem item);

    Task SaveChangesAsync(CancellationToken ct);
}

/// <summary>A document that can be printed, as lists show it.</summary>
public sealed record PrintableDocument(FormDocuments Document, Guid Id, string Number, DateOnly Date, string Party, decimal Total, bool Voided)
{
    public string Title => $"{FormDesignService.UsualTitle(Document)} {Number}";
}

public sealed record PrintQueueEntry(PrintQueueItem Item, PrintableDocument? Document);

/// <summary>Statements for these customers over a period, printed with a batch.</summary>
public sealed record StatementRun(IReadOnlyList<Guid> CustomerIds, DateOnly From, DateOnly To);

/// <summary>
/// Printing: the "print later" queue, and printing many documents (and statements) as one PDF, each document starting
/// on its own page with its own design and page numbers. Queued documents in a batch are marked printed.
/// </summary>
public sealed class PrintService(
    IPrintQueueStore store, ISalesStore sales, IPurchasesStore purchases, FormService forms, PurchaseOrderForms orders, IFormRenderer renderer,
    ICompanyStore companies, IClock clock, ICurrentUser user)
{
    public const int MaxDocuments = 200;
    private static readonly TimeSpan RecentlyPrinted = TimeSpan.FromDays(30);

    /// <summary>Waiting documents, oldest first.</summary>
    public async Task<IReadOnlyList<PrintQueueEntry>> QueueAsync(CancellationToken ct = default) =>
        await EntriesAsync((await ItemsAsync(ct)).Where(i => i.IsWaiting).OrderBy(i => i.AddedAt), ct);

    /// <summary>Printed in the last 30 days, newest first, so a batch that came out wrong can be put back.</summary>
    public async Task<IReadOnlyList<PrintQueueEntry>> PrintedAsync(CancellationToken ct = default) =>
        await EntriesAsync((await ItemsAsync(ct)).Where(i => !i.IsWaiting).OrderByDescending(i => i.PrintedAt), ct);

    public async Task<bool> IsWaitingAsync(FormDocuments document, Guid id, CancellationToken ct = default) =>
        (await ItemsAsync(ct)).Any(i => i.IsWaiting && i.Document == document && i.DocumentId == id);

    /// <summary>Puts a document in the queue (once: adding it again changes nothing).</summary>
    public async Task<PrintQueueItem> AddAsync(FormDocuments document, Guid id, CancellationToken ct = default)
    {
        var items = await ItemsAsync(ct);
        if (items.FirstOrDefault(i => i.IsWaiting && i.Document == document && i.DocumentId == id) is { } waiting)
            return waiting;
        var item = PrintQueueItem.Create(document, id, clock.UtcNow, user.Actor);
        _ = await DocumentAsync(document, id, null, ct) ?? throw new EntityNotFoundException(document.ToString(), id);
        store.Add(item);
        await store.SaveChangesAsync(ct);
        return item;
    }

    /// <summary>Takes a waiting document out of the queue without printing it.</summary>
    public async Task RemoveAsync(FormDocuments document, Guid id, CancellationToken ct = default)
    {
        foreach (var item in (await ItemsAsync(ct)).Where(i => i.IsWaiting && i.Document == document && i.DocumentId == id))
            store.Remove(item);
        await store.SaveChangesAsync(ct);
    }

    public async Task RemoveAsync(Guid itemId, CancellationToken ct = default)
    {
        var item = (await ItemsAsync(ct)).FirstOrDefault(i => i.Id == itemId && i.IsWaiting) ?? throw new EntityNotFoundException("print_queue_item", itemId);
        store.Remove(item);
        await store.SaveChangesAsync(ct);
    }

    /// <summary>A printed document back in the queue.</summary>
    public async Task PutBackAsync(Guid itemId, CancellationToken ct = default)
    {
        var items = await ItemsAsync(ct);
        var item = items.FirstOrDefault(i => i.Id == itemId) ?? throw new EntityNotFoundException("print_queue_item", itemId);
        // Queued again since: it's waiting already, and once is enough.
        if (items.Any(i => i.IsWaiting && i.Document == item.Document && i.DocumentId == item.DocumentId))
            return;
        item.PutBack();
        await store.SaveChangesAsync(ct);
    }

    /// <summary>Documents of one kind dated in a period, newest first, to pick for printing.</summary>
    public async Task<IReadOnlyList<PrintableDocument>> DocumentsAsync(FormDocuments document, DateOnly from, DateOnly to, CancellationToken ct = default)
    {
        var names = await NamesAsync(ct);
        IEnumerable<PrintableDocument> all = document switch
        {
            FormDocuments.Invoice => (await sales.ListInvoicesDatedAsync(from, to, ct)).Select(i => Printable(i, names)),
            FormDocuments.Estimate => (await sales.ListEstimatesDatedAsync(from, to, ct)).Select(e => Printable(e, names)),
            FormDocuments.CreditMemo => (await sales.ListCreditMemosDatedAsync(from, to, ct)).Select(c => Printable(c, names)),
            FormDocuments.SalesReceipt => (await sales.ListReceiptsDatedAsync(from, to, ct)).Select(r => Printable(r, names)),
            FormDocuments.PurchaseOrder => (await purchases.ListPurchaseOrdersDatedAsync(from, to, ct)).Select(o => Printable(o, names)),
            _ => throw new DomainException("print.document_invalid", "Choose invoices, estimates, credit memos, sales receipts or purchase orders."),
        };
        return [.. all.Where(d => d.Date >= from && d.Date <= to).OrderByDescending(d => d.Date).ThenByDescending(d => d.Number, StringComparer.Ordinal)];
    }

    /// <summary>
    /// One PDF of the documents, in the order given, then the statements (customers by name). Waiting queue items for
    /// those documents are marked printed.
    /// </summary>
    public async Task<RenderedForm> PrintAsync(IReadOnlyList<(FormDocuments Document, Guid Id)> documents, StatementRun? statements = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(documents);
        var unique = documents.Distinct().ToList();
        var count = unique.Count + (statements?.CustomerIds.Count ?? 0);
        if (count == 0)
            throw new DomainException("print.nothing", "Choose something to print.");
        if (count > MaxDocuments)
            throw new DomainException("print.too_many", $"Print at most {MaxDocuments} documents at a time.");

        var pdfs = new List<byte[]>();
        foreach (var (document, id) in unique)
        {
            pdfs.Add(document switch
            {
                FormDocuments.Invoice => (await forms.RenderAsync(FormKind.Invoice, id, ct)).Pdf,
                FormDocuments.Estimate => (await forms.RenderAsync(FormKind.Estimate, id, ct)).Pdf,
                FormDocuments.CreditMemo => (await forms.RenderAsync(FormKind.CreditMemo, id, ct)).Pdf,
                FormDocuments.SalesReceipt => (await forms.RenderAsync(FormKind.SalesReceipt, id, ct)).Pdf,
                FormDocuments.PurchaseOrder => (await orders.RenderAsync(id, ct)).Pdf,
                _ => throw new DomainException("print.document_invalid", "Statements are printed for a period: choose customers below."),
            });
        }

        if (statements is not null)
        {
            if (statements.From > statements.To)
                throw new DomainException("print.period_invalid", "The statement period ends before it starts.");
            var names = (await sales.ListCustomersAsync(ct)).ToDictionary(c => c.Id, c => c.DisplayName);
            foreach (var customerId in statements.CustomerIds.Distinct().OrderBy(c => names.GetValueOrDefault(c, ""), StringComparer.CurrentCultureIgnoreCase))
                pdfs.Add((await forms.RenderStatementAsync(customerId, statements.From, statements.To, ct)).Pdf);
        }

        // The PDF first: documents count as printed only once there's something to print.
        var combined = pdfs.Count == 1 ? pdfs[0] : renderer.Combine(pdfs);
        var printed = unique.ToHashSet();
        foreach (var item in (await ItemsAsync(ct)).Where(i => i.IsWaiting && printed.Contains((i.Document, i.DocumentId))))
            item.MarkPrinted(clock.UtcNow, user.Actor);
        await store.SaveChangesAsync(ct);

        var company = await companies.GetRequiredCompanyAsync(ct);
        var today = CompanyTime.TodayAt(clock.UtcNow, CompanyTime.Zone).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        return new RenderedForm(FormService.FileName(company, $"Printing {count}", today), combined, Guid.Empty, $"{count} document{(count == 1 ? "" : "s")}");
    }

    private Task<IReadOnlyList<PrintQueueItem>> ItemsAsync(CancellationToken ct) => store.ListAsync(clock.UtcNow - RecentlyPrinted, ct);

    private async Task<IReadOnlyList<PrintQueueEntry>> EntriesAsync(IEnumerable<PrintQueueItem> items, CancellationToken ct)
    {
        var names = await NamesAsync(ct);
        var entries = new List<PrintQueueEntry>();
        foreach (var item in items)
            entries.Add(new PrintQueueEntry(item, await DocumentAsync(item.Document, item.DocumentId, names, ct)));
        return entries;
    }

    private async Task<PrintableDocument?> DocumentAsync(FormDocuments document, Guid id, IReadOnlyDictionary<Guid, string>? names, CancellationToken ct)
    {
        names ??= await NamesAsync(ct);
        return document switch
        {
            FormDocuments.Invoice => await sales.GetInvoiceAsync(id, ct) is { } i ? Printable(i, names) : null,
            FormDocuments.Estimate => await sales.GetEstimateAsync(id, ct) is { } e ? Printable(e, names) : null,
            FormDocuments.CreditMemo => await sales.GetCreditMemoAsync(id, ct) is { } c ? Printable(c, names) : null,
            FormDocuments.SalesReceipt => await sales.GetReceiptAsync(id, ct) is { } r ? Printable(r, names) : null,
            FormDocuments.PurchaseOrder => await purchases.GetPurchaseOrderAsync(id, ct) is { } o ? Printable(o, names) : null,
            _ => null,
        };
    }

    /// <summary>Customer and vendor names by id.</summary>
    private async Task<IReadOnlyDictionary<Guid, string>> NamesAsync(CancellationToken ct) =>
        (await sales.ListCustomersAsync(ct)).Select(c => (c.Id, c.DisplayName))
            .Concat((await purchases.ListVendorsAsync(ct)).Select(v => (v.Id, v.DisplayName)))
            .ToDictionary(x => x.Id, x => x.DisplayName);

    private static PrintableDocument Printable(Invoice i, IReadOnlyDictionary<Guid, string> names) =>
        new(FormDocuments.Invoice, i.Id, i.Number, i.Date, names.GetValueOrDefault(i.CustomerId, ""), i.Total, i.Status == DocumentStatus.Voided);

    private static PrintableDocument Printable(Estimate e, IReadOnlyDictionary<Guid, string> names) =>
        new(FormDocuments.Estimate, e.Id, e.Number, e.Date, names.GetValueOrDefault(e.CustomerId, ""), e.Total, false);

    private static PrintableDocument Printable(CreditMemo c, IReadOnlyDictionary<Guid, string> names) =>
        new(FormDocuments.CreditMemo, c.Id, c.Number, c.Date, names.GetValueOrDefault(c.CustomerId, ""), c.Total, c.Status == DocumentStatus.Voided);

    private static PrintableDocument Printable(SalesReceipt r, IReadOnlyDictionary<Guid, string> names) =>
        new(FormDocuments.SalesReceipt, r.Id, r.Number, r.Date, r.CustomerId is { } c ? names.GetValueOrDefault(c, "") : "Cash sale", r.Total, r.Status == DocumentStatus.Voided);

    private static PrintableDocument Printable(Domain.Purchases.PurchaseOrder o, IReadOnlyDictionary<Guid, string> names) =>
        new(FormDocuments.PurchaseOrder, o.Id, o.Number, o.Date, names.GetValueOrDefault(o.VendorId, ""), o.Total, o.Status == Domain.Purchases.PurchaseOrderStatus.Cancelled);
}
