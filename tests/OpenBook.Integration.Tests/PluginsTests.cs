using System.Text;
using Microsoft.Extensions.DependencyInjection;
using OpenBook.Application.Import.Migration;
using OpenBook.Application.Plugins;
using OpenBook.Application.Reporting;
using OpenBook.Application.Sales;
using OpenBook.Domain.Common;
using OpenBook.Domain.Sales;
using OpenBook.Web.Plugins;

namespace OpenBook.Integration.Tests;

/// <summary>An add-in with a report and an import format, as a third party would write one.</summary>
public sealed class SamplePlugin : IOpenBookPlugin
{
    public string Name => "Sample";
    public string Version => "1.2.0";
    public string? Description => "A report and an import format.";

    public void Register(IPluginRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        registry.AddReport(
            new ReportDefinition("customer-count", "Customer count", "How many customers there are.", "Add-ins", ReportInputs.None),
            async (services, p, ct) =>
            {
                var customers = await services.GetRequiredService<CustomerService>().ListAsync(DateOnly.FromDateTime(DateTime.Today), ct: ct);
                return new ReportTable("Customer count", [], [new("Measure"), new("Count", ColumnFormat.Number)],
                    [new ReportTableRow(["Customers", new ReportCell((decimal)customers.Count)])]);
            });
        // One customer per line.
        registry.AddImporter("names-list", "A list of names", "One customer name per line.", ".txt", (bytes, _) =>
        {
            var data = new MigrationData();
            var lines = Encoding.UTF8.GetString(bytes).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            data.Customers.AddRange(lines.Select((name, i) => new SourceCustomer(i + 1, new CustomerDetails(name))));
            return data;
        });
    }
}

/// <summary>Adds a report, then fails: nothing it added may stay.</summary>
public sealed class FailingPlugin : IOpenBookPlugin
{
    public string Name => "Failing";
    public string Version => "0.1";

    public void Register(IPluginRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        registry.AddReport(new ReportDefinition("half-done", "Half done", "", "Add-ins", ReportInputs.None),
            (_, _, _) => Task.FromResult(new ReportTable("x", [], [], [])));
        throw new InvalidOperationException("Something broke.");
    }
}

/// <summary>Tries to take a built-in report's key.</summary>
public sealed class CollidingPlugin : IOpenBookPlugin
{
    public string Name => "Colliding";
    public string Version => "0.1";

    public void Register(IPluginRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        registry.AddReport(new ReportDefinition("trial-balance", "Mine", "", "Add-ins", ReportInputs.None),
            (_, _, _) => Task.FromResult(new ReportTable("x", [], [], [])));
    }
}

/// <summary>L9 slice 2: add-ins (reports and import formats), loading from the plugins folder, safe mode.</summary>
public sealed class PluginsTests : IAsyncLifetime
{
    private readonly LedgerDatabase _db = new();

    public Task InitializeAsync() => _db.InitializeAsync();

    public Task DisposeAsync() => _db.DisposeAsync();

    private Task<T> In<TService, T>(Func<TService, Task<T>> work) where TService : notnull =>
        _db.InScope(sp => work(sp.GetRequiredService<TService>()));

    [Fact]
    public async Task An_add_in_report_and_import_format_work_like_built_in_ones()
    {
        var registry = await _db.InScope(sp => Task.FromResult(sp.GetRequiredService<PluginRegistry>()));
        Assert.Equal(PluginState.Loaded, registry.Load("sample", new SamplePlugin()).State);

        Assert.NotNull(await _db.InScope(sp => Task.FromResult(sp.GetRequiredService<ReportCatalog>().Find("customer-count"))));
        Assert.Contains(await _db.InScope(sp => Task.FromResult(sp.GetRequiredService<ReportCatalog>().All)), d => d.Key == "customer-count");

        var preview = await In<MigrationImportService, MigrationResult>(s => s.PreviewAsync("names-list", Encoding.UTF8.GetBytes("Acme\nGlobex\n")));
        Assert.Equal(2, preview.Count(MigrationKind.Customer, MigrationRowStatus.New));
        await In<MigrationImportService, MigrationResult>(s => s.ImportAsync("names-list", Encoding.UTF8.GetBytes("Acme\nGlobex\n")));

        var table = await _db.InScope(sp => sp.GetRequiredService<ReportCatalog>().RunAsync("customer-count", ReportParameters.Parse("")));
        Assert.Equal(2m, table.Find("Customers"));
        var saved = await In<SavedReportService, Domain.Reporting.SavedReport>(s => s.SaveAsync("How many", "customer-count", null));
        Assert.Equal("customer-count", saved.ReportKey);

        Assert.Contains(await In<MigrationImportService, IReadOnlyList<(string Key, string Label)>>(s => Task.FromResult(s.Sources())), x => x.Key == "names-list");
        Assert.Equal("import.source_unknown", (await Assert.ThrowsAsync<DomainException>(() =>
            In<MigrationImportService, MigrationResult>(s => s.PreviewAsync("no-such-format", [1])))).Code);
    }

    [Fact]
    public void A_failing_or_colliding_add_in_leaves_nothing_behind()
    {
        var registry = new PluginRegistry();
        var failed = registry.Load("failing", new FailingPlugin());
        Assert.Equal((PluginState.Failed, "Something broke."), (failed.State, failed.Error));
        Assert.Null(registry.Report("half-done"));
        Assert.Equal(PluginState.Failed, registry.Load("colliding", new CollidingPlugin()).State);
        Assert.Empty(registry.Reports);
    }

    [Fact]
    public void The_loader_reads_add_in_folders_honours_disabled_ones_and_safe_mode()
    {
        var root = Path.Combine(Path.GetTempPath(), $"openbook-plugins-{Guid.NewGuid():N}");
        try
        {
            var dll = typeof(SamplePlugin).Assembly.Location;
            foreach (var name in new[] { "sample", "off" })
            {
                Directory.CreateDirectory(Path.Combine(root, name));
                File.Copy(dll, Path.Combine(root, name, Path.GetFileName(dll)));
            }

            Directory.CreateDirectory(Path.Combine(root, "empty"));
            File.WriteAllText(Path.Combine(root, "off", PluginLoader.DisabledMarker), "");

            var registry = PluginLoader.Load(root, safeMode: false);
            Assert.Equal(PluginState.Disabled, registry.Plugins.Single(p => p.Folder == "off").State);
            Assert.Equal(PluginState.Failed, registry.Plugins.Single(p => p.Folder == "empty").State);
            var sample = registry.Plugins.Where(p => p.Folder == "sample").ToList();
            Assert.Contains(sample, p => p is { Name: "Sample", State: PluginState.Loaded });
            Assert.Contains(sample, p => p is { Name: "Failing", State: PluginState.Failed });
            Assert.NotNull(registry.Report("customer-count"));
            Assert.NotNull(registry.Importer("names-list"));

            var safe = PluginLoader.Load(root, safeMode: true);
            Assert.True(safe.SafeMode);
            Assert.Empty(safe.Plugins);
        }
        finally
        {
            // A loaded add-in's DLL stays locked on Windows until the process ends; the temp folder is left then.
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }
}
