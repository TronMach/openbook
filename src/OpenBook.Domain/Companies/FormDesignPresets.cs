namespace OpenBook.Domain.Companies;

/// <summary>A ready-made design to start from: a key that stays the same, what it's for, and its settings.</summary>
public sealed record FormDesignPreset(string Key, string Description, FormDesignDetails Details);

/// <summary>
/// Ready-made designs, added from the designs page as ordinary designs that can then be changed. Fonts are ones that come
/// with OpenBook. Keys are stable; names and settings may be improved.
/// </summary>
public static class FormDesignPresets
{
    private static readonly FormColumnDraft[] Standard = [new(FormColumns.Description), new(FormColumns.Quantity), new(FormColumns.Rate), new(FormColumns.Amount)];

    public static readonly IReadOnlyList<FormDesignPreset> All =
    [
        new("service", "Hours and rates for work done, under a colored band. No shipping address.",
            new FormDesignDetails("Service invoice", FormTemplate.Modern, "#1f3a5f", FontKey: "inter",
                Show: FormParts.Default & ~FormParts.ShipTo,
                Columns: [new(FormColumns.Description, Width: 56), new(FormColumns.Quantity, "Hours", 10), new(FormColumns.Rate, "Rate", 14), new(FormColumns.Amount)])),

        new("product", "Item, SKU and description in a boxed grid, with bill-to and ship-to side by side.",
            new FormDesignDetails("Product invoice", FormTemplate.Boxed, "#2f5d8a", FontKey: "open-sans",
                Show: FormParts.Default | FormParts.StripedRows,
                Columns: [new(FormColumns.Item, Width: 16), new(FormColumns.Description, Width: 34), new(FormColumns.Sku, Width: 12),
                    new(FormColumns.Quantity, Width: 8), new(FormColumns.Rate, Width: 12), new(FormColumns.Amount, Width: 13)])),

        new("professional", "A centered serif heading and one amount per line, for fees and retainers.",
            new FormDesignDetails("Professional", FormTemplate.Elegant, "#6d2e46", FontKey: "lora",
                Show: FormParts.Default & ~FormParts.ShipTo,
                Columns: [new(FormColumns.Description, "Services", 80), new(FormColumns.Amount, Width: 20)],
                Texts: new Dictionary<FormDocuments, (string?, string?)>
                {
                    [FormDocuments.Invoice] = (null, "Thank you for the opportunity to work with you."),
                    [FormDocuments.Estimate] = ("Proposal", "This proposal is valid for 30 days."),
                })),

        new("bold", "A strong accent bar, a large title and a colored total that stands out.",
            new FormDesignDetails("Bold", FormTemplate.Bold, "#d1495b", FontKey: "montserrat",
                Show: FormParts.Default | FormParts.StripedRows | FormParts.Acceptance, Columns: Standard)),

        new("minimal", "Black and white with hairlines and plenty of space; the color only on the total.",
            new FormDesignDetails("Minimal", FormTemplate.Minimal, "#111827", FontKey: "inter", Margins: FormMargins.Wide, Columns: Standard)),

        new("detailed", "Small type and narrow margins for long invoices: line numbers, item, lots and class.",
            new FormDesignDetails("Detailed", FormTemplate.Compact, "#0f766e", FontKey: "source-sans-3",
                Show: FormParts.Default | FormParts.StripedRows, Margins: FormMargins.Narrow, TextSize: FormTextSize.Small,
                Columns: [new(FormColumns.Number), new(FormColumns.Item, Width: 16), new(FormColumns.Description, Width: 38), new(FormColumns.Lots),
                    new(FormColumns.Class), new(FormColumns.Quantity, Width: 8), new(FormColumns.Rate, Width: 11), new(FormColumns.Amount, Width: 12)])),

        new("wide", "Landscape pages with room for many columns, including your item custom fields.",
            new FormDesignDetails("Wide", FormTemplate.Classic, "#4338ca", FontKey: "roboto", Landscape: true,
                Show: FormParts.Default | FormParts.StripedRows,
                Columns: [new(FormColumns.Number), new(FormColumns.Item), new(FormColumns.Description, Width: 40), new(FormColumns.Sku), new(FormColumns.Lots),
                    new(FormColumns.Class), new(FormColumns.Taxable), new(FormColumns.Quantity), new(FormColumns.Rate), new(FormColumns.Amount)])),

        new("packing-slip", "What was shipped, without prices: print an invoice or sales receipt with it to pack the order.",
            new FormDesignDetails("Packing slip", FormTemplate.Boxed, "#4b5563", FontKey: "roboto",
                Show: FormParts.Default & ~(FormParts.Totals | FormParts.Terms | FormParts.Savings),
                Columns: [new(FormColumns.Item, Width: 18), new(FormColumns.Description, Width: 42), new(FormColumns.Sku, Width: 14),
                    new(FormColumns.Lots, Width: 16), new(FormColumns.Quantity, "Shipped", 10)],
                Texts: new Dictionary<FormDocuments, (string?, string?)>
                {
                    [FormDocuments.Invoice] = ("Packing slip", "Please check the goods against this slip and let us know of anything missing."),
                    [FormDocuments.SalesReceipt] = ("Packing slip", null),
                })),
    ];

    public static FormDesignPreset? Find(string? key) => All.FirstOrDefault(p => p.Key == key);
}
