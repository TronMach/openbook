using Microsoft.Extensions.DependencyInjection;
using OpenBook.Application.CustomFields;
using OpenBook.Application.Inventory;
using OpenBook.Application.Pricing;
using OpenBook.Application.Purchases;
using OpenBook.Application.Sales;
using OpenBook.Domain.Accounts;
using OpenBook.Domain.Common;
using OpenBook.Domain.CustomFields;
using OpenBook.Domain.Inventory;
using OpenBook.Domain.Pricing;
using OpenBook.Domain.Purchases;
using OpenBook.Domain.Sales;

namespace OpenBook.Integration.Tests;

/// <summary>The items spreadsheet: every item out as CSV with pricing, stock and custom fields, and back in.</summary>
public sealed class ItemFileTests : IAsyncLifetime
{
    private static readonly DateOnly Mar1 = new(2026, 3, 1);
    private static readonly DateOnly Mar20 = new(2026, 3, 20);
    private readonly LedgerDatabase _db = new();
    private Guid _sales;
    private Item _mug = null!;
    private Item _design = null!;
    private PriceLevel _wholesale = null!;
    private CustomField _color = null!;

    public async Task InitializeAsync()
    {
        await _db.InitializeAsync();
        _db.Clock.UtcNow = new DateTimeOffset(2026, 3, 31, 12, 0, 0, TimeSpan.Zero);
        _sales = (await _db.CreateAccount("Sales", AccountType.Income, "4000")).Id;
        await _db.CreateAccount("Design Income", AccountType.Income, "4100");
        await In<VendorService, Vendor>(s => s.CreateAsync(new VendorDetails("Clay Co")));
        _color = await In<CustomFieldService, CustomField>(s => s.CreateAsync(CustomFieldTarget.Item, new CustomFieldDetails("Color", CustomFieldType.Text)));
        _mug = await _db.InScope(async sp =>
        {
            var item = await sp.GetRequiredService<ItemService>().CreateAsync(new ItemDetails("Mug", ItemType.Inventory, _sales, 12m, "Stoneware mug", PurchaseCost: 4m, Barcode: "MUG-1", ReorderPoint: 5m));
            await sp.GetRequiredService<InventoryAdjustmentService>().CreateAsync(new CreateInventoryAdjustmentCommand(Mar1, [new(item.Id, 10m, 4m)]));
            await sp.GetRequiredService<PricingService>().SetQuantityBreaksAsync(item.Id, [(10m, QuantityBreakKind.PercentOff, 5m)]);
            await sp.GetRequiredService<CustomFieldService>().SaveAsync("item", item.Id, new Dictionary<Guid, string?> { [_color.Id] = "Blue" });
            return item;
        });
        _design = await In<ItemService, Item>(s => s.CreateAsync(new ItemDetails("Design work", ItemType.Service, _sales, 90m)));
        _wholesale = await _db.InScope(async sp =>
        {
            var pricing = sp.GetRequiredService<PricingService>();
            var level = await pricing.CreateLevelAsync(new PriceLevelDetails("Wholesale", PriceLevelKind.PerItem));
            return await pricing.SetLevelPricesAsync(level.Id, [(_mug.Id, 9m)]);
        });
    }

    public Task DisposeAsync() => _db.DisposeAsync();

    private Task<T> In<TService, T>(Func<TService, Task<T>> work) where TService : notnull =>
        _db.InScope(sp => work(sp.GetRequiredService<TService>()));

    private Task<string> Export() => In<ItemFileService, string>(s => s.ExportAsync());

    private static readonly ItemFileOptions Options = new(Mar20);

    /// <summary>The file with one cell changed: the row whose Item column is <paramref name="item"/>, the column named <paramref name="column"/>.</summary>
    private static string With(string csv, string item, string column, string value)
    {
        var records = OpenBook.Application.Import.Csv.Parse(csv);
        var header = records[0].Fields.ToList();
        var at = header.IndexOf(column);
        Assert.True(at >= 0, $"No column {column}");
        var lines = new List<string> { string.Join(',', header.Select(Quote)) };
        foreach (var record in records.Skip(1))
        {
            var fields = record.Fields.ToList();
            if (fields[1] == item)
                fields[at] = value;
            lines.Add(string.Join(',', fields.Select(Quote)));
        }

        return string.Join("\r\n", lines) + "\r\n";
    }

    private static string Quote(string field) => field.Contains(',') || field.Contains('"') ? $"\"{field.Replace("\"", "\"\"")}\"" : field;

    [Fact]
    public async Task The_export_carries_pricing_stock_and_custom_fields_and_comes_back_unchanged()
    {
        var csv = await Export();

        var header = csv[..csv.IndexOf('\r')];
        Assert.Contains("Quantity breaks", header);
        Assert.Contains("Price level: Wholesale", header);
        Assert.Contains("On hand", header);
        Assert.Contains("Color", header);
        var records = OpenBook.Application.Import.Csv.Parse(csv);
        var columns = records[0].Fields.ToList();
        var mug = records.Skip(1).Single(r => r.Fields[1] == "Mug").Fields;
        Assert.Equal(_mug.Id.ToString(), mug[0]);
        Assert.Equal("12.00", mug[columns.IndexOf("Price")]);
        Assert.Equal("4.00", mug[columns.IndexOf("Purchase cost")]);
        Assert.Equal("10: 5%", mug[columns.IndexOf("Quantity breaks")]);
        Assert.Equal("9.00", mug[columns.IndexOf("Price level: Wholesale")]);
        Assert.Equal("10.00", mug[columns.IndexOf("On hand")]);
        Assert.Equal("Blue", mug[columns.IndexOf("Color")]);

        var preview = await In<ItemFileService, ItemFilePlan>(s => s.PreviewAsync(csv, Options));
        Assert.Equal((0, 0, 2, 0), (preview.NewCount, preview.ChangedCount, preview.UnchangedCount, preview.ErrorCount));
        Assert.False(preview.CanImport);
        var refused = await Assert.ThrowsAsync<DomainException>(() => In<ItemFileService, ItemFilePlan>(s => s.ImportAsync(csv, Options)));
        Assert.Equal("import.nothing_new", refused.Code);
    }

    [Fact]
    public async Task Changes_in_the_file_reach_the_items_their_prices_fields_and_stock()
    {
        var csv = await Export();
        csv = With(csv, "Mug", "Price", "14");
        csv = With(csv, "Mug", "Quantity breaks", "10: 5%; 50: 11.00");
        csv = With(csv, "Mug", "Price level: Wholesale", "10.50");
        csv = With(csv, "Mug", "Color", "Green");
        csv = With(csv, "Mug", "On hand", "7");
        csv = With(csv, "Design work", "Item", "Design hours");
        csv = With(csv, "Design hours", "Description", "By the hour");
        csv += ",Bowl,Inventory,Yes,,Kitchen,,20,6,Yes,Sales,,,FIFO,None,Clay Co,,,,,,15,Red\r\n";

        var preview = await In<ItemFileService, ItemFilePlan>(s => s.PreviewAsync(csv, Options));
        Assert.Equal((1, 2, 0), (preview.NewCount, preview.ChangedCount, preview.ErrorCount));
        Assert.Null(preview.Problem);
        var mugRow = preview.Rows.Single(r => r.Item == "Mug");
        Assert.Contains("Price: 12.00 → 14.00", mugRow.Changes);
        Assert.Contains("On hand: 10 → 7", mugRow.Changes);
        Assert.Contains("Wholesale price: 9.00 → 10.50", mugRow.Changes);
        Assert.Equal(2, preview.StockChanges);

        // The preview tried it all and kept nothing.
        Assert.Equal(12m, (await In<ItemService, Item?>(s => s.GetAsync(_mug.Id)))!.Rate);
        Assert.DoesNotContain(await In<ItemService, IReadOnlyList<Item>>(s => s.ListAsync()), i => i.Name == "Bowl");

        var done = await In<ItemFileService, ItemFilePlan>(s => s.ImportAsync(csv, Options));
        Assert.Equal((1, 2), (done.NewCount, done.ChangedCount));

        var mug = (await In<ItemService, Item?>(s => s.GetAsync(_mug.Id)))!;
        Assert.Equal(14m, mug.Rate);
        Assert.Equal(new[] { (10m, QuantityBreakKind.PercentOff, 5m), (50m, QuantityBreakKind.Price, 11m) }, mug.QuantityBreaks.Select(b => (b.FromQuantity, b.Kind, b.Value)));
        Assert.Equal(10.50m, (await In<PricingService, PriceLevel?>(s => s.GetLevelAsync(_wholesale.Id)))!.PriceFor(_mug.Id));
        var values = await In<CustomFieldService, IReadOnlyDictionary<Guid, IReadOnlyList<CustomFieldEntry>>>(s => s.ValuesByRecordAsync(["item"]));
        Assert.Equal("Green", values[_mug.Id].Single(e => e.Field.Id == _color.Id).Value);

        var design = (await In<ItemService, Item?>(s => s.GetAsync(_design.Id)))!;
        Assert.Equal(("Design hours", "By the hour"), (design.Name, design.Description));

        var bowl = (await In<ItemService, IReadOnlyList<Item>>(s => s.ListAsync())).Single(i => i.Name == "Bowl");
        Assert.Equal((ItemType.Inventory, 20m, 6m, "Kitchen"), (bowl.Type, bowl.Rate, bowl.PurchaseCost, bowl.Category));
        Assert.NotNull(bowl.PreferredVendorId);
        Assert.Equal("Red", values[bowl.Id].Single(e => e.Field.Id == _color.Id).Value);

        var stock = (await In<InventoryService, IReadOnlyList<StockLevel>>(s => s.StockAsync())).ToDictionary(s => s.Item.Id, s => s.Quantity);
        Assert.Equal(7m, stock[_mug.Id]);
        Assert.Equal(15m, stock[bowl.Id]);
        var adjustments = await In<InventoryAdjustmentService, IReadOnlyList<InventoryAdjustment>>(s => s.ListAsync());
        var made = Assert.Single(adjustments, a => a.Reference == ItemFileService.AdjustmentReference);
        Assert.Equal(Mar20, made.Date);

        // Brought back again, the same file has nothing left to do.
        var exported = await Export();
        var again = await In<ItemFileService, ItemFilePlan>(s => s.PreviewAsync(exported, Options));
        Assert.Equal(0, again.NewCount + again.ChangedCount);
    }

    [Fact]
    public async Task A_row_with_a_mistake_stops_the_whole_file()
    {
        var csv = await Export();
        csv = With(csv, "Mug", "Price", "15");
        csv = With(csv, "Design work", "Income account", "No such account");
        csv += ",Plate,Inventory,Yes,,,,8,3,Yes,Sales,,,FIFO,None,,,,,10 five,,,\r\n";

        var preview = await In<ItemFileService, ItemFilePlan>(s => s.PreviewAsync(csv, Options));

        Assert.Equal(2, preview.ErrorCount);
        Assert.Contains("There's no income account 'No such account'", preview.Rows.Single(r => r.Item == "Design work").Message, StringComparison.Ordinal);
        Assert.Contains("quantity break", preview.Rows.Single(r => r.Item == "Plate").Message, StringComparison.Ordinal);
        var refused = await Assert.ThrowsAsync<DomainException>(() => In<ItemFileService, ItemFilePlan>(s => s.ImportAsync(csv, Options)));
        Assert.Equal("import.has_errors", refused.Code);
        Assert.Equal(12m, (await In<ItemService, Item?>(s => s.GetAsync(_mug.Id)))!.Rate);
    }

    [Fact]
    public async Task Columns_left_out_change_nothing_and_an_unknown_id_is_refused()
    {
        var csv = $"Item ID,Item,Price\r\n{_mug.Id},Mug,13\r\n{Guid.NewGuid()},Ghost,1\r\n";

        var preview = await In<ItemFileService, ItemFilePlan>(s => s.PreviewAsync(csv, Options));

        Assert.Equal("Price: 12.00 → 13.00", Assert.Single(preview.Rows.Single(r => r.Item == "Mug").Changes));
        Assert.Contains("no item with this Item ID", preview.Rows.Single(r => r.Item == "Ghost").Message, StringComparison.Ordinal);

        await In<ItemFileService, ItemFilePlan>(s => s.ImportAsync($"Item,Price\r\nMug,13\r\n", Options));
        var mug = (await In<ItemService, Item?>(s => s.GetAsync(_mug.Id)))!;
        Assert.Equal((13m, "Stoneware mug", "MUG-1", 1), (mug.Rate, mug.Description, mug.Barcode, mug.QuantityBreaks.Count));
        Assert.Equal(9m, (await In<PricingService, PriceLevel?>(s => s.GetLevelAsync(_wholesale.Id)))!.PriceFor(_mug.Id));
    }
}
