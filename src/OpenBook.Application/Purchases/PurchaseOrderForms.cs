using System.Globalization;
using System.Text.Json;
using OpenBook.Application.Companies;
using OpenBook.Application.Forms;
using OpenBook.Domain.Auditing;
using OpenBook.Domain.Common;
using OpenBook.Domain.Companies;
using OpenBook.Domain.Purchases;

namespace OpenBook.Application.Purchases;

/// <summary>Purchase orders as PDF (the company's form style); <see cref="EmailService"/> emails them to the vendor.</summary>
public sealed class PurchaseOrderForms(
    IPurchasesStore store, Sales.ISalesStore sales, ICompanyStore companies, IFormRenderer renderer, CustomFields.CustomFieldService customFields, FormDesignService designs)
{
    public Task<RenderedForm> RenderAsync(Guid id, CancellationToken ct = default) => RenderAsync(id, null, ct);

    /// <param name="designId">Print with this design rather than the usual one; null = the usual one.</param>
    public async Task<RenderedForm> RenderAsync(Guid id, Guid? designId, CancellationToken ct = default)
    {
        var order = await store.GetPurchaseOrderAsync(id, ct) ?? throw new EntityNotFoundException(PurchaseOrderService.EntityType, id);
        var vendor = await store.GetVendorAsync(order.VendorId, ct) ?? throw new EntityNotFoundException("vendor", order.VendorId);
        var company = await companies.GetRequiredCompanyAsync(ct);
        using var region = CompanyRegion.Use(company);

        var facts = new List<(string, string)> { ("Date", D(order.Date)) };
        if (order.ExpectedDate is { } expected)
            facts.Add(("Expected", D(expected)));
        if (vendor.AccountNumber is { } account)
            facts.Add(("Our account", account));
        var itemIds = order.Lines.Select(l => l.ItemId).OfType<Guid>().Distinct().ToList();
        var items = itemIds.Count == 0 ? new Dictionary<Guid, Domain.Sales.Item>() : await sales.GetItemsAsync(itemIds, ct);
        // An item bought in packs says how many packs the quantity is (C329).
        static string? WithPacks(string? description, Domain.Sales.Item? item, decimal quantity) =>
            item?.InPacks(quantity) is { } packs ? string.IsNullOrWhiteSpace(description) ? $"({packs})" : $"{description} ({packs})" : description;
        var lines = order.Lines.Select(l => l.IsItem
            ? new FormLine(WithPacks(l.Description, l.ItemId is { } packed ? items.GetValueOrDefault(packed) : null, l.Quantity), l.Quantity, l.UnitCost, l.Amount, LineNumber: l.LineNumber,
                Cells: FormService.Cells(l.ItemId is { } item ? items.GetValueOrDefault(item) : null, null, null, null))
            : new FormLine(l.Description, null, null, l.Amount, LineNumber: l.LineNumber)).ToList();
        var party = new FormParty(vendor.CompanyName is { } name && name != vendor.DisplayName ? $"{vendor.DisplayName}\n{name}" : vendor.DisplayName,
            order.VendorAddress ?? vendor.Address, vendor.Email, vendor.Phone);
        var form = new SalesForm(order.Status == PurchaseOrderStatus.Cancelled ? "Purchase order (cancelled)" : "Purchase order", order.Number, FormService.CompanyParty(company), party, facts, lines,
            [new FormTotal("Total", order.Total, Emphasis: true)], order.Memo, company.HomeCurrency.Code, FormService.Unstyled, PartyLabel: "VENDOR");
        form = await designs.ApplyAsync(form, await designs.ChosenAsync(FormDocuments.PurchaseOrder, null, designId, ct), FormDocuments.PurchaseOrder, ct);
        form = await customFields.DecorateAsync(form, order.Id, ct);
        return new RenderedForm(FormService.FileName(company, form.Title, order.Number), renderer.Render(form), Guid.Empty, $"{form.Title} {order.Number}");
    }

    private static string D(DateOnly date) => date.ToString("d", CultureInfo.CurrentCulture);
}
