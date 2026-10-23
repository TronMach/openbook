using OpenBook.Domain.Banking;
using OpenBook.Domain.Ledger;
using OpenBook.Domain.Purchases;
using OpenBook.Domain.Sales;

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
        BankTransaction.SpendSource or BankTransaction.ReceiveSource or BankTransaction.TransferSource => "bank transaction",
        _ when JournalEntry.IsJournalSource(source) => null,
        _ => "document",
    };
}
