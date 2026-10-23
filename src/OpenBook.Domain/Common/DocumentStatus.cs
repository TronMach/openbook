namespace OpenBook.Domain.Common;

/// <summary>Business documents (invoices, bills, payments, ...) are posted, then possibly voided. Never edited.</summary>
public enum DocumentStatus
{
    Posted,
    Voided,
}
