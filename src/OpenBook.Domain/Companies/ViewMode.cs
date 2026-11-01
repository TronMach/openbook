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

/// <summary>A person's own choice of view, one row per user (not audited, like dashboard panels); none = the company's default.</summary>
public sealed class ViewPreference
{
    private ViewPreference()
    {
        Username = string.Empty;
    }

    public string Username { get; private set; }

    public ViewMode Mode { get; private set; }

    public static ViewPreference Create(string username, ViewMode mode)
    {
        var preference = new ViewPreference
        {
            Username = Text.Optional(username, 100, "view.user_invalid", "User") ?? throw new DomainException("view.user_invalid", "No user."),
        };
        preference.Set(mode);
        return preference;
    }

    public void Set(ViewMode mode) =>
        Mode = Enum.IsDefined(mode) ? mode : throw new DomainException("view.invalid", $"Unknown view {mode}.");
}
