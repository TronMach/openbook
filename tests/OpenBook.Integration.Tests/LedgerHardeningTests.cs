using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OpenBook.Application;
using OpenBook.Application.Accounts;
using OpenBook.Application.Auditing;
using OpenBook.Application.Ledger;
using OpenBook.Application.Reporting;
using OpenBook.Domain.Accounts;
using OpenBook.Domain.Auditing;
using OpenBook.Domain.Common;
using OpenBook.Domain.Ledger;
using static OpenBook.Domain.Ledger.JournalLineDraft;

namespace OpenBook.Integration.Tests;

/// <summary>V1.1: closing date, year-end close, reversing and recurring entries, opening balances, templates, audit log.</summary>
public sealed class LedgerHardeningTests : IAsyncLifetime
{
    private const string ClosingPassword = "lock-2025";
    private static readonly DateOnly Dec15 = new(2025, 12, 15);
    private static readonly DateOnly Dec31 = new(2025, 12, 31);
    private static readonly DateOnly Jan15 = new(2026, 1, 15);

    private readonly LedgerDatabase _db = new();
    private Account _cash = null!;
    private Account _sales = null!;
    private Account _rent = null!;

    public async Task InitializeAsync()
    {
        await _db.InitializeAsync();
        _cash = await _db.CreateAccount("Checking", AccountType.Bank, "1000");
        _sales = await _db.CreateAccount("Sales", AccountType.Income, "4000");
        _rent = await _db.CreateAccount("Rent", AccountType.Expense, "6100");
    }

    public Task DisposeAsync() => _db.DisposeAsync();

    private Task<JournalEntry> Post(DateOnly date, string? closingPassword = null, DateOnly? reverseOn = null) =>
        _db.InScope(sp =>
        {
            sp.GetRequiredService<ClosedPeriodOverride>().Password = closingPassword;
            return sp.GetRequiredService<PostingEngine>().PostAsync(
                new PostJournalEntryCommand(date, [Dr(_cash.Id, 100m), Cr(_sales.Id, 100m)], "test", ReverseOn: reverseOn));
        });

    private Task<JournalEntry> Void(Guid id, string? closingPassword = null) =>
        _db.InScope(sp =>
        {
            sp.GetRequiredService<ClosedPeriodOverride>().Password = closingPassword;
            return sp.GetRequiredService<PostingEngine>().VoidAsync(id, "wrong");
        });

    private Task<ClosingStatus> SetClosing(DateOnly? date, string? newPassword = null, bool remove = false, string? current = null) =>
        _db.InScope(sp =>
        {
            sp.GetRequiredService<ClosedPeriodOverride>().Password = current;
            return sp.GetRequiredService<CloseBooksService>().SetAsync(date, newPassword, remove);
        });

    private static async Task<string> Refused(Func<Task> action) => (await Assert.ThrowsAsync<DomainException>(action)).Code;

    // ---- Closing date ----

    [Fact]
    public async Task Without_a_password_closed_periods_cannot_change()
    {
        await SetClosing(Dec31);

        Assert.Equal("period.closed", await Refused(() => Post(Dec15)));
        Assert.Equal("period.closed", await Refused(() => Post(Dec31)));
        await Post(Jan15);
    }

    [Fact]
    public async Task With_a_password_closed_period_changes_need_it_and_are_audited()
    {
        var entry = await Post(Dec15);
        await SetClosing(Dec31, ClosingPassword);

        Assert.Equal("period.password_required", await Refused(() => Post(Dec15)));
        Assert.Equal("period.wrong_password", await Refused(() => Post(Dec15, "guess")));
        Assert.Equal("period.password_required", await Refused(() => Void(entry.Id)));

        await Post(Dec15, ClosingPassword);
        await Void(entry.Id, ClosingPassword);

        var overrides = await _db.InDb(db => db.AuditRecords.Where(r => r.Action == AuditAction.ClosedPeriodOverride).ToListAsync());
        Assert.Equal(2, overrides.Count);
        Assert.All(overrides, r => Assert.Equal(LedgerDatabase.OwnerUsername, r.Actor));
        Assert.Contains(overrides, r => r.After!.Contains("voiding entry #1", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Reopening_or_changing_the_password_needs_the_current_password()
    {
        await SetClosing(new DateOnly(2025, 6, 30), ClosingPassword);

        // Closing more is always allowed.
        var moved = await SetClosing(Dec31);
        Assert.Equal(Dec31, moved.ClosingDate);
        Assert.True(moved.HasPassword);

        Assert.Equal("period.password_required", await Refused(() => SetClosing(new DateOnly(2025, 6, 30))));
        Assert.Equal("period.password_required", await Refused(() => SetClosing(null)));
        Assert.Equal("period.password_required", await Refused(() => SetClosing(Dec31, "new-password")));
        Assert.Equal("period.wrong_password", await Refused(() => SetClosing(Dec31, remove: true, current: "guess")));

        var removed = await SetClosing(Dec31, remove: true, current: ClosingPassword);
        Assert.False(removed.HasPassword);
        // With no password left, reopening needs nothing.
        Assert.Null((await SetClosing(null)).ClosingDate);
    }

    [Fact]
    public async Task Closing_password_must_be_long_enough()
    {
        Assert.Equal("period.password_too_short", await Refused(() => SetClosing(Dec31, "abc")));
    }

    [Fact]
    public async Task Closing_a_year_moves_the_closing_date_only()
    {
        await Post(Dec15);
        var close = (DateOnly end) => _db.InScope(sp => sp.GetRequiredService<CloseBooksService>().CloseYearAsync(end));

        // The clock is Jan 31, 2026 and the fiscal year is the calendar year.
        Assert.Equal("period.not_year_end", await Refused(() => close(new DateOnly(2025, 6, 30))));
        Assert.Equal("period.year_not_over", await Refused(() => close(new DateOnly(2026, 12, 31))));

        var entriesBefore = await _db.InDb(db => db.JournalEntries.CountAsync());
        var status = await close(Dec31);

        Assert.Equal(Dec31, status.ClosingDate);
        Assert.Equal(entriesBefore, await _db.InDb(db => db.JournalEntries.CountAsync()));

        var years = await _db.InScope(sp => sp.GetRequiredService<CloseBooksService>().FiscalYearsAsync(new DateOnly(2026, 1, 31)));
        Assert.Equal([(new DateOnly(2026, 1, 1), false), (new DateOnly(2025, 1, 1), true)], years.Select(y => (y.Start, y.IsClosed)));
        Assert.Equal(100m, years[1].NetIncome);
    }

    // ---- Reversing entries ----

    [Fact]
    public async Task Reverse_on_posts_a_mirror_entry_on_that_date()
    {
        var accrual = await Post(Dec31, reverseOn: new DateOnly(2026, 1, 1));

        var (reversals, _) = await _db.InScope(sp => sp.GetRequiredService<PostingEngine>().ListAsync(
            new JournalEntryFilter(Source: JournalEntry.AutoReversalSource, SourceId: accrual.Id)));
        var reversal = Assert.Single(reversals);

        Assert.Equal(new DateOnly(2026, 1, 1), reversal.Date);
        Assert.Equal(100m, reversal.Lines.Single(l => l.AccountId == _sales.Id).Debit);
        Assert.Equal(100m, reversal.Lines.Single(l => l.AccountId == _cash.Id).Credit);

        var tb = await _db.InScope(sp => sp.GetRequiredService<TrialBalanceQuery>().RunAsync(new DateOnly(2026, 1, 1)));
        Assert.Equal(0m, tb.TotalDebits.Amount);
    }

    [Fact]
    public async Task Reverse_on_must_be_after_the_entry_date()
    {
        Assert.Equal("journal.reverse_date", await Refused(() => Post(Dec31, reverseOn: Dec31)));
        Assert.Equal(0, await _db.InDb(db => db.JournalEntries.CountAsync()));
    }

    [Fact]
    public async Task Auto_reversal_entries_can_be_voided_on_their_own()
    {
        var accrual = await Post(Dec31, reverseOn: new DateOnly(2026, 1, 1));
        var reversalId = await _db.InDb(db => db.JournalEntries.Where(e => e.SourceId == accrual.Id).Select(e => e.Id).SingleAsync());

        await Void(reversalId);
    }

    // ---- Recurring entries ----

    private Task<RecurringEntry> CreateRecurring(DateOnly start, int? max = null, DateOnly? end = null, bool autoPost = false) =>
        _db.InScope(sp => sp.GetRequiredService<RecurringEntryService>().CreateAsync(new RecurringEntryDetails(
            "Office rent", [Dr(_rent.Id, 1200m), Cr(_cash.Id, 1200m)], RecurrenceFrequency.Monthly, start,
            MaxOccurrences: max, EndDate: end, AutoPost: autoPost)));

    private Task<IReadOnlyList<RecurringPostResult>> PostDue(DateOnly today, bool autoPostOnly = false) =>
        _db.InScope(sp => sp.GetRequiredService<RecurringEntryService>().PostDueAsync(today, autoPostOnly));

    private Task<List<DateOnly>> RecurringDates() =>
        _db.InDb(async db => (await db.JournalEntries.Where(e => e.Source == JournalEntry.RecurringSource).Select(e => e.Date).ToListAsync())
            .Order().ToList());

    [Fact]
    public async Task Recurring_entries_catch_up_without_drifting_off_month_end()
    {
        var template = await CreateRecurring(new DateOnly(2025, 10, 31));

        var result = Assert.Single(await PostDue(new DateOnly(2026, 1, 31)));

        Assert.Null(result.Error);
        Assert.Equal(
            [new DateOnly(2025, 10, 31), new DateOnly(2025, 11, 30), new DateOnly(2025, 12, 31), new DateOnly(2026, 1, 31)],
            await RecurringDates());
        var saved = await _db.InScope(sp => sp.GetRequiredService<RecurringEntryService>().GetAsync(template.Id));
        Assert.Equal(4, saved!.OccurrencesPosted);
        Assert.Equal(new DateOnly(2026, 2, 28), saved.NextDate);

        // Running again posts nothing new.
        Assert.Empty(await PostDue(new DateOnly(2026, 1, 31)));
    }

    [Fact]
    public async Task Recurring_entries_stop_at_the_end_date_or_occurrence_count()
    {
        await CreateRecurring(new DateOnly(2025, 10, 1), max: 2);
        await CreateRecurring(new DateOnly(2025, 10, 15), end: new DateOnly(2025, 11, 20));

        await PostDue(new DateOnly(2026, 1, 31));

        Assert.Equal(
            [new DateOnly(2025, 10, 1), new DateOnly(2025, 10, 15), new DateOnly(2025, 11, 1), new DateOnly(2025, 11, 15)],
            await RecurringDates());
    }

    [Fact]
    public async Task Background_run_posts_only_automatic_templates()
    {
        await CreateRecurring(new DateOnly(2026, 1, 1));
        await CreateRecurring(new DateOnly(2026, 1, 5), autoPost: true);

        await PostDue(new DateOnly(2026, 1, 31), autoPostOnly: true);

        Assert.Equal([new DateOnly(2026, 1, 5)], await RecurringDates());
    }

    [Fact]
    public async Task A_closed_period_stops_a_template_and_reports_why()
    {
        await CreateRecurring(new DateOnly(2025, 11, 30));
        await SetClosing(Dec31);

        var result = Assert.Single(await PostDue(new DateOnly(2026, 1, 31)));

        Assert.Empty(result.Posted);
        Assert.Contains("closed", result.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(await RecurringDates());
    }

    [Fact]
    public async Task Inactive_templates_do_not_post()
    {
        var template = await CreateRecurring(new DateOnly(2026, 1, 1));
        await _db.InScope(sp => sp.GetRequiredService<RecurringEntryService>().SetActiveAsync(template.Id, false));

        Assert.Empty(await PostDue(new DateOnly(2026, 1, 31)));
    }

    // ---- Opening balances ----

    [Fact]
    public async Task Opening_balances_put_the_difference_in_opening_balance_equity()
    {
        var loan = await _db.CreateAccount("Truck Loan", AccountType.LongTermLiability);
        var service = (IServiceProvider sp) => sp.GetRequiredService<OpeningBalanceService>();

        var entry = await _db.InScope(sp => service(sp).PostAsync(Dec31, [new(_cash.Id, 1000m), new(loan.Id, 400m)]));

        var equity = await _db.InDb(db => db.Accounts.SingleAsync(a => a.Name == OpeningBalanceService.EquityAccountName));
        Assert.Equal(AccountType.Equity, equity.Type);
        Assert.Equal(1000m, entry.Lines.Single(l => l.AccountId == _cash.Id).Debit);
        Assert.Equal(400m, entry.Lines.Single(l => l.AccountId == loan.Id).Credit);
        Assert.Equal(600m, entry.Lines.Single(l => l.AccountId == equity.Id).Credit);
        Assert.Equal(JournalEntry.OpeningBalanceSource, entry.Source);
        Assert.Equal([entry.Id], (await _db.InScope(sp => service(sp).ExistingAsync())).Select(e => e.Id));

        // A second batch reuses the same equity account.
        await _db.InScope(sp => service(sp).PostAsync(Dec31, [new(_cash.Id, 50m)]));
        Assert.Equal(1, await _db.InDb(db => db.Accounts.CountAsync(a => a.Name == OpeningBalanceService.EquityAccountName)));
    }

    [Fact]
    public async Task Receivables_and_income_take_no_opening_balance()
    {
        var receivable = await _db.CreateAccount("Accounts Receivable", AccountType.AccountsReceivable);
        var service = (IServiceProvider sp) => sp.GetRequiredService<OpeningBalanceService>();

        Assert.Equal("opening.account_not_eligible", await Refused(() => _db.InScope(sp => service(sp).PostAsync(Dec31, [new(receivable.Id, 10m)]))));
        Assert.Equal("opening.account_not_eligible", await Refused(() => _db.InScope(sp => service(sp).PostAsync(Dec31, [new(_sales.Id, 10m)]))));
        Assert.Equal("opening.nothing_entered", await Refused(() => _db.InScope(sp => service(sp).PostAsync(Dec31, [new(_cash.Id, 0m)]))));

        var eligible = await _db.InScope(sp => service(sp).EligibleAccountsAsync());
        Assert.Equal([_cash.Id], eligible.Select(a => a.Id));
    }

    // ---- Audit log ----

    [Fact]
    public async Task Audit_log_filters_by_record_and_shows_changed_fields()
    {
        var template = await CreateRecurring(new DateOnly(2026, 2, 1));
        await _db.InScope(sp => sp.GetRequiredService<RecurringEntryService>().SetActiveAsync(template.Id, false));

        var page = await _db.InScope(sp => sp.GetRequiredService<AuditLogQuery>().QueryAsync(new AuditFilter(EntityId: template.Id)));

        Assert.Equal(2, page.Total);
        var update = page.Entries[0];
        Assert.Equal(AuditAction.Updated, update.Record.Action);
        var change = Assert.Single(update.Changes);
        Assert.Equal(("IsActive", "true", "false"), (change.Field, change.Before, change.After));
        Assert.Contains("recurring_entry", page.EntityTypes);
        Assert.Contains(LedgerDatabase.OwnerUsername, page.Actors);
    }

    [Fact]
    public void Audit_diff_lists_only_changed_fields()
    {
        var changes = AuditLogQuery.Changes("""{"A":1,"B":"x","C":null}""", """{"A":1,"B":"y","D":true}""");

        Assert.Equal(
            [("B", "\"x\"", "\"y\""), ("D", null, "true")],
            changes.Select(c => (c.Field, c.Before, c.After)).OrderBy(c => c.Field));
        Assert.Equal(2, AuditLogQuery.Changes(null, """{"A":1,"B":2}""").Count);
    }
}

/// <summary>Starter charts and how retained earnings shows on the balance sheet.</summary>
public sealed class ChartTemplateTests : IAsyncLifetime
{
    private readonly LedgerDatabase _db = new();

    public Task InitializeAsync() => _db.InitializeAsync();

    public Task DisposeAsync() => _db.DisposeAsync();

    [Theory]
    [InlineData("sole-proprietor")]
    [InlineData("partnership")]
    [InlineData("corporation")]
    [InlineData("nonprofit")]
    public async Task Every_template_imports_cleanly(string key)
    {
        var preview = await _db.InScope(sp => sp.GetRequiredService<ChartTemplates>().PreviewAsync(key));
        Assert.Equal(0, preview.ErrorCount);
        Assert.True(preview.NewCount > 20);

        var applied = await _db.InScope(sp => sp.GetRequiredService<ChartTemplates>().ApplyAsync(key));
        Assert.Equal(preview.NewCount, applied.NewCount);
        Assert.Equal(applied.NewCount, await _db.InDb(db => db.Accounts.CountAsync()));
        Assert.True(await _db.InDb(db => db.Accounts.AnyAsync(a => a.Name == "Retained Earnings" && a.Type == AccountType.Equity && a.ParentId == null)));
    }

    [Fact]
    public void Unknown_templates_are_refused()
    {
        Assert.Equal("template.unknown", Assert.Throws<DomainException>(() => ChartTemplates.Csv("nope")).Code);
        Assert.All(ChartTemplates.All, t => Assert.NotEmpty(ChartTemplates.Csv(t.Key)));
    }

    [Fact]
    public async Task Prior_years_income_shows_on_the_retained_earnings_account()
    {
        await _db.InScope(sp => sp.GetRequiredService<ChartTemplates>().ApplyAsync("sole-proprietor"));
        var accounts = await _db.InDb(db => db.Accounts.AsNoTracking().ToListAsync());
        var bank = accounts.First(a => a.Type == AccountType.Bank);
        var income = accounts.First(a => a.Type == AccountType.Income);
        var retained = accounts.Single(a => a.Name == "Retained Earnings");

        await _db.InScope(sp => sp.GetRequiredService<PostingEngine>().PostAsync(
            new PostJournalEntryCommand(new DateOnly(2025, 6, 1), [Dr(bank.Id, 500m), Cr(income.Id, 500m)])));
        await _db.InScope(sp => sp.GetRequiredService<PostingEngine>().PostAsync(
            new PostJournalEntryCommand(new DateOnly(2026, 1, 10), [Dr(bank.Id, 70m), Cr(income.Id, 70m)])));

        var sheet = await _db.InScope(sp => sp.GetRequiredService<FinancialReports>().BalanceSheetAsync(new DateOnly(2026, 1, 31), ReportBasis.Accrual));

        Assert.True(sheet.IsBalanced);
        var retainedRow = Assert.Single(sheet.Equity.Rows, r => r.AccountId == retained.Id);
        Assert.Equal(500m, retainedRow.Amount);
        Assert.DoesNotContain(sheet.Equity.Rows, r => r.Kind == RowKind.Computed && r.Label == "Retained earnings");
        Assert.Equal(70m, sheet.Equity.Rows.Single(r => r.Label == "Net income").Amount);
    }
}
