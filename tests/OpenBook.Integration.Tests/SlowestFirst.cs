using System.Reflection;
using System.Text.Json;
using Xunit;
using Xunit.Abstractions;

[assembly: TestCollectionOrderer("OpenBook.Integration.Tests.SlowestFirst", "OpenBook.Integration.Tests")]

namespace OpenBook.Integration.Tests;

/// <summary>
/// Starts the test collections that took longest last time first, so a run doesn't end with one long class (the
/// accessibility crawl, the page smoke tests) running on its own while every other thread waits. The times come from
/// the file <c>OPENBOOK_TEST_TIMES</c> names (seconds per test class, by full name), which <c>tools/run-tests.ps1</c>
/// keeps up to date after each run; without one, or for a class it doesn't know, xUnit's own order stands.
/// </summary>
public sealed class SlowestFirst : ITestCollectionOrderer
{
    public IEnumerable<ITestCollection> OrderTestCollections(IEnumerable<ITestCollection> testCollections)
    {
        var collections = testCollections.ToList();
        var times = ReadTimes();
        if (times.Count == 0)
            return collections;

        // A collection is one class, or every class that names it with [Collection("…")].
        var named = typeof(SlowestFirst).Assembly.GetTypes()
            .Select(type => (type, name: type.CustomAttributes
                .FirstOrDefault(a => a.AttributeType == typeof(CollectionAttribute))?.ConstructorArguments[0].Value as string))
            .Where(x => x.name is not null && x.type.FullName is not null)
            .GroupBy(x => x.name!)
            .ToDictionary(g => g.Key, g => g.Select(x => x.type.FullName!).ToList());

        double Seconds(ITestCollection collection)
        {
            const string prefix = "Test collection for ";
            var classes = collection.DisplayName.StartsWith(prefix, StringComparison.Ordinal)
                ? [collection.DisplayName[prefix.Length..]]
                : named.GetValueOrDefault(collection.DisplayName) ?? [];
            return classes.Sum(c => times.GetValueOrDefault(c));
        }

        // OrderByDescending is stable, so collections without times keep xUnit's order after the timed ones.
        return collections.OrderByDescending(Seconds).ToList();
    }

    private static Dictionary<string, double> ReadTimes()
    {
        var path = Environment.GetEnvironmentVariable("OPENBOOK_TEST_TIMES");
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return [];
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, double>>(File.ReadAllText(path)) ?? [];
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            return [];
        }
    }
}
