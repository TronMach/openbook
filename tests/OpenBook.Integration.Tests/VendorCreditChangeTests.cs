using Microsoft.Extensions.DependencyInjection;
using OpenBook.Application;
using OpenBook.Application.Ledger;
using OpenBook.Application.Purchases;
using OpenBook.Application.Reporting;
using OpenBook.Domain.Accounts;
using OpenBook.Domain.Common;
using OpenBook.Domain.Ledger;
using OpenBook.Domain.Purchases;

namespace OpenBook.Integration.Tests;

/// <summary>Correcting a posted vendor credit in place: its entry reversed and a new one posted, same id.</summary>
public sealed class VendorCreditChangeTests : IAsyncLifetime
{
    private static readonly DateOnly Jan5 = new(2026, 1, 5);
    private static readonly DateOnly Jan10 = new(2026, 1, 10);
    private static readonly DateOnly Mar1 = new(2026, 3, 1);
    private static readonly DateOnly Mar5 = new(2026, 3, 5);

    private readonly LedgerDatabase _db = new();
    private Account _checking = null!;
    private Account _rent = null!;
    private Account _supplies = null!;
    private Vendor _landlord = null!;

    public async Task InitializeAsync()
    {
        await _db.InitializeAsync();
        _checking = await _db.CreateAccount("Checking", AccountType.Bank, "1000");
        _rent = await _db.CreateAccount("Rent", AccountType.Expense, "6500");
        _supplies = await _db.CreateAccount("Supplies", AccountType.Expense, "6600");
        _landlord = await In<VendorService, Vendor>(s => s.CreateAsync(new VendorDetails("Landlord")));
    }

    public Task DisposeAsync() => _db.DisposeAsync();

    private Task<T> In<TService, T>(Func<TService, Task<T>> work, string? closingPassword = null) where TService : notnull =>
        _db.InScope(sp =>
        {
            sp.GetRequiredService<ClosedPeriodOverride>().Password = closingPassword;
            return work(sp.GetRequiredService<TService>());
        });

    private static async Task<string> Refused(Func<Task> action) => (await Assert.ThrowsAsync<DomainException>(action)).Code;

    private Task<VendorCredit> Credit(DateOnly date, params BillLineDraft[] lines) =>
        In<VendorCreditService, VendorCredit>(s => s.CreateAsync(new CreateVendorCreditCommand(_landlord.Id, date, lines, Reference: "CR-1")));

    private Task<VendorCredit> Change(Guid id, DateOnly date, BillLineDraft[] lines, string? reference = null, string? closingPassword = null) =>
        In<VendorCreditService, VendorCredit>(s => s.ChangeAsync(id, new ChangeVendorCreditCommand(date, lines, "Corrected", reference)), closingPassword);

    private BillLineDraft Rent(decimal amount) => new(_rent.Id, amount, "Rent");

    private BillLineDraft Supplies(decimal amount) => new(_supplies.Id, amount, "Supplies");

    private Task<decimal> Balance(Guid accountId, ReportBasis basis = ReportBasis.Accrual) =>
        _db.InScope(async sp => (await sp.GetRequiredService<ReportLedger>().LinesAsync(basis, null, new DateOnly(2026, 12, 31)))
            .Where(l => l.AccountId == accountId).Sum(l => l.Amount));

    [Fact]
    public async Task A_change_reverses_the_old_entry_and_posts_the_new_one()
    {
        var original = await Credit(Mar1, Rent(100m));
        var changed = await Change(original.Id, Mar5, [Rent(60m), Supplies(20m)]);

        Assert.Equal((original.Id, "CR-1", 80m, Mar5), (changed.Id, changed.Reference, changed.Total, changed.Date));
        Assert.Equal((-60m, -20m), (await Balance(_rent.Id), await Balance(_supplies.Id)));
        var (entries, _) = await In<PostingEngine, (IReadOnlyList<JournalEntry>, int)>(e => e.ListAsync(new JournalEntryFilter(Source: VendorCredit.Source, SourceId: original.Id)));
        Assert.Equal(3, entries.Count);
        Assert.Single(await In<VendorCreditService, IReadOnlyList<DocumentChange>>(s => s.ChangesAsync(original.Id)));
        Assert.Null((await Change(original.Id, Mar5, [Rent(80m)], reference: "")).Reference);
        Assert.Equal("vendorcredit.no_items", await Refused(() => Change(original.Id, Mar5, [new BillLineDraft(Guid.Empty, 10m, "Widget", Guid.NewGuid(), 1m)])));
    }

    [Fact]
    public async Task Applied_credit_keeps_the_total_and_the_date_no_later_than_the_first_application()
    {
        var bill = await In<BillService, Bill>(s => s.CreateAsync(new CreateBillCommand(_landlord.Id, Mar1, [Rent(1000m)])));
        var credit = await Credit(Mar1, Rent(100m));
        await In<VendorCreditService, bool>(async s =>
        {
            await s.ApplyAsync(_landlord.Id, Mar5, [new ApplyVendorCredit(credit.Id, bill.Id, 70m)]);
            return true;
        });

        Assert.Equal("vendorcredit.total_below_used", await Refused(() => Change(credit.Id, Mar1, [Rent(50m)])));
        Assert.Equal("credit.applied_before_payment", await Refused(() => Change(credit.Id, new DateOnly(2026, 3, 6), [Rent(100m)])));
        var changed = await Change(credit.Id, Mar5, [Rent(70m)]);
        Assert.Equal((70m, 0m), (changed.Total, changed.Balance));
    }

    [Fact]
    public async Task Refunded_credit_in_a_closed_period_needs_the_password()
    {
        var credit = await Credit(Jan5, Rent(100m));
        await In<VendorCreditService, VendorRefund>(s => s.RecordRefundAsync(new RecordVendorRefundCommand(credit.Id, Jan10, _checking.Id, 100m)));
        await In<CloseBooksService, ClosingStatus>(s => s.SetAsync(new DateOnly(2026, 1, 31), "secret1", false));

        Assert.Equal("period.password_required", await Refused(() => Change(credit.Id, Jan5, [Rent(50m), Supplies(50m)])));
        await Change(credit.Id, Jan5, [Rent(50m), Supplies(50m)], closingPassword: "secret1");

        // Cash basis: the refund now reduces both expenses.
        Assert.Equal((-50m, -50m), (await Balance(_rent.Id, ReportBasis.Cash), await Balance(_supplies.Id, ReportBasis.Cash)));
    }
}
