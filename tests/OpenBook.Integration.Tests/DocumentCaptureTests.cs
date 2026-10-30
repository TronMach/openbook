using System.Text;
using Microsoft.Extensions.DependencyInjection;
using OpenBook.Application.Attachments;
using OpenBook.Application.Documents;
using OpenBook.Application.Purchases;
using OpenBook.Application.Sales;
using OpenBook.Domain.Accounts;
using OpenBook.Domain.Auditing;
using OpenBook.Domain.Common;
using OpenBook.Domain.Documents;
using OpenBook.Domain.Purchases;
using OpenBook.Domain.Sales;

namespace OpenBook.Integration.Tests;

/// <summary>The document inbox: reading vendor invoices, customer POs, packing slips and quotes, locally or with Claude.</summary>
public sealed class DocumentCaptureTests : IAsyncLifetime
{
    private static readonly DateOnly Oct1 = new(2026, 10, 1);

    private const string Invoice = """
        Parts Supply Co
        INVOICE
        Invoice #: PS-881
        Invoice Date: 10/12/2026
        Due Date: 11/11/2026
        Your PO: PO-NUMBER
        WID-1 Widget, blue 10 5.00 50.00
        Bracket kit 2 7.50 15.00
        Total 65.00
        """;

    private readonly LedgerDatabase _db = new();
    private Item _widget = null!;
    private Item _bracket = null!;
    private Vendor _supplier = null!;
    private Customer _contoso = null!;

    public async Task InitializeAsync()
    {
        await _db.InitializeAsync();
        var sales = await _db.CreateAccount("Sales", AccountType.Income, "4000");
        _widget = await In<ItemService, Item>(s => s.CreateAsync(new ItemDetails("Widget", ItemType.Inventory, sales.Id, 20m, Barcode: "WID-1")));
        _bracket = await In<ItemService, Item>(s => s.CreateAsync(new ItemDetails("Bracket kit", ItemType.NonInventory, sales.Id, 12m)));
        _supplier = await In<VendorService, Vendor>(s => s.CreateAsync(new VendorDetails("Parts Supply Co")));
        _contoso = await In<CustomerService, Customer>(s => s.CreateAsync(new CustomerDetails("Contoso Hardware")));
    }

    public Task DisposeAsync() => _db.DisposeAsync();

    private Task<T> In<TService, T>(Func<TService, Task<T>> work) where TService : notnull =>
        _db.InScope(sp => work(sp.GetRequiredService<TService>()));

    private static async Task<string> Refused(Func<Task> action) => (await Assert.ThrowsAsync<DomainException>(action)).Code;

    private Task<CapturedDocument> Upload(DocumentKind kind, string fileName, string text, bool? useClaude = null) =>
        In<DocumentCaptureService, CapturedDocument>(s => s.UploadAsync(kind, fileName, new MemoryStream(Encoding.UTF8.GetBytes(text)), useClaude));

    private Task<CapturedDocument?> Get(Guid id) => In<DocumentCaptureService, CapturedDocument?>(s => s.GetAsync(id));

    [Fact]
    public async Task A_vendor_invoice_is_read_on_the_server_and_matched_to_the_vendor_order_and_items()
    {
        var order = await In<PurchaseOrderService, PurchaseOrder>(s => s.CreateAsync(new SavePurchaseOrderCommand(_supplier.Id, Oct1,
            [new PurchaseOrderLineDraft(_widget.Id, null, null, 10m, 5m)])));

        var document = await Upload(DocumentKind.VendorInvoice, "invoice.txt", Invoice.Replace("NUMBER", order.Number, StringComparison.Ordinal));

        Assert.Equal(DocumentReadMethods.Text, document.ReadBy);
        Assert.Null(document.ReadNote);
        Assert.Equal(("Parts Supply Co", _supplier.Id, "PS-881", $"PO-{order.Number}", order.Id),
            (document.Party, document.VendorId, document.Number, document.OrderReference, document.PurchaseOrderId));
        Assert.Equal((new DateOnly(2026, 10, 12), new DateOnly(2026, 11, 11), 65.00m), (document.Date, document.DueDate, document.Total));
        Assert.Equal([_widget.Id, _bracket.Id], document.Lines.Select(l => l.ItemId!.Value));
        Assert.Equal((10m, 5.00m, 50.00m), (document.Lines[0].Quantity, document.Lines[0].UnitPrice, document.Lines[0].Amount));
        Assert.Empty(_db.Claude.Calls);

        var files = await In<AttachmentService, IReadOnlyList<OpenBook.Domain.Attachments.Attachment>>(s => s.ListAsync(DocumentCaptureService.EntityType, document.Id));
        Assert.Equal("invoice.txt", Assert.Single(files).FileName);
    }

    [Fact]
    public async Task Claude_reads_documents_once_switched_on_and_falls_back_to_the_server_when_it_fails()
    {
        Assert.Equal("document_reading.key_required", await Refused(() =>
            In<DocumentCaptureService, DocumentReadingSettings>(s => s.SaveSettingsAsync(new DocumentReadingSettingsCommand(true)))));
        await In<DocumentCaptureService, DocumentReadingSettings>(s => s.SaveSettingsAsync(new DocumentReadingSettingsCommand(true, null, "sk-ant-test")));

        _db.Claude.Answer = new DocumentFields("Contoso Hardware", "55821", Oct1, null, null, null, null, null, 30m,
            [new DocumentLineFields("Bracket kit, zinc", null, 2m, 15m, 30m)]);
        var order = await Upload(DocumentKind.CustomerOrder, "po.pdf", "%PDF-1.4 not really");
        Assert.Equal(DocumentReadMethods.Claude, order.ReadBy);
        Assert.Equal(("sk-ant-test", DocumentReadingSettings.DefaultModel, DocumentKind.CustomerOrder), (_db.Claude.Calls[0].ApiKey, _db.Claude.Calls[0].Model, _db.Claude.Calls[0].Kind));
        Assert.Equal((_contoso.Id, (Guid?)null, "55821"), (order.CustomerId, order.VendorId, order.Number));
        Assert.Equal(_bracket.Id, Assert.Single(order.Lines).ItemId);

        // Kept on the server when asked, and when Claude fails.
        await Upload(DocumentKind.VendorQuote, "quote.txt", "Parts Supply Co\nQuote # Q-1\nWidget 3 5.00 15.00", useClaude: false);
        Assert.Single(_db.Claude.Calls);
        _db.Claude.Failure = "Anthropic couldn't be reached";
        var quote = await Upload(DocumentKind.VendorQuote, "quote2.txt", "Parts Supply Co\nQuote # Q-2\nWidget 3 5.00 15.00");
        Assert.Equal((DocumentReadMethods.Text, "Q-2", _supplier.Id), (quote.ReadBy, quote.Number, quote.VendorId));
        Assert.Contains("couldn't be reached", quote.ReadNote, StringComparison.Ordinal);

        // Read again with Claude once it works.
        _db.Claude.Failure = null;
        _db.Claude.Answer = new DocumentFields("Parts Supply Co", "Q-2", Oct1, null, null, null, null, null, 15m, []);
        var again = await In<DocumentCaptureService, CapturedDocument>(s => s.ReadAgainAsync(quote.Id, useClaude: true));
        Assert.Equal((DocumentReadMethods.Claude, (string?)null, 15m), (again.ReadBy, again.ReadNote, again.Total));

        // Audit snapshots never carry the key.
        var settingsAudit = await _db.InDb(db => Task.FromResult(db.AuditRecords.Where(a => a.EntityType == DocumentCaptureService.SettingsEntity).ToList()));
        Assert.DoesNotContain(settingsAudit, a => (a.After ?? "").Contains("sk-ant", StringComparison.Ordinal));

        await In<DocumentCaptureService, DocumentReadingSettings>(s => s.SaveSettingsAsync(new DocumentReadingSettingsCommand(false, ClearApiKey: true)));
        Assert.Equal("document_reading.claude_off", await Refused(() => In<DocumentCaptureService, CapturedDocument>(s => s.ReadAgainAsync(quote.Id, useClaude: true))));
    }

    [Fact]
    public async Task Scans_without_an_ocr_engine_are_kept_with_a_note_and_can_be_filled_in_by_hand()
    {
        var scan = await In<DocumentCaptureService, CapturedDocument>(s => s.UploadAsync(DocumentKind.PackingSlip, "slip.jpg", new MemoryStream([0xFF, 0xD8, 0xFF, 0xE0])));
        Assert.Null(scan.ReadBy);
        Assert.Contains("no OCR engine", scan.ReadNote, StringComparison.Ordinal);
        Assert.Equal("attachment.type_not_allowed", await Refused(() =>
            In<DocumentCaptureService, CapturedDocument>(s => s.UploadAsync(DocumentKind.PackingSlip, "slip.exe", new MemoryStream([1, 2, 3])))));

        var review = new DocumentReview("Parts Supply Co", _supplier.Id, null, "PS-1", Oct1, null, null, null, null, null, null, null,
            [new DocumentReviewLine("Widget", "WID-1", 8m, null, null, _widget.Id), new DocumentReviewLine(null, null, null, null, null, null)]);
        var reviewed = await In<DocumentCaptureService, CapturedDocument>(s => s.ReviewAsync(scan.Id, review));
        Assert.Equal((_supplier.Id, 8m), (reviewed.VendorId, Assert.Single(reviewed.Lines).Quantity));

        Assert.Equal("document.party_kind", await Refused(() => In<DocumentCaptureService, CapturedDocument>(s => s.ReviewAsync(scan.Id, review with { CustomerId = _contoso.Id }))));
        var otherVendor = await In<VendorService, Vendor>(s => s.CreateAsync(new VendorDetails("Other Supplier")));
        var order = await In<PurchaseOrderService, PurchaseOrder>(s => s.CreateAsync(new SavePurchaseOrderCommand(otherVendor.Id, Oct1,
            [new PurchaseOrderLineDraft(_widget.Id, null, null, 1m, 5m)])));
        Assert.Equal("document.order_vendor", await Refused(() => In<DocumentCaptureService, CapturedDocument>(s => s.ReviewAsync(scan.Id, review with { PurchaseOrderId = order.Id }))));
        await Assert.ThrowsAsync<EntityNotFoundException>(() => In<DocumentCaptureService, CapturedDocument>(s => s.ReviewAsync(scan.Id, review with { VendorId = Guid.NewGuid() })));

        await In<DocumentCaptureService, CapturedDocument>(s => s.DismissAsync(scan.Id));
        Assert.Equal("document.done", await Refused(() => In<DocumentCaptureService, CapturedDocument>(s => s.ReviewAsync(scan.Id, review))));
        await In<DocumentCaptureService, CapturedDocument>(s => s.RestoreAsync(scan.Id));
        Assert.Equal(CapturedDocumentStatus.New, (await Get(scan.Id))!.Status);

        var audits = await _db.InDb(db => Task.FromResult(db.AuditRecords.Count(a => a.EntityType == DocumentCaptureService.EntityType && a.EntityId == scan.Id)));
        // Created, the file attached, reviewed, dismissed, restored.
        Assert.Equal(5, audits);
    }

    [Fact]
    public void Claudes_answer_is_mapped_to_the_fields_and_anything_malformed_is_a_read_failure()
    {
        var fields = OpenBook.Infrastructure.Documents.ClaudeDocumentReader.Parse("""
            {"party":" Parts Supply Co ","number":"PS-9","date":"2026-10-12","due_date":"12/01/2026","order_reference":null,
             "subtotal":65,"tax":null,"shipping":0,"total":65.00,
             "lines":[{"sku":"WID-1","description":"Widget","quantity":10,"unit_price":5,"amount":50},{"sku":null,"description":"","quantity":"2","unit_price":null,"amount":15}]}
            """);
        Assert.Equal(("Parts Supply Co", "PS-9", new DateOnly(2026, 10, 12), (DateOnly?)null, 65.00m, 0m), (fields.Party, fields.Number, fields.Date!.Value, fields.DueDate, fields.Total!.Value, fields.Shipping!.Value));
        Assert.Equal([new DocumentLineFields("Widget", "WID-1", 10m, 5m, 50m), new DocumentLineFields(null, null, null, null, 15m)], fields.Lines);
        Assert.Throws<DocumentReadException>(() => OpenBook.Infrastructure.Documents.ClaudeDocumentReader.Parse("not json"));
    }
}
