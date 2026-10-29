using Microsoft.Extensions.DependencyInjection;
using OpenBook.Application.Forms;
using OpenBook.Application.Ledger;
using OpenBook.Application.Reporting;
using OpenBook.Domain.Accounts;
using OpenBook.Domain.Common;
using OpenBook.Domain.Companies;
using OpenBook.Domain.Ledger;
using OpenBook.Domain.Reporting;

namespace OpenBook.Integration.Tests;

/// <summary>L6 slice 3: the custom report builder, KPIs and the dashboard, and reports emailed on a schedule.</summary>
public sealed class ReportingAdvancedTests : IAsyncLifetime
{
    private readonly LedgerDatabase _db = new();
    private Account _checking = null!;
    private Account _sales = null!;
    private Account _rent = null!;
    private Account _supplies = null!;

    public async Task InitializeAsync()
    {
        await _db.InitializeAsync();
        _checking = await _db.CreateAccount("Checking", AccountType.Bank, "1000");
        _sales = await _db.CreateAccount("Sales", AccountType.Income, "4000");
        _rent = await _db.CreateAccount("Rent", AccountType.Expense, "6100");
        _supplies = await _db.CreateAccount("Supplies", AccountType.Expense, "6200");
        await Post(new DateOnly(2026, 1, 10), _checking, _sales, 1_000m, "January sales");
        await Post(new DateOnly(2026, 1, 15), _rent, _checking, 400m, "January rent");
        await Post(new DateOnly(2026, 1, 20), _supplies, _checking, 75m, "Paper");
        await Post(new DateOnly(2026, 2, 15), _rent, _checking, 400m, "February rent");
    }

    public Task DisposeAsync() => _db.DisposeAsync();

    private Task<T> In<TService, T>(Func<TService, Task<T>> work) where TService : notnull =>
        _db.InScope(sp => work(sp.GetRequiredService<TService>()));

    private Task<JournalEntry> Post(DateOnly date, Account debit, Account credit, decimal amount, string memo) =>
        In<PostingEngine, JournalEntry>(s => s.PostAsync(new PostJournalEntryCommand(date, [JournalLineDraft.Dr(debit.Id, amount), JournalLineDraft.Cr(credit.Id, amount)], memo)));

    private Task<ReportTable> Run(string key, string query) =>
        _db.InScope(sp => sp.GetRequiredService<ReportCatalog>().RunAsync(key, ReportParameters.Parse(query)));

    [Fact]
    public async Task The_builder_filters_groups_and_totals_ledger_lines()
    {
        var table = await Run("builder", "preset=Custom&from=2026-01-01&to=2026-12-31&accountClass=Expense&groupBy=Account&columns=Date,Memo,Debit,Credit,Balance&title=Spending");
        Assert.Equal("Spending", table.Title);
        Assert.Equal(["Date", "Memo", "Debit", "Credit", "Balance"], table.Columns.Select(c => c.Header));
        Assert.Equal(800m, table.Find("Total for 6100 Rent", 2));
        Assert.Equal(75m, table.Find("Total for 6200 Supplies", 2));
        Assert.Equal(875m, table.Find("Total", 2));

        var paper = await Run("builder", "preset=Custom&from=2026-01-01&to=2026-12-31&memo=paper");
        Assert.Equal(-75m + 75m, paper.Find("Total", 6)); // both lines of the entry, debit and credit
        Assert.Equal(2, paper.Rows.Count - 1);

        var big = await Run("builder", $"preset=Custom&from=2026-01-01&to=2026-12-31&accounts={_checking.Id}&min=500");
        Assert.Equal(1_000m, big.Find("Total", 6));
        var types = await Run("builder", "preset=Custom&from=2026-01-01&to=2026-12-31&types=Invoice");
        Assert.Single(types.Rows); // only the total
    }

    [Fact]
    public async Task Kpis_show_the_position_profit_and_months()
    {
        var k = await In<KpiService, KpiSnapshot>(s => s.SnapshotAsync(new DateOnly(2026, 1, 31)));
        Assert.Equal(525m, k.Cash);
        Assert.Equal((1_000m, 475m), (k.IncomeMonth, k.ExpensesMonth));
        Assert.Equal(525m, k.NetIncomeYear);
        Assert.Equal(12, k.Months.Count);
        Assert.Equal((new DateOnly(2026, 1, 1), 525m), (k.Months[^1].Month, k.Months[^1].Cash));
        Assert.Equal("Rent", k.TopExpenses[0].Name);
        Assert.Equal(100m, k.GrossMarginPercent);

        var report = await Run("kpis", "asOf=2026-02-28");
        Assert.Equal(125m, report.Find("Cash in the bank"));

        await In<DashboardService, bool>(async s => { await s.SetHiddenAsync(["ratios", "cash"]); return true; });
        Assert.Equal(["cash", "ratios"], (await In<DashboardService, IReadOnlySet<string>>(s => s.HiddenAsync())).Order());
    }

    [Fact]
    public async Task Scheduled_reports_are_emailed_once_per_slot_and_failures_are_kept()
    {
        var saved = await In<SavedReportService, SavedReport>(s => s.SaveAsync("Monthly P&L", "profit-and-loss", "preset=LastMonth"));
        _db.Clock.UtcNow = new DateTimeOffset(2026, 1, 10, 12, 0, 0, TimeSpan.Zero);
        var subscription = await In<ReportSubscriptionService, ReportSubscription>(s => s.CreateAsync(new ReportSubscriptionDetails(
            saved.Id, "owner@studio.test; partner@studio.test", ReportFrequency.Monthly, 1, 7, ReportFileFormat.Pdf)));
        Assert.Equal("owner@studio.test, partner@studio.test", subscription.Recipients);
        Assert.Empty(await In<ReportSubscriptionService, IReadOnlyList<SubscriptionRun>>(s => s.SendDueAsync()));

        _db.Clock.UtcNow = new DateTimeOffset(2026, 2, 2, 12, 0, 0, TimeSpan.Zero);
        var failed = Assert.Single(await In<ReportSubscriptionService, IReadOnlyList<SubscriptionRun>>(s => s.SendDueAsync()));
        Assert.Contains("Set up email", failed.Error, StringComparison.Ordinal);
        Assert.True((await In<ReportSubscriptionService, ReportSubscription?>(s => s.GetAsync(subscription.Id)))!.LastRunFailed);

        await In<EmailService, EmailSettings>(s => s.SaveSettingsAsync(new EmailSettingsCommand(
            EmailDelivery.Smtp, "smtp.test", 587, SmtpSecurity.Auto, "books", "s3cret-pass", false, "books@studio.test", "Studio", null)));
        var sent = await In<ReportSubscriptionService, SubscriptionRun>(s => s.SendNowAsync(subscription.Id));
        Assert.Null(sent.Error);
        Assert.Equal(2, _db.Mail.Sent.Count);
        var attachment = Assert.Single(_db.Mail.Sent[0].Message.Attachments);
        Assert.Equal(("Monthly P_L.pdf", "application/pdf"), (attachment.FileName, attachment.ContentType));
        Assert.Empty(await In<ReportSubscriptionService, IReadOnlyList<SubscriptionRun>>(s => s.SendDueAsync()));

        Assert.Equal("subscription.day_invalid", (await Assert.ThrowsAsync<DomainException>(() => In<ReportSubscriptionService, ReportSubscription>(s =>
            s.CreateAsync(new ReportSubscriptionDetails(saved.Id, "a@b.test", ReportFrequency.Monthly, 31, 7, ReportFileFormat.Csv))))).Code);
        Assert.Equal("email.address_invalid", (await Assert.ThrowsAsync<DomainException>(() => In<ReportSubscriptionService, ReportSubscription>(s =>
            s.CreateAsync(new ReportSubscriptionDetails(saved.Id, "not an address", ReportFrequency.Daily, 0, 7, ReportFileFormat.Csv))))).Code);
    }
}
