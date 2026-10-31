using OpenBook.Domain.Common;

namespace OpenBook.Domain.Companies;

/// <summary>
/// A document waiting to be printed ("print later"), until a batch prints it (<see cref="PrintedAt"/>). A convenience
/// list beside the books, not audited, like dashboard preferences.
/// </summary>
public sealed class PrintQueueItem
{
    /// <summary>The documents that can wait in the queue (statements are printed by period from the print page).</summary>
    public const FormDocuments Queueable = FormDocuments.Invoice | FormDocuments.Estimate | FormDocuments.CreditMemo
        | FormDocuments.SalesReceipt | FormDocuments.PurchaseOrder;

    private PrintQueueItem()
    {
        AddedBy = string.Empty;
    }

    public Guid Id { get; private set; }
    public FormDocuments Document { get; private set; }
    public Guid DocumentId { get; private set; }
    public DateTimeOffset AddedAt { get; private set; }
    public string AddedBy { get; private set; }
    public DateTimeOffset? PrintedAt { get; private set; }
    public string? PrintedBy { get; private set; }

    public bool IsWaiting => PrintedAt is null;

    public static PrintQueueItem Create(FormDocuments document, Guid documentId, DateTimeOffset addedAt, string addedBy)
    {
        if (FormDesign.Each(document).Count() != 1 || !Queueable.HasFlag(document))
            throw new DomainException("print.document_invalid", "Only invoices, estimates, credit memos, sales receipts and purchase orders wait to be printed.");
        return new PrintQueueItem
        {
            Id = Guid.CreateVersion7(),
            Document = document,
            DocumentId = documentId,
            AddedAt = addedAt.ToUniversalTime(),
            AddedBy = addedBy,
        };
    }

    public void MarkPrinted(DateTimeOffset at, string by)
    {
        PrintedAt = at.ToUniversalTime();
        PrintedBy = by;
    }

    /// <summary>Back in the queue (it didn't come out right).</summary>
    public void PutBack()
    {
        PrintedAt = null;
        PrintedBy = null;
    }
}
