using Microsoft.Extensions.DependencyInjection;
using OpenBook.Application;
using OpenBook.Application.Attachments;
using OpenBook.Application.Banking;
using OpenBook.Application.Companies;
using OpenBook.Application.Import;
using OpenBook.Application.Nonprofit;
using OpenBook.Application.Reporting;
using OpenBook.Application.Sales;
using OpenBook.Domain.Accounts;
using OpenBook.Domain.Banking;
using OpenBook.Domain.Common;
using OpenBook.Domain.Nonprofit;
using OpenBook.Domain.Sales;

namespace OpenBook.Integration.Tests;

/// <summary>Small gaps from the audit: files on more records, bank files with lines above the header, reconciling from the feed, numbers.</summary>
public sealed class SmallGapTests : IAsyncLifetime
{
    private static readonly DateOnly Mar1 = new(2026, 3, 1);
    private static readonly DateOnly Mar31 = new(2026, 3, 31);

    private readonly LedgerDatabase _db = new();
    private Account _checking = null!;
    private Account _rent = null!;

    public async Task InitializeAsync()
    {
        await _db.InitializeAsync();
        _checking = await _db.CreateAccount("Checking", AccountType.Bank, "1000");
        _rent = await _db.CreateAccount("Rent", AccountType.Expense, "6500");
    }

    public Task DisposeAsync() => _db.DisposeAsync();

    private Task<T> In<TService, T>(Func<TService, Task<T>> work) where TService : notnull =>
        _db.InScope(sp => work(sp.GetRequiredService<TService>()));

    [Fact]
    public async Task Gifts_grants_assets_loans_and_projects_take_files()
    {
        foreach (var type in new[] { "grant", "donation", "pledge", "fixed_asset", "loan", "project" })
            Assert.Contains(type, AttachmentService.EntityTypes);
        Assert.Equal("nonprofit", RecordAreas.Of("donation"));
        Assert.Equal("projects", RecordAreas.Of("project"));

        var donor = await In<CustomerService, Customer>(s => s.CreateAsync(new CustomerDetails("Pat")));
        var gift = await In<GiftService, Donation>(s => s.RecordAsync(new RecordGiftCommand(donor.Id, Mar1, 50m, _checking.Id)));
        using var file = new MemoryStream("Thank you"u8.ToArray());
        await In<AttachmentService, OpenBook.Domain.Attachments.Attachment>(s => s.AddAsync("donation", gift.Id, "letter.txt", file));
        Assert.Single(await In<AttachmentService, IReadOnlyList<OpenBook.Domain.Attachments.Attachment>>(s => s.ListAsync("donation", gift.Id)));
    }

    [Fact]
    public async Task Gifts_carry_a_campaign_reported_by_campaign_and_lapsed_donors_follow_the_fiscal_year()
    {
        var pat = await In<CustomerService, Customer>(s => s.CreateAsync(new CustomerDetails("Pat")));
        var sam = await In<CustomerService, Customer>(s => s.CreateAsync(new CustomerDetails("Sam")));
        await In<GiftService, Donation>(s => s.RecordAsync(new RecordGiftCommand(pat.Id, new DateOnly(2025, 8, 1), 50m, _checking.Id, Campaign: "Year-end letter")));
        var second = await In<GiftService, Donation>(s => s.RecordAsync(new RecordGiftCommand(sam.Id, Mar1, 30m, _checking.Id, Campaign: "Gala")));
        await In<GiftService, Donation>(s => s.RecordAsync(new RecordGiftCommand(sam.Id, Mar31, 20m, _checking.Id)));
        Assert.Equal(["Gala", "Year-end letter"], await In<GiftService, IReadOnlyList<string>>(s => s.CampaignsAsync()));
        Assert.Equal("Spring appeal", (await In<GiftService, Donation>(s => s.SetCampaignAsync(second.Id, "Spring appeal"))).Campaign);

        // A batch's campaign, and the import's column, go on each gift.
        var batch = await In<GiftService, IReadOnlyList<Donation>>(s => s.RecordBatchAsync(new GiftBatchCommand(Mar31,
            new GiftBatchDefaults(_checking.Id, Campaign: "Gala"), [new GiftBatchRow(pat.Id, 10m)])));
        Assert.Equal("Gala", Assert.Single(batch).Campaign);
        await In<GiftImportService, GiftImportResult>(s => s.ImportAsync("Date,Donor,Amount,Appeal\n2026-03-15,Sam,15.00,Gala\n", new GiftBatchDefaults(_checking.Id)));
        Assert.Equal(2, (await In<GiftService, IReadOnlyList<Donation>>(s => s.ListAsync())).Count(d => d.Campaign == "Gala"));

        var table = await _db.InScope(sp => sp.GetRequiredService<ReportCatalog>().RunAsync("giving-by-campaign", new ReportParameters(new Dictionary<string, string?>
        {
            ["preset"] = "Custom", ["from"] = "2026-01-01", ["to"] = "2026-12-31",
        }), CancellationToken.None));
        Assert.Equal(["Spring appeal", "Gala", "No campaign", "Total"], table.Rows.Select(r => r.Cells[0].Value?.ToString()));
        Assert.Equal((30m, 25m), (table.Rows[0].Cells[3].Value, table.Rows[1].Cells[3].Value));

        // With a fiscal year from July, both gave in the year that started July 2025 (Sam in March), and not yet in the next.
        await _db.InScope(async sp =>
        {
            var settings = sp.GetRequiredService<OpenBook.Application.Companies.CompanySettingsService>();
            var c = await settings.GetAsync();
            return await settings.UpdateAsync(new OpenBook.Application.Companies.UpdateCompanySettingsCommand(
                c.Name, c.BusinessType, c.LegalName, c.Email, c.Phone, c.Address, 7, c.UseAccountNumbers, c.DefaultReportBasis));
        });
        var lapsed = await In<OpenBook.Application.Nonprofit.GiftReports, ReportTable>(s => s.LapsedDonorsAsync(2026));
        Assert.StartsWith("Gave in Jul 2025", lapsed.Rows[0].Cells[0].Value?.ToString(), StringComparison.Ordinal);
        Assert.Equal(["Pat", "Sam"], lapsed.Rows.Skip(1).Take(2).Select(r => r.Cells[0].Value?.ToString()).Order());
    }

    [Fact]
    public async Task A_partnerships_tax_summary_keeps_schedule_k_apart_halves_meals_and_ties_books_to_the_return()
    {
        await _db.InScope(async sp =>
        {
            var settings = sp.GetRequiredService<CompanySettingsService>();
            var c = await settings.GetAsync();
            return await settings.UpdateAsync(new UpdateCompanySettingsCommand(c.Name, OpenBook.Domain.Companies.BusinessType.Partnership, c.LegalName, c.Email, c.Phone,
                c.Address, c.FiscalYearStartMonth, c.UseAccountNumbers, c.DefaultReportBasis));
        });
        var sales = await _db.CreateAccount("Fees", AccountType.Income, "4000");
        var interest = await _db.CreateAccount("Interest Earned", AccountType.OtherIncome, "7000");
        var meals = await _db.CreateAccount("Meals", AccountType.Expense, "6400");
        var charity = await _db.CreateAccount("Donations Given", AccountType.Expense, "6800");
        var misc = await _db.CreateAccount("Sundry", AccountType.Expense, "6900");
        await In<OpenBook.Application.Accounts.AccountService, int>(s => s.MapTaxLinesAsync(new Dictionary<Guid, string?>
        {
            [sales.Id] = "p.1a", [interest.Id] = "p.k5", [meals.Id] = "p.meals", [_rent.Id] = "p.13", [charity.Id] = "p.k13a",
        }));
        Task Post(Account debit, Account credit, decimal amount) => In<OpenBook.Application.Ledger.PostingEngine, OpenBook.Domain.Ledger.JournalEntry>(s =>
            s.PostAsync(new OpenBook.Application.Ledger.PostJournalEntryCommand(Mar1, [OpenBook.Domain.Ledger.JournalLineDraft.Dr(debit.Id, amount), OpenBook.Domain.Ledger.JournalLineDraft.Cr(credit.Id, amount)])));
        await Post(_checking, sales, 1_000m);
        await Post(_checking, interest, 20m);
        await Post(_rent, _checking, 300m);
        await Post(meals, _checking, 100m);
        await Post(charity, _checking, 30m);
        await Post(misc, _checking, 10m);

        var table = await _db.InScope(sp => sp.GetRequiredService<ReportCatalog>().RunAsync("tax-summary", ReportParameters.Parse("preset=Custom&from=2026-03-01&to=2026-03-31")));
        decimal? Row(string label) => table.Rows.FirstOrDefault(r => r.Cells[0].Value as string == label)?.Cells[1].Value as decimal?;
        // Page 1: 1,000 less 300 rent and 100 meals, half of which comes back.
        Assert.Equal(650m, Row("Ordinary business income on tax lines (page 1)"));
        Assert.Equal(-50m, Row("Less half of meals (50% of most business meals is deductible)"));
        Assert.Contains(table.Rows, r => (r.Cells[0].Value as string)?.EndsWith("Schedule K line 5: Interest income", StringComparison.Ordinal) == true);
        // Books: 1,020 in, 440 out = 580; back to the return through the items that differ.
        Assert.Equal(580m, Row("Net income per books (accounts on tax lines and not)"));
        Assert.Equal(-20m, Row("Less income stated apart on Schedule K"));
        Assert.Equal(30m, Row("Plus deductions stated apart on Schedule K"));
        Assert.Equal(10m, Row("Less accounts not on a tax line (map them, or adjust by hand)"));
        Assert.Equal(650m, Row("Income per return (page 1)"));
    }

    [Fact]
    public async Task A_reprint_shows_the_addresses_the_document_was_sent_to()
    {
        var sales = await _db.CreateAccount("Fees", AccountType.Income, "4000");
        var service = await In<ItemService, Item>(s => s.CreateAsync(new ItemDetails("Advice", ItemType.Service, sales.Id, 100m)));
        const string oldBill = "1 Old Rd\nSpringfield, IL 62701", oldShip = "Dock 4\nSpringfield, IL 62702", newBill = "9 New St\nChicago, IL 60601";
        var details = new CustomerDetails("Acme", BillingAddress: oldBill, ShippingAddress: oldShip);
        var acme = await In<CustomerService, Customer>(s => s.CreateAsync(details));
        var invoice = await In<InvoiceService, Invoice>(s => s.CreateAsync(new CreateInvoiceCommand(acme.Id, Mar1, [new SalesLineDraft(service.Id, null, 1m, 100m)])));
        Assert.Equal((oldBill, oldShip), (invoice.BillToAddress, invoice.ShipToAddress));

        // They move; the invoice still prints where it was sent, and a new one goes to the new address.
        await In<CustomerService, Customer>(s => s.UpdateAsync(acme.Id, details with { BillingAddress = newBill, ShippingAddress = null }));
        var form = await In<OpenBook.Application.Forms.FormService, OpenBook.Application.Forms.SalesForm>(s => s.BuildAsync(OpenBook.Application.Forms.FormKind.Invoice, invoice.Id));
        Assert.Equal((oldBill, oldShip), (form.Customer.Address, form.ShipTo?.Address));
        var later = await In<InvoiceService, Invoice>(s => s.CreateAsync(new CreateInvoiceCommand(acme.Id, Mar31, [new SalesLineDraft(service.Id, null, 1m, 100m)])));
        var laterForm = await In<OpenBook.Application.Forms.FormService, OpenBook.Application.Forms.SalesForm>(s => s.BuildAsync(OpenBook.Application.Forms.FormKind.Invoice, later.Id));
        Assert.Equal((newBill, (string?)null), (laterForm.Customer.Address, laterForm.ShipTo?.Address));
    }

    [Fact]
    public async Task A_line_of_credit_takes_draws_and_a_merchant_advance_repays_its_fee_in_proportion()
    {
        var line = await _db.CreateAccount("Line of Credit", AccountType.OtherCurrentLiability, "2200");
        var advance = await _db.CreateAccount("Merchant Advance", AccountType.OtherCurrentLiability, "2210");
        var credit = await In<OpenBook.Application.Assets.LoanService, OpenBook.Domain.Assets.Loan>(s => s.CreateAsync(new OpenBook.Application.Assets.CreateLoanCommand(
            new OpenBook.Domain.Assets.LoanDetails("Bank line", line.Id, Guid.Empty, 1_000m, 12m, 12, Mar31, CreditLimit: 5_000m), _checking.Id, Mar1)));
        await In<OpenBook.Application.Assets.LoanService, OpenBook.Domain.Assets.LoanDraw>(s => s.DrawAsync(credit.Id, Mar1.AddDays(5), 2_000m, _checking.Id));
        Assert.Equal("loan.over_limit", (await Assert.ThrowsAsync<DomainException>(() =>
            In<OpenBook.Application.Assets.LoanService, OpenBook.Domain.Assets.LoanDraw>(s => s.DrawAsync(credit.Id, Mar1.AddDays(6), 2_500m, _checking.Id)))).Code);
        // Interest is a month on what's owed now: 1% of 3,000.
        var paid = await In<OpenBook.Application.Assets.LoanService, OpenBook.Domain.Assets.LoanPayment>(s => s.PayAsync(credit.Id, new OpenBook.Application.Assets.LoanPaymentCommand(Mar31, _checking.Id, 500m)));
        Assert.Equal((30m, 470m), (paid.Interest, paid.Principal));

        // An advance of 10,000 with a 2,000 fee: each repayment is one sixth fee.
        var mca = await In<OpenBook.Application.Assets.LoanService, OpenBook.Domain.Assets.Loan>(s => s.CreateAsync(new OpenBook.Application.Assets.CreateLoanCommand(
            new OpenBook.Domain.Assets.LoanDetails("Card advance", advance.Id, Guid.Empty, 10_000m, 0m, 10, Mar31, FactorFee: 2_000m), _checking.Id, Mar1)));
        var repaid = await In<OpenBook.Application.Assets.LoanService, OpenBook.Domain.Assets.LoanPayment>(s => s.PayAsync(mca.Id, new OpenBook.Application.Assets.LoanPaymentCommand(Mar31, _checking.Id, 1_200m)));
        Assert.Equal((200m, 1_000m), (repaid.Interest, repaid.Principal));
        // The last repayment takes what's left of both.
        var last = await In<OpenBook.Application.Assets.LoanService, OpenBook.Domain.Assets.LoanPayment>(s => s.PayAsync(mca.Id, new OpenBook.Application.Assets.LoanPaymentCommand(Mar31, _checking.Id, 10_800m)));
        Assert.Equal((1_800m, 9_000m), (last.Interest, last.Principal));

        var check = await In<OpenBook.Application.Ledger.BooksCheckService, OpenBook.Application.Ledger.BooksCheckReport>(s => s.RunAsync());
        Assert.Equal(OpenBook.Application.Ledger.CheckOutcome.Ok, check.Results.Single(r => r.Key == "loans").Outcome);
        Assert.Equal(0, check.Problems);
    }

    [Fact]
    public async Task A_bank_file_with_lines_above_its_header_is_read_from_the_header()
    {
        const string csv = "Account Name: Checking\nAccount Number: ****1234\nBalance;1,234.56\n\nDate,Description,Amount\n03/01/2026,Coffee,-4.50\n03/02/2026,\"Deposit, branch\",100.00\n";
        var preview = await In<BankImportService, BankImportPreview>(s => s.PreviewAsync(_checking.Id, csv, new BankImportOptions(DateOrder.MonthFirst)));
        Assert.Equal(2, preview.Rows.Count);
        Assert.All(preview.Rows, r => Assert.True(r.IsValid));
        Assert.Equal([-4.50m, 100m], preview.Rows.Select(r => r.Amount!.Value));
        Assert.Equal(6, preview.Rows[0].Line);
    }

    [Fact]
    public async Task Reconciling_starts_with_the_lines_the_bank_feed_added_or_matched_ticked()
    {
        await In<BankFeedService, FeedImportResult>(s => s.QueueAsync(_checking.Id,
            [new FeedLine(Mar1, -100m, "Landlord", null, null, "A1"), new FeedLine(Mar1, -40m, "Shop", null, null, "A2"), new FeedLine(Mar1, -7m, "Later", null, null, "A3")]));
        var pending = await In<BankFeedService, IReadOnlyList<FeedReviewItem>>(s => s.ReviewAsync(_checking.Id, BankFeedStatus.Pending));
        await In<BankFeedService, BankTransaction>(s => s.AddAsync(pending.Single(p => p.Item.FitId == "A1").Item.Id, new AddFeedItemCommand(_rent.Id)));
        var spend = await In<BankingService, BankTransaction>(s => s.RecordAsync(new RecordBankTransactionCommand(BankTransactionKind.Spend, _checking.Id, Mar1, [new BankSplit(_rent.Id, 40m)])));
        await _db.InScope(sp => sp.GetRequiredService<BankFeedService>().MatchAsync(pending.Single(p => p.Item.FitId == "A2").Item.Id, spend.JournalEntryId));
        await In<BankingService, BankTransaction>(s => s.RecordAsync(new RecordBankTransactionCommand(BankTransactionKind.Spend, _checking.Id, Mar1, [new BankSplit(_rent.Id, 9m)])));

        var rec = await In<ReconciliationService, Reconciliation>(s => s.StartAsync(_checking.Id, Mar31, -149m));
        var sheet = await In<ReconciliationService, ReconciliationWorksheet>(s => s.GetWorksheetAsync(rec.Id));
        Assert.Equal(-140m, sheet.ClearedTotal);
        Assert.Equal(3, sheet.Items.Count);
    }

    [Theory]
    [InlineData(new string[0], "1001")]
    [InlineData(new[] { "1001", "1002" }, "1003")]
    [InlineData(new[] { "INV-0041", "INV-0042" }, "INV-0043")]
    [InlineData(new[] { "0099" }, "0100")]
    [InlineData(new[] { "A7", "B7" }, "B8")]
    [InlineData(new[] { "INV-9", "INV-10", "Misc" }, "INV-11")]
    [InlineData(new[] { "5", "6", "7" }, "8")]
    public void Suggested_numbers_keep_their_prefix_and_padding(string[] used, string next) =>
        Assert.Equal(next, SalesAccounts.NextNumber(used));

    [Fact]
    public async Task An_invoice_and_a_credit_memo_cant_share_a_number()
    {
        await _db.CreateAccount("Sales", AccountType.Income, "4000");
        var customer = await In<CustomerService, Customer>(s => s.CreateAsync(new CustomerDetails("Acme")));
        var item = await _db.InScope(sp => sp.GetRequiredService<OpenBook.Application.Sales.ItemService>().CreateQuickAsync("Work", OpenBook.Domain.Sales.ItemType.Service, 100m, false, null));
        await In<CreditService, CreditMemo>(s => s.CreateCreditMemoAsync(new CreateCreditMemoCommand(customer.Id, Mar1, [new SalesLineDraft(item.Id, null, 1m, 10m)], Number: "5000")));
        var refused = await Assert.ThrowsAsync<DomainException>(() => In<InvoiceService, Invoice>(s => s.CreateAsync(new CreateInvoiceCommand(customer.Id, Mar1,
            [new SalesLineDraft(item.Id, null, 1m, 100m)], Number: "5000"))));
        Assert.Equal("invoice.number_taken", refused.Code);
    }

    [Fact]
    public async Task Starting_mid_year_the_year_to_date_step_makes_the_years_profit_whole()
    {
        var income = await _db.CreateAccount("Sales", AccountType.Income, "4000");
        var june30 = new DateOnly(2026, 6, 30);
        // Bank 15,000 on June 30: 5,000 brought from last year and 10,000 profit since January.
        await In<OpenBook.Application.Ledger.OpeningBalanceService, OpenBook.Domain.Ledger.JournalEntry>(s => s.PostAsync(june30, [new(_checking.Id, 15_000m)]));
        await In<OpenBook.Application.Ledger.OpeningBalanceService, OpenBook.Domain.Ledger.JournalEntry>(s => s.PostYearToDateAsync(june30, [new(income.Id, 25_000m), new(_rent.Id, 15_000m)]));

        var lines = await In<OpenBook.Application.Reporting.ReportLedger, IReadOnlyList<LedgerLine>>(s => s.LinesAsync(OpenBook.Domain.Ledger.ReportBasis.Accrual, new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31)));
        Assert.Equal(-25_000m, lines.Where(l => l.AccountId == income.Id).Sum(l => l.Amount));
        Assert.Equal(15_000m, lines.Where(l => l.AccountId == _rent.Id).Sum(l => l.Amount));
        var equity = (await In<OpenBook.Application.Accounts.AccountService, OpenBook.Domain.Accounts.ChartOfAccounts>(s => s.GetChartAsync()))
            .FindByFullName(OpenBook.Application.Ledger.OpeningBalanceService.EquityAccountName)!;
        Assert.Equal(-5_000m, lines.Where(l => l.AccountId == equity.Id).Sum(l => l.Amount));

        var refused = await Assert.ThrowsAsync<DomainException>(() => In<OpenBook.Application.Ledger.OpeningBalanceService, OpenBook.Domain.Ledger.JournalEntry>(
            s => s.PostYearToDateAsync(june30, [new(_checking.Id, 1m)])));
        Assert.Equal("opening.year_to_date_account", refused.Code);
    }

    [Fact]
    public async Task A_bank_line_pays_an_open_invoice_or_bill_and_is_matched_in_one_go()
    {
        await _db.CreateAccount("Sales", AccountType.Income, "4000");
        var item = await _db.InScope(sp => sp.GetRequiredService<ItemService>().CreateQuickAsync("Work", ItemType.Service, 100m, false, null));
        var customer = await In<CustomerService, Customer>(s => s.CreateAsync(new CustomerDetails("Acme")));
        var invoice = await In<InvoiceService, Invoice>(s => s.CreateAsync(new CreateInvoiceCommand(customer.Id, Mar1, [new SalesLineDraft(item.Id, null, 3m, 100m)])));
        var vendor = await In<OpenBook.Application.Purchases.VendorService, OpenBook.Domain.Purchases.Vendor>(s => s.CreateAsync(new OpenBook.Domain.Purchases.VendorDetails("Landlord")));
        var bill = await In<OpenBook.Application.Purchases.BillService, OpenBook.Domain.Purchases.Bill>(s => s.CreateAsync(
            new OpenBook.Application.Purchases.CreateBillCommand(vendor.Id, Mar1, [new OpenBook.Domain.Purchases.BillLineDraft(_rent.Id, 950m)])));
        await In<BankFeedService, FeedImportResult>(s => s.QueueAsync(_checking.Id,
            [new FeedLine(new DateOnly(2026, 3, 8), 300m, "ACME CORP", "CHK 1042", null, "P1"), new FeedLine(new DateOnly(2026, 3, 9), -950m, "LANDLORD", null, null, "P2")]));
        var pending = await In<BankFeedService, IReadOnlyList<FeedReviewItem>>(s => s.ReviewAsync(_checking.Id, BankFeedStatus.Pending));

        var candidates = await In<FeedDocumentService, IReadOnlyDictionary<Guid, IReadOnlyList<FeedDocument>>>(s => s.CandidatesAsync(_checking.Id, [.. pending.Select(p => p.Item)]));
        var paysInvoice = pending.Single(p => p.Item.FitId == "P1").Item;
        var paysBill = pending.Single(p => p.Item.FitId == "P2").Item;
        Assert.Equal(invoice.Id, Assert.Single(candidates[paysInvoice.Id]).Id);
        Assert.Equal(bill.Id, Assert.Single(candidates[paysBill.Id]).Id);

        await In<FeedDocumentService, Guid>(s => s.ApplyAsync(paysInvoice.Id, FeedDocumentKind.Invoice, invoice.Id));
        await In<FeedDocumentService, Guid>(s => s.ApplyAsync(paysBill.Id, FeedDocumentKind.Bill, bill.Id));

        Assert.Equal(0m, (await In<InvoiceService, Invoice?>(s => s.GetAsync(invoice.Id)))!.Balance);
        Assert.Equal(0m, (await In<OpenBook.Application.Purchases.BillService, OpenBook.Domain.Purchases.Bill?>(s => s.GetAsync(bill.Id)))!.Balance);
        Assert.Equal(2, (await In<BankFeedService, IReadOnlyList<FeedReviewItem>>(s => s.ReviewAsync(_checking.Id, BankFeedStatus.Matched))).Count);
        Assert.Equal("feed.not_pending", (await Assert.ThrowsAsync<DomainException>(() => In<FeedDocumentService, Guid>(s => s.ApplyAsync(paysInvoice.Id, FeedDocumentKind.Invoice, invoice.Id)))).Code);
    }

    [Fact]
    public void Text_a_printed_form_may_not_draw_is_pointed_out()
    {
        Assert.Null(OpenBook.Application.Forms.FormScripts.Warning(["Café Müller", "Smith & Sons — €1,250.00", "naïve “quotes”"]));
        Assert.Contains("beyond the Latin alphabet", OpenBook.Application.Forms.FormScripts.Warning(["山田商店"]), StringComparison.Ordinal);
        Assert.Contains("right to left", OpenBook.Application.Forms.FormScripts.Warning(["شركة الأمل", "山田"]), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_year_end_package_takes_a_basis_and_carries_the_mileage_log_when_there_is_one()
    {
        await In<OpenBook.Application.Projects.MileageService, OpenBook.Domain.Projects.MileageTrip>(s => s.CreateAsync(new OpenBook.Domain.Projects.MileageDetails(Mar1, 42m, "Client visit")));
        var file = await In<OpenBook.Application.Reporting.AccountantPackageService, ExportFile>(s => s.BuildAsync(new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31), OpenBook.Domain.Ledger.ReportBasis.Cash));
        try
        {
            using var zip = System.IO.Compression.ZipFile.OpenRead(file.Path);
            Assert.Contains(zip.Entries, e => e.Name == "22 Mileage log.xlsx");
            Assert.DoesNotContain(zip.Entries, e => e.Name == "24 Inventory valuation.xlsx");
            using var readme = new StreamReader(zip.Entries.Single(e => e.Name == "README.txt").Open());
            Assert.Contains("cash basis", await readme.ReadToEndAsync(), StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(file.Path);
        }
    }

    [Fact]
    public async Task The_tax_summary_ends_at_net_profit_with_meals_at_half_and_mileage_beside_it()
    {
        var sales = await _db.CreateAccount("Sales", AccountType.Income, "4000");
        var meals = await _db.CreateAccount("Meals", AccountType.Expense, "6700");
        await In<OpenBook.Application.Accounts.AccountService, int>(s => s.MapTaxLinesAsync(new Dictionary<Guid, string?> { [sales.Id] = "c.1", [meals.Id] = "c.24b", [_rent.Id] = "c.20b" }));
        await In<BankingService, BankTransaction>(s => s.RecordAsync(new RecordBankTransactionCommand(BankTransactionKind.Receive, _checking.Id, Mar1, [new BankSplit(sales.Id, 10_000m)])));
        await In<BankingService, BankTransaction>(s => s.RecordAsync(new RecordBankTransactionCommand(BankTransactionKind.Spend, _checking.Id, Mar1, [new BankSplit(_rent.Id, 2_000m), new BankSplit(meals.Id, 300m)])));
        await In<OpenBook.Application.Projects.MileageService, OpenBook.Domain.Projects.MileageTrip>(s => s.CreateAsync(new OpenBook.Domain.Projects.MileageDetails(Mar1, 100m, "Clients")));

        var table = await In<OpenBook.Application.Reporting.StatementReports, OpenBook.Application.Reporting.ReportTable>(s => s.TaxSummaryTableAsync(new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31)));
        decimal? Value(string label) => table.Rows.FirstOrDefault(r => r.Cells[0].Value is string text && text.StartsWith(label, StringComparison.Ordinal))?.Cells[1].Value as decimal?;
        Assert.Equal(-150m, Value("Less half of meals"));
        Assert.Equal(7_850m, Value("Net profit on tax lines (line 31)"));
        Assert.NotNull(Value("Standard mileage: 100.0 business miles"));
    }

    [Fact]
    public async Task Depreciation_and_loan_interest_post_to_the_class_of_their_asset_and_loan()
    {
        var program = await In<OpenBook.Application.Segments.SegmentService, OpenBook.Domain.Segments.Segment>(s => s.CreateAsync(OpenBook.Domain.Segments.SegmentKind.Class, "Food pantry"));
        var equipment = await _db.CreateAccount("Equipment", AccountType.FixedAsset, "1500");
        var loanAccount = await _db.CreateAccount("Van loan", AccountType.LongTermLiability, "2500");
        var asset = await In<OpenBook.Application.Assets.FixedAssetService, OpenBook.Domain.Assets.FixedAsset>(s => s.CreateAsync(new OpenBook.Domain.Assets.FixedAssetDetails(
            "Freezer", equipment.Id, Guid.Empty, Guid.Empty, new DateOnly(2026, 1, 1), 1_200m, OpenBook.Domain.Assets.DepreciationMethod.StraightLine, 12, ClassId: program.Id)));
        await _db.InScope(sp => sp.GetRequiredService<OpenBook.Application.Assets.DepreciationService>().RunAsync(Mar31));
        var loan = await In<OpenBook.Application.Assets.LoanService, OpenBook.Domain.Assets.Loan>(s => s.CreateAsync(new OpenBook.Application.Assets.CreateLoanCommand(
            new OpenBook.Domain.Assets.LoanDetails("Van", loanAccount.Id, Guid.Empty, 12_000m, 6m, 24, Mar1, ClassId: program.Id))));
        await In<OpenBook.Application.Assets.LoanService, OpenBook.Domain.Assets.LoanPayment>(s => s.PayAsync(loan.Id, new OpenBook.Application.Assets.LoanPaymentCommand(Mar1, _checking.Id)));

        var lines = await In<OpenBook.Application.Reporting.ReportLedger, IReadOnlyList<LedgerLine>>(s => s.LinesAsync(OpenBook.Domain.Ledger.ReportBasis.Accrual, new DateOnly(2026, 1, 1), Mar31));
        Assert.Equal(300m, lines.Where(l => l.ClassId == program.Id && l.Source == OpenBook.Domain.Assets.DepreciationRun.Source).Sum(l => l.Amount));
        Assert.Equal(60m, lines.Where(l => l.ClassId == program.Id && l.Source == OpenBook.Domain.Assets.Loan.PaymentSource).Sum(l => l.Amount));
    }

    [Fact]
    public async Task A_grants_new_class_can_go_under_the_program_it_pays_for()
    {
        await _db.CreateAccount("Grants", AccountType.Income, "4300");
        var program = await In<OpenBook.Application.Segments.SegmentService, OpenBook.Domain.Segments.Segment>(s => s.CreateAsync(OpenBook.Domain.Segments.SegmentKind.Class, "Food pantry"));
        var funder = await In<CustomerService, Customer>(s => s.CreateAsync(new CustomerDetails("County")));
        var grant = await In<GrantService, Grant>(s => s.CreateAsync(new GrantCommand("USDA", funder.Id, 5_000m, Mar1, Mar1, Mar31, ParentClassId: program.Id)));
        var made = await In<OpenBook.Application.Segments.SegmentService, OpenBook.Domain.Segments.Segment?>(s => s.GetAsync(grant.ClassId));
        Assert.Equal(program.Id, made!.ParentId);
    }

    [Fact]
    public void Twice_a_year_filing_periods_are_halves_of_the_year()
    {
        Assert.Equal((new DateOnly(2026, 7, 1), new DateOnly(2026, 12, 31)), OpenBook.Domain.SalesTax.FilingPeriods.Containing(OpenBook.Domain.SalesTax.FilingFrequency.Semiannually, new DateOnly(2026, 8, 15)));
        Assert.Equal((new DateOnly(2026, 1, 1), new DateOnly(2026, 6, 30)), OpenBook.Domain.SalesTax.FilingPeriods.LastCompleted(OpenBook.Domain.SalesTax.FilingFrequency.Semiannually, new DateOnly(2026, 8, 15)));
    }

    [Fact]
    public async Task An_asset_entered_by_mistake_comes_off_the_register_until_depreciation_posts()
    {
        var equipment = await _db.CreateAccount("Equipment", AccountType.FixedAsset, "1500");
        OpenBook.Domain.Assets.FixedAssetDetails Details(string name) =>
            new(name, equipment.Id, Guid.Empty, Guid.Empty, new DateOnly(2026, 1, 1), 1_200m, OpenBook.Domain.Assets.DepreciationMethod.StraightLine, 12);
        var wrong = await In<OpenBook.Application.Assets.FixedAssetService, OpenBook.Domain.Assets.FixedAsset>(s => s.CreateAsync(Details("Typo")));
        var real = await In<OpenBook.Application.Assets.FixedAssetService, OpenBook.Domain.Assets.FixedAsset>(s => s.CreateAsync(Details("Laptop")));

        await In<OpenBook.Application.Assets.FixedAssetService, OpenBook.Domain.Assets.FixedAsset>(s => s.RemoveAsync(wrong.Id));
        var run = await _db.InScope(sp => sp.GetRequiredService<OpenBook.Application.Assets.DepreciationService>().RunAsync(Mar31));
        Assert.Equal([real.Id], run.Lines.Select(l => l.AssetId).Distinct());
        var refused = await Assert.ThrowsAsync<DomainException>(() => In<OpenBook.Application.Assets.FixedAssetService, OpenBook.Domain.Assets.FixedAsset>(s => s.RemoveAsync(real.Id)));
        Assert.Equal("asset.depreciated", refused.Code);
        Assert.Equal(OpenBook.Domain.Assets.FixedAssetStatus.Active,
            (await In<OpenBook.Application.Assets.FixedAssetService, OpenBook.Domain.Assets.FixedAsset>(s => s.PutBackAsync(wrong.Id))).Status);
    }

    [Fact]
    public async Task A_check_number_already_used_on_the_account_is_noticed()
    {
        await In<BankingService, BankTransaction>(s => s.RecordAsync(new RecordBankTransactionCommand(BankTransactionKind.Spend, _checking.Id, Mar1, [new BankSplit(_rent.Id, 50m)], Reference: "1042")));
        Assert.True(await In<BankingService, bool>(s => s.CheckNumberUsedAsync(_checking.Id, " 1042 ")));
        Assert.False(await In<BankingService, bool>(s => s.CheckNumberUsedAsync(_checking.Id, "1043")));
    }

    [Fact]
    public async Task A_barcode_too_long_to_scan_on_a_label_is_refused()
    {
        var sales = await _db.CreateAccount("Sales", AccountType.Income, "4000");
        var shortCode = await In<ItemService, Item>(s => s.CreateAsync(new ItemDetails("Mug", ItemType.Service, sales.Id, 8m, Barcode: "012345678905")));
        var longCode = await In<ItemService, Item>(s => s.CreateAsync(new ItemDetails("Kit", ItemType.Service, sales.Id, 8m, Barcode: "KIT-DELUXE-EDITION-BLUE-LARGE-2026")));
        Assert.NotEmpty(await In<OpenBook.Application.Inventory.LabelService, byte[]>(s => s.RenderAsync([(shortCode.Id, 1)], showPrice: false)));
        var refused = await Assert.ThrowsAsync<DomainException>(() => In<OpenBook.Application.Inventory.LabelService, byte[]>(s => s.RenderAsync([(longCode.Id, 1)], showPrice: false)));
        Assert.Equal("label.barcode_too_long", refused.Code);
    }

    [Fact]
    public async Task Checks_printed_without_a_number_are_numbered_from_the_first_given()
    {
        var first = await In<BankingService, BankTransaction>(s => s.RecordAsync(new RecordBankTransactionCommand(BankTransactionKind.Spend, _checking.Id, Mar1, [new BankSplit(_rent.Id, 50m)], Payee: "A")));
        var second = await In<BankingService, BankTransaction>(s => s.RecordAsync(new RecordBankTransactionCommand(BankTransactionKind.Spend, _checking.Id, Mar1, [new BankSplit(_rent.Id, 60m)], Payee: "B")));
        var numbered = await In<BankingService, BankTransaction>(s => s.RecordAsync(new RecordBankTransactionCommand(BankTransactionKind.Spend, _checking.Id, Mar1, [new BankSplit(_rent.Id, 70m)], Payee: "C", Reference: "900")));

        await In<CheckService, PrintedChecks>(s => s.PrintAsync([(CheckKind.Expense, first.Id), (CheckKind.Expense, numbered.Id), (CheckKind.Expense, second.Id)], "0042"));

        Assert.Equal(("0042", "900", "0043"), ((await In<BankingService, BankTransaction?>(s => s.GetAsync(first.Id)))!.Reference,
            (await In<BankingService, BankTransaction?>(s => s.GetAsync(numbered.Id)))!.Reference, (await In<BankingService, BankTransaction?>(s => s.GetAsync(second.Id)))!.Reference));
    }

    [Fact]
    public async Task A_customer_over_their_credit_limit_or_on_hold_is_warned_about()
    {
        await _db.CreateAccount("Sales", AccountType.Income, "4000");
        var item = await _db.InScope(sp => sp.GetRequiredService<ItemService>().CreateQuickAsync("Work", ItemType.Service, 100m, false, null));
        var customer = await In<CustomerService, Customer>(s => s.CreateAsync(new CustomerDetails("Wholesale Co", CreditLimit: 500m)));
        Assert.Null(await In<CustomerService, string?>(s => s.CreditWarningAsync(customer.Id)));
        await In<InvoiceService, Invoice>(s => s.CreateAsync(new CreateInvoiceCommand(customer.Id, Mar1, [new SalesLineDraft(item.Id, null, 6m, 100m)])));
        Assert.Contains("credit limit of 500.00", await In<CustomerService, string?>(s => s.CreditWarningAsync(customer.Id)), StringComparison.Ordinal);
        await In<CustomerService, Customer>(s => s.UpdateAsync(customer.Id, new CustomerDetails("Wholesale Co", CreditHold: true)));
        Assert.Contains("credit hold", await In<CustomerService, string?>(s => s.CreditWarningAsync(customer.Id)), StringComparison.Ordinal);
    }
}
