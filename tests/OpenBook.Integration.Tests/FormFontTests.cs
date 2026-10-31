using System.Text;
using Microsoft.Extensions.DependencyInjection;
using OpenBook.Application.Forms;
using OpenBook.Application.Sales;
using OpenBook.Domain.Accounts;
using OpenBook.Domain.Common;
using OpenBook.Domain.Companies;
using OpenBook.Domain.Sales;

namespace OpenBook.Integration.Tests;

/// <summary>Fonts for printed forms: those shipped with OpenBook, Google Fonts downloads and uploads.</summary>
public sealed class FormFontTests : IAsyncLifetime
{
    private static readonly string Shipped = Path.Combine(AppContext.BaseDirectory, "Forms", "Fonts");

    private readonly LedgerDatabase _db = new();
    private Invoice _invoice = null!;

    public async Task InitializeAsync()
    {
        await _db.InitializeAsync();
        var income = await _db.CreateAccount("Consulting", AccountType.Income, "4000");
        var item = await In<ItemService, Item>(s => s.CreateAsync(new ItemDetails("Consulting", ItemType.Service, income.Id, 100m)));
        var acme = await In<CustomerService, Customer>(s => s.CreateAsync(new CustomerDetails("Acme")));
        _invoice = await In<InvoiceService, Invoice>(s => s.CreateAsync(new CreateInvoiceCommand(acme.Id, new DateOnly(2026, 3, 1),
            [new SalesLineDraft(item.Id, "Consulting", 2m, 100m)])));
    }

    public Task DisposeAsync() => _db.DisposeAsync();

    private Task<T> In<TService, T>(Func<TService, Task<T>> work) where TService : notnull =>
        _db.InScope(sp => work(sp.GetRequiredService<TService>()));

    private Task<T> Fonts<T>(Func<FormFontService, Task<T>> work) => In(work);

    private static async Task<string> Refused(Func<Task> action) => (await Assert.ThrowsAsync<DomainException>(action)).Code;

    private static byte[] ShippedFile(string key, string face) => File.ReadAllBytes(Path.Combine(Shipped, key, face));

    /// <summary>Prints the invoice with the default design set to a font, and returns the PDF as text.</summary>
    private async Task<string> PrintWith(string? fontKey)
    {
        var design = (await In<FormDesignService, IReadOnlyList<FormDesign>>(s => s.ListAsync())).Single(d => d.IsDefault);
        await In<FormDesignService, FormDesign>(s => s.UpdateAsync(design.Id, design.ToDetails() with { FontKey = fontKey }));
        var pdf = await In<FormService, RenderedForm>(s => s.RenderAsync(FormKind.Invoice, _invoice.Id));
        return Encoding.ASCII.GetString(pdf.Pdf);
    }

    [Fact]
    public async Task Twelve_fonts_ship_with_their_licenses_and_print()
    {
        var choices = await Fonts(s => s.ChoicesAsync());
        Assert.Equal((null, "Built in"), (choices[0].Key, choices[0].Group));
        var shipped = choices.Where(c => c.Group is "Sans-serif" or "Serif" or "Monospace").ToList();
        Assert.Equal(12, shipped.Count);
        Assert.All(shipped, c => Assert.True(File.Exists(Path.Combine(Shipped, c.Key!, "OFL.txt")), c.Family));

        // PDFsharp names the embedded faces "Lora Regular" and "Lora,Bold".
        var lora = await PrintWith("lora");
        Assert.Matches("/BaseFont\\s*/[A-Z]{6}\\+Lora#20Regular", lora);
        Assert.Matches("/BaseFont\\s*/[A-Z]{6}\\+Lora,Bold", lora);
        Assert.DoesNotMatch("/BaseFont\\s*/[A-Z]{6}\\+Lora", await PrintWith(null));
        Assert.Equal("font.unknown", await Refused(() => PrintWith("comic-sans")));
    }

    [Fact]
    public async Task Google_fonts_are_downloaded_once_and_kept_until_no_design_uses_them()
    {
        _db.GoogleFonts.Families["Work Sans"] = (ShippedFile("lato", "Regular.ttf"), ShippedFile("lato", "Bold.ttf"));
        var font = await Fonts(s => s.AddFromGoogleAsync("work sans"));
        Assert.Equal(("Work Sans", "added-work-sans", FormFontOrigin.Google), (font.Family, font.Key, font.Origin));
        Assert.Contains(_db.GoogleFonts.Requests, r => r.StartsWith("https://fonts.googleapis.com/css2?family=Work+Sans:wght@400;700", StringComparison.Ordinal));

        Assert.Equal("font.exists", await Refused(() => Fonts(s => s.AddFromGoogleAsync("Work Sans"))));
        Assert.Equal("font.built_in", await Refused(() => Fonts(s => s.AddFromGoogleAsync("Lato"))));
        Assert.Equal("font.not_found", await Refused(() => Fonts(s => s.AddFromGoogleAsync("No Such Font"))));
        Assert.Equal("font.family_invalid", await Refused(() => Fonts(s => s.AddFromGoogleAsync("Robo<script>"))));
        _db.GoogleFonts.Offline = true;
        Assert.Equal("font.unreachable", await Refused(() => Fonts(s => s.AddFromGoogleAsync("Space Grotesk"))));

        Assert.Matches("/BaseFont\\s*/[A-Z]{6}\\+Lato", await PrintWith(font.Key));
        Assert.Equal("font.in_use", await Refused(() => Fonts(async s => { await s.RemoveAsync(font.Key); return 0; })));
        Assert.False((await Fonts(s => s.ChoicesAsync())).Single(c => c.Key == font.Key).CanRemove);

        await PrintWith(null);
        await Fonts(async s => { await s.RemoveAsync(font.Key); return 0; });
        Assert.DoesNotContain(await Fonts(s => s.ChoicesAsync()), c => c.Key == font.Key);
        var audit = await _db.InDb(db => Task.FromResult(db.AuditRecords.Where(a => a.EntityType == FormFontService.EntityType).Select(a => a.Action).ToList()));
        Assert.Equal([OpenBook.Domain.Auditing.AuditAction.Created, OpenBook.Domain.Auditing.AuditAction.Deleted], audit);
    }

    [Fact]
    public async Task Uploads_must_be_truetype_and_bold_is_optional()
    {
        var regular = ShippedFile("roboto", "Regular.ttf");
        Assert.Equal("font.woff", await Refused(() => Fonts(s => s.UploadAsync("Brand", Encoding.ASCII.GetBytes("wOF2rest-of-a-web-font"), null))));
        Assert.Equal("font.cff", await Refused(() => Fonts(s => s.UploadAsync("Brand", Encoding.ASCII.GetBytes("OTTOrest-of-a-cff-font"), null))));
        Assert.Equal("font.not_truetype", await Refused(() => Fonts(s => s.UploadAsync("Brand", Encoding.ASCII.GetBytes("%PDF-1.7 not a font at all"), null))));
        Assert.Equal("font.not_truetype", await Refused(() => Fonts(s => s.UploadAsync("Brand", regular, Encoding.ASCII.GetBytes("nonsense nonsense")))));

        var brand = await Fonts(s => s.UploadAsync("Brand Sans", regular, null));
        Assert.Equal((FormFontOrigin.Uploaded, null), (brand.Origin, brand.Bold));
        // Without a bold face, bold text is simulated from the regular one.
        var pdf = await PrintWith(brand.Key);
        Assert.Matches("/BaseFont\\s*/[A-Z]{6}\\+Roboto", pdf);
        Assert.DoesNotMatch("Roboto-Bold", pdf);
    }
}
