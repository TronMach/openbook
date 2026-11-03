namespace OpenBook.Domain.Sales;

/// <summary>How a customer paid: deposit slips total cash and checks apart, and the deposit form can pick by it.</summary>
public enum PaymentMethod
{
    Cash,
    Check,
    Card,

    /// <summary>ACH, wire or another bank transfer.</summary>
    BankTransfer,

    Other,
}

public static class PaymentMethods
{
    public static string Label(PaymentMethod? method) => method switch
    {
        PaymentMethod.Cash => "Cash",
        PaymentMethod.Check => "Check",
        PaymentMethod.Card => "Card",
        PaymentMethod.BankTransfer => "Bank transfer",
        PaymentMethod.Other => "Other",
        _ => "",
    };

    /// <summary>Null stays null; an undefined value is refused.</summary>
    public static PaymentMethod? Valid(PaymentMethod? method) =>
        method is { } m && !Enum.IsDefined(m) ? throw new Common.DomainException("payment.method_invalid", "Unknown payment method.") : method;
}
