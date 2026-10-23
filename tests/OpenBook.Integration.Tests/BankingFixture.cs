using Microsoft.Extensions.DependencyInjection;
using OpenBook.Application.Banking;
using OpenBook.Application.Ledger;
using OpenBook.Domain.Accounts;
using OpenBook.Domain.Banking;
using OpenBook.Domain.Ledger;

namespace OpenBook.Integration.Tests;

/// <summary>A set-up company with a checking account, a credit card and a few categories.</summary>
public abstract class BankingFixture : IAsyncLifetime
{
    protected static readonly DateOnly Jan1 = new(2026, 1, 1);

    protected LedgerDatabase Db { get; } = new();
    protected Account Checking { get; private set; } = null!;
    protected Account Visa { get; private set; } = null!;
    protected Account Rent { get; private set; } = null!;
    protected Account Supplies { get; private set; } = null!;
    protected Account Sales { get; private set; } = null!;
    protected Account Equity { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        await Db.InitializeAsync();
        Checking = await Db.CreateAccount("Checking", AccountType.Bank, "1000");
        Visa = await Db.CreateAccount("Visa", AccountType.CreditCard, "2100");
        Rent = await Db.CreateAccount("Rent", AccountType.Expense, "6500");
        Supplies = await Db.CreateAccount("Supplies", AccountType.Expense, "6300");
        Sales = await Db.CreateAccount("Sales", AccountType.Income, "4000");
        Equity = await Db.CreateAccount("Owner's Equity", AccountType.Equity, "3000");
    }

    public Task DisposeAsync() => Db.DisposeAsync();

    protected Task<T> Banking<T>(Func<BankingService, Task<T>> work) => Db.InScope(sp => work(sp.GetRequiredService<BankingService>()));

    protected Task<T> Reconciling<T>(Func<ReconciliationService, Task<T>> work) => Db.InScope(sp => work(sp.GetRequiredService<ReconciliationService>()));

    protected Task<T> Importing<T>(Func<BankImportService, Task<T>> work) => Db.InScope(sp => work(sp.GetRequiredService<BankImportService>()));

    protected Task<BankTransaction> Spend(Account from, DateOnly date, decimal amount, Account category, string? payee = null, string? reference = null) =>
        Banking(s => s.RecordAsync(new RecordBankTransactionCommand(BankTransactionKind.Spend, from.Id, date, [new BankSplit(category.Id, amount)], payee, reference)));

    protected Task<BankTransaction> Receive(Account into, DateOnly date, decimal amount, Account category, string? payee = null) =>
        Banking(s => s.RecordAsync(new RecordBankTransactionCommand(BankTransactionKind.Receive, into.Id, date, [new BankSplit(category.Id, amount)], payee)));

    /// <summary>An opening balance posted as a manual journal entry.</summary>
    protected Task<JournalEntry> Opening(Account bank, decimal amount, DateOnly date) =>
        Db.InScope(sp => sp.GetRequiredService<PostingEngine>().PostAsync(new PostJournalEntryCommand(
            date, [JournalLineDraft.Dr(bank.Id, amount), JournalLineDraft.Cr(Equity.Id, amount)], "Opening balance")));

    protected async Task<decimal> Balance(Account account) =>
        (await Banking(s => s.ListAccountsAsync())).Single(a => a.Account.Id == account.Id).Balance;
}
