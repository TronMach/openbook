using Microsoft.Extensions.DependencyInjection;
using OpenBook.Application.Accounts;
using OpenBook.Application.Sales;
using OpenBook.Domain.Accounts;
using OpenBook.Domain.Common;
using OpenBook.Domain.Sales;

namespace OpenBook.Integration.Tests;

public sealed class SalesImportTests : IAsyncLifetime
{
    private readonly LedgerDatabase _db = new();
    private Account _services = null!;
    private Account _products = null!;

    public async Task InitializeAsync()
    {
        await _db.InitializeAsync();
        _services = await _db.CreateAccount("Service Revenue", AccountType.Income, "4000");
        _products = await _db.CreateAccount("Product Sales", AccountType.Income, "4100");
        await _db.CreateAccount("Checking", AccountType.Bank, "1000");
    }

    public Task DisposeAsync() => _db.DisposeAsync();

    private Task<T> Run<T>(Func<IServiceProvider, Task<T>> f) => _db.InScope(f);

    [Fact]
    public async Task Customers_import_with_composite_addresses_terms_and_existing_rows_skipped()
    {
        await Run(sp => sp.GetRequiredService<CustomerService>().CreateAsync(new CustomerDetails("Initech")));
        const string csv = """
            Customer,Company,Email,Street,City,State,Zip,Terms
            Acme Corp,Acme Corporation,ap@acme.test,1 Road Runner Way,Phoenix,AZ,85001,Net 15
            Globex,,,,,,,Due on receipt
            initech,,,,,,,
            """;

        var result = await Run(sp => sp.GetRequiredService<CustomerImportService>().ImportAsync(csv));

        Assert.Equal((2, 1, 0), (result.NewCount, result.ExistingCount, result.ErrorCount));
        var customers = (await Run(sp => sp.GetRequiredService<CustomerService>().ListAsync(new DateOnly(2026, 1, 1))))
            .ToDictionary(c => c.Customer.DisplayName, c => c.Customer);
        Assert.Equal("1 Road Runner Way\nPhoenix, AZ 85001", customers["Acme Corp"].BillingAddress);
        Assert.Equal(15, customers["Acme Corp"].PaymentTermsDays);
        Assert.Equal(0, customers["Globex"].PaymentTermsDays);
    }

    [Fact]
    public async Task Customer_rows_with_errors_block_the_whole_import()
    {
        const string csv = "Name,Email,Terms\nGood,good@x.test,Net 30\nBad Email,nope,\nBad Terms,,whenever\n";

        var preview = await Run(sp => sp.GetRequiredService<CustomerImportService>().PreviewAsync(csv));

        Assert.Equal((1, 2), (preview.NewCount, preview.ErrorCount));
        Assert.Contains("isn't an email", preview.Rows[1].Message, StringComparison.Ordinal);
        Assert.Contains("Couldn't read the terms", preview.Rows[2].Message, StringComparison.Ordinal);
        var ex = await Assert.ThrowsAsync<DomainException>(() => Run(sp => sp.GetRequiredService<CustomerImportService>().ImportAsync(csv)));
        Assert.Equal("import.has_errors", ex.Code);
    }

    [Fact]
    public async Task Items_import_matching_accounts_by_name_or_number_with_a_default()
    {
        const string csv = """
            Product/Service,Type,Sales Description,Sales Price/Rate,Income Account
            Consulting,Service,Hourly consulting,150,Service Revenue
            Widget,Non-inventory,Blue widget,"1,234.5675",4100
            Gift wrap,,,3.5,
            """;

        var result = await Run(sp => sp.GetRequiredService<ItemImportService>().ImportAsync(csv, _services.Id));

        Assert.Equal(3, result.NewCount);
        var items = (await Run(sp => sp.GetRequiredService<ItemService>().ListAsync())).ToDictionary(i => i.Name);
        Assert.Equal((ItemType.NonInventory, 1234.5675m, _products.Id), (items["Widget"].Type, items["Widget"].Rate, items["Widget"].IncomeAccountId));
        Assert.Equal((ItemType.Service, _services.Id), (items["Gift wrap"].Type, items["Gift wrap"].IncomeAccountId));
    }

    [Fact]
    public async Task Inventory_items_come_in_with_their_stock_details()
    {
        const string csv = "Name,Type,Price,Income Account,Cost,Barcode,Reorder Point\nBolt,Inventory,2.50,4000,1.25,BOLT-1,40\n";
        var chartBefore = (await Run(sp => sp.GetRequiredService<AccountService>().GetChartAsync())).All.Count;

        var preview = await Run(sp => sp.GetRequiredService<ItemImportService>().PreviewAsync(csv, null));
        Assert.Equal(ImportRowStatus.New, Assert.Single(preview.Rows).Status);
        Assert.Equal(chartBefore, (await Run(sp => sp.GetRequiredService<AccountService>().GetChartAsync())).All.Count);

        await Run(sp => sp.GetRequiredService<ItemImportService>().ImportAsync(csv, null));

        var item = (await Run(sp => sp.GetRequiredService<ItemService>().ListAsync())).Single(i => i.Name == "Bolt");
        Assert.Equal((ItemType.Inventory, 1.25m, "BOLT-1", 40m), (item.Type, item.PurchaseCost, item.Barcode, item.ReorderPoint));
        var chart = await Run(sp => sp.GetRequiredService<AccountService>().GetChartAsync());
        Assert.Equal("Inventory Asset", chart.Get(item.InventoryAssetAccountId!.Value).Name);
    }

    [Fact]
    public async Task Item_rows_report_unknown_accounts_inventory_types_and_missing_defaults()
    {
        const string csv = "Name,Type,Price,Income Account\nA,Service,1,Nowhere\nB,Gadget,1,4000\nC,Service,1,\nD,Service,1,Checking\n";

        var rows = (await Run(sp => sp.GetRequiredService<ItemImportService>().PreviewAsync(csv, null))).Rows;

        Assert.All(rows, r => Assert.Equal(ImportRowStatus.Error, r.Status));
        Assert.Contains("no account 'Nowhere'", rows[0].Message, StringComparison.Ordinal);
        Assert.Contains("isn't a known item type", rows[1].Message, StringComparison.Ordinal);
        Assert.Contains("choose a default", rows[2].Message, StringComparison.Ordinal);
        Assert.Contains("isn't an income account", rows[3].Message, StringComparison.Ordinal);
    }
}
