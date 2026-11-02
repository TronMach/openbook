using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OpenBook.Application;
using OpenBook.Application.Forms;
using OpenBook.Application.Ledger;
using OpenBook.Application.Pricing;
using OpenBook.Application.Sales;
using OpenBook.Domain.Accounts;
using OpenBook.Domain.Common;
using OpenBook.Domain.Companies;
using OpenBook.Domain.Ledger;
using OpenBook.Domain.Pricing;
using OpenBook.Domain.Sales;

namespace OpenBook.Integration.Tests;

/// <summary>
/// Everyday depth (2026-11-02): journal entries and deposits changed in place, batch invoicing (all or none, the
/// preview making nothing, each customer's prices) and statements emailed in a batch. Each leaves the books agreeing.
/// </summary>
public sealed class EverydayDepthTests : IAsyncLifetime
{
    private static readonly DateOnly Jan5 = new(2026, 1, 5);
    private static readonly DateOnly Jan10 = new(2026, 1, 10);

    private readonly LedgerDatabase _db = new();
    private Account _checking = null!;
    private Account _undeposited = null!;
    private Account _rent = null!;
    private Account _equity = null!;
    private Item _plan = null!;
    private Customer _acme = null!;
    private Customer _globex = null!;

    public async Task InitializeAsync()
    {
        await _db.InitializeAsync();
        _checking = await _db.CreateAccount("Checking", AccountType.Bank, "1000");
        _undeposited = await _db.CreateAccount("Undeposited Funds", AccountType.OtherCurrentAsset, "1200");
        _equity = await _db.CreateAccount("Owner's Equity", AccountType.Equity, "3000");
        _rent = await _db.CreateAccount("Rent", AccountType.Expense, "6500");
        var income = await _db.CreateAccount("Service Revenue", AccountType.Income, "4000");
        _plan = await In<ItemService, Item>(s => s.CreateAsync(new ItemDetails("Support plan", ItemType.Service, income.Id, 100m, Taxable: false)));
        _acme = await In<CustomerService, Customer>(s => s.CreateAsync(new CustomerDetails("Acme", Email: "ap@acme.test", PaymentTermsDays: 30)));
        _globex = await In<CustomerService, Customer>(s => s.CreateAsync(new CustomerDetails("Globex", PaymentTermsDays: 15)));
    }

    public Task DisposeAsync() => _db.DisposeAsync();

    private Task<T> In<TService, T>(Func<TService, Task<T>> work) where TService : notnull =>
        _db.InScope(sp => work(sp.GetRequiredService<TService>()));

    private static async Task<string> Refused(Func<Task> action) => (await Assert.ThrowsAsync<DomainException>(action)).Code;

    private async Task AssertBooksAgree()
    {
        var report = await In<BooksCheckService, BooksCheckReport>(s => s.RunAsync());
        Assert.True(report.Problems == 0, string.Join("; ", report.Results.Where(r => r.Outcome == CheckOutcome.Problem).SelectMany(r => r.Findings).Select(f => f.Text)));
    }

    // ---- Journal entries ----

    private Task<JournalEntry> PostRent(decimal amount, DateOnly? reverseOn = null) =>
        In<PostingEngine, JournalEntry>(s => s.PostAsync(new PostJournalEntryCommand(Jan5,
            [JournalLineDraft.Dr(_rent.Id, amount, "January"), JournalLineDraft.Cr(_equity.Id, amount)], "Rent paid by the owner", ReverseOn: reverseOn)));

    [Fact]
    public async Task Changing_a_journal_entry_voids_it_and_posts_the_replacement_together()
    {
        var original = await PostRent(900m);
        var draft = await In<PostingEngine, PostJournalEntryCommand?>(s => s.DraftOfAsync(original.Id));
        Assert.NotNull(draft);
        Assert.Equal(900m, draft!.Lines.Sum(l => l.Debit));

        var replacement = await In<PostingEngine, JournalEntry>(s => s.ChangeAsync(original.Id,
            draft with { Lines = [JournalLineDraft.Dr(_rent.Id, 950m), JournalLineDraft.Cr(_equity.Id, 950m)] }, "Rent went up"));

        var reloaded = await In<PostingEngine, JournalEntry?>(s => s.GetAsync(original.Id));
        Assert.Equal(JournalEntryStatus.Voided, reloaded!.Status);
        Assert.Equal("Changed: Rent went up", reloaded.VoidReason);
        Assert.Equal(JournalEntry.ManualSource, replacement.Source);
        Assert.Equal("Rent paid by the owner", replacement.Memo);
        Assert.True(replacement.Number > original.Number);
        var tb = await In<TrialBalanceQuery, TrialBalance>(s => s.RunAsync(Jan10, ReportBasis.Accrual));
        Assert.Equal(950m, tb.Lines.Single(l => l.AccountId == _rent.Id).Debit.Amount);
        var audit = await _db.InDb(db => db.AuditRecords.AsNoTracking().Where(r => r.EntityId == original.Id && r.Action == OpenBook.Domain.Auditing.AuditAction.Updated).ToListAsync());
        Assert.Contains(audit, r => r.After!.Contains("Rent went up", StringComparison.Ordinal) && r.After.Contains($"{replacement.Number}", StringComparison.Ordinal));
        await AssertBooksAgree();
    }

    [Fact]
    public async Task Only_hand_entered_entries_without_an_automatic_reversal_can_be_changed()
    {
        var accrual = await PostRent(500m, reverseOn: new DateOnly(2026, 2, 1));
        var command = new PostJournalEntryCommand(Jan5, [JournalLineDraft.Dr(_rent.Id, 10m), JournalLineDraft.Cr(_equity.Id, 10m)]);
        Assert.Equal("journal.change_reversing", await Refused(() => In<PostingEngine, JournalEntry>(s => s.ChangeAsync(accrual.Id, command, "Wrong"))));

        var invoice = await In<InvoiceService, Invoice>(s => s.CreateAsync(new CreateInvoiceCommand(_acme.Id, Jan5, [new SalesLineDraft(_plan.Id, null, 1m, 100m)])));
        Assert.Equal("journal.change_source", await Refused(() => In<PostingEngine, JournalEntry>(s => s.ChangeAsync(invoice.JournalEntryId, command, "Wrong"))));

        var plain = await PostRent(100m);
        Assert.Equal("journal.change_reason_required", await Refused(() => In<PostingEngine, JournalEntry>(s => s.ChangeAsync(plain.Id, command, " "))));
        Assert.Equal(JournalEntryStatus.Posted, (await In<PostingEngine, JournalEntry?>(s => s.GetAsync(plain.Id)))!.Status);
    }

    // ---- Deposits ----

    private async Task<CustomerPayment> Paid(Customer customer, decimal hours)
    {
        var invoice = await In<InvoiceService, Invoice>(s => s.CreateAsync(new CreateInvoiceCommand(customer.Id, Jan5, [new SalesLineDraft(_plan.Id, null, hours, 100m)])));
        return await In<PaymentService, CustomerPayment>(s => s.ReceiveAsync(new ReceivePaymentCommand(customer.Id, Jan5, _undeposited.Id, [new ApplyToInvoice(invoice.Id, invoice.Total)])));
    }

    [Fact]
    public async Task Changing_a_deposit_moves_payments_in_and_out_of_it()
    {
        var first = await Paid(_acme, 1m);
        var second = await Paid(_globex, 2m);
        var third = await Paid(_acme, 3m);
        var deposit = await In<DepositService, BankDeposit>(s => s.DepositAsync(new MakeDepositCommand(_checking.Id, Jan10,
            [(DepositedKind.Payment, first.Id), (DepositedKind.Payment, second.Id)])));

        var changed = await In<DepositService, BankDeposit>(s => s.ChangeAsync(deposit.Id, new MakeDepositCommand(_checking.Id, Jan10,
            [(DepositedKind.Payment, first.Id), (DepositedKind.Payment, third.Id)], "Two slips"), "Globex's check bounced back to the drawer"));

        Assert.Equal(deposit.Id, changed.Id);
        Assert.Equal(400m, changed.Total);
        Assert.NotEqual(deposit.JournalEntryId, changed.JournalEntryId);
        var waiting = await In<DepositService, IReadOnlyList<UndepositedItem>>(s => s.UndepositedAsync());
        Assert.Equal(second.Id, Assert.Single(waiting).Id);
        Assert.Equal(deposit.Id, (await In<PaymentService, CustomerPayment?>(s => s.GetAsync(third.Id)))!.DepositId);
        Assert.Null((await In<PaymentService, CustomerPayment?>(s => s.GetAsync(second.Id)))!.DepositId);
        Assert.Single(await In<DepositService, IReadOnlyList<DocumentChange>>(s => s.ChangesAsync(deposit.Id)));
        await AssertBooksAgree();

        Assert.Equal("deposit.empty", await Refused(() => In<DepositService, BankDeposit>(s => s.ChangeAsync(deposit.Id,
            new MakeDepositCommand(_checking.Id, Jan10, []), "Nothing"))));
        Assert.Equal("deposit.before_payment", await Refused(() => In<DepositService, BankDeposit>(s => s.ChangeAsync(deposit.Id,
            new MakeDepositCommand(_checking.Id, new DateOnly(2026, 1, 1), [(DepositedKind.Payment, first.Id)]), "Earlier"))));
    }

    // ---- Batch invoicing ----

    [Fact]
    public async Task A_batch_previews_without_making_anything_then_invoices_each_customer_at_their_price()
    {
        var level = await In<PricingService, PriceLevel>(s => s.CreateLevelAsync(new PriceLevelDetails("Partners", PriceLevelKind.FixedPercent, -10m)));
        await In<CustomerService, Customer>(s => s.UpdateAsync(_globex.Id, new CustomerDetails("Globex", PaymentTermsDays: 15, PriceLevelId: level.Id)));
        var command = new BatchInvoiceCommand([_acme.Id, _globex.Id], Jan5, [new SalesLineDraft(_plan.Id, null, 1m, 0m, AutoPrice: true)], "January support");

        var preview = await In<BatchInvoiceService, IReadOnlyList<BatchInvoiceLine>>(s => s.PreviewAsync(command));
        Assert.Equal(["Acme", "Globex"], preview.Select(p => p.Customer));
        Assert.Equal([100m, 90m], preview.Select(p => p.Total));
        Assert.Equal(new DateOnly(2026, 2, 4), preview[0].DueDate);
        Assert.Equal(0, await _db.InDb(db => db.Invoices.CountAsync()));

        var made = await In<BatchInvoiceService, IReadOnlyList<BatchInvoiceLine>>(s => s.CreateAsync(command));
        Assert.Equal(preview.Select(p => (p.Customer, p.Number, p.Total)), made.Select(m => (m.Customer, m.Number, m.Total)));
        Assert.Equal(2, await _db.InDb(db => db.Invoices.CountAsync()));
        await AssertBooksAgree();
    }

    [Fact]
    public async Task A_batch_with_one_refusal_makes_no_invoices_and_names_the_customer()
    {
        await In<ItemService, Item>(s => s.SetActiveAsync(_plan.Id, active: false));
        var command = new BatchInvoiceCommand([_acme.Id, _globex.Id], Jan5, [new SalesLineDraft(_plan.Id, null, 1m, 100m)]);

        var refusal = await Assert.ThrowsAsync<DomainException>(() => In<BatchInvoiceService, IReadOnlyList<BatchInvoiceLine>>(s => s.CreateAsync(command)));

        Assert.StartsWith("Acme: ", refusal.Message, StringComparison.Ordinal);
        Assert.Equal(0, await _db.InDb(db => db.Invoices.CountAsync()));
        Assert.Equal("batch.no_customers", await Refused(() => In<BatchInvoiceService, IReadOnlyList<BatchInvoiceLine>>(s => s.CreateAsync(command with { CustomerIds = [] }))));
    }

    // ---- Statements ----

    [Fact]
    public async Task Statements_go_to_customers_with_an_email_address_and_the_others_are_listed()
    {
        await Paid(_acme, 1m);
        await In<InvoiceService, Invoice>(s => s.CreateAsync(new CreateInvoiceCommand(_globex.Id, Jan5, [new SalesLineDraft(_plan.Id, null, 2m, 100m)])));
        Assert.Equal("email.not_configured", await Refused(() => In<EmailService, StatementEmailRun>(s => s.SendStatementsAsync([_acme.Id], Jan5, Jan10))));
        await _db.InScope(sp => sp.GetRequiredService<EmailService>().SaveSettingsAsync(new EmailSettingsCommand(
            EmailDelivery.Smtp, "smtp.test", 587, SmtpSecurity.Auto, "books", "s3cret-pass", false, "books@studio.test", "Studio", null)));

        var run = await In<EmailService, StatementEmailRun>(s => s.SendStatementsAsync([_globex.Id, _acme.Id], Jan5, Jan10));

        Assert.Equal(["Acme"], run.Sent);
        Assert.Equal(["Globex"], run.NoEmail);
        Assert.Empty(run.Failed);
        var message = Assert.Single(_db.Mail.Sent).Message;
        Assert.Equal("ap@acme.test", message.To);
        Assert.Equal("application/pdf", Assert.Single(message.Attachments).ContentType);
    }
}
