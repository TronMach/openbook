using OpenBook.Domain.Common;

namespace OpenBook.Domain.Reporting;

/// <summary>Which dashboard panels a user has hidden (keys from the dashboard page), one row per user.</summary>
public sealed class DashboardPreference
{
    private DashboardPreference()
    {
        Username = string.Empty;
        HiddenPanels = string.Empty;
    }

    public string Username { get; private set; }

    /// <summary>Comma-separated panel keys.</summary>
    public string HiddenPanels { get; private set; }

    public IReadOnlySet<string> Hidden => HiddenPanels.Split(',', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);

    public static DashboardPreference Create(string username) =>
        new() { Username = Text.Optional(username, 100, "dashboard.user_invalid", "User") ?? throw new DomainException("dashboard.user_invalid", "No user.") };

    public void Hide(IEnumerable<string> panels)
    {
        ArgumentNullException.ThrowIfNull(panels);
        var keys = panels.Select(p => p.Trim()).Where(p => p.Length is > 0 and <= 40 && p.All(c => char.IsAsciiLetterOrDigit(c) || c == '-'))
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        HiddenPanels = string.Join(',', keys);
    }
}
