using OpenBook.Domain.Common;

namespace OpenBook.Domain.Banking;

public enum RuleDirection
{
    Any,
    MoneyIn,
    MoneyOut,
}

/// <summary>
/// How to categorize statement lines that look alike: when the payee or memo contains some text (and,
/// optionally, the direction, account and amount fit), suggest a category, payee and vendor, or add the
/// transaction without review. Rules are tried in <see cref="Priority"/> order; the first that fits wins.
/// </summary>
public sealed class BankRule
{
    private BankRule() => (Name, Contains) = (string.Empty, string.Empty);

    public Guid Id { get; private set; }
    public string Name { get; private set; }

    /// <summary>Lower runs first.</summary>
    public int Priority { get; private set; }

    /// <summary>Only for this bank or card account; null for all.</summary>
    public Guid? AccountId { get; private set; }

    public RuleDirection Direction { get; private set; }

    /// <summary>Text the payee or memo must contain, ignoring case.</summary>
    public string Contains { get; private set; }

    public decimal? MinAmount { get; private set; }
    public decimal? MaxAmount { get; private set; }

    public Guid CategoryAccountId { get; private set; }

    /// <summary>Replaces the bank's payee text, e.g. "AMZN MKTP US*2K4" → "Amazon".</summary>
    public string? Payee { get; private set; }

    /// <summary>For money out: the vendor it's an expense for.</summary>
    public Guid? VendorId { get; private set; }

    /// <summary>Add matching lines straight to the books instead of suggesting.</summary>
    public bool AutoAdd { get; private set; }

    public bool IsActive { get; private set; }

    public static BankRule Create(BankRuleDetails details)
    {
        var rule = new BankRule { Id = Guid.CreateVersion7(), IsActive = true };
        rule.Update(details);
        return rule;
    }

    public void Update(BankRuleDetails details)
    {
        ArgumentNullException.ThrowIfNull(details);
        Name = Text.Optional(details.Name, 100, "rule.name_too_long", "Name")
            ?? throw new DomainException("rule.name_required", "Give the rule a name.");
        Contains = Text.Optional(details.Contains, 200, "rule.text_too_long", "Text")
            ?? throw new DomainException("rule.text_required", "Enter the text the payee or memo contains.");
        if (!Enum.IsDefined(details.Direction))
            throw new DomainException("rule.direction_invalid", "Unknown direction.");
        if (details.MinAmount is < 0m || details.MaxAmount is < 0m)
            throw new DomainException("rule.amount_negative", "Amounts in a rule are sizes: enter them without a sign.");
        if (details.MinAmount is { } min && details.MaxAmount is { } max && min > max)
            throw new DomainException("rule.amount_range", "The lowest amount is more than the highest.");
        if (details.CategoryAccountId == Guid.Empty)
            throw new DomainException("rule.category_required", "Choose the category the rule assigns.");
        if (details.VendorId is not null && details.Direction == RuleDirection.MoneyIn)
            throw new DomainException("rule.vendor_on_money_out", "Only money-out rules can name a vendor.");

        Priority = details.Priority;
        AccountId = details.AccountId;
        Direction = details.Direction;
        MinAmount = details.MinAmount;
        MaxAmount = details.MaxAmount;
        CategoryAccountId = details.CategoryAccountId;
        Payee = Text.Optional(details.Payee, 200, "rule.payee_too_long", "Payee");
        VendorId = details.VendorId;
        AutoAdd = details.AutoAdd;
    }

    public bool Fits(BankFeedItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (!IsActive || (AccountId is { } account && account != item.AccountId) || CategoryAccountId == item.AccountId)
            return false;
        if ((Direction == RuleDirection.MoneyIn && !item.IsMoneyIn) || (Direction == RuleDirection.MoneyOut && item.IsMoneyIn))
            return false;
        // A vendor makes sense only on money out, whatever the rule's direction says.
        if (VendorId is not null && item.IsMoneyIn)
            return false;
        var size = Math.Abs(item.Amount);
        if ((MinAmount is { } min && size < min) || (MaxAmount is { } max && size > max))
            return false;
        return (item.Payee?.Contains(Contains, StringComparison.OrdinalIgnoreCase) ?? false)
            || (item.Memo?.Contains(Contains, StringComparison.OrdinalIgnoreCase) ?? false);
    }

    public void Deactivate() => IsActive = false;

    public void Reactivate() => IsActive = true;
}

public sealed record BankRuleDetails(
    string Name,
    string Contains,
    Guid CategoryAccountId,
    RuleDirection Direction = RuleDirection.Any,
    Guid? AccountId = null,
    decimal? MinAmount = null,
    decimal? MaxAmount = null,
    string? Payee = null,
    Guid? VendorId = null,
    bool AutoAdd = false,
    int Priority = 100);
