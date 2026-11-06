using System.IO.Compression;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using OpenBook.Application;
using OpenBook.Application.Accounts;
using OpenBook.Application.Banking;
using OpenBook.Application.Companies;
using OpenBook.Application.Forms;
using OpenBook.Application.Import;
using OpenBook.Application.Inventory;
using OpenBook.Application.Ledger;
using OpenBook.Application.Nonprofit;
using OpenBook.Application.Reporting;
using OpenBook.Application.Sales;
using OpenBook.Application.Search;
using OpenBook.Application.Segments;
using OpenBook.Domain.Accounts;
using OpenBook.Domain.Banking;
using OpenBook.Domain.Common;
using OpenBook.Domain.Companies;
using OpenBook.Domain.Ledger;
using OpenBook.Domain.Sales;
using OpenBook.Domain.Segments;
using OpenBook.Infrastructure.Persistence;

namespace OpenBook.Integration.Tests;

/// <summary>Small refusals and limits from the review's low findings: each a thing that used to fail badly or quietly.</summary>
public sealed class SmallFixTests : IAsyncLifetime
{
    private static readonly DateOnly Jan5 = new(2026, 1, 5);

    private readonly LedgerDatabase _db = new();
    private Account _checking = null!;
    private Account _expense = null!;

    public async Task InitializeAsync()
    {
        await _db.InitializeAsync();
        _checking = await _db.CreateAccount("Checking", AccountType.Bank, "1000");
        _expense = await _db.CreateAccount("Supplies", AccountType.Expense, "6000");
    }

    public Task DisposeAsync() => _db.DisposeAsync();

    private Task<T> In<TService, T>(Func<TService, Task<T>> work) where TService : notnull =>
        _db.InScope(sp => work(sp.GetRequiredService<TService>()));

    private static async Task<string> Refused(Func<Task> action) => (await Assert.ThrowsAsync<DomainException>(action)).Code;

    private Task<OpenBook.Domain.Ledger.JournalEntry> Post(decimal amount, string? memo = null) =>
        In<PostingEngine, OpenBook.Domain.Ledger.JournalEntry>(s => s.PostAsync(new PostJournalEntryCommand(Jan5,
            [JournalLineDraft.Dr(_expense.Id, amount), JournalLineDraft.Cr(_checking.Id, amount)], memo)));

    private Task<ClosingStatus> CloseThroughJanuary() =>
        In<CloseBooksService, ClosingStatus>(s => s.SetAsync(new DateOnly(2026, 1, 31), newPassword: null, removePassword: false));

    [Fact]
    public async Task An_amount_too_large_to_store_is_refused_rather_than_failing_the_save()
    {
        Assert.Equal("money.too_large", await Refused(() => Post(100_000_000_000_000_000m)));
        Assert.Equal("sales.rate_too_large", Assert.Throws<DomainException>(() => SalesLine.ValidRate(10_000_000_000_000m)).Code);
        Assert.Equal("sales.quantity_too_large", Assert.Throws<DomainException>(() => SalesLine.ValidQuantity(10_000_000_000_000m)).Code);
    }

    [Fact]
    public async Task A_text_search_doesnt_list_entries_for_having_a_line_of_one_dollar()
    {
        await Post(1m);
        var results = await In<SearchService, SearchResults>(s => s.SearchAsync("zzz"));
        Assert.Empty(results.Hits);
    }

    [Fact]
    public async Task A_petty_cash_box_refused_for_a_closed_date_leaves_nothing_behind()
    {
        await CloseThroughJanuary();
        var command = new CreatePettyCashFundCommand(null, "Front desk box", 100m, "Pat", _checking.Id, Jan5);
        Assert.Equal("period.closed", await Refused(() => In<PettyCashService, PettyCashFund>(s => s.CreateAsync(command))));
        var accounts = await In<AccountService, IReadOnlyList<Account>>(s => s.ListAsync());
        Assert.DoesNotContain(accounts, a => a.Name == "Front desk box");
        Assert.Equal(0, await _db.InDb(db => Task.FromResult(db.PettyCashFunds.Count())));
    }

    [Fact]
    public async Task A_grant_refused_for_a_closed_date_leaves_no_class_behind()
    {
        var funder = await In<CustomerService, Customer>(s => s.CreateAsync(new CustomerDetails("Foundation")));
        await CloseThroughJanuary();
        var command = new GrantCommand("Library fund", funder.Id, 1000m, Jan5, Jan5, new DateOnly(2026, 12, 31));
        Assert.Equal("period.closed", await Refused(() => In<GrantService, OpenBook.Domain.Nonprofit.Grant>(s => s.CreateAsync(command))));
        Assert.Equal(0, await _db.InDb(db => Task.FromResult(db.Segments.Count())));
    }

    [Fact]
    public async Task No_password_hash_or_protected_secret_goes_out_in_the_data_export()
    {
        var file = await _db.InScope(sp => sp.GetRequiredService<IDataExporter>().ExportAsync(CancellationToken.None));
        using var zip = ZipFile.OpenRead(file.Path);
        foreach (var entry in zip.Entries.Where(e => e.FullName.EndsWith(".csv", StringComparison.Ordinal)))
        {
            using var reader = new StreamReader(entry.Open());
            var header = (await reader.ReadLineAsync() ?? string.Empty).Split(',');
            Assert.DoesNotContain(header, c => c.Trim('"').EndsWith("PasswordHash", StringComparison.Ordinal) || c.Trim('"').StartsWith("Protected", StringComparison.Ordinal));
        }
    }

    [Fact]
    public void A_stripe_timestamp_past_the_calendar_is_a_bad_signature_not_a_failure() =>
        Assert.False(StripeSignature.IsValid("{}", "t=99999999999999999,v1=x", "secret", DateTimeOffset.UtcNow));

    [Fact]
    public async Task A_key_set_to_expire_today_works_until_the_day_ends()
    {
        var today = CompanyTime.TodayAt(_db.Clock.UtcNow, CompanyTime.Zone);
        var created = await In<ApiKeyService, CreatedApiKey>(s => s.CreateAsync(_db.Owner!.Id, "Today", today));
        Assert.True(created.Key.IsUsable(_db.Clock.UtcNow));
    }

    [Fact]
    public async Task A_year_past_what_reports_can_read_is_refused_and_dates_are_held_in_range()
    {
        var query = ReportParameters.Parse("year=0&to=9999-12-31&from=0001-01-01");
        Assert.Equal("report.year_range", Assert.Throws<DomainException>(() => query.GetYear("year")).Code);
        Assert.Equal(new DateOnly(DateRanges.LastYear, 12, 31), query.GetDate("to"));
        Assert.Equal(new DateOnly(DateRanges.FirstYear, 1, 1), query.GetDate("from"));
        Assert.Equal("report.year_range", await Refused(() => In<ReportCatalog, ReportTable>(s => s.RunAsync("depreciation-schedule", query))));
    }

    [Fact]
    public async Task Too_many_labels_are_refused_before_anything_is_built()
    {
        var income = await _db.CreateAccount("Sales", AccountType.Income, "4000");
        var item = await In<ItemService, Item>(s => s.CreateAsync(new ItemDetails("Widget", ItemType.Service, income.Id, 5m, Taxable: false, Barcode: "12345678")));
        Assert.Equal("label.too_many", await Refused(() => In<LabelService, byte[]>(s => s.RenderAsync([(item.Id, 2_000_000_000)], showPrice: false))));
    }

    [Fact]
    public async Task A_damaged_logo_is_refused_when_it_comes_in()
    {
        var design = (await In<FormDesignService, IReadOnlyList<FormDesign>>(s => s.ListAsync()))[0];
        byte[] damaged = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, .. new byte[8], 0, 0, 0, 16, 0, 0, 0, 16, .. new byte[100]];
        Assert.Equal("formdesign.logo_unreadable", await Refused(() => In<FormDesignService, FormDesign>(s => s.SetLogoAsync(design.Id, damaged))));
    }

    [Fact]
    public void Goods_received_are_named_in_reports() =>
        Assert.Equal("Goods received", ReportCatalog.DocumentType(OpenBook.Application.Purchases.PurchaseOrderService.ReceiptSource));

    [Fact]
    public void An_ofx_line_left_open_in_the_middle_is_kept_and_a_bad_balance_doesnt_stop_the_file()
    {
        var statement = Ofx.Parse("<OFX><STMTTRN><DTPOSTED>20260101<TRNAMT>-1.00<FITID>A<STMTTRN><DTPOSTED>20260102<TRNAMT>-2.00<FITID>B</STMTTRN>"
            + "<LEDGERBAL><BALAMT>12.345<DTASOF>2026</LEDGERBAL></OFX>");
        Assert.Equal(["A", "B"], statement.Transactions.Select(t => t.FitId));
        Assert.Null(statement.LedgerBalance);
    }

    [Fact]
    public void A_backup_in_the_hour_clocks_repeat_runs_once()
    {
        var eastern = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
        var schedule = BackupSchedule.Default();
        schedule.Update(true, BackupFrequency.Daily, DayOfWeek.Sunday, 1, BackupDestination.Folder, "C:/backups", null, null, null, null, null, null, 14);
        schedule.Started(new DateTimeOffset(2026, 11, 1, 5, 0, 0, TimeSpan.Zero));
        Assert.False(schedule.IsDue(new DateTimeOffset(2026, 11, 1, 5, 15, 0, TimeSpan.Zero), eastern));
        Assert.False(schedule.IsDue(new DateTimeOffset(2026, 11, 1, 6, 15, 0, TimeSpan.Zero), eastern));
    }

    [Fact]
    public async Task A_correction_keeps_the_tags_on_a_transfer_and_a_payment()
    {
        var savings = await _db.CreateAccount("Savings", AccountType.Bank, "1010");
        var tag = await In<SegmentService, Segment>(s => s.CreateAsync(SegmentKind.Tag, "Audit 2026", null));
        var transfer = await In<BankingService, BankTransaction>(s => s.TransferAsync(new TransferCommand(_checking.Id, savings.Id, Jan5, 50m, "First")));
        await In<SegmentService, object?>(async s =>
        {
            await s.SetTagsAsync(transfer.JournalEntryId, [tag.Id]);
            return null;
        });

        var changed = await In<BankingService, BankTransaction>(s => s.ChangeTransferAsync(transfer.Id, new ChangeTransferCommand(_checking.Id, savings.Id, Jan5, 50m, "Second", "Memo wrong")));

        var tags = await _db.InScope(sp => sp.GetRequiredService<ILedgerStore>().GetEntryTagsAsync([changed.JournalEntryId], default));
        Assert.Equal(tag.Id, Assert.Single(tags).TagId);
    }

    [Fact]
    public async Task A_stale_check_voided_later_stays_on_the_old_reconciliation_report()
    {
        await Post(5m, "Before the statement");
        var spend = await In<BankingService, BankTransaction>(s => s.RecordAsync(new RecordBankTransactionCommand(
            BankTransactionKind.Spend, _checking.Id, Jan5, [new BankSplit(_expense.Id, 30m)], "Payee", "1002")));
        var started = await In<ReconciliationService, Reconciliation>(s => s.StartAsync(_checking.Id, new DateOnly(2026, 1, 31), -5m));
        var worksheet = await In<ReconciliationService, ReconciliationWorksheet>(s => s.GetWorksheetAsync(started.Id));
        var ticks = worksheet.Items.Where(i => i.NaturalAmount == -5m).Select(i => i.Line.JournalLineId).ToList();
        await In<ReconciliationService, ReconciliationWorksheet>(s => s.SetClearedAsync(started.Id, ticks, true));
        await In<ReconciliationService, Reconciliation>(s => s.CompleteAsync(started.Id));
        var before = await In<ReconciliationService, ReconciliationReport>(s => s.GetReportAsync(started.Id));
        Assert.Single(before.Outstanding);

        _db.Clock.UtcNow = _db.Clock.UtcNow.AddDays(60);
        await In<BankingService, BankTransaction>(s => s.VoidAsync(spend.Id, "Stale"));

        var after = await In<ReconciliationService, ReconciliationReport>(s => s.GetReportAsync(started.Id));
        Assert.Single(after.Outstanding);
        Assert.Equal(before.RegisterBalance, after.RegisterBalance);
    }

    [Fact]
    public void A_stub_past_ten_bills_ends_with_one_line_that_keeps_the_total()
    {
        var lines = Enumerable.Range(1, 14).Select(i => ($"Bill {i}", 10m)).ToList();
        var rows = CheckForm.StubRowsOf(lines);
        Assert.Equal(10, rows.Count);
        Assert.Equal(140m, rows.Sum(r => r.Amount));
        Assert.Equal("5 more bills", rows[^1].Description);
    }

    [SqliteFact]
    public async Task An_encrypted_file_stays_in_write_ahead_mode()
    {
        var folder = Path.Combine(_db.BackupDirectory, "wal");
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, "company.db");
        var plain = new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString();
        await using (var connection = new SqliteConnection(plain))
        {
            await connection.OpenAsync();
            await using var create = connection.CreateCommand();
            create.CommandText = "PRAGMA journal_mode = WAL; CREATE TABLE T (Id INTEGER);";
            await create.ExecuteNonQueryAsync();
        }

        await SqliteEncryption.EncryptAsync(path, "a key");
        await using var opened = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false, Password = "a key" }.ToString());
        await opened.OpenAsync();
        await using var read = opened.CreateCommand();
        read.CommandText = "PRAGMA journal_mode;";
        Assert.Equal("wal", await read.ExecuteScalarAsync());
    }
}
