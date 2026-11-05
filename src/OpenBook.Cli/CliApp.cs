using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace OpenBook.Cli;

/// <summary>
/// The <c>openbook</c> command: every command is a call to the server's API with the user's API key, so it does
/// exactly what the key's user may do, with the same rules and audit trail as the web app.
/// </summary>
public sealed class CliApp(TextWriter output, TextWriter error, string settingsPath, Func<CliSettings, HttpClient>? clientFactory = null)
{
    private static readonly HashSet<string> Flags = new(StringComparer.OrdinalIgnoreCase) { "json", "dry-run", "help" };

    /// <summary>Options that take a value. Anything else is a typing mistake, and a mistyped --dry-run mustn't import for real.</summary>
    private static readonly HashSet<string> ValueOptions = new(StringComparer.OrdinalIgnoreCase)
    {
        "url", "key", "closing-password", "idempotency-key", "format", "out", "query", "data",
    };

    /// <summary>Resources <c>list</c> knows, by API path.</summary>
    public static readonly IReadOnlyDictionary<string, string> Lists = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["accounts"] = "/api/accounts",
        ["customers"] = "/api/customers",
        ["vendors"] = "/api/vendors",
        ["items"] = "/api/items",
        ["invoices"] = "/api/sales/invoices",
        ["bills"] = "/api/purchases/bills",
        ["projects"] = "/api/projects",
        ["assets"] = "/api/assets",
        ["reports"] = "/api/reports/catalog",
        ["saved-reports"] = "/api/saved-reports",
    };

    private static readonly string[] PreferredColumns =
        ["key", "number", "fullName", "displayName", "name", "title", "date", "dueDate", "type", "status", "total", "balance", "amount", "isActive"];

    public const string Usage = """
        openbook: work with an OpenBook server from the command line.

        Setup
          openbook config --url https://books.example.com --key obk_…   save where to connect (key from Settings › API keys)
          openbook config                                               show the saved settings (key hidden)
          openbook whoami                                               who the key signs in as

        Everyday
          openbook list <what> [--json]        what: accounts, customers, vendors, items, invoices, bills, projects, assets, reports, saved-reports
          openbook report <key> [--query "preset=LastMonth&basis=Cash"] [--format table|json|csv|xlsx|pdf] [--out FILE]
          openbook import <source> <FILE> [--dry-run]     move in from another program (iif, qbxml, qbo, gnucash, xero, wave, freshbooks)
          openbook export --out FILE.zip                  everything as CSV files (admin)
          openbook backup --out FILE.openbook.db          a copy of the company file (admin, SQLite servers)
          openbook check [--json]                         check that the books agree with themselves (accountant)

        Any API call
          openbook get <path> [--out FILE]
          openbook post|put <path> --data '{"…": …}' | --data @FILE
          openbook delete <path>

        Options: --url, --key (override the saved settings and OPENBOOK_URL / OPENBOOK_API_KEY),
                 --closing-password (for changes dated in a closed period),
                 --idempotency-key KEY (on post/put/delete/import: running it again with the same key repeats the first answer).
        Exit code 0 on success, 1 when the server refuses, 2 for a mistake in the command, 3 when `check` finds problems.
        """;

    public async Task<int> RunAsync(string[] args, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(args);
        var (words, options) = Parse(args);
        foreach (var (name, value) in options)
        {
            if (!Flags.Contains(name) && !ValueOptions.Contains(name))
                return await Mistake($"Unknown option --{name}. Try `openbook help`.");
            if (ValueOptions.Contains(name) && value == "true" && args.Contains($"--{name}", StringComparer.OrdinalIgnoreCase) && args[^1].Equals($"--{name}", StringComparison.OrdinalIgnoreCase))
                return await Mistake($"--{name} needs a value.");
        }

        if (words.Count == 0 || options.ContainsKey("help") || words[0] is "help" or "-h" or "--help")
        {
            await output.WriteLineAsync(Usage);
            return words.Count == 0 && !options.ContainsKey("help") ? 2 : 0;
        }

        var saved = CliSettings.Load(settingsPath);
        var settings = new CliSettings(
            options.GetValueOrDefault("url") ?? Environment.GetEnvironmentVariable("OPENBOOK_URL") ?? saved.Url,
            options.GetValueOrDefault("key") ?? Environment.GetEnvironmentVariable("OPENBOOK_API_KEY") ?? saved.ApiKey);

        try
        {
            return words[0].ToLowerInvariant() switch
            {
                "config" => await ConfigAsync(saved, options),
                "whoami" => await ShowAsync(settings, options, "/api/me", json: true, ct),
                "list" => words.Count == 2 && Lists.TryGetValue(words[1], out var path)
                    ? await ShowAsync(settings, options, path, options.ContainsKey("json"), ct)
                    : await Mistake($"List what? One of: {string.Join(", ", Lists.Keys)}."),
                "report" => words.Count == 2 ? await ReportAsync(settings, words[1], options, ct) : await Mistake("Which report? Its key, as `openbook list reports` shows."),
                "import" => words.Count == 3 ? await ImportAsync(settings, words[1], words[2], options, ct) : await Mistake("Usage: openbook import <source> <FILE> [--dry-run]"),
                "check" => await CheckAsync(settings, options, ct),
                "export" => await DownloadAsync(settings, options, "/api/export", ct),
                "backup" => await DownloadAsync(settings, options, "/api/backup", ct),
                "get" or "post" or "put" or "delete" => words.Count == 2
                    ? await RawAsync(settings, words[0].ToUpperInvariant(), words[1], options, ct)
                    : await Mistake($"Usage: openbook {words[0]} <path>"),
                _ => await Mistake($"Unknown command '{words[0]}'. Try `openbook help`."),
            };
        }
        catch (CliException ex)
        {
            await error.WriteLineAsync(ex.Message);
            return ex.ExitCode;
        }
        catch (HttpRequestException ex)
        {
            await error.WriteLineAsync($"Can't reach the server at {settings.Url}: {ex.Message}");
            return 1;
        }
        catch (OperationCanceledException)
        {
            await error.WriteLineAsync("Stopped.");
            return 1;
        }
    }

    private async Task<int> ConfigAsync(CliSettings saved, Dictionary<string, string> options)
    {
        if (!options.ContainsKey("url") && !options.ContainsKey("key"))
        {
            await output.WriteLineAsync($"Settings file: {settingsPath}");
            await output.WriteLineAsync($"Server: {saved.Url ?? "(not set)"}");
            await output.WriteLineAsync($"API key: {saved.MaskedKey ?? "(not set)"}");
            return 0;
        }

        var url = options.GetValueOrDefault("url") ?? saved.Url;
        if (url is not null && !Uri.TryCreate(url, UriKind.Absolute, out _))
            return await Mistake($"'{url}' isn't a web address (https://…).");
        var updated = new CliSettings(url?.TrimEnd('/'), options.GetValueOrDefault("key") ?? saved.ApiKey);
        updated.Save(settingsPath);
        await output.WriteLineAsync($"Saved to {settingsPath}. The key is stored as plain text there; keep the file private.");
        return 0;
    }

    private async Task<int> ShowAsync(CliSettings settings, Dictionary<string, string> options, string path, bool json, CancellationToken ct)
    {
        using var client = Client(settings, options);
        using var response = await client.GetAsync(path, ct);
        var body = await Checked(response, ct);
        if (json)
        {
            await output.WriteLineAsync(Pretty(body));
            return 0;
        }

        using var document = JsonDocument.Parse(body);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            await output.WriteLineAsync(Pretty(body));
            return 0;
        }

        var rows = document.RootElement.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.Object).ToList();
        if (rows.Count == 0)
        {
            await output.WriteLineAsync("(none)");
            return 0;
        }

        var columns = PreferredColumns.Where(c => rows[0].TryGetProperty(c, out _)).ToList();
        if (rows[0].TryGetProperty("id", out _))
            columns.Add("id");
        await output.WriteAsync(TextTable.Render(columns, rows.Select(r => columns.Select(c => r.TryGetProperty(c, out var v) ? Text(v) : "").ToList()).ToList()));
        return 0;
    }

    /// <summary>Runs the books check and prints each check with what it found; exit code 3 when something doesn't agree.</summary>
    private async Task<int> CheckAsync(CliSettings settings, Dictionary<string, string> options, CancellationToken ct)
    {
        using var client = Client(settings, options);
        using var response = await client.GetAsync("/api/accountant/check", ct);
        var body = await Checked(response, ct);
        using var document = JsonDocument.Parse(body);
        var problems = document.RootElement.GetProperty("problems").GetInt32();
        if (options.ContainsKey("json"))
        {
            await output.WriteLineAsync(Pretty(body));
            return problems > 0 ? 3 : 0;
        }

        foreach (var result in document.RootElement.GetProperty("results").EnumerateArray())
        {
            var outcome = result.GetProperty("outcome").GetString() switch
            {
                "Problem" => "PROBLEM",
                "Note" => "NOTE   ",
                _ => "OK     ",
            };
            await output.WriteLineAsync($"{outcome} {result.GetProperty("title").GetString()}: {result.GetProperty("summary").GetString()}");
            foreach (var finding in result.GetProperty("findings").EnumerateArray())
                await output.WriteLineAsync($"        {finding.GetProperty("text").GetString()}");
        }

        await output.WriteLineAsync(problems == 0 ? "The books agree." : $"{problems} check{(problems == 1 ? "" : "s")} found problems.");
        return problems > 0 ? 3 : 0;
    }

    private async Task<int> ReportAsync(CliSettings settings, string key, Dictionary<string, string> options, CancellationToken ct)
    {
        var query = options.GetValueOrDefault("query")?.TrimStart('?');
        var format = (options.GetValueOrDefault("format") ?? (options.ContainsKey("out") ? "csv" : "table")).ToLowerInvariant();
        using var client = Client(settings, options);
        var escaped = Uri.EscapeDataString(key);
        if (format is "csv" or "xlsx" or "pdf")
        {
            using var file = await client.GetAsync($"/api/reports/{escaped}/export?format={format}{(query is { Length: > 0 } ? "&" + query : "")}", ct);
            return await SaveAsync(file, options, ct);
        }

        if (format is not ("table" or "json"))
            return await Mistake("Format is table, json, csv, xlsx or pdf.");
        using var response = await client.GetAsync($"/api/reports/{escaped}/table{(query is { Length: > 0 } ? "?" + query : "")}", ct);
        var body = await Checked(response, ct);
        await output.WriteAsync(format == "json" ? Pretty(body) + Environment.NewLine : RenderReport(body));
        return 0;
    }

    private async Task<int> ImportAsync(CliSettings settings, string source, string file, Dictionary<string, string> options, CancellationToken ct)
    {
        if (!File.Exists(file))
            return await Mistake($"No file at {file}.");
        using var client = Client(settings, options);
        using var content = new ByteArrayContent(await File.ReadAllBytesAsync(file, ct));
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        var dryRun = options.ContainsKey("dry-run") ? "?dryRun=true" : "";
        using var response = await client.PostAsync($"/api/import/migrate/{Uri.EscapeDataString(source)}{dryRun}", content, ct);
        await output.WriteLineAsync(Pretty(await Checked(response, ct)));
        return 0;
    }

    private async Task<int> DownloadAsync(CliSettings settings, Dictionary<string, string> options, string path, CancellationToken ct)
    {
        if (!options.ContainsKey("out"))
            return await Mistake("Say where to save it: --out FILE");
        using var client = Client(settings, options);
        // A backup can be larger than memory: it goes to the file as it arrives.
        using var response = await client.GetAsync(path, HttpCompletionOption.ResponseHeadersRead, ct);
        return await SaveAsync(response, options, ct);
    }

    private async Task<int> RawAsync(CliSettings settings, string method, string path, Dictionary<string, string> options, CancellationToken ct)
    {
        if (!path.StartsWith('/'))
            path = "/" + path;
        if (!path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase))
            path = "/api" + path;
        using var client = Client(settings, options);
        using var request = new HttpRequestMessage(new HttpMethod(method), path);
        if (options.GetValueOrDefault("data") is { } data)
        {
            var json = data.StartsWith('@') ? await File.ReadAllTextAsync(data[1..], ct) : data;
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        }

        using var response = await client.SendAsync(request, ct);
        if (options.ContainsKey("out"))
            return await SaveAsync(response, options, ct);
        var body = await Checked(response, ct);
        if (body.Length > 0)
            await output.WriteLineAsync(Pretty(body));
        return 0;
    }

    private async Task<int> SaveAsync(HttpResponseMessage response, Dictionary<string, string> options, CancellationToken ct)
    {
        if (!response.IsSuccessStatusCode)
            await Checked(response, ct);
        var name = options.GetValueOrDefault("out") ?? response.Content.Headers.ContentDisposition?.FileNameStar ?? response.Content.Headers.ContentDisposition?.FileName?.Trim('"') ?? "download";
        // Under another name until it's all here, so a stopped download can't pass for a whole backup.
        var partial = name + ".part";
        await using (var file = File.Create(partial))
            await response.Content.CopyToAsync(file, ct);
        File.Move(partial, name, overwrite: true);
        await output.WriteLineAsync($"Saved {Path.GetFullPath(name)} ({new FileInfo(name).Length:N0} bytes).");
        return 0;
    }

    private HttpClient Client(CliSettings settings, Dictionary<string, string> options)
    {
        if (settings.Url is null && clientFactory is null)
            throw new CliException("No server set. Run `openbook config --url https://… --key obk_…` (or set OPENBOOK_URL).", 2);
        if (settings.ApiKey is null)
            throw new CliException("No API key set. Make one under Settings › API keys, then `openbook config --key obk_…` (or set OPENBOOK_API_KEY).", 2);
        var client = clientFactory?.Invoke(settings) ?? new HttpClient { BaseAddress = new Uri(settings.Url!), Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", settings.ApiKey);
        if (options.GetValueOrDefault("closing-password") is { } password)
            client.DefaultRequestHeaders.Add("X-Closing-Password", password);
        // Running the same command again with the same key gets the first answer instead of doing it twice.
        if (options.GetValueOrDefault("idempotency-key") is { } key)
            client.DefaultRequestHeaders.Add("Idempotency-Key", key);
        return client;
    }

    /// <summary>The body of a successful response; otherwise the server's message as a <see cref="CliException"/>.</summary>
    private static async Task<string> Checked(HttpResponseMessage response, CancellationToken ct)
    {
        var body = await response.Content.ReadAsStringAsync(ct);
        if (response.IsSuccessStatusCode)
            return body;
        string? message = null;
        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind == JsonValueKind.Object && document.RootElement.TryGetProperty("message", out var m))
                message = m.GetString();
        }
        catch (JsonException)
        {
            // Not JSON: fall back to the status.
        }

        throw new CliException($"{(int)response.StatusCode} {response.ReasonPhrase}: {message ?? (body.Length is > 0 and < 300 ? body : "the server refused the request.")}", 1);
    }

    private async Task<int> Mistake(string message)
    {
        await error.WriteLineAsync(message);
        return 2;
    }

    /// <summary>A report table as aligned text: the title block, then the rows with indented labels.</summary>
    private static string RenderReport(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var text = new StringBuilder();
        text.AppendLine(root.GetProperty("title").GetString());
        foreach (var subtitle in root.GetProperty("subtitles").EnumerateArray())
            text.AppendLine(subtitle.GetString());
        text.AppendLine();
        var columns = root.GetProperty("columns").EnumerateArray().Select(c => c.GetProperty("header").GetString() ?? "").ToList();
        var rows = root.GetProperty("rows").EnumerateArray().Select(r =>
        {
            var depth = r.TryGetProperty("depth", out var d) ? d.GetInt32() : 0;
            return r.GetProperty("cells").EnumerateArray()
                .Select((c, i) => (i == 0 ? new string(' ', 2 * depth) : "") + (c.TryGetProperty("value", out var v) ? Text(v) : ""))
                .ToList();
        }).ToList();
        text.Append(TextTable.Render(columns, rows));
        return text.ToString();
    }

    private static string Text(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Null or JsonValueKind.Undefined => "",
        JsonValueKind.String => value.GetString() ?? "",
        JsonValueKind.Number when value.TryGetDecimal(out var d) => d.ToString("#,0.00##", CultureInfo.InvariantCulture),
        JsonValueKind.True => "yes",
        JsonValueKind.False => "no",
        _ => value.GetRawText(),
    };

    private static string Pretty(string json)
    {
        if (json.Length == 0)
            return json;
        try
        {
            using var document = JsonDocument.Parse(json);
            return JsonSerializer.Serialize(document.RootElement, PrettyJson);
        }
        catch (JsonException)
        {
            return json;
        }
    }

    private static readonly JsonSerializerOptions PrettyJson = new() { WriteIndented = true };

    /// <summary>Words, and <c>--name value</c> / <c>--name=value</c> options (<see cref="Flags"/> take no value).</summary>
    public static (List<string> Words, Dictionary<string, string> Options) Parse(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        var words = new List<string>();
        var options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];
            if (!arg.StartsWith("--", StringComparison.Ordinal) || arg.Length == 2)
            {
                words.Add(arg);
                continue;
            }

            var name = arg[2..];
            var equals = name.IndexOf('=', StringComparison.Ordinal);
            if (equals > 0)
                options[name[..equals]] = name[(equals + 1)..];
            else if (Flags.Contains(name) || i + 1 >= args.Count)
                options[name] = "true";
            else
                options[name] = args[++i];
        }

        return (words, options);
    }
}

public sealed class CliException(string message, int exitCode) : Exception(message)
{
    public int ExitCode { get; } = exitCode;
}

/// <summary>Plain text columns: numbers right-aligned, everything else left.</summary>
public static class TextTable
{
    public static string Render(IReadOnlyList<string> headers, IReadOnlyList<IReadOnlyList<string>> rows)
    {
        ArgumentNullException.ThrowIfNull(headers);
        ArgumentNullException.ThrowIfNull(rows);
        var count = Math.Max(headers.Count, rows.Count == 0 ? 0 : rows.Max(r => r.Count));
        string Cell(IReadOnlyList<string> row, int i) => i < row.Count ? row[i] : "";
        var widths = Enumerable.Range(0, count)
            .Select(i => Math.Min(60, Math.Max(Cell(headers, i).Length, rows.Count == 0 ? 0 : rows.Max(r => Cell(r, i).Length))))
            .ToList();
        var numeric = Enumerable.Range(0, count)
            .Select(i => rows.Any(r => Cell(r, i).Length > 0) && rows.All(r => Cell(r, i).Length == 0 || decimal.TryParse(Cell(r, i), NumberStyles.Number, CultureInfo.InvariantCulture, out _)))
            .ToList();
        var text = new StringBuilder();
        void Line(IReadOnlyList<string> row)
        {
            var cells = Enumerable.Range(0, count).Select(i =>
            {
                var value = Cell(row, i);
                if (value.Length > widths[i])
                    value = value[..(widths[i] - 1)] + "…";
                return numeric[i] ? value.PadLeft(widths[i]) : value.PadRight(widths[i]);
            });
            text.AppendLine(string.Join("  ", cells).TrimEnd());
        }

        Line(headers);
        text.AppendLine(string.Join("  ", widths.Select(w => new string('-', w))));
        foreach (var row in rows)
            Line(row);
        return text.ToString();
    }
}
