using System.Text;
using Microsoft.Extensions.DependencyInjection;
using OpenBook.Application.Forms;
using OpenBook.Application.Sales;
using OpenBook.Domain.Accounts;
using OpenBook.Domain.Common;
using OpenBook.Domain.Companies;
using OpenBook.Domain.Sales;

namespace OpenBook.Integration.Tests;

/// <summary>Saved form designs: which one prints, what it changes on the form, logos, paper and previews.</summary>
public sealed class FormDesignTests : IAsyncLifetime
{
    private static readonly DateOnly Mar1 = new(2026, 3, 1);

    /// <summary>A 40 × 10 PNG.</summary>
    private static readonly byte[] Logo = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAACgAAAAKCAIAAABJ+IsHAAAAG0lEQVR4nGOQz48ZEMQwavGoxaMWj1qcT6bFAOsbbbC65kkkAAAAAElFTkSuQmCC");

    private readonly LedgerDatabase _db = new();
    private Customer _acme = null!;
    private Item _consulting = null!;

    public async Task InitializeAsync()
    {
        await _db.InitializeAsync();
        var income = await _db.CreateAccount("Consulting", AccountType.Income, "4000");
        _consulting = await In<ItemService, Item>(s => s.CreateAsync(new ItemDetails("Consulting", ItemType.Service, income.Id, 100m)));
        _acme = await In<CustomerService, Customer>(s => s.CreateAsync(new CustomerDetails("Acme", ShippingAddress: "Dock 4\nSpringfield")));
    }

    public Task DisposeAsync() => _db.DisposeAsync();

    private Task<T> In<TService, T>(Func<TService, Task<T>> work) where TService : notnull =>
        _db.InScope(sp => work(sp.GetRequiredService<TService>()));

    private Task<T> Designs<T>(Func<FormDesignService, Task<T>> work) => In(work);

    private static async Task<string> Refused(Func<Task> action) => (await Assert.ThrowsAsync<DomainException>(action)).Code;

    private Task<Invoice> InvoiceFor(Customer customer) =>
        In<InvoiceService, Invoice>(s => s.CreateAsync(new CreateInvoiceCommand(customer.Id, Mar1, [new SalesLineDraft(_consulting.Id, "Consulting", 2m, 100m)])));

    private Task<SalesForm> Form(FormKind kind, Guid id) => In<FormService, SalesForm>(s => s.BuildAsync(kind, id));

    [Fact]
    public async Task Setup_makes_a_standard_default_and_designs_have_unique_names()
    {
        var standard = Assert.Single(await Designs(s => s.ListAsync()));
        Assert.Equal((FormDesignService.DefaultName, true), (standard.Name, standard.IsDefault));

        Assert.Equal("formdesign.name_taken", await Refused(() => Designs(s => s.CreateAsync(new FormDesignDetails("standard")))));
        Assert.Equal("formdesign.default_inactive", await Refused(() => Designs(s => s.SetActiveAsync(standard.Id, false))));
        Assert.Equal("company.color_invalid", await Refused(() => Designs(s => s.CreateAsync(new FormDesignDetails("Blue", AccentColor: "blue")))));
    }

    [Fact]
    public async Task The_customers_design_wins_then_the_documents_then_the_default()
    {
        var standard = (await Designs(s => s.ListAsync())).Single();
        var quotes = await Designs(s => s.CreateAsync(new FormDesignDetails("Quotes", FormTemplate.Modern)));
        var acmeDesign = await Designs(s => s.CreateAsync(new FormDesignDetails("Acme", FormTemplate.Compact)));
        await Designs(s => s.SetDefaultForAsync(quotes.Id, FormDocuments.Estimate | FormDocuments.Invoice));
        // Invoices move to a third design; quotes keeps estimates.
        var invoices = await Designs(s => s.CreateAsync(new FormDesignDetails("Invoices")));
        await Designs(s => s.SetDefaultForAsync(invoices.Id, FormDocuments.Invoice));
        Assert.Equal(FormDocuments.Estimate, (await Designs(s => s.GetAsync(quotes.Id)))!.DefaultFor);

        Assert.Equal(invoices.Id, (await Designs(s => s.ForAsync(FormDocuments.Invoice, _acme.Id))).Id);
        Assert.Equal(quotes.Id, (await Designs(s => s.ForAsync(FormDocuments.Estimate, _acme.Id))).Id);
        Assert.Equal(standard.Id, (await Designs(s => s.ForAsync(FormDocuments.Statement, _acme.Id))).Id);

        await Designs(s => s.SetForCustomerAsync(_acme.Id, acmeDesign.Id));
        Assert.All(FormDesign.Each(FormDesign.AllDocuments), d => Assert.Equal(acmeDesign.Id, Designs(s => s.ForAsync(d, _acme.Id)).Result.Id));

        // Switched off, the customer's design is passed over (and stops being the default for anything).
        await Designs(s => s.SetActiveAsync(acmeDesign.Id, false));
        Assert.Equal(invoices.Id, (await Designs(s => s.ForAsync(FormDocuments.Invoice, _acme.Id))).Id);
        Assert.Equal("formdesign.inactive", await Refused(() => Designs(s => s.MakeDefaultAsync(acmeDesign.Id))));

        await Designs(s => s.MakeDefaultAsync(quotes.Id));
        Assert.Equal([quotes.Id], (await Designs(s => s.ListAsync())).Where(d => d.IsDefault).Select(d => d.Id));
    }

    [Fact]
    public async Task A_design_sets_titles_messages_terms_ship_to_and_columns()
    {
        var invoice = await InvoiceFor(_acme);
        var plain = await Form(FormKind.Invoice, invoice.Id);
        Assert.Equal("Dock 4\nSpringfield", plain.ShipTo?.Address);
        Assert.Contains(plain.Facts, f => f.Label == "Terms");

        var standard = (await Designs(s => s.ListAsync())).Single();
        await Designs(s => s.UpdateAsync(standard.Id, standard.ToDetails() with
        {
            ShowShipTo = false,
            ShowTerms = false,
            ShowQuantityAndRate = false,
            ColumnLabels = new FormColumnLabels(Description: "Work done"),
            PaperSize = FormPaperSize.A4,
            Texts = new Dictionary<FormDocuments, (string?, string?)> { [FormDocuments.Invoice] = ("Tax invoice", "Thank you for your business.") },
        }));

        var form = await Form(FormKind.Invoice, invoice.Id);
        Assert.Equal("Tax invoice", form.Title);
        Assert.Null(form.ShipTo);
        Assert.DoesNotContain(form.Facts, f => f.Label is "Terms" or "Due date");
        Assert.Equal("Thank you for your business.", form.Message);
        Assert.Equal(("Work done", false, FormPaperSize.A4), (form.Style.ColumnLabels?.Description, form.Style.ShowQuantityAndRate, form.Style.PaperSize));

        await In<InvoiceService, Invoice>(s => s.VoidAsync(invoice.Id, "Duplicate"));
        Assert.Equal("Tax invoice (void)", (await Form(FormKind.Invoice, invoice.Id)).Title);

        var pdf = await In<FormService, RenderedForm>(s => s.RenderAsync(FormKind.Invoice, invoice.Id));
        Assert.StartsWith("Tax invoice", pdf.Title, StringComparison.Ordinal);
        // A4 is 595 × 842 points.
        Assert.Matches(@"/MediaBox\s*\[\s*0\s+0\s+595", Encoding.ASCII.GetString(pdf.Pdf));
    }

    [Fact]
    public async Task Logos_must_be_png_or_jpeg_and_print_in_every_position()
    {
        var standard = (await Designs(s => s.ListAsync())).Single();
        Assert.Equal("formdesign.logo_type", await Refused(() => Designs(s => s.SetLogoAsync(standard.Id, Encoding.ASCII.GetBytes("<svg></svg>")))));
        await Designs(s => s.SetLogoAsync(standard.Id, Logo));
        var invoice = await InvoiceFor(_acme);

        foreach (var position in Enum.GetValues<FormLogoPosition>())
        {
            var design = (await Designs(s => s.GetAsync(standard.Id)))!;
            await Designs(s => s.UpdateAsync(standard.Id, design.ToDetails() with { LogoPosition = position, LogoSize = FormLogoSize.Large }));
            var pdf = await In<FormService, RenderedForm>(s => s.RenderAsync(FormKind.Invoice, invoice.Id));
            Assert.Matches(@"/Subtype\s*/Image", Encoding.ASCII.GetString(pdf.Pdf));
        }

        await Designs(s => s.RemoveLogoAsync(standard.Id));
        Assert.Null((await Designs(s => s.GetAsync(standard.Id)))!.Logo);

        // The audit keeps a fingerprint of the logo, never its bytes.
        var audit = await _db.InDb(db => Task.FromResult(db.AuditRecords.Where(a => a.EntityType == FormDesignService.EntityType).ToList()));
        Assert.Contains(audit, a => a.After?.Contains($"image/png, {Logo.Length} bytes", StringComparison.Ordinal) == true);
    }

    [Fact]
    public async Task Previews_render_every_document_with_unsaved_settings()
    {
        var standard = (await Designs(s => s.ListAsync())).Single();
        await Designs(s => s.SetLogoAsync(standard.Id, Logo));
        foreach (var document in FormDesign.Each(FormDesign.AllDocuments))
        {
            var pdf = await Designs(s => s.PreviewAsync(new FormDesignDetails("Preview", FormTemplate.Modern, "#aa3300", PaperSize: FormPaperSize.A4,
                ShowQuantityAndRate: false), document, standard.Id));
            Assert.StartsWith("%PDF", Encoding.ASCII.GetString(pdf, 0, 4), StringComparison.Ordinal);
        }

        // Nothing is saved by a preview.
        Assert.Equal(FormTemplate.Classic, (await Designs(s => s.GetAsync(standard.Id)))!.Template);
    }
}
