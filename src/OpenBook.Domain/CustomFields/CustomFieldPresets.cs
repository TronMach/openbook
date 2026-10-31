namespace OpenBook.Domain.CustomFields;

/// <summary>A ready-made field: added as an ordinary custom field, which can then be renamed, changed or switched off.</summary>
public sealed record CustomFieldPreset(string Key, CustomFieldTarget Target, CustomFieldDetails Details);

/// <summary>
/// The ready-made fields offered on the custom fields page. Keys are stable (they're stored on the fields added from
/// them, so a preset isn't offered twice); relabel, never rekey.
/// </summary>
public static class CustomFieldPresets
{
    private const CustomFieldDocuments Sales = CustomField.SalesDocuments;
    private const CustomFieldDocuments Invoice = CustomFieldDocuments.Invoice;
    private const CustomFieldDocuments Estimate = CustomFieldDocuments.Estimate;
    private const CustomFieldDocuments Order = CustomFieldDocuments.SalesOrder;
    private const CustomFieldDocuments Receipt = CustomFieldDocuments.SalesReceipt;
    private const CustomFieldDocuments PurchaseOrder = CustomFieldDocuments.PurchaseOrder;
    private const CustomFieldDocuments Bill = CustomFieldDocuments.Bill;

    public static readonly IReadOnlyList<CustomFieldPreset> All =
    [
        // Products and services.
        Item("item.pack_size", "Pack size", CustomFieldType.Text, "How it's packed, e.g. \"Case of 12\".", docs: Sales | PurchaseOrder, list: true),
        Item("item.unit", "Unit", CustomFieldType.PickList, "What one of it is.", ["Each", "Pair", "Box", "Case", "Pack", "Dozen", "Pallet", "lb", "kg", "oz", "g", "ft", "m", "gal", "L", "Hour", "Day"],
            docs: Sales | PurchaseOrder, placement: CustomFieldPlacement.Column),
        Item("item.brand", "Brand / manufacturer", CustomFieldType.Text, list: true),
        Item("item.mpn", "Manufacturer part number", CustomFieldType.Text, docs: PurchaseOrder),
        Item("item.model", "Model number", CustomFieldType.Text),
        Item("item.size", "Size", CustomFieldType.Text),
        Item("item.color", "Color", CustomFieldType.Text),
        Item("item.material", "Material", CustomFieldType.Text),
        Item("item.weight", "Weight", CustomFieldType.Text, "With its unit, e.g. \"2.5 lb\"."),
        Item("item.dimensions", "Dimensions", CustomFieldType.Text, "e.g. \"12 x 8 x 4 in\"."),
        Item("item.origin", "Country of origin", CustomFieldType.Text),
        Item("item.hs_code", "HS tariff code", CustomFieldType.Text, "The customs code for exports."),
        Item("item.min_order", "Minimum order quantity", CustomFieldType.WholeNumber, minimum: 0),
        Item("item.lead_time", "Lead time (days)", CustomFieldType.WholeNumber, "How long it takes to get more.", minimum: 0),
        Item("item.warranty", "Warranty", CustomFieldType.Text, "e.g. \"1 year parts and labor\"."),
        Item("item.handling", "Storage or handling instructions", CustomFieldType.LongText),
        Item("item.hazmat", "Hazardous material class", CustomFieldType.PickList, null,
            ["Class 1: Explosives", "Class 2: Gases", "Class 3: Flammable liquids", "Class 4: Flammable solids", "Class 5: Oxidizers and organic peroxides",
             "Class 6: Toxic and infectious substances", "Class 7: Radioactive", "Class 8: Corrosives", "Class 9: Miscellaneous"]),
        Item("item.web_page", "Product web page", CustomFieldType.Link),

        // Customers.
        Customer("customer.type", "Customer type", CustomFieldType.PickList, null,
            ["Retail", "Wholesale", "Distributor", "Contractor", "Business", "Government", "Nonprofit", "Other"], list: true),
        Customer("customer.sales_rep", "Sales rep", CustomFieldType.Text, "Copied onto their invoices, estimates, orders and receipts.",
            docs: Invoice | Estimate | Order | Receipt, list: true),
        Customer("customer.territory", "Territory or region", CustomFieldType.Text),
        Customer("customer.referral", "Referral source", CustomFieldType.PickList, "How they heard about you.",
            ["Referral", "Web search", "Social media", "Advertising", "Trade show", "Existing customer", "Other"]),
        Customer("customer.since", "Customer since", CustomFieldType.Date),
        Customer("customer.delivery", "Delivery instructions", CustomFieldType.LongText, "Copied onto their sales orders and invoices.", docs: Order | Invoice),
        Customer("customer.receiving_hours", "Receiving hours", CustomFieldType.Text, "e.g. \"Mon–Fri 7am–3pm\".", docs: Order),
        Customer("customer.carrier", "Preferred carrier", CustomFieldType.Text, docs: Order),
        Customer("customer.carrier_account", "Carrier account number", CustomFieldType.Text, "Their shipping account, to ship on their account.", docs: Order),
        Customer("customer.contract", "Contract number", CustomFieldType.Text, docs: Invoice | Estimate | Order),
        Customer("customer.contact_method", "Preferred contact method", CustomFieldType.PickList, null, ["Email", "Phone", "Text message", "Mail"]),

        // Vendors.
        Vendor("vendor.type", "Vendor type", CustomFieldType.PickList, null,
            ["Supplier", "Manufacturer", "Distributor", "Contractor", "Service provider", "Utility", "Landlord", "Other"], list: true),
        Vendor("vendor.rep_name", "Rep name", CustomFieldType.Text),
        Vendor("vendor.rep_phone", "Rep direct phone", CustomFieldType.Phone),
        Vendor("vendor.min_order", "Minimum order amount", CustomFieldType.Money, minimum: 0),
        Vendor("vendor.lead_time", "Usual lead time (days)", CustomFieldType.WholeNumber, minimum: 0),
        Vendor("vendor.ship_via", "Ship via", CustomFieldType.Text, "Copied onto your purchase orders to them.", docs: PurchaseOrder),
        Vendor("vendor.w9", "W-9 received", CustomFieldType.Date),
        Vendor("vendor.insurance", "Insurance certificate expires", CustomFieldType.Date),
        Vendor("vendor.certifications", "Certifications", CustomFieldType.Text, "e.g. \"ISO 9001, organic\"."),
        Vendor("vendor.portal", "Ordering or payment portal", CustomFieldType.Link),

        // Documents.
        Document("document.ship_via", "Ship via", CustomFieldType.Text, Invoice | Order | Receipt | PurchaseOrder,
            "On purchase orders, filled from the vendor's Ship via field when there is one."),
        Document("document.ship_date", "Ship date", CustomFieldType.Date, Invoice | Order | Receipt),
        Document("document.tracking", "Tracking number", CustomFieldType.Text, Invoice | Order | Receipt),
        Document("document.fob", "FOB", CustomFieldType.PickList, Invoice | Order | PurchaseOrder, options: ["Origin", "Destination"]),
        Document("document.requested_delivery", "Requested delivery date", CustomFieldType.Date, Estimate | Order | PurchaseOrder),
        Document("document.sales_rep", "Sales rep", CustomFieldType.Text, Invoice | Estimate | Order | Receipt,
            "Filled from the customer's Sales rep field when there is one."),
        Document("document.job", "Job or site name", CustomFieldType.Text, Sales),
        Document("document.contract", "Contract number", CustomFieldType.Text, Invoice | Estimate | Order | PurchaseOrder | Bill,
            "Filled from the customer's Contract number field when there is one."),
        Document("document.delivery", "Delivery instructions", CustomFieldType.LongText, Order | Invoice,
            "Filled from the customer's Delivery instructions field when there is one."),
    ];

    public static CustomFieldPreset? Find(string key) => All.FirstOrDefault(p => p.Key == key);

    private static CustomFieldPreset Item(
        string key, string name, CustomFieldType type, string? help = null, IReadOnlyList<string>? options = null,
        CustomFieldDocuments docs = CustomFieldDocuments.None, CustomFieldPlacement placement = CustomFieldPlacement.UnderDescription,
        bool list = false, decimal? minimum = null) =>
        new(key, CustomFieldTarget.Item, new CustomFieldDetails(name, type, help, options, Documents: docs, Placement: placement, ShowInList: list, Minimum: minimum));

    private static CustomFieldPreset Customer(
        string key, string name, CustomFieldType type, string? help = null, IReadOnlyList<string>? options = null,
        CustomFieldDocuments docs = CustomFieldDocuments.None, bool list = false) =>
        new(key, CustomFieldTarget.Customer, new CustomFieldDetails(name, type, help, options, Documents: docs, ShowInList: list));

    private static CustomFieldPreset Vendor(
        string key, string name, CustomFieldType type, string? help = null, IReadOnlyList<string>? options = null,
        CustomFieldDocuments docs = CustomFieldDocuments.None, bool list = false, decimal? minimum = null) =>
        new(key, CustomFieldTarget.Vendor, new CustomFieldDetails(name, type, help, options, Documents: docs, ShowInList: list, Minimum: minimum));

    private static CustomFieldPreset Document(
        string key, string name, CustomFieldType type, CustomFieldDocuments docs, string? help = null, IReadOnlyList<string>? options = null) =>
        new(key, CustomFieldTarget.Document, new CustomFieldDetails(name, type, help, options, Documents: docs));
}
