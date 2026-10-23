using OpenBook.Domain.Banking;
using OpenBook.Domain.Ledger;
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
        (BankTransaction.SpendSource or BankTransaction.ReceiveSource or BankTransaction.TransferSource, { } id) => $"/banking/transactions/{id}",
        _ => $"/journal/{journalEntryId}",
    };

    /// <summary>"invoice", "bank transaction", ... or null for a manual entry.</summary>
    public static string? DocumentName(string source) => source switch
    {
        Invoice.Source => "invoice",
        CustomerPayment.Source => "customer payment",
        SalesReceipt.Source => "sales receipt",
        BankTransaction.SpendSource or BankTransaction.ReceiveSource or BankTransaction.TransferSource => "bank transaction",
        JournalEntry.ManualSource => null,
        _ => "document",
    };
}
