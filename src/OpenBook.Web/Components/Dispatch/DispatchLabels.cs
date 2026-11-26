using OpenBook.Application;
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

    /// <summary>Where a deduction can go: anything but what customers or vendors owe, a bank or a card.</summary>
    public static bool TakesDeductions(Account a) =>
        a.Type is not (AccountType.AccountsReceivable or AccountType.AccountsPayable or AccountType.Bank or AccountType.CreditCard);

    /// <summary>"Dallas → Tulsa": the first pickup and the last delivery.</summary>
    public static string Route(Load load) => $"{load.FirstPickup?.Place} → {load.LastDelivery?.Place}";

    /// <summary>The next appointment still to make, in the company's time; null when there's none.</summary>
    public static string? Next(Load load) =>
        load.Stops.FirstOrDefault(s => s.Departed is null) is { AppointmentFrom: { } at } stop
            ? $"{(stop.Kind == StopKind.Pickup ? "Pickup" : "Delivery")} {Time(at)}"
            : null;

    public static string Time(DateTimeOffset at) => at.InCompanyTime().ToString("MMM d, h:mm tt", System.Globalization.CultureInfo.CurrentCulture);

    /// <summary>For a datetime-local field: the moment in the company's time.</summary>
    public static string? Field(DateTimeOffset? at) => at?.InCompanyTime().ToString("yyyy-MM-ddTHH:mm", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>A moment as the company's clock reads it, for an interactive datetime-local field.</summary>
    public static DateTime? Local(DateTimeOffset? at) => at is { } moment ? DateTime.SpecifyKind(moment.InCompanyTime().DateTime, DateTimeKind.Unspecified) : null;

    /// <summary>What the company's clock read, as a moment in UTC.</summary>
    public static DateTimeOffset? Utc(DateTime? local) =>
        local is { } wall ? new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(wall, DateTimeKind.Unspecified), CompanyTime.Zone), TimeSpan.Zero) : null;

    public static bool IsExpense(Account a) => a.Type is AccountType.Expense or AccountType.CostOfGoodsSold or AccountType.OtherExpense;

    /// <summary>Where a charge's money can come from: a bank, a card, or another current account such as a fuel card's.</summary>
    public static bool PaysCharges(Account a) => a.Type is AccountType.Bank or AccountType.CreditCard or AccountType.OtherCurrentLiability or AccountType.OtherCurrentAsset;
}
