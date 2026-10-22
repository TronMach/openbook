using System.Globalization;
using OpenBook.Domain.Companies;

namespace OpenBook.Web.Components;

public static class Labels
{
    public static string For(BusinessType type) => type switch
    {
        BusinessType.SoleProprietorship => "Sole proprietorship",
        BusinessType.SingleMemberLlc => "Single-member LLC",
        BusinessType.MultiMemberLlc => "Multi-member LLC",
        BusinessType.Partnership => "Partnership",
        BusinessType.SCorporation => "S corporation",
        BusinessType.CCorporation => "C corporation",
        BusinessType.Nonprofit => "Nonprofit",
        _ => "Other",
    };

    public static string Month(int month) => CultureInfo.CurrentCulture.DateTimeFormat.GetMonthName(month);
}
