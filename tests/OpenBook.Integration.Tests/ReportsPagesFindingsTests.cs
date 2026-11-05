using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OpenBook.Application.Assets;
using OpenBook.Application.Forms;
using OpenBook.Application.Reporting;
using OpenBook.Application.Sales;
using OpenBook.Application.Search;
using OpenBook.Domain.Accounts;
using OpenBook.Domain.Assets;
using OpenBook.Domain.Common;
using OpenBook.Domain.Companies;
using OpenBook.Domain.Email;
using OpenBook.Domain.Reporting;

namespace OpenBook.Integration.Tests;

/// <summary>The audit's Medium findings on reports and pages.</summary>
public sealed class ReportsPagesFindingsTests : IAsyncLifetime
{
    private readonly LedgerDatabase _db = new();

    public Task InitializeAsync() => _db.InitializeAsync();

    public Task DisposeAsync() => _db.DisposeAsync();

    private Task<T> In<TService, T>(Func<TService, Task<T>> work) where TService : notnull =>
        _db.InScope(sp => work(sp.GetRequiredService<TService>()));

    private Task<EmailSettings> SetUpEmail() =>
        In<EmailService, EmailSettings>(s => s.SaveSettingsAsync(new EmailSettingsCommand(
            EmailDelivery.Smtp, "smtp.test", 587, SmtpSecurity.Auto, "books", "pw", false, "books@studio.test", "Studio", null, null)));

    private async Task<ReportSubscription> Schedule(string recipients)
    {
        var saved = await In<SavedReportService, SavedReport>(s => s.SaveAsync("Monthly", "profit-and-loss", "preset=ThisMonth"));
        return await In<ReportSubscriptionService, ReportSubscription>(s => s.CreateAsync(new ReportSubscriptionDetails(saved.Id, recipients, ReportFrequency.Daily, 0, 8, ReportFileFormat.Csv)));
    }

    [Fact]
    public async Task A_failed_recipient_stops_nobody_and_is_tried_again_alone()
    {
        await SetUpEmail();
        var schedule = await Schedule("a@b.test, c@d.test");
        _db.Mail.Failure = "The server said no";

        var first = await In<ReportSubscriptionService, SubscriptionRun>(s => s.SendNowAsync(schedule.Id));
        Assert.NotNull(first.Error);
        Assert.Equal("a@b.test, c@d.test", first.Subscription.RetryRecipients);

        _db.Mail.Failure = null;
        var second = await In<ReportSubscriptionService, SubscriptionRun>(s => s.SendNowAsync(schedule.Id));

        Assert.Null(second.Error);
        Assert.Null(second.Subscription.RetryRecipients);
        Assert.Equal(2, _db.Mail.Sent.Count);
    }

    [Fact]
    public async Task Saving_a_report_as_of_today_keeps_it_as_of_the_day_it_runs()
    {
        var today = OpenBook.Application.CompanyTime.Today;

        var saved = await In<SavedReportService, SavedReport>(s => s.SaveAsync("Sheet", "balance-sheet", $"asOf={today:yyyy-MM-dd}&basis=Accrual"));

        Assert.DoesNotContain("asOf", saved.Query, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_loans_report_can_look_back()
    {
        var bank = await _db.CreateAccount("Checking", AccountType.Bank, "1000");
        var liability = await _db.CreateAccount("Bank Loan", AccountType.LongTermLiability, "2500");
        var loan = await In<LoanService, Loan>(s => s.CreateAsync(new CreateLoanCommand(
            new LoanDetails("Van", liability.Id, Guid.Empty, 1_200m, 0m, 12, new DateOnly(2026, 2, 1)), null, null)));
        await In<LoanService, LoanPayment>(s => s.PayAsync(loan.Id, new LoanPaymentCommand(new DateOnly(2026, 3, 1), bank.Id)));

        var before = await In<AssetReports, ReportTable>(s => s.LoansAsync(new DateOnly(2026, 2, 15)));
        var after = await In<AssetReports, ReportTable>(s => s.LoansAsync(new DateOnly(2026, 3, 15)));

        Assert.Equal(1_200m, before.Rows[0].Cells[^1].Value);
        Assert.Equal(1_100m, after.Rows[0].Cells[^1].Value);
    }

    [Fact]
    public async Task A_very_long_number_searches_as_text()
    {
        var results = await In<SearchService, SearchResults>(s => s.SearchAsync("123456789012345678901234"));

        Assert.Empty(results.Hits);
    }

    [Fact]
    public async Task A_report_too_long_for_a_pdf_says_so()
    {
        var table = new ReportTable("Long", [], [new ReportColumn("Name")], Enumerable.Range(0, 5_001).Select(i => new ReportTableRow([$"Row {i}"])).ToList());

        var code = await _db.InScope(sp => Task.FromResult(Assert.Throws<DomainException>(() => sp.GetRequiredService<IReportExporter>().ToPdf(table)).Code));

        Assert.Equal("report.too_large_for_pdf", code);
    }
}
