using Microsoft.Extensions.DependencyInjection;
using OpenBook.Application.Banking;
using OpenBook.Application.Ledger;
using OpenBook.Domain.Banking;
using OpenBook.Domain.Common;
using static OpenBook.Domain.Ledger.JournalLineDraft;

namespace OpenBook.Integration.Tests;

public sealed class ReconciliationTests : BankingFixture
{
    private static readonly DateOnly Jan31 = new(2026, 1, 31);
    private static readonly DateOnly Feb28 = new(2026, 2, 28);

    private Task<Reconciliation> Start(DateOnly date, decimal ending) => Reconciling(s => s.StartAsync(Checking.Id, date, ending));

    private Task<ReconciliationWorksheet> Worksheet(Guid id) => Reconciling(s => s.GetWorksheetAsync(id));

    private Task<ReconciliationWorksheet> Tick(Guid id, IEnumerable<ReconcileItem> items, bool cleared = true) =>
        Reconciling(s => s.SetClearedAsync(id, items.Select(i => i.Line.JournalLineId).ToList(), cleared));

    private async Task<ReconciliationWorksheet> TickAll(Guid id) => await Tick(id, (await Worksheet(id)).Items);

    [Fact]
    public async Task Full_cycle_with_an_outstanding_check_and_a_report_that_ties_out()
    {
        await Opening(Checking, 1000m, Jan1);
        await Receive(Checking, new DateOnly(2026, 1, 10), 500m, Sales, "Customer A");
        await Spend(Checking, new DateOnly(2026, 1, 15), 200m, Rent, "Landlord", "1001");
        var outstanding = await Spend(Checking, new DateOnly(2026, 1, 28), 75m, Supplies, "Supply Co", "1002");
        await Spend(Checking, new DateOnly(2026, 2, 3), 50m, Supplies, "After statement");

        var rec = await Start(Jan31, 1300m);
        var sheet = await Worksheet(rec.Id);
        Assert.Equal(0m, sheet.Reconciliation.BeginningBalance);
        Assert.Equal(4, sheet.Items.Count); // the February check isn't on this statement

        sheet = await Tick(rec.Id, sheet.Items.Where(i => i.Document?.Id != outstanding.Id));
        Assert.Equal(0m, sheet.Difference);
        await Reconciling(s => s.CompleteAsync(rec.Id));

        var report = await Reconciling(s => s.GetReportAsync(rec.Id));
        Assert.Equal(1500m, report.ClearedIncreases);
        Assert.Equal(-200m, report.ClearedDecreases);
        Assert.Equal("Supply Co", Assert.Single(report.Outstanding).Description);
        Assert.Equal(1225m, report.RegisterBalance);
        Assert.Equal(report.Reconciliation.StatementEndingBalance + report.OutstandingTotal, report.RegisterBalance);

        var summary = (await Banking(s => s.ListAccountsAsync())).Single(a => a.Account.Id == Checking.Id);
        Assert.Equal(1300m, summary.ReconciledBalance);
        Assert.Equal(Jan31, summary.LastReconciledDate);
    }

    [Fact]
    public async Task Next_statement_starts_from_the_reconciled_balance_and_includes_older_outstanding_items()
    {
        await Opening(Checking, 1000m, Jan1);
        var late = await Spend(Checking, new DateOnly(2026, 1, 28), 75m, Supplies, "Supply Co");
        var first = await Start(Jan31, 1000m);
        await Tick(first.Id, (await Worksheet(first.Id)).Items.Where(i => i.Document?.Id != late.Id));
        await Reconciling(s => s.CompleteAsync(first.Id));

        var tooEarly = await Assert.ThrowsAsync<DomainException>(() => Start(new DateOnly(2026, 1, 30), 925m));
        Assert.Equal("reconcile.date_before_previous", tooEarly.Code);

        var second = await Start(Feb28, 925m);
        var sheet = await Worksheet(second.Id);
        Assert.Equal(1000m, sheet.Reconciliation.BeginningBalance);
        Assert.Equal(late.Id, Assert.Single(sheet.Items).Document?.Id);
    }

    [Fact]
    public async Task Cannot_finish_with_a_difference_or_start_a_second_one()
    {
        await Opening(Checking, 1000m, Jan1);
        var rec = await Start(Jan31, 999m);
        await TickAll(rec.Id);

        Assert.Equal("reconcile.not_balanced", (await Assert.ThrowsAsync<DomainException>(() => Reconciling(s => s.CompleteAsync(rec.Id)))).Code);
        Assert.Equal("reconcile.in_progress", (await Assert.ThrowsAsync<DomainException>(() => Start(Feb28, 0m))).Code);
    }

    [Fact]
    public async Task Ticked_and_reconciled_transactions_cannot_be_voided()
    {
        var check = await Spend(Checking, new DateOnly(2026, 1, 15), 200m, Rent);
        var rec = await Start(Jan31, -200m);
        await TickAll(rec.Id);

        Task Void() => Banking(s => s.VoidAsync(check.Id, "mistake"));
        Assert.Equal("journal.reconciled", (await Assert.ThrowsAsync<DomainException>(Void)).Code);

        await Reconciling(s => s.CompleteAsync(rec.Id));
        Assert.Equal("journal.reconciled", (await Assert.ThrowsAsync<DomainException>(Void)).Code);

        // A refused void leaves nothing changed, even in the same unit of work.
        var status = await Db.InScope(async sp =>
        {
            var banking = sp.GetRequiredService<BankingService>();
            await Assert.ThrowsAsync<DomainException>(() => banking.VoidAsync(check.Id, "mistake"));
            return (await banking.GetAsync(check.Id))!.Status;
        });
        Assert.Equal(BankTransactionStatus.Posted, status);
    }

    [Fact]
    public async Task Voided_transactions_and_their_reversals_never_appear_on_the_worksheet()
    {
        var check = await Spend(Checking, new DateOnly(2026, 1, 15), 200m, Rent);
        await Banking(s => s.VoidAsync(check.Id, "duplicate"));
        await Receive(Checking, new DateOnly(2026, 1, 20), 50m, Sales);

        var sheet = await Worksheet((await Start(Jan31, 50m)).Id);

        Assert.Equal(50m, Assert.Single(sheet.Items).NaturalAmount);
    }

    [Fact]
    public async Task Moving_the_statement_date_earlier_unticks_later_transactions()
    {
        await Opening(Checking, 1000m, Jan1);
        await Spend(Checking, new DateOnly(2026, 1, 25), 100m, Rent);
        var rec = await Start(Jan31, 900m);
        Assert.Equal(0m, (await TickAll(rec.Id)).Difference);

        var sheet = await Reconciling(s => s.UpdateStatementAsync(rec.Id, new DateOnly(2026, 1, 20), 1000m));

        Assert.Equal(1000m, sheet.ClearedTotal);
        Assert.Single(sheet.Items);
    }

    [Fact]
    public async Task Cancelling_discards_the_ticks_so_it_can_start_over()
    {
        await Opening(Checking, 1000m, Jan1);
        var rec = await Start(Jan31, 1000m);
        await TickAll(rec.Id);

        await Reconciling(async s => { await s.CancelAsync(rec.Id); return 0; });

        var again = await Worksheet((await Start(Jan31, 1000m)).Id);
        Assert.Equal(0m, again.ClearedTotal);
        Assert.Single(again.Items);
    }

    [Fact]
    public async Task Lines_outside_the_statement_cannot_be_ticked()
    {
        var later = await Spend(Checking, new DateOnly(2026, 2, 5), 10m, Rent);
        var rec = await Start(Jan31, 0m);
        var lineId = (await Banking(s => s.GetRegisterAsync(Checking.Id))).Lines.Single(l => l.Document?.Id == later.Id).Line.JournalLineId;

        var ex = await Assert.ThrowsAsync<DomainException>(() => Reconciling(s => s.SetClearedAsync(rec.Id, [lineId], true)));
        Assert.Equal("reconcile.line_not_eligible", ex.Code);
    }

    [Fact]
    public async Task Manual_journal_entries_on_the_bank_account_are_reconcilable()
    {
        await Db.InScope(sp => sp.GetRequiredService<PostingEngine>().PostAsync(new PostJournalEntryCommand(
            new DateOnly(2026, 1, 31), [Dr(Rent.Id, 12m), Cr(Checking.Id, 12m)], "Bank fee")));

        var item = Assert.Single((await Worksheet((await Start(Jan31, -12m)).Id)).Items);

        Assert.Equal("Bank fee", item.Description);
        Assert.Equal(-12m, item.NaturalAmount);
    }

    [Fact]
    public async Task Credit_card_statements_use_the_amount_owed()
    {
        await Spend(Visa, new DateOnly(2026, 1, 3), 120m, Supplies, "Office Depot");
        var rec = await Reconciling(s => s.StartAsync(Visa.Id, Jan31, 120m));

        var sheet = await TickAll(rec.Id);

        Assert.Equal(120m, Assert.Single(sheet.Items).NaturalAmount);
        Assert.Equal(0m, sheet.Difference);
    }
}
