using System.Reflection;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.Web.HtmlRendering;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using OpenBook.Web.Components;

namespace OpenBook.Integration.Tests;

/// <summary>
/// Components that wait, as they do on PostgreSQL. SQLite's calls finish without waiting, so a run on SQLite can't
/// show components overlapping or drawing before their data is in; these tests wait for real on either database.
/// </summary>
public sealed class OrderedComponentTests
{
    private sealed class Tracker
    {
        public int Running { get; private set; }
        public int MostAtOnce { get; private set; }
        public List<string> Steps { get; } = [];

        public async Task LoadAsync(string name)
        {
            MostAtOnce = Math.Max(MostAtOnce, ++Running);
            Steps.Add($"{name} loading");
            await Task.Delay(20);
            Steps.Add($"{name} loaded");
            Running--;
        }
    }

    private abstract class Part : OrderedComponent
    {
        [Inject] protected Tracker Tracker { get; set; } = null!;
    }

    private sealed class Sibling : Part
    {
        private string? _data;

        [Parameter] public string Name { get; set; } = "";

        protected override async Task OnParametersSetAsync()
        {
            await Tracker.LoadAsync(Name);
            _data = Name;
        }

        protected override void BuildRenderTree(RenderTreeBuilder builder) => builder.AddContent(0, _data!.ToUpperInvariant());
    }

    private sealed class Parent : Part
    {
        private string? _data;

        protected override async Task OnInitializedAsync()
        {
            await Tracker.LoadAsync("parent");
            _data = "parent";
        }

        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            builder.AddContent(0, _data!.ToUpperInvariant());
            builder.OpenComponent<Sibling>(1);
            builder.AddComponentParameter(2, nameof(Sibling.Name), "first");
            builder.CloseComponent();
            builder.OpenComponent<Sibling>(3);
            builder.AddComponentParameter(4, nameof(Sibling.Name), "second");
            builder.CloseComponent();
        }
    }

    [Fact]
    public async Task A_static_page_loads_one_component_at_a_time_and_draws_after_its_data_is_in()
    {
        var tracker = new Tracker();
        await using var services = new ServiceCollection()
            .AddSingleton(tracker)
            .AddScoped<RenderTurn>()
            .AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance)
            .BuildServiceProvider();
        await using var scope = services.CreateAsyncScope();
        await using var renderer = new HtmlRenderer(scope.ServiceProvider, NullLoggerFactory.Instance);

        var html = await renderer.Dispatcher.InvokeAsync(async () => (await renderer.RenderComponentAsync<Parent>()).ToHtmlString());

        Assert.Equal("PARENTFIRSTSECOND", html);
        Assert.Equal(1, tracker.MostAtOnce);
        Assert.Equal(
            ["parent loading", "parent loaded", "first loading", "first loaded", "second loading", "second loaded"],
            tracker.Steps);
    }

    [Fact]
    public void Every_component_of_the_app_is_ordered()
    {
        var app = typeof(OrderedComponent).Assembly;
        var others = app.GetTypes()
            .Where(t => t.IsAssignableTo(typeof(ComponentBase)) && !t.IsAssignableTo(typeof(OrderedComponent)))
            .Where(t => t.GetCustomAttribute<System.Runtime.CompilerServices.CompilerGeneratedAttribute>() is null)
            .Select(t => t.FullName)
            .ToList();
        Assert.Empty(others);
    }
}
