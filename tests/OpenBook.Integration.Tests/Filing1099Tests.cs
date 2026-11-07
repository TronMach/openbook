using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OpenBook.Application.Banking;
using OpenBook.Application.Companies;
using OpenBook.Application.Forms;
using OpenBook.Application.Purchases;
using OpenBook.Application.Reporting;
using OpenBook.Domain.Accounts;
using OpenBook.Domain.Banking;
using OpenBook.Domain.Common;
using OpenBook.Domain.Purchases;

namespace OpenBook.Integration.Tests;

/// <summary>What surrounds filing 1099s: copies for vendors, W-9 requests, the note of what was filed and the file's fields.</summary>
public sealed class Filing1099Tests : IAsyncLifetime
{
    private static readonly DateOnly Mar1 = new(2025, 3, 1);

    private readonly LedgerDatabase _db = new();
    private Account _checking = null!;
    private Account _contractors = null!;
    private Vendor _dana = null!;
    private Vendor _noNumber = null!;

    public async Task InitializeAsync()
    {
        await _db.InitializeAsync();
        _checking = await _db.CreateAccount("Checking", AccountType.Bank, "1000");
        _contractors = await _db.CreateAccount("Contractors", AccountType.Expense, "6000");
        _dana = await In<VendorService, Vendor>(s => s.CreateAsync(new VendorDetails("Dana Designs", Email: "dana@designs.test",
            Address: "1 Main St\nSuite 4\nSpringfield, IL 62701", Track1099: true, TaxId: "12-3456789")));
        _noNumber = await In<VendorService, Vendor>(s => s.CreateAsync(new VendorDetails("No Number", Track1099: true)));
        await Pay(_dana, 5_000m);
        await Pay(_noNumber, 900m);
    }

    public Task DisposeAsync() => _db.DisposeAsync();

    private Task<T> In<TService, T>(Func<TService, Task<T>> work) where TService : notnull =>
        _db.InScope(sp => work(sp.GetRequiredService<TService>()));

    private Task<BankTransaction> Pay(Vendor vendor, decimal amount) =>
        In<BankingService, BankTransaction>(s => s.RecordAsync(new RecordBankTransactionCommand(
            BankTransactionKind.Spend, _checking.Id, Mar1, [new BankSplit(_contractors.Id, amount)], VendorId: vendor.Id)));

    private Task<Domain.Companies.EmailSettings> UseEmail() =>
        _db.InScope(sp => sp.GetRequiredService<EmailService>().SaveSettingsAsync(new EmailSettingsCommand(
            Domain.Companies.EmailDelivery.Smtp, "smtp.test", 587, Domain.Companies.SmtpSecurity.Auto, "books", "pw", false, "books@studio.test", "Studio", null, null)));

    [Fact]
    public async Task Materials_in_accounts_marked_not_reportable_come_off_the_1099_total()
    {
        var materials = await _db.CreateAccount("Materials", AccountType.CostOfGoodsSold, "5100");
        await In<OpenBook.Application.Accounts.AccountService, Account>(s => s.UpdateAsync(new OpenBook.Application.Accounts.UpdateAccountCommand(
            materials.Id, materials.Name, materials.Type, materials.Number, null, null, Excluded1099: true)));

        // An expense with labor and parts on one check, and a bill of 600 labor + 400 parts paid half.
        await In<BankingService, BankTransaction>(s => s.RecordAsync(new RecordBankTransactionCommand(BankTransactionKind.Spend, _checking.Id, Mar1,
            [new BankSplit(_contractors.Id, 300m), new BankSplit(materials.Id, 200m)], VendorId: _dana.Id)));
        var bill = await In<BillService, Bill>(s => s.CreateAsync(new CreateBillCommand(_dana.Id, Mar1,
            [new BillLineDraft(_contractors.Id, 600m), new BillLineDraft(materials.Id, 400m)], Reference: "D-1")));
        await In<BillPaymentService, IReadOnlyList<BillPayment>>(s => s.PayAsync(new PayBillsCommand(Mar1.AddDays(10), _checking.Id, [new ApplyToBill(bill.Id, 500m)])));

        var row = (await In<PayablesService, Report1099>(s => s.Report1099Async(2025))).Vendors.Single(v => v.Vendor.Id == _dana.Id);
        Assert.Equal((5_000m + 300m + 300m, 200m + 200m), (row.Total, row.NotReportable));
    }

    [Fact]
    public async Task The_vendor_import_sets_1099_tracking_the_tax_id_and_the_box_and_never_shows_the_number()
    {
        const string csv = "Vendor,Eligible for 1099,Tax ID,1099 box\nLandlord LLC,Yes,98-7654321,MISC 1 Rents\nLawyer & Co,,11-2223334,Attorney\nPlain Supply,No,,\n";
        var preview = await In<VendorImportService, OpenBook.Application.Sales.ImportResult<VendorImportRow>>(s => s.PreviewAsync(csv));
        Assert.All(preview.Rows, r => Assert.Equal(OpenBook.Application.Accounts.ImportRowStatus.New, r.Status));
        Assert.Equal("•••••4321", preview.Rows[0].Details!.TaxId);
        await In<VendorImportService, OpenBook.Application.Sales.ImportResult<VendorImportRow>>(s => s.ImportAsync(csv));

        var vendors = (await In<VendorService, IReadOnlyList<VendorSummary>>(s => s.ListAsync(Mar1))).Select(v => v.Vendor).ToDictionary(v => v.DisplayName);
        Assert.Equal((true, "98-7654321", Form1099Box.MiscRents), (vendors["Landlord LLC"].Track1099, vendors["Landlord LLC"].TaxId, vendors["Landlord LLC"].Form1099Box));
        Assert.Equal((true, Form1099Box.MiscAttorney), (vendors["Lawyer & Co"].Track1099, vendors["Lawyer & Co"].Form1099Box));
        Assert.False(vendors["Plain Supply"].Track1099);
        Assert.Equal("import.1099_box", Assert.Throws<DomainException>(() => VendorImportService.ParseBox("Box 99")).Code);
    }

    [Fact]
    public async Task Paid_before_OpenBook_counts_toward_the_total_and_can_be_taken_away()
    {
        await In<Filing1099Service, int>(async s => { await s.SetPriorAsync(_noNumber.Id, 2025, 1_400m, "Old books"); return 0; });
        var row = (await In<PayablesService, Report1099>(s => s.Report1099Async(2025, 2_000m))).Vendors.Single(v => v.Vendor.Id == _noNumber.Id);
        Assert.Equal((2_300m, 1_400m, true), (row.Total, row.Prior, row.MeetsThreshold));

        await In<Filing1099Service, int>(async s => { await s.SetPriorAsync(_noNumber.Id, 2025, 0m, null); return 0; });
        row = (await In<PayablesService, Report1099>(s => s.Report1099Async(2025, 2_000m))).Vendors.Single(v => v.Vendor.Id == _noNumber.Id);
        Assert.Equal((900m, 0m, false), (row.Total, row.Prior, row.MeetsThreshold));
        Assert.Equal(2, (await _db.InDb(db => db.AuditRecords.Where(r => r.EntityId == _noNumber.Id && r.Action == Domain.Auditing.AuditAction.Updated).CountAsync())));
    }

    [Fact]
    public void Addresses_come_apart_for_the_filing_file()
    {
        Assert.Equal(new AddressParts("1 Main St, Suite 4", "Springfield", "IL", "62701"), AddressParts.Parse("1 Main St\nSuite 4\nSpringfield, IL 62701"));
        Assert.Equal(new AddressParts("9 Oak Ave", "New York", "NY", "10001-1234"), AddressParts.Parse("9 Oak Ave\r\nNew York NY 10001-1234"));
        Assert.Equal(new AddressParts("Somewhere, Abroad", "", "", ""), AddressParts.Parse("Somewhere\nAbroad"));
        Assert.Equal(new AddressParts("", "", "", ""), AddressParts.Parse(null));
    }

    [Fact]
    public async Task The_file_has_the_payer_number_and_split_addresses_and_the_threshold_typed()
    {
        var company = await In<CompanySettingsService, Domain.Companies.Company>(s => s.GetAsync());
        await In<CompanySettingsService, Domain.Companies.Company>(s => s.UpdateAsync(new UpdateCompanySettingsCommand(
            company.Name, company.BusinessType, company.LegalName, null, null, "5 Elm Rd\nDover, DE 19901", company.FiscalYearStartMonth, false, company.DefaultReportBasis, TaxId: "98 7654321")));

        var rows = Encoding.UTF8.GetString(await In<AccountantPackageService, byte[]>(s => s.Filing1099CsvAsync(2025, threshold: 800m))).Split("\r\n");

        Assert.Contains("Recipient street,Recipient city,Recipient state,Recipient ZIP,Amount,Payer name,Payer TIN,Payer street", rows[0], StringComparison.Ordinal);
        Assert.Contains("\"1 Main St, Suite 4\",\"Springfield\",\"IL\",\"62701\",5000.00", rows[1], StringComparison.Ordinal);
        Assert.Contains("\"98-7654321\",\"5 Elm Rd\",\"Dover\",\"DE\",\"19901\"", rows[1], StringComparison.Ordinal);
        Assert.Contains("No Number", rows[2], StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_employer_id_needs_nine_digits()
    {
        var company = await In<CompanySettingsService, Domain.Companies.Company>(s => s.GetAsync());
        var refused = await Assert.ThrowsAsync<DomainException>(() => In<CompanySettingsService, Domain.Companies.Company>(s => s.UpdateAsync(new UpdateCompanySettingsCommand(
            company.Name, company.BusinessType, null, null, null, null, 1, false, company.DefaultReportBasis, TaxId: "123"))));
        Assert.Equal("company.tax_id_invalid", refused.Code);
    }

    [Fact]
    public async Task Copies_print_together_or_one_at_a_time()
    {
        Assert.NotEmpty((await In<Filing1099Service, RenderedForm>(s => s.CopiesAsync(2025))).Pdf);
        var one = await In<Filing1099Service, RenderedForm>(s => s.CopiesAsync(2025, vendorId: _dana.Id));
        Assert.Contains("Dana Designs", one.FileName, StringComparison.Ordinal);

        var none = await Assert.ThrowsAsync<DomainException>(() => In<Filing1099Service, RenderedForm>(s => s.CopiesAsync(2024)));
        Assert.Equal("filing1099.none_due", none.Code);
    }

    [Fact]
    public async Task Emailing_copies_goes_to_those_with_addresses_and_reports_the_rest()
    {
        await UseEmail();
        await In<Filing1099Service, int>(s => s.MarkFiledAsync(2025, new DateOnly(2026, 1, 20)));

        var run = await In<Filing1099Service, Copies1099Run>(s => s.EmailCopiesAsync(2025));

        Assert.Equal(1, run.Sent);
        Assert.Equal(["No Number"], run.NoEmail);
        var message = Assert.Single(_db.Mail.Sent).Message;
        Assert.Equal("dana@designs.test", message.To);
        Assert.Single(message.Attachments);
        var filed = await In<Filing1099Service, IReadOnlyDictionary<Guid, Filing1099>>(s => s.FiledAsync(2025));
        Assert.NotNull(filed[_dana.Id].CopySentAt);
        Assert.Null(filed[_noNumber.Id].CopySentAt);
    }

    [Fact]
    public async Task A_w9_request_needs_an_address_and_goes_with_the_form_linked()
    {
        await UseEmail();
        var refused = await Assert.ThrowsAsync<DomainException>(() => _db.InScope(sp => sp.GetRequiredService<Filing1099Service>().RequestW9Async(_noNumber.Id)));
        Assert.Equal("filing1099.no_email", refused.Code);

        await _db.InScope(sp => sp.GetRequiredService<Filing1099Service>().RequestW9Async(_dana.Id));
        Assert.Contains("fw9.pdf", Assert.Single(_db.Mail.Sent).Message.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Filing_is_noted_per_vendor_once_and_can_be_taken_back()
    {
        var on = new DateOnly(2026, 1, 20);
        Assert.Equal(2, await In<Filing1099Service, int>(s => s.MarkFiledAsync(2025, on)));
        var filed = await In<Filing1099Service, IReadOnlyDictionary<Guid, Filing1099>>(s => s.FiledAsync(2025));
        Assert.Equal((5_000m, Form1099Box.NecNonemployeeCompensation, on), (filed[_dana.Id].Amount, filed[_dana.Id].Box, filed[_dana.Id].FiledOn));

        await Pay(_dana, 100m);
        Assert.Equal(0, await In<Filing1099Service, int>(s => s.MarkFiledAsync(2025, on)));
        Assert.Equal(5_000m, (await In<Filing1099Service, IReadOnlyDictionary<Guid, Filing1099>>(s => s.FiledAsync(2025)))[_dana.Id].Amount);

        await _db.InScope(sp => sp.GetRequiredService<Filing1099Service>().ClearFiledAsync(2025));
        Assert.Empty(await In<Filing1099Service, IReadOnlyDictionary<Guid, Filing1099>>(s => s.FiledAsync(2025)));
    }

    [Fact]
    public async Task Vendors_due_a_form_with_no_number_are_listed()
    {
        var missing = await In<Filing1099Service, IReadOnlyList<Vendor>>(s => s.MissingTaxIdsAsync(2025));
        Assert.Equal(_noNumber.Id, Assert.Single(missing).Id);
    }
}
