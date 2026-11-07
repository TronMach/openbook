using System.IO.Compression;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using OpenBook.Application.Assets;
using OpenBook.Application.Banking;
using OpenBook.Application.Ledger;
using OpenBook.Application.Purchases;
using OpenBook.Application.Reporting;
using OpenBook.Application.Sales;
using OpenBook.Domain.Accounts;
using OpenBook.Domain.Assets;
using OpenBook.Domain.Banking;
using OpenBook.Domain.Common;
using OpenBook.Domain.Ledger;
using OpenBook.Domain.Purchases;
using OpenBook.Domain.Reporting;
using OpenBook.Domain.Sales;

namespace OpenBook.Integration.Tests;

/// <summary>L6 slice 4: cash-flow forecast with scenarios, adjusting entries and the worksheet, the accountant's package and 1099 data.</summary>
public sealed class AccountantToolsTests : IAsyncLifetime
{
    private static readonly DateOnly Mar1 = new(2026, 3, 1);

    private readonly LedgerDatabase _db = new();
    private Account _checking = null!;
    private Account _equity = null!;
    private Account _fees = null!;
    private Account _contractors = null!;

    public async Task InitializeAsync()
    {
        await _db.InitializeAsync();
        _checking = await _db.CreateAccount("Checking", AccountType.Bank, "1000");
        _equity = await _db.CreateAccount("Owner Investment", AccountType.Equity, "3000");
        _fees = await _db.CreateAccount("Fees", AccountType.Income, "4000");
        _contractors = await _db.CreateAccount("Contractors", AccountType.Expense, "6000");
        await Post(Mar1, _checking, _equity, 1_000m);
    }

    public Task DisposeAsync() => _db.DisposeAsync();

    private Task<T> In<TService, T>(Func<TService, Task<T>> work) where TService : notnull =>
        _db.InScope(sp => work(sp.GetRequiredService<TService>()));

    private Task<JournalEntry> Post(DateOnly date, Account debit, Account credit, decimal amount, string? memo = null) =>
        In<PostingEngine, JournalEntry>(s => s.PostAsync(new PostJournalEntryCommand(date, [JournalLineDraft.Dr(debit.Id, amount), JournalLineDraft.Cr(credit.Id, amount)], memo)));

    [Fact]
    public async Task The_forecast_pays_open_purchase_orders_on_their_expected_date()
    {
        var vendor = await In<VendorService, Vendor>(s => s.CreateAsync(new VendorDetails("Mill")));
        await In<PurchaseOrderService, PurchaseOrder>(s => s.CreateAsync(new SavePurchaseOrderCommand(vendor.Id, Mar1,
            [new PurchaseOrderLineDraft(null, _contractors.Id, "Cloth", 1m, 300m)], Mar1.AddDays(10))));
        var forecast = await In<ForecastService, CashForecast>(s => s.ForecastAsync(Mar1, 4));
        Assert.Equal(-300m, forecast.Weeks[1].Bills);
    }

    [Fact]
    public async Task The_forecast_collects_invoices_pays_bills_and_loans_and_applies_a_scenario()
    {
        var item = await In<ItemService, Item>(s => s.CreateAsync(new ItemDetails("Advice", ItemType.Service, _fees.Id, 500m)));
        var customer = await In<CustomerService, Customer>(s => s.CreateAsync(new CustomerDetails("Acme", PaymentTermsDays: 15)));
        await In<InvoiceService, Invoice>(s => s.CreateAsync(new CreateInvoiceCommand(customer.Id, Mar1, [new SalesLineDraft(item.Id, null, 1m, 500m)])));
        var vendor = await In<VendorService, Vendor>(s => s.CreateAsync(new VendorDetails("Freelancer")));
        await In<BillService, Bill>(s => s.CreateAsync(new CreateBillCommand(vendor.Id, Mar1, [new BillLineDraft(_contractors.Id, 200m)], DueDate: Mar1.AddDays(3))));
        var loanAccount = await _db.CreateAccount("Loan", AccountType.LongTermLiability, "2500");
        await In<LoanService, Loan>(s => s.CreateAsync(new CreateLoanCommand(new LoanDetails("Loan", loanAccount.Id, Guid.Empty, 1_200m, 0m, 12, Mar1.AddDays(20)))));

        var plain = await In<ForecastService, CashForecast>(s => s.ForecastAsync(Mar1, 4));
        Assert.Equal(1_000m, plain.Opening);
        Assert.Equal(-200m, plain.Weeks[0].Bills);
        Assert.Equal(500m, plain.Weeks[2].Collections); // due March 16
        Assert.Equal(-100m, plain.Weeks[2].Loans); // March 21
        Assert.Equal(1_200m, plain.Weeks[^1].Closing);

        var scenario = await In<ForecastService, ForecastScenario>(s => s.CreateScenarioAsync(new ForecastScenarioDetails("Slow payers", CollectionDelayDays: 30)));
        await In<ForecastService, ForecastScenario>(s => s.AddItemAsync(scenario.Id, new ScenarioItemDetails("New hire", -300m, Mar1.AddDays(5), ScenarioRecurrence.Weekly)));
        var slow = await In<ForecastService, CashForecast>(s => s.ForecastAsync(Mar1, 4, scenario.Id));
        Assert.Equal(0m, slow.Weeks.Sum(w => w.Collections));
        Assert.Equal(-1_200m, slow.Weeks.Sum(w => w.Other));
        Assert.Equal(-500m, slow.LowestClosing); // 1,000 − 200 bill − 100 loan − 4 × 300

        var table = await _db.InScope(sp => sp.GetRequiredService<ReportCatalog>().RunAsync("cash-forecast", ReportParameters.Parse($"asOf=2026-03-01&weeks=4&scenarioId={scenario.Id}")));
        Assert.Equal(-500m, table.Find("Total", 6));
        Assert.Equal("scenario.amount_zero", (await Assert.ThrowsAsync<DomainException>(() =>
            In<ForecastService, ForecastScenario>(s => s.AddItemAsync(scenario.Id, new ScenarioItemDetails("Nothing", 0m, Mar1))))).Code);
    }

    [Fact]
    public async Task Adjusting_entries_show_apart_on_the_worksheet()
    {
        var fees = await Post(Mar1.AddDays(2), _checking, _fees, 300m);
        var accrual = await Post(Mar1.AddDays(30), _contractors, _checking, 120m, "Year-end accrual");
        await In<AdjustingEntryService, bool>(async s => { await s.SetAsync(accrual.Id, true); return true; });

        var lines = await In<AdjustingEntryService, IReadOnlyList<WorksheetLine>>(s => s.WorksheetAsync(new DateOnly(2026, 12, 31), ReportBasis.Accrual));
        var contractors = lines.Single(l => l.AccountId == _contractors.Id);
        Assert.Equal((0m, 120m, 120m), (contractors.Unadjusted, contractors.Adjustments, contractors.Adjusted));
        var checking = lines.Single(l => l.AccountId == _checking.Id);
        Assert.Equal((1_300m, -120m, 1_180m), (checking.Unadjusted, checking.Adjustments, checking.Adjusted));

        var table = await _db.InScope(sp => sp.GetRequiredService<ReportCatalog>().RunAsync("trial-balance-worksheet", ReportParameters.Parse("asOf=2026-12-31")));
        Assert.Equal(table.Find("Total", 3), table.Find("Total", 4));
        var entries = await _db.InScope(sp => sp.GetRequiredService<ReportCatalog>().RunAsync("adjusting-entries", ReportParameters.Parse("preset=Custom&from=2026-01-01&to=2026-12-31")));
        Assert.Equal(120m, entries.Find("Total", 4));

        await In<PostingEngine, JournalEntry>(s => s.VoidAsync(accrual.Id, "Wrong amount"));
        Assert.DoesNotContain(await In<AdjustingEntryService, IReadOnlyList<WorksheetLine>>(s => s.WorksheetAsync(new DateOnly(2026, 12, 31), ReportBasis.Accrual)),
            l => l.Adjustments != 0m);

        var vendor = await In<VendorService, Vendor>(s => s.CreateAsync(new VendorDetails("Shop")));
        var bill = await In<BillService, Bill>(s => s.CreateAsync(new CreateBillCommand(vendor.Id, Mar1, [new BillLineDraft(_contractors.Id, 50m)])));
        Assert.Equal("adjusting.not_journal", (await Assert.ThrowsAsync<DomainException>(() =>
            In<AdjustingEntryService, bool>(async s => { await s.SetAsync(bill.JournalEntryId, true); return true; }))).Code);
        await In<AdjustingEntryService, bool>(async s => { await s.SetAsync(fees.Id, true); await s.SetAsync(fees.Id, false); return true; });
        Assert.DoesNotContain(fees.Id, await In<AdjustingEntryService, IReadOnlySet<Guid>>(s => s.MarkedAsync()));
    }

    [Fact]
    public async Task The_1099_file_has_what_was_paid_outside_cards()
    {
        var card = await _db.CreateAccount("Visa", AccountType.CreditCard, "2100");
        var contractor = await In<VendorService, Vendor>(s => s.CreateAsync(new VendorDetails("Dana Designs", Track1099: true, TaxId: "12-3456789")));
        await In<BankingService, BankTransaction>(s => s.RecordAsync(new RecordBankTransactionCommand(
            BankTransactionKind.Spend, _checking.Id, Mar1, [new BankSplit(_contractors.Id, 5_000m)], VendorId: contractor.Id)));
        await In<BankingService, BankTransaction>(s => s.RecordAsync(new RecordBankTransactionCommand(
            BankTransactionKind.Spend, card.Id, Mar1, [new BankSplit(_contractors.Id, 1_000m)], VendorId: contractor.Id)));

        var csv = Encoding.UTF8.GetString(await In<AccountantPackageService, byte[]>(s => s.Filing1099CsvAsync(2026)));

        Assert.Contains(",5000.00,", csv.Split("\r\n")[1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_package_holds_every_report_and_the_1099_data_with_full_tax_ids()
    {
        var contractor = await In<VendorService, Vendor>(s => s.CreateAsync(new VendorDetails("Dana Designs", Address: "1 Main St\nSpringfield", Track1099: true, TaxId: "12-3456789")));
        await In<BankingService, BankTransaction>(s => s.RecordAsync(new RecordBankTransactionCommand(
            BankTransactionKind.Spend, _checking.Id, Mar1.AddDays(1), [new BankSplit(_contractors.Id, 2_500m)], VendorId: contractor.Id)));

        var csv = Encoding.UTF8.GetString(await In<AccountantPackageService, byte[]>(s => s.Filing1099CsvAsync(2026)));
        var row = csv.Split("\r\n")[1];
        Assert.StartsWith("1099-NEC,1,\"Dana Designs\"", row.TrimStart('﻿'), StringComparison.Ordinal);
        Assert.Contains("\"12-3456789\",EIN,\"1 Main St, Springfield\",\"\",\"\",\"\",2500.00", row, StringComparison.Ordinal);

        var file = await In<AccountantPackageService, Application.ExportFile>(s => s.BuildAsync(new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31)));
        try
        {
            using var zip = ZipFile.OpenRead(file.Path);
            Assert.Equal(AccountantPackageService.Reports.Count + 2, zip.Entries.Count);
            Assert.Contains(zip.Entries, e => e.Name == "16 1099 filing data 2026.csv");
            Assert.All(zip.Entries.Where(e => e.Name.EndsWith(".xlsx", StringComparison.Ordinal)), e => Assert.True(e.Length > 0));
        }
        finally
        {
            File.Delete(file.Path);
        }

        Assert.Contains(await _db.InDb(db => Task.FromResult(db.AuditRecords.Where(a => a.Action == Domain.Auditing.AuditAction.Exported).ToList())), a => a.EntityType == "company");
    }
}
