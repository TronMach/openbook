using Microsoft.Extensions.DependencyInjection;
using OpenBook.Application;
using OpenBook.Application.Purchases;
using OpenBook.Application.Reporting;
using OpenBook.Application.Sales;
using OpenBook.Domain.Accounts;
using OpenBook.Domain.Ledger;
using OpenBook.Domain.Purchases;
using OpenBook.Domain.Sales;

namespace OpenBook.Integration.Tests;

/// <summary>
/// Reports that need only totals read history folded (<see cref="ReportLedger.LinesSinceAsync"/>,
/// <see cref="ReportLedger.TotalsAsync"/>), added up by the database on the accrual basis: they must give exactly
/// the sums that reading every line gives, on both bases, including an account brought back to zero.
/// </summary>
public sealed class LedgerFoldingTests : IAsyncLifetime
{
    private readonly LedgerDatabase _db = new();

    public async Task InitializeAsync()
    {
        await _db.InitializeAsync();
        var checking = await _db.CreateAccount("Checking", AccountType.Bank, "1000");
        var undeposited = await _db.CreateAccount("Undeposited Funds", AccountType.OtherCurrentAsset, "1200");
        var income = await _db.CreateAccount("Service Revenue", AccountType.Income, "4000");
        var supplies = await _db.CreateAccount("Supplies", AccountType.Expense, "6100");
        var item = await In<ItemService, Item>(s => s.CreateAsync(new ItemDetails("Work", ItemType.Service, income.Id, 100m)));
        var customer = await In<CustomerService, Customer>(s => s.CreateAsync(new CustomerDetails("Acme")));
        var vendor = await In<VendorService, Vendor>(s => s.CreateAsync(new VendorDetails("Paper Co")));

        foreach (var date in new[] { new DateOnly(2024, 11, 5), new DateOnly(2025, 6, 9), new DateOnly(2025, 12, 30), new DateOnly(2026, 1, 20) })
        {
            var invoice = await In<InvoiceService, Invoice>(s => s.CreateAsync(new CreateInvoiceCommand(customer.Id, date, [new SalesLineDraft(item.Id, null, 3m, 100m)])));
            await In<PaymentService, CustomerPayment>(s => s.ReceiveAsync(new ReceivePaymentCommand(customer.Id, date.AddDays(10), undeposited.Id,
                [new ApplyToInvoice(invoice.Id, 200m)])));
            var bill = await In<BillService, Bill>(s => s.CreateAsync(new CreateBillCommand(vendor.Id, date, [new BillLineDraft(supplies.Id, 80m)])));
            await In<BillPaymentService, IReadOnlyList<BillPayment>>(s => s.PayAsync(new PayBillsCommand(date.AddDays(5), checking.Id, [new ApplyToBill(bill.Id, 50m)])));
        }

        // Everything waiting goes into the bank on the last payment's day, so Undeposited Funds ends at zero that day.
        var waiting = await In<DepositService, IReadOnlyList<UndepositedItem>>(s => s.UndepositedAsync());
        await In<DepositService, BankDeposit>(s => s.DepositAsync(new MakeDepositCommand(checking.Id, new DateOnly(2026, 1, 30), waiting.Select(w => (w.Kind, w.Id)).ToList())));
    }

    public Task DisposeAsync() => _db.DisposeAsync();

    private Task<T> In<TService, T>(Func<TService, Task<T>> work) where TService : notnull =>
        _db.InScope(sp => work(sp.GetRequiredService<TService>()));

    private static Dictionary<Guid, decimal> Sums(IEnumerable<LedgerLine> lines) =>
        lines.GroupBy(l => l.AccountId).Select(g => (g.Key, Sum: g.Sum(l => l.Amount))).Where(x => x.Sum != 0m).ToDictionary(x => x.Key, x => x.Sum);

    public static TheoryData<ReportBasis, string, string> Splits => new()
    {
        { ReportBasis.Accrual, "2025-01-01", "2026-01-30" },
        { ReportBasis.Accrual, "2026-01-30", "2026-01-30" },
        { ReportBasis.Accrual, "2025-06-15", "2025-12-31" },
        { ReportBasis.Cash, "2025-01-01", "2026-01-30" },
        { ReportBasis.Cash, "2026-01-30", "2026-01-30" },
    };

    [Theory]
    [MemberData(nameof(Splits))]
    public async Task Folded_history_adds_up_to_the_same_as_every_line(ReportBasis basis, string split, string through)
    {
        var splitAt = DateOnly.Parse(split, System.Globalization.CultureInfo.InvariantCulture);
        var to = DateOnly.Parse(through, System.Globalization.CultureInfo.InvariantCulture);
        var all = await In<ReportLedger, IReadOnlyList<LedgerLine>>(s => s.LinesAsync(basis, null, to));
        var since = await In<ReportLedger, IReadOnlyList<LedgerLine>>(s => s.LinesSinceAsync(basis, splitAt, to));
        var totals = await In<ReportLedger, IReadOnlyList<LedgerLine>>(s => s.TotalsAsync(basis, splitAt, to));

        Assert.Equal(Sums(all), Sums(since));
        Assert.Equal(Sums(all), Sums(totals));
        Assert.Equal(Sums(all.Where(l => l.Date < splitAt)), Sums(since.Where(l => l.Date < splitAt)));
        Assert.Equal(Sums(all.Where(l => l.Date < splitAt)), Sums(totals.Where(l => l.Date < splitAt)));
        Assert.Equal(all.Count(l => l.Date >= splitAt), since.Count(l => l.Date >= splitAt));
        Assert.All(since.Where(l => l.Date < splitAt), l => Assert.Equal(ReportLedger.BroughtForwardSource, l.Source));

        // A few accounts' balances, as the sales tax figures read them: the same sums, for just those accounts.
        var some = all.Select(l => l.AccountId).Distinct().Order().Take(3).Append(Guid.NewGuid()).ToList();
        var balances = await In<ReportLedger, IReadOnlyDictionary<Guid, decimal>>(s => s.BalancesAsync(basis, some, to));
        Assert.Equal(
            Sums(all.Where(l => some.Contains(l.AccountId))).Where(x => x.Value != 0m).OrderBy(x => x.Key),
            balances.Where(x => x.Value != 0m).OrderBy(x => x.Key));
    }

    [Fact]
    public async Task An_account_brought_to_zero_on_the_last_day_is_zero_in_the_trial_balance()
    {
        var undeposited = (await In<Application.Accounts.AccountService, OpenBook.Domain.Accounts.ChartOfAccounts>(s => s.GetChartAsync()))
            .FindByFullName("Undeposited Funds")!;

        var tb = await In<Application.Ledger.TrialBalanceQuery, Application.Ledger.TrialBalance>(s => s.RunAsync(new DateOnly(2026, 1, 30), ReportBasis.Accrual));
        var dayBefore = await In<Application.Ledger.TrialBalanceQuery, Application.Ledger.TrialBalance>(s => s.RunAsync(new DateOnly(2026, 1, 29), ReportBasis.Accrual));

        Assert.True(tb.IsBalanced);
        Assert.DoesNotContain(tb.Lines, l => l.AccountId == undeposited.Id);
        Assert.Contains(dayBefore.Lines, l => l.AccountId == undeposited.Id && l.Debit.Amount > 0m);
    }
}
