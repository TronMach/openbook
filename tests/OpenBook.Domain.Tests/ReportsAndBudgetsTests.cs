using OpenBook.Domain.Accounts;
using OpenBook.Domain.Common;
using OpenBook.Domain.Companies;
using OpenBook.Domain.Reporting;

namespace OpenBook.Domain.Tests;

public class ReportsAndBudgetsTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 31, 9, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("Sch C: Gross receipts or sales", "c.1")]
    [InlineData("Sch C: Legal and professional fees", "c.17")]
    [InlineData("Sch C: Meals and entertainment", "c.24b")]
    [InlineData("Sch C: Wages paid", "c.26")]
    [InlineData("Sch C: Rent or lease-other bus. prop.", null)]
    [InlineData("B/S-Assets: Cash", null)]
    [InlineData("<Unassigned>", null)]
    public void Quickbooks_tax_line_names_map_to_the_forms_lines_only_when_one_clearly_fits(string name, string? key) =>
        Assert.Equal(key, OpenBook.Domain.Accounts.TaxLines.FromName(name, OpenBook.Domain.Accounts.TaxForm.ScheduleC)?.Key);

    [Fact]
    public void Tax_line_keys_are_unique_and_every_form_offers_lines()
    {
        Assert.Equal(TaxLines.All.Count, TaxLines.All.Select(l => l.Key).Distinct().Count());
        foreach (var form in Enum.GetValues<TaxForm>())
        {
            var lines = TaxLines.For(form);
            Assert.Contains(lines, l => l.IsIncome);
            Assert.Contains(lines, l => !l.IsIncome);
            Assert.Equal(TaxLines.NonDeductibleKey, lines[^1].Key);
        }

        Assert.Equal("Line 8: Advertising", TaxLines.Find("c.8")!.Label);
        Assert.Equal("Line VIII 1: Contributions, gifts and grants", TaxLines.Find("n.8.1")!.Label);
        Assert.Null(TaxLines.Find("c.99"));
    }

    [Theory]
    [InlineData(BusinessType.SoleProprietorship, TaxForm.ScheduleC)]
    [InlineData(BusinessType.SingleMemberLlc, TaxForm.ScheduleC)]
    [InlineData(BusinessType.MultiMemberLlc, TaxForm.Form1065)]
    [InlineData(BusinessType.Partnership, TaxForm.Form1065)]
    [InlineData(BusinessType.SCorporation, TaxForm.Form1120S)]
    [InlineData(BusinessType.CCorporation, TaxForm.Form1120)]
    [InlineData(BusinessType.Nonprofit, TaxForm.Form990)]
    [InlineData(BusinessType.Other, TaxForm.ScheduleC)]
    public void Business_type_picks_the_tax_form(BusinessType type, TaxForm form) => Assert.Equal(form, TaxLines.FormFor(type));

    [Fact]
    public void Only_profit_and_loss_accounts_take_a_tax_line()
    {
        var rent = Account.Create("Rent", AccountType.Expense);
        rent.SetTaxLine(" c.20b ");
        Assert.Equal("c.20b", rent.TaxLineKey);
        rent.SetTaxLine("");
        Assert.Null(rent.TaxLineKey);

        Assert.Equal("account.tax_line_invalid", Assert.Throws<DomainException>(() => rent.SetTaxLine("c.99")).Code);
        var bank = Account.Create("Checking", AccountType.Bank);
        Assert.Equal("account.tax_line_balance_sheet", Assert.Throws<DomainException>(() => bank.SetTaxLine("c.8")).Code);

        rent.SetTaxLine("c.20b");
        rent.ChangeType(AccountType.OtherCurrentAsset);
        Assert.Null(rent.TaxLineKey);
    }

    [Fact]
    public void Budget_amounts_are_replaced_whole_and_zeros_dropped()
    {
        var account = Guid.NewGuid();
        var budget = Budget.Create(" 2026 plan ", new DateOnly(2026, 7, 1), Now);
        Assert.Equal("2026 plan", budget.Name);
        Assert.Equal(new DateOnly(2027, 6, 30), budget.FiscalYearEnd);
        Assert.Equal(new DateOnly(2027, 1, 1), budget.PeriodStart(7));

        budget.SetAmounts([new(account, 1, 100m), new(account, 2, 0m), new(account, 12, 50.25m)]);
        Assert.Equal(2, budget.Lines.Count);
        Assert.Equal(50.25m, budget.AmountFor(account, 12));

        budget.SetAmounts([new(account, 3, 10m)]);
        Assert.Equal(0m, budget.AmountFor(account, 1));
        Assert.Equal(10m, budget.AmountFor(account, 3));
    }

    [Theory]
    [InlineData(0, 1, "budget.period_invalid")]
    [InlineData(13, 1, "budget.period_invalid")]
    [InlineData(1, 1.005, "budget.amount_precision")]
    [InlineData(1, 1_000_000_000_000, "budget.amount_too_large")]
    public void Budget_rejects_bad_amounts(int period, double amount, string code)
    {
        var budget = Budget.Create("Plan", new DateOnly(2026, 1, 1), Now);
        var ex = Assert.Throws<DomainException>(() => budget.SetAmounts([new(Guid.NewGuid(), period, (decimal)amount)]));
        Assert.Equal(code, ex.Code);
    }

    [Fact]
    public void Budget_rejects_duplicates_names_and_mid_month_starts()
    {
        var account = Guid.NewGuid();
        var budget = Budget.Create("Plan", new DateOnly(2026, 1, 1), Now);
        Assert.Equal("budget.duplicate", Assert.Throws<DomainException>(() => budget.SetAmounts([new(account, 1, 1m), new(account, 1, 2m)])).Code);
        Assert.Equal("budget.name_required", Assert.Throws<DomainException>(() => budget.Rename("  ")).Code);
        Assert.Equal("budget.start_invalid", Assert.Throws<DomainException>(() => Budget.Create("Plan", new DateOnly(2026, 1, 15), Now)).Code);
    }

    [Fact]
    public void Saved_report_keeps_the_query_without_its_question_mark()
    {
        var report = SavedReport.Create(" Monthly P&L ", "profit-and-loss", "?preset=LastMonth&compare=PreviousYear", Now, "owner");
        Assert.Equal("Monthly P&L", report.Name);
        Assert.Equal("preset=LastMonth&compare=PreviousYear", report.Query);
        Assert.Equal("report.query_too_long", Assert.Throws<DomainException>(() => report.SetQuery(new string('a', SavedReport.MaxQueryLength + 1))).Code);
        Assert.Equal("report.name_required", Assert.Throws<DomainException>(() => SavedReport.Create("", "journal", null, Now, "owner")).Code);
    }
}
