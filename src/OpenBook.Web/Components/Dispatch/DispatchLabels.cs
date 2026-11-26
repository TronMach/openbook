using OpenBook.Domain.Accounts;
using OpenBook.Domain.Settlements;

namespace OpenBook.Web.Components.Dispatch;

/// <summary>How the dispatch pages show owner-operator things.</summary>
public static class DispatchLabels
{
    public static string Method(ReserveMethod method) => method switch
    {
        ReserveMethod.PerMile => "Per mile",
        ReserveMethod.Percent => "Percent of earnings",
        _ => "Fixed each settlement",
    };

    public static string Rate(OwnerReserve reserve) => reserve.Method switch
    {
        ReserveMethod.PerMile => $"{reserve.Rate:0.00##} a mile",
        ReserveMethod.Percent => $"{reserve.Rate:0.##}%",
        _ => Labels.Amount(reserve.Rate),
    };

    public static bool IsExpense(Account a) => a.Type is AccountType.Expense or AccountType.CostOfGoodsSold or AccountType.OtherExpense;

    /// <summary>Where a charge's money can come from: a bank, a card, or another current account such as a fuel card's.</summary>
    public static bool PaysCharges(Account a) => a.Type is AccountType.Bank or AccountType.CreditCard or AccountType.OtherCurrentLiability or AccountType.OtherCurrentAsset;
}
