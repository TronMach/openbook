using System.Reflection;
using OpenBook.Domain.Common;

namespace OpenBook.Domain.Tests;

public class ArchitectureTests
{
    [Fact]
    public void Domain_has_no_floating_point_state()
    {
        Type[] floating = [typeof(double), typeof(float), typeof(Half), typeof(double?), typeof(float?), typeof(Half?)];
        const BindingFlags all = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

        var offenders = typeof(Money).Assembly.GetTypes()
            .SelectMany(t => t.GetFields(all).Select(f => (Type: t, Member: f.Name, f.FieldType))
                .Concat(t.GetProperties(all).Select(p => (Type: t, Member: p.Name, FieldType: p.PropertyType))))
            .Where(m => floating.Contains(m.FieldType))
            .Select(m => $"{m.Type.FullName}.{m.Member}")
            .ToList();

        Assert.Empty(offenders);
    }
}
