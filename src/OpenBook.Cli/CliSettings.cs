using System.Text.Json;

namespace OpenBook.Cli;

/// <summary>
/// Where the CLI connects: the server's address and an API key. Saved by <c>openbook config</c> in a file only the
/// user can read (<see cref="DefaultPath"/>); <c>OPENBOOK_URL</c> / <c>OPENBOOK_API_KEY</c> and the <c>--url</c> /
/// <c>--key</c> options override it, in that order.
/// </summary>
public sealed record CliSettings(string? Url, string? ApiKey)
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static string DefaultPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "OpenBook", "cli.json");

    public static CliSettings Load(string path)
    {
        if (!File.Exists(path))
            return new CliSettings(null, null);
        try
        {
            return JsonSerializer.Deserialize<CliSettings>(File.ReadAllText(path)) ?? new CliSettings(null, null);
        }
        catch (JsonException)
        {
            return new CliSettings(null, null);
        }
    }

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(this, Json));
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    /// <summary>The key with all but its prefix and last four characters hidden, for showing.</summary>
    public string? MaskedKey => ApiKey is { Length: > 8 } key ? $"{key[..4]}…{key[^4..]}" : ApiKey is null ? null : "…";
}
