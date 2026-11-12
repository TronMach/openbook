using OpenBook.Application.Plugins;
using OpenBook.Application.Reporting;

namespace OpenBook.Integration.Tests;

/// <summary>Written for an add-in API this OpenBook doesn't have yet; its registration must never run.</summary>
public sealed class FuturePlugin : IOpenBookPlugin
{
    public string Name => "From the future";
    public string Version => "9.0";
    public int ApiVersion => PluginApi.Version + 1;

    public void Register(IPluginRegistry registry) => throw new InvalidOperationException("Registration ran.");
}

/// <summary>Written for an add-in API older than any this OpenBook loads.</summary>
public sealed class AncientPlugin : IOpenBookPlugin
{
    public string Name => "Ancient";
    public string Version => "0.0.1";
    public int ApiVersion => PluginApi.Oldest - 1;

    public void Register(IPluginRegistry registry) => throw new InvalidOperationException("Registration ran.");
}

/// <summary>Built against another OpenBook without saying so: it calls what isn't here.</summary>
public sealed class MismatchedPlugin : IOpenBookPlugin
{
    public string Name => "Mismatched";
    public string Version => "2.0";

    public void Register(IPluginRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        registry.AddReport(new ReportDefinition("mismatched-report", "Mismatched", "", "Add-ins", ReportInputs.None),
            (_, _, _) => Task.FromResult(new ReportTable("x", [], [], [])));
        throw new MissingMethodException("Method not found: 'Void OpenBook.Application.Plugins.IPluginRegistry.AddPaymentProvider()'.");
    }
}

/// <summary>The add-in API version: add-ins for a contract OpenBook can't run aren't loaded, and say why. No database.</summary>
public sealed class PluginApiTests
{
    [Fact]
    public void An_add_in_that_says_nothing_was_written_for_the_first_version_and_loads()
    {
        var info = new PluginRegistry().Load("sample", new SamplePlugin());
        Assert.Equal((PluginState.Loaded, 1), (info.State, info.ApiVersion));
    }

    [Fact]
    public void An_add_in_for_a_newer_OpenBook_is_not_loaded_and_says_to_update()
    {
        var registry = new PluginRegistry();
        var info = registry.Load("future", new FuturePlugin());
        Assert.Equal(PluginState.Failed, info.State);
        Assert.Contains("needs a newer OpenBook", info.Error, StringComparison.Ordinal);
        Assert.Equal(PluginApi.Version + 1, info.ApiVersion);
        Assert.Equal("From the future", info.Name);
    }

    [Fact]
    public void An_add_in_for_an_API_no_longer_supported_is_not_loaded()
    {
        var info = new PluginRegistry().Load("ancient", new AncientPlugin());
        Assert.Equal(PluginState.Failed, info.State);
        Assert.Contains("no longer supports", info.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void An_add_in_built_for_another_OpenBook_says_so_and_leaves_nothing_behind()
    {
        var registry = new PluginRegistry();
        var info = registry.Load("mismatched", new MismatchedPlugin());
        Assert.Equal(PluginState.Failed, info.State);
        Assert.StartsWith("It was built for a different version of OpenBook", info.Error, StringComparison.Ordinal);
        Assert.Null(registry.Report("mismatched-report"));
    }

    [Theory]
    [InlineData(PluginApi.Oldest, true)]
    [InlineData(PluginApi.Version + 1, false)]
    [InlineData(PluginApi.Oldest - 1, false)]
    public void The_versions_OpenBook_loads(int apiVersion, bool loads) =>
        Assert.Equal(loads, PluginApi.Refusal(apiVersion) is null);

    [Fact]
    public void Other_errors_keep_their_own_message() =>
        Assert.Equal("Something broke.", PluginApi.Explain(new InvalidOperationException("Something broke.")));
}
