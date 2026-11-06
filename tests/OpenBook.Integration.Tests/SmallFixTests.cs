using System.IO.Compression;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using OpenBook.Application;
using OpenBook.Application.Accounts;
using OpenBook.Application.Banking;
using OpenBook.Application.Channels;
using OpenBook.Application.Companies;
using OpenBook.Application.Forms;
using OpenBook.Application.Import;
using OpenBook.Application.Import.Migration;
using OpenBook.Application.Inventory;
using OpenBook.Application.Ledger;
using OpenBook.Application.Nonprofit;
using OpenBook.Application.Reporting;
using OpenBook.Application.Sales;
using OpenBook.Application.Search;
using OpenBook.Application.Segments;
using OpenBook.Domain.Accounts;
using OpenBook.Domain.Banking;
using OpenBook.Domain.Channels;
using OpenBook.Domain.Common;
using OpenBook.Domain.Inventory;
using OpenBook.Domain.Companies;
using OpenBook.Domain.Ledger;
using OpenBook.Domain.Sales;
using OpenBook.Domain.SalesTax;
using OpenBook.Domain.Segments;
using OpenBook.Infrastructure.Persistence;

namespace OpenBook.Integration.Tests;

/// <summary>Small refusals and limits from the review's low findings: each a thing that used to fail badly or quietly.</summary>
public sealed class SmallFixTests : IAsyncLifetime
{
    private static readonly DateOnly Jan5 = new(2026, 1, 5);
    private static readonly decimal[] RatesWithAZero = [2m, 2m, 2m, 0m];

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

    [Fact]
    public async Task A_closing_password_needs_a_closing_date_to_protect()
    {
        Assert.Equal("period.password_needs_date", await Refused(() => In<CloseBooksService, ClosingStatus>(s => s.SetAsync(null, "secret-word", removePassword: false))));
    }

    [Theory]
    [InlineData("Property", "Properties")]
    [InlineData("Store", "Stores")]
    [InlineData("Branch", "Branches")]
    [InlineData("Bay", "Bays")]
    public void Names_people_choose_get_a_proper_plural(string word, string plural) =>
        Assert.Equal(plural, OpenBook.Web.Components.Labels.Plural(word));

    [Fact]
    public void A_lot_name_may_hold_the_separator_when_no_quantity_follows_it()
    {
        Assert.Equal([("A x B", 5m)], LotText.Parse("A x B", ItemTracking.Lot, 5m, "Widget"));
        Assert.Equal([("A x B", 3m), ("C", 2m)], LotText.Parse("A x B x 3; C*2", ItemTracking.Lot, 5m, "Widget"));
    }

    [Fact]
    public void A_list_cut_to_fit_a_line_stands_for_what_was_recorded()
    {
        var cut = new string('S', 997) + "...";
        Assert.Empty(LotText.Parse(cut, ItemTracking.Serial, 150m, "Widget"));
    }

    [Fact]
    public void A_damaged_compressed_file_is_reported_as_unreadable()
    {
        byte[] damaged = [0x1F, 0x8B, 0x08, 0x00, 1, 2, 3, 4, 5, 6, 7, 8, 9];
        Assert.Equal("import.unreadable", Assert.Throws<DomainException>(() => MigrationReaders.Read(MigrationSource.GnuCash, damaged, "USD")).Code);
    }

    [Fact]
    public void Item_prices_keep_four_decimals_and_a_negative_price_stays_out()
    {
        const string iif = "!INVITEM\tNAME\tINVITEMTYPE\tACCNT\tPRICE\nINVITEM\tBolt\tSERV\tSales\t0.125\nINVITEM\tCredit\tSERV\tSales\t-10.00\n";
        var data = MigrationReaders.Read(MigrationSource.QuickBooksIif, System.Text.Encoding.UTF8.GetBytes(iif), "USD");
        Assert.Equal(0.125m, data.Items.Single(i => i.Name == "Bolt").Price);
        Assert.Null(data.Items.Single(i => i.Name == "Credit").Type);
    }

    [Fact]
    public void Iif_dates_can_be_day_first_and_children_can_come_before_parents()
    {
        const string iif = "!ACCNT\tNAME\tACCNTTYPE\nACCNT\tBank:Sub\tBANK\nACCNT\tBank\tBANK\n"
            + "!TRNS\tTRNSTYPE\tDATE\tACCNT\tAMOUNT\n!SPL\tTRNSTYPE\tDATE\tACCNT\tAMOUNT\n!ENDTRNS\n"
            + "TRNS\tGENERAL JOURNAL\t25/01/2026\tBank\t10\nSPL\tGENERAL JOURNAL\t25/01/2026\tBank:Sub\t-10\nENDTRNS\n";
        var data = MigrationReaders.Read(MigrationSource.QuickBooksIif, System.Text.Encoding.UTF8.GetBytes(iif), "USD");
        Assert.Equal(new DateOnly(2026, 1, 25), Assert.Single(data.Transactions).Date);
        Assert.Equal(["Bank", "Bank:Sub"], data.Accounts.Select(a => a.FullName));
    }

    [Fact]
    public void A_field_full_of_quotes_reads_in_linear_time()
    {
        var text = "a,b\n" + string.Concat(Enumerable.Repeat("a\"", 1_000_000)) + ",2\n";
        var clock = System.Diagnostics.Stopwatch.StartNew();
        Assert.Equal(2, Csv.Parse(text).Count);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(20), $"{clock.Elapsed} to read it.");
    }

    [Fact]
    public async Task Voided_opening_balances_leave_nothing_listed()
    {
        var entry = await In<OpeningBalanceService, JournalEntry>(s => s.PostAsync(new DateOnly(2025, 12, 31), [new OpeningBalance(_checking.Id, 100m)]));
        await In<PostingEngine, JournalEntry>(s => s.VoidAsync(entry.Id, "Wrong figures"));
        Assert.Empty(await In<OpeningBalanceService, IReadOnlyList<JournalEntry>>(s => s.ExistingAsync()));
    }

    [Fact]
    public async Task An_automatic_reversal_follows_a_reclassify_and_new_tags()
    {
        var retail = await In<SegmentService, Segment>(s => s.CreateAsync(SegmentKind.Class, "Retail"));
        var online = await In<SegmentService, Segment>(s => s.CreateAsync(SegmentKind.Class, "Online"));
        var tag = await In<SegmentService, Segment>(s => s.CreateAsync(SegmentKind.Tag, "Accrual"));
        var entry = await In<PostingEngine, JournalEntry>(s => s.PostAsync(new PostJournalEntryCommand(Jan5,
            [JournalLineDraft.Dr(_expense.Id, 40m, null, retail.Id), JournalLineDraft.Cr(_checking.Id, 40m)], "Accrual", ReverseOn: new DateOnly(2026, 2, 1))));
        var mirror = (await In<PostingEngine, JournalEntry?>(s => s.StandingAutoReversalAsync(entry.Id)))!;

        var expenseLine = entry.Lines.Single(l => l.AccountId == _expense.Id).Id;
        await In<SegmentService, int>(s => s.ReclassifyAsync(new ReclassifyCommand([expenseLine], true, online.Id, false, null)));
        await In<SegmentService, IReadOnlyList<Guid>>(s => s.SetTagsAsync(entry.Id, [tag.Id]));

        var rows = await _db.InScope(sp => sp.GetRequiredService<ILedgerStore>().GetLineSegmentsAsync([mirror.Id], default));
        Assert.Equal(online.Id, Assert.Single(rows).ClassId);
        var tags = await _db.InScope(sp => sp.GetRequiredService<ILedgerStore>().GetEntryTagsAsync([mirror.Id], default));
        Assert.Equal(tag.Id, Assert.Single(tags).TagId);
    }

    [Fact]
    public async Task Nothing_new_goes_under_an_inactive_parent_but_a_child_keeps_the_one_it_has()
    {
        var parent = await In<SegmentService, Segment>(s => s.CreateAsync(SegmentKind.Class, "Retired"));
        var child = await In<SegmentService, Segment>(s => s.CreateAsync(SegmentKind.Class, "East", parent.Id));
        await In<SegmentService, Segment>(s => s.SetActiveAsync(child.Id, false));
        await In<SegmentService, Segment>(s => s.SetActiveAsync(parent.Id, false));

        Assert.Equal("segment.parent_inactive", await Refused(() => In<SegmentService, Segment>(s => s.CreateAsync(SegmentKind.Class, "West", parent.Id))));
        var renamed = await In<SegmentService, Segment>(s => s.UpdateAsync(child.Id, "East side", parent.Id));
        Assert.Equal(parent.Id, renamed.ParentId);
    }

    [Fact]
    public void All_dates_are_named_as_that_and_not_as_a_date_in_1900() =>
        Assert.StartsWith("All dates to ", Comparisons.Describe(DateRanges.Beginning, new DateOnly(2026, 3, 31)), StringComparison.Ordinal);

    [Fact]
    public async Task The_builder_keeps_its_totals_when_the_first_column_is_an_amount()
    {
        await Post(40m);
        var table = await In<ReportCatalog, ReportTable>(s => s.RunAsync("builder", ReportParameters.Parse("columns=Amount&groupBy=Account&preset=AllDates")));
        Assert.Equal(40m, table.Rows.First(r => r.Style == ReportRowStyle.Subtotal && r.Cells[0].Value is decimal d && d == 40m).Cells[0].Value);
    }

    [Theory]
    [InlineData(2026, 11, 5, 2026, 11, 6)] // a Thursday
    [InlineData(2026, 11, 6, 2026, 11, 9)] // a Friday
    [InlineData(2026, 11, 7, 2026, 11, 9)] // a Saturday
    public void The_next_banking_day_skips_the_weekend(int y, int m, int d, int ny, int nm, int nd) =>
        Assert.Equal(new DateOnly(ny, nm, nd), OpenBook.Domain.Purchases.Ach.NextBankingDay(new DateOnly(y, m, d)));

    [Fact]
    public void A_loans_schedule_comes_back_to_its_day_after_a_short_month()
    {
        var rest = OpenBook.Domain.Assets.Amortization.Schedule(1000m, 6m, 100m, new DateOnly(2027, 1, 31), startNumber: 3, monthsIn: 2);
        Assert.Equal(new DateOnly(2027, 3, 31), rest[0].Date);
    }

    [Fact]
    public async Task An_account_documents_still_need_stays_active()
    {
        var receivable = await _db.CreateAccount("Accounts Receivable", AccountType.AccountsReceivable, "1100");
        var income = await _db.CreateAccount("Sales", AccountType.Income, "4000");
        var item = await In<ItemService, Item>(s => s.CreateAsync(new ItemDetails("Hours", ItemType.Service, income.Id, 100m, Taxable: false)));
        var customer = await In<CustomerService, Customer>(s => s.CreateAsync(new CustomerDetails("Acme")));
        await In<InvoiceService, Invoice>(s => s.CreateAsync(new CreateInvoiceCommand(customer.Id, Jan5, [new SalesLineDraft(item.Id, "Work", 1m, 100m)])));

        Assert.Equal("account.in_use", await Refused(() => In<AccountService, Account>(s => s.DeactivateAsync(receivable.Id))));
    }

    [Fact]
    public async Task A_name_held_by_an_inactive_account_says_what_to_do()
    {
        var held = await _db.CreateAccount("Bad Debts", AccountType.Expense, "6900");
        await In<AccountService, Account>(s => s.DeactivateAsync(held.Id));
        var refused = await Assert.ThrowsAsync<DomainException>(() => In<AccountService, Account>(s => s.CreateUsualAsync(new CreateAccountCommand("Bad Debts", AccountType.Expense))));
        Assert.Equal("account.usual_name_held", refused.Code);
        Assert.Contains("inactive", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_payment_moved_earlier_takes_its_application_with_it()
    {
        var receivable = await _db.CreateAccount("Accounts Receivable", AccountType.AccountsReceivable, "1100");
        var income = await _db.CreateAccount("Sales", AccountType.Income, "4000");
        var item = await In<ItemService, Item>(s => s.CreateAsync(new ItemDetails("Hours", ItemType.Service, income.Id, 100m, Taxable: false)));
        var customer = await In<CustomerService, Customer>(s => s.CreateAsync(new CustomerDetails("Acme")));
        var invoice = await In<InvoiceService, Invoice>(s => s.CreateAsync(new CreateInvoiceCommand(customer.Id, new DateOnly(2026, 1, 2), [new SalesLineDraft(item.Id, "Work", 1m, 100m)])));
        var payment = await In<PaymentService, CustomerPayment>(s => s.ReceiveAsync(new ReceivePaymentCommand(customer.Id, new DateOnly(2026, 1, 20), _checking.Id, [new ApplyToInvoice(invoice.Id, 100m)])));

        var moved = await In<PaymentService, CustomerPayment>(s => s.ChangeAsync(payment.Id, new ChangePaymentCommand(new DateOnly(2026, 1, 10), [new ApplyToInvoice(invoice.Id, 100m)], "Wrong date")));

        Assert.Equal(new DateOnly(2026, 1, 10), Assert.Single(moved.Applications).AppliedOn);
        _ = receivable;
    }

    [Fact]
    public void Many_small_shares_add_up_and_none_goes_negative()
    {
        var shares = Shares.ByWeight(1.00m, [.. Enumerable.Repeat(10m, 200)]);
        Assert.Equal(1.00m, shares.Sum());
        Assert.All(shares, s => Assert.True(s >= 0m));
    }

    [Theory]
    [InlineData(10.40, new[] { 0.21, 0.20, 0.21, 0.0 })]
    [InlineData(10.20, new[] { 0.20, 0.21, 0.20, 0.0 })]
    public void A_zero_rate_takes_no_tax_and_no_rate_goes_negative(double taxable, double[] expected)
    {
        var rates = RatesWithAZero.Select(p => new TaxComponent(Guid.NewGuid(), "Rate", p, Guid.NewGuid())).ToList();
        var shares = TaxCalculator.Split((decimal)taxable, rates, exempt: false);
        Assert.Equal(expected.Select(e => (decimal)e), shares);
    }

    [Fact]
    public async Task A_percentage_that_would_leave_less_than_a_cent_finishes_the_line()
    {
        var income = await _db.CreateAccount("Sales", AccountType.Income, "4000");
        var item = await In<ItemService, Item>(s => s.CreateAsync(new ItemDetails("Hours", ItemType.Service, income.Id, 30m, Taxable: false)));
        var line = SalesLine.FromDrafts([new SalesLineDraft(item.Id, "Work", 1m, 30m)], new Dictionary<Guid, Item> { [item.Id] = item })[0];
        var picked = PartialInvoicing.ByPercent([line], new Dictionary<int, decimal> { [line.LineNumber] = 0.3334m }, 33.33m);
        Assert.Equal(0.3334m, Assert.Single(picked).Quantity);
    }

    [Fact]
    public void A_year_without_a_mileage_rate_uses_the_latest_before_it()
    {
        Assert.Equal(0.725m, OpenBook.Domain.Projects.MileageRate.For(OpenBook.Domain.Projects.MileageRate.Standard, 2027));
        Assert.Equal(0.70m, OpenBook.Domain.Projects.MileageRate.For(OpenBook.Domain.Projects.MileageRate.Standard, 2025));
    }

    [Fact]
    public void A_channel_file_says_what_it_left_out()
    {
        var read = ChannelReports.Read(ChannelKind.Etsy, "Date,Type,Net\n\"2 March 2026\",Sale,$25.00\n");
        Assert.Empty(read.Payouts);
        Assert.Contains(read.Warnings, w => w.Contains("dates", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Only_one_reconciliation_of_an_account_can_be_in_progress()
    {
        await In<ReconciliationService, Reconciliation>(s => s.StartAsync(_checking.Id, new DateOnly(2026, 1, 31), 0m));
        var second = Reconciliation.Start(_checking.Id, new DateOnly(2026, 1, 31), 0m, 0m, null, _db.Clock.UtcNow, "someone");
        await Assert.ThrowsAsync<ConcurrencyConflictException>(() => _db.InDb(async db =>
        {
            db.Reconciliations.Add(second);
            return await db.SaveChangesAsync();
        }));
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
