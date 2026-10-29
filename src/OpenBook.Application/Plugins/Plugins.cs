using OpenBook.Application.Import.Migration;
using OpenBook.Application.Reporting;
using OpenBook.Domain.Common;

namespace OpenBook.Application.Plugins;

/// <summary>
/// An add-in: a .NET assembly in its own folder under the plugins folder, with one public class implementing this.
/// It runs inside OpenBook with full trust (so only an admin installs one) and adds to the slots offered by
/// <see cref="IPluginRegistry"/>. It reads through the same services the pages use and can change things only
/// through them, so the ledger rules, permissions and audit trail still apply.
/// </summary>
public interface IOpenBookPlugin
{
    string Name { get; }
    string Version { get; }
    string? Description => null;

    /// <summary>Called once at startup; anything it adds is removed again if it throws.</summary>
    void Register(IPluginRegistry registry);
}

/// <summary>The slots an add-in can fill.</summary>
public interface IPluginRegistry
{
    /// <summary>
    /// A report: listed with the others, with its own page, exports, saving and scheduled email. The key must not be
    /// a built-in report's. <paramref name="run"/> gets a scoped service provider (read through the report ledger or
    /// the services, so it agrees with the other reports) and the report's query string.
    /// </summary>
    void AddReport(ReportDefinition definition, Func<IServiceProvider, ReportParameters, CancellationToken, Task<ReportTable>> run);

    /// <summary>
    /// A format to move in from: <paramref name="read"/> turns the uploaded file (and the company's home currency) into
    /// <see cref="MigrationData"/>, which is previewed and imported like the built-in formats, all or nothing.
    /// </summary>
    void AddImporter(string key, string label, string help, string accept, Func<byte[], string, MigrationData> read);
}

public enum PluginState
{
    Loaded,
    Disabled,
    Failed,
}

public sealed record PluginInfo(string Folder, string? Name, string? Version, string? Description, PluginState State, string? Error, IReadOnlyList<string> Adds);

public sealed record PluginReport(string Plugin, ReportDefinition Definition, Func<IServiceProvider, ReportParameters, CancellationToken, Task<ReportTable>> Run);

public sealed record PluginImporter(string Plugin, string Key, string Label, string Help, string Accept, Func<byte[], string, MigrationData> Read);

/// <summary>What the add-ins loaded at startup contribute (a singleton; empty when there are none or in safe mode).</summary>
public sealed class PluginRegistry
{
    private readonly List<PluginInfo> _plugins = [];
    private readonly List<PluginReport> _reports = [];
    private readonly List<PluginImporter> _importers = [];

    /// <summary>True when OpenBook was started without add-ins (OpenBook:Plugins:SafeMode or --safe-mode).</summary>
    public bool SafeMode { get; init; }

    public string? Folder { get; init; }
    public IReadOnlyList<PluginInfo> Plugins => _plugins;
    public IReadOnlyList<PluginReport> Reports => _reports;
    public IReadOnlyList<PluginImporter> Importers => _importers;

    public PluginReport? Report(string? key) => _reports.FirstOrDefault(r => string.Equals(r.Definition.Key, key, StringComparison.OrdinalIgnoreCase));

    public PluginImporter? Importer(string? key) => _importers.FirstOrDefault(i => string.Equals(i.Key, key, StringComparison.OrdinalIgnoreCase));

    /// <summary>Records a folder that wasn't loaded (disabled, or it failed before registering).</summary>
    public void Skip(string folder, PluginState state, string? error = null, string? name = null, string? version = null) =>
        _plugins.Add(new PluginInfo(folder, name, version, null, state, error, []));

    /// <summary>Runs a plugin's registration; its additions are kept only if it finishes without throwing.</summary>
    public PluginInfo Load(string folder, IOpenBookPlugin plugin)
    {
        ArgumentNullException.ThrowIfNull(plugin);
        var pending = new Pending(this, plugin.Name);
        PluginInfo info;
        try
        {
            plugin.Register(pending);
            _reports.AddRange(pending.Reports);
            _importers.AddRange(pending.Importers);
            info = new PluginInfo(folder, plugin.Name, plugin.Version, plugin.Description, PluginState.Loaded, null,
                [.. pending.Reports.Select(r => $"Report: {r.Definition.Title}"), .. pending.Importers.Select(i => $"Import format: {i.Label}")]);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            info = new PluginInfo(folder, plugin.Name, plugin.Version, plugin.Description, PluginState.Failed, ex.Message, []);
        }

        _plugins.Add(info);
        return info;
    }

    private sealed class Pending(PluginRegistry registry, string plugin) : IPluginRegistry
    {
        public List<PluginReport> Reports { get; } = [];
        public List<PluginImporter> Importers { get; } = [];

        public void AddReport(ReportDefinition definition, Func<IServiceProvider, ReportParameters, CancellationToken, Task<ReportTable>> run)
        {
            ArgumentNullException.ThrowIfNull(definition);
            ArgumentNullException.ThrowIfNull(run);
            if (!IsKey(definition.Key))
                throw new DomainException("plugin.key_invalid", $"'{definition.Key}' isn't a valid report key (lowercase letters, digits and dashes).");
            if (ReportCatalog.FindBuiltIn(definition.Key) is not null || registry.Report(definition.Key) is not null || Reports.Any(r => r.Definition.Key == definition.Key))
                throw new DomainException("plugin.key_taken", $"There's already a report '{definition.Key}'.");
            Reports.Add(new PluginReport(plugin, definition, run));
        }

        public void AddImporter(string key, string label, string help, string accept, Func<byte[], string, MigrationData> read)
        {
            ArgumentNullException.ThrowIfNull(read);
            if (!IsKey(key))
                throw new DomainException("plugin.key_invalid", $"'{key}' isn't a valid import key (lowercase letters, digits and dashes).");
            if (Enum.TryParse<MigrationSource>(key, ignoreCase: true, out _) || registry.Importer(key) is not null || Importers.Any(i => i.Key == key))
                throw new DomainException("plugin.key_taken", $"There's already an import format '{key}'.");
            Importers.Add(new PluginImporter(plugin, key, label, help, accept, read));
        }

        private static bool IsKey(string? key) => key is { Length: > 0 and <= 60 } && key.All(c => c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-');
    }
}
