using Microsoft.Extensions.DependencyInjection;
using OpenBook.Application;
using OpenBook.Application.Sales;
using OpenBook.Domain.Accounts;
using OpenBook.Domain.Common;
using OpenBook.Domain.Sales;

namespace OpenBook.Integration.Tests;

/// <summary>A form left open is told the record moved on, instead of overwriting what happened since.</summary>
public sealed class RecordVersionTests : IAsyncLifetime
{
    private static readonly DateOnly Jan5 = new(2026, 1, 5);

    private readonly LedgerDatabase _db = new();
    private Item _item = null!;
    private Customer _acme = null!;

    public async Task InitializeAsync()
    {
        await _db.InitializeAsync();
        var income = await _db.CreateAccount("Service Revenue", AccountType.Income, "4000");
        _item = await _db.InScope(sp => sp.GetRequiredService<ItemService>().CreateAsync(new ItemDetails("Advice", ItemType.Service, income.Id, 100m, Taxable: false)));
        _acme = await _db.InScope(sp => sp.GetRequiredService<CustomerService>().CreateAsync(new CustomerDetails("Acme")));
    }

    public Task DisposeAsync() => _db.DisposeAsync();

    private Task<int?> VersionOf<T>(Guid id) where T : class =>
        _db.InScope(sp => sp.GetRequiredService<IRecordVersions>().GetAsync<T>(id));

    private Task Check<T>(Guid id, int? opened) where T : class =>
        _db.InScope(sp => sp.GetRequiredService<IRecordVersions>().EnsureUnchangedAsync<T>(id, opened));

    [Fact]
    public async Task An_invoice_changed_since_the_form_opened_is_refused()
    {
        var invoice = await _db.InScope(sp => sp.GetRequiredService<InvoiceService>().CreateAsync(new CreateInvoiceCommand(_acme.Id, Jan5, [new SalesLineDraft(_item.Id, null, 1m, 100m)])));
        var opened = await VersionOf<Invoice>(invoice.Id);
        Assert.NotNull(opened);
        await Check<Invoice>(invoice.Id, opened);

        await _db.InScope(sp => sp.GetRequiredService<InvoiceService>().ChangeAsync(invoice.Id,
            new ChangeInvoiceCommand(Jan5, [new SalesLineDraft(_item.Id, null, 2m, 100m)], "Two hours, not one")));

        var refused = await Assert.ThrowsAsync<DomainException>(() => Check<Invoice>(invoice.Id, opened));
        Assert.Equal(RecordVersionChecks.StaleCode, refused.Code);
        await Check<Invoice>(invoice.Id, await VersionOf<Invoice>(invoice.Id));
    }

    [Fact]
    public async Task A_customer_has_a_version_too()
    {
        var opened = await VersionOf<Customer>(_acme.Id);
        await _db.InScope(sp => sp.GetRequiredService<CustomerService>().UpdateAsync(_acme.Id, new CustomerDetails("Acme Ltd")));
        await Assert.ThrowsAsync<DomainException>(() => Check<Customer>(_acme.Id, opened));
    }

    [Fact]
    public async Task With_no_version_noted_or_no_record_there_is_nothing_to_compare()
    {
        await Check<Customer>(_acme.Id, null);
        await Check<Customer>(Guid.NewGuid(), 3);
        Assert.Null(await VersionOf<Customer>(Guid.NewGuid()));
    }
}
