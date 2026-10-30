using OpenBook.Domain.Common;
using OpenBook.Domain.Documents;

namespace OpenBook.Domain.Tests;

public class DocumentParserTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 30, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_vendor_invoice_gives_its_number_our_po_dates_totals_and_lines()
    {
        const string text = """
            Northwind Supply Co.
            12 Harbor Road
            Springfield IL 62704
            INVOICE
            Invoice #: NW-20931
            Invoice Date: 10/12/2026
            Due Date: 11/11/2026
            PO Number: PO-1004
            Bill To: Acme Books LLC
            Item Description Qty Unit Price Amount
            WID-100 Blue widget, large 10 EA 12.50 125.00
            GAD-7 Gadget 3 4.00 12.00
            2 BRK-9 Bracket set 7.25 14.50
            Setup fee 40.00
            Subtotal 191.50
            Sales Tax 8.25% 15.80
            Freight 20.00
            Total Due $227.30
            Thank you for your business
            """;
        var fields = DocumentParser.Parse(text, DocumentKind.VendorInvoice, ["Northwind Supply Co.", "Other Vendor"], "Acme Books LLC");

        Assert.Equal("Northwind Supply Co.", fields.Party);
        Assert.Equal("NW-20931", fields.Number);
        Assert.Equal("PO-1004", fields.OrderReference);
        Assert.Equal(new DateOnly(2026, 10, 12), fields.Date);
        Assert.Equal(new DateOnly(2026, 11, 11), fields.DueDate);
        Assert.Equal(191.50m, fields.Subtotal);
        Assert.Equal(15.80m, fields.Tax);
        Assert.Equal(20.00m, fields.Shipping);
        Assert.Equal(227.30m, fields.Total);

        Assert.Equal(4, fields.Lines.Count);
        Assert.Equal(new DocumentLineFields("Blue widget, large", "WID-100", 10m, 12.50m, 125.00m), fields.Lines[0]);
        Assert.Equal(new DocumentLineFields("Gadget", "GAD-7", 3m, 4.00m, 12.00m), fields.Lines[1]);
        Assert.Equal(new DocumentLineFields("Bracket set", "BRK-9", 2m, 7.25m, 14.50m), fields.Lines[2]);
        Assert.Equal(new DocumentLineFields("Setup fee", null, null, null, 40.00m), fields.Lines[3]);
    }

    [Fact]
    public void A_customer_po_gives_their_po_number_and_an_unknown_sender_is_the_first_named_line()
    {
        const string text = """
            PURCHASE ORDER
            Contoso Hardware
            P.O. Box 77, Dayton OH
            PO # 55821
            Order date 2026-10-20
            Vendor: Acme Books LLC
            Qty Item Price Total
            4 Ledger paper ream 6.00 24.00
            1 Binder, 3 inch 9.99 9.99
            Total 33.99
            """;
        var fields = DocumentParser.Parse(text, DocumentKind.CustomerOrder, [], "Acme Books LLC");

        Assert.Equal("Contoso Hardware", fields.Party);
        Assert.Equal("55821", fields.Number);
        Assert.Null(fields.OrderReference);
        Assert.Equal(new DateOnly(2026, 10, 20), fields.Date);
        Assert.Equal(33.99m, fields.Total);
        Assert.Equal(2, fields.Lines.Count);
        Assert.Equal(new DocumentLineFields("Ledger paper ream", null, 4m, 6.00m, 24.00m), fields.Lines[0]);
        Assert.Equal(new DocumentLineFields("Binder, 3 inch", null, 1m, 9.99m, 9.99m), fields.Lines[1]);
    }

    [Fact]
    public void A_packing_slip_gives_quantities_shipped_and_the_po_on_the_next_line()
    {
        const string text = """
            Northwind Supply Co.
            Packing Slip
            Slip No: PS-4410
            Ship Date: 10/14/2026
            Customer PO
            PO-1004
            Item Description Ordered Shipped
            WID-100 Blue widget, large 10 8
            GAD-7 Gadget 3 3
            Springfield IL 62704
            Suite 200
            """;
        var fields = DocumentParser.Parse(text, DocumentKind.PackingSlip, [], null);

        Assert.Equal("PS-4410", fields.Number);
        Assert.Equal("PO-1004", fields.OrderReference);
        Assert.Null(fields.Date);
        Assert.Equal(2, fields.Lines.Count);
        Assert.Equal(new DocumentLineFields("Blue widget, large", "WID-100", 8m, null, null), fields.Lines[0]);
        Assert.Equal(new DocumentLineFields("Gadget", "GAD-7", 3m, null, null), fields.Lines[1]);
    }

    [Fact]
    public void A_quote_gives_its_number_and_nothing_is_read_from_empty_text()
    {
        var fields = DocumentParser.Parse("Fabrikam Tools\nQuotation No. Q-7781\nDate: Oct 2, 2026\nValid until Nov 1, 2026\nTorque wrench 2 x 85.00 170.00\nTotal 170.00", DocumentKind.VendorQuote, [], null);
        Assert.Equal("Q-7781", fields.Number);
        Assert.Equal(new DateOnly(2026, 10, 2), fields.Date);
        Assert.Equal(new DocumentLineFields("Torque wrench", null, 2m, 85.00m, 170.00m), Assert.Single(fields.Lines));

        Assert.Same(DocumentFields.Empty, DocumentParser.Parse("  ", DocumentKind.VendorQuote, [], null));
    }

    [Fact]
    public void A_reading_is_kept_leniently_but_a_review_is_checked()
    {
        var document = CapturedDocument.Create(DocumentKind.VendorInvoice, "scan.pdf", Now, "owner");
        document.Read("text", DocumentReadMethods.Text, null, new DocumentFields(new string('N', 250), "1", null, null, null, null, null, null, 10.555m,
            [new DocumentLineFields("Widget", null, 1.00005m, 3m, 3m), new DocumentLineFields(null, null, null, null, null)]));
        Assert.Equal(200, document.Party!.Length);
        Assert.Equal(10.56m, document.Total);
        Assert.Equal(1.0001m, Assert.Single(document.Lines).Quantity);

        var review = new DocumentReview("Northwind", null, null, "1", null, null, null, null, null, null, null, 10.555m, []);
        Assert.Equal("document.amount_invalid", Assert.Throws<DomainException>(() => document.Review(review)).Code);
        Assert.Equal("document.party_kind", Assert.Throws<DomainException>(() => document.Review(review with { Total = null, CustomerId = Guid.NewGuid() })).Code);

        document.Dismiss(Now);
        Assert.Equal("document.done", Assert.Throws<DomainException>(() => document.Review(review with { Total = 1m })).Code);
        document.Restore();
        document.Complete("bill", Guid.NewGuid(), Now);
        Assert.Equal(CapturedDocumentStatus.Done, document.Status);
        Assert.Equal("document.not_dismissed", Assert.Throws<DomainException>(document.Restore).Code);
    }

    [Fact]
    public void Claude_can_only_be_switched_on_with_a_key()
    {
        var settings = DocumentReadingSettings.Create();
        Assert.Equal("document_reading.key_required", Assert.Throws<DomainException>(() => settings.Update(true, null, null)).Code);
        settings.Update(true, null, "protected");
        Assert.True(settings.IsReady);
        Assert.Equal(DocumentReadingSettings.DefaultModel, settings.Model);
        Assert.Equal("document_reading.model_invalid", Assert.Throws<DomainException>(() => settings.Update(true, "bad model!", null)).Code);
        Assert.Equal("document_reading.key_required", Assert.Throws<DomainException>(() => settings.Update(true, null, "")).Code);
    }
}
