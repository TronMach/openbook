using Microsoft.Extensions.DependencyInjection;
using OpenBook.Application.Banking;
using OpenBook.Application.Purchases;
using OpenBook.Domain.Accounts;
using OpenBook.Domain.Banking;
using OpenBook.Domain.Common;
using OpenBook.Domain.Ledger;
using OpenBook.Domain.Purchases;

namespace OpenBook.Integration.Tests;

/// <summary>Everyday extras: possible duplicate bills, recurring expenses.</summary>
public sealed class EverydayExtrasTests : IAsyncLifetime
{
    private static readonly DateOnly Mar1 = new(2026, 3, 1);

    private readonly LedgerDatabase _db = new();
    private Account _supplies = null!;
    private Account _card = null!;
    private Vendor _parts = null!;
    private Vendor _other = null!;

    public async Task InitializeAsync()
    {
        await _db.InitializeAsync();
        _supplies = await _db.CreateAccount("Supplies", AccountType.Expense, "6100");
        _card = await _db.CreateAccount("Visa", AccountType.CreditCard, "2100");
        _parts = await In<VendorService, Vendor>(s => s.CreateAsync(new VendorDetails("Parts Supply Co")));
        _other = await In<VendorService, Vendor>(s => s.CreateAsync(new VendorDetails("Other Co")));
    }

    public Task DisposeAsync() => _db.DisposeAsync();

    private Task<T> In<TService, T>(Func<TService, Task<T>> work) where TService : notnull =>
        _db.InScope(sp => work(sp.GetRequiredService<TService>()));

    private Task<Bill> Bill(Vendor vendor, DateOnly date, decimal amount, string? reference = null) =>
        In<BillService, Bill>(s => s.CreateAsync(new CreateBillCommand(vendor.Id, date, [new BillLineDraft(_supplies.Id, amount, "Paper")], Reference: reference)));

    [Fact]
    public async Task Bills_from_the_same_vendor_for_the_same_amount_close_in_date_are_possible_duplicates()
    {
        var first = await Bill(_parts, Mar1, 120m, "A-1");
        await Bill(_parts, Mar1.AddDays(30), 120m, "A-2");
        await Bill(_parts, Mar1, 125m, "A-3");
        await Bill(_other, Mar1, 120m);

        var found = await In<BillService, IReadOnlyList<Bill>>(s => s.PossibleDuplicatesAsync(_parts.Id, Mar1.AddDays(10), 120m));
        Assert.Equal(first.Id, Assert.Single(found).Id);
        Assert.Empty(await In<BillService, IReadOnlyList<Bill>>(s => s.PossibleDuplicatesAsync(_parts.Id, Mar1, 120m, except: first.Id)));

        await In<BillService, Bill>(s => s.VoidAsync(first.Id, "Entered twice"));
        Assert.Empty(await In<BillService, IReadOnlyList<Bill>>(s => s.PossibleDuplicatesAsync(_parts.Id, Mar1, 120m)));
    }

    [Fact]
    public async Task A_recurring_expense_records_each_due_spend_from_its_account()
    {
        var template = await In<RecurringExpenseService, RecurringExpense>(s => s.CreateAsync(new RecurringExpenseDetails(
            "Design software", _card.Id, [new BankSplit(_supplies.Id, 29.99m, "Monthly plan")], RecurrenceFrequency.Monthly, Mar1,
            MaxOccurrences: 3, VendorId: _parts.Id)));

        // Manual templates wait for "Record due expenses"; the job only runs automatic ones.
        Assert.Empty((await In<RecurringExpenseService, IReadOnlyList<RecurringExpenseResult>>(s => s.CreateDueAsync(Mar1.AddMonths(1), automaticOnly: true))));
        var result = Assert.Single(await In<RecurringExpenseService, IReadOnlyList<RecurringExpenseResult>>(s => s.CreateDueAsync(Mar1.AddMonths(1), automaticOnly: false)));
        Assert.Null(result.Error);
        Assert.Equal([Mar1, Mar1.AddMonths(1)], result.Created.Select(t => t.Date).ToArray());
        Assert.All(result.Created, t => Assert.Equal((_card.Id, _parts.Id, BankTransactionKind.Spend, 29.99m, "Parts Supply Co"), (t.AccountId, t.VendorId, t.Kind, t.Amount, t.Payee)));

        var reloaded = await In<RecurringExpenseService, RecurringExpense?>(s => s.GetAsync(template.Id));
        Assert.Equal((2, Mar1.AddMonths(2)), (reloaded!.OccurrencesPosted, reloaded.NextDate));

        Assert.Equal("recurringexpense.account_type", (await Assert.ThrowsAsync<DomainException>(() => In<RecurringExpenseService, RecurringExpense>(s =>
            s.CreateAsync(new RecurringExpenseDetails("Wrong", _supplies.Id, [new BankSplit(_card.Id, 5m)], RecurrenceFrequency.Monthly, Mar1))))).Code);
    }
}
