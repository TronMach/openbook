using OpenBook.Domain.Common;

namespace OpenBook.Domain.Accounts;

public enum AccountType
{
    Asset,
    Liability,
    Equity,
    Income,
    CostOfGoodsSold,
    Expense,
    OtherIncome,
    OtherExpense,
}

/// <summary>
/// A chart-of-accounts entry. M0 keeps this minimal so the ledger can post;
/// hierarchy rules, numbering settings and templates arrive in M1.
/// </summary>
public sealed class Account
{
    private Account() => Name = string.Empty;

    public Guid Id { get; private set; }
    public string? Number { get; private set; }
    public string Name { get; private set; }
    public AccountType Type { get; private set; }
    public Guid? ParentId { get; private set; }
    public bool IsActive { get; private set; }

    public static Account Create(string name, AccountType type, string? number = null, Guid? parentId = null)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new DomainException("account.name_required", "An account needs a name.");
        if (!Enum.IsDefined(type))
            throw new DomainException("account.type_invalid", $"Unknown account type {type}.");

        return new Account
        {
            Id = Guid.CreateVersion7(),
            Name = name.Trim(),
            Type = type,
            Number = string.IsNullOrWhiteSpace(number) ? null : number.Trim(),
            ParentId = parentId,
            IsActive = true,
        };
    }

    public void Deactivate() => IsActive = false;
}
