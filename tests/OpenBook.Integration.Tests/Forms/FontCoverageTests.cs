using Microsoft.Extensions.DependencyInjection;
using OpenBook.Application.Forms;
using OpenBook.Domain.Common;
using OpenBook.Infrastructure;
using OpenBook.Integration.Tests.Fuzz;

namespace OpenBook.Integration.Tests.Forms;

/// <summary>Which characters a font has (C346), read from shipped fonts and hand-made ones, and the warning beside Print. No database.</summary>
public sealed class FontCoverageTests
{
    private static readonly IServiceProvider Services = new ServiceCollection()
        .AddOpenBookInfrastructure("Data Source=:memory:", backupDirectory: Path.GetTempPath())
        .BuildServiceProvider();

    private static FormFontFace Shipped(string key)
    {
        var fonts = Services.GetRequiredService<IBundledFonts>();
        var files = fonts.Load(key) ?? throw new InvalidOperationException($"The font files for {key} are missing beside the tests.");
        return new FormFontFace(key, fonts.List().Single(f => f.Key == key).Family, files.Regular, files.Bold);
    }

    private static SalesForm Invoice(string customer, FormFontFace? font, FormFontFace? companyFont = null, string company = "Northwind Studio") =>
        new("INVOICE", "1001", new FormParty(company, "48 Harbour Lane"), new FormParty(customer, "100 Main Street"), [("Date", "3/2/2026")],
            [new FormLine("Consulting, March", 2m, 95m, 190m)], [], null, "USD",
            new FormStyle(default, "#336699", null, Font: font, CompanyFont: companyFont));

    [Fact]
    public void A_shipped_font_is_read_and_lacks_what_it_lacks()
    {
        var inter = FontCoverage.Read(Shipped("inter").Regular);
        Assert.True(inter.Has('A'));
        Assert.True(inter.Has('é'));
        Assert.True(inter.Has('€'));
        Assert.False(inter.Has('山'));
        Assert.Equal(["山", "田", "商", "店"], inter.Missing(["Café 山田商店", "山 again"]));
        Assert.Empty(inter.Missing(["Crème brûlée, 12 × €4.50 — thanks!", null, ""]));
    }

    [Fact]
    public void Hand_made_fonts_of_either_format_are_read_and_damaged_ones_refused()
    {
        var basic = FontCoverage.Read(Seeds.MinimalFont(4));
        Assert.True(basic.Has('A') && basic.Has('Z'));
        Assert.False(basic.Has('a'));
        Assert.False(basic.Has(0x1F600));
        var full = FontCoverage.Read(Seeds.MinimalFont(12));
        Assert.True(full.Has('M'));
        Assert.True(full.Has(0x1F600));
        Assert.False(full.Has(0x1F601));
        Assert.Equal(["a", "😁"], full.Missing(["AaA 😀😁"]));

        Assert.Equal("font.unreadable", Assert.Throws<DomainException>(() => FontCoverage.Read([0, 1, 0, 0])).Code);
        var truncated = Seeds.MinimalFont(4)[..40];
        Assert.Equal("font.unreadable", Assert.Throws<DomainException>(() => FontCoverage.Read(truncated)).Code);
        var noCmap = Seeds.MinimalFont(12);
        noCmap[12] = (byte)'x';
        Assert.Equal("font.unreadable", Assert.Throws<DomainException>(() => FontCoverage.Read(noCmap)).Code);
    }

    [Fact]
    public void The_print_warning_names_what_the_forms_fonts_lack()
    {
        var inter = Shipped("inter");
        Assert.Null(FormScripts.Warning(Invoice("Bo's Bakery", inter), builtInFont: null));

        var warning = FormScripts.Warning(Invoice("山田商店", inter), builtInFont: null);
        Assert.NotNull(warning);
        Assert.StartsWith("Inter has no 山 田 商 店.", warning, StringComparison.Ordinal);

        // The company's lines print in the company font; with none, a design without a font uses the built-in one.
        var roboto = Shipped("roboto");
        Assert.Contains("Roboto has no 東 京", FormScripts.Warning(Invoice("Bo's Bakery", inter, roboto, company: "東京 Studio"), null), StringComparison.Ordinal);
        Assert.Contains("OpenBook Sans has no 山", FormScripts.Warning(Invoice("山田", font: null), builtInFont: inter.Regular), StringComparison.Ordinal);

        Assert.Equal(FormScripts.RightToLeftWarning, FormScripts.Warning(Invoice("شركة النور", inter), null));
        // No font to read: the guess from the characters used.
        Assert.Contains("beyond the Latin alphabet", FormScripts.Warning(Invoice("山田", font: null), builtInFont: null), StringComparison.Ordinal);
    }
}
