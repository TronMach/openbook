using Microsoft.Extensions.DependencyInjection;
using OpenBook.Application.Sales;
using OpenBook.Domain.Accounts;
using OpenBook.Domain.Sales;

namespace OpenBook.Integration.Tests;

/// <summary>Items made from a picker while a form is being filled in, and the starter service of a new company.</summary>
public sealed class QuickAddTests : IAsyncLifetime
{
    private readonly LedgerDatabase _db = new();

    public Task InitializeAsync() => _db.InitializeAsync();

    public Task DisposeAsync() => _db.DisposeAsync();

    private async Task<T> Items<T>(Func<ItemService, Task<T>> work)
    {
        await using var scope = _db.Services.CreateAsyncScope();
        return await work(scope.ServiceProvider.GetRequiredService<ItemService>());
    }

    [Fact]
    public async Task A_quick_item_goes_to_the_first_income_account_or_the_one_chosen()
    {
        await _db.CreateAccount("Rent", AccountType.Expense, "6000");
        var first = await _db.CreateAccount("Consulting", AccountType.Income, "4000");
        var other = await _db.CreateAccount("Shop", AccountType.Income, "4100");

        var plain = await Items(s => s.CreateQuickAsync("Advice", ItemType.Service, 75m, false, null));
        Assert.Equal((first.Id, 75m, false, ItemType.Service), (plain.IncomeAccountId, plain.Rate, plain.Taxable, plain.Type));

        var chosen = await Items(s => s.CreateQuickAsync("Pens", ItemType.NonInventory, 2m, true, other.Id));
        Assert.Equal(other.Id, chosen.IncomeAccountId);
    }

    [Fact]
    public async Task A_quick_item_on_a_chart_with_no_income_account_makes_one()
    {
        var item = await Items(s => s.CreateQuickAsync("Advice", ItemType.Service, 0m, true, null));
        var income = await Items(s => s.IncomeAccountsAsync());
        Assert.Equal(item.IncomeAccountId, Assert.Single(income).Id);
        Assert.Equal("Sales", income[0].Name);
    }

    [Fact]
    public async Task The_starter_service_prefers_a_service_account_and_only_comes_once()
    {
        await _db.CreateAccount("Product Sales", AccountType.Income, "4000");
        var services = await _db.CreateAccount("Service Revenue", AccountType.Income, "4100");

        var item = await Items(s => s.CreateStarterServiceAsync());
        Assert.NotNull(item);
        Assert.Equal(("Services", services.Id), (item.Name, item.IncomeAccountId));

        Assert.Null(await Items(s => s.CreateStarterServiceAsync()));
    }

    [Fact]
    public async Task There_is_no_starter_service_without_an_income_account() =>
        Assert.Null(await Items(s => s.CreateStarterServiceAsync()));
}
