using System.Text;
using OpenBook.Application.Banking;
using OpenBook.Application.Channels;
using OpenBook.Application.Import;
using OpenBook.Application.Import.Migration;
using OpenBook.Domain.Channels;
using OpenBook.Domain.Common;
using OpenBook.Domain.Documents;

namespace OpenBook.Integration.Tests.Fuzz;

/// <summary>What the fuzzer found (2026-11-12), each kept as a plain test of the input that broke it. No database.</summary>
public sealed class FuzzFindingsTests
{
    private static void ReadsOrRefuses(Action read)
    {
        var error = Record.Exception(read);
        Assert.True(error is null or DomainException, $"{error?.GetType().Name}: {error?.Message}");
    }

    [Fact]
    public void A_bank_feed_time_beyond_any_date_is_unreadable_not_a_crash()
    {
        // FromUnixTimeSeconds threw ArgumentOutOfRangeException, failing the whole sync.
        const string json = """
            {"errors": [], "accounts": [{"org": {"name": "Bank"}, "id": "A1", "name": "Checking", "currency": "USD", "balance": "10.00",
              "balance-date": 177236644294967296,
              "transactions": [{"id": "T1", "posted": 253402300799, "amount": "-5.00", "description": "Coffee"}]}]}
            """;
        ReadsOrRefuses(() => SimpleFin.Parse(json, TimeZoneInfo.FindSystemTimeZoneById("Pacific/Auckland")));
    }

    [Fact]
    public void A_huge_quantity_or_price_in_an_orders_report_is_unreadable()
    {
        // price × quantity overflowed decimal.
        const string orders =
            "amazon-order-id\tpurchase-date\torder-status\tproduct-name\tsku\tquantity\titem-price\titem-tax\tshipping-price\tship-state\n" +
            "113-1\t2026-03-02T10:00:00+00:00\tShipped\tBlue mug\tMUG-1\t99999999999999999999\t80.00\t6.80\t5.00\tWA\n" +
            "113-2\t2026-03-02T10:00:00+00:00\tShipped\tRed mug\tMUG-2\t2\t\t\t\tWA\n";
        ReadsOrRefuses(() => ChannelOrderReports.Read(ChannelKind.Amazon, orders));
        var etsy = "Sale Date,Item Name,Quantity,Price,Discount Amount,Order Shipping,Order Sales Tax,Ship State,Order ID,SKU\n" +
            "03/02/26,Blue mug,1,40.00,0,6.00,3.20,NY,900,MUG-1\n" +
            "03/02/26,Red mug,2,79228162514264337593543950335,4294967296,6.00,3.20,NY,900,MUG-2\n";
        ReadsOrRefuses(() => ChannelOrderReports.Read(ChannelKind.Etsy, etsy));
    }

    [Fact]
    public void Payout_reports_with_absurd_amounts_are_refused_or_read_not_crashed()
    {
        // Sums of the report's lines overflowed decimal.
        var etsy = "Date,Type,Title,Info,Currency,Amount,Fees & Taxes,Net,Tax Details\n" +
            string.Concat(Enumerable.Repeat("\"March 2, 2026\",Sale,Payment,Order #1,USD,$79228162514264337593543950335.00,--,$79228162514264337593543950335.00,--\n", 3));
        ReadsOrRefuses(() => ChannelReports.Read(ChannelKind.Etsy, etsy));
        var general = "Transaction Date,Type,Order,Payout Status,Payout Date,Payout ID,Amount,Fee,Net\n" +
            "2026-03-02,charge,#1,paid,2026-03-04,778,99999999999999999999999999.00,1.00,99999999999999999999999998.00\n" +
            "2026-03-02,charge,#2,paid,2026-03-04,778,99999999999999999999999999.00,1.00,99999999999999999999999998.00\n";
        ReadsOrRefuses(() => ChannelReports.Read(ChannelKind.Shopify, general, "payouts.csv"));
    }

    [Theory]
    [InlineData("99,999,999,999,999.99", true)]
    [InlineData("100,000,000,000,000.00", false)]
    [InlineData("-99999999999999999999999.00", false)]
    public void Statement_amounts_stop_at_what_the_books_can_hold(string text, bool readable)
    {
        Assert.Equal(readable, StatementValues.TryParseAmount(text, out var amount));
        Assert.True(Math.Abs(amount) <= Money.MaxAmount);
    }

    [Fact]
    public void A_quickbooks_online_property_with_no_name_is_passed_over()
    {
        // property.Name[0] on "" threw IndexOutOfRangeException, which the readers' safety net doesn't turn into a message.
        const string json = """[{"": [{"a": 1}], "QueryResponse": {"Customer": [{"DisplayName": "Bo's Bakery"}]}}]""";
        var data = MigrationReaders.Read(MigrationSource.QuickBooksOnline, Encoding.UTF8.GetBytes(json), "USD");
        Assert.Equal("Bo's Bakery", Assert.Single(data.Customers).Details.DisplayName);
    }

    [Fact]
    public void Numbers_no_document_holds_are_read_as_words()
    {
        // quantity × price, or amount ÷ a tiny price, overflowed decimal.
        const string text = """
            INVOICE INV-1
            Widget  0.0000000000000000000000000001  99999999999999999999999999.00  5.00
            Gadget  99999999999999999999  99999999999999999999.00  1.00
            Copy paper  10  4.25  42.50
            Total due 42.50
            """;
        var fields = DocumentParser.Parse(text, DocumentKind.VendorInvoice, []);
        Assert.Contains(fields.Lines, l => l is { Quantity: 10m, UnitPrice: 4.25m, Amount: 42.50m });
    }
}
