namespace OpenBook.Application.Companies;

/// <summary>
/// The access area of a kind of record, by the entity type the audit log names it with: what's kept with a record
/// (its files) and lists of records from several areas follow this, as search results do.
/// </summary>
public static class RecordAreas
{
    public static string Of(string entityType) => entityType switch
    {
        "invoice" or "payment" or "sales_receipt" or "estimate" or "credit_memo" or "refund" or "write_off" or "sales_order" or "retainer"
            or "sales_tax_payment" or "customer" or "item" => "sales",
        "bill" or "bill_payment" or "vendor_credit" or "vendor_refund" or "purchase_order" or "receipt" or "vendor" => "purchases",
        "captured_document" => "documents",
        "bank_transaction" or "deposit" or "reconciliation" => "banking",
        "journal_entry" or "account" => "ledger",
        "inventory_adjustment" or "assembly_build" or "landed_cost" or "stock_transfer" => "inventory",
        "fixed_asset" or "loan" => "assets",
        "grant" or "donation" or "pledge" => "nonprofit",
        "project" => "projects",
        _ => "data",
    };
}
