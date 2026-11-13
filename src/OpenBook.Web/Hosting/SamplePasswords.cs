namespace OpenBook.Web.Hosting;

/// <summary>
/// The sample database password from <c>docker-compose.yml</c>: anyone who has read the file knows it, so a server
/// doesn't start with it.
/// </summary>
public static class SamplePasswords
{
    public const string Sample = "change-me";

    /// <summary>Why OpenBook won't start with this connection string; null when its password isn't the sample one.</summary>
    public static string? Refusal(string? connectionString)
    {
        foreach (var part in (connectionString ?? string.Empty).Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var pair = part.Split('=', 2, StringSplitOptions.TrimEntries);
            if (pair.Length == 2 && pair[0].ToUpperInvariant() is "PASSWORD" or "PWD"
                && string.Equals(pair[1].Trim('"', '\''), Sample, StringComparison.OrdinalIgnoreCase))
                return "OpenBook won't start with the sample database password \"change-me\" from docker-compose.yml: anyone who has read "
                    + "the file knows it. Choose a password of your own and put it in both POSTGRES_PASSWORD and ConnectionStrings__OpenBook. "
                    + "A database first started with the sample password keeps it, so change it there too: docker compose exec db psql -U openbook "
                    + "-c \"ALTER USER openbook PASSWORD 'your new password'\". See docs/admin/server.md.";
        }

        return null;
    }
}
