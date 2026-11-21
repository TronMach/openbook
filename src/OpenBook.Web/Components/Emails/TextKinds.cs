using OpenBook.Domain.Email;

namespace OpenBook.Web.Components.Emails;

/// <summary>The documents that can be texted, by the name their pages use in addresses ("/sales/invoices/…").</summary>
public static class TextKinds
{
    public static EmailKind? FromRoute(string? kind) => kind switch
    {
        "invoices" => EmailKind.Invoice,
        "estimates" => EmailKind.Estimate,
        "credit-memos" => EmailKind.CreditMemo,
        "receipts" => EmailKind.SalesReceipt,
        _ => null,
    };
}
