using System.IO.Compression;
using ClosedXML.Excel;
using Microsoft.Extensions.DependencyInjection;
using OpenBook.Application.Ledger;
using OpenBook.Application.Reporting;
using OpenBook.Domain.Accounts;
using OpenBook.Domain.Ledger;

namespace OpenBook.Integration.Tests;

/// <summary>The Journal report and the year-end package hold every entry of the period, however many.</summary>
public sealed class JournalReportTests : IAsyncLifetime
{
    private const int Entries = 503;
    private static readonly DateOnly First = new(2026, 1, 1);
    private static readonly DateOnly Last = new(2026, 12, 31);
    private readonly LedgerDatabase _db = new();

    public async Task InitializeAsync()
    {
        await _db.InitializeAsync();
        var cash = await _db.CreateAccount("Cash", AccountType.Bank, "1000");
        var sales = await _db.CreateAccount("Sales", AccountType.Income, "4000");
        await _db.InScope(async sp =>
        {
            var engine = sp.GetRequiredService<PostingEngine>();
            for (var i = 0; i < Entries; i++)
                await engine.PostAsync(new PostJournalEntryCommand(First.AddDays(i % 365), [JournalLineDraft.Dr(cash.Id, 1m), JournalLineDraft.Cr(sales.Id, 1m)]));
            return true;
        });
    }

    public Task DisposeAsync() => _db.DisposeAsync();

    [Fact]
    public async Task The_report_holds_every_entry_oldest_first_and_totals_them()
    {
        var table = await _db.InScope(sp => sp.GetRequiredService<ReportCatalog>().RunAsync("journal", ReportParameters.Parse($"preset=Custom&from={First:yyyy-MM-dd}&to={Last:yyyy-MM-dd}")));

        var dated = table.Rows.Select(r => r.Cells[1]?.Value).OfType<DateOnly>().ToList();
        Assert.Equal(Entries, dated.Count);
        Assert.Equal(First, dated[0]);
        Assert.Equal(dated.Order(), dated);
        Assert.Equal((decimal)Entries, table.Rows[^1].Cells[5]?.Value);
        Assert.Null(table.Note);
    }

    [Fact]
    public async Task The_year_end_package_holds_every_entry()
    {
        var file = await _db.InScope(sp => sp.GetRequiredService<AccountantPackageService>().BuildAsync(First, Last));

        try
        {
            using var zip = ZipFile.OpenRead(file.Path);
            await using var journal = zip.Entries.Single(e => e.Name == "08 Journal.xlsx").Open();
            using var copy = new MemoryStream();
            await journal.CopyToAsync(copy);
            copy.Position = 0;
            using var book = new XLWorkbook(copy);
            Assert.Contains(book.Worksheets.First().RowsUsed(), r => r.Cell(1).GetString() == "Total" && r.Cell(6).GetValue<decimal>() == Entries);
        }
        finally
        {
            File.Delete(file.Path);
        }
    }
}
