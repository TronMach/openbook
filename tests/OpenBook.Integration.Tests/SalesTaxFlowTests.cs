using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OpenBook.Application.Ledger;
using OpenBook.Application.Sales;
using OpenBook.Application.SalesTax;
using OpenBook.Domain.Accounts;
using OpenBook.Domain.Common;
using OpenBook.Domain.Ledger;
using OpenBook.Domain.Sales;
using OpenBook.Domain.SalesTax;

namespace OpenBook.Integration.Tests;

/// <summary>V1.5: tax agencies, rates and codes; tax on sales documents; exemptions; the liability report; tax payments.</summary>
public sealed class SalesTaxFlowTests : IAsyncLifetime
{
    private static readonly DateOnly Mar1 = new(2026, 3, 1);

    private readonly LedgerDatabase _db = new();
    private Account _checking = null!;
    private Account _sales = null!;
    private Account _penalties = null!;
    private TaxAgency _stateDor = null!;
    private TaxAgency _city = null!;
    private TaxRate _state = null!;
    private TaxRate _cityRate = null!;
    private TaxCode _springfield = null!;
    private Item _widget = null!;
    private Item _labor = null!;
    private Customer _acme = null!;
    private Customer _reseller = null!;

    public async Task InitializeAsync()
    {
        await _db.InitializeAsync();
        _checking = await _db.CreateAccount("Checking", AccountType.Bank, "1000");
        _sales = await _db.CreateAccount("Sales", AccountType.Income, "4000");
        _penalties = await _db.CreateAccount("Penalties", AccountType.Expense, "6900");

        _stateDor = await Tax(s => s.CreateAgencyAsync(new TaxAgencyDetails("State Department of Revenue", FilingFrequency.Monthly, "ST-1")));
        _city = await Tax(s => s.CreateAgencyAsync(new TaxAgencyDetails("City of Springfield")));
        _state = await Tax(s => s.CreateRateAsync(new TaxRateDetails("State", _stateDor.Id, 6.25m)));
        _cityRate = await Tax(s => s.CreateRateAsync(new TaxRateDetails("Springfield city", _city.Id, 2m), createCode: false));
        _springfield = await Tax(s => s.CreateCodeAsync(new TaxCodeDetails("Springfield", [_state.Id, _cityRate.Id], "State + city")));

        _widget = await In<ItemService, Item>(s => s.CreateAsync(new ItemDetails("Widget", ItemType.NonInventory, _sales.Id, 100m)));
        _labor = await In<ItemService, Item>(s => s.CreateAsync(new ItemDetails("Labor", ItemType.Service, _sales.Id, 50m, Taxable: false)));
        _acme = await In<CustomerService, Customer>(s => s.CreateAsync(new CustomerDetails("Acme", TaxCodeId: _springfield.Id)));
        _reseller = await In<CustomerService, Customer>(s => s.CreateAsync(new CustomerDetails("Reseller", TaxCodeId: _springfield.Id,
            ExemptionCertificate: "R-123", ExemptionReason: ExemptionReason.Resale, ExemptionExpiresOn: new DateOnly(2026, 3, 31))));
    }

    public Task DisposeAsync() => _db.DisposeAsync();

    private Task<T> In<TService, T>(Func<TService, Task<T>> work) where TService : notnull =>
        _db.InScope(sp => work(sp.GetRequiredService<TService>()));

    private Task<T> Tax<T>(Func<SalesTaxService, Task<T>> work) => In(work);

    private static async Task<string> Refused(Func<Task> action) => (await Assert.ThrowsAsync<DomainException>(action)).Code;

    private SalesLineDraft Widgets(decimal qty, bool? taxable = null) => new(_widget.Id, null, qty, 100m, Taxable: taxable);

    private SalesLineDraft Labor(decimal hours) => new(_labor.Id, null, hours, 50m);

    private Task<Invoice> Invoice(Customer customer, DateOnly date, Guid? taxCode, params SalesLineDraft[] lines) =>
        In<InvoiceService, Invoice>(s => s.CreateAsync(new CreateInvoiceCommand(customer.Id, date, lines, TaxCodeId: taxCode)));

    private async Task<decimal> Balance(Guid accountId, ReportBasis basis = ReportBasis.Accrual)
    {
        var tb = await _db.InScope(sp => sp.GetRequiredService<TrialBalanceQuery>().RunAsync(new DateOnly(2026, 12, 31), basis));
        Assert.True(tb.IsBalanced);
        var line = tb.Lines.SingleOrDefault(l => l.AccountId == accountId);
        return line is null ? 0m : line.Debit.Amount - line.Credit.Amount;
    }

    [Fact]
    public async Task Agencies_get_their_own_liability_account_under_sales_tax_payable()
    {
        var accounts = await _db.InDb(db => db.Accounts.AsNoTracking().ToListAsync());
        var parent = accounts.Single(a => a.Name == SalesTaxService.LiabilityParentName);
        var state = accounts.Single(a => a.Id == _stateDor.LiabilityAccountId);

        Assert.Equal(AccountType.OtherCurrentLiability, state.Type);
        Assert.Equal(parent.Id, state.ParentId);
        Assert.Equal("State Department of Revenue", state.Name);
        Assert.Equal("tax.agency_name_taken", await Refused(() => Tax(s => s.CreateAgencyAsync(new TaxAgencyDetails("city of springfield")))));

        // Creating a rate made a one-rate code of the same name; the city rate asked not to.
        var codes = await Tax(s => s.ListCodesAsync());
        Assert.Equal(["Springfield", "State"], codes.Select(c => c.Name));
    }

    [Fact]
    public async Task Invoices_charge_the_customers_tax_code_on_taxable_lines()
    {
        var invoice = await Invoice(_acme, Mar1, null, Widgets(2), Labor(1));

        Assert.Equal(_springfield.Id, invoice.TaxCodeId);
        Assert.Equal(250m, invoice.Subtotal);
        Assert.Equal(16.50m, invoice.TaxTotal);
        Assert.Equal(266.50m, invoice.Total);
        Assert.Equal([12.50m, 4.00m], invoice.Lines.Where(l => l.Kind == SalesLineKind.Tax).Select(l => l.Amount));
        Assert.Equal(-12.50m, await Balance(_stateDor.LiabilityAccountId));
        Assert.Equal(-4.00m, await Balance(_city.LiabilityAccountId));
        Assert.Equal(-250m, await Balance(_sales.Id));
    }

    [Fact]
    public async Task A_document_can_use_another_code_or_none()
    {
        var stateOnly = (await Tax(s => s.ListCodesAsync())).Single(c => c.Name == "State");
        var invoice = await Invoice(_acme, Mar1, stateOnly.Id, Widgets(1), Widgets(1, taxable: false));
        Assert.Equal(6.25m, invoice.TaxTotal);

        var untaxed = await Invoice(_acme, Mar1, Guid.Empty, Widgets(1));
        Assert.Null(untaxed.TaxCodeId);
        Assert.Equal(100m, untaxed.Total);
        Assert.DoesNotContain(untaxed.Lines, l => l.Kind == SalesLineKind.Tax);
    }

    [Fact]
    public async Task Exempt_customers_are_charged_nothing_until_the_certificate_expires()
    {
        var exempt = await Invoice(_reseller, Mar1, null, Widgets(1));
        Assert.Equal("Resale, certificate R-123", exempt.TaxExemption);
        Assert.Equal(100m, exempt.Total);
        Assert.Equal([0m, 0m], exempt.Lines.Where(l => l.Kind == SalesLineKind.Tax).Select(l => l.Amount));

        var expired = await Invoice(_reseller, new DateOnly(2026, 4, 1), null, Widgets(1));
        Assert.Null(expired.TaxExemption);
        Assert.Equal(108.25m, expired.Total);
    }

    [Fact]
    public async Task Credit_memos_and_receipts_carry_tax_too()
    {
        var credit = await In<CreditService, CreditMemo>(s => s.CreateCreditMemoAsync(new CreateCreditMemoCommand(_acme.Id, Mar1, [Widgets(1)])));
        Assert.Equal(108.25m, credit.Total);
        Assert.Equal(6.25m, await Balance(_stateDor.LiabilityAccountId));

        var receipt = await In<SalesReceiptService, SalesReceipt>(s => s.CreateAsync(
            new CreateSalesReceiptCommand(null, Mar1, _checking.Id, [Widgets(1)], TaxCodeId: _springfield.Id)));
        Assert.Equal(108.25m, receipt.Total);
        Assert.Equal(0m, await Balance(_stateDor.LiabilityAccountId));
        Assert.Equal(0m, await Balance(_city.LiabilityAccountId));
    }

    [Fact]
    public async Task Estimates_keep_their_tax_code_when_invoiced()
    {
        var estimate = await In<EstimateService, Estimate>(s => s.CreateAsync(new SaveEstimateCommand(_acme.Id, Mar1, [Widgets(1, taxable: false), Widgets(1)])));
        Assert.Equal(8.25m, estimate.TaxTotal);

        var invoice = await In<EstimateService, Invoice>(s => s.ConvertToInvoiceAsync(estimate.Id, Mar1));
        Assert.Equal(_springfield.Id, invoice.TaxCodeId);
        Assert.Equal([false, true], invoice.Lines.Where(l => l.Kind == SalesLineKind.Item).Select(l => l.Taxable));
        Assert.Equal(208.25m, invoice.Total);

        var untaxed = await In<EstimateService, Estimate>(s => s.CreateAsync(new SaveEstimateCommand(_acme.Id, Mar1, [Widgets(1)], TaxCodeId: Guid.Empty)));
        var untaxedInvoice = await In<EstimateService, Invoice>(s => s.ConvertToInvoiceAsync(untaxed.Id, Mar1));
        Assert.Null(untaxedInvoice.TaxCodeId);
    }

    [Fact]
    public async Task Inactive_codes_and_rates_are_refused()
    {
        Assert.Equal("tax.rate_in_code", await Refused(() => Tax(s => s.SetRateActiveAsync(_cityRate.Id, false))));
        Assert.Equal("tax.agency_has_rates", await Refused(() => Tax(s => s.SetAgencyActiveAsync(_city.Id, false))));

        await Tax(s => s.SetCodeActiveAsync(_springfield.Id, false));
        Assert.Equal("tax.code_inactive", await Refused(() => Invoice(_acme, Mar1, null, Widgets(1))));
        Assert.Equal("tax.code_inactive", await Refused(() => In<CustomerService, Customer>(s =>
            s.CreateAsync(new CustomerDetails("New", TaxCodeId: _springfield.Id)))));
        Assert.DoesNotContain(await Tax(s => s.ListChoicesAsync()), c => c.Code.Id == _springfield.Id);
    }

    [Fact]
    public async Task The_liability_report_breaks_sales_down_by_rate_and_agency()
    {
        await Invoice(_acme, Mar1, null, Widgets(2), Labor(1));                          // 250 sales, 200 taxable, 16.50 tax
        await Invoice(_reseller, Mar1, null, Widgets(1));                                 // 100 exempt
        await Invoice(_acme, Mar1, Guid.Empty, Widgets(1));                               // untaxed
        await In<CreditService, CreditMemo>(s => s.CreateCreditMemoAsync(new CreateCreditMemoCommand(_acme.Id, Mar1, [Widgets(1)])));  // -100, -8.25
        await Invoice(_acme, new DateOnly(2026, 4, 1), null, Widgets(1));                 // next month: left out

        var report = await In<SalesTaxReports, TaxLiabilityReport>(s => s.LiabilityAsync(Mar1, new DateOnly(2026, 3, 31)));

        Assert.Equal(100m, report.UntaxedSales);
        var state = report.Agencies.Single(a => a.Agency.Id == _stateDor.Id);
        var row = Assert.Single(state.Rates);
        Assert.Equal((250m, 50m, 100m, 100m, 6.25m), (row.TotalSales, row.NonTaxable, row.Exempt, row.Taxable, row.Tax));
        Assert.Equal(6.25m, state.OwedAtEnd);
        Assert.Equal(0m, state.OwedAtStart);
        Assert.Equal(2.00m, report.Agencies.Single(a => a.Agency.Id == _city.Id).Tax);
    }

    [Fact]
    public async Task Paying_tax_clears_the_agency_balance_and_can_be_voided()
    {
        await Invoice(_acme, Mar1, null, Widgets(2), Labor(1));

        Assert.Equal("taxpayment.adjustment_account_required", await Refused(() => In<SalesTaxPaymentService, SalesTaxPayment>(s =>
            s.PayAsync(new PaySalesTaxCommand(_stateDor.Id, Mar1, _checking.Id, 12m, 0.5m)))));
        Assert.Equal("sales.payment_account_type", await Refused(() => In<SalesTaxPaymentService, SalesTaxPayment>(s =>
            s.PayAsync(new PaySalesTaxCommand(_stateDor.Id, Mar1, _sales.Id, 12.50m)))));

        // 12.50 owed; a 0.50 penalty paid on top.
        var payment = await In<SalesTaxPaymentService, SalesTaxPayment>(s => s.PayAsync(
            new PaySalesTaxCommand(_stateDor.Id, new DateOnly(2026, 4, 20), _checking.Id, 13m, -0.5m, _penalties.Id, new DateOnly(2026, 3, 31))));

        Assert.Equal(0m, await Balance(_stateDor.LiabilityAccountId));
        Assert.Equal(0.5m, await Balance(_penalties.Id));
        var owed = await In<SalesTaxPaymentService, IReadOnlyDictionary<Guid, decimal>>(s => s.OwedAsync(new DateOnly(2026, 4, 30)));
        Assert.Equal(0m, owed[_stateDor.Id]);
        Assert.Equal(4m, owed[_city.Id]);

        var april = await In<SalesTaxReports, TaxLiabilityReport>(s => s.LiabilityAsync(new DateOnly(2026, 4, 1), new DateOnly(2026, 4, 30)));
        var state = april.Agencies.Single(a => a.Agency.Id == _stateDor.Id);
        Assert.Equal((12.50m, 12.50m, 0m), (state.OwedAtStart, state.Paid, state.OwedAtEnd));

        await In<SalesTaxPaymentService, SalesTaxPayment>(s => s.VoidAsync(payment.Id, "Entered twice"));
        Assert.Equal(-12.50m, await Balance(_stateDor.LiabilityAccountId));
    }

    [Fact]
    public async Task Cash_basis_recognizes_tax_as_invoices_are_paid()
    {
        var invoice = await Invoice(_acme, Mar1, null, Widgets(2), Labor(1));
        await In<PaymentService, CustomerPayment>(s => s.ReceiveAsync(new ReceivePaymentCommand(
            _acme.Id, Mar1, _checking.Id, [new ApplyToInvoice(invoice.Id, 133.25m)])));

        Assert.Equal(-6.25m, await Balance(_stateDor.LiabilityAccountId, ReportBasis.Cash));
        Assert.Equal(-2.00m, await Balance(_city.LiabilityAccountId, ReportBasis.Cash));
        Assert.Equal(-12.50m, await Balance(_stateDor.LiabilityAccountId));
    }
}
