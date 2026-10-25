using OpenBook.Domain.Accounts;
using OpenBook.Domain.Purchases;

namespace OpenBook.Web.Components.Purchases;

/// <summary>A category line as typed in <see cref="BillLinesEditor"/>: bills, vendor credits and recurring bills. Bills can also take inventory item lines.</summary>
public sealed class BillLineModel
{
    public Guid? AccountId { get; set; }
    public string? Description { get; set; }
    public decimal? Amount { get; set; }

    /// <summary>An inventory item received; the line then posts to the item's asset account.</summary>
    public Guid? ItemId { get; set; }

    public decimal? Quantity { get; set; }

    public Guid? ClassId { get; set; }

    private bool IsFilled => AccountId is not null || ItemId is not null || (Amount ?? 0m) != 0m;

    public static decimal Total(IEnumerable<BillLineModel> lines) => lines.Sum(l => l.Amount ?? 0m);

    /// <summary>Every filled line has a category and an amount, and the total is above zero.</summary>
    public static bool IsComplete(IReadOnlyCollection<BillLineModel> lines) =>
        Total(lines) > 0m && lines.Where(l => l.IsFilled).All(l => l.ItemId is not null
            ? (l.Quantity ?? 0m) > 0m && (l.Amount ?? -1m) >= 0m
            : l.AccountId is not null && (l.Amount ?? 0m) != 0m);

    /// <summary>Item lines go with an empty account: the bill service posts them to the item's asset account.</summary>
    public static List<BillLineDraft> Drafts(IEnumerable<BillLineModel> lines) =>
        lines.Where(l => l.IsFilled)
            .Select(l => l.ItemId is { } item
                ? new BillLineDraft(Guid.Empty, l.Amount ?? 0m, l.Description, item, l.Quantity, l.ClassId)
                : new BillLineDraft(l.AccountId!.Value, l.Amount!.Value, l.Description, ClassId: l.ClassId))
            .ToList();

    public static BillLineModel From(BillLineDraft draft)
    {
        ArgumentNullException.ThrowIfNull(draft);
        return new BillLineModel { AccountId = draft.AccountId, Amount = draft.Amount, Description = draft.Description, ItemId = draft.ItemId, Quantity = draft.Quantity };
    }

    /// <summary>Accounts a bill line can use, expenses first: anything but receivables and payables.</summary>
    public static List<Account> Categories(ChartOfAccounts chart)
    {
        ArgumentNullException.ThrowIfNull(chart);
        return chart.Ordered(includeInactive: false).Select(x => x.Account)
            .Where(a => a.Type is not (AccountType.AccountsPayable or AccountType.AccountsReceivable))
            .OrderBy(a => a.Type.ClassOf() != AccountClass.Expense)
            .ToList();
    }
}
