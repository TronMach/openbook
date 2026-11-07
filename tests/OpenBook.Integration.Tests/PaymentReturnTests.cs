using Microsoft.Extensions.DependencyInjection;
using OpenBook.Application;
using OpenBook.Application.Ledger;
using OpenBook.Application.Reporting;
using OpenBook.Application.Sales;
using OpenBook.Domain.Accounts;
using OpenBook.Domain.Common;
using OpenBook.Domain.Ledger;
using OpenBook.Domain.Sales;

namespace OpenBook.Integration.Tests;

/// <summary>A payment the bank took back (a bounced check): the money leaves the bank on that day and the invoices are owed again.</summary>
public sealed class PaymentReturnTests : IAsyncLifetime
{
    private static readonly DateOnly Mar1 = new(2026, 3, 1);
    private static readonly DateOnly Mar5 = new(2026, 3, 5);
    private static readonly DateOnly Mar10 = new(2026, 3, 10);
    private static readonly DateOnly Mar31 = new(2026, 3, 31);

    private readonly LedgerDatabase _db = new();
    private Account _checking = null!;
    private Account _undeposited = null!;
    private Account _income = null!;
    private Item _consulting = null!;
    private Customer _acme = null!;

    public async Task InitializeAsync()
    {
        await _db.InitializeAsync();
        _checking = await _db.CreateAccount("Checking", AccountType.Bank, "1000");
        _undeposited = await _db.CreateAccount("Undeposited Funds", AccountType.OtherCurrentAsset, "1200");
        _income = await _db.CreateAccount("Service Revenue", AccountType.Income, "4000");
        _consulting = await In<ItemService, Item>(s => s.CreateAsync(new ItemDetails("Consulting", ItemType.Service, _income.Id, 100m)));
        _acme = await In<CustomerService, Customer>(s => s.CreateAsync(new CustomerDetails("Acme Corp")));
    }

    public Task DisposeAsync() => _db.DisposeAsync();

    private Task<T> In<TService, T>(Func<TService, Task<T>> work) where TService : notnull =>
        _db.InScope(sp => work(sp.GetRequiredService<TService>()));

    private static async Task<string> Refused(Func<Task> action) => (await Assert.ThrowsAsync<DomainException>(action)).Code;

    private Task<Invoice> Invoice(DateOnly date, decimal hours = 1m) =>
        In<InvoiceService, Invoice>(s => s.CreateAsync(new CreateInvoiceCommand(_acme.Id, date, [new SalesLineDraft(_consulting.Id, null, hours, 100m)])));

    private Task<CustomerPayment> Pay(Invoice invoice, DateOnly date, Guid into, decimal? amount = null) =>
        In<PaymentService, CustomerPayment>(s => s.ReceiveAsync(new ReceivePaymentCommand(_acme.Id, date, into, [new ApplyToInvoice(invoice.Id, invoice.Total)], Amount: amount)));

    private async Task<decimal> Balance(ReportBasis basis, Guid accountId, DateOnly from, DateOnly to) =>
        (await In<ReportLedger, IReadOnlyList<LedgerLine>>(s => s.LinesAsync(basis, from, to))).Where(l => l.AccountId == accountId).Sum(l => l.Amount);

    private Task<decimal> Owed(DateOnly asOf) =>
        In<ReceivablesService, decimal>(async s => (await s.AgingAsync(asOf)).Totals.Total);

    private async Task AssertBooksAgree()
    {
        var report = await In<BooksCheckService, BooksCheckReport>(s => s.RunAsync());
        Assert.True(report.Problems == 0, string.Join(Environment.NewLine,
            report.Results.Where(r => r.Outcome == CheckOutcome.Problem).SelectMany(r => r.Findings.Select(f => $"{r.Title}: {f.Text}").DefaultIfEmpty(r.Summary))));
    }

    [Fact]
    public async Task A_bounced_check_takes_the_money_back_and_reopens_the_invoice_from_that_day()
    {
        var invoice = await Invoice(Mar1, 2m);
        var payment = await Pay(invoice, Mar5, _checking.Id);

        var returned = await In<PaymentService, CustomerPayment>(s => s.ReturnAsync(payment.Id, Mar10, 15m, null, "Insufficient funds"));

        Assert.Equal(Mar10, returned.ReturnedOn);
        Assert.Equal(0m, (await In<InvoiceService, Invoice?>(s => s.GetAsync(invoice.Id)))!.AmountPaid);
        Assert.Equal(-15m, await Balance(ReportBasis.Accrual, _checking.Id, Mar1, Mar31));
        Assert.Equal(15m, await Balance(ReportBasis.Accrual, (await In<SalesAccounts, Account>(s => s.GetOrCreateBankChargesAsync(default))).Id, Mar1, Mar31));

        // Paid on the days between, owed again from the day it came back.
        Assert.Equal(0m, await Owed(Mar5));
        Assert.Equal(200m, await Owed(Mar10));
        var statement = await In<ReceivablesService, CustomerStatement>(s => s.StatementAsync(_acme.Id, Mar1, Mar31));
        Assert.Equal(200m, statement.ClosingBalance);
        Assert.Contains(statement.Lines, l => l.Date == Mar10 && l.Amount == 200m);

        // Cash basis: the income came with the money and went with it.
        Assert.Equal(-200m, await Balance(ReportBasis.Cash, _income.Id, Mar1, Mar9()));
        Assert.Equal(200m, await Balance(ReportBasis.Cash, _income.Id, Mar10, Mar31));
        Assert.Equal(0m, await Balance(ReportBasis.Cash, _income.Id, Mar1, Mar31));
        Assert.Equal(-15m, await Balance(ReportBasis.Cash, _checking.Id, Mar1, Mar31));
        await AssertBooksAgree();

        Assert.Equal("payment.returned", await Refused(() => In<PaymentService, CustomerPayment>(s => s.VoidAsync(payment.Id, "No"))));
        Assert.Equal("payment.returned", await Refused(() => In<PaymentService, CustomerPayment>(s => s.ReturnAsync(payment.Id, Mar10, 0m, null, "Again"))));
    }

    private static DateOnly Mar9() => Mar10.AddDays(-1);

    [Fact]
    public async Task Waiting_payments_are_voided_instead_and_a_deposit_holding_a_returned_one_stays()
    {
        var invoice = await Invoice(Mar1);
        var payment = await Pay(invoice, Mar5, _undeposited.Id);
        Assert.Equal("payment.not_in_bank", await Refused(() => In<PaymentService, CustomerPayment>(s => s.ReturnAsync(payment.Id, Mar10, 0m, null, "Bounced"))));

        var deposit = await In<DepositService, BankDeposit>(s => s.DepositAsync(new MakeDepositCommand(_checking.Id, Mar5, [(DepositedKind.Payment, payment.Id)])));
        await In<PaymentService, CustomerPayment>(s => s.ReturnAsync(payment.Id, Mar10, 0m, null, "Bounced"));

        Assert.Equal(0m, await Balance(ReportBasis.Accrual, _checking.Id, Mar1, Mar31));
        Assert.Equal(0m, await Balance(ReportBasis.Accrual, _undeposited.Id, Mar1, Mar31));
        Assert.Equal("deposit.payment_returned", await Refused(() => In<DepositService, BankDeposit>(s => s.VoidAsync(deposit.Id, "No"))));
        await AssertBooksAgree();
    }

    [Fact]
    public async Task A_return_can_be_taken_back_until_the_invoice_is_paid_another_way()
    {
        var invoice = await Invoice(Mar1);
        var payment = await Pay(invoice, Mar5, _checking.Id);
        await In<PaymentService, CustomerPayment>(s => s.ReturnAsync(payment.Id, Mar10, 0m, null, "Bounced"));

        var undone = await In<PaymentService, CustomerPayment>(s => s.UndoReturnAsync(payment.Id, "It cleared"));
        Assert.Null(undone.ReturnedOn);
        Assert.Equal(100m, (await In<InvoiceService, Invoice?>(s => s.GetAsync(invoice.Id)))!.AmountPaid);
        Assert.Equal(100m, await Balance(ReportBasis.Accrual, _checking.Id, Mar1, Mar31));
        await AssertBooksAgree();

        await In<PaymentService, CustomerPayment>(s => s.ReturnAsync(payment.Id, Mar10, 0m, null, "Bounced after all"));
        await Pay(invoice, Mar31, _checking.Id);
        Assert.Equal("payment.return_invoice_paid", await Refused(() => In<PaymentService, CustomerPayment>(s => s.UndoReturnAsync(payment.Id, "No"))));
        Assert.Equal(0m, await Balance(ReportBasis.Cash, _income.Id, Mar1, Mar9()) + 100m);
        Assert.Equal(-100m, await Balance(ReportBasis.Cash, _income.Id, Mar1, Mar31));
        await AssertBooksAgree();
    }

    [Fact]
    public async Task Unapplied_money_that_comes_back_leaves_no_credit()
    {
        var payment = await In<PaymentService, CustomerPayment>(s => s.ReceiveAsync(new ReceivePaymentCommand(_acme.Id, Mar5, _checking.Id, [], Amount: 80m)));
        await In<PaymentService, CustomerPayment>(s => s.ReturnAsync(payment.Id, Mar10, 0m, null, "Stopped"));

        Assert.Equal(-80m, await Owed(Mar5));
        Assert.Equal(0m, await Owed(Mar10));
        Assert.Equal(0m, (await In<PaymentService, CustomerPayment?>(s => s.GetAsync(payment.Id)))!.Unapplied);
        Assert.Equal(0m, await Balance(ReportBasis.Cash, _checking.Id, Mar1, Mar31));
        await AssertBooksAgree();
    }

    [Fact]
    public async Task A_deposit_takes_a_processors_fee_so_the_net_payout_is_one_deposit()
    {
        var fees = await _db.CreateAccount("Merchant Fees", AccountType.Expense, "6100");
        var first = await Pay(await Invoice(Mar1, 6m), Mar5, _undeposited.Id);
        var second = await Pay(await Invoice(Mar1, 4m), Mar5, _undeposited.Id);

        var deposit = await In<DepositService, BankDeposit>(s => s.DepositAsync(new MakeDepositCommand(_checking.Id, Mar10,
            [(DepositedKind.Payment, first.Id), (DepositedKind.Payment, second.Id)], Extras: [new DepositExtraDraft(fees.Id, -30m, "Card fees")])));

        Assert.Equal(970m, deposit.Total);
        Assert.Equal(970m, await Balance(ReportBasis.Accrual, _checking.Id, Mar1, Mar31));
        Assert.Equal(30m, await Balance(ReportBasis.Accrual, fees.Id, Mar1, Mar31));
        Assert.Equal(0m, await Balance(ReportBasis.Accrual, _undeposited.Id, Mar1, Mar31));
        Assert.Equal(30m, await Balance(ReportBasis.Cash, fees.Id, Mar1, Mar31));
        await AssertBooksAgree();

        var changed = await In<DepositService, BankDeposit>(s => s.ChangeAsync(deposit.Id, new MakeDepositCommand(_checking.Id, Mar10,
            [(DepositedKind.Payment, first.Id), (DepositedKind.Payment, second.Id)], Extras: [new DepositExtraDraft(fees.Id, -31m, "Card fees")]), "Fee was 31"));
        Assert.Equal((969m, 1), (changed.Total, changed.Extras.Count));

        Assert.Equal("deposit.total_not_positive", await Refused(() => In<DepositService, BankDeposit>(s => s.ChangeAsync(deposit.Id, new MakeDepositCommand(_checking.Id, Mar10,
            [(DepositedKind.Payment, first.Id)], Extras: [new DepositExtraDraft(fees.Id, -600m)]), "Too much"))));
        Assert.Equal("deposit.extra_receivable", await Refused(() => In<DepositService, BankDeposit>(s => s.ChangeAsync(deposit.Id, new MakeDepositCommand(_checking.Id, Mar10,
            [(DepositedKind.Payment, first.Id)], Extras: [new DepositExtraDraft(first.ReceivableAccountId, -6m)]), "A/R"))));
    }

    [Fact]
    public async Task A_refund_by_check_prints_goes_to_positive_pay_and_counts_toward_the_next_number()
    {
        var prepaid = await In<PaymentService, CustomerPayment>(s => s.ReceiveAsync(new ReceivePaymentCommand(_acme.Id, Mar1, _checking.Id, [], Amount: 500m)));
        var refund = await In<CreditService, CustomerRefund>(s => s.RefundAsync(new RefundCreditCommand(CreditKind.Payment, prepaid.Id, Mar5, _checking.Id, 120m, Reference: "2041")));

        var listed = await In<OpenBook.Application.Banking.CheckService, IReadOnlyList<OpenBook.Application.Banking.PrintableCheck>>(s => s.ListAsync(_checking.Id));
        Assert.Contains(listed, c => c.Kind == OpenBook.Application.Banking.CheckKind.CustomerRefund && c.Id == refund.Id && c.Payee == "Acme Corp");
        var printed = await In<OpenBook.Application.Banking.CheckService, OpenBook.Application.Banking.PrintedChecks>(s => s.PrintAsync([(OpenBook.Application.Banking.CheckKind.CustomerRefund, refund.Id)]));
        Assert.Equal(1, printed.Count);
        Assert.NotNull((await In<CreditService, CustomerRefund?>(s => s.GetRefundAsync(refund.Id)))!.PrintedAt);

        Assert.Equal("2042", await In<OpenBook.Application.Banking.BankingService, string?>(s => s.SuggestCheckNumberAsync(_checking.Id)));
        var unsent = await In<OpenBook.Application.Banking.PositivePayService, IReadOnlyList<OpenBook.Application.Banking.PositivePayCheck>>(s => s.ListAsync(_checking.Id, Mar1, Mar31));
        Assert.Contains(unsent, c => c.Kind == OpenBook.Application.Banking.CheckKind.CustomerRefund && c.Number == "2041" && c.Amount == 120m);
        await In<OpenBook.Application.Banking.PositivePayService, OpenBook.Application.Banking.PositivePayFile>(s => s.ExportAsync(_checking.Id, Mar1, Mar31,
            new OpenBook.Application.Banking.PositivePayOptions("123456789", OpenBook.Domain.Banking.PositivePayDateFormat.MonthDayYear, true)));
        Assert.NotNull((await In<CreditService, CustomerRefund?>(s => s.GetRefundAsync(refund.Id)))!.PositivePaySentAt);
    }
}
