using Microsoft.EntityFrameworkCore;
using OpenBook.Application.Banking;
using OpenBook.Application.Import;
using OpenBook.Domain.Common;

namespace OpenBook.Integration.Tests;

public sealed class BankImportTests : BankingFixture
{
    private const string SignedCsv = """
        Date,Description,Amount,Check Number
        10/02/2026,Landlord LLC,-1500.00,1042
        10/15/2026,Customer A,"2,400.00",
        10/20/2026,Coffee Shop,-4.50,
        10/20/2026,Coffee Shop,-4.50,
        """;

    private Task<BankImportPreview> Preview(string csv, BankImportOptions? options = null) =>
        Importing(s => s.PreviewAsync(Checking.Id, csv, options ?? new BankImportOptions()));

    private Task<int> Import(BankImportPreview preview) =>
        Importing(s => s.ImportAsync(Checking.Id, preview.Rows
            .Where(r => r.IsValid)
            .Select(r => new ImportBankRowCommand(r.Date!.Value, r.Amount!.Value, r.Amount > 0m ? Sales.Id : Rent.Id, r.Payee, r.Reference, r.Memo))
            .ToList()));

    [Fact]
    public async Task Reads_a_signed_amount_file_and_imports_every_row_in_one_batch()
    {
        var preview = await Preview(SignedCsv);

        Assert.Equal(DateOrder.MonthFirst, preview.DateOrder);
        Assert.False(preview.DateOrderAmbiguous);
        Assert.All(preview.Rows, r => Assert.True(r.IsValid));
        Assert.Equal([-1500m, 2400m, -4.50m, -4.50m], preview.Rows.Select(r => r.Amount!.Value));
        Assert.Equal("1042", preview.Rows[0].Reference);

        Assert.Equal(4, await Import(preview));

        Assert.Equal(891m, await Balance(Checking));
        Assert.Equal(1, await Db.InDb(db => db.BankTransactions.Select(t => t.ImportBatchId).Distinct().CountAsync()));
        var numbers = await Db.InDb(db => db.JournalEntries.OrderBy(e => e.Number).Select(e => e.Number).ToListAsync());
        Assert.Equal([1L, 2L, 3L, 4L], numbers);
    }

    [Fact]
    public async Task Reimporting_flags_every_row_as_a_possible_duplicate()
    {
        await Import(await Preview(SignedCsv));

        var again = await Preview(SignedCsv);

        Assert.All(again.Rows, r => Assert.True(r.PossibleDuplicate));
    }

    [Fact]
    public async Task Separate_money_out_and_in_columns_and_day_first_dates()
    {
        const string csv = """
            Posted Date;Payee;Withdrawals;Deposits;Notes
            02/10/2026;Landlord;1.500,00;;October rent
            25/10/2026;Customer A;;2.400,00;
            """;

        var preview = await Preview(csv);

        Assert.True(preview.HasDebitCreditColumns);
        Assert.Equal(DateOrder.DayFirst, preview.DateOrder);
        Assert.Equal(new DateOnly(2026, 10, 2), preview.Rows[0].Date);
        Assert.Equal([-1500m, 2400m], preview.Rows.Select(r => r.Amount!.Value));
        Assert.Equal("October rent", preview.Rows[0].Memo);
    }

    [Fact]
    public async Task Ambiguous_dates_are_flagged_and_can_be_overridden()
    {
        const string csv = "Date,Description,Amount\n01/02/2026,Shop,-5.00\n";

        var auto = await Preview(csv);
        var dayFirst = await Preview(csv, new BankImportOptions(DateOrder.DayFirst));

        Assert.True(auto.DateOrderAmbiguous);
        Assert.False(dayFirst.DateOrderAmbiguous);
        Assert.Equal(new DateOnly(2026, 2, 1), dayFirst.Rows[0].Date);
    }

    [Fact]
    public async Task Sign_convention_can_be_flipped_for_card_style_exports()
    {
        var preview = await Preview("Date,Description,Amount\n2026-10-03,Shop,25.00\n", new BankImportOptions(AmountSign: AmountSign.PositiveIsMoneyOut));
        Assert.Equal(-25m, preview.Rows[0].Amount);
    }

    [Fact]
    public async Task Bad_rows_carry_their_own_errors()
    {
        const string csv = """
            Date,Description,Amount
            2026-10-01,Good,-5.00
            soon,Bad date,-5.00
            2026-10-02,Bad amount,lots
            2026-10-03,Zero,0.00
            """;

        var rows = (await Preview(csv)).Rows;

        Assert.True(rows[0].IsValid);
        Assert.Contains("date 'soon'", rows[1].Error, StringComparison.Ordinal);
        Assert.Contains("amount 'lots'", rows[2].Error, StringComparison.Ordinal);
        Assert.Contains("zero", rows[3].Error, StringComparison.Ordinal);
        Assert.Equal(3, rows[1].Line);
    }

    [Theory]
    [InlineData("Description,Amount\nShop,5\n", "import.no_date_column")]
    [InlineData("Date,Description,Debit\n2026-01-01,Shop,5\n", "import.no_amount_column")]
    public async Task Missing_columns_are_reported(string csv, string code) =>
        Assert.Equal(code, (await Assert.ThrowsAsync<DomainException>(() => Preview(csv))).Code);
}
