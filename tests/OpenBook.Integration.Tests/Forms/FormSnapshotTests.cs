using System.Globalization;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using OpenBook.Application.Forms;
using OpenBook.Domain.Companies;
using OpenBook.Infrastructure;

namespace OpenBook.Integration.Tests.Forms;

/// <summary>
/// Printed forms as they come out of the renderer, compared with saved copies (<c>Forms/Snapshots</c>): every ready-made
/// design prints a sample invoice, and a few print the other documents, a long invoice over several pages and a
/// European region. Each page's drawing instructions are kept with their text decoded (<see cref="PdfSnapshot"/>), so a
/// change to the renderer, a font or a design shows up as a readable difference. No database.
/// Only shipped fonts are used: the built-in sans-serif is whatever the machine has (Segoe UI on Windows), which differs
/// between Windows versions.
/// When a change is meant, set OPENBOOK_UPDATE_SNAPSHOTS=1 and run these tests to save the new copies, then look at the
/// PDFs in artifacts/form-snapshots and the snapshot diff before committing. A failing run saves its PDF and text there too.
/// </summary>
public sealed class FormSnapshotTests
{
    private static readonly DateOnly Date = new(2026, 3, 2);
    private static readonly IServiceProvider Services = new ServiceCollection()
        .AddOpenBookInfrastructure("Data Source=:memory:", backupDirectory: Path.GetTempPath())
        .BuildServiceProvider();

    private static IFormRenderer Renderer => Services.GetRequiredService<IFormRenderer>();

    public static TheoryData<string> Presets() => [.. FormDesignPresets.All.Select(p => p.Key)];

    [Theory]
    [MemberData(nameof(Presets))]
    public void Every_ready_made_design_prints_an_invoice_as_saved(string preset) =>
        Matches($"{preset}-invoice", Sales(preset, FormDocuments.Invoice, SampleLines()));

    [Theory]
    [InlineData("bold", FormDocuments.Estimate)]
    [InlineData("professional", FormDocuments.Estimate)]
    [InlineData("service", FormDocuments.CreditMemo)]
    [InlineData("packing-slip", FormDocuments.SalesReceipt)]
    [InlineData("product", FormDocuments.PurchaseOrder)]
    public void Other_documents_print_as_saved(string preset, FormDocuments document) =>
        Matches($"{preset}-{Name(document)}", Sales(preset, document, SampleLines()));

    [Theory]
    [InlineData("minimal")]
    [InlineData("bold")]
    [InlineData("detailed")]
    public void Statements_print_as_saved(string preset) =>
        Matches($"{preset}-statement", Statement(preset));

    [Fact]
    public void A_long_invoice_runs_over_pages_with_numbers_as_saved()
    {
        var lines = Enumerable.Range(1, 70).Select(i => Line(i, $"Part {i:000}, machined to drawing", i % 7 + 1, 12.5m + i)).ToList();
        Matches("detailed-long-invoice", Sales("detailed", FormDocuments.Invoice, lines, FormParts.PageNumbers));
    }

    [Fact]
    public void A_european_region_writes_its_own_dates_and_amounts_on_A4()
    {
        Matches("minimal-invoice-de", Sales("minimal", FormDocuments.Invoice, SampleLines(), paper: FormPaperSize.A4, currency: "EUR", culture: "de-DE"));
    }

    private static byte[] Sales(
        string preset, FormDocuments document, IReadOnlyList<FormLine> lines, FormParts extra = FormParts.None,
        FormPaperSize? paper = null, string currency = "USD", string culture = "en-US")
    {
        using var region = new Region(culture);
        var (design, style) = Design(preset, extra, paper);
        var subtotal = lines.Where(l => l.Style == FormLineStyle.Normal).Sum(l => l.Amount ?? 0m);
        var tax = Math.Round(subtotal * 0.08m, 2);
        var facts = new List<(string, string)> { ("Date", D(Date)) };
        if (document == FormDocuments.Invoice)
            facts.AddRange([("Due date", D(Date.AddDays(30))), ("Terms", "Net 30")]);
        var purchase = document == FormDocuments.PurchaseOrder;
        var form = new SalesForm(
            FormDesignService.UsualTitle(document), "1042", Company,
            purchase ? new FormParty("Harbor Supply Co", "20 Industrial Way\nRiverside, CA 92501", "orders@harbor.example", "(555) 010-0200") : Customer,
            facts, lines,
            [
                new FormTotal("Subtotal", subtotal), new FormTotal(FormService.SavingsLabel, 12.50m), new FormTotal("Sales tax", tax),
                new FormTotal("Total", subtotal + tax, Emphasis: true),
            ],
            "Memo: spring order, delivered to the loading dock.", currency, style,
            PartyLabel: purchase ? "VENDOR" : "BILL TO",
            ShipTo: purchase ? null : new FormParty("Bo's Bakery", "Dock 4, 250 Commerce Drive\nSpringfield, IL 62702"));
        return Renderer.Render(FormDesignService.Apply(form, design, document, style));
    }

    private static byte[] Statement(string preset)
    {
        using var region = new Region("en-US");
        var (design, style) = Design(preset, FormParts.None, null);
        var form = new StatementForm(Company, Customer, Date.AddDays(-30), Date, 250m,
            [
                new StatementFormLine(Date.AddDays(-20), "Invoice 1040", "1040", 480m, 730m),
                new StatementFormLine(Date.AddDays(-12), "Credit memo 7", "7", -30m, 700m),
                new StatementFormLine(Date.AddDays(-5), "Payment", "Check 2210", -250m, 450m),
            ],
            450m, [("Current", 450m), ("1–30 days", 0m), ("31–60 days", 0m), ("61–90 days", 0m), ("Over 90 days", 0m)], "USD", style);
        return Renderer.Render(FormDesignService.Apply(form, design, style));
    }

    private static (FormDesign Design, FormStyle Style) Design(string key, FormParts extra, FormPaperSize? paper)
    {
        var preset = FormDesignPresets.Find(key) ?? throw new ArgumentException($"No preset {key}.", nameof(key));
        var details = preset.Details with { Show = preset.Details.Show | extra, PaperSize = paper ?? preset.Details.PaperSize };
        var design = FormDesign.Create(details, new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        return (design, FormStyle.From(design, Face(design.FontKey), Face(design.CompanyFontKey)));
    }

    private static FormFontFace? Face(string? key)
    {
        if (key is null || key == FormDesign.BuiltInFont)
            return null;
        var fonts = Services.GetRequiredService<IBundledFonts>();
        var shipped = fonts.List().Single(f => f.Key == key);
        var files = fonts.Load(key) ?? throw new InvalidOperationException($"The font files for {key} are missing beside the tests.");
        return new FormFontFace(key, shipped.Family, files.Regular, files.Bold);
    }

    private static readonly FormParty Company =
        new("Northwind Studio", "48 Harbour Lane\nPortland, OR 97201", "hello@northwind.example", "(555) 010-0100");

    private static readonly FormParty Customer =
        new("Bo's Bakery", "100 Main Street\nSpringfield, IL 62701", "accounts@bosbakery.example", "(555) 010-0300");

    private static List<FormLine> SampleLines() =>
    [
        new FormLine("Design work", null, null, null, FormLineStyle.Heading),
        Line(1, "Consulting services, March", 6m, 95m, item: "Consulting", cls: "Services", taxable: "No"),
        Line(2, "Widget, blue", 12m, 4.5m, item: "Widget", sku: "WID-BLU-12", lots: "LOT-2410 x 12", cls: "Products", details: "Pack size: Case of 12"),
        new FormLine("Design work subtotal", null, null, 624m, FormLineStyle.Subtotal),
        new FormLine("Please call ahead: the dock closes at 4 pm.", null, null, null),
        Line(3, "Delivery", 1m, 25m, item: "Delivery", cls: "Services"),
        Line(4, "Spring discount", null, null, amount: -12.50m),
    ];

    private static FormLine Line(
        int number, string description, decimal? quantity, decimal? rate, string? item = null, string? sku = null, string? lots = null,
        string? cls = null, string taxable = "Yes", string? details = null, decimal? amount = null)
    {
        var cells = new Dictionary<string, string> { [FormColumns.Taxable] = taxable };
        if (item is not null)
            cells[FormColumns.Item] = item;
        if (sku is not null)
            cells[FormColumns.Sku] = sku;
        if (lots is not null)
            cells[FormColumns.Lots] = lots;
        if (cls is not null)
            cells[FormColumns.Class] = cls;
        return new FormLine(description, quantity, rate, amount ?? quantity * rate, LineNumber: number, Details: details, Cells: cells);
    }

    private static string D(DateOnly date) => date.ToString("d", CultureInfo.CurrentCulture);

    private static string Name(FormDocuments document) => document switch
    {
        FormDocuments.CreditMemo => "credit-memo",
        FormDocuments.SalesReceipt => "sales-receipt",
        FormDocuments.PurchaseOrder => "purchase-order",
        _ => document.ToString().ToLowerInvariant(),
    };

    private static void Matches(string name, byte[] pdf, [CallerFilePath] string source = "")
    {
        var actual = PdfSnapshot.Describe(pdf);
        var saved = Path.Combine(Path.GetDirectoryName(source)!, "Snapshots", name + ".txt");
        if (Environment.GetEnvironmentVariable("OPENBOOK_UPDATE_SNAPSHOTS") == "1")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(saved)!);
            File.WriteAllText(saved, actual);
            Keep(name, pdf, actual);
            return;
        }

        Assert.True(File.Exists(saved), $"No saved copy of {name}: run with OPENBOOK_UPDATE_SNAPSHOTS=1 and check it.");
        var expected = File.ReadAllText(saved).ReplaceLineEndings("\n");
        if (expected == actual)
            return;
        var folder = Keep(name, pdf, actual);
        Assert.Fail($"{name} prints differently from its saved copy ({PdfSnapshot.FirstDifference(expected, actual)}). " +
            $"The new PDF and text are in {folder}; if the change is meant, save it with OPENBOOK_UPDATE_SNAPSHOTS=1.");
    }

    private static string Keep(string name, byte[] pdf, string text)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "OpenBook.slnx")))
            dir = dir.Parent;
        var folder = Path.Combine(dir?.FullName ?? Path.GetTempPath(), "artifacts", "form-snapshots");
        Directory.CreateDirectory(folder);
        File.WriteAllBytes(Path.Combine(folder, name + ".pdf"), pdf);
        File.WriteAllText(Path.Combine(folder, name + ".txt"), text);
        return folder;
    }

    /// <summary>The culture dates and amounts are written in while a form is built and rendered.</summary>
    private sealed class Region : IDisposable
    {
        private readonly CultureInfo _culture = CultureInfo.CurrentCulture;
        private readonly CultureInfo _ui = CultureInfo.CurrentUICulture;

        public Region(string name)
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(name);
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(name);
        }

        public void Dispose()
        {
            CultureInfo.CurrentCulture = _culture;
            CultureInfo.CurrentUICulture = _ui;
        }
    }
}
