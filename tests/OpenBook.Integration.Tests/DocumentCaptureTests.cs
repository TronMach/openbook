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

    // ---- Turning documents into records ----

    private Task<PurchaseOrder> WidgetOrder(decimal quantity = 10m) =>
        In<PurchaseOrderService, PurchaseOrder>(s => s.CreateAsync(new SavePurchaseOrderCommand(_supplier.Id, Oct1, [new PurchaseOrderLineDraft(_widget.Id, null, null, quantity, 5m)])));

    [Fact]
    public async Task A_vendor_invoice_becomes_a_bill_from_its_order_with_the_rest_as_category_lines()
    {
        var supplies = await _db.CreateAccount("Supplies", AccountType.Expense, "6100");
        var freight = await _db.CreateAccount("Freight In", AccountType.CostOfGoodsSold, "5100");
        var order = await WidgetOrder();
        var text = Invoice.Replace("NUMBER", order.Number, StringComparison.Ordinal).Replace("Total 65.00", "Freight 10.00\nTotal 75.00", StringComparison.Ordinal);
        var document = await Upload(DocumentKind.VendorInvoice, "invoice.txt", text);
        Assert.Equal((10.00m, 75.00m), (document.Shipping, document.Total));

        // The vendor has no default category, so the bracket line needs one.
        Assert.Equal("document.category_required", await Refused(() =>
            In<DocumentConversionService, Bill>(s => s.MakeBillAsync(document.Id, new DocumentAccounts()))));
        var accounts = new DocumentAccounts(supplies.Id, ShippingAccountId: freight.Id);

        // A total that doesn't add up is refused, and nothing is made.
        var reviewed = await In<DocumentCaptureService, CapturedDocument>(s => s.ReviewAsync(document.Id, Review(document) with { Total = 80m }));
        Assert.Equal("document.total_mismatch", await Refused(() => In<DocumentConversionService, Bill>(s => s.MakeBillAsync(document.Id, accounts))));
        Assert.Empty(await In<BillService, IReadOnlyList<Bill>>(s => s.ListAsync()));
        await In<DocumentCaptureService, CapturedDocument>(s => s.ReviewAsync(document.Id, Review(reviewed) with { Total = 75m }));

        var bill = await In<DocumentConversionService, Bill>(s => s.MakeBillAsync(document.Id, accounts));
        Assert.Equal((order.Id, "PS-881", 75.00m, new DateOnly(2026, 11, 11)), (bill.PurchaseOrderId, bill.Reference, bill.Total, bill.DueDate));
        Assert.Equal([(_widget.Id, 50.00m), ((Guid?)null, 15.00m), (null, 10.00m)], bill.Lines.Select(l => (l.ItemId, l.Amount)));
        Assert.Equal([supplies.Id, freight.Id], bill.Lines.Skip(1).Select(l => l.AccountId));

        var done = (await Get(document.Id))!;
        Assert.Equal((CapturedDocumentStatus.Done, "bill", bill.Id), (done.Status, done.ResultType, done.ResultId!.Value));
        Assert.Single(await In<AttachmentService, IReadOnlyList<OpenBook.Domain.Attachments.Attachment>>(s => s.ListAsync("bill", bill.Id)));
        Assert.Equal("document.done", await Refused(() => In<DocumentConversionService, Bill>(s => s.MakeBillAsync(document.Id, accounts))));
    }

    [Fact]
    public async Task A_customer_po_becomes_a_sales_order_and_a_quote_a_purchase_order()
    {
        var po = await Upload(DocumentKind.CustomerOrder, "po.txt", "Contoso Hardware\nPO # 55821\nOrder date 2026-10-20\n4 Bracket kit 6.00 24.00\nPlease deliver to dock 3\nTotal 24.00");
        Assert.Equal((_contoso.Id, _bracket.Id), (po.CustomerId, Assert.Single(po.Lines).ItemId));
        Assert.Equal("document.wrong_kind", await Refused(() => In<DocumentConversionService, Bill>(s => s.MakeBillAsync(po.Id, new DocumentAccounts()))));
        var order = await In<DocumentConversionService, SalesOrder>(s => s.MakeSalesOrderAsync(po.Id));
        Assert.Equal(("55821", new DateOnly(2026, 10, 20)), (order.CustomerReference, order.Date));
        var line = Assert.Single(order.Lines, l => l.ItemId is not null);
        Assert.Equal((_bracket.Id, 4m, 6m), (line.ItemId!.Value, line.Quantity, line.Rate));

        var supplies = await _db.CreateAccount("Supplies", AccountType.Expense, "6100");
        var quote = await Upload(DocumentKind.VendorQuote, "quote.txt", "Parts Supply Co\nQuote # Q-1\nWID-1 Widget 3 5.00 15.00\nInstallation 40.00\nTotal 55.00");
        Assert.Equal("document.category_required", await Refused(() => In<DocumentConversionService, PurchaseOrder>(s => s.MakePurchaseOrderAsync(quote.Id, new DocumentAccounts()))));
        var purchase = await In<DocumentConversionService, PurchaseOrder>(s => s.MakePurchaseOrderAsync(quote.Id, new DocumentAccounts(supplies.Id)));
        Assert.Equal((_supplier.Id, "Quote Q-1", 55m), (purchase.VendorId, purchase.Memo, purchase.Total));
        Assert.Equal([(_widget.Id, (Guid?)null, 3m, 5m), (null, supplies.Id, 1m, 40m)], purchase.Lines.Select(l => (l.ItemId, l.AccountId, l.Quantity, l.UnitCost)));
    }

    [Fact]
    public async Task A_packing_slip_receives_goods_on_its_order_and_learned_codes_match_next_time()
    {
        var order = await WidgetOrder();
        var slip = await Upload(DocumentKind.PackingSlip, "slip.txt", $"Parts Supply Co\nPacking Slip\nSlip No: PS-1\nCustomer PO\nPO-{order.Number}\nWID-1 Widget, blue 10 8\nXB-77 Brkt large 2 2");
        Assert.Equal((order.Id, _widget.Id, (Guid?)null), (slip.PurchaseOrderId, slip.Lines[0].ItemId, slip.Lines[1].ItemId));

        // The bracket isn't on the order: refused until the line is cleared.
        var review = Review(slip) with { Lines = [.. Review(slip).Lines.Take(1), Review(slip).Lines[1] with { ItemId = _bracket.Id }] };
        await In<DocumentCaptureService, CapturedDocument>(s => s.ReviewAsync(slip.Id, review));
        Assert.Equal("document.not_on_order", await Refused(() => In<DocumentConversionService, PurchaseOrder>(s => s.ReceiveAsync(slip.Id))));
        await In<DocumentCaptureService, CapturedDocument>(s => s.ReviewAsync(slip.Id, review with { Lines = [.. review.Lines.Take(1)] }));
        var received = await In<DocumentConversionService, PurchaseOrder>(s => s.ReceiveAsync(slip.Id, new DateOnly(2026, 10, 14)));
        Assert.Equal(8m, received.Received[1]);
        Assert.Equal("Packing slip PS-1", Assert.Single(received.Receipts).Note);

        // The vendor's code XB-77 was learned for the bracket kit.
        var next = await Upload(DocumentKind.VendorInvoice, "next.txt", "Parts Supply Co\nInvoice # 9\nXB-77 Something else 1 7.50 7.50");
        Assert.Equal(_bracket.Id, Assert.Single(next.Lines).ItemId);
    }

    private static DocumentReview Review(CapturedDocument d) => new(
        d.Party, d.VendorId, d.CustomerId, d.Number, d.Date, d.DueDate, d.OrderReference, d.PurchaseOrderId, d.Subtotal, d.Tax, d.Shipping, d.Total,
        d.Lines.Select(l => new DocumentReviewLine(l.Description, l.Sku, l.Quantity, l.UnitPrice, l.Amount, l.ItemId)).ToList());
}
