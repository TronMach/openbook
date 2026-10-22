using System.Text.RegularExpressions;
using OpenBook.Domain.Common;

namespace OpenBook.Domain.Accounts;

/// <summary>
/// QuickBooks' account types, in balance-sheet-then-P&amp;L order. Banking (M2), receivables (M3) and
/// payables (M4) rely on the specific types, so pick the narrowest one that fits.
/// </summary>
public enum AccountType
{
    Bank,
    AccountsReceivable,
    OtherCurrentAsset,
    FixedAsset,
    OtherAsset,
    AccountsPayable,
    CreditCard,
    OtherCurrentLiability,
    LongTermLiability,
    Equity,
    Income,
    CostOfGoodsSold,
    Expense,
    OtherIncome,
    OtherExpense,
}

/// <summary>The five classes financial statements group account types into.</summary>
public enum AccountClass
{
    Asset,
    Liability,
    Equity,
    Income,
    Expense,
}

public static class AccountTypes
{
    public static AccountClass ClassOf(this AccountType type) => type switch
    {
        AccountType.Bank or AccountType.AccountsReceivable or AccountType.OtherCurrentAsset
            or AccountType.FixedAsset or AccountType.OtherAsset => AccountClass.Asset,
        AccountType.AccountsPayable or AccountType.CreditCard or AccountType.OtherCurrentLiability
            or AccountType.LongTermLiability => AccountClass.Liability,
        AccountType.Equity => AccountClass.Equity,
        AccountType.Income or AccountType.OtherIncome => AccountClass.Income,
        AccountType.CostOfGoodsSold or AccountType.Expense or AccountType.OtherExpense => AccountClass.Expense,
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, null),
    };

    /// <summary>Assets and expenses normally carry debit balances; everything else credit.</summary>
    public static bool IsDebitNormal(this AccountType type) => type.ClassOf() is AccountClass.Asset or AccountClass.Expense;

    public static bool IsBalanceSheet(this AccountType type) => type.ClassOf() is AccountClass.Asset or AccountClass.Liability or AccountClass.Equity;

    /// <summary>"Other current asset" for error messages; the UI has its own labels.</summary>
    public static string Describe(this AccountType type)
    {
        var name = type.ToString();
        var words = new System.Text.StringBuilder(name[..1]);
        foreach (var c in name[1..])
            words.Append(char.IsUpper(c) ? " " + char.ToLowerInvariant(c) : c.ToString());
        return words.ToString();
    }
}

/// <summary>
/// A chart-of-accounts entry. Rules that need the rest of the chart (parents, unique names and
/// numbers, depth) live in <see cref="ChartOfAccounts"/>.
/// </summary>
public sealed partial class Account
{
    public const int MaxNameLength = 100;
    public const char PathSeparator = ':';

    private Account() => Name = string.Empty;

    public Guid Id { get; private set; }
    public string? Number { get; private set; }
    public string Name { get; private set; }
    public string? Description { get; private set; }
    public AccountType Type { get; private set; }
    public Guid? ParentId { get; private set; }
    public bool IsActive { get; private set; }

    public static Account Create(string name, AccountType type, string? number = null, Guid? parentId = null, string? description = null) =>
        new()
        {
            Id = Guid.CreateVersion7(),
            Name = NormalizeName(name),
            Type = ValidType(type),
            Number = NormalizeNumber(number),
            ParentId = parentId,
            Description = NormalizeDescription(description),
            IsActive = true,
        };

    public void Update(string name, string? number, string? description)
    {
        Name = NormalizeName(name);
        Number = NormalizeNumber(number);
        Description = NormalizeDescription(description);
    }

    public void ChangeType(AccountType type) => Type = ValidType(type);

    public void MoveTo(Guid? parentId)
    {
        if (parentId == Id)
            throw new DomainException("account.parent_self", "An account can't be its own parent.");
        ParentId = parentId;
    }

    public void Deactivate() => IsActive = false;

    public void Reactivate() => IsActive = true;

    public static string NormalizeName(string? name)
    {
        var trimmed = name?.Trim() ?? string.Empty;
        if (trimmed.Length == 0)
            throw new DomainException("account.name_required", "An account needs a name.");
        if (trimmed.Length > MaxNameLength)
            throw new DomainException("account.name_too_long", $"Account names can be at most {MaxNameLength} characters.");
        if (trimmed.Contains(PathSeparator, StringComparison.Ordinal))
            throw new DomainException("account.name_colon", $"Account names can't contain '{PathSeparator}'; it separates parent and subaccount names.");
        return trimmed;
    }

    public static string? NormalizeNumber(string? number)
    {
        if (string.IsNullOrWhiteSpace(number))
            return null;
        var trimmed = number.Trim();
        if (!NumberPattern().IsMatch(trimmed))
            throw new DomainException("account.number_invalid", "Account numbers are up to 20 letters, digits, '.' or '-'.");
        return trimmed;
    }

    private static string? NormalizeDescription(string? description)
    {
        if (string.IsNullOrWhiteSpace(description))
            return null;
        var trimmed = description.Trim();
        return trimmed.Length <= 500
            ? trimmed
            : throw new DomainException("account.description_too_long", "Descriptions can be at most 500 characters.");
    }

    private static AccountType ValidType(AccountType type) =>
        Enum.IsDefined(type) ? type : throw new DomainException("account.type_invalid", $"Unknown account type {type}.");

    [GeneratedRegex(@"^[A-Za-z0-9.\-]{1,20}$")]
    private static partial Regex NumberPattern();
}
