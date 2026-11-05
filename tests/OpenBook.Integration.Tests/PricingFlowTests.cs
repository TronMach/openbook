using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OpenBook.Application.Forms;
using OpenBook.Application.Pricing;
using OpenBook.Application.Reporting;
using OpenBook.Application.Sales;
using OpenBook.Domain.Accounts;
using OpenBook.Domain.Common;
using OpenBook.Domain.Pricing;
using OpenBook.Domain.Sales;

namespace OpenBook.Integration.Tests;

/// <summary>L12: price levels on customers, quantity breaks, price rules and rounding, applied to sales documents.</summary>
public sealed class PricingFlowTests : IAsyncLifetime
{
    private static readonly DateOnly May10 = new(2026, 5, 10);

    private readonly LedgerDatabase _db = new();
    private Account _checking = null!;
    private Item _widget = null!;
    private Item _gadget = null!;
    private PriceLevel _wholesale = null!;
    private Customer _shop = null!;
    private Customer _walkIn = null!;

    public async Task InitializeAsync()
    {
        await _db.InitializeAsync();
        _checking = await _db.CreateAccount("Checking", AccountType.Bank, "1000");
        var sales = await _db.CreateAccount("Sales", AccountType.Income, "4000");
        _widget = await In<ItemService, Item>(s => s.CreateAsync(new ItemDetails("Widget", ItemType.NonInventory, sales.Id, 100m)));
        _gadget = await In<ItemService, Item>(s => s.CreateAsync(new ItemDetails("Gadget", ItemType.NonInventory, sales.Id, 20m)));
        _wholesale = await Pricing(s => s.CreateLevelAsync(new PriceLevelDetails("Wholesale", PriceLevelKind.FixedPercent, -10m)));
        _shop = await In<CustomerService, Customer>(s => s.CreateAsync(new CustomerDetails("Corner Shop", PriceLevelId: _wholesale.Id)));
        _walkIn = await In<CustomerService, Customer>(s => s.CreateAsync(new CustomerDetails("Walk-in")));
    }

    public Task DisposeAsync() => _db.DisposeAsync();

    private Task<T> In<TService, T>(Func<TService, Task<T>> work) where TService : notnull =>
        _db.InScope(sp => work(sp.GetRequiredService<TService>()));

    private Task<T> Pricing<T>(Func<PricingService, Task<T>> work) => In(work);

    private static SalesLineDraft Auto(Item item, decimal qty) => new(item.Id, null, qty, 0m, AutoPrice: true);

    private Task<Invoice> Invoice(Customer customer, params SalesLineDraft[] lines) =>
        In<InvoiceService, Invoice>(s => s.CreateAsync(new CreateInvoiceCommand(customer.Id, May10, lines)));

    [Fact]
    public async Task Automatic_lines_get_the_customers_level_and_manual_prices_are_kept()
    {
        var invoice = await Invoice(_shop, Auto(_widget, 2m), new SalesLineDraft(_gadget.Id, null, 1m, 18.5m));

        var widget = invoice.Lines[0];
        Assert.Equal(90m, widget.Rate);
        Assert.Equal(100m, widget.ListRate);
        Assert.Equal("Wholesale (-10%)", widget.PriceSource);
        Assert.Equal(20m, widget.Saved);
        var gadget = invoice.Lines[1];
        Assert.Equal(18.5m, gadget.Rate);
        Assert.Equal(SalesLine.ManualPrice, gadget.PriceSource);
        Assert.Equal(198.5m, invoice.Total);

        // Stored and read back.
        var saved = await In<InvoiceService, Invoice?>(s => s.GetAsync(invoice.Id));
        Assert.Equal("Wholesale (-10%)", saved!.Lines[0].PriceSource);
        Assert.Equal(100m, saved.Lines[0].ListRate);

        var form = await In<FormService, SalesForm>(s => s.BuildAsync(FormKind.Invoice, invoice.Id));
        Assert.Contains(form.Totals, t => t.Label == "You saved" && t.Amount == 20m);
    }

    [Fact]
    public async Task The_highest_quantity_break_reached_wins_however_the_rows_come_back()
    {
        await Pricing(s => s.SetQuantityBreaksAsync(_widget.Id, [(50m, QuantityBreakKind.PercentOff, 5m), (200m, QuantityBreakKind.PercentOff, 20m)]));
        await Pricing(s => s.SetQuantityBreaksAsync(_widget.Id, [(50m, QuantityBreakKind.PercentOff, 5m), (100m, QuantityBreakKind.PercentOff, 10m), (200m, QuantityBreakKind.PercentOff, 20m)]));

        var quote = await In<PricingService, PriceQuote>(s => s.QuoteAsync(null, _widget.Id, 250m, new DateOnly(2026, 3, 1)));

        Assert.Equal(_widget.Rate * 0.8m, quote.Price);
        var item = await In<ItemService, Item?>(s => s.GetAsync(_widget.Id));
        Assert.Equal([50m, 100m, 200m], item!.QuantityBreaks.Select(b => b.FromQuantity));
    }

    [Fact]
    public async Task Quantity_breaks_count_the_items_total_across_lines()
    {
        await Pricing(s => s.SetQuantityBreaksAsync(_widget.Id, [(10m, QuantityBreakKind.PercentOff, 5m)]));
        var invoice = await Invoice(_walkIn, Auto(_widget, 6m), Auto(_widget, 4m));
        Assert.All(invoice.Lines, l => Assert.Equal(95m, l.Rate));
        Assert.Equal(950m, invoice.Total);

        var item = await In<ItemService, Item?>(s => s.GetAsync(_widget.Id));
        Assert.Single(item!.QuantityBreaks);
        Assert.True(await _db.InDb(db => db.AuditRecords.AnyAsync(r => r.EntityType == "item" && r.EntityId == _widget.Id && r.After!.Contains("QuantityBreaks"))));
    }

    [Fact]
    public async Task Rules_apply_by_date_customer_and_priority_and_rounding_follows_the_company()
    {
        await Pricing(s => s.CreateRuleAsync(new PriceRuleDetails("May sale", PriceAdjustment.Percent, -15m, [_widget.Id],
            StartsOn: new DateOnly(2026, 5, 1), EndsOn: new DateOnly(2026, 5, 31), Priority: 10)));
        await Pricing(s => s.CreateRuleAsync(new PriceRuleDetails("Shop extra", PriceAdjustment.Amount, -0.33m, [],
            RuleCustomers.Customers, [_shop.Id], Priority: 20)));
        await In<PricingService, int>(async s => { await s.SetRoundingAsync(PriceRounding.TenCents); return 0; });

        var shop = await Invoice(_shop, Auto(_widget, 1m), Auto(_gadget, 1m));
        Assert.Equal(76.2m, shop.Lines[0].Rate); // 100 → 90 → 76.5 → 76.17 → 76.2
        Assert.Equal("Wholesale (-10%) + May sale (-15%) + Shop extra (-0.33)", shop.Lines[0].PriceSource);
        Assert.Equal(17.7m, shop.Lines[1].Rate); // 20 → 18 → 17.67 → 17.7

        var receipt = await In<SalesReceiptService, SalesReceipt>(s => s.CreateAsync(new CreateSalesReceiptCommand(null, new DateOnly(2026, 6, 1), _checking.Id, [Auto(_widget, 1m)])));
        Assert.Equal(100m, receipt.Lines[0].Rate);
        Assert.Null(receipt.Lines[0].PriceSource);
    }

    [Fact]
    public async Task Per_item_levels_import_from_csv_all_or_nothing()
    {
        var gold = await Pricing(s => s.CreateLevelAsync(new PriceLevelDetails("Gold", PriceLevelKind.PerItem)));
        var bad = await Pricing(s => s.ImportLevelPricesAsync(gold.Id, "Item,Price\nWidget,80\nNope,5\n"));
        Assert.Single(bad.Errors);
        Assert.Empty((await Pricing(s => s.GetLevelAsync(gold.Id)))!.Items);

        var ok = await Pricing(s => s.ImportLevelPricesAsync(gold.Id, "Product/Service,Level price\nwidget,\"$80.00\"\n"));
        Assert.Equal(1, ok.Imported);
        var csv = await Pricing(s => s.ExportLevelPricesAsync(gold.Id, May10));
        Assert.Contains("Widget,100.00,80.00", csv, StringComparison.Ordinal);
        Assert.DoesNotContain("Gadget", csv, StringComparison.Ordinal);

        Assert.True(await _db.InDb(db => db.AuditRecords.AnyAsync(r => r.EntityType == PricingService.LevelEntity && r.EntityId == gold.Id && r.Action == Domain.Auditing.AuditAction.Updated)));
    }

    [Fact]
    public async Task Names_are_unique_and_inactive_levels_cant_be_assigned()
    {
        Assert.Equal("pricing.level_name_taken", (await Assert.ThrowsAsync<DomainException>(() =>
            Pricing(s => s.CreateLevelAsync(new PriceLevelDetails("wholesale", PriceLevelKind.FixedPercent, -5m))))).Code);
        var retired = await Pricing(s => s.CreateLevelAsync(new PriceLevelDetails("Retired", PriceLevelKind.FixedPercent, 5m)));
        await Pricing(s => s.SetLevelActiveAsync(retired.Id, false));
        Assert.Equal("pricing.level_inactive", (await Assert.ThrowsAsync<DomainException>(() =>
            In<CustomerService, Customer>(s => s.CreateAsync(new CustomerDetails("Late", PriceLevelId: retired.Id))))).Code);
        await Assert.ThrowsAsync<EntityNotFoundException>(() =>
            Pricing(s => s.CreateRuleAsync(new PriceRuleDetails("Ghost", PriceAdjustment.Amount, -1m, [Guid.NewGuid()]))));
    }

    [Fact]
    public async Task Last_price_and_the_reports_follow_the_documents()
    {
        await Invoice(_shop, Auto(_widget, 3m));
        _db.Clock.UtcNow = _db.Clock.UtcNow.AddMinutes(5);
        await Invoice(_shop, new SalesLineDraft(_widget.Id, null, 1m, 88m));

        var last = await Pricing(s => s.LastPricesAsync(_shop.Id));
        Assert.Equal(88m, last[_widget.Id].Rate);

        var list = await In<ReportCatalog, ReportTable>(c => c.RunAsync("price-list", ReportParameters.Parse($"customerId={_shop.Id}&asOf=2026-05-10")));
        var widgetRow = list.Rows.Single(r => r.Cells[0].Value as string == "Widget");
        Assert.Equal(90m, widgetRow.Cells[2].Value);

        var discounts = await In<ReportCatalog, ReportTable>(c => c.RunAsync("discounts-given", ReportParameters.Parse("preset=Custom&from=2026-05-01&to=2026-05-31")));
        var wholesale = discounts.Rows.Single(r => r.Cells[0].Value as string == "Wholesale (-10%)");
        Assert.Equal(30m, wholesale.Cells[5].Value);
        var manual = discounts.Rows.Single(r => r.Cells[0].Value as string == SalesLine.ManualPrice);
        Assert.Equal(12m, manual.Cells[5].Value);
        Assert.Equal(42m, discounts.Rows[^1].Cells[5].Value);

        var detail = await In<ReportCatalog, ReportTable>(c => c.RunAsync("discounts-given", ReportParameters.Parse("preset=Custom&from=2026-05-01&to=2026-05-31&detail=true")));
        Assert.Equal(42m, detail.Rows[^1].Cells[5].Value);
    }

    [Fact]
    public async Task Estimates_copied_to_invoices_keep_their_prices()
    {
        var estimate = await In<EstimateService, Estimate>(s => s.CreateAsync(new SaveEstimateCommand(_shop.Id, May10, [Auto(_widget, 1m)])));
        Assert.Equal(90m, estimate.Lines[0].Rate);
        await Pricing(s => s.UpdateLevelAsync(_wholesale.Id, new PriceLevelDetails("Wholesale", PriceLevelKind.FixedPercent, -20m)));

        var invoice = await In<InvoiceService, Invoice>(s => s.CreateAsync(new CreateInvoiceCommand(_shop.Id, May10, SalesLine.Drafts(estimate.Lines), EstimateId: estimate.Id)));
        Assert.Equal(90m, invoice.Lines[0].Rate);
        Assert.Equal("Wholesale (-10%)", invoice.Lines[0].PriceSource);
    }
}
