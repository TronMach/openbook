using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OpenBook.Application.Banking;
using OpenBook.Application.Import;
using OpenBook.Application.Ledger;
using OpenBook.Application.Purchases;
using OpenBook.Domain.Accounts;
using OpenBook.Domain.Banking;
using OpenBook.Domain.Common;
using OpenBook.Domain.Ledger;
using OpenBook.Domain.Purchases;

namespace OpenBook.Integration.Tests;

/// <summary>V1.2: OFX import, the bank feed review queue, bank rules, undo reconciliation, check printing.</summary>
public sealed class BankingProTests : IAsyncLifetime
{
    private const string SgmlOfx = """
        OFXHEADER:100
        DATA:OFXSGML
        VERSION:102

        <OFX>
        <SIGNONMSGSRSV1><SONRS><STATUS><CODE>0<SEVERITY>INFO</STATUS><DTSERVER>20260331120000<LANGUAGE>ENG</SONRS></SIGNONMSGSRSV1>
        <BANKMSGSRSV1><STMTTRNRS><TRNUID>1<STMTRS><CURDEF>USD
        <BANKACCTFROM><BANKID>011000015<ACCTID>000123456789<ACCTTYPE>CHECKING</BANKACCTFROM>
        <BANKTRANLIST><DTSTART>20260301<DTEND>20260331
        <STMTTRN><TRNTYPE>DEBIT<DTPOSTED>20260302120000.000[-5:EST]<TRNAMT>-45.20<FITID>F100<NAME>AMZN MKTP US*2K4<MEMO>Office supplies</STMTTRN>
        <STMTTRN><TRNTYPE>CHECK<DTPOSTED>20260305<TRNAMT>-1200.00<FITID>F101<CHECKNUM>1001<NAME>CHECK 1001</STMTTRN>
        <STMTTRN><TRNTYPE>CREDIT<DTPOSTED>20260310<TRNAMT>2500.00<FITID>F102<NAME>ACME CORP &amp; SONS</STMTTRN>
        </BANKTRANLIST>
        <LEDGERBAL><BALAMT>1254.80<DTASOF>20260331</LEDGERBAL>
        </STMTRS></STMTTRNRS></BANKMSGSRSV1>
        </OFX>
        """;

    private const string XmlOfx = """
        <?xml version="1.0" encoding="UTF-8"?>
        <?OFX OFXHEADER="200" VERSION="220"?>
        <OFX><CREDITCARDMSGSRSV1><CCSTMTTRNRS><CCSTMTRS><CURDEF>USD</CURDEF>
        <CCACCTFROM><ACCTID>4111111111111111</ACCTID></CCACCTFROM>
        <BANKTRANLIST>
        <STMTTRN><TRNTYPE>DEBIT</TRNTYPE><DTPOSTED>20260304</DTPOSTED><TRNAMT>-19,99</TRNAMT><FITID>C1</FITID><NAME>SPOTIFY</NAME></STMTTRN>
        <STMTTRN><TRNTYPE>CREDIT</TRNTYPE><DTPOSTED>20260320</DTPOSTED><TRNAMT>500.00</TRNAMT><FITID>C2</FITID><NAME>PAYMENT THANK YOU</NAME></STMTTRN>
        </BANKTRANLIST></CCSTMTRS></CCSTMTTRNRS></CREDITCARDMSGSRSV1></OFX>
        """;

    private readonly LedgerDatabase _db = new();
    private Account _checking = null!;
    private Account _card = null!;
    private Account _office = null!;
    private Account _sales = null!;
    private Account _rent = null!;

    public async Task InitializeAsync()
    {
        await _db.InitializeAsync();
        _checking = await _db.CreateAccount("Checking", AccountType.Bank, "1000");
        _card = await _db.CreateAccount("Visa", AccountType.CreditCard, "2100");
        _office = await _db.CreateAccount("Office Supplies", AccountType.Expense, "6300");
        _sales = await _db.CreateAccount("Sales", AccountType.Income, "4000");
        _rent = await _db.CreateAccount("Rent", AccountType.Expense, "6500");
    }

    public Task DisposeAsync() => _db.DisposeAsync();

    private Task<T> In<TService, T>(Func<TService, Task<T>> work) where TService : notnull =>
        _db.InScope(sp => work(sp.GetRequiredService<TService>()));

    private static async Task<string> Refused(Func<Task> action) => (await Assert.ThrowsAsync<DomainException>(action)).Code;

    private Task<BankImportPreview> Preview(Guid account, string text) =>
        In<BankFeedService, BankImportPreview>(s => s.PreviewAsync(account, text, new BankImportOptions()));

    private Task<FeedImportResult> Queue(Guid account, BankImportPreview preview) =>
        In<BankFeedService, FeedImportResult>(s => s.QueueAsync(account, preview.Rows.Where(r => r.IsValid)
            .Select(r => new FeedLine(r.Date!.Value, r.Amount!.Value, r.Payee, r.Reference, r.Memo, r.FitId)).ToList()));

    private Task<IReadOnlyList<FeedReviewItem>> Review(Guid account, BankFeedStatus status = BankFeedStatus.Pending) =>
        In<BankFeedService, IReadOnlyList<FeedReviewItem>>(s => s.ReviewAsync(account, status));

    private const string OfxHead = "<OFX><BANKMSGSRSV1><STMTTRNRS><STMTRS><BANKACCTFROM><ACCTID>1</BANKACCTFROM><BANKTRANLIST>";

    private static string Statement(params string[] lines) => OfxHead + string.Concat(lines) + "</BANKTRANLIST></STMTRS></STMTTRNRS></BANKMSGSRSV1></OFX>";

    private static string Line(string id, string date, string amount, string name) =>
        $"<STMTTRN><TRNTYPE>DEBIT<DTPOSTED>{date}<TRNAMT>{amount}<FITID>{id}<NAME>{name}</STMTTRN>";

    [Fact]
    public async Task A_bank_id_used_for_two_lines_brings_both_in_once()
    {
        var file = Statement(Line("X", "20260302", "-5.00", "COFFEE"), Line("X", "20260302", "-9.00", "LUNCH"));
        var preview = await Preview(_checking.Id, file);
        Assert.DoesNotContain(preview.Rows, r => r.AlreadyImported);

        var first = await Queue(_checking.Id, preview);
        Assert.Equal((2, 0), (first.Queued, first.Skipped));
        Assert.Equal(2, (await Review(_checking.Id)).Count);

        // The same file again: both are known, under keys that don't change.
        var again = await Preview(_checking.Id, file);
        Assert.All(again.Rows, r => Assert.True(r.AlreadyImported));
        var second = await Queue(_checking.Id, again);
        Assert.Equal((0, 2), (second.Queued, second.Skipped));
    }

    [Fact]
    public async Task A_line_whose_posting_date_moved_is_still_the_same_line()
    {
        await Queue(_checking.Id, await Preview(_checking.Id, Statement(Line("Y", "20260302", "-5.00", "COFFEE"))));

        var moved = await Preview(_checking.Id, Statement(Line("Y", "20260304", "-5.00", "COFFEE")));

        Assert.True(Assert.Single(moved.Rows).AlreadyImported);
    }

    [Fact]
    public async Task A_line_the_bank_gave_a_new_id_is_flagged_though_it_never_became_a_transaction()
    {
        await Queue(_checking.Id, await Preview(_checking.Id, Statement(Line("Z1", "20260302", "-5.00", "COFFEE"))));
        var line = Assert.Single(await Review(_checking.Id)).Item;
        await In<BankFeedService, bool>(async s => { await s.ExcludeAsync(line.Id); return true; });

        var row = Assert.Single((await Preview(_checking.Id, Statement(Line("Z2", "20260302", "-5.00", "COFFEE SHOP")))).Rows);

        Assert.False(row.AlreadyImported);
        Assert.True(row.PossibleDuplicate);
    }

    // ---- OFX ----

    [Fact]
    public void Sgml_ofx_is_read_with_bank_ids_checks_and_balance()
    {
        var statement = Ofx.Parse(SgmlOfx);

        Assert.Equal("000123456789", statement.AccountId);
        Assert.Equal((1254.80m, new DateOnly(2026, 3, 31)), (statement.LedgerBalance!.Value, statement.LedgerBalanceDate!.Value));
        Assert.Equal(["F100", "F101", "F102"], statement.Transactions.Select(t => t.FitId));
        Assert.Equal([-45.20m, -1200m, 2500m], statement.Transactions.Select(t => t.Amount));
        Assert.Equal(new DateOnly(2026, 3, 2), statement.Transactions[0].Date);
        Assert.Equal("1001", statement.Transactions[1].CheckNumber);
        Assert.Equal("ACME CORP & SONS", statement.Transactions[2].Name);
        Assert.Equal("Office supplies", statement.Transactions[0].Memo);
    }

    [Fact]
    public void Xml_ofx_from_a_card_is_read_too()
    {
        var statement = Ofx.Parse(XmlOfx);

        Assert.Equal([-19.99m, 500m], statement.Transactions.Select(t => t.Amount));
        Assert.Equal("4111111111111111", statement.AccountId);
        Assert.True(Ofx.LooksLikeOfx(XmlOfx));
        Assert.False(Ofx.LooksLikeOfx("Date,Amount\n2026-03-01,5"));
    }

    [Fact]
    public void Broken_ofx_is_refused_with_a_reason()
    {
        Assert.Equal("ofx.invalid", Assert.Throws<DomainException>(() => Ofx.Parse("Date,Amount")).Code);
        Assert.Equal("ofx.no_transactions", Assert.Throws<DomainException>(() => Ofx.Parse("<OFX><BANKTRANLIST></BANKTRANLIST></OFX>")).Code);
        Assert.Equal("ofx.bad_date", Assert.Throws<DomainException>(() => Ofx.Parse("<OFX><STMTTRN><DTPOSTED>2026<TRNAMT>1.00</STMTTRN></OFX>")).Code);
    }

    // ---- Queue and duplicates ----

    [Fact]
    public async Task Importing_the_same_statement_twice_queues_each_line_once()
    {
        var first = await Queue(_checking.Id, await Preview(_checking.Id, SgmlOfx));
        Assert.Equal(new FeedImportResult(3, 0, 0), first);
        Assert.Equal(0, await _db.InDb(db => db.JournalEntries.CountAsync()));

        var again = await Preview(_checking.Id, SgmlOfx);
        Assert.All(again.Rows, r => Assert.True(r.AlreadyImported));
        Assert.Equal(new FeedImportResult(0, 0, 3), await Queue(_checking.Id, again));
        Assert.Equal(3, (await Review(_checking.Id)).Count);
    }

    [Fact]
    public async Task Identical_csv_lines_in_one_file_are_kept_and_reimporting_skips_both()
    {
        const string csv = "Date,Description,Amount\n2026-03-02,COFFEE SHOP,-5.00\n2026-03-02,COFFEE SHOP,-5.00\n";

        Assert.Equal(new FeedImportResult(2, 0, 0), await Queue(_checking.Id, await Preview(_checking.Id, csv)));
        Assert.Equal(new FeedImportResult(0, 0, 2), await Queue(_checking.Id, await Preview(_checking.Id, csv)));
    }

    // ---- Review: add, match, exclude, reopen ----

    [Fact]
    public async Task Adding_a_line_records_a_categorized_transaction()
    {
        await Queue(_checking.Id, await Preview(_checking.Id, SgmlOfx));
        var amazon = (await Review(_checking.Id)).Single(r => r.Item.FitId == "F100").Item;

        var transaction = await In<BankFeedService, BankTransaction>(s => s.AddAsync(amazon.Id, new AddFeedItemCommand(_office.Id, "Amazon")));

        Assert.Equal((BankTransactionKind.Spend, 45.20m, "Amazon", new DateOnly(2026, 3, 2)), (transaction.Kind, transaction.Amount, transaction.Payee, transaction.Date));
        Assert.Equal(2, (await Review(_checking.Id)).Count);
        Assert.Single(await Review(_checking.Id, BankFeedStatus.Added));
        Assert.Equal("feed.not_pending", await Refused(() => In<BankFeedService, BankTransaction>(s => s.AddAsync(amazon.Id, new AddFeedItemCommand(_office.Id)))));
    }

    [Fact]
    public async Task A_line_already_in_the_books_is_offered_as_a_match()
    {
        var check = await In<BankingService, BankTransaction>(s => s.RecordAsync(new RecordBankTransactionCommand(
            BankTransactionKind.Spend, _checking.Id, new DateOnly(2026, 3, 1), [new BankSplit(_rent.Id, 1200m)], "Landlord", "1001")));
        await Queue(_checking.Id, await Preview(_checking.Id, SgmlOfx));

        var line = (await Review(_checking.Id)).Single(r => r.Item.FitId == "F101");
        var match = Assert.Single(line.Matches);
        Assert.Equal(check.JournalEntryId, match.JournalEntryId);

        await _db.InScope(sp => sp.GetRequiredService<BankFeedService>().MatchAsync(line.Item.Id, match.JournalEntryId));

        Assert.Equal(1, await _db.InDb(db => db.JournalEntries.CountAsync()));
        Assert.Empty((await Review(_checking.Id)).SelectMany(r => r.Matches));
        Assert.Single(await Review(_checking.Id, BankFeedStatus.Matched));
    }

    [Fact]
    public async Task Matches_must_have_the_same_amount_and_not_be_used_twice()
    {
        var deposit = await In<BankingService, BankTransaction>(s => s.RecordAsync(new RecordBankTransactionCommand(
            BankTransactionKind.Receive, _checking.Id, new DateOnly(2026, 3, 9), [new BankSplit(_sales.Id, 2500m)], "Acme")));
        await Queue(_checking.Id, await Preview(_checking.Id, SgmlOfx));
        var acme = (await Review(_checking.Id)).Single(r => r.Item.FitId == "F102").Item;
        var amazon = (await Review(_checking.Id)).Single(r => r.Item.FitId == "F100").Item;

        Assert.Equal("feed.match_amount", await Refused(() => _db.InScope(sp => sp.GetRequiredService<BankFeedService>().MatchAsync(amazon.Id, deposit.JournalEntryId))));
        await _db.InScope(sp => sp.GetRequiredService<BankFeedService>().MatchAsync(acme.Id, deposit.JournalEntryId));
        await _db.InScope(sp => sp.GetRequiredService<BankFeedService>().ReopenAsync(acme.Id));
        Assert.Single((await Review(_checking.Id)).Single(r => r.Item.Id == acme.Id).Matches);
    }

    [Fact]
    public async Task Reopening_an_added_line_voids_its_transaction()
    {
        await Queue(_checking.Id, await Preview(_checking.Id, SgmlOfx));
        var amazon = (await Review(_checking.Id)).Single(r => r.Item.FitId == "F100").Item;
        var transaction = await In<BankFeedService, BankTransaction>(s => s.AddAsync(amazon.Id, new AddFeedItemCommand(_office.Id)));

        await _db.InScope(sp => sp.GetRequiredService<BankFeedService>().ReopenAsync(amazon.Id));

        Assert.Equal(BankTransactionStatus.Voided, (await In<BankingService, BankTransaction?>(s => s.GetAsync(transaction.Id)))!.Status);
        Assert.Equal(3, (await Review(_checking.Id)).Count);

        await _db.InScope(sp => sp.GetRequiredService<BankFeedService>().ExcludeAsync(amazon.Id));
        Assert.Single(await Review(_checking.Id, BankFeedStatus.Excluded));
    }

    [Fact]
    public async Task The_audit_log_finds_a_feed_line_or_an_import_by_its_id()
    {
        await Queue(_checking.Id, await Preview(_checking.Id, SgmlOfx));
        var amazon = (await Review(_checking.Id)).Single(r => r.Item.FitId == "F100").Item;
        await _db.InScope(sp => sp.GetRequiredService<BankFeedService>().ExcludeAsync(amazon.Id));

        Assert.Equal((_checking.Id, BankFeedStatus.Excluded), await In<BankFeedService, (Guid, BankFeedStatus)?>(s => s.LocateAsync(amazon.Id)));
        Assert.Equal((_checking.Id, BankFeedStatus.Pending), await In<BankFeedService, (Guid, BankFeedStatus)?>(s => s.LocateAsync(amazon.ImportBatchId)));
        Assert.Null(await In<BankFeedService, (Guid, BankFeedStatus)?>(s => s.LocateAsync(Guid.NewGuid())));
    }

    [Fact]
    public async Task Card_lines_use_the_holders_view_charges_out_payments_in()
    {
        await Queue(_card.Id, await Preview(_card.Id, XmlOfx));
        var spotify = (await Review(_card.Id)).Single(r => r.Item.FitId == "C1").Item;

        var charge = await In<BankFeedService, BankTransaction>(s => s.AddAsync(spotify.Id, new AddFeedItemCommand(_office.Id)));

        Assert.Equal(BankTransactionKind.Spend, charge.Kind);
        var tb = await _db.InScope(sp => sp.GetRequiredService<TrialBalanceQuery>().RunAsync(new DateOnly(2026, 12, 31), ReportBasis.Accrual));
        Assert.Equal(19.99m, tb.Lines.Single(l => l.AccountId == _card.Id).Credit.Amount);
    }

    // ---- Rules ----

    private Task<FeedImportResult> Line(DateOnly date, decimal amount, string fitId) =>
        In<BankFeedService, FeedImportResult>(s => s.QueueAsync(_checking.Id, [new FeedLine(date, amount, "CITY POWER", null, null, fitId)]));

    private Task<BankRule> PowerRule() =>
        In<BankRuleService, BankRule>(s => s.CreateAsync(new BankRuleDetails("Power", "city power", _office.Id, RuleDirection.MoneyOut, AutoAdd: true)));

    [Fact]
    public async Task An_automatic_rule_leaves_a_line_already_in_the_books_for_review()
    {
        await PowerRule();
        await In<BankingService, BankTransaction>(s => s.RecordAsync(new RecordBankTransactionCommand(
            BankTransactionKind.Spend, _checking.Id, new DateOnly(2026, 10, 1), [new BankSplit(_office.Id, 120m)], Payee: "City Power")));

        Assert.Equal(new FeedImportResult(1, 0, 0), await Line(new DateOnly(2026, 10, 3), -120m, "P1"));

        var waiting = Assert.Single(await Review(_checking.Id));
        Assert.Single(waiting.Matches);
        Assert.NotNull(waiting.Suggestion);
    }

    [Fact]
    public async Task An_automatic_rule_still_adds_the_same_bill_a_week_after_the_last_it_added()
    {
        await PowerRule();

        Assert.Equal(new FeedImportResult(0, 1, 0), await Line(new DateOnly(2026, 10, 3), -120m, "P1"));
        Assert.Equal(new FeedImportResult(0, 1, 0), await Line(new DateOnly(2026, 10, 10), -120m, "P2"));
    }

    [Fact]
    public async Task A_rule_splits_the_line_it_adds_by_percent()
    {
        var rule = await PowerRule();
        Assert.Equal("rule.split_total", (await Assert.ThrowsAsync<DomainException>(() => In<BankRuleService, BankRule>(s =>
            s.SetSplitsAsync(rule.Id, [(_office.Id, 60m, null), (_rent.Id, 30m, null)])))).Code);
        await In<BankRuleService, BankRule>(s => s.SetSplitsAsync(rule.Id, [(_office.Id, 60m, null), (_rent.Id, 40m, null)]));

        Assert.Equal(new FeedImportResult(0, 1, 0), await Line(new DateOnly(2026, 10, 3), -125m, "P9"));
        var added = Assert.Single(await Review(_checking.Id, BankFeedStatus.Added));
        var transaction = (await In<BankingService, BankTransaction?>(s => s.GetAsync(added.Item.BankTransactionId!.Value)))!;
        var entry = (await In<PostingEngine, JournalEntry?>(e => e.GetAsync(transaction.JournalEntryId)))!;
        Assert.Equal((75m, 50m), (entry.Lines.Where(l => l.AccountId == _office.Id).Sum(l => l.Amount), entry.Lines.Where(l => l.AccountId == _rent.Id).Sum(l => l.Amount)));
    }

    [Fact]
    public async Task Rules_suggest_or_add_automatically()
    {
        var landlord = await In<VendorService, Vendor>(s => s.CreateAsync(new VendorDetails("Landlord LLC")));
        await In<BankRuleService, BankRule>(s => s.CreateAsync(new BankRuleDetails("Amazon", "amzn", _office.Id, RuleDirection.MoneyOut, Payee: "Amazon")));
        await In<BankRuleService, BankRule>(s => s.CreateAsync(new BankRuleDetails("Rent check", "check 1001", _rent.Id, RuleDirection.MoneyOut,
            MinAmount: 1000m, VendorId: landlord.Id, AutoAdd: true, Priority: 10)));

        var result = await Queue(_checking.Id, await Preview(_checking.Id, SgmlOfx));

        Assert.Equal(new FeedImportResult(2, 1, 0), result);
        var added = Assert.Single(await Review(_checking.Id, BankFeedStatus.Added));
        var rent = await In<BankingService, BankTransaction?>(s => s.GetAsync(added.Item.BankTransactionId!.Value));
        Assert.Equal((landlord.Id, "Landlord LLC", "1001"), (rent!.VendorId!.Value, rent.Payee, rent.Reference));

        var amazon = (await Review(_checking.Id)).Single(r => r.Item.FitId == "F100");
        Assert.Equal((_office.Id, "Amazon"), (amazon.Suggestion!.CategoryAccountId, amazon.Suggestion.Payee));
        Assert.Null((await Review(_checking.Id)).Single(r => r.Item.FitId == "F102").Suggestion);
    }

    [Fact]
    public async Task Rules_cannot_point_at_receivables_or_their_own_account()
    {
        var ar = await _db.CreateAccount("Accounts Receivable", AccountType.AccountsReceivable);

        Assert.Equal("rule.category_type", await Refused(() => In<BankRuleService, BankRule>(s => s.CreateAsync(new BankRuleDetails("x", "x", ar.Id)))));
        Assert.Equal("rule.category_self", await Refused(() => In<BankRuleService, BankRule>(s => s.CreateAsync(new BankRuleDetails("x", "x", _checking.Id, AccountId: _checking.Id)))));
    }

    // ---- Undo reconciliation ----

    [Fact]
    public async Task Only_the_latest_reconciliation_can_be_undone_and_it_reopens_with_its_ticks()
    {
        var deposit = await In<BankingService, BankTransaction>(s => s.RecordAsync(new RecordBankTransactionCommand(
            BankTransactionKind.Receive, _checking.Id, new DateOnly(2026, 1, 10), [new BankSplit(_sales.Id, 100m)])));
        var line = (await _db.InDb(db => db.JournalEntries.Include(e => e.Lines).SingleAsync(e => e.Id == deposit.JournalEntryId)))
            .Lines.Single(l => l.AccountId == _checking.Id).Id;
        var service = (IServiceProvider sp) => sp.GetRequiredService<ReconciliationService>();

        var jan = await _db.InScope(sp => service(sp).StartAsync(_checking.Id, new DateOnly(2026, 1, 31), 100m));
        await _db.InScope(sp => service(sp).SetClearedAsync(jan.Id, [line], true));
        await _db.InScope(sp => service(sp).CompleteAsync(jan.Id));
        var feb = await _db.InScope(sp => service(sp).StartAsync(_checking.Id, new DateOnly(2026, 2, 28), 100m));
        await _db.InScope(sp => service(sp).CompleteAsync(feb.Id));

        Assert.Equal("reconcile.not_latest", await Refused(() => _db.InScope(sp => service(sp).UndoAsync(jan.Id))));
        await _db.InScope(sp => service(sp).UndoAsync(feb.Id));
        Assert.Equal("reconcile.in_progress", await Refused(() => _db.InScope(sp => service(sp).UndoAsync(feb.Id))));
        await _db.InScope(sp => service(sp).CancelAsync(feb.Id));

        var reopened = await _db.InScope(sp => service(sp).UndoAsync(jan.Id));
        Assert.Equal(ReconciliationStatus.InProgress, reopened.Status);
        Assert.Contains(line, reopened.ClearedLineIds);
    }

    // ---- Checks ----

    [Fact]
    public async Task Checks_print_as_pdf_and_are_marked_printed()
    {
        var vendor = await In<VendorService, Vendor>(s => s.CreateAsync(new VendorDetails("Landlord LLC", Address: "9 Elm St\nPortland, ME")));
        var expense = await In<BankingService, BankTransaction>(s => s.RecordAsync(new RecordBankTransactionCommand(
            BankTransactionKind.Spend, _checking.Id, new DateOnly(2026, 3, 1), [new BankSplit(_rent.Id, 1234.56m)], null, "1001", "March rent", vendor.Id)));
        var bill = await In<BillService, Bill>(s => s.CreateAsync(new CreateBillCommand(vendor.Id, new DateOnly(2026, 3, 1), [new BillLineDraft(_rent.Id, 300m)], Reference: "INV-7")));
        var payment = (await In<BillPaymentService, IReadOnlyList<BillPayment>>(s => s.PayAsync(new PayBillsCommand(new DateOnly(2026, 3, 5), _checking.Id, [new ApplyToBill(bill.Id, 300m)], "1002")))).Single();
        var cardCharge = await In<BankingService, BankTransaction>(s => s.RecordAsync(new RecordBankTransactionCommand(
            BankTransactionKind.Spend, _card.Id, new DateOnly(2026, 3, 1), [new BankSplit(_office.Id, 9m)])));

        var unprinted = await In<CheckService, IReadOnlyList<PrintableCheck>>(s => s.ListAsync(unprintedOnly: true));
        Assert.Equal([payment.Id, expense.Id], unprinted.Select(c => c.Id));

        var printed = await In<CheckService, PrintedChecks>(s => s.PrintAsync([(CheckKind.Expense, expense.Id), (CheckKind.BillPayment, payment.Id)]));

        Assert.Equal(2, printed.Count);
        Assert.StartsWith("%PDF", Encoding.ASCII.GetString(printed.Pdf, 0, 4), StringComparison.Ordinal);
        Assert.Empty(await In<CheckService, IReadOnlyList<PrintableCheck>>(s => s.ListAsync(unprintedOnly: true)));
        Assert.Equal("check.not_printable", await Refused(() => In<CheckService, PrintedChecks>(s => s.PrintAsync([(CheckKind.Expense, cardCharge.Id)]))));
    }
}
