using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OpenBook.Application.Accounts;
using OpenBook.Application.Banking;
using OpenBook.Application.Ledger;
using OpenBook.Domain.Accounts;
using OpenBook.Domain.Banking;
using OpenBook.Domain.Common;
using OpenBook.Domain.Ledger;

namespace OpenBook.Integration.Tests;

public sealed class BankingServiceTests : BankingFixture
{
    [Fact]
    public async Task Check_posts_a_balanced_entry_linked_both_ways_and_suggests_the_next_number()
    {
        await Opening(Checking, 1000m, Jan1);
        var check = await Spend(Checking, new DateOnly(2026, 1, 5), 250m, Rent, "Landlord LLC", "1001");

        var entry = await Db.InScope(sp => sp.GetRequiredService<PostingEngine>().GetAsync(check.JournalEntryId));
        Assert.Equal(BankTransaction.SpendSource, entry!.Source);
        Assert.Equal(check.Id, entry.SourceId);
        Assert.Equal("Landlord LLC", entry.Memo);
        Assert.Equal(750m, await Balance(Checking));
        Assert.Equal("1002", await Banking(s => s.SuggestCheckNumberAsync(Checking.Id)));
    }

    [Fact]
    public async Task Card_charges_raise_what_is_owed_and_a_payment_transfer_lowers_it()
    {
        await Opening(Checking, 1000m, Jan1);
        await Banking(s => s.RecordAsync(new RecordBankTransactionCommand(
            BankTransactionKind.Spend, Visa.Id, new DateOnly(2026, 1, 3),
            [new BankSplit(Supplies.Id, 120m), new BankSplit(Rent.Id, 80m), new BankSplit(Supplies.Id, -20m, "Coupon")], "Office Depot")));
        Assert.Equal(180m, await Balance(Visa));

        await Banking(s => s.TransferAsync(new TransferCommand(Checking.Id, Visa.Id, new DateOnly(2026, 1, 20), 180m)));

        Assert.Equal(0m, await Balance(Visa));
        Assert.Equal(820m, await Balance(Checking));
    }

    [Fact]
    public async Task Register_shows_payees_running_balances_and_counterparts_newest_first()
    {
        await Opening(Checking, 1000m, Jan1);
        await Spend(Checking, new DateOnly(2026, 1, 5), 250m, Rent, "Landlord");
        await Receive(Checking, new DateOnly(2026, 1, 9), 400m, Sales, "Customer A");

        var register = await Banking(s => s.GetRegisterAsync(Checking.Id));

        Assert.Equal(1150m, register.Balance);
        Assert.Equal(["Customer A", "Landlord", null], register.Lines.Select(l => l.Document?.Payee));
        Assert.Equal([1150m, 750m, 1000m], register.Lines.Select(l => l.Balance));
        Assert.Equal([Sales.Id], register.Lines[0].OtherAccountIds);
    }

    [Fact]
    public async Task Only_bank_and_card_accounts_can_record_bank_transactions()
    {
        var ex = await Assert.ThrowsAsync<DomainException>(() => Spend(Rent, Jan1, 10m, Supplies));
        Assert.Equal("bank.not_banking_account", ex.Code);
    }

    [Fact]
    public async Task Voiding_reverses_the_entry_and_the_journal_page_cannot_void_it_directly()
    {
        var check = await Spend(Checking, new DateOnly(2026, 1, 5), 250m, Rent);

        var direct = await Assert.ThrowsAsync<DomainException>(() =>
            Db.InScope(sp => sp.GetRequiredService<PostingEngine>().VoidAsync(check.JournalEntryId, "oops")));
        Assert.Equal("journal.void_document", direct.Code);

        var voided = await Banking(s => s.VoidAsync(check.Id, "wrong amount"));

        Assert.Equal(BankTransactionStatus.Voided, voided.Status);
        Assert.Equal(0m, await Balance(Checking));
        var register = await Banking(s => s.GetRegisterAsync(Checking.Id));
        Assert.Equal(2, register.Lines.Count);
        Assert.All(register.Lines, l => Assert.True(l.Line.IsVoidPair));
    }

    [Fact]
    public async Task Bank_account_type_is_locked_once_it_has_banking_records()
    {
        await Spend(Checking, Jan1, 10m, Rent);

        var ex = await Assert.ThrowsAsync<DomainException>(() => Db.InScope(sp => sp.GetRequiredService<AccountService>()
            .UpdateAsync(new UpdateAccountCommand(Checking.Id, "Checking", AccountType.OtherCurrentAsset, "1000", null, null))));
        Assert.Equal("account.type_banking", ex.Code);
    }

    [Fact]
    public async Task Documents_and_audit_are_saved_with_their_entry()
    {
        var check = await Spend(Checking, Jan1, 10m, Rent);

        Assert.True(await Db.InDb(db => db.BankTransactions.AnyAsync(t => t.Id == check.Id)));
        Assert.True(await Db.InDb(db => db.AuditRecords.AnyAsync(r => r.EntityType == "bank_transaction" && r.EntityId == check.Id)));
        Assert.Equal(check.JournalEntryId, await Db.InDb(db => db.JournalEntries.Where(e => e.SourceId == check.Id).Select(e => e.Id).SingleAsync()));
    }
}
