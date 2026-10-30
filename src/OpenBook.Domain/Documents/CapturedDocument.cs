using OpenBook.Domain.Common;

namespace OpenBook.Domain.Documents;

/// <summary>What a captured paper document is, which decides who it's from and what it can become.</summary>
public enum DocumentKind
{
    /// <summary>A vendor's invoice: becomes a bill (from the matching purchase order when there is one).</summary>
    VendorInvoice,

    /// <summary>A customer's purchase order: becomes a sales order.</summary>
    CustomerOrder,

    /// <summary>A vendor's packing slip: records goods received on the purchase order it names.</summary>
    PackingSlip,

    /// <summary>A vendor's quote or order confirmation: becomes a purchase order.</summary>
    VendorQuote,
}

public enum CapturedDocumentStatus
{
    /// <summary>Waiting to be checked and turned into a record, or dismissed.</summary>
    New,

    /// <summary>Turned into a record (<see cref="CapturedDocument.ResultType"/>, <see cref="CapturedDocument.ResultId"/>).</summary>
    Done,

    Dismissed,
}

/// <summary>How a document's fields were read.</summary>
public static class DocumentReadMethods
{
    /// <summary>The text inside a PDF or text file, read exactly.</summary>
    public const string Text = "text";

    /// <summary>A scan or photo read by the local OCR engine.</summary>
    public const string Ocr = "ocr";

    /// <summary>Sent to Claude (Anthropic's API) to read.</summary>
    public const string Claude = "claude";
}

public static class DocumentKinds
{
    public static bool IsFromVendor(DocumentKind kind) => kind != DocumentKind.CustomerOrder;

    public static string Label(DocumentKind kind) => kind switch
    {
        DocumentKind.VendorInvoice => "Vendor invoice",
        DocumentKind.CustomerOrder => "Customer purchase order",
        DocumentKind.PackingSlip => "Packing slip",
        _ => "Vendor quote",
    };

    /// <summary>What the document's own number is called.</summary>
    public static string NumberLabel(DocumentKind kind) => kind switch
    {
        DocumentKind.VendorInvoice => "Invoice number",
        DocumentKind.CustomerOrder => "Their PO number",
        DocumentKind.PackingSlip => "Slip number",
        _ => "Quote number",
    };

    /// <summary>Whether the document's prices matter (a packing slip only counts goods).</summary>
    public static bool HasPrices(DocumentKind kind) => kind != DocumentKind.PackingSlip;
}

/// <summary>One line as read from a document; every field may be missing.</summary>
public sealed record DocumentLineFields(string? Description, string? Sku, decimal? Quantity, decimal? UnitPrice, decimal? Amount);

/// <summary>
/// What was read from a document: who it's from (<see cref="Party"/>, the name as printed), its own number, dates, the
/// order it refers to (our PO number on a vendor's invoice or packing slip), totals and lines.
/// </summary>
public sealed record DocumentFields(
    string? Party,
    string? Number,
    DateOnly? Date,
    DateOnly? DueDate,
    string? OrderReference,
    decimal? Subtotal,
    decimal? Tax,
    decimal? Shipping,
    decimal? Total,
    IReadOnlyList<DocumentLineFields> Lines)
{
    public static readonly DocumentFields Empty = new(null, null, null, null, null, null, null, null, null, []);
}

/// <summary>A line of a captured document, with the item it was matched to.</summary>
public sealed class CapturedDocumentLine
{
    private CapturedDocumentLine() { }

    internal CapturedDocumentLine(int lineNumber, string? description, string? sku, decimal? quantity, decimal? unitPrice, decimal? amount, Guid? itemId)
    {
        LineNumber = lineNumber;
        Description = description;
        Sku = sku;
        Quantity = quantity;
        UnitPrice = unitPrice;
        Amount = amount;
        ItemId = itemId;
    }

    public int LineNumber { get; private set; }
    public string? Description { get; private set; }

    /// <summary>The sender's code for the product (SKU, part number, item number).</summary>
    public string? Sku { get; private set; }

    public decimal? Quantity { get; private set; }
    public decimal? UnitPrice { get; private set; }
    public decimal? Amount { get; private set; }
    public Guid? ItemId { get; private set; }
}

/// <summary>
/// A paper or PDF document (vendor invoice, customer purchase order, packing slip, vendor quote) captured to turn into a
/// record. The file is an attachment on it; the fields are what was read from it, corrected by whoever checks it.
/// </summary>
public sealed class CapturedDocument
{
    public const int MaxTextLength = 20000;
    public const int MaxLines = 200;

    private readonly List<CapturedDocumentLine> _lines = [];

    private CapturedDocument() => FileName = string.Empty;

    public Guid Id { get; private set; }
    public DocumentKind Kind { get; private set; }
    public string FileName { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public string? CreatedBy { get; private set; }
    public CapturedDocumentStatus Status { get; private set; }

    /// <summary>The text read locally; null when nothing could be read or Claude read the file.</summary>
    public string? Text { get; private set; }

    /// <summary>How the fields were read (<see cref="DocumentReadMethods"/>); null when nothing could be read.</summary>
    public string? ReadBy { get; private set; }

    /// <summary>Why reading fell short (no OCR engine, Claude refused or failed); shown on the review page.</summary>
    public string? ReadNote { get; private set; }

    /// <summary>The sender's name as printed.</summary>
    public string? Party { get; private set; }

    public Guid? VendorId { get; private set; }
    public Guid? CustomerId { get; private set; }
    public string? Number { get; private set; }
    public DateOnly? Date { get; private set; }
    public DateOnly? DueDate { get; private set; }
    public string? OrderReference { get; private set; }

    /// <summary>Our purchase order the document belongs to (vendor documents).</summary>
    public Guid? PurchaseOrderId { get; private set; }

    public decimal? Subtotal { get; private set; }
    public decimal? Tax { get; private set; }
    public decimal? Shipping { get; private set; }
    public decimal? Total { get; private set; }

    public IReadOnlyList<CapturedDocumentLine> Lines => _lines;

    /// <summary>The kind of record it became (an audit entity type: bill, sales_order, purchase_order, purchase_order_receipt).</summary>
    public string? ResultType { get; private set; }

    public Guid? ResultId { get; private set; }
    public DateTimeOffset? DoneAt { get; private set; }

    public static CapturedDocument Create(DocumentKind kind, string fileName, DateTimeOffset now, string? createdBy)
    {
        if (!Enum.IsDefined(kind))
            throw new DomainException("document.kind_invalid", "Choose what kind of document it is.");
        return new CapturedDocument
        {
            Id = Guid.CreateVersion7(),
            Kind = kind,
            FileName = Common.Text.Optional(fileName, 255, "document.name_too_long", "File name") ?? "document",
            CreatedAt = now.ToUniversalTime(),
            CreatedBy = createdBy,
            Status = CapturedDocumentStatus.New,
        };
    }

    /// <summary>Records a reading of the file, replacing what was read before (and any corrections).</summary>
    public void Read(string? text, string? readBy, string? note, DocumentFields fields)
    {
        ArgumentNullException.ThrowIfNull(fields);
        EnsureNew();
        Text = string.IsNullOrWhiteSpace(text) ? null : text.Length > MaxTextLength ? text[..MaxTextLength] : text;
        ReadBy = readBy;
        ReadNote = note is null ? null : note.Length > 1000 ? note[..1000] : note;
        Apply(new DocumentReview(fields.Party, null, null, fields.Number, fields.Date, fields.DueDate, fields.OrderReference, null,
            fields.Subtotal, fields.Tax, fields.Shipping, fields.Total,
            fields.Lines.Select(l => new DocumentReviewLine(l.Description, l.Sku, l.Quantity, l.UnitPrice, l.Amount, null)).ToList()), lenient: true);
    }

    /// <summary>Sets the fields as checked by a person (or matched by the service after reading).</summary>
    public void Review(DocumentReview review)
    {
        EnsureNew();
        Apply(review, lenient: false);
    }

    public void Complete(string resultType, Guid resultId, DateTimeOffset now)
    {
        EnsureNew();
        Status = CapturedDocumentStatus.Done;
        ResultType = resultType;
        ResultId = resultId;
        DoneAt = now.ToUniversalTime();
    }

    public void Dismiss(DateTimeOffset now)
    {
        EnsureNew();
        Status = CapturedDocumentStatus.Dismissed;
        DoneAt = now.ToUniversalTime();
    }

    /// <summary>Puts a dismissed document back in the inbox.</summary>
    public void Restore()
    {
        if (Status != CapturedDocumentStatus.Dismissed)
            throw new DomainException("document.not_dismissed", "Only a dismissed document can be put back.");
        Status = CapturedDocumentStatus.New;
        DoneAt = null;
    }

    /// <param name="lenient">For readings: text too long is cut and odd numbers dropped instead of refused, since nobody typed them.</param>
    private void Apply(DocumentReview review, bool lenient)
    {
        ArgumentNullException.ThrowIfNull(review);
        if (DocumentKinds.IsFromVendor(Kind) && review.CustomerId is not null)
            throw new DomainException("document.party_kind", "This document comes from a vendor.");
        if (!DocumentKinds.IsFromVendor(Kind) && (review.VendorId is not null || review.PurchaseOrderId is not null))
            throw new DomainException("document.party_kind", "This document comes from a customer.");
        if (review.Lines.Count > MaxLines)
            throw new DomainException("document.too_many_lines", $"A document can have at most {MaxLines} lines.");

        Party = Field(review.Party, 200, "document.party_too_long", "Name", lenient);
        VendorId = review.VendorId;
        CustomerId = review.CustomerId;
        Number = Field(review.Number, 50, "document.number_too_long", "Number", lenient);
        Date = review.Date;
        DueDate = review.DueDate;
        OrderReference = Field(review.OrderReference, 50, "document.reference_too_long", "Order reference", lenient);
        PurchaseOrderId = review.PurchaseOrderId;
        Subtotal = Amount(review.Subtotal, lenient);
        Tax = Amount(review.Tax, lenient);
        Shipping = Amount(review.Shipping, lenient);
        Total = Amount(review.Total, lenient);

        _lines.Clear();
        foreach (var line in review.Lines)
        {
            var description = Field(line.Description, 500, "document.description_too_long", "Description", lenient);
            var sku = Field(line.Sku, 50, "document.sku_too_long", "Code", lenient);
            var quantity = Quantity(line.Quantity, lenient);
            var price = Rate(line.UnitPrice, lenient);
            var amount = Amount(line.Amount, lenient);
            if (description is null && sku is null && quantity is null && price is null && amount is null && line.ItemId is null)
                continue;
            _lines.Add(new CapturedDocumentLine(_lines.Count + 1, description, sku, quantity, price, amount, line.ItemId));
        }
    }

    private static string? Field(string? value, int max, string code, string name, bool lenient)
    {
        var trimmed = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        if (lenient && trimmed is not null && trimmed.Length > max)
            trimmed = trimmed[..max].Trim();
        return Common.Text.Optional(trimmed, max, code, name);
    }

    private static decimal? Amount(decimal? value, bool lenient)
    {
        if (value is not { } v)
            return null;
        if (decimal.Round(v, 2) != v || Math.Abs(v) >= 100_000_000m)
        {
            if (lenient)
                return Math.Abs(v) >= 100_000_000m ? null : decimal.Round(v, 2, MidpointRounding.AwayFromZero);
            throw new DomainException("document.amount_invalid", "Amounts are in cents, below 100 million.");
        }

        return v;
    }

    private static decimal? Quantity(decimal? value, bool lenient)
    {
        if (value is not { } v)
            return null;
        if (decimal.Round(v, 4) != v || v < 0m || v >= 100_000_000m)
        {
            if (lenient)
                return v < 0m || v >= 100_000_000m ? null : decimal.Round(v, 4, MidpointRounding.AwayFromZero);
            throw new DomainException("document.quantity_invalid", "Quantities are positive, with at most 4 decimals.");
        }

        return v;
    }

    private static decimal? Rate(decimal? value, bool lenient)
    {
        if (value is not { } v)
            return null;
        if (decimal.Round(v, 4) != v || Math.Abs(v) >= 100_000_000m)
        {
            if (lenient)
                return Math.Abs(v) >= 100_000_000m ? null : decimal.Round(v, 4, MidpointRounding.AwayFromZero);
            throw new DomainException("document.price_invalid", "Unit prices have at most 4 decimals.");
        }

        return v;
    }

    private void EnsureNew()
    {
        if (Status != CapturedDocumentStatus.New)
            throw new DomainException("document.done", "This document has already been dealt with.");
    }
}

public sealed record DocumentReviewLine(string? Description, string? Sku, decimal? Quantity, decimal? UnitPrice, decimal? Amount, Guid? ItemId);

/// <summary>A document's fields as checked: the sender chosen from the vendors or customers, the purchase order, and lines with items.</summary>
public sealed record DocumentReview(
    string? Party,
    Guid? VendorId,
    Guid? CustomerId,
    string? Number,
    DateOnly? Date,
    DateOnly? DueDate,
    string? OrderReference,
    Guid? PurchaseOrderId,
    decimal? Subtotal,
    decimal? Tax,
    decimal? Shipping,
    decimal? Total,
    IReadOnlyList<DocumentReviewLine> Lines);
