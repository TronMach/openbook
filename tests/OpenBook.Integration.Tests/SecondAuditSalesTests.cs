using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OpenBook.Application;
using OpenBook.Application.Companies;
using OpenBook.Application.Ledger;
using OpenBook.Application.Nonprofit;
using OpenBook.Application.Sales;
using OpenBook.Application.SalesTax;
using OpenBook.Domain.Accounts;
using OpenBook.Domain.Common;
using OpenBook.Domain.Companies;
using OpenBook.Domain.Ledger;
using OpenBook.Domain.Nonprofit;
using OpenBook.Domain.Sales;
using OpenBook.Domain.SalesTax;

namespace OpenBook.Integration.Tests;

/// <summary>What the second audit found in sales, sales tax and online payments (SALES-1 to SALES-15).</summary>
public sealed class SecondAuditSalesTests : IAsyncLifetime
{
    private const string PublicUrl = "https://books.studio.test";
    private static readonly DateOnly Jan5 = new(2026, 1, 5);
    private static readonly DateOnly Jan10 = new(2026, 1, 10);
    private static readonly DateOnly Jan31 = new(2026, 1, 31);
    private static readonly DateOnly Feb1 = new(2026, 2, 1);
    private static readonly DateOnly Feb28 = new(2026, 2, 28);

    private readonly LedgerDatabase _db = new();
    private Account _checking = null!;
    private Account _undeposited = null!;
    private Account _income = null!;
    private Item _widget = null!;
    private Item _labor = null!;
    private Customer _acme = null!;

    public async Task InitializeAsync()
    {
        await _db.InitializeAsync();
        _checking = await _db.CreateAccount("Checking", AccountType.Bank, "1000");
        _undeposited = await _db.CreateAccount("Undeposited Funds", AccountType.OtherCurrentAsset, "1200");
        _income = await _db.CreateAccount("Sales", AccountType.Income, "4000");
        _widget = await In<ItemService, Item>(s => s.CreateAsync(new ItemDetails("Widget", ItemType.NonInventory, _income.Id, 100m)));
        _labor = await In<ItemService, Item>(s => s.CreateAsync(new ItemDetails("Labor", ItemType.Service, _income.Id, 100m, Taxable: false)));
        _acme = await In<CustomerService, Customer>(s => s.CreateAsync(new CustomerDetails("Acme Corp", Email: "ap@acme.test")));
    }

    public Task DisposeAsync() => _db.DisposeAsync();

    private Task<T> In<TService, T>(Func<TService, Task<T>> work) where TService : notnull =>
        _db.InScope(sp => work(sp.GetRequiredService<TService>()));

    private Task<T> Online<T>(Func<OnlinePaymentService, Task<T>> work) => In(work);

    private Task<T> Giving<T>(Func<OnlineGivingService, Task<T>> work) => In(work);

    private static async Task<string> Refused(Func<Task> action) => (await Assert.ThrowsAsync<DomainException>(action)).Code;

    private Task<Invoice> Invoice(DateOnly date, Guid? taxCode, params SalesLineDraft[] lines) =>
        In<InvoiceService, Invoice>(s => s.CreateAsync(new CreateInvoiceCommand(_acme.Id, date, lines, TaxCodeId: taxCode)));

    private Task<CustomerPayment> Pay(Invoice invoice, DateOnly date, decimal amount) =>
        In<PaymentService, CustomerPayment>(s => s.ReceiveAsync(new ReceivePaymentCommand(_acme.Id, date, _checking.Id, [new ApplyToInvoice(invoice.Id, amount)])));

    private async Task<decimal> Balance(Guid accountId, ReportBasis basis = ReportBasis.Accrual)
    {
        var tb = await _db.InScope(sp => sp.GetRequiredService<TrialBalanceQuery>().RunAsync(new DateOnly(2026, 12, 31), basis));
        Assert.True(tb.IsBalanced);
        var line = tb.Lines.SingleOrDefault(l => l.AccountId == accountId);
        return line is null ? 0m : line.Debit.Amount - line.Credit.Amount;
    }

    private async Task AssertBooksAgree()
    {
        var report = await In<BooksCheckService, BooksCheckReport>(s => s.RunAsync());
        Assert.True(report.Problems == 0, string.Join(Environment.NewLine,
            report.Results.Where(r => r.Outcome == CheckOutcome.Problem).SelectMany(r => r.Findings.Select(f => $"{r.Title}: {f.Text}"))));
    }

    // ---- Online giving ----

    private Task<OnlinePaymentSettings> UseStripe(string? webhookSecret = null) =>
        Online(s => s.SaveSettingsAsync(new OnlinePaymentSettingsCommand(true, _undeposited.Id, PublicUrl, FakeStripe.SecretKey, webhookSecret)));

    private Task<OnlinePaymentSettings> UseSquare(bool sandbox = true) =>
        Online(s => s.SaveSettingsAsync(new OnlinePaymentSettingsCommand(true, _undeposited.Id, PublicUrl, FakeSquare.AccessToken, FakeSquare.SignatureKey,
            Provider: PaymentProvider.Square, Sandbox: sandbox)));

    private Task<OnlinePaymentSettings> UsePayPal() =>
        Online(s => s.SaveSettingsAsync(new OnlinePaymentSettingsCommand(true, _undeposited.Id, PublicUrl, FakePayPal.Secret, null,
            Provider: PaymentProvider.PayPal, ClientId: FakePayPal.ClientId, WebhookId: FakePayPal.WebhookId, Sandbox: true)));

    private Task<GiftSettings> OpenGiving() =>
        In<GiftService, GiftSettings>(s => s.SaveSettingsAsync(new GiftSettingsCommand(null, null, null, null, OnlineGiving: true)));

    /// <summary>Starts a gift and returns its checkout's id at the provider.</summary>
    private async Task<string> Give(decimal amount)
    {
        var before = await _db.InDb(db => db.DonationCheckouts.Select(c => c.SessionId).ToListAsync());
        await Giving(s => s.StartAsync("Ana Ruiz", "ana@example.test", amount, null));
        return (await _db.InDb(db => db.DonationCheckouts.Select(c => c.SessionId).ToListAsync())).Except(before).Single();
    }

    private Task<DonationCheckout> Checkout(string sessionId) => _db.InDb(db => db.DonationCheckouts.AsNoTracking().SingleAsync(c => c.SessionId == sessionId));

    private Task<IReadOnlyList<Donation>> Gifts() => In<GiftService, IReadOnlyList<Donation>>(s => s.ListAsync());

    [Fact]
    public async Task A_gift_reported_twice_at_once_is_recorded_once()
    {
        await UseStripe();
        await OpenGiving();
        var session = await Give(50m);
        _db.Stripe.Pay(session);

        // The webhook has read the checkout when the donor's return records it.
        await using var webhook = _db.Services.CreateAsyncScope();
        var read = await webhook.ServiceProvider.GetRequiredService<INonprofitStore>().FindDonationCheckoutAsync(session, default);
        Assert.Equal(DonationCheckoutStatus.Open, read!.Status);
        Assert.True(await Giving(s => s.CompleteAsync(session, default)));

        await Assert.ThrowsAsync<ConcurrencyConflictException>(() =>
            webhook.ServiceProvider.GetRequiredService<OnlineGivingService>().HandleAsync("checkout.session.completed", session, true, "pi_1", 50m, default));
        var gift = Assert.Single(await Gifts());
        Assert.Equal((DonationCheckoutStatus.Paid, (Guid?)gift.Id), ((await Checkout(session)).Status, (await Checkout(session)).DonationId));
        // Told again, it's just recorded already; the books have one gift.
        Assert.Equal(WebhookOutcome.Ignored, await Giving(s => s.HandleAsync("checkout.session.completed", session, true, "pi_1", 50m, default)));
        Assert.Equal(50m, await Balance(_undeposited.Id));
    }

    [Fact]
    public async Task A_gift_nothing_reported_is_found_by_the_donors_return_the_job_or_a_check()
    {
        // No webhook signing secret: Stripe has nobody to tell.
        await UseStripe();
        await OpenGiving();
        var returned = await Give(10m);
        Assert.Equal($"{PublicUrl}/give/thanks?session={{CHECKOUT_SESSION_ID}}", _db.Stripe.Created.Single()["success_url"]);
        _db.Stripe.Pay(returned);
        Assert.False(await Giving(s => s.IsRecordedAsync(returned)));
        Assert.True(await Giving(s => s.CompleteAsync(returned, default)));
        Assert.True(await Giving(s => s.IsRecordedAsync(returned)));

        var polled = await Give(20m);
        var checkedByHand = await Give(30m);
        var givenUp = await Give(40m);
        _db.Stripe.Pay(polled);
        Assert.Equal(3, (await Giving(s => s.UnrecordedAsync())).Count);
        _db.Clock.UtcNow += TimeSpan.FromMinutes(3);
        Assert.Null(await Online(s => s.ExpireOutdatedAsync()));
        Assert.Single(await Gifts());
        _db.Clock.UtcNow += TimeSpan.FromMinutes(OnlinePaymentService.PayPalCheckMinutes - 3);
        Assert.Null(await Online(s => s.ExpireOutdatedAsync()));
        Assert.Equal(DonationCheckoutStatus.Paid, (await Checkout(polled)).Status);

        var id = (await Checkout(checkedByHand)).Id;
        Assert.Equal(DonationCheckoutStatus.Open, await Giving(s => s.CheckAsync(id)));
        _db.Stripe.Pay(checkedByHand);
        Assert.Equal(DonationCheckoutStatus.Paid, await Giving(s => s.CheckAsync(id)));
        Assert.Equal([10m, 20m, 30m], (await Gifts()).Select(g => g.Amount).Order());

        // Given up: closed at the end of its day, so it can't be paid unheard.
        _db.Clock.UtcNow += OnlineGivingService.Lifetime - TimeSpan.FromMinutes(OnlinePaymentService.PayPalCheckMinutes);
        Assert.Null(await Online(s => s.ExpireOutdatedAsync()));
        Assert.Equal(DonationCheckoutStatus.Expired, (await Checkout(givenUp)).Status);
        Assert.True(_db.Stripe.IsExpired(givenUp));
        Assert.Empty(await Giving(s => s.UnrecordedAsync()));
    }

    [Fact]
    public async Task A_gift_isnt_refused_over_a_name_and_says_when_theres_nowhere_to_put_it()
    {
        await UseStripe();
        await OpenGiving();
        foreach (var name in new[] { "Ana Ruiz", "Ana Ruiz (ana@example.test)" })
        {
            var gone = await In<CustomerService, Customer>(s => s.CreateAsync(new CustomerDetails(name, Email: "ana@example.test")));
            await In<CustomerService, Customer>(s => s.SetActiveAsync(gone.Id, false));
        }

        var session = await Give(50m);
        _db.Stripe.Pay(session);
        await Online(s => s.SaveSettingsAsync(new OnlinePaymentSettingsCommand(false, null, PublicUrl)));
        Assert.Equal("online.not_set_up", await Refused(() => Giving(s => s.CompleteAsync(session, default))));
        Assert.Equal(DonationCheckoutStatus.Open, (await Checkout(session)).Status);

        await UseStripe();
        Assert.True(await Giving(s => s.CompleteAsync(session, default)));
        var donor = await In<CustomerService, Customer?>(async s => await s.GetAsync((await Gifts()).Single().DonorId));
        Assert.Equal("Ana Ruiz (ana@example.test) 2", donor!.DisplayName);
    }

    [Fact]
    public async Task Changing_the_provider_closes_gifts_under_way_and_records_those_paid()
    {
        await UseSquare();
        await OpenGiving();
        var paid = await Give(25m);
        var open = await Give(75m);
        _db.Square.Pay(paid);

        _db.Square.Down = true;
        Assert.Equal("online.links_open", await Refused(() => UsePayPal()));
        _db.Square.Down = false;
        await UsePayPal();
        Assert.Equal((DonationCheckoutStatus.Paid, DonationCheckoutStatus.Expired), ((await Checkout(paid)).Status, (await Checkout(open)).Status));
        Assert.Equal("CANCELED", _db.Square[open].State);
        Assert.Equal(25m, Assert.Single(await Gifts()).Amount);
        Assert.Equal("online.other_provider", await Refused(async () => await Giving(async s => await s.CheckAsync((await Checkout(open)).Id))));
    }

    // ---- Square ----

    [Fact]
    public async Task A_Square_order_paid_in_parts_is_recorded_once_its_paid_for_all_of_it()
    {
        await UseSquare();
        var invoice = await Invoice(Jan10, Guid.Empty, new SalesLineDraft(_labor.Id, null, 3m, 100m));
        var link = await Online(s => s.LinkForInvoiceAsync(invoice.Id));
        Task<WebhookOutcome> Hook(string payload) =>
            Online(s => s.HandleSquareWebhookAsync(payload, Square.Sign(payload, $"{PublicUrl}/hooks/square", FakeSquare.SignatureKey)));

        var giftCard = _db.Square.PayPart(link.SessionId, 10_000);
        Assert.Equal("online.not_paid_yet", await Refused(() => Hook(FakeSquare.PaymentEvent(link.SessionId, giftCard, 10_000))));
        Assert.Empty(await In<PaymentService, IReadOnlyList<CustomerPayment>>(s => s.ListAsync(_acme.Id)));

        var card = _db.Square.Pay(link.SessionId);
        Assert.Equal(WebhookOutcome.Handled, await Hook(FakeSquare.PaymentEvent(link.SessionId, card, 20_000)));
        Assert.Equal(WebhookOutcome.Ignored, await Hook(FakeSquare.PaymentEvent(link.SessionId, giftCard, 10_000)));
        Assert.Equal(300m, Assert.Single(await In<PaymentService, IReadOnlyList<CustomerPayment>>(s => s.ListAsync(_acme.Id))).Amount);
    }

    [Fact]
    public async Task Switching_the_sandbox_closes_the_links_first()
    {
        await UseSquare(sandbox: false);
        var invoice = await Invoice(Jan10, Guid.Empty, new SalesLineDraft(_labor.Id, null, 1m, 100m));
        var link = await Online(s => s.LinkForInvoiceAsync(invoice.Id));

        _db.Square.Down = true;
        Assert.Equal("online.links_open", await Refused(() => UseSquare()));
        _db.Square.Down = false;
        Assert.True((await UseSquare()).Sandbox);
        Assert.Equal("CANCELED", _db.Square[link.SessionId].State);
        Assert.Equal(PaymentLinkStatus.Expired, (await Online(s => s.LinksAsync(invoice.Id))).Single().Status);
    }

    // ---- Sales tax ----

    private async Task<(TaxAgency Agency, TaxRate Rate, TaxCode Code)> EightPercent()
    {
        var agency = await In<SalesTaxService, TaxAgency>(s => s.CreateAgencyAsync(new TaxAgencyDetails("State Revenue", FilingFrequency.Monthly, Region: "TX")));
        var rate = await In<SalesTaxService, TaxRate>(s => s.CreateRateAsync(new TaxRateDetails("State", agency.Id, 8m)));
        return (agency, rate, (await In<SalesTaxService, IReadOnlyList<TaxCode>>(s => s.ListCodesAsync())).Single());
    }

    private Task<TaxLiabilityReport> Liability(DateOnly from, DateOnly to, ReportBasis basis) =>
        In<SalesTaxReports, TaxLiabilityReport>(s => s.LiabilityAsync(from, to, basis));

    [Fact]
    public async Task The_cash_basis_tax_report_takes_back_a_returned_payment_and_agrees_with_the_ledger()
    {
        var (_, _, code) = await EightPercent();
        var invoice = await Invoice(Jan10, code.Id, new SalesLineDraft(_widget.Id, null, 1m, 100m));
        var bounced = await Pay(invoice, new DateOnly(2026, 1, 20), 108m);
        await In<PaymentService, CustomerPayment>(s => s.ReturnAsync(bounced.Id, new DateOnly(2026, 2, 5), 0m, null, "Insufficient funds"));

        Assert.Equal(8m, (await Liability(Jan10, Jan31, ReportBasis.Cash)).Agencies.Single().Tax);
        var taken = (await Liability(Feb1, Feb28, ReportBasis.Cash)).Agencies.Single();
        Assert.Equal((8m, -8m, 0m), (taken.OwedAtStart, taken.Tax, taken.OwedAtEnd));

        // Paid again: the tax is owed once, and the two periods add up to it.
        await Pay(invoice, new DateOnly(2026, 2, 20), 108m);
        var february = (await Liability(Feb1, Feb28, ReportBasis.Cash)).Agencies.Single();
        Assert.Equal((8m, 0m, 8m), (february.OwedAtStart, february.Tax, february.OwedAtEnd));
    }

    [Fact]
    public async Task The_cash_basis_tax_report_gives_a_payment_the_tax_an_untaxed_credit_left()
    {
        var (_, _, code) = await EightPercent();
        var invoice = await Invoice(Jan10, code.Id, new SalesLineDraft(_widget.Id, null, 1m, 100m));
        var credit = await In<CreditService, CreditMemo>(s => s.CreateCreditMemoAsync(new CreateCreditMemoCommand(_acme.Id, Jan10,
            [new SalesLineDraft(_labor.Id, null, 1m, 54m)], TaxCodeId: Guid.Empty)));
        await In<CreditService, bool>(async s =>
        {
            await s.ApplyAsync(_acme.Id, Jan10, [new ApplyCredit(CreditKind.CreditMemo, credit.Id, invoice.Id, 54m)]);
            return true;
        });
        await Pay(invoice, new DateOnly(2026, 1, 20), 54m);

        var report = (await Liability(Jan10, Jan31, ReportBasis.Cash)).Agencies.Single();
        Assert.Equal((8m, 8m), (report.Tax, report.OwedAtEnd));
    }

    [Fact]
    public async Task A_rate_change_is_in_the_choices_for_the_documents_date_and_in_the_api()
    {
        var (_, rate, code) = await EightPercent();
        await In<SalesTaxService, TaxRate>(s => s.SetRateChangeAsync(rate.Id, Jan5, 9m));
        Task<IReadOnlyList<TaxCodeChoice>> Choices(DateOnly on) => In<SalesTaxService, IReadOnlyList<TaxCodeChoice>>(s => s.ListChoicesForDocumentAsync(null, [], on));
        Assert.Equal((8m, 9m), ((await Choices(Jan5.AddDays(-1))).Single().CombinedPercent, (await Choices(Jan5)).Single().CombinedPercent));
        var other = await In<SalesTaxService, TaxCode>(s => s.CreateCodeAsync(new TaxCodeDetails("Also state", [rate.Id])));
        var invoice = await Invoice(Jan5.AddDays(-1), code.Id, new SalesLineDraft(_widget.Id, null, 1m, 100m));
        var changing = await In<SalesTaxService, IReadOnlyList<TaxCodeChoice>>(s => s.ListChoicesForDocumentAsync(invoice.TaxCodeId, invoice.Lines, invoice.Date));
        Assert.Equal(8m, changing.Single(c => c.Code.Id == other.Id).CombinedPercent);
        Assert.Equal(108m, invoice.Total);
    }

    [Fact]
    public async Task An_update_that_names_no_state_keeps_the_agencys()
    {
        var (agency, _, _) = await EightPercent();
        Assert.Equal("TX", (await In<SalesTaxService, TaxAgency>(s => s.UpdateAgencyAsync(agency.Id, new TaxAgencyDetails("State Revenue", FilingFrequency.Monthly)))).Region);
        Assert.Null((await In<SalesTaxService, TaxAgency>(s => s.UpdateAgencyAsync(agency.Id, new TaxAgencyDetails("State Revenue", FilingFrequency.Monthly, Region: "")))).Region);
    }

    // ---- Gift cards and retainage ----

    private async Task<(Account Account, Item Card)> GiftCards()
    {
        var account = await _db.CreateAccount("Gift Cards Outstanding", AccountType.OtherCurrentLiability);
        return (account, await In<ItemService, Item>(s => s.CreateAsync(new ItemDetails("Gift card", ItemType.Service, account.Id, 100m, Taxable: false))));
    }

    [Fact]
    public async Task A_gift_card_can_pay_for_all_of_a_sale()
    {
        var (outstanding, card) = await GiftCards();
        await In<SalesReceiptService, SalesReceipt>(s => s.CreateAsync(new CreateSalesReceiptCommand(_acme.Id, Jan5, _undeposited.Id,
            [new SalesLineDraft(card.Id, "Gift card 0001", 2m, 100m)])));

        var receipt = await In<SalesReceiptService, SalesReceipt>(s => s.CreateAsync(new CreateSalesReceiptCommand(_acme.Id, Jan10, _undeposited.Id,
            [new SalesLineDraft(_labor.Id, null, 1m, 100m), new SalesLineDraft(card.Id, "0001 redeemed", -1m, 100m)])));
        var invoice = await Invoice(Jan10, Guid.Empty, new SalesLineDraft(_labor.Id, null, 1m, 100m), new SalesLineDraft(card.Id, "0001 redeemed", -1m, 100m));
        Assert.Equal((0m, 0m, false), (receipt.Total, invoice.Balance, invoice.IsOpen));
        Assert.Equal(0m, await Balance(outstanding.Id));
        // On the cash basis too: the card was the payment, so nothing else will bring the invoice's income in.
        Assert.Equal((-200m, -200m), (await Balance(_income.Id), await Balance(_income.Id, ReportBasis.Cash)));
        Assert.Equal(0m, await Balance(outstanding.Id, ReportBasis.Cash));
        // Nothing to deposit for it, and the books agree.
        Assert.Equal(200m, Assert.Single(await In<DepositService, IReadOnlyList<UndepositedItem>>(s => s.UndepositedAsync())).Amount);
        await AssertBooksAgree();

        // It can be corrected and voided like any other.
        var corrected = await In<InvoiceService, Invoice>(s => s.ChangeAsync(invoice.Id, new ChangeInvoiceCommand(Jan10,
            [new SalesLineDraft(_labor.Id, "Two hours", 2m, 50m), new SalesLineDraft(card.Id, "0001 redeemed", -1m, 100m)], "Wording")));
        Assert.Equal(0m, corrected.Total);
        await In<InvoiceService, Invoice>(s => s.VoidAsync(invoice.Id, "Not sold"));
        Assert.Equal((-100m, -100m), (await Balance(outstanding.Id), await Balance(outstanding.Id, ReportBasis.Cash)));
        await AssertBooksAgree();

        // Only a card makes a sale of nothing, and it can't be worth more than the sale.
        Assert.Equal("sales.total_not_positive", await Refused(() => Invoice(Jan10, Guid.Empty, new SalesLineDraft(_labor.Id, null, 1m, 100m), new SalesLineDraft(_labor.Id, null, -1m, 100m))));
        Assert.Equal("sales.total_not_positive", await Refused(() => Invoice(Jan10, Guid.Empty, new SalesLineDraft(_labor.Id, null, 1m, 50m), new SalesLineDraft(card.Id, null, -1m, 100m))));
    }

    [Fact]
    public async Task A_discount_below_a_redeemed_card_takes_no_more_than_itself_off_the_taxable_amount()
    {
        var (_, _, code) = await EightPercent();
        var (_, card) = await GiftCards();
        var invoice = await Invoice(Jan10, code.Id, new SalesLineDraft(_widget.Id, null, 1m, 100m), new SalesLineDraft(card.Id, "0001 redeemed", -1m, 90m),
            new SalesLineDraft(null, null, 1m, 10m, SalesLineKind.DiscountPercent));
        // 10% of the 10.00 left is 1.00: 99.00 is taxed.
        Assert.Equal(7.92m, invoice.TaxTotal);
        // As before where every line is a sale: half of a discount comes off the taxable half.
        Assert.Equal(90m, TaxCalculator.TaxableAmount([(SalesLineKind.Item, 100m, true), (SalesLineKind.Item, 100m, false), (SalesLineKind.DiscountAmount, -20m, false)]));
    }

    [Fact]
    public async Task Retainage_and_gift_cards_arent_sales_on_returns_or_by_state()
    {
        var (_, _, code) = await EightPercent();
        var (_, card) = await GiftCards();
        await In<CustomerService, Customer>(s => s.UpdateAsync(_acme.Id, new CustomerDetails("Acme Corp", BillingAddress: "9 Elm St\nAustin, TX 78701")));
        var estimate = await In<EstimateService, Estimate>(s => s.CreateAsync(new SaveEstimateCommand(_acme.Id, Jan5, [new SalesLineDraft(_widget.Id, null, 10m, 100m)], TaxCodeId: code.Id)));
        await In<EstimateService, Invoice>(s => s.ProgressInvoiceAsync(estimate.Id, new InvoiceFromSourceCommand(Jan10, RetainagePercent: 10m)));
        await Invoice(Jan10, Guid.Empty, new SalesLineDraft(card.Id, "Gift card 0001", 1m, 60m));
        await Invoice(Jan10, code.Id, new SalesLineDraft(_widget.Id, null, 1m, 100m), new SalesLineDraft(card.Id, "0001 redeemed", -1m, 60m));

        var report = await Liability(Jan5, Jan31, ReportBasis.Accrual);
        var row = Assert.Single(report.Agencies.Single().Rates);
        Assert.Equal((1_100m, 0m, 1_100m, 88m, 0m), (row.TotalSales, row.NonTaxable, row.Taxable, row.Tax, report.UntaxedSales));
        var states = await In<NexusService, SalesByState>(s => s.SalesByStateAsync(Jan5, Jan31));
        Assert.Equal(1_100m, states.States.Single(s => s.Region == "TX").Sales);
    }

    [Fact]
    public async Task Voiding_billed_retainage_leaves_the_estimate_closed_and_it_can_be_billed_again()
    {
        var estimate = await In<EstimateService, Estimate>(s => s.CreateAsync(new SaveEstimateCommand(_acme.Id, Jan5, [new SalesLineDraft(_labor.Id, null, 10m, 100m)])));
        var progress = await In<EstimateService, Invoice>(s => s.ProgressInvoiceAsync(estimate.Id, new InvoiceFromSourceCommand(Jan10, RetainagePercent: 10m)));
        var release = await In<EstimateService, Invoice>(s => s.BillRetainageAsync(estimate.Id, Jan31));
        await In<InvoiceService, Invoice>(s => s.VoidAsync(release.Id, "Wrong date"));
        Assert.Equal(EstimateStatus.Closed, (await In<EstimateService, Estimate?>(s => s.GetAsync(estimate.Id)))!.Status);
        Assert.Equal(100m, (await In<EstimateService, Invoice>(s => s.BillRetainageAsync(estimate.Id, Feb1))).Total);

        // Voiding the work itself still opens it.
        await In<InvoiceService, Invoice>(s => s.VoidAsync(progress.Id, "Wrong job"));
        Assert.Equal(EstimateStatus.Accepted, (await In<EstimateService, Estimate?>(s => s.GetAsync(estimate.Id)))!.Status);
    }

    // ---- The rest ----

    [Fact]
    public async Task An_invoices_income_stays_where_a_closed_period_counted_a_payment_that_came_back_since()
    {
        var other = await _db.CreateAccount("Other sales", AccountType.Income, "4100");
        var moved = await In<ItemService, Item>(s => s.CreateAsync(new ItemDetails("Moved", ItemType.Service, other.Id, 100m, Taxable: false)));
        var prepaid = await In<PaymentService, CustomerPayment>(s => s.ReceiveAsync(new ReceivePaymentCommand(_acme.Id, new DateOnly(2025, 12, 28), _checking.Id, [], Amount: 100m)));
        var invoice = await Invoice(Jan5, Guid.Empty, new SalesLineDraft(_labor.Id, null, 1m, 100m));
        await In<CreditService, bool>(async s =>
        {
            await s.ApplyAsync(_acme.Id, Jan5, [new ApplyCredit(CreditKind.Payment, prepaid.Id, invoice.Id, 100m)]);
            return true;
        });
        await In<PaymentService, CustomerPayment>(s => s.ReturnAsync(prepaid.Id, Jan10, 0m, null, "Insufficient funds"));
        await In<CloseBooksService, ClosingStatus>(s => s.SetAsync(new DateOnly(2025, 12, 31), null, false));

        Assert.Equal("period.closed", await Refused(() => In<InvoiceService, Invoice>(s =>
            s.ChangeAsync(invoice.Id, new ChangeInvoiceCommand(Jan5, [new SalesLineDraft(moved.Id, null, 1m, 100m)], "Other work")))));
        // Wording alone changes no income.
        await In<InvoiceService, Invoice>(s => s.ChangeAsync(invoice.Id, new ChangeInvoiceCommand(Jan5, [new SalesLineDraft(_labor.Id, "An hour", 1m, 100m)], "Wording")));
    }

    [Fact]
    public async Task Terms_with_endless_digits_are_an_error_on_their_row()
    {
        var preview = await In<CustomerImportService, ImportResult<CustomerImportRow>>(s => s.PreviewAsync("Name,Terms\nShop,2% 99999999999 Net 30\nOther,2% 10 Net 30\n"));
        Assert.Equal((1, 1), (preview.ErrorCount, preview.NewCount));
    }
}

/// <summary>The second audit's sales findings in the API (SALES-9): what a program leaves out of a change stays.</summary>
public sealed class SecondAuditSalesApiTests : IAsyncLifetime
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), $"openbook-audit2-{Guid.NewGuid():N}");
    private WebTestDatabase _database = null!;
    private WebApplicationFactory<Program> _app = null!;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_folder);
        _database = new WebTestDatabase(_folder, "audit2.db");
        await _database.CreateAsync();
        _app = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            _database.Apply(b);
            b.UseSetting("OpenBook:DataProtectionKeys", Path.Combine(_folder, "keys"));
            b.UseEnvironment("Development");
        });
        _ = _app.Server;

        await using var scope = _app.Services.CreateAsyncScope();
        var owner = await scope.ServiceProvider.GetRequiredService<CompanySetupService>().SetUpAsync(
            new SetUpCompanyCommand("Api Co", BusinessType.SoleProprietorship, "USD", 1, "owner", "correct horse battery"));
        var token = (await scope.ServiceProvider.GetRequiredService<ApiKeyService>().CreateAsync(owner.Id, "test")).Token;
        _client = _app.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _app.DisposeAsync();
        SqliteConnection.ClearAllPools();
        await _database.DropAsync();
        TestFolder.Delete(_folder);
    }

    private static async Task<JsonElement> Read(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, body);
        using var json = JsonDocument.Parse(body);
        return json.RootElement.Clone();
    }

    [Fact]
    public async Task A_customer_sent_back_without_newer_fields_keeps_them_and_null_clears()
    {
        var made = await Read(await _client.PostAsJsonAsync("/api/customers", new
        {
            displayName = "Shop",
            exemptionCertificate = "TX-1",
            exemptionReason = "Resale",
            exemptionRegions = "TX",
            creditLimit = 500m,
            creditHold = true,
        }));
        var path = $"/api/customers/{made.GetProperty("id").GetGuid()}";
        Assert.Equal((500m, true), (made.GetProperty("creditLimit").GetDecimal(), made.GetProperty("creditHold").GetBoolean()));

        var kept = await Read(await _client.PutAsJsonAsync(path, new { displayName = "Shop", phone = "555-0100", exemptionCertificate = "TX-1", exemptionReason = "Resale" }));
        Assert.Equal(("555-0100", "TX", 500m, true), (kept.GetProperty("phone").GetString(), kept.GetProperty("exemptionRegions").GetString(),
            kept.GetProperty("creditLimit").GetDecimal(), kept.GetProperty("creditHold").GetBoolean()));

        var cleared = await Read(await _client.PutAsJsonAsync(path, new { displayName = "Shop", creditLimit = (decimal?)null, creditHold = false }));
        Assert.Equal((JsonValueKind.Null, false, JsonValueKind.Null), (cleared.GetProperty("creditLimit").ValueKind, cleared.GetProperty("creditHold").GetBoolean(),
            cleared.GetProperty("exemptionRegions").ValueKind));
        using var notJson = new StringContent("{", System.Text.Encoding.UTF8, "application/json");
        Assert.Equal(HttpStatusCode.BadRequest, (await _client.PutAsync(path, notJson)).StatusCode);
    }

    [Fact]
    public async Task A_deposit_sent_back_without_its_extras_keeps_them()
    {
        async Task<Guid> Account(string name, string type) => (await Read(await _client.PostAsJsonAsync("/api/accounts", new { name, type }))).GetProperty("id").GetGuid();
        var bank = await Account("Bank", "Bank");
        var holding = await Account("Undeposited", "OtherCurrentAsset");
        var income = await Account("Fees earned", "Income");
        var fees = await Account("Card fees", "Expense");
        var item = (await Read(await _client.PostAsJsonAsync("/api/items", new { name = "Work", type = "Service", incomeAccountId = income, rate = 100m }))).GetProperty("id").GetGuid();
        var receipt = (await Read(await _client.PostAsJsonAsync("/api/sales/receipts", new
        {
            date = "2026-01-10",
            depositAccountId = holding,
            lines = new[] { new { itemId = item, quantity = 1m, rate = 100m } },
        }))).GetProperty("id").GetGuid();
        var items = new[] { new { kind = "SalesReceipt", id = receipt } };
        var deposit = await Read(await _client.PostAsJsonAsync("/api/sales/deposits", new
        {
            bankAccountId = bank,
            date = "2026-01-12",
            items,
            extras = new[] { new { accountId = fees, amount = -3.20m } },
        }));
        Assert.Equal(96.80m, deposit.GetProperty("total").GetDecimal());

        var path = $"/api/sales/deposits/{deposit.GetProperty("id").GetGuid()}";
        var kept = await Read(await _client.PutAsJsonAsync(path, new { bankAccountId = bank, date = "2026-01-12", items, reason = "Memo", memo = "Cards" }));
        Assert.Equal(96.80m, kept.GetProperty("total").GetDecimal());
        var cleared = await Read(await _client.PutAsJsonAsync(path, new { bankAccountId = bank, date = "2026-01-12", items, reason = "No fee", extras = Array.Empty<object>() }));
        Assert.Equal(100m, cleared.GetProperty("total").GetDecimal());
    }

    [Fact]
    public async Task Agencies_and_rates_say_what_they_hold()
    {
        var agency = (await Read(await _client.PostAsJsonAsync("/api/sales-tax/agencies", new { name = "State", region = "TX", dueDays = 25 }))).GetProperty("id").GetGuid();
        var read = await Read(await _client.GetAsync($"/api/sales-tax/agencies/{agency}"));
        Assert.Equal(("TX", 25), (read.GetProperty("region").GetString(), read.GetProperty("dueDays").GetInt32()));
        var rate = (await Read(await _client.PostAsJsonAsync("/api/sales-tax/rates", new { name = "State", agencyId = agency, percent = 8m }))).GetProperty("id").GetGuid();
        await Read(await _client.PutAsJsonAsync($"/api/sales-tax/rates/{rate}/changes/2020-01-01", new { percent = 9m }));
        var changed = await Read(await _client.GetAsync($"/api/sales-tax/rates/{rate}"));
        Assert.Equal((8m, 9m, 1), (changed.GetProperty("percent").GetDecimal(), changed.GetProperty("percentToday").GetDecimal(), changed.GetProperty("changes").GetArrayLength()));
    }
}
