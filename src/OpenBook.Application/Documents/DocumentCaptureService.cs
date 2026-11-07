using System.Text.Json;
using OpenBook.Application.Attachments;
using OpenBook.Application.Companies;
using OpenBook.Application.Forms;
using OpenBook.Application.Purchases;
using OpenBook.Application.Sales;
using OpenBook.Domain.Auditing;
using OpenBook.Domain.Common;
using OpenBook.Domain.Documents;
using OpenBook.Domain.Purchases;
using OpenBook.Domain.Sales;

namespace OpenBook.Application.Documents;

/// <summary>Earlier uploads of the same file, and bills that may already stand for a vendor's invoice.</summary>
/// <param name="NumberedBill">The vendor's bill with the document's number, already entered.</param>
/// <param name="SimilarBills">The vendor's bills for the same total within two weeks of the document's date.</param>
public sealed record DocumentDuplicates(
    IReadOnlyList<CapturedDocument> SameFile, IReadOnlyList<Domain.Purchases.ReceiptCapture> SameFileReceipts, Domain.Purchases.Bill? NumberedBill,
    IReadOnlyList<Domain.Purchases.Bill> SimilarBills)
{
    public bool Any => SameFile.Count > 0 || SameFileReceipts.Count > 0 || NumberedBill is not null || SimilarBills.Count > 0;
}

public interface IDocumentStore
{
    /// <summary>Tracked.</summary>
    Task<CapturedDocument?> GetDocumentAsync(Guid id, CancellationToken ct);

    /// <summary>Newest first.</summary>
    Task<IReadOnlyList<CapturedDocument>> ListDocumentsAsync(CancellationToken ct);

    Task<int> CountNewDocumentsAsync(CancellationToken ct);

    /// <summary>Documents whose file has this SHA-256, oldest first.</summary>
    Task<IReadOnlyList<CapturedDocument>> ListDocumentsByFileHashAsync(string hash, CancellationToken ct);

    /// <summary>Tracked; null until the settings are first saved.</summary>
    Task<DocumentReadingSettings?> GetReadingSettingsAsync(CancellationToken ct);

    /// <summary>Tracked; the codes learned for one vendor or customer.</summary>
    Task<IReadOnlyList<DocumentItemCode>> ListItemCodesAsync(Guid partyId, CancellationToken ct);

    /// <summary>Tracked.</summary>
    Task<DocumentItemCode?> GetItemCodeAsync(Guid id, CancellationToken ct);

    void Remove(DocumentItemCode code);

    void Add(CapturedDocument document);
    void Add(DocumentItemCode code);
    void Add(DocumentReadingSettings settings);
    void Add(AuditRecord record);
    Task SaveChangesAsync(CancellationToken ct);
}

/// <summary>Reads a document with Claude (Anthropic's API). Implemented in Infrastructure.</summary>
public interface IClaudeDocumentReader
{
    /// <summary>Whether Claude takes this kind of file (PDFs, common image types, text).</summary>
    bool CanRead(string fileName);

    /// <exception cref="DocumentReadException">Claude couldn't be reached, refused, or gave nothing usable.</exception>
    Task<DocumentFields> ReadAsync(string apiKey, string model, DocumentKind kind, string fileName, byte[] content, CancellationToken ct);
}

public sealed class DocumentReadException(string message) : Exception(message);

/// <param name="ApiKey">A new key; null or blank keeps the saved one.</param>
public sealed record DocumentReadingSettingsCommand(bool UseClaude, string? Model = null, string? ApiKey = null, bool ClearApiKey = false);

/// <summary>
/// The document inbox: upload a vendor invoice, customer purchase order, packing slip or quote (PDF, scan or photo), have it
/// read (on the server, or by Claude when an admin has switched that on), matched to the sender, our purchase order and
/// items, checked by a person, and then turned into a record or dismissed.
/// </summary>
/// <summary>What a text extractor leaves in the text for the reader to notice.</summary>
public static class DocumentTextNotes
{
    public const string PagesLeftOut = "[OpenBook read only the first pages:";
}

/// <summary>A vendor's or customer's product code learned from their documents.</summary>
public sealed record LearnedItemCode(Guid Id, string Code, Guid ItemId, string ItemName);

public sealed class DocumentCaptureService(
    IDocumentStore store,
    IPurchasesStore purchases,
    ISalesStore sales,
    ICompanyStore companies,
    AttachmentService attachments,
    IReceiptTextExtractor extractor,
    IClaudeDocumentReader claude,
    ISecretProtector secrets,
    IClock clock,
    ICurrentUser user)
{
    public const string EntityType = "captured_document";
    public const string SettingsEntity = "document_reading_settings";

    private static readonly HashSet<string> Images = new(StringComparer.OrdinalIgnoreCase) { ".png", ".jpg", ".jpeg", ".gif", ".webp", ".tif", ".tiff", ".bmp" };

    public Task<CapturedDocument?> GetAsync(Guid id, CancellationToken ct = default) => store.GetDocumentAsync(id, ct);

    public Task<IReadOnlyList<CapturedDocument>> ListAsync(CancellationToken ct = default) => store.ListDocumentsAsync(ct);

    public Task<int> NewCountAsync(CancellationToken ct = default) => store.CountNewDocumentsAsync(ct);

    public bool CanReadImages => extractor.CanReadImages;

    public async Task<DocumentReadingSettings> GetSettingsAsync(CancellationToken ct = default) =>
        await store.GetReadingSettingsAsync(ct) ?? DocumentReadingSettings.Create();

    public async Task<DocumentReadingSettings> SaveSettingsAsync(DocumentReadingSettingsCommand command, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        var company = await companies.GetRequiredCompanyAsync(ct);
        var settings = await store.GetReadingSettingsAsync(ct);
        if (settings is null)
        {
            settings = DocumentReadingSettings.Create();
            store.Add(settings);
        }

        var before = SettingsSnapshot(settings);
        var key = command.ClearApiKey ? "" : string.IsNullOrWhiteSpace(command.ApiKey) ? null : secrets.Protect(command.ApiKey.Trim());
        settings.Update(command.UseClaude, command.Model, key);
        store.Add(AuditRecord.Create(clock.UtcNow, user.Actor, AuditAction.Updated, SettingsEntity, company.Id, before, SettingsSnapshot(settings)));
        await store.SaveChangesAsync(ct);
        return settings;
    }

    /// <summary>
    /// Saves the file on a new document, then reads and matches it. A file that can't be read is still kept, with the
    /// reason in <see cref="CapturedDocument.ReadNote"/>.
    /// </summary>
    /// <param name="useClaude">Null: Claude reads it when switched on. False keeps this file on the server.</param>
    /// <param name="purchaseOrderId">Our purchase order it belongs to, when it's uploaded from the order: used when reading doesn't find one.</param>
    public async Task<CapturedDocument> UploadAsync(
        DocumentKind kind, string fileName, Stream content, bool? useClaude = null, Guid? purchaseOrderId = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await content.ReadAsync(chunk, ct)) > 0 && buffer.Length <= Domain.Attachments.Attachment.MaxBytes)
            buffer.Write(chunk, 0, read);
        // Checks the name, type and size before anything is saved.
        Domain.Attachments.Attachment.Create(EntityType, Guid.Empty, fileName, buffer.Length, clock.UtcNow, user.Actor);

        var document = CapturedDocument.Create(kind, fileName, clock.UtcNow, user.Actor);
        document.SetFileHash(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(buffer.GetBuffer().AsSpan(0, (int)buffer.Length))));
        store.Add(document);
        store.Add(Audit(AuditAction.Created, document, null));
        await store.SaveChangesAsync(ct);
        buffer.Position = 0;
        await attachments.AddAsync(EntityType, document.Id, fileName, buffer, ct);

        await ReadAsync(document, useClaude ?? true, ct, purchaseOrderId);
        await store.SaveChangesAsync(ct);
        return document;
    }

    /// <summary>
    /// What may make a document a duplicate: the same file uploaded before (to the inbox or as a receipt), and, for a
    /// vendor's invoice, a bill already entered with its number or for its total close to its date.
    /// </summary>
    public async Task<DocumentDuplicates> DuplicatesAsync(CapturedDocument document, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        IReadOnlyList<CapturedDocument> files = [];
        IReadOnlyList<ReceiptCapture> receipts = [];
        if (document.FileHash is { } hash)
        {
            files = (await store.ListDocumentsByFileHashAsync(hash, ct)).Where(d => d.Id != document.Id).ToList();
            receipts = await purchases.ListReceiptsByFileHashAsync(hash, ct);
        }

        Bill? numbered = null;
        IReadOnlyList<Bill> similar = [];
        if (document.Kind == DocumentKind.VendorInvoice && document.VendorId is { } vendorId && document.Status == CapturedDocumentStatus.New)
        {
            var bills = (await purchases.ListBillsAsync(vendorId, ct)).Where(b => b.Status == Domain.Common.DocumentStatus.Posted).ToList();
            if (document.Number is { } number)
                numbered = bills.FirstOrDefault(b => string.Equals(b.Reference?.Trim(), number.Trim(), StringComparison.OrdinalIgnoreCase));
            if (document.Total is { } total && document.Date is { } date)
                similar = bills.Where(b => b.Id != numbered?.Id && b.Total == total && Math.Abs(b.Date.DayNumber - date.DayNumber) <= Purchases.BillService.DuplicateDays).ToList();
        }

        return new DocumentDuplicates(files, receipts, numbered, similar);
    }

    /// <summary>The next document waiting to be checked after this one (oldest first), or null.</summary>
    public async Task<CapturedDocument?> NextWaitingAsync(Guid after, CancellationToken ct = default) =>
        (await store.ListDocumentsAsync(ct)).Where(d => d.Status == CapturedDocumentStatus.New && d.Id != after).OrderBy(d => d.CreatedAt).FirstOrDefault();

    /// <summary>Reads the file again (by Claude or on the server), replacing the fields and any corrections.</summary>
    public async Task<CapturedDocument> ReadAgainAsync(Guid id, bool useClaude, CancellationToken ct = default)
    {
        var document = await store.GetDocumentAsync(id, ct) ?? throw new EntityNotFoundException(EntityType, id);
        if (document.Status != CapturedDocumentStatus.New)
            throw new DomainException("document.done", "This document has already been dealt with.");
        if (useClaude && !(await GetSettingsAsync(ct)).IsReady)
            throw new DomainException("document_reading.claude_off", "Reading with Claude isn't switched on.");
        var before = Snapshot(document);
        await ReadAsync(document, useClaude, ct);
        store.Add(Audit(AuditAction.Updated, document, before));
        await store.SaveChangesAsync(ct);
        return document;
    }

    /// <summary>Saves the fields as checked: the sender, our purchase order and the item on each line must exist and fit.</summary>
    public async Task<CapturedDocument> ReviewAsync(Guid id, DocumentReview review, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(review);
        var document = await store.GetDocumentAsync(id, ct) ?? throw new EntityNotFoundException(EntityType, id);
        if (review.VendorId is { } vendorId && await purchases.GetVendorAsync(vendorId, ct) is null)
            throw new EntityNotFoundException("vendor", vendorId);
        if (review.CustomerId is { } customerId && await sales.GetCustomerAsync(customerId, ct) is null)
            throw new EntityNotFoundException("customer", customerId);
        if (review.PurchaseOrderId is { } orderId)
        {
            var order = await purchases.GetPurchaseOrderAsync(orderId, ct) ?? throw new EntityNotFoundException("purchase_order", orderId);
            if (review.VendorId is { } v && order.VendorId != v)
                throw new DomainException("document.order_vendor", $"Purchase order {order.Number} is to another vendor.");
        }

        var items = (await sales.ListItemsAsync(ct)).Select(i => i.Id).ToHashSet();
        if (review.Lines.FirstOrDefault(l => l.ItemId is { } itemId && !items.Contains(itemId)) is { ItemId: { } missing })
            throw new EntityNotFoundException("item", missing);

        var before = Snapshot(document);
        document.Review(review);
        store.Add(Audit(AuditAction.Updated, document, before));
        await store.SaveChangesAsync(ct);
        return document;
    }

    /// <summary>
    /// Remembers the item on each coded line as that party's code for it. Called when a document is made into a bill, order
    /// or receipt, from the lines it used, so a review's guesses and lines thrown away teach nothing.
    /// </summary>
    internal static async Task LearnCodesAsync(IDocumentStore store, Guid partyId, IEnumerable<CapturedDocumentLine> lines, CancellationToken ct)
    {
        var codes = (await store.ListItemCodesAsync(partyId, ct)).ToDictionary(c => c.Code, StringComparer.Ordinal);
        foreach (var line in lines.Where(l => !string.IsNullOrWhiteSpace(l.Sku) && l.ItemId is not null))
        {
            var code = DocumentItemCode.Normalize(line.Sku!);
            if (codes.TryGetValue(code, out var known))
                known.Point(line.ItemId!.Value);
            else
                store.Add(codes[code] = DocumentItemCode.Create(partyId, code, line.ItemId!.Value));
        }
    }

    /// <summary>The product codes learned for a vendor or customer, by code, with the item each points at.</summary>
    public async Task<IReadOnlyList<LearnedItemCode>> ItemCodesAsync(Guid partyId, CancellationToken ct = default)
    {
        var codes = await store.ListItemCodesAsync(partyId, ct);
        var items = await sales.GetItemsAsync([.. codes.Select(c => c.ItemId).Distinct()], ct);
        return [.. codes.OrderBy(c => c.Code, StringComparer.Ordinal)
            .Select(c => new LearnedItemCode(c.Id, c.Code, c.ItemId, items.GetValueOrDefault(c.ItemId)?.Name ?? "(item not found)"))];
    }

    /// <summary>Forgets a learned code, audited on the vendor or customer it belongs to.</summary>
    public async Task ForgetItemCodeAsync(Guid codeId, CancellationToken ct = default)
    {
        var code = await store.GetItemCodeAsync(codeId, ct) ?? throw new EntityNotFoundException("document_item_code", codeId);
        var party = await purchases.GetVendorAsync(code.PartyId, ct) is not null ? "vendor" : "customer";
        store.Remove(code);
        store.Add(AuditRecord.Create(clock.UtcNow, user.Actor, AuditAction.Updated, party, code.PartyId,
            JsonSerializer.Serialize(new { ProductCode = code.Code, code.ItemId }), JsonSerializer.Serialize(new { ProductCode = code.Code, ItemId = (Guid?)null })));
        await store.SaveChangesAsync(ct);
    }

    public async Task<CapturedDocument> DismissAsync(Guid id, CancellationToken ct = default)
    {
        var document = await store.GetDocumentAsync(id, ct) ?? throw new EntityNotFoundException(EntityType, id);
        var before = Snapshot(document);
        document.Dismiss(clock.UtcNow);
        store.Add(Audit(AuditAction.Updated, document, before));
        await store.SaveChangesAsync(ct);
        return document;
    }

    public async Task<CapturedDocument> RestoreAsync(Guid id, CancellationToken ct = default)
    {
        var document = await store.GetDocumentAsync(id, ct) ?? throw new EntityNotFoundException(EntityType, id);
        var before = Snapshot(document);
        document.Restore();
        store.Add(Audit(AuditAction.Updated, document, before));
        await store.SaveChangesAsync(ct);
        return document;
    }

    /// <param name="orderHint">The purchase order to fall back on when reading finds none (the one it was uploaded from, or the one chosen before).</param>
    private async Task ReadAsync(CapturedDocument document, bool useClaude, CancellationToken ct, Guid? orderHint = null)
    {
        orderHint ??= document.PurchaseOrderId;
        var files = await attachments.ListAsync(EntityType, document.Id, ct);
        var file = files.Count > 0 ? files[0] : null;
        var bytes = file is null ? [] : await attachments.GetContentAsync(file.Id, ct) ?? [];
        var company = await companies.GetRequiredCompanyAsync(ct);
        var vendors = (await purchases.ListVendorsAsync(ct)).Where(v => v.IsActive).ToList();
        var customers = (await sales.ListCustomersAsync(ct)).Where(c => c.IsActive).ToList();
        var fromVendor = DocumentKinds.IsFromVendor(document.Kind);
        var names = fromVendor ? vendors.Select(v => v.DisplayName) : customers.Select(c => c.DisplayName);

        string? text = null, method = null, note = null;
        var fields = DocumentFields.Empty;
        var settings = await GetSettingsAsync(ct);
        if (useClaude && settings.IsReady && claude.CanRead(document.FileName))
        {
            var key = secrets.Unprotect(settings.ProtectedApiKey!);
            if (key is null)
            {
                note = "The Anthropic API key can't be read on this server; enter it again in Settings. Read on the server instead.";
            }
            else
            {
                try
                {
                    fields = await claude.ReadAsync(key, settings.Model, document.Kind, document.FileName, bytes, ct);
                    method = DocumentReadMethods.Claude;
                }
                catch (DocumentReadException ex)
                {
                    note = $"Claude couldn't read it ({ex.Message}). Read on the server instead.";
                }
            }
        }

        if (method is null)
        {
            text = await extractor.ExtractAsync(document.FileName, bytes, ct);
            if (text is null)
            {
                var image = Images.Contains(Path.GetExtension(document.FileName));
                note ??= image && !extractor.CanReadImages
                    ? "This is a photo or scan and no OCR engine is set up on the server, so nothing was read. Fill in the fields, or have an admin switch on reading with Claude."
                    : "No text could be read from this file (a scanned PDF has none). Fill in the fields, or read it with Claude.";
            }
            else
            {
                method = Images.Contains(Path.GetExtension(document.FileName)) ? DocumentReadMethods.Ocr : DocumentReadMethods.Text;
                fields = DocumentParser.Parse(text, document.Kind, names, company.Name);
                if (text.Contains(DocumentTextNotes.PagesLeftOut, StringComparison.Ordinal))
                    note ??= "This file is long, so only its first pages were read. Check the lines and the total against it.";
                else if (fields.Total is null && DocumentKinds.HasPrices(document.Kind))
                    note ??= "No total was found, so the lines couldn't be checked against one.";
            }
        }

        document.Read(text, method, note, fields);

        // Match the sender, our purchase order and the items, then keep that as the starting point to check.
        var text2 = string.Join('\n', new[] { document.Party, text }.Where(t => t is not null));
        Guid? vendorId = fromVendor ? DocumentMatching.Party(document.Party, text2, vendors.Select(v => (v.Id, v.DisplayName))) : null;
        Guid? customerId = fromVendor ? null : DocumentMatching.Party(document.Party, text2, customers.Select(c => (c.Id, c.DisplayName)));
        PurchaseOrder? order = null;
        if (fromVendor && document.Kind != DocumentKind.VendorQuote)
        {
            var orders = await purchases.ListPurchaseOrdersAsync(null, ct);
            order = DocumentMatching.PurchaseOrder(document.OrderReference, vendorId, orders)
                ?? orders.FirstOrDefault(o => o.Id == orderHint && (vendorId is null || o.VendorId == vendorId));
        }
        vendorId ??= order?.VendorId;
        var items = (await sales.ListItemsAsync(ct)).Where(i => i.IsActive).ToList();
        var codes = (vendorId ?? customerId) is { } partyId
            ? (await store.ListItemCodesAsync(partyId, ct)).Where(c => items.Any(i => i.Id == c.ItemId)).ToDictionary(c => c.Code, c => c.ItemId, StringComparer.Ordinal)
            : [];
        document.Review(new DocumentReview(
            document.Party, vendorId, customerId, document.Number, document.Date, document.DueDate, document.OrderReference, order?.Id,
            document.Subtotal, document.Tax, document.Shipping, document.Total,
            document.Lines.Select(l => new DocumentReviewLine(l.Description, l.Sku, l.Quantity, l.UnitPrice, l.Amount,
                (l.Sku is { } sku && codes.TryGetValue(DocumentItemCode.Normalize(sku), out var learned) ? learned : (Guid?)null)
                ?? DocumentMatching.Item(l.Sku, l.Description, items, order))).ToList()));
    }

    private AuditRecord Audit(AuditAction action, CapturedDocument d, string? before) =>
        AuditRecord.Create(clock.UtcNow, user.Actor, action, EntityType, d.Id, before, Snapshot(d));

    private static string Snapshot(CapturedDocument d) => JsonSerializer.Serialize(new
    {
        Kind = d.Kind.ToString(),
        d.FileName,
        Status = d.Status.ToString(),
        d.ReadBy,
        d.Party,
        d.VendorId,
        d.CustomerId,
        d.Number,
        d.Date,
        d.DueDate,
        d.OrderReference,
        d.PurchaseOrderId,
        d.Subtotal,
        d.Tax,
        d.Shipping,
        d.Total,
        Lines = d.Lines.Count,
        d.ResultType,
        d.ResultId,
    });

    private static string SettingsSnapshot(DocumentReadingSettings s) => JsonSerializer.Serialize(new { s.UseClaude, s.Model, HasApiKey = s.ProtectedApiKey is not null });
}

/// <summary>Matches what was read to the books: the sender by name, our purchase order by number, items by code or name.</summary>
public static class DocumentMatching
{
    /// <summary>The party whose name is the one read, else the longest name (3+ letters) found in the text.</summary>
    public static Guid? Party(string? name, string? text, IEnumerable<(Guid Id, string Name)> parties)
    {
        var list = parties.ToList();
        if (name is not null && list.FirstOrDefault(p => string.Equals(p.Name, name.Trim(), StringComparison.OrdinalIgnoreCase)) is { Id: var exact } && exact != Guid.Empty)
            return exact;
        if (text is null)
            return null;
        var found = list.Where(p => p.Name.Length >= 3 && text.Contains(p.Name, StringComparison.OrdinalIgnoreCase)).OrderByDescending(p => p.Name.Length).ToList();
        return found.Count > 0 ? found[0].Id : null;
    }

    /// <summary>The purchase order (not cancelled; the vendor's when known) whose number is the reference, ignoring prefixes like "PO-".</summary>
    public static PurchaseOrder? PurchaseOrder(string? reference, Guid? vendorId, IEnumerable<PurchaseOrder> orders)
    {
        if (string.IsNullOrWhiteSpace(reference))
            return null;
        var key = Key(reference);
        var digits = Digits(reference);
        return orders
            .Where(o => o.Status != PurchaseOrderStatus.Cancelled && (vendorId is null || o.VendorId == vendorId))
            .Where(o => Key(o.Number) == key || (digits.Length > 0 && Digits(o.Number) == digits))
            .OrderBy(o => o.Status == PurchaseOrderStatus.Open ? 0 : 1)
            .FirstOrDefault();
    }

    /// <summary>
    /// An item for a line: its code is the item's barcode or name; else its description is an item's name, or the
    /// description of a line of the purchase order; else the longest item name (4+ letters) inside the description.
    /// </summary>
    public static Guid? Item(string? sku, string? description, IReadOnlyList<Item> items, PurchaseOrder? order = null)
    {
        if (sku is not null && items.FirstOrDefault(i => string.Equals(i.Barcode, sku, StringComparison.OrdinalIgnoreCase) || string.Equals(i.Name, sku, StringComparison.OrdinalIgnoreCase)) is { } byCode)
            return byCode.Id;
        if (description is null)
            return null;
        if (items.FirstOrDefault(i => string.Equals(i.Name, description, StringComparison.OrdinalIgnoreCase)) is { } byName)
            return byName.Id;
        if (order?.Lines.FirstOrDefault(l => l.ItemId is not null && string.Equals(l.Description, description, StringComparison.OrdinalIgnoreCase)) is { ItemId: { } fromOrder })
            return fromOrder;
        return items.Where(i => i.Name.Length >= 4 && ContainsWords(description, i.Name)).OrderByDescending(i => i.Name.Length).Select(i => (Guid?)i.Id).FirstOrDefault();
    }

    private static bool ContainsWords(string text, string words)
    {
        var at = text.IndexOf(words, StringComparison.OrdinalIgnoreCase);
        return at >= 0 && (at == 0 || !char.IsLetterOrDigit(text[at - 1])) && (at + words.Length == text.Length || !char.IsLetterOrDigit(text[at + words.Length]));
    }

    private static string Key(string value) => new(value.Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());

    private static string Digits(string value) => new string(value.Where(char.IsAsciiDigit).ToArray()).TrimStart('0');
}
