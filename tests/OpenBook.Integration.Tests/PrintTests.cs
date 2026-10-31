using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using OpenBook.Application.Forms;
using OpenBook.Application.Purchases;
using OpenBook.Application.Sales;
using OpenBook.Domain.Accounts;
using OpenBook.Domain.Common;
using OpenBook.Domain.Companies;
using OpenBook.Domain.Purchases;
using OpenBook.Domain.Sales;

namespace OpenBook.Integration.Tests;

/// <summary>Printing: the "print later" queue and batches printed as one PDF.</summary>
public sealed partial class PrintTests : IAsyncLifetime
{
    private static readonly DateOnly Mar1 = new(2026, 3, 1);

    private readonly LedgerDatabase _db = new();
    private Customer _acme = null!;
    private Item _consulting = null!;
    private Vendor _supplier = null!;
    private Account _supplies = null!;

    public async Task InitializeAsync()
    {
        await _db.InitializeAsync();
        var income = await _db.CreateAccount("Consulting", AccountType.Income, "4000");
        _supplies = await _db.CreateAccount("Supplies", AccountType.Expense, "6100");
        _consulting = await In<ItemService, Item>(s => s.CreateAsync(new ItemDetails("Consulting", ItemType.Service, income.Id, 100m)));
        _acme = await In<CustomerService, Customer>(s => s.CreateAsync(new CustomerDetails("Acme")));
        _supplier = await In<VendorService, Vendor>(s => s.CreateAsync(new VendorDetails("Parts Supply Co")));
    }

    public Task DisposeAsync() => _db.DisposeAsync();

    private Task<T> In<TService, T>(Func<TService, Task<T>> work) where TService : notnull =>
        _db.InScope(sp => work(sp.GetRequiredService<TService>()));

    private Task<T> Printing<T>(Func<PrintService, Task<T>> work) => In(work);

    private static async Task<string> Refused(Func<Task> action) => (await Assert.ThrowsAsync<DomainException>(action)).Code;

    private Task<Invoice> InvoiceOn(DateOnly date) =>
        In<InvoiceService, Invoice>(s => s.CreateAsync(new CreateInvoiceCommand(_acme.Id, date, [new SalesLineDraft(_consulting.Id, "Consulting", 1m, 100m)])));

    private static int Pages(byte[] pdf) => PagePattern().Count(Encoding.ASCII.GetString(pdf));

    [GeneratedRegex(@"/Type\s*/Page(?![s\w])")]
    private static partial Regex PagePattern();

    [Fact]
    public async Task Documents_wait_in_the_queue_once_and_can_be_taken_out()
    {
        var invoice = await InvoiceOn(Mar1);
        var first = await Printing(s => s.AddAsync(FormDocuments.Invoice, invoice.Id));
        var again = await Printing(s => s.AddAsync(FormDocuments.Invoice, invoice.Id));
        Assert.Equal(first.Id, again.Id);
        Assert.True(await Printing(s => s.IsWaitingAsync(FormDocuments.Invoice, invoice.Id)));
        var entry = Assert.Single(await Printing(s => s.QueueAsync()));
        Assert.Equal(($"Invoice {invoice.Number}", "Acme", 100m), (entry.Document?.Title, entry.Document?.Party, entry.Document?.Total));

        Assert.Equal("print.document_invalid", await Refused(() => Printing(s => s.AddAsync(FormDocuments.Statement, _acme.Id))));
        await Assert.ThrowsAsync<EntityNotFoundException>(() => Printing(s => s.AddAsync(FormDocuments.Estimate, invoice.Id)));

        await Printing(async s => { await s.RemoveAsync(FormDocuments.Invoice, invoice.Id); return 0; });
        Assert.Empty(await Printing(s => s.QueueAsync()));
    }

    [Fact]
    public async Task A_batch_prints_documents_and_statements_as_one_pdf_and_marks_the_queue_printed()
    {
        var march = await InvoiceOn(Mar1);
        var april = await InvoiceOn(Mar1.AddMonths(1));
        var estimate = await In<EstimateService, Estimate>(s => s.CreateAsync(new SaveEstimateCommand(_acme.Id, Mar1, [new SalesLineDraft(_consulting.Id, "Consulting", 3m, 100m)])));
        var order = await In<PurchaseOrderService, PurchaseOrder>(s => s.CreateAsync(new SavePurchaseOrderCommand(_supplier.Id, Mar1,
            [new PurchaseOrderLineDraft(null, _supplies.Id, "Paper", 1m, 40m)])));
        await Printing(s => s.AddAsync(FormDocuments.Invoice, march.Id));
        await Printing(s => s.AddAsync(FormDocuments.PurchaseOrder, order.Id));

        // Picking by kind and date.
        Assert.Equal([march.Id], (await Printing(s => s.DocumentsAsync(FormDocuments.Invoice, Mar1, Mar1.AddDays(10)))).Select(d => d.Id));

        var pdf = await Printing(s => s.PrintAsync(
            [(FormDocuments.Invoice, march.Id), (FormDocuments.Estimate, estimate.Id), (FormDocuments.PurchaseOrder, order.Id), (FormDocuments.Invoice, march.Id)],
            new StatementRun([_acme.Id], Mar1, Mar1.AddMonths(2))));
        Assert.StartsWith("%PDF", Encoding.ASCII.GetString(pdf.Pdf, 0, 4), StringComparison.Ordinal);
        // The repeated invoice prints once: three documents and one statement, a page each.
        Assert.Equal(4, Pages(pdf.Pdf));
        Assert.Equal("4 documents", pdf.Title);

        Assert.Empty(await Printing(s => s.QueueAsync()));
        var printed = await Printing(s => s.PrintedAsync());
        Assert.Equal(2, printed.Count);
        Assert.All(printed, p => Assert.Equal(LedgerDatabase.OwnerUsername, p.Item.PrintedBy));

        // One that came out wrong goes back in the queue; april's invoice was never queued.
        await Printing(async s => { await s.PutBackAsync(printed[0].Item.Id); return 0; });
        Assert.Single(await Printing(s => s.QueueAsync()));
        Assert.False(await Printing(s => s.IsWaitingAsync(FormDocuments.Invoice, april.Id)));
    }

    [Fact]
    public async Task A_batch_needs_something_to_print_and_not_too_much()
    {
        Assert.Equal("print.nothing", await Refused(() => Printing(s => s.PrintAsync([]))));
        var many = Enumerable.Range(0, PrintService.MaxDocuments + 1).Select(_ => (FormDocuments.Invoice, Guid.NewGuid())).ToList();
        Assert.Equal("print.too_many", await Refused(() => Printing(s => s.PrintAsync(many))));
        Assert.Equal("print.period_invalid", await Refused(() => Printing(s => s.PrintAsync([], new StatementRun([_acme.Id], Mar1, Mar1.AddDays(-1))))));
    }

}
