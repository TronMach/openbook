using OpenBook.Domain.Accounts;
using OpenBook.Domain.Common;

namespace OpenBook.Domain.Sales;

/// <summary>What you sell. Stock-tracked inventory items arrive in V1.6.</summary>
public enum ItemType
{
    Service,
    NonInventory,
}

/// <summary>A product or service with a default description, price and the income account its sales post to.</summary>
public sealed class Item
{
    private Item() => Name = string.Empty;

    public Guid Id { get; private set; }
    public string Name { get; private set; }
    public ItemType Type { get; private set; }

    /// <summary>Default description on sales lines.</summary>
    public string? Description { get; private set; }

    /// <summary>Default unit price, up to 4 decimal places.</summary>
    public decimal Rate { get; private set; }

    public Guid IncomeAccountId { get; private set; }

    /// <summary>Whether sales tax applies by default; each sales line can override it.</summary>
    public bool Taxable { get; private set; }

    public bool IsActive { get; private set; }

    public static Item Create(ItemDetails details)
    {
        var item = new Item { Id = Guid.CreateVersion7(), IsActive = true };
        item.Update(details);
        return item;
    }

    public void Update(ItemDetails details)
    {
        ArgumentNullException.ThrowIfNull(details);
        Name = NormalizeName(details.Name);
        Type = Enum.IsDefined(details.Type) ? details.Type : throw new DomainException("item.type_invalid", $"Unknown item type {details.Type}.");
        Description = Text.Optional(details.Description, 1000, "item.description_too_long", "Description");
        Rate = SalesLine.ValidRate(details.Rate);
        Taxable = details.Taxable;
        IncomeAccountId = details.IncomeAccountId == Guid.Empty
            ? throw new DomainException("item.income_account_required", "Choose the income account sales of this item post to.")
            : details.IncomeAccountId;
    }

    public void Deactivate() => IsActive = false;

    public void Reactivate() => IsActive = true;

    /// <summary>Sales post to income accounts (income or other income).</summary>
    public static void EnsureIncomeAccount(Account account)
    {
        ArgumentNullException.ThrowIfNull(account);
        if (account.Type.ClassOf() != AccountClass.Income)
            throw new DomainException("item.income_account_type", $"'{account.Name}' isn't an income account.");
        if (!account.IsActive)
            throw new DomainException("item.income_account_inactive", $"'{account.Name}' is inactive.");
    }

    public static string NormalizeName(string? name) =>
        Text.Optional(name, 100, "item.name_too_long", "Item name")
        ?? throw new DomainException("item.name_required", "An item needs a name.");
}

public sealed record ItemDetails(string Name, ItemType Type, Guid IncomeAccountId, decimal Rate = 0m, string? Description = null, bool Taxable = true);
