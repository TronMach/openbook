using System.Text;
using Microsoft.Extensions.DependencyInjection;
using OpenBook.Application.Accounts;
using OpenBook.Application.Channels;
using OpenBook.Application.Ledger;
using OpenBook.Application.Purchases;
using OpenBook.Application.Inventory;
using OpenBook.Application.Reporting;
using OpenBook.Application.Sales;
using OpenBook.Domain.Accounts;
using OpenBook.Domain.Channels;
using OpenBook.Domain.Common;
using OpenBook.Domain.Ledger;

namespace OpenBook.Integration.Tests;

/// <summary>L11 e-commerce: sales channels, payout reports read into typed lines, and posting payouts.</summary>
public sealed class ChannelTests : IAsyncLifetime
{
    private readonly LedgerDatabase _db = new();
    private Account _checking = null!;

    // Amazon settlement flat file V2 (tab-separated): the first row is the settlement's totals.
    private const string Amazon =
        "settlement-id\tsettlement-start-date\tsettlement-end-date\tdeposit-date\ttotal-amount\tcurrency\ttransaction-type\torder-id\tamount-type\tamount-description\tamount\tposted-date\tsku\n" +
        "111\t2026-03-01 00:00:00 UTC\t2026-03-14 00:00:00 UTC\t2026-03-16 00:00:00 UTC\t73.50\tUSD\t\t\t\t\t\t\t\n" +
        "111\t\t\t\t\t\tOrder\t113-1\tItemPrice\tPrincipal\t80.00\t2026-03-02 10:00:00 UTC\tMUG-1\n" +
        "111\t\t\t\t\t\tOrder\t113-1\tItemPrice\tShipping\t5.00\t2026-03-02 10:00:00 UTC\tMUG-1\n" +
        "111\t\t\t\t\t\tOrder\t113-1\tItemPrice\tTax\t6.80\t2026-03-02 10:00:00 UTC\tMUG-1\n" +
        "111\t\t\t\t\t\tOrder\t113-1\tItemWithheldTax\tMarketplaceFacilitatorTax-Principal\t-6.80\t2026-03-02 10:00:00 UTC\tMUG-1\n" +
        "111\t\t\t\t\t\tOrder\t113-1\tItemFees\tCommission\t-12.00\t2026-03-02 10:00:00 UTC\tMUG-1\n" +
        "111\t\t\t\t\t\tRefund\t113-2\tItemPrice\tPrincipal\t-10.00\t2026-03-05 10:00:00 UTC\tMUG-1\n" +
        "111\t\t\t\t\t\tRefund\t113-2\tItemFees\tCommission\t1.50\t2026-03-05 10:00:00 UTC\tMUG-1\n" +
        "111\t\t\t\t\t\tServiceFee\t\tother-transaction\tSubscription Fee\t-39.99\t2026-03-10 10:00:00 UTC\t\n" +
        "111\t\t\t\t\t\tother-transaction\t\tother-transaction\tFBA Inventory Reimbursement\t48.99\t2026-03-11 10:00:00 UTC\t\n";

    // eBay transaction report: a preamble, then the table.
    private const string Ebay =
        "\"Transaction report\"\n\"Start date\",\"2026-03-01\"\n\n" +
        "\"Transaction creation date\",\"Type\",\"Order number\",\"Item title\",\"Ship to province/region/state\",\"Net amount\",\"Payout date\",\"Payout ID\",\"Item subtotal\",\"Shipping and handling\",\"Seller collected tax\",\"eBay collected tax\",\"Final Value Fee - fixed\",\"Final Value Fee - variable\",\"Gross transaction amount\"\n" +
        "\"Mar 2, 2026\",\"Order\",\"01-1\",\"Blue mug\",\"CA\",\"40.20\",\"Mar 4, 2026\",\"P-9\",\"40.00\",\"5.00\",\"--\",\"3.60\",\"-0.30\",\"-4.50\",\"48.60\"\n" +
        "\"Mar 3, 2026\",\"Refund\",\"01-0\",\"Red mug\",\"NY\",\"-12.00\",\"Mar 4, 2026\",\"P-9\",\"--\",\"--\",\"--\",\"--\",\"--\",\"--\",\"-12.00\"\n" +
        "\"Mar 4, 2026\",\"Payout\",\"--\",\"--\",\"--\",\"-28.20\",\"Mar 4, 2026\",\"P-9\",\"--\",\"--\",\"--\",\"--\",\"--\",\"--\",\"--\"\n";

    private const string Etsy =
        "Date,Type,Title,Info,Currency,Amount,Fees & Taxes,Net,Tax Details\n" +
        "\"March 2, 2026\",Sale,Payment for Order #1,Order #1,USD,$25.00,--,$25.00,--\n" +
        "\"March 2, 2026\",Fee,Transaction fee: Mug,Order #1,USD,--,-$1.63,-$1.63,--\n" +
        "\"March 2, 2026\",Tax,Sales tax paid by buyer,Order #1,USD,--,-$2.00,-$2.00,--\n" +
        "\"March 9, 2026\",Deposit,\"$21.37 sent to your bank account\",,USD,--,--,-$21.37,--\n";

    private const string PayPal =
        "\"Date\",\"Time\",\"TimeZone\",\"Name\",\"Type\",\"Status\",\"Currency\",\"Gross\",\"Fee\",\"Net\",\"Transaction ID\",\"Balance Impact\"\n" +
        "\"03/02/2026\",\"10:00:00\",\"PST\",\"Pat Buyer\",\"Express Checkout Payment\",\"Completed\",\"USD\",\"50.00\",\"-1.95\",\"48.05\",\"T1\",\"Credit\"\n" +
        "\"03/03/2026\",\"10:00:00\",\"PST\",\"Pat Buyer\",\"Payment Refund\",\"Completed\",\"USD\",\"-10.00\",\"0.30\",\"-9.70\",\"T2\",\"Debit\"\n" +
        "\"03/04/2026\",\"10:00:00\",\"PST\",\"\",\"General Withdrawal\",\"Completed\",\"USD\",\"-30.00\",\"0.00\",\"-30.00\",\"T3\",\"Debit\"\n" +
        "\"03/05/2026\",\"10:00:00\",\"PST\",\"Sam\",\"Express Checkout Payment\",\"Pending\",\"USD\",\"20.00\",\"-0.88\",\"19.12\",\"T4\",\"Credit\"\n";

    // A Shopify payouts export (read by the general reader): gross and fee, grouped by payout id.
    private const string Shopify =
        "Transaction Date,Type,Order,Payout Status,Payout Date,Payout ID,Amount,Fee,Net\n" +
        "2026-03-02,charge,#1001,paid,2026-03-04,778,100.00,3.20,96.80\n" +
        "2026-03-03,refund,#1000,paid,2026-03-04,778,-20.00,0.00,-20.00\n" +
        "2026-03-05,charge,#1002,paid,2026-03-07,779,50.00,1.75,48.25\n";

    public async Task InitializeAsync()
    {
        await _db.InitializeAsync();
        _checking = await _db.CreateAccount("Checking", AccountType.Bank, "1000");
    }

    public Task DisposeAsync() => _db.DisposeAsync();

    private Task<T> In<TService, T>(Func<TService, Task<T>> work) where TService : notnull =>
        _db.InScope(sp => work(sp.GetRequiredService<TService>()));

    private Task<SalesChannel> Channel(ChannelKind kind, string name = "Shop") =>
        In<ChannelService, SalesChannel>(s => s.CreateAsync(new ChannelDetails(name, kind, _checking.Id)));

    private Task<PayoutImportResult> Import(SalesChannel channel, string text, bool save = true) =>
        In<ChannelService, PayoutImportResult>(s => s.ImportAsync(channel.Id, "report.csv", Encoding.UTF8.GetBytes(text), save));

    private static decimal Total(PayoutData p, ChannelLineType type) => p.Lines.Where(l => l.Type == type).Sum(l => l.Amount);

    [Fact]
    public void Etsy_sales_come_out_net_of_the_tax_it_pays_on()
    {
        var month = Assert.Single(ChannelReports.Read(ChannelKind.Etsy, Etsy).Payouts);

        Assert.Equal(23m, Total(month, ChannelLineType.Sale));
        Assert.Equal(0m, Total(month, ChannelLineType.Tax) + Total(month, ChannelLineType.MarketplaceTax));
        Assert.Equal(21.37m, month.Net);
    }

    [Fact]
    public async Task An_overlapping_file_with_no_payout_ids_brings_only_what_is_new()
    {
        var channel = await Channel(ChannelKind.Etsy);
        await Import(channel, Etsy);

        var result = await Import(channel, Etsy + "\"March 12, 2026\",Sale,Payment for Order #2,Order #2,USD,$10.00,--,$10.00,--\n");

        var fresh = Assert.Single(result.New);
        Assert.Equal(10m, Assert.Single(fresh.Lines).Amount);
        Assert.Contains(result.Warnings, w => w.Contains("imported before", StringComparison.Ordinal));
        Assert.Equal(2, (await In<ChannelService, IReadOnlyList<ChannelPayout>>(s => s.PayoutsAsync(channel.Id))).Count);
    }

    [Fact]
    public void A_refund_takes_the_orders_tax_back_out()
    {
        var payout = new PayoutData("p1", new DateOnly(2026, 3, 9),
            [new ChannelLineDraft(new DateOnly(2026, 3, 2), ChannelLineType.Sale, 108m, "order 5000", null),
                new ChannelLineDraft(new DateOnly(2026, 3, 3), ChannelLineType.Refund, -108m, "order 5000", null)],
            new DateOnly(2026, 3, 2), new DateOnly(2026, 3, 3));

        var split = Assert.Single(ChannelSyncService.SplitCharges([payout], [new ChannelOrderDraft("5000", new DateOnly(2026, 3, 2), null, null, 1m, 100m, 0m, 8m)]));

        Assert.Equal(0m, Total(split, ChannelLineType.Tax));
        Assert.Equal(-100m, Total(split, ChannelLineType.Refund));
        Assert.Equal(100m, Total(split, ChannelLineType.Sale));
    }

    [Fact]
    public async Task A_line_with_no_sku_is_mapped_by_its_title()
    {
        var channel = await Channel(ChannelKind.Other);
        var orders = "Order ID,Sale Date,SKU,Title,Quantity,Price\nA1,2026-03-02,,Custom engraving,1,20.00\n";
        await In<ChannelOrderService, OrderImportResult>(s => s.ImportAsync(channel.Id, Encoding.UTF8.GetBytes(orders)));
        var batch = Assert.Single(await In<ChannelOrderService, IReadOnlyList<ChannelOrderBatch>>(s => s.BatchesAsync(channel.Id)));
        Assert.Equal("channel.sku_unmapped", (await Assert.ThrowsAsync<DomainException>(() => In<ChannelOrderService, ChannelOrderBatch>(s => s.PostAsync(batch.Id)))).Code);

        var choice = Assert.Single(await In<ChannelOrderService, IReadOnlyList<SkuChoice>>(s => s.SkusAsync(channel.Id)));
        Assert.Equal("Custom engraving", choice.Sku);
        await _db.InScope(async sp => { await sp.GetRequiredService<ChannelOrderService>().MapSkuAsync(channel.Id, choice.Sku, null); return true; });

        Assert.Equal(PayoutStatus.Posted, (await In<ChannelOrderService, ChannelOrderBatch>(s => s.PostAsync(batch.Id))).Status);
    }

    [Fact]
    public void An_amazon_settlement_reads_into_typed_lines_that_add_up_to_the_deposit()
    {
        var payout = Assert.Single(ChannelReports.Read(ChannelKind.Amazon, Amazon).Payouts);
        Assert.Equal(("111", new DateOnly(2026, 3, 16), 73.50m), (payout.ExternalId, payout.Date, payout.ReportedNet!.Value));
        Assert.Equal(73.50m, payout.Net);
        Assert.Equal(80m, Total(payout, ChannelLineType.Sale));
        Assert.Equal(5m, Total(payout, ChannelLineType.Shipping));
        Assert.Equal(6.80m, Total(payout, ChannelLineType.Tax));
        Assert.Equal(-6.80m, Total(payout, ChannelLineType.MarketplaceTax));
        Assert.Equal(-10m, Total(payout, ChannelLineType.Refund));
        Assert.Equal(-12m + 1.50m - 39.99m, Total(payout, ChannelLineType.Fee));
        Assert.Equal(48.99m, Total(payout, ChannelLineType.Adjustment));
        Assert.Equal((new DateOnly(2026, 3, 1), new DateOnly(2026, 3, 14)), (payout.PeriodStart!.Value, payout.PeriodEnd!.Value));
    }

    [Fact]
    public void Ebay_etsy_paypal_and_payout_exports_read_too()
    {
        var ebay = Assert.Single(ChannelReports.Read(ChannelKind.Ebay, Ebay).Payouts);
        Assert.Equal(("P-9", 28.20m, 28.20m), (ebay.ExternalId, ebay.ReportedNet!.Value, ebay.Net));
        Assert.Equal(40m, Total(ebay, ChannelLineType.Sale));
        Assert.Equal(3.60m, Total(ebay, ChannelLineType.Tax));
        Assert.Equal(-3.60m, Total(ebay, ChannelLineType.MarketplaceTax));
        Assert.Equal(-4.80m, Total(ebay, ChannelLineType.Fee));
        Assert.Equal(-12m, Total(ebay, ChannelLineType.Refund));
        Assert.Contains(ebay.Lines, l => l.Region == "CA");

        var etsy = ChannelReports.Read(ChannelKind.Etsy, Etsy);
        var month = Assert.Single(etsy.Payouts);
        Assert.Equal(21.37m, month.Net);
        Assert.Equal(-2m, Total(month, ChannelLineType.MarketplaceTax));
        Assert.Contains(etsy.Warnings, w => w.Contains("left out", StringComparison.Ordinal));

        var paypal = Assert.Single(ChannelReports.Read(ChannelKind.PayPal, PayPal).Payouts);
        Assert.Equal(38.35m, paypal.Net); // the pending payment and the withdrawal are left out
        Assert.Equal(-1.65m, Total(paypal, ChannelLineType.Fee));

        var shopify = ChannelReports.Read(ChannelKind.Shopify, Shopify).Payouts;
        Assert.Equal(["778", "779"], shopify.Select(p => p.ExternalId));
        Assert.Equal((76.80m, new DateOnly(2026, 3, 4)), (shopify[0].Net, shopify[0].Date));

        Assert.Equal("channel.wrong_report", Assert.Throws<DomainException>(() => ChannelReports.Read(ChannelKind.Amazon, Shopify)).Code);
    }

    [Fact]
    public async Task Account_rules_send_payout_lines_to_their_own_accounts()
    {
        var amazon = await Channel(ChannelKind.Amazon, "Amazon US");
        var subscriptions = await _db.CreateAccount("Software Subscriptions", AccountType.Expense);
        var reimbursed = await _db.CreateAccount("Inventory Reimbursements", AccountType.OtherIncome);
        var payout = Assert.Single((await Import(amazon, Amazon)).Saved);

        Assert.Equal("channel.rule_type", (await Assert.ThrowsAsync<DomainException>(() => In<ChannelService, SalesChannel>(s =>
            s.SetRulesAsync(amazon.Id, [new ChannelRuleDraft("Tax", ChannelLineType.Tax, subscriptions.Id)])))).Code);
        await In<ChannelService, SalesChannel>(s => s.SetRulesAsync(amazon.Id,
            [new ChannelRuleDraft("subscription", ChannelLineType.Fee, subscriptions.Id), new ChannelRuleDraft("Reimbursement", null, reimbursed.Id)]));

        var posted = await In<ChannelService, ChannelPayout>(s => s.PostAsync(payout.Id));
        var entry = (await In<PostingEngine, JournalEntry?>(e => e.GetAsync(posted.JournalEntryId!.Value)))!;
        decimal Line(Guid account) => entry.Lines.Where(l => l.AccountId == account).Sum(l => l.Amount);
        Assert.Equal(39.99m, Line(subscriptions.Id));
        Assert.Equal(10.50m, Line(amazon.FeesAccountId));
        Assert.Equal(-48.99m, Line(reimbursed.Id));
        Assert.Equal(0m, Line(amazon.AdjustmentsAccountId));
        Assert.Equal(73.50m, Line(_checking.Id));
    }

    [Fact]
    public async Task Payouts_import_once_post_as_one_entry_and_void_back_to_waiting()
    {
        var amazon = await Channel(ChannelKind.Amazon, "Amazon US");
        var chart = await In<AccountService, ChartOfAccounts>(s => s.GetChartAsync());
        Assert.NotNull(chart.Find(amazon.FeesAccountId));
        Assert.Equal(AccountType.OtherCurrentLiability, chart.Find(amazon.TaxAccountId)!.Type);

        var preview = await Import(amazon, Amazon, save: false);
        Assert.Single(preview.New);
        Assert.Empty(preview.Saved);
        var first = await Import(amazon, Amazon);
        var payout = Assert.Single(first.Saved);
        var again = await Import(amazon, Amazon);
        Assert.Empty(again.Saved);
        Assert.Single(again.AlreadyImported);

        var posted = await In<ChannelService, ChannelPayout>(s => s.PostAsync(payout.Id));
        Assert.Equal(PayoutStatus.Posted, posted.Status);
        var entry = (await In<PostingEngine, JournalEntry?>(e => e.GetAsync(posted.JournalEntryId!.Value)))!;
        Assert.Equal((ChannelPayout.Source, payout.Id), (entry.Source, entry.SourceId));
        decimal Line(Guid account) => entry.Lines.Where(l => l.AccountId == account).Sum(l => l.Amount);
        Assert.Equal(73.50m, Line(_checking.Id));
        Assert.Equal(-80m, Line(amazon.SalesAccountId));
        Assert.Equal(-5m, Line(amazon.ShippingAccountId));
        Assert.Equal(0m, Line(amazon.TaxAccountId)); // collected and paid by Amazon
        Assert.Equal(10m, Line(amazon.RefundsAccountId));
        Assert.Equal(50.49m, Line(amazon.FeesAccountId));
        Assert.Equal(-48.99m, Line(amazon.AdjustmentsAccountId));

        var report = await _db.InScope(sp => sp.GetRequiredService<ReportCatalog>().RunAsync("channel-sales", ReportParameters.Parse("preset=Custom&from=2026-03-01&to=2026-03-31")));
        Assert.Equal(73.50m, report.Rows.Single(r => r.Cells[0].Value as string == "Amazon US").Cells[10].Value);

        Assert.Equal("journal.void_document", (await Assert.ThrowsAsync<DomainException>(() =>
            In<PostingEngine, JournalEntry>(e => e.VoidAsync(entry.Id, "no")))).Code);
        var voided = await In<ChannelService, ChannelPayout>(s => s.VoidAsync(payout.Id, "Wrong account"));
        Assert.Equal((PayoutStatus.Pending, (Guid?)null), (voided.Status, voided.JournalEntryId));
        Assert.Equal(JournalEntryStatus.Voided, (await In<PostingEngine, JournalEntry?>(e => e.GetAsync(entry.Id)))!.Status);

        var dismissed = await In<ChannelService, ChannelPayout>(s => s.SetDismissedAsync(payout.Id, true));
        Assert.Equal(PayoutStatus.Dismissed, dismissed.Status);
        Assert.Equal("channel.payout_not_pending", (await Assert.ThrowsAsync<DomainException>(() => In<ChannelService, ChannelPayout>(s => s.PostAsync(payout.Id)))).Code);
    }

    [Fact]
    public async Task A_payout_that_doesnt_add_up_to_what_was_reported_waits()
    {
        var amazon = await Channel(ChannelKind.Amazon);
        var payout = Assert.Single((await Import(amazon, Amazon.Replace("\t73.50\t", "\t70.00\t", StringComparison.Ordinal))).Saved);
        Assert.Equal(-3.50m, payout.Difference);
        Assert.Equal("channel.payout_unbalanced", (await Assert.ThrowsAsync<DomainException>(() => In<ChannelService, ChannelPayout>(s => s.PostAsync(payout.Id)))).Code);
        Assert.Equal("channel.name_taken", (await Assert.ThrowsAsync<DomainException>(() => Channel(ChannelKind.Etsy))).Code);
        Assert.Equal("channel.deposit_required", (await Assert.ThrowsAsync<DomainException>(() =>
            In<ChannelService, SalesChannel>(s => s.CreateAsync(new ChannelDetails("Etsy", ChannelKind.Etsy, Guid.Empty))))).Code);
    }

    [Fact]
    public async Task A_charge_is_recognized_when_the_payout_posts_and_the_payment_stays_with_it()
    {
        var undeposited = await _db.CreateAccount("Undeposited Funds", AccountType.OtherCurrentAsset);
        var income = await _db.CreateAccount("Services", AccountType.Income);
        var item = await In<ItemService, Domain.Sales.Item>(s => s.CreateAsync(new Domain.Sales.ItemDetails("Work", Domain.Sales.ItemType.Service, income.Id, 50m)));
        var customer = await In<CustomerService, Domain.Sales.Customer>(s => s.CreateAsync(new Domain.Sales.CustomerDetails("Client")));
        var invoice = await In<InvoiceService, Domain.Sales.Invoice>(s => s.CreateAsync(new CreateInvoiceCommand(customer.Id, new DateOnly(2026, 3, 1), [new(item.Id, null, 1m, 50m)])));
        var stripe = await In<ChannelService, SalesChannel>(s => s.CreateAsync(new ChannelDetails("Stripe", ChannelKind.Stripe, _checking.Id, ClearingAccountId: undeposited.Id)));

        // The payout comes in first, with the charge as a sale.
        var report = string.Join(Environment.NewLine, "Date,Type,Amount,Fee,Net,Payout ID,Reference",
            "2026-03-10,charge,50.00,-1.45,48.55,po_9,pi_invoice", "2026-03-10,charge,100.00,-2.90,97.10,po_9,pi_other");
        var imported = await Import(stripe, report);
        var payout = Assert.Single(imported.Saved);
        Assert.Equal(0m, payout.Total(ChannelLineType.Cleared));
        Assert.Contains(imported.Warnings, w => w.Contains("payment reference", StringComparison.Ordinal));

        var payment = await In<PaymentService, Domain.Sales.CustomerPayment>(s => s.ReceiveAsync(new ReceivePaymentCommand(customer.Id, new DateOnly(2026, 3, 2), undeposited.Id,
            [new ApplyToInvoice(invoice.Id, 50m)], Reference: "pi_invoice")));

        var posted = await In<ChannelService, ChannelPayout>(s => s.PostAsync(payout.Id));

        Assert.Equal(50m, posted.Total(ChannelLineType.Cleared));
        Assert.Equal(100m, posted.Total(ChannelLineType.Sale));
        Assert.Empty(await In<DepositService, IReadOnlyList<UndepositedItem>>(s => s.UndepositedAsync()));
        Assert.Equal("payment.in_payout", (await Assert.ThrowsAsync<DomainException>(() => In<PaymentService, Domain.Sales.CustomerPayment>(s => s.VoidAsync(payment.Id, "Mistake")))).Code);

        await In<ChannelService, ChannelPayout>(s => s.VoidAsync(payout.Id, "Wrong"));
        Assert.Single(await In<DepositService, IReadOnlyList<UndepositedItem>>(s => s.UndepositedAsync()));
    }

    [Fact]
    public async Task Orders_waiting_to_be_posted_follow_a_later_report()
    {
        var shop = await Channel(ChannelKind.Shopify, "Waiting");
        const string Header = "Name,Created at,Financial Status,Shipping,Taxes,Lineitem quantity,Lineitem name,Lineitem price,Lineitem sku,Shipping Province,Cancelled at";
        var first = string.Join(Environment.NewLine, Header,
            "#1,2026-03-02 09:00:00 -0500,paid,0,0,1,Mug,40.00,MUG,CA,",
            "#2,2026-03-02 09:00:00 -0500,paid,0,0,1,Cup,10.00,CUP,CA,");
        await In<ChannelOrderService, OrderImportResult>(s => s.ImportAsync(shop.Id, Encoding.UTF8.GetBytes(first)));

        var second = string.Join(Environment.NewLine, Header,
            "#1,2026-03-02 09:00:00 -0500,paid,0,0,2,Mug,40.00,MUG,CA,",
            "#2,2026-03-02 09:00:00 -0500,paid,0,0,1,Cup,10.00,CUP,CA,2026-03-05");
        var again = await In<ChannelOrderService, OrderImportResult>(s => s.ImportAsync(shop.Id, Encoding.UTF8.GetBytes(second)));

        Assert.Equal(0, again.Orders);
        Assert.Contains(again.Warnings, w => w.Contains("2 order(s) waiting to be posted were updated", StringComparison.Ordinal));
        var batch = Assert.Single(await In<ChannelOrderService, IReadOnlyList<ChannelOrderBatch>>(s => s.BatchesAsync(shop.Id)));
        var line = Assert.Single(batch.Lines);
        Assert.Equal(("#1", 2m, 80m), (line.OrderId, line.Quantity, line.Sales));
    }

    [Fact]
    public async Task Pending_orders_wait_and_an_order_cancelled_since_is_called_out()
    {
        var shop = await Channel(ChannelKind.Shopify);
        const string Header = "Name,Created at,Financial Status,Shipping,Taxes,Lineitem quantity,Lineitem name,Lineitem price,Lineitem sku,Shipping Province,Cancelled at";
        var first = string.Join(Environment.NewLine, Header,
            "#1,2026-03-02 09:00:00 -0500,paid,0,0,1,Mug,40.00,MUG,CA,",
            "#2,2026-03-02 09:00:00 -0500,pending,0,0,1,Mug,40.00,MUG,CA,");
        var read = await In<ChannelOrderService, OrderImportResult>(s => s.ImportAsync(shop.Id, Encoding.UTF8.GetBytes(first)));
        Assert.Equal(1, read.Orders);
        Assert.Contains(read.Warnings, w => w.Contains("pending", StringComparison.Ordinal));

        var second = string.Join(Environment.NewLine, Header,
            "#1,2026-03-02 09:00:00 -0500,paid,0,0,1,Mug,40.00,MUG,CA,2026-03-05",
            "#2,2026-03-02 09:00:00 -0500,paid,0,0,1,Mug,40.00,MUG,CA,");
        var again = await In<ChannelOrderService, OrderImportResult>(s => s.ImportAsync(shop.Id, Encoding.UTF8.GetBytes(second)));
        Assert.Equal(1, again.Orders);
        Assert.Contains(again.Warnings, w => w.Contains("#1", StringComparison.Ordinal) && w.Contains("cancelled since", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_stripe_connection_fetches_payouts_and_clears_invoices_already_paid_online()
    {
        // An invoice paid online was recorded as a payment into Undeposited Funds with its PaymentIntent.
        var undeposited = await _db.CreateAccount("Undeposited Funds", AccountType.OtherCurrentAsset);
        var income = await _db.CreateAccount("Services", AccountType.Income);
        var item = await In<ItemService, Domain.Sales.Item>(s => s.CreateAsync(new Domain.Sales.ItemDetails("Work", Domain.Sales.ItemType.Service, income.Id, 50m)));
        var customer = await In<CustomerService, Domain.Sales.Customer>(s => s.CreateAsync(new Domain.Sales.CustomerDetails("Client")));
        var invoice = await In<InvoiceService, Domain.Sales.Invoice>(s => s.CreateAsync(new CreateInvoiceCommand(customer.Id, new DateOnly(2026, 3, 1), [new(item.Id, null, 1m, 50m)])));
        await In<PaymentService, Domain.Sales.CustomerPayment>(s => s.ReceiveAsync(new ReceivePaymentCommand(customer.Id, new DateOnly(2026, 3, 2), undeposited.Id,
            [new ApplyToInvoice(invoice.Id, 50m)], Reference: "pi_invoice")));

        var stripe = await In<ChannelService, SalesChannel>(s => s.CreateAsync(new ChannelDetails("Stripe", ChannelKind.Stripe, _checking.Id, ClearingAccountId: undeposited.Id)));
        Assert.Equal("channel.not_connected", (await Assert.ThrowsAsync<DomainException>(() => In<ChannelSyncService, ChannelSyncResult>(s => s.SyncAsync(stripe.Id)))).Code);

        await In<ChannelSyncService, SalesChannel>(s => s.ConnectAsync(stripe.Id, null, "wrong_key"));
        var failed = await In<ChannelSyncService, ChannelSyncResult>(s => s.SyncAsync(stripe.Id));
        Assert.False(failed.Succeeded);
        Assert.Contains("Invalid API Key", failed.Error, StringComparison.Ordinal);

        await In<ChannelSyncService, SalesChannel>(s => s.ConnectAsync(stripe.Id, null, FakeChannelApis.StripeKey));
        var synced = await In<ChannelSyncService, ChannelSyncResult>(s => s.SyncAsync(stripe.Id));
        Assert.Equal((true, 1), (synced.Succeeded, synced.Imported));
        Assert.Equal(0, (await In<ChannelSyncService, ChannelSyncResult>(s => s.SyncAsync(stripe.Id))).Imported);

        var payout = Assert.Single(await In<ChannelService, IReadOnlyList<ChannelPayout>>(s => s.PayoutsAsync(stripe.Id)));
        Assert.Equal(("po_1", 125.05m, 0m), (payout.ExternalId, payout.Net, payout.Difference));
        Assert.Equal(50m, payout.Total(ChannelLineType.Cleared));
        Assert.Equal(100m, payout.Total(ChannelLineType.Sale));
        Assert.Equal(-4.95m, payout.Total(ChannelLineType.Fee));

        var posted = await In<ChannelService, ChannelPayout>(s => s.PostAsync(payout.Id));
        var entry = (await In<PostingEngine, JournalEntry?>(e => e.GetAsync(posted.JournalEntryId!.Value)))!;
        Assert.Equal(-50m, entry.Lines.Where(l => l.AccountId == undeposited.Id).Sum(l => l.Amount));

        // The credential is stored protected and never audited.
        var channel = (await In<ChannelService, SalesChannel?>(s => s.GetAsync(stripe.Id)))!;
        Assert.True(channel.IsConnected);
        Assert.NotEqual(FakeChannelApis.StripeKey, channel.ProtectedCredential);
        var audit = await _db.InDb(async db => await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.ToListAsync(db.AuditRecords.Where(a => a.EntityId == stripe.Id)));
        Assert.DoesNotContain(audit, a => (a.Before + a.After).Contains(FakeChannelApis.StripeKey, StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_shopify_connection_follows_pages_of_transactions()
    {
        Assert.Equal(FakeChannelApis.Shop, ChannelSyncService.ShopDomain("https://My-Store.myshopify.com/admin"));
        Assert.Equal(FakeChannelApis.Shop, ChannelSyncService.ShopDomain("my-store"));
        Assert.Equal("channel.shop_invalid", Assert.Throws<DomainException>(() => ChannelSyncService.ShopDomain("evil.example.com")).Code);

        var shopify = await Channel(ChannelKind.Shopify);
        await In<ChannelSyncService, SalesChannel>(s => s.ConnectAsync(shopify.Id, "my-store", FakeChannelApis.ShopifyToken));
        var synced = await In<ChannelSyncService, ChannelSyncResult>(s => s.SyncAsync(shopify.Id));
        Assert.True(synced.Succeeded, synced.Error);
        var payout = Assert.Single(await In<ChannelService, IReadOnlyList<ChannelPayout>>(s => s.PayoutsAsync(shopify.Id)));
        Assert.Equal(("778", 76.80m, 0m), (payout.ExternalId, payout.Net, payout.Difference));
        Assert.Equal(-20m, payout.Total(ChannelLineType.Refund));
        Assert.Contains(_db.ChannelApis.Requests, r => r.Contains("page_info=2", StringComparison.Ordinal));

        var import = await In<ChannelService, SalesChannel>(s => s.CreateAsync(new ChannelDetails("Etsy", ChannelKind.Etsy, _checking.Id)));
        Assert.Equal("channel.no_live_connection", (await Assert.ThrowsAsync<DomainException>(() =>
            In<ChannelSyncService, SalesChannel>(s => s.ConnectAsync(import.Id, null, "x")))).Code);
    }

    // Amazon All Orders report (tab-separated): item-price is the line's total.
    private const string AmazonOrders =
        "amazon-order-id\tpurchase-date\torder-status\tproduct-name\tsku\tquantity\titem-price\titem-tax\tshipping-price\tship-state\n" +
        "113-1\t2026-03-02T10:00:00+00:00\tShipped\tBlue mug\tMUG-1\t2\t80.00\t6.80\t5.00\tWA\n" +
        "113-2\t2026-03-02T11:00:00+00:00\tCancelled\tBlue mug\tMUG-1\t1\t40.00\t0\t0\tOR\n" +
        "113-3\t2026-03-03T09:00:00+00:00\tShipped\tGift card\tGIFT\t1\t25.00\t0\t0\tTX\n";

    // Etsy sold order items: the order's shipping and tax repeat on each item row.
    private const string EtsyOrders =
        "Sale Date,Item Name,Quantity,Price,Discount Amount,Order Shipping,Order Sales Tax,Ship State,Order ID,SKU\n" +
        "03/02/26,Blue mug,1,40.00,0,6.00,3.20,NY,900,MUG-1\n" +
        "03/02/26,Red mug,2,30.00,5.00,6.00,3.20,NY,900,MUG-2\n";

    // Shopify orders export: order fields only on the first row.
    private const string ShopifyOrders =
        "Name,Created at,Financial Status,Shipping,Taxes,Lineitem quantity,Lineitem name,Lineitem price,Lineitem sku,Shipping Province,Cancelled at\n" +
        "#1001,2026-03-02 09:00:00 -0500,paid,10.00,10.00,2,Blue mug,40.00,MUG-1,CA,\n" +
        "#1001,,,,,1,Red mug,30.00,MUG-2,,\n";

    [Fact]
    public void Order_reports_read_into_item_lines_once_per_order()
    {
        var amazon = ChannelOrderReports.Read(ChannelKind.Amazon, AmazonOrders);
        Assert.Equal(2, amazon.Lines.Count);
        Assert.Equal(new ChannelOrderDraft("113-1", new DateOnly(2026, 3, 2), "MUG-1", "Blue mug", 2m, 80m, 5m, 6.80m, "WA"), amazon.Lines[0]);
        Assert.Contains(amazon.Warnings, w => w.Contains("cancelled", StringComparison.Ordinal));

        var etsy = ChannelOrderReports.Read(ChannelKind.Etsy, EtsyOrders).Lines;
        Assert.Equal([40m, 55m], etsy.Select(l => l.Sales));
        Assert.Equal([6m, 0m], etsy.Select(l => l.Shipping));
        Assert.Equal(3.20m, etsy.Sum(l => l.Tax));

        var shopify = ChannelOrderReports.Read(ChannelKind.Shopify, ShopifyOrders).Lines;
        Assert.Equal(2, shopify.Count);
        Assert.All(shopify, l => Assert.Equal(new DateOnly(2026, 3, 2), l.Date));
        Assert.Equal((10m, 10m, 110m), (shopify.Sum(l => l.Shipping), shopify.Sum(l => l.Tax), shopify.Sum(l => l.Sales)));
        Assert.All(shopify, l => Assert.Equal("CA", l.Region));

        var cancelled = ChannelOrderReports.Read(ChannelKind.Shopify, ShopifyOrders.Replace("-0500,paid,10.00,10.00,2,Blue mug,40.00,MUG-1,CA,", "-0500,paid,10.00,10.00,2,Blue mug,40.00,MUG-1,CA,2026-03-03", StringComparison.Ordinal));
        Assert.Empty(cancelled.Lines);
        Assert.DoesNotContain(cancelled.Warnings, w => w.Contains("date", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Sku_maps_go_out_and_come_back_as_csv_all_or_nothing()
    {
        var sales = await _db.CreateAccount("Sales", AccountType.Income);
        var cup = await In<ItemService, Domain.Sales.Item>(s => s.CreateAsync(new Domain.Sales.ItemDetails("Teacup", Domain.Sales.ItemType.NonInventory, sales.Id, 12m, Barcode: "TC-9")));
        var amazon = await Channel(ChannelKind.Amazon, "Amazon US");
        await In<ChannelOrderService, OrderImportResult>(s => s.ImportAsync(amazon.Id, Encoding.UTF8.GetBytes(AmazonOrders)));

        var csv = await In<ChannelOrderService, string>(s => s.SkusCsvAsync(amazon.Id));
        Assert.StartsWith("SKU,As sold,Item", csv, StringComparison.Ordinal);
        Assert.Contains("GIFT", csv, StringComparison.Ordinal);

        // A name that isn't an item stops the file: nothing is mapped.
        var bad = "SKU,Item\r\nGIFT,Not stocked\r\nMUG-1,Saucer\r\n";
        Assert.Equal("channel.sku_item_unknown", (await Assert.ThrowsAsync<DomainException>(() => In<ChannelOrderService, int>(s => s.ImportSkusCsvAsync(amazon.Id, bad)))).Code);
        Assert.False((await In<ChannelOrderService, IReadOnlyList<SkuChoice>>(s => s.SkusAsync(amazon.Id))).Single(c => c.Sku == "GIFT").Mapped);

        // By barcode, and "Not stocked"; a blank item leaves a row alone.
        Assert.Equal(2, await In<ChannelOrderService, int>(s => s.ImportSkusCsvAsync(amazon.Id, "SKU,Item\r\nGIFT,not stocked\r\nMUG-1,tc-9\r\nOTHER,\r\n")));
        var skus = await In<ChannelOrderService, IReadOnlyList<SkuChoice>>(s => s.SkusAsync(amazon.Id));
        Assert.Equal((true, (Guid?)null), (skus.Single(c => c.Sku == "GIFT").Mapped, skus.Single(c => c.Sku == "GIFT").ItemId));
        Assert.Equal(cup.Id, skus.Single(c => c.Sku == "MUG-1").ItemId);
        Assert.Contains("MUG-1,Blue mug,Teacup", (await In<ChannelOrderService, string>(s => s.SkusCsvAsync(amazon.Id))).Replace("\"", "", StringComparison.Ordinal), StringComparison.Ordinal);

        // The one-form save changes only what differs.
        Assert.Equal(0, await In<ChannelOrderService, int>(s => s.MapSkusAsync(amazon.Id, [("GIFT", null), ("MUG-1", cup.Id)])));
        Assert.Equal(1, await In<ChannelOrderService, int>(s => s.MapSkusAsync(amazon.Id, [("GIFT", cup.Id), ("MUG-1", cup.Id)])));
        await Assert.ThrowsAsync<EntityNotFoundException>(() => In<ChannelOrderService, int>(s => s.MapSkusAsync(amazon.Id, [("GIFT", Guid.NewGuid())])));
    }

    [Fact]
    public async Task A_channel_ships_from_its_own_warehouse()
    {
        var sales = await _db.CreateAccount("Sales", AccountType.Income);
        var mug = await In<ItemService, Domain.Sales.Item>(s => s.CreateAsync(new Domain.Sales.ItemDetails("Blue mug", Domain.Sales.ItemType.Inventory, sales.Id, 40m, Barcode: "MUG-1")));
        var fba = await In<WarehouseService, Domain.Inventory.Warehouse>(s => s.CreateAsync("Amazon FBA"));
        await In<InventoryAdjustmentService, Domain.Inventory.InventoryAdjustment>(s => s.CreateAsync(new CreateInventoryAdjustmentCommand(new DateOnly(2026, 3, 1),
            [new Domain.Inventory.InventoryAdjustmentLineDraft(mug.Id, 10m, 5m, new Domain.Inventory.StockPlace(fba.Id, null))])));
        var amazon = await In<ChannelService, SalesChannel>(s => s.CreateAsync(new ChannelDetails("Amazon US", ChannelKind.Amazon, _checking.Id, WarehouseId: fba.Id)));
        Assert.Equal(fba.Id, amazon.WarehouseId);

        var imported = await In<ChannelOrderService, OrderImportResult>(s => s.ImportAsync(amazon.Id, Encoding.UTF8.GetBytes(AmazonOrders)));
        await In<ChannelOrderService, ChannelOrderBatch>(s => s.PostAsync(imported.Saved.Single(b => b.Date == new DateOnly(2026, 3, 2)).Id));
        var there = (await In<InventoryService, IReadOnlyList<StockAt>>(s => s.StockByPlaceAsync(mug.Id))).Where(p => p.Place.WarehouseId == fba.Id).Sum(p => p.Quantity);
        Assert.Equal(8m, there);
    }

    [Fact]
    public async Task A_return_that_doesnt_go_back_on_the_shelf_leaves_stock_alone()
    {
        var sales = await _db.CreateAccount("Sales", AccountType.Income);
        var mug = await In<ItemService, Domain.Sales.Item>(s => s.CreateAsync(new Domain.Sales.ItemDetails("Blue mug", Domain.Sales.ItemType.Inventory, sales.Id, 40m, Barcode: "MUG-1")));
        await In<InventoryAdjustmentService, Domain.Inventory.InventoryAdjustment>(s => s.CreateAsync(new CreateInventoryAdjustmentCommand(new DateOnly(2026, 3, 1),
            [new Domain.Inventory.InventoryAdjustmentLineDraft(mug.Id, 10m, 5m)])));
        var amazon = await Channel(ChannelKind.Amazon, "Amazon US");
        var imported = await In<ChannelOrderService, OrderImportResult>(s => s.ImportAsync(amazon.Id, Encoding.UTF8.GetBytes(AmazonOrders)));
        var batch = await In<ChannelOrderService, ChannelOrderBatch>(s => s.PostAsync(imported.Saved.Single(b => b.Date == new DateOnly(2026, 3, 2)).Id));
        var line = batch.Lines.Single(l => l.Sku == "MUG-1").LineNumber;

        await In<ChannelOrderService, ChannelReturn>(s => s.ReturnAsync(batch.Id, new DateOnly(2026, 3, 9), [(line, 1m)], "Broken in transit", new HashSet<int> { line }));
        Assert.Equal(8m, (await In<InventoryService, StockLevel?>(s => s.StockForAsync(mug.Id)))!.Quantity);
        // It still counts as back: only one more can be returned.
        Assert.Equal("channel.return_too_many", (await Assert.ThrowsAsync<DomainException>(() =>
            In<ChannelOrderService, ChannelReturn>(s => s.ReturnAsync(batch.Id, new DateOnly(2026, 3, 9), [(line, 2m)])))).Code);
    }

    [Fact]
    public async Task Posting_orders_moves_stock_once_every_sku_is_mapped()
    {
        var sales = await _db.CreateAccount("Sales", AccountType.Income);
        var mug = await In<ItemService, Domain.Sales.Item>(s => s.CreateAsync(new Domain.Sales.ItemDetails("Blue mug", Domain.Sales.ItemType.Inventory, sales.Id, 40m, Barcode: "MUG-1")));
        var vendor = await In<VendorService, Domain.Purchases.Vendor>(s => s.CreateAsync(new Domain.Purchases.VendorDetails("Pottery")));
        await In<BillService, Domain.Purchases.Bill>(s => s.CreateAsync(new CreateBillCommand(vendor.Id, new DateOnly(2026, 3, 1),
            [new Domain.Purchases.BillLineDraft(Guid.Empty, 50m, null, mug.Id, 10m)])));

        var amazon = await Channel(ChannelKind.Amazon, "Amazon US");
        var imported = await In<ChannelOrderService, OrderImportResult>(s => s.ImportAsync(amazon.Id, Encoding.UTF8.GetBytes(AmazonOrders)));
        Assert.Equal((2, 2), (imported.Orders, imported.Saved.Count));
        Assert.Equal(0, (await In<ChannelOrderService, OrderImportResult>(s => s.ImportAsync(amazon.Id, Encoding.UTF8.GetBytes(AmazonOrders)))).Orders);

        // MUG-1 maps itself (the mug's barcode); the gift card needs saying it isn't stocked.
        var giftDay = imported.Saved.Single(b => b.Date == new DateOnly(2026, 3, 3));
        Assert.Equal("channel.sku_unmapped", (await Assert.ThrowsAsync<DomainException>(() => In<ChannelOrderService, ChannelOrderBatch>(s => s.PostAsync(giftDay.Id)))).Code);
        await In<ChannelOrderService, bool>(async s => { await s.MapSkuAsync(amazon.Id, "GIFT", null); return true; });
        var gifts = await In<ChannelOrderService, ChannelOrderBatch>(s => s.PostAsync(giftDay.Id));
        Assert.Equal((PayoutStatus.Posted, (Guid?)null), (gifts.Status, gifts.JournalEntryId));

        var mugDay = await In<ChannelOrderService, ChannelOrderBatch>(s => s.PostAsync(imported.Saved.Single(b => b.Date == new DateOnly(2026, 3, 2)).Id));
        var entry = (await In<PostingEngine, JournalEntry?>(e => e.GetAsync(mugDay.JournalEntryId!.Value)))!;
        Assert.Equal(10m, entry.Lines.Where(l => l.AccountId == mug.CostAccountId).Sum(l => l.Amount));
        Assert.Equal(8m, (await In<InventoryService, StockLevel?>(s => s.StockForAsync(mug.Id)))!.Quantity);

        // One mug comes back: into stock at its sale cost, off cost of goods sold.
        var mugLine = mugDay.Lines.Single(l => l.Sku == "MUG-1").LineNumber;
        Assert.Equal("channel.return_too_many", (await Assert.ThrowsAsync<DomainException>(() =>
            In<ChannelOrderService, ChannelReturn>(s => s.ReturnAsync(mugDay.Id, new DateOnly(2026, 3, 5), [(mugLine, 3m)])))).Code);
        var back = await In<ChannelOrderService, ChannelReturn>(s => s.ReturnAsync(mugDay.Id, new DateOnly(2026, 3, 5), [(mugLine, 1m)], "Chipped in transit"));
        var backEntry = (await In<PostingEngine, JournalEntry?>(e => e.GetAsync(back.JournalEntryId!.Value)))!;
        Assert.Equal(-5m, backEntry.Lines.Where(l => l.AccountId == mug.CostAccountId).Sum(l => l.Amount));
        Assert.Equal(9m, (await In<InventoryService, StockLevel?>(s => s.StockForAsync(mug.Id)))!.Quantity);
        Assert.Equal("channel.orders_have_returns", (await Assert.ThrowsAsync<DomainException>(() =>
            In<ChannelOrderService, ChannelOrderBatch>(s => s.VoidAsync(mugDay.Id, "Wrong day")))).Code);
        await In<ChannelOrderService, ChannelReturn>(s => s.VoidReturnAsync(back.Id, "Not actually back"));
        Assert.Equal(8m, (await In<InventoryService, StockLevel?>(s => s.StockForAsync(mug.Id)))!.Quantity);

        var voided = await In<ChannelOrderService, ChannelOrderBatch>(s => s.VoidAsync(mugDay.Id, "Wrong day"));
        Assert.Equal(PayoutStatus.Pending, voided.Status);
        Assert.Equal(10m, (await In<InventoryService, StockLevel?>(s => s.StockForAsync(mug.Id)))!.Quantity);

        var tax = await _db.InScope(sp => sp.GetRequiredService<ReportCatalog>().RunAsync("channel-sales-tax", ReportParameters.Parse("preset=Custom&from=2026-03-01&to=2026-03-31")));
        Assert.Equal("Marketplaces collected and paid", tax.Rows[0].Cells[0].Value);
        Assert.Equal(6.80m, tax.Rows.Single(r => r.Cells[0].Value as string == "WA").Cells[4].Value);
    }

    [Fact]
    public async Task A_shopify_sync_splits_charges_by_order_and_brings_the_orders_in()
    {
        var shopify = await Channel(ChannelKind.Shopify);
        Assert.False(shopify.MarketplaceCollectsTax);
        await In<ChannelSyncService, SalesChannel>(s => s.ConnectAsync(shopify.Id, "my-store", FakeChannelApis.ShopifyToken));
        var synced = await In<ChannelSyncService, ChannelSyncResult>(s => s.SyncAsync(shopify.Id));
        Assert.True(synced.Succeeded, synced.Error);
        Assert.Equal(1, synced.Orders); // the cancelled order is left out

        var payout = Assert.Single(await In<ChannelService, IReadOnlyList<ChannelPayout>>(s => s.PayoutsAsync(shopify.Id)));
        Assert.Equal((80m, 10m, 10m), (payout.Total(ChannelLineType.Sale), payout.Total(ChannelLineType.Shipping), payout.Total(ChannelLineType.Tax)));
        Assert.Equal(76.80m, payout.Net);
        Assert.All(payout.Lines.Where(l => l.Type is ChannelLineType.Tax), l => Assert.Equal("CA", l.Region));

        var batch = Assert.Single(await In<ChannelOrderService, IReadOnlyList<ChannelOrderBatch>>(s => s.BatchesAsync(shopify.Id)));
        Assert.Equal(("5001", 2m, "CA"), (batch.Lines[0].OrderId, batch.Lines[0].Quantity, batch.Lines[0].Region));
    }
}
