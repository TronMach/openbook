using Microsoft.AspNetCore.Components;

namespace OpenBook.Web.Components;

/// <summary>
/// One picture per area of OpenBook, beside its name wherever the menus show it: the full view's bar and drop-downs
/// and the simple view's shortcut column. The same area always has the same picture, so people can find it again by
/// its look as well as its word; the word is always shown too, and the picture is hidden from screen readers.
/// Simple line drawings of our own (24 × 24), stroked in the text color, so they follow the theme.
/// </summary>
public static class AreaIcons
{
    public const string Home = "M3 11.5 12 4l9 7.5M5.5 9.5V20h5v-6h3v6h5V9.5";
    public const string People = "M9 11a3.5 3.5 0 1 0 0-7 3.5 3.5 0 0 0 0 7ZM2.5 20c.6-3.6 3.2-6 6.5-6s5.9 2.4 6.5 6M16 4.3a3.5 3.5 0 0 1 0 6.4M18 14.2c2 .8 3.2 2.9 3.5 5.8";
    public const string Invoice = "M6 3h9l4 4v14H6V3ZM15 3v4h4M9 11h7M9 14.5h7M9 18h4";
    public const string Store = "M3.5 9.5 5 4h14l1.5 5.5M3.5 9.5h17M3.5 9.5c0 1.7 1.3 3 3 3s3-1.3 3-3c0 1.7 1.3 3 2.5 3s2.5-1.3 2.5-3c0 1.7 1.3 3 3 3s3-1.3 3-3M5 12.3V20h14v-7.7M10 20v-4.5h4V20";
    public const string Box = "M12 3 20.5 7.5v9L12 21l-8.5-4.5v-9L12 3ZM3.5 7.5 12 12l8.5-4.5M12 12v9";
    public const string Bank = "M3 9.5 12 4l9 5.5M4 9.5h16M6 9.5V18M10 9.5V18M14 9.5V18M18 9.5V18M3 20.5h18";
    public const string Chart = "M4 20.5h16M6.5 20.5v-6M11 20.5V8M15.5 20.5v-9M20 20.5V5";
    public const string Gauge = "M3.5 17a8.5 8.5 0 0 1 17 0M12 17l4-5M3.5 20.5h17";
    public const string Clock = "M12 21a9 9 0 1 0 0-18 9 9 0 0 0 0 18ZM12 7v5.5l3.5 2";
    public const string Clipboard = "M8.5 4.5H6v16h12v-16h-2.5M9 3h6v3H9V3ZM9 12l2 2 4-4M9 17h6";
    public const string Gift = "M4 11h16v9.5H4V11ZM3 7.5h18V11H3V7.5ZM12 7.5v13M12 7.5C10 4 7 4 7 6s3 1.5 5 1.5ZM12 7.5c2-3.5 5-3.5 5-1.5s-3 1.5-5 1.5Z";
    public const string Ledger = "M6 3.5h12.5v17H6A1.5 1.5 0 0 1 4.5 19V5A1.5 1.5 0 0 1 6 3.5ZM8.5 3.5v17M11.5 8h4.5M11.5 11.5h4.5";
    public const string Pencil = "M4 20.5h7M6 15l9.5-9.5a2.1 2.1 0 0 1 3 3L9 18l-4 1 1-4ZM14 7l3 3";
    public const string Cart = "M3 4h2.5l2.2 11h10.6L20.5 7.5H6.5M9.5 20a1 1 0 1 0 0-2 1 1 0 0 0 0 2ZM17 20a1 1 0 1 0 0-2 1 1 0 0 0 0 2Z";
    public const string Truck = "M2.5 6h11v10h-11V6ZM13.5 9.5h4l4 3.5v3h-8V9.5ZM7 19a2 2 0 1 0 0-4 2 2 0 0 0 0 4ZM17 19a2 2 0 1 0 0-4 2 2 0 0 0 0 4Z";
    public const string Gear = "M10.6 5.3 10.7 3.1 13.3 3.1 13.4 5.3 15.8 6.2 17.4 4.8 19.2 6.6 17.8 8.2 18.7 10.6 20.9 10.7 20.9 13.3 18.7 13.4 17.8 15.8 19.2 17.4 17.4 19.2 15.8 17.8 13.4 18.7 13.3 20.9 10.7 20.9 10.6 18.7 8.2 17.8 6.6 19.2 4.8 17.4 6.2 15.8 5.3 13.4 3.1 13.3 3.1 10.7 5.3 10.6 6.2 8.2 4.8 6.6 6.6 4.8 8.2 6.2ZM12 15a3 3 0 1 0 0-6 3 3 0 0 0 0 6Z";

    /// <summary>The picture of an area of the menu (an entry of <see cref="Editions.Menu"/>, or <c>/settings</c>).</summary>
    public static string For(string area) => area switch
    {
        "/" => Home,
        "/customers" => People,
        "/sales" => Invoice,
        "/purchases" => Store,
        "/banking" => Bank,
        "/inventory" => Box,
        "/projects" => Clipboard,
        "/time" => Clock,
        "/nonprofit" => Gift,
        "/settlements" => Truck,
        "/dispatch" => Truck,
        "/accounts" => Ledger,
        "/journal" => Pencil,
        "/reports" => Chart,
        "/dashboard" => Gauge,
        "/settings" => Gear,
        _ => throw new ArgumentOutOfRangeException(nameof(area), area, "No picture for this area."),
    };

    /// <summary>The drawing as markup, hidden from screen readers since its name is written beside it.</summary>
    public static MarkupString Svg(string path, int size = 16) => new(
        $"""<svg class="area-icon" viewBox="0 0 24 24" width="{size}" height="{size}" aria-hidden="true" focusable="false"><path d="{path}" /></svg>""");
}
