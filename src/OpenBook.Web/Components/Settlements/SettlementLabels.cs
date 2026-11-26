using System.Globalization;
using OpenBook.Domain.Settlements;

namespace OpenBook.Web.Components.Settlements;

public static class SettlementLabels
{
    public static string Status(SettlementStatus status) => status switch
    {
        SettlementStatus.Waiting => "Waiting to post",
        SettlementStatus.Posted => "Posted",
        _ => "Dismissed",
    };

    /// <summary>"1,207", "124.3"; nothing for none.</summary>
    public static string Miles(decimal? miles) => miles is null or 0m ? string.Empty : miles.Value.ToString("#,0.#", CultureInfo.CurrentCulture);

    public static string Gallons(decimal? gallons) => gallons is null or 0m ? string.Empty : gallons.Value.ToString("#,0.###", CultureInfo.CurrentCulture);
}
