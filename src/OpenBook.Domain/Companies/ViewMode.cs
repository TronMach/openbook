using OpenBook.Domain.Common;

namespace OpenBook.Domain.Companies;

/// <summary>
/// How the screens are laid out for a person: the full menu, or the simple view for a one-person business (a task
/// menu bar, a workflow home page). Only what's shown changes; every page, rule and permission is the same in both.
/// </summary>
public enum ViewMode
{
    Full,
    Simple,
}

/// <summary>
/// How the full view's main menu is drawn: a bar of area links (the default), or a drop-down per area listing its
/// pages. Like the view, only what's shown changes.
/// </summary>
public enum MenuStyle
{
    Bar,
    DropDowns,
}

/// <summary>
/// A person's own choices of how the screens look, one row per user (not audited, like dashboard panels): their view
/// (null = the company's default) and the full view's menu style.
/// </summary>
public sealed class ViewPreference
{
    private ViewPreference()
    {
        Username = string.Empty;
    }

    public string Username { get; private set; }

    /// <summary>The view they chose; null when they've only chosen a menu style and follow the company's default view.</summary>
    public ViewMode? Mode { get; private set; }

    public MenuStyle Menu { get; private set; }

    public static ViewPreference Create(string username) => new()
    {
        Username = Text.Optional(username, 100, "view.user_invalid", "User") ?? throw new DomainException("view.user_invalid", "No user."),
    };

    public static ViewPreference Create(string username, ViewMode mode)
    {
        var preference = Create(username);
        preference.Set(mode);
        return preference;
    }

    public void Set(ViewMode mode) =>
        Mode = Enum.IsDefined(mode) ? mode : throw new DomainException("view.invalid", $"Unknown view {mode}.");

    public void Set(MenuStyle menu) =>
        Menu = Enum.IsDefined(menu) ? menu : throw new DomainException("view.menu_invalid", $"Unknown menu style {menu}.");
}
