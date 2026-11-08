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
    public async Task A_design_can_show_the_previous_balance_and_retainer_on_invoices()
    {
        var first = await InvoiceFor(_acme);
        var second = await InvoiceFor(_acme);
        Assert.DoesNotContain((await Form(FormKind.Invoice, second.Id)).Totals, t => t.Label == FormService.PreviousBalanceLabel);

        var standard = (await Designs(s => s.ListAsync())).Single();
        await Designs(s => s.UpdateAsync(standard.Id, standard.ToDetails() with { Show = FormParts.Default | FormParts.AccountBalance }));
        var totals = (await Form(FormKind.Invoice, second.Id)).Totals.ToDictionary(t => t.Label, t => t.Amount);
        Assert.Equal((200m, 400m), (totals[FormService.PreviousBalanceLabel], totals[FormService.TotalOwedLabel]));
        Assert.DoesNotContain(FormService.PreviousBalanceLabel, (await Form(FormKind.Invoice, first.Id)).Totals.Select(t => t.Label));
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
            Show = FormParts.Default & ~(FormParts.ShipTo | FormParts.Terms),
            Columns = [new(FormColumns.Description, "Work done"), new(FormColumns.Amount)],
            PaperSize = FormPaperSize.A4,
            Texts = new Dictionary<FormDocuments, (string?, string?)> { [FormDocuments.Invoice] = ("Tax invoice", "Thank you for your business.") },
        }));

        var form = await Form(FormKind.Invoice, invoice.Id);
        Assert.Equal("Tax invoice", form.Title);
        Assert.Null(form.ShipTo);
        Assert.DoesNotContain(form.Facts, f => f.Label is "Terms" or "Due date");
        Assert.Equal("Thank you for your business.", form.Message);
        Assert.Equal(["Work done", "Amount"], form.Style.TableColumns.Select(c => c.Label));
        Assert.Equal(FormPaperSize.A4, form.Style.PaperSize);
        // The older fields still read the same.
        var saved = (await Designs(s => s.GetAsync(standard.Id)))!;
        Assert.Equal((false, false, false, "Work done"), (saved.ShowShipTo, saved.ShowTerms, saved.ShowQuantityAndRate, saved.ColumnLabels.Description));

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
                Columns: [new(FormColumns.Description), new(FormColumns.Amount)]), document, standard.Id));
            Assert.StartsWith("%PDF", Encoding.ASCII.GetString(pdf, 0, 4), StringComparison.Ordinal);
        }

        // Nothing is saved by a preview.
        Assert.Equal(FormTemplate.Classic, (await Designs(s => s.GetAsync(standard.Id)))!.Template);
    }

    [Fact]
    public async Task Columns_are_checked_and_print_item_details_where_lines_have_them()
    {
        var standard = (await Designs(s => s.ListAsync())).Single();
        Task<string> Columns(params FormColumnDraft[] columns) => Refused(() => Designs(s => s.UpdateAsync(standard.Id, standard.ToDetails() with { Columns = columns })));
        Assert.Equal("formdesign.columns_required", await Columns());
        Assert.Equal("formdesign.column_unknown", await Columns(new FormColumnDraft("colour")));
        Assert.Equal("formdesign.column_twice", await Columns(new(FormColumns.Rate), new(FormColumns.Rate)));
        Assert.Equal("formdesign.column_width", await Columns(new FormColumnDraft(FormColumns.Rate, Width: 0)));
        Assert.Equal("formdesign.too_many_columns", await Columns([.. FormColumns.BuiltIn.Select(c => new FormColumnDraft(c.Key)), new(FormColumns.Number)]));
        Assert.Equal("formdesign.field_unknown", await Columns(new FormColumnDraft(FormColumns.Field(Guid.NewGuid()))));

        var income = await _db.CreateAccount("Products", AccountType.Income, "4100");
        var widget = await In<ItemService, Item>(s => s.CreateAsync(new ItemDetails("Widget", ItemType.NonInventory, income.Id, 4.5m, Barcode: "WID-12")));
        var invoice = await In<InvoiceService, Invoice>(s => s.CreateAsync(new CreateInvoiceCommand(_acme.Id, Mar1,
            [new SalesLineDraft(_consulting.Id, "Advice", 2m, 100m), new SalesLineDraft(null, "Packed by hand", 0m, 0m, SalesLineKind.Note), new SalesLineDraft(widget.Id, "Blue widget", 3m, 4.5m)])));
        await Designs(s => s.UpdateAsync(standard.Id, standard.ToDetails() with
        {
            Columns = [new(FormColumns.Number), new(FormColumns.Item), new(FormColumns.Description), new(FormColumns.Sku), new(FormColumns.Class),
                new(FormColumns.Quantity, "Hours"), new(FormColumns.Amount, Width: 20)],
        }));

        var form = await Form(FormKind.Invoice, invoice.Id);
        Assert.Equal(["#", "Item", "Description", "SKU", "Class", "Hours", "Amount"], form.Style.TableColumns.Select(c => c.Label));
        Assert.Equal([("Consulting", null), (null, null), ("Widget", "WID-12")],
            form.Lines.Select(l => (l.Cell(FormColumns.Item), l.Cell(FormColumns.Sku))));
        // No line has a class, so that column is left out of the PDF; every look prints.
        foreach (var template in Enum.GetValues<FormTemplate>())
        {
            var design = (await Designs(s => s.GetAsync(standard.Id)))!;
            await Designs(s => s.UpdateAsync(standard.Id, design.ToDetails() with
            {
                Template = template,
                Show = FormParts.All,
                Landscape = template == FormTemplate.Boxed,
                Margins = FormMargins.Narrow,
                TextSize = FormTextSize.Large,
            }));
            var pdf = await In<FormService, RenderedForm>(s => s.RenderAsync(FormKind.Invoice, invoice.Id));
            Assert.StartsWith("%PDF", Encoding.ASCII.GetString(pdf.Pdf, 0, 4), StringComparison.Ordinal);
            var statement = await In<FormService, RenderedForm>(s => s.RenderStatementAsync(_acme.Id, Mar1.AddDays(-30), Mar1));
            Assert.StartsWith("%PDF", Encoding.ASCII.GetString(statement.Pdf, 0, 4), StringComparison.Ordinal);
        }

        // Landscape Letter is 792 points wide.
        Assert.Matches(@"/MediaBox\s*\[\s*0\s+0\s+792", Encoding.ASCII.GetString((await Designs(s => s.PreviewAsync(
            new FormDesignDetails("Wide", Landscape: true), FormDocuments.Invoice))).AsSpan()));
    }

    [Fact]
    public async Task Columns_can_fit_their_contents_and_line_up_left_center_or_right()
    {
        var standard = (await Designs(s => s.ListAsync())).Single();
        Assert.Equal("formdesign.column_align", await Refused(() => Designs(s => s.UpdateAsync(standard.Id, standard.ToDetails() with
        {
            Columns = [new FormColumnDraft(FormColumns.Description, Align: (ColumnAlign)7)],
        }))));

        await Designs(s => s.UpdateAsync(standard.Id, standard.ToDetails() with
        {
            Columns = [new(FormColumns.Description), new(FormColumns.Quantity, AutoWidth: true, Align: ColumnAlign.Center),
                new(FormColumns.Rate, Align: ColumnAlign.Right), new(FormColumns.Amount, AutoWidth: true, Align: ColumnAlign.Left)],
        }));
        var design = (await Designs(s => s.GetAsync(standard.Id)))!;
        // A column's usual alignment is kept as "usual".
        Assert.Equal([(false, null), (true, ColumnAlign.Center), (false, null), (true, ColumnAlign.Left)],
            design.Columns.Select(c => (c.AutoWidth, c.Align)));
        Assert.Equal(design.Columns.Select(c => c.ToDraft()), design.ToDetails().Columns!);

        var invoice = await InvoiceFor(_acme);
        var form = await Form(FormKind.Invoice, invoice.Id);
        Assert.Equal([(ColumnAlign.Left, false), (ColumnAlign.Center, true), (ColumnAlign.Right, false), (ColumnAlign.Left, true)],
            form.Style.TableColumns.Select(c => (c.Align, c.AutoWidth)));
        foreach (var template in Enum.GetValues<FormTemplate>())
        {
            await Designs(s => s.UpdateAsync(standard.Id, design.ToDetails() with { Template = template }));
            Assert.StartsWith("%PDF", Encoding.ASCII.GetString((await In<FormService, RenderedForm>(s => s.RenderAsync(FormKind.Invoice, invoice.Id))).Pdf, 0, 4), StringComparison.Ordinal);
        }

        var audit = await _db.InDb(db => Task.FromResult(db.AuditRecords.Where(a => a.EntityType == FormDesignService.EntityType).OrderBy(a => a.Id).Select(a => a.After).ToList()));
        Assert.Contains(audit, a => a!.Contains("quantity (auto) center", StringComparison.Ordinal));
    }

    [Fact]
    public void Auto_columns_take_what_they_need_and_the_others_share_the_rest()
    {
        static int[] Share(int textIndex, double available, params (int Weight, double? Need)[] columns)
        {
            var widths = FormColumnWidths.Share(columns, available, textIndex);
            Assert.Equal(available, widths.Sum(), 6);
            return [.. widths.Select(w => (int)Math.Round(w))];
        }

        Assert.Equal([430, 30, 40], Share(0, 500, (50, null), (10, 30), (14, 40)));
        Assert.Equal([200, 100, 200], Share(0, 500, (20, null), (10, 100), (20, null)));
        // Every column fits its contents: the room left goes to the text column, else to all by their needs.
        Assert.Equal([470, 30], Share(0, 500, (50, 100), (10, 30)));
        Assert.Equal([200, 600], Share(-1, 800, (1, 100), (1, 300)));
        // Too wide: the text column (which can wrap) gives way first.
        Assert.Equal([500, 40, 60], Share(0, 600, (50, 900), (10, 40), (10, 60)));
        // One auto column takes at most 60% while others share the table.
        Assert.Equal([200, 300], Share(0, 500, (50, null), (10, 2000)));
    }

    [Fact]
    public async Task What_is_shown_follows_the_design()
    {
        var invoice = await InvoiceFor(_acme);
        var standard = (await Designs(s => s.ListAsync())).Single();
        await In<CustomerService, Customer>(s => s.UpdateAsync(_acme.Id, new CustomerDetails("Acme", Email: "ap@acme.test", Phone: "555-0100", ShippingAddress: "Dock 4\nSpringfield")));
        Assert.Equal("ap@acme.test", (await Form(FormKind.Invoice, invoice.Id)).Customer.Email);

        await Designs(s => s.UpdateAsync(standard.Id, standard.ToDetails() with
        {
            Show = FormParts.Default & ~(FormParts.CustomerContact | FormParts.Totals | FormParts.CompanyAddress) | FormParts.Acceptance,
        }));
        var form = await Form(FormKind.Invoice, invoice.Id);
        Assert.Equal((null, null), (form.Customer.Email, form.Customer.Phone));
        Assert.Null(form.Company.Address);
        Assert.Empty(form.Totals);
        // Acceptance lines are for estimates only.
        Assert.False(form.Acceptance);
        var design = (await Designs(s => s.GetAsync(standard.Id)))!;
        Assert.True(FormDesignService.Apply(form, design, FormDocuments.Estimate, form.Style).Acceptance);
        Assert.NotEmpty((await In<FormService, RenderedForm>(s => s.RenderAsync(FormKind.Invoice, invoice.Id))).Pdf);
    }

    [Fact]
    public async Task Ready_made_designs_become_designs_and_any_design_can_print_a_document()
    {
        foreach (var preset in FormDesignPresets.All)
        {
            foreach (var document in new[] { FormDocuments.Invoice, FormDocuments.Estimate, FormDocuments.Statement })
                Assert.StartsWith("%PDF", Encoding.ASCII.GetString(await Designs(s => s.PreviewAsync(preset.Details, document)), 0, 4), StringComparison.Ordinal);
            var made = await Designs(s => s.CreateFromPresetAsync(preset.Key));
            Assert.Equal((preset.Details.Name, preset.Details.Template, preset.Details.Columns!.Count), (made.Name, made.Template, made.Columns.Count));
        }

        // The ink savers: black, no fills, columns fitted to their contents, the company in a font of its own.
        var ink = (await Designs(s => s.ListAsync())).Single(d => d.Name == "Ink saver");
        Assert.Equal((FormTemplate.Minimal, "#000000", "eb-garamond", "lato"), (ink.Template, ink.AccentColor, ink.FontKey, ink.CompanyFontKey));
        Assert.Equal([false, true, true, true], ink.Columns.Select(c => c.AutoWidth));
        Assert.DoesNotContain(FormParts.StripedRows, FormDesign.EachPart(ink.Show));
        var compact = (await Designs(s => s.ListAsync())).Single(d => d.Name == "Ink saver, compact");
        Assert.Equal((FormMargins.Narrow, FormTextSize.Small), (compact.Margins, compact.TextSize));

        Assert.Equal("Bold 2", (await Designs(s => s.CreateFromPresetAsync("bold"))).Name);
        Assert.Equal("Our slip", (await Designs(s => s.CreateFromPresetAsync("packing-slip", "Our slip"))).Name);
        Assert.Equal("formdesign.preset_unknown", await Refused(() => Designs(s => s.CreateFromPresetAsync("nope"))));

        // A packing slip: printed on request, the invoice's usual design is untouched.
        var invoice = await InvoiceFor(_acme);
        var slip = (await Designs(s => s.ListAsync())).Single(d => d.Name == "Packing slip");
        var form = await In<FormService, SalesForm>(s => s.BuildAsync(FormKind.Invoice, invoice.Id, slip.Id));
        Assert.Equal(("Packing slip", 0), (form.Title, form.Totals.Count));
        Assert.Equal("Shipped", form.Style.TableColumns.Single(c => c.Key == FormColumns.Quantity).Label);
        Assert.Equal("Invoice", (await Form(FormKind.Invoice, invoice.Id)).Title);
        Assert.NotEmpty((await In<FormService, RenderedForm>(s => s.RenderAsync(FormKind.Invoice, invoice.Id, slip.Id))).Pdf);

        await Designs(s => s.SetActiveAsync(slip.Id, false));
        Assert.Equal("formdesign.inactive", await Refused(() => In<FormService, RenderedForm>(s => s.RenderAsync(FormKind.Invoice, invoice.Id, slip.Id))));
    }
}
