using OpenBook.Domain.Banking;
using OpenBook.Domain.Ledger;
using OpenBook.Domain.Purchases;
using OpenBook.Domain.Sales;
using OpenBook.Domain.SalesTax;

namespace OpenBook.Web.Components;

/// <summary>Where to send someone who clicks a ledger line: the document that posted it, or the journal entry.</summary>
public static class Links
{
    public static string ForEntry(string source, Guid? sourceId, Guid journalEntryId) => (source, sourceId) switch
    {
        (Invoice.Source, { } id) => $"/sales/invoices/{id}",
        (CustomerPayment.Source, { } id) => $"/sales/payments/{id}",
        (SalesReceipt.Source, { } id) => $"/sales/receipts/{id}",
        (Bill.Source, { } id) => $"/purchases/bills/{id}",
        (BillPayment.Source, { } id) => $"/purchases/payments/{id}",
        (CreditMemo.Source, { } id) => $"/sales/credit-memos/{id}",
        (CustomerRefund.Source, { } id) => $"/sales/refunds/{id}",
        (BankDeposit.Source, { } id) => $"/banking/deposits/{id}",
        (VendorCredit.Source, { } id) => $"/purchases/vendor-credits/{id}",
        (VendorRefund.Source, { } id) => $"/purchases/vendor-refunds/{id}",
        (SalesTaxPayment.Source, { } id) => $"/sales-tax/payments/{id}",
        (OpenBook.Domain.Inventory.InventoryAdjustment.Source, { } id) => $"/inventory/adjustments/{id}",
        (OpenBook.Domain.Inventory.AssemblyBuild.Source, { } id) => $"/inventory/builds/{id}",
        (OpenBook.Domain.Inventory.LandedCost.Source, { } id) => $"/inventory/landed-costs/{id}",
        (BankTransaction.SpendSource or BankTransaction.ReceiveSource or BankTransaction.TransferSource, { } id) => $"/banking/transactions/{id}",
        _ => $"/journal/{journalEntryId}",
    };

    /// <summary>The page for something named in the audit log, or null when it has none.</summary>
    public static string? ForAuditEntity(string entityType, Guid id) => entityType switch
    {
        "account" => $"/accounts/{id}",
        "bank_transaction" => $"/banking/transactions/{id}",
        "bill" => $"/purchases/bills/{id}",
        "bill_payment" => $"/purchases/payments/{id}",
        "customer" => $"/customers/{id}",
        "invoice" => $"/sales/invoices/{id}",
        "item" => $"/items/{id}",
        "journal_entry" => $"/journal/{id}",
        "payment" => $"/sales/payments/{id}",
        "reconciliation" => $"/banking/reconciliations/{id}",
        "recurring_entry" => $"/journal/recurring/{id}",
        "sales_receipt" => $"/sales/receipts/{id}",
        "vendor" => $"/vendors/{id}",
        "estimate" => $"/sales/estimates/{id}",
        "credit_memo" => $"/sales/credit-memos/{id}",
        "refund" => $"/sales/refunds/{id}",
        "deposit" => $"/banking/deposits/{id}",
        "recurring_invoice" => $"/sales/recurring/{id}",
        "vendor_credit" => $"/purchases/vendor-credits/{id}",
        "vendor_refund" => $"/purchases/vendor-refunds/{id}",
        "recurring_bill" => $"/purchases/recurring/{id}",
        "bank_rule" => $"/banking/rules/{id}",
        "bank_connection" => $"/banking/connections/{id}",
        "petty_cash_fund" => $"/banking/petty-cash/{id}",
        "positive_pay_export" => $"/banking/positive-pay?accountId={id}",
        "tax_agency" => $"/sales-tax/agencies/{id}",
        "tax_rate" => $"/sales-tax/rates/{id}",
        "tax_code" => $"/sales-tax/codes/{id}",
        "sales_tax_payment" => $"/sales-tax/payments/{id}",
        "budget" => $"/reports/budgets/{id}",
        "inventory_adjustment" => $"/inventory/adjustments/{id}",
        "assembly_build" => $"/inventory/builds/{id}",
        "landed_cost" => $"/inventory/landed-costs/{id}",
        "stock_transfer" => $"/inventory/transfers/{id}",
        "pending_build" => "/inventory#pending",
        "warehouse" => "/inventory/warehouses",
        "saved_report" => "/reports",
        "price_level" => $"/pricing/levels/{id}",
        "price_rule" => $"/pricing/rules/{id}",
        "segment" => $"/segments/{id}",
        "backup_schedule" => "/backup",
        "email_settings" => "/settings/email",
        "form_style" => "/settings/forms",
        "company" => "/settings",
        "closing_date" => "/settings/close-books",
        _ => null,
    };

    /// <summary>"invoice", "bank transaction", ... or null for a manual entry.</summary>
    public static string? DocumentName(string source) => source switch
    {
        Invoice.Source => "invoice",
        CustomerPayment.Source => "customer payment",
        SalesReceipt.Source => "sales receipt",
        Bill.Source => "bill",
        BillPayment.Source => "bill payment",
        CreditMemo.Source => "credit memo",
        CustomerRefund.Source => "customer refund",
        BankDeposit.Source => "bank deposit",
        VendorCredit.Source => "vendor credit",
        VendorRefund.Source => "vendor refund",
        SalesTaxPayment.Source => "sales tax payment",
        OpenBook.Domain.Inventory.InventoryAdjustment.Source => "inventory adjustment",
        OpenBook.Domain.Inventory.AssemblyBuild.Source => "assembly build",
        OpenBook.Domain.Inventory.LandedCost.Source => "landed cost",
        BankTransaction.SpendSource or BankTransaction.ReceiveSource or BankTransaction.TransferSource => "bank transaction",
        _ when JournalEntry.IsJournalSource(source) => null,
        _ => "document",
    };
}
