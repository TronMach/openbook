using Microsoft.Extensions.DependencyInjection;
using OpenBook.Application.CustomFields;
using OpenBook.Application.Forms;
using OpenBook.Application.Purchases;
using OpenBook.Application.Reporting;
using OpenBook.Application.Sales;
using OpenBook.Application.Search;
using OpenBook.Domain.Accounts;
using OpenBook.Domain.Auditing;
using OpenBook.Domain.Common;
using OpenBook.Domain.CustomFields;
using OpenBook.Domain.Purchases;
using OpenBook.Domain.Sales;

namespace OpenBook.Integration.Tests;

/// <summary>Custom fields: defining them, values on records, and the copies documents keep and print.</summary>
public sealed class CustomFieldTests : IAsyncLifetime
{
    private static readonly DateOnly Oct1 = new(2026, 10, 1);

    private readonly LedgerDatabase _db = new();
    private Item _widget = null!;
    private Item _bolts = null!;
    private Customer _contoso = null!;
    private Vendor _supplier = null!;

    public async Task InitializeAsync()
    {
        await _db.InitializeAsync();
        var sales = await _db.CreateAccount("Sales", AccountType.Income, "4000");
        _widget = await In<ItemService, Item>(s => s.CreateAsync(new ItemDetails("Widget", ItemType.NonInventory, sales.Id, 20m)));
        _bolts = await In<ItemService, Item>(s => s.CreateAsync(new ItemDetails("Bolts", ItemType.NonInventory, sales.Id, 2m)));
        _contoso = await In<CustomerService, Customer>(s => s.CreateAsync(new CustomerDetails("Contoso Hardware")));
        _supplier = await In<VendorService, Vendor>(s => s.CreateAsync(new VendorDetails("Parts Supply Co")));
    }

    public Task DisposeAsync() => _db.DisposeAsync();

    private Task<T> In<TService, T>(Func<TService, Task<T>> work) where TService : notnull =>
        _db.InScope(sp => work(sp.GetRequiredService<TService>()));

    private Task<T> Fields<T>(Func<CustomFieldService, Task<T>> work) => In(work);

    private static async Task<string> Refused(Func<Task> action) => (await Assert.ThrowsAsync<DomainException>(action)).Code;

    /// <summary>Adds ready-made fields by key and returns them by key.</summary>
    private async Task<IReadOnlyDictionary<string, CustomField>> Presets(params string[] keys) =>
        (await Fields(s => s.AddPresetsAsync(keys))).ToDictionary(f => f.PresetKey!);

    private Task<List<AuditRecord>> Audit() => _db.InDb(db => Task.FromResult(db.AuditRecords.ToList()));

    private Task<IReadOnlyList<CustomFieldEntry>> Save(string entityType, Guid id, params (CustomField Field, string? Value)[] values) =>
        Fields(s => s.SaveAsync(entityType, id, values.ToDictionary(v => v.Field.Id, v => v.Value)));

    private async Task<Dictionary<string, string?>> Values(string entityType, Guid id) =>
        (await Fields(s => s.EntriesAsync(entityType, id))).Where(e => e.Value is not null).ToDictionary(e => e.Field.Name, e => e.Value);

    [Fact]
    public async Task Ready_made_fields_are_added_once_and_names_are_unique_per_kind_of_record()
    {
        var all = CustomFieldPresets.All.Select(p => p.Key).ToList();
        var added = await Fields(s => s.AddPresetsAsync(all));
        Assert.Equal(CustomFieldPresets.All.Count, added.Count);
        Assert.Empty(await Fields(s => s.AvailablePresetsAsync()));
        Assert.Empty(await Fields(s => s.AddPresetsAsync(all)));

        // "Pack size" is taken for products, but customers can have one.
        Assert.Equal("customfield.name_taken", await Refused(() => Fields(s => s.CreateAsync(CustomFieldTarget.Item, new CustomFieldDetails("pack size", CustomFieldType.Text)))));
        var custom = await Fields(s => s.CreateAsync(CustomFieldTarget.Customer, new CustomFieldDetails("Pack size", CustomFieldType.Text)));
        Assert.True(custom.Position > 0);

        var audit = await Audit();
        Assert.Equal(added.Count + 1, audit.Count(r => r.EntityType == CustomFieldService.EntityType && r.Action == AuditAction.Created));
    }

    [Fact]
    public async Task Values_on_records_are_checked_saved_cleared_and_audited()
    {
        var f = await Presets("item.pack_size", "item.min_order", "customer.type");
        var required = await Fields(s => s.CreateAsync(CustomFieldTarget.Item, new CustomFieldDetails("Shelf", CustomFieldType.Text, Required: true)));

        await Save("item", _widget.Id, (f["item.pack_size"], "Case of 12"), (f["item.min_order"], "1,200"), (required, "A3"));
        Assert.Equal(new Dictionary<string, string?> { ["Pack size"] = "Case of 12", ["Minimum order quantity"] = "1200", ["Shelf"] = "A3" }, await Values("item", _widget.Id));

        Assert.Equal("customfield.value_invalid", await Refused(() => Save("item", _widget.Id, (f["item.min_order"], "a dozen"))));
        Assert.Equal("customfield.required", await Refused(() => Save("item", _widget.Id, (required, ""))));
        // A customer's field can't be set on a product.
        Assert.Equal("customfield.not_on_record", await Refused(() => Save("item", _widget.Id, (f["customer.type"], "Retail"))));
        Assert.Equal("customfield.entity_type", await Refused(() => Save("account", _widget.Id, (f["item.pack_size"], "x"))));

        await Save("item", _widget.Id, (f["item.pack_size"], null));
        Assert.DoesNotContain("Pack size", (await Values("item", _widget.Id)).Keys);

        var changes = (await Audit()).Where(r => r.EntityType == "item" && r.EntityId == _widget.Id && r.Action == AuditAction.Updated).ToList();
        Assert.Equal(2, changes.Count);
        Assert.Contains("Case of 12", changes[^1].Before, StringComparison.Ordinal);

        // The kind of value is locked once anything has one.
        Assert.Equal("customfield.type_locked", await Refused(() => Fields(s => s.UpdateAsync(f["item.min_order"].Id, new CustomFieldDetails("Minimum order quantity", CustomFieldType.Text)))));

        // Switched off, it leaves the record's panel but keeps its value.
        await Fields(s => s.SetActiveAsync(required.Id, false));
        Assert.DoesNotContain("Shelf", (await Fields(s => s.EntriesAsync("item", _widget.Id))).Select(e => e.Field.Name));
        await Fields(s => s.SetActiveAsync(required.Id, true));
        Assert.Equal("A3", (await Values("item", _widget.Id))["Shelf"]);
    }

    [Fact]
    public async Task An_invoice_keeps_copies_of_its_customers_and_items_fields_and_prints_them()
    {
        var f = await Presets("item.pack_size", "item.unit", "customer.sales_rep", "customer.since", "document.sales_rep", "document.ship_via", "document.tracking");
        await Fields(s => s.UpdateAsync(f["document.ship_via"].Id, new CustomFieldDetails("Ship via", CustomFieldType.Text, DefaultValue: "UPS Ground",
            Documents: CustomFieldDocuments.Invoice | CustomFieldDocuments.SalesOrder)));
        await Save("item", _widget.Id, (f["item.pack_size"], "Case of 12"), (f["item.unit"], "Case"));
        await Save("customer", _contoso.Id, (f["customer.sales_rep"], "Dana"), (f["customer.since"], "2020-01-15"));

        var invoice = await In<InvoiceService, Invoice>(s => s.CreateAsync(new CreateInvoiceCommand(_contoso.Id, Oct1,
            [new SalesLineDraft(_widget.Id, "Widget", 2m, 20m), new SalesLineDraft(_bolts.Id, "Bolts", 10m, 2m)])));

        // The customer's sales rep went into the invoice's own "Sales rep" field; the default filled Ship via.
        Assert.Equal(new Dictionary<string, string?> { ["Sales rep"] = "Dana", ["Ship via"] = "UPS Ground" }, await Values("invoice", invoice.Id));
        Assert.Equal(f["document.sales_rep"].Id, (await Fields(s => s.EntriesAsync("invoice", invoice.Id))).Single(e => e.Value == "Dana").Field.Id);
        var lines = await Fields(s => s.LineEntriesAsync(invoice.Id));
        Assert.Equal(["Pack size: Case of 12", "Unit: Case"], lines[1].Select(e => $"{e.Field.Name}: {e.Display}"));
        Assert.False(lines.ContainsKey(2));

        // Later changes to the item or customer don't touch the invoice; its own fields can still be filled in.
        await Save("item", _widget.Id, (f["item.pack_size"], "Case of 24"));
        await Save("customer", _contoso.Id, (f["customer.sales_rep"], "Lee"));
        await Save("invoice", invoice.Id, (f["document.tracking"], "1Z999"));
        Assert.Equal("Case of 12", (await Fields(s => s.LineEntriesAsync(invoice.Id)))[1][0].Value);
        Assert.Equal("Dana", (await Values("invoice", invoice.Id))["Sales rep"]);

        var form = await In<FormService, SalesForm>(s => s.BuildAsync(FormKind.Invoice, invoice.Id));
        Assert.Contains(("Sales rep", "Dana"), form.Facts);
        Assert.Contains(("Tracking number", "1Z999"), form.Facts);
        Assert.Equal(["Unit"], form.ExtraColumns);
        var widgetLine = form.Lines.Single(l => l.LineNumber == 1);
        Assert.Equal(("Pack size: Case of 12", "Case"), (widgetLine.Details, Assert.Single(widgetLine.Extra!)));
        Assert.Equal("", Assert.Single(form.Lines.Single(l => l.LineNumber == 2).Extra!));

        // Not printed: kept on the screen only.
        await Fields(s => s.UpdateAsync(f["document.tracking"].Id, new CustomFieldDetails("Tracking number", CustomFieldType.Text,
            Documents: CustomFieldDocuments.Invoice, PrintOnForms: false)));
        Assert.DoesNotContain(("Tracking number", "1Z999"), (await In<FormService, SalesForm>(s => s.BuildAsync(FormKind.Invoice, invoice.Id))).Facts);
        Assert.NotEmpty((await In<FormService, RenderedForm>(s => s.RenderAsync(FormKind.Invoice, invoice.Id))).Pdf);
    }

    [Fact]
    public async Task Sales_orders_pass_their_fields_to_invoices_and_edits_copy_the_lines_again()
    {
        var f = await Presets("item.pack_size", "document.ship_via", "document.tracking");
        await Fields(s => s.UpdateAsync(f["item.pack_size"].Id, new CustomFieldDetails("Pack size", CustomFieldType.Text, Documents: CustomField.SalesDocuments)));
        await Save("item", _widget.Id, (f["item.pack_size"], "Case of 12"));

        var order = await In<SalesOrderService, SalesOrder>(s => s.CreateAsync(new SaveSalesOrderCommand(_contoso.Id, Oct1, [new SalesLineDraft(_bolts.Id, "Bolts", 5m, 2m)])));
        Assert.Empty(await Fields(s => s.LineEntriesAsync(order.Id)));
        await Save("sales_order", order.Id, (f["document.ship_via"], "Freight"), (f["document.tracking"], "PRO-1"));

        // Editing the order copies the lines' fields again and keeps its own.
        await In<SalesOrderService, SalesOrder>(s => s.UpdateAsync(order.Id, new SaveSalesOrderCommand(_contoso.Id, Oct1,
            [new SalesLineDraft(_bolts.Id, "Bolts", 5m, 2m), new SalesLineDraft(_widget.Id, "Widget", 1m, 20m)])));
        Assert.Equal([2], (await Fields(s => s.LineEntriesAsync(order.Id))).Keys);
        Assert.Equal("Freight", (await Values("sales_order", order.Id))["Ship via"]);

        var invoice = await In<SalesOrderService, Invoice>(s => s.InvoiceAsync(order.Id, new InvoiceFromSourceCommand(Oct1)));
        Assert.Equal(new Dictionary<string, string?> { ["Ship via"] = "Freight", ["Tracking number"] = "PRO-1" }, await Values("invoice", invoice.Id));

        // An estimate edited after its item changed takes the new value.
        var estimate = await In<EstimateService, Estimate>(s => s.CreateAsync(new SaveEstimateCommand(_contoso.Id, Oct1, [new SalesLineDraft(_widget.Id, "Widget", 1m, 20m)])));
        await Save("item", _widget.Id, (f["item.pack_size"], "Case of 6"));
        Assert.Equal("Case of 12", (await Fields(s => s.LineEntriesAsync(estimate.Id)))[1][0].Value);
        await In<EstimateService, Estimate>(s => s.UpdateAsync(estimate.Id, new SaveEstimateCommand(_contoso.Id, Oct1, [new SalesLineDraft(_widget.Id, "Widget", 3m, 20m)])));
        Assert.Equal("Case of 6", (await Fields(s => s.LineEntriesAsync(estimate.Id)))[1][0].Value);
    }

    [Fact]
    public async Task Vendor_fields_go_onto_purchase_orders_and_bills_made_from_them()
    {
        var f = await Presets("vendor.ship_via", "document.ship_via", "document.contract", "item.mpn");
        var expenses = await _db.CreateAccount("Supplies", AccountType.Expense, "6100");
        await Save("vendor", _supplier.Id, (f["vendor.ship_via"], "Their truck"));
        await Save("item", _widget.Id, (f["item.mpn"], "W-100-B"));

        var order = await In<PurchaseOrderService, PurchaseOrder>(s => s.CreateAsync(new SavePurchaseOrderCommand(_supplier.Id, Oct1,
            [new PurchaseOrderLineDraft(null, expenses.Id, "Paper", 1m, 40m)])));
        Assert.Equal("Their truck", (await Values("purchase_order", order.Id))["Ship via"]);
        await Save("purchase_order", order.Id, (f["document.contract"], "C-77"));

        var bill = await In<PurchaseOrderService, Bill>(s => s.CreateBillAsync(order.Id, new BillFromOrderCommand(Oct1, "INV-1")));
        // Ship via isn't a bill field; the contract number is, and comes from the order.
        Assert.Equal(new Dictionary<string, string?> { ["Contract number"] = "C-77" }, await Values("bill", bill.Id));
    }

    [Fact]
    public async Task Imports_fill_custom_columns_search_finds_values_and_the_report_builder_shows_them()
    {
        var f = await Presets("customer.type", "customer.territory", "item.brand", "document.job");
        var csv = "Name,Email,Customer type,Territory or region\nNorthwind,nw@example.com,wholesale,West\nFabrikam,,Retail,East\n";
        var preview = await In<CustomerImportService, ImportResult<CustomerImportRow>>(s => s.PreviewAsync(csv));
        Assert.Equal(2, preview.NewCount);
        await In<CustomerImportService, ImportResult<CustomerImportRow>>(s => s.ImportAsync(csv));
        var northwind = (await In<CustomerService, IReadOnlyList<CustomerSummary>>(s => s.ListAsync(Oct1))).Single(c => c.Customer.DisplayName == "Northwind").Customer;
        Assert.Equal(new Dictionary<string, string?> { ["Customer type"] = "Wholesale", ["Territory or region"] = "West" }, await Values("customer", northwind.Id));

        var bad = await In<CustomerImportService, ImportResult<CustomerImportRow>>(s => s.PreviewAsync("Name,Customer type\nAdatum,Space station\n"));
        Assert.Equal(1, bad.ErrorCount);
        Assert.Contains("Space station", bad.Rows[0].Message, StringComparison.Ordinal);

        var gadgetSales = await _db.CreateAccount("Gadget sales", AccountType.Income);
        await In<ItemImportService, ImportResult<ItemImportRow>>(s => s.ImportAsync("Name,Price,Brand / manufacturer\nGadget,5,Acme Tools\n", gadgetSales.Id));
        var hits = await In<SearchService, SearchResults>(s => s.SearchAsync("acme tools"));
        Assert.Equal("Gadget", Assert.Single(hits.Hits).Title);

        var invoice = await In<InvoiceService, Invoice>(s => s.CreateAsync(new CreateInvoiceCommand(northwind.Id, Oct1, [new SalesLineDraft(_widget.Id, "Widget", 1m, 20m)])));
        await Save("invoice", invoice.Id, (f["document.job"], "Warehouse 3"));
        Assert.Contains(SearchKind.Invoice, (await In<SearchService, SearchResults>(s => s.SearchAsync("warehouse 3"))).Hits.Select(h => h.Kind));

        var query = $"preset=Custom&from=2026-10-01&to=2026-10-31&basis=Accrual&columns=Date,Name,Amount&fields={f["customer.territory"].Id},{f["document.job"].Id}"
            + $"&fieldId={f["customer.type"].Id}&fieldValue=whole";
        var table = await _db.InScope(sp => sp.GetRequiredService<ReportCatalog>().RunAsync("builder", ReportParameters.Parse(query)));
        Assert.Equal(["Date", "Name", "Territory or region", "Job or site name", "Amount"], table.Columns.Select(c => c.Header));
        var rows = table.Rows.Where(r => r.Cells[1]?.Value as string == "Northwind").ToList();
        Assert.NotEmpty(rows);
        Assert.All(rows, r => Assert.Equal(("West", "Warehouse 3"), (r.Cells[2]?.Value as string, r.Cells[3]?.Value as string)));

        var columns = await Fields(s => s.ListColumnsAsync(CustomFieldTarget.Customer));
        Assert.Equal(["Customer type"], columns.Columns.Select(c => c.Name));
        Assert.Equal("Wholesale", columns.Values[northwind.Id][f["customer.type"].Id]);
    }
}
