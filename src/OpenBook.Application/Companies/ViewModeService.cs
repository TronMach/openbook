using OpenBook.Application.Purchases;
using OpenBook.Application.Sales;
using OpenBook.Domain.Companies;
using OpenBook.Domain.Purchases;
using OpenBook.Domain.Sales;

namespace OpenBook.Application.Companies;

/// <summary>What the screens need to know about the person's view: theirs, else the company's default.</summary>
public sealed record ViewState(ViewMode Mode, Edition Edition, bool OwnChoice, MenuStyle Menu = MenuStyle.Bar);

/// <summary>Which view a person sees, and switching it. A preference only: nothing is audited and no rule depends on it.</summary>
public sealed class ViewModeService(ICompanyStore store, ICurrentUser user)
{
    public async Task<ViewState> CurrentAsync(CancellationToken ct = default)
    {
        var company = await store.GetCompanyAsync(ct);
        if (company is null)
            return new ViewState(ViewMode.Full, Edition.General, false);
        var own = await store.GetViewPreferenceAsync(user.Actor, ct);
        return new ViewState(own?.Mode ?? company.DefaultViewMode, company.Edition, own?.Mode is not null, own?.Menu ?? MenuStyle.Bar);
    }

    /// <summary>Remembers the current person's view.</summary>
    public async Task SetAsync(ViewMode mode, CancellationToken ct = default)
    {
        var own = await store.GetViewPreferenceAsync(user.Actor, ct);
        if (own is null)
            store.Add(ViewPreference.Create(user.Actor, mode));
        else
            own.Set(mode);
        await store.SaveChangesAsync(ct);
    }

    /// <summary>Remembers how the current person wants the full view's menu drawn.</summary>
    public async Task SetMenuAsync(MenuStyle menu, CancellationToken ct = default)
    {
        var own = await store.GetViewPreferenceAsync(user.Actor, ct);
        if (own is null)
        {
            own = ViewPreference.Create(user.Actor);
            store.Add(own);
        }

        own.Set(menu);
        await store.SaveChangesAsync(ct);
    }
}

/// <summary>
/// What's waiting at each step of the simple view's home page. The "used" flags say whether the company has ever made
/// that kind of document, so a step nobody uses yet can say what it's for instead of showing zero.
/// </summary>
public sealed record WorkflowFigures(
    int OpenEstimates,
    bool EstimatesUsed,
    int OpenInvoices,
    int OverdueInvoices,
    decimal OwedToYou,
    bool InvoicesUsed,
    bool SalesReceiptsUsed,
    bool PaymentsUsed,
    int PaymentsToDeposit,
    decimal ToDeposit,
    bool DepositsUsed,
    int OpenOrders,
    int OrdersToReceive,
    bool OrdersUsed,
    int BillsToPay,
    int BillsDueSoon,
    decimal YouOwe,
    bool BillsUsed,
    int ActiveItems,
    int StockItems);

public sealed class HomeWorkflowService(ISalesStore sales, IPurchasesStore purchases, DepositService deposits)
{
    /// <summary>Bills due within this many days count as due soon.</summary>
    public const int DueSoonDays = 7;

    public async Task<WorkflowFigures> FiguresAsync(DateOnly today, CancellationToken ct = default)
    {
        // Counts and open documents only: this runs on every visit to the home page.
        var counts = await sales.CountDocumentsAsync(ct);
        var openInvoices = await sales.ListOpenInvoicesAsync(null, ct);
        var waiting = await deposits.UndepositedAsync(ct);
        var orders = await purchases.ListPurchaseOrdersAsync(null, ct);
        var openBills = await purchases.ListOpenBillsAsync(null, ct);
        var anyBills = openBills.Count > 0 || await purchases.AnyBillsAsync(ct);
        var items = (await sales.ListItemsAsync(ct)).Where(i => i.IsActive).ToList();

        var openOrders = orders.Where(o => o.Status == PurchaseOrderStatus.Open).ToList();
        var payable = openBills.Where(b => b.CanBePaid).ToList();

        return new WorkflowFigures(
            counts.PendingEstimates,
            counts.Estimates > 0,
            openInvoices.Count,
            openInvoices.Count(i => i.IsOverdue(today)),
            openInvoices.Sum(i => i.Balance),
            counts.Invoices > 0,
            counts.Receipts > 0,
            counts.Payments > 0,
            waiting.Count,
            waiting.Sum(w => w.Amount),
            counts.Deposits > 0 || waiting.Count > 0,
            openOrders.Count,
            openOrders.Count(o => o.Lines.Any(l => l.ItemId is not null && o.Received.GetValueOrDefault(l.LineNumber) < l.Quantity)),
            orders.Count > 0,
            payable.Count,
            payable.Count(b => b.DueDate <= today.AddDays(DueSoonDays)),
            payable.Sum(b => b.Balance),
            anyBills,
            items.Count,
            items.Count(i => i.IsInventory));
    }
}
