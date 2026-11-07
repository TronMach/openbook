using System.Globalization;
using System.Xml.Linq;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OpenBook.Application.Accounts;
using OpenBook.Application.Import.Migration;
using OpenBook.Domain.Ledger;
using OpenBook.QuickBooksExport;

namespace OpenBook.Integration.Tests;

/// <summary>
/// The QuickBooks Desktop export helper against a fake QuickBooks that answers qbXML requests like the request
/// processor does, and its files imported through the real qbXML import.
/// </summary>
public sealed class QuickBooksExportTests : IAsyncLifetime
{
    private readonly LedgerDatabase _db = new();
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "openbook-qb-" + Guid.NewGuid().ToString("N"));

    public Task InitializeAsync() => _db.InitializeAsync();

    public async Task DisposeAsync()
    {
        await _db.DisposeAsync();
        if (Directory.Exists(_folder))
            Directory.Delete(_folder, true);
    }

    private static readonly DateOnly Today = new(2026, 3, 20);

    private Task<MigrationResult> Import(string file, bool dryRun = false) => _db.InScope(sp =>
    {
        var service = sp.GetRequiredService<MigrationImportService>();
        var bytes = File.ReadAllBytes(file);
        return dryRun ? service.PreviewAsync(MigrationSource.QuickBooksQbxml, bytes) : service.ImportAsync(MigrationSource.QuickBooksQbxml, bytes);
    });

    private Task<List<JournalEntry>> Imported() =>
        _db.InDb(db => db.JournalEntries.AsNoTracking().Include(e => e.Lines).Where(e => e.Source == JournalEntry.ImportSource).ToListAsync());

    [Fact]
    public async Task The_whole_company_comes_over_from_one_file()
    {
        using var quickBooks = new FakeQuickBooks();
        var result = new QuickBooksExporter(quickBooks, TextWriter.Null).Export(new ExportOptions(_folder, Today));

        var file = Assert.Single(result.Files);
        Assert.Equal("Pine Hardware - for OpenBook.qbxml", Path.GetFileName(file));
        Assert.Equal((6, 3, 0, 1, 9), (result.Accounts, result.Customers, result.Vendors, result.Items, result.Lines));
        Assert.Equal(new DateOnly(2022, 5, 1), result.FirstDate);
        Assert.All(quickBooks.Requests, r => Assert.Contains("<?qbxml version=\"13.0\"?>", r, StringComparison.Ordinal));
        Assert.Contains(quickBooks.Requests, r => r.Contains("<ActiveStatus>All</ActiveStatus>", StringComparison.Ordinal));
        Assert.Contains(quickBooks.Requests, r => r.Contains("iterator=\"Continue\" iteratorID=\"{cust-1}\"", StringComparison.Ordinal));
        // Back from March 2026 month by month, then five empty years before May 2022 and it stops.
        Assert.Equal(47 + 60, quickBooks.Requests.Count(r => r.Contains("GeneralDetailReportQueryRq", StringComparison.Ordinal)));

        var company = MigrationReaders.ReadCompany(MigrationSource.QuickBooksQbxml, await File.ReadAllBytesAsync(file));
        Assert.Equal("Pine Hardware", company!.Name);

        var preview = await Import(file, dryRun: true);
        Assert.Equal(0, preview.ErrorCount);
        Assert.Equal(3, preview.Count(MigrationKind.Transaction, MigrationRowStatus.New));
        Assert.Equal(1, preview.Count(MigrationKind.Transaction, MigrationRowStatus.Skipped));

        await Import(file);
        var chart = await _db.InScope(sp => sp.GetRequiredService<AccountService>().GetChartAsync());
        var entries = (await Imported()).OrderBy(e => e.Date).ToList();
        Assert.Equal([new DateOnly(2022, 5, 10), new DateOnly(2026, 2, 3), new DateOnly(2026, 2, 10)], entries.Select(e => e.Date));
        Assert.Equal("Invoice 1001 · Acme · Shelving", entries[0].Memo);
        // "6000 · Utilities:6010 · Phone" and a bare "Phone" both find the subaccount.
        var phone = chart.FindByFullName("Utilities:Phone")!;
        Assert.Equal(80.25m, entries[1].Lines.Where(l => l.AccountId == phone.Id).Sum(l => l.Amount));
        Assert.Equal(2, entries[1].Lines.Count(l => l.AccountId == phone.Id));
        Assert.Equal(-250m, entries[2].Lines.Single(l => l.AccountId == chart.FindByFullName("Accounts Receivable")!.Id).Amount);
        Assert.Equal(3, await _db.InDb(db => db.Customers.CountAsync())); // with the job (Acme:Store) as a sub-customer
    }

    [Fact]
    public async Task Bigger_books_are_split_into_files_that_import_in_turn()
    {
        using var quickBooks = new FakeQuickBooks();
        var result = new QuickBooksExporter(quickBooks, TextWriter.Null).Export(new ExportOptions(_folder, Today, new DateOnly(2022, 1, 1), MaxFileBytes: 6_000));

        Assert.True(result.Files.Count >= 2, $"{result.Files.Count} files");
        Assert.All(result.Files, f => Assert.Contains($"of {result.Files.Count}.qbxml", f, StringComparison.Ordinal));
        Assert.All(result.Files, f => Assert.True(new FileInfo(f).Length <= 6_000, $"{f}: {new FileInfo(f).Length} bytes"));
        // With --from, every month back to it is asked for (and a month too big for a file again in halves).
        Assert.True(quickBooks.Requests.Count(r => r.Contains("GeneralDetailReportQueryRq", StringComparison.Ordinal)) >= 51);

        foreach (var file in result.Files)
            Assert.Equal(0, (await Import(file)).ErrorCount);
        Assert.Equal(3, (await Imported()).Count);
        Assert.Equal(3, await _db.InDb(db => db.Customers.CountAsync()));
    }

    [Fact]
    public void The_command_checks_its_options_and_reports_what_quickbooks_refuses()
    {
        var output = new StringWriter(CultureInfo.InvariantCulture);
        var error = new StringWriter(CultureInfo.InvariantCulture);
        Assert.Equal(2, ExportCommand.Run(["--from", "2026-13-01"], output, error, _ => new FakeQuickBooks(), Today));
        Assert.Contains("yyyy-mm-dd", error.ToString(), StringComparison.Ordinal);
        Assert.Equal(2, ExportCommand.Run(["--from", "2026-03-01", "--to", "2026-02-01"], output, error, _ => new FakeQuickBooks(), Today));
        Assert.Equal(0, ExportCommand.Run(["help"], output, error, _ => throw new InvalidOperationException(), Today));

        Assert.Equal(1, ExportCommand.Run(["--out", _folder], output, error, _ => new FakeQuickBooks { Refuse = "AccountQueryRq" }, Today));
        Assert.Contains("QuickBooks refused AccountQueryRq: There was an error when parsing the provided XML file. (status 3000)", error.ToString(), StringComparison.Ordinal);

        Assert.Equal(0, ExportCommand.Run(["--out", _folder, "--from", "2026-02-01"], output, error, file => new FakeQuickBooks(), Today));
        Assert.Contains("Pine Hardware - for OpenBook.qbxml", output.ToString(), StringComparison.Ordinal);

        Assert.Throws<QuickBooksException>(() => QuickBooksExporter.PickVersion(["2.1", "CA2.0"]));
        Assert.Equal("16.0", QuickBooksExporter.PickVersion(["CA3.0", "8.0", "16.0", "17.0"]));
    }

    /// <summary>
    /// Answers like QuickBooks' request processor: company, accounts (numbered, one subaccount, one non-posting),
    /// customers in pages of two (one a job), no vendors ("nothing found"), one item, and the Journal report for the
    /// asked dates.
    /// </summary>
    private sealed class FakeQuickBooks : IQuickBooksSession
    {
        public List<string> Requests { get; } = [];

        /// <summary>A request to answer with a status 3000 error.</summary>
        public string? Refuse { get; init; }

        public IReadOnlyList<string> Versions { get; } = ["CA3.0", "2.0", "13.0"];

        private static readonly (string Id, string Type, string Date, string? Number, string? Name, string? Memo, (string Account, decimal Debit, decimal Credit)[] Lines)[] Transactions =
        [
            ("T1", "Invoice", "2022-05-10", "1001", "Acme", "Shelving", [("1200 · Accounts Receivable", 250m, 0m), ("4000 · Sales", 0m, 250m)]),
            ("T2", "Check", "2026-02-03", "501", "Phone Co", null, [("1000 · Checking", 0m, 80.25m), ("6000 · Utilities:6010 · Phone", 60m, 0m), ("Phone", 20.25m, 0m)]),
            ("T3", "Payment", "2026-02-10", null, "Acme", null, [("1000 · Checking", 250m, 0m), ("1200 · Accounts Receivable", 0m, 250m)]),
            ("T4", "Invoice", "2026-02-12", "1002", "Acme", "No charge", [("1200 · Accounts Receivable", 0m, 0m), ("4000 · Sales", 0m, 0m)]),
        ];

        public string Process(string request)
        {
            Requests.Add(request);
            var rq = XDocument.Parse(request).Descendants().First(e => e.Name.LocalName.EndsWith("Rq", StringComparison.Ordinal) && e.Name.LocalName != "QBXMLMsgsRq");
            var name = rq.Name.LocalName;
            var rs = new XElement(name[..^2] + "Rs", new XAttribute("requestID", (string)rq.Attribute("requestID")!),
                new XAttribute("statusCode", "0"), new XAttribute("statusSeverity", "Info"), new XAttribute("statusMessage", "Status OK"));
            if (name == Refuse)
            {
                rs.SetAttributeValue("statusCode", "3000");
                rs.SetAttributeValue("statusSeverity", "Error");
                rs.SetAttributeValue("statusMessage", "There was an error when parsing the provided XML file.");
            }
            else if (name == "CompanyQueryRq")
                rs.Add(XElement.Parse("<CompanyRet><CompanyName>Pine Hardware</CompanyName><TaxForm>Form1120S</TaxForm><FirstMonthFiscalYear>January</FirstMonthFiscalYear></CompanyRet>"));
            else if (name == "AccountQueryRq")
            {
                foreach (var (full, type, number) in new[] { ("Checking", "Bank", "1000"), ("Accounts Receivable", "AccountsReceivable", "1200"), ("Sales", "Income", "4000"),
                    ("Utilities", "Expense", "6000"), ("Utilities:Phone", "Expense", "6010"), ("Estimates", "NonPosting", null) })
                {
                    rs.Add(new XElement("AccountRet", new XElement("Name", full.Split(':')[^1]), new XElement("FullName", full),
                        new XElement("Sublevel", full.Count(c => c == ':')), new XElement("AccountType", type), number is null ? null : new XElement("AccountNumber", number)));
                }
            }
            else if (name == "CustomerQueryRq")
            {
                var first = (string?)rq.Attribute("iterator") == "Start";
                Assert.True(first || (string?)rq.Attribute("iteratorID") == "{cust-1}");
                Assert.Equal("500", (string?)rq.Element("MaxReturned"));
                var customers = first ? new[] { ("Acme", "Acme", 0), ("Store", "Acme:Store", 1) } : [("Bolt Bros", "Bolt Bros", 0)];
                rs.Add(new XAttribute("iteratorRemainingCount", first ? 1 : 0), new XAttribute("iteratorID", "{cust-1}"),
                    customers.Select(c => new XElement("CustomerRet", new XElement("Name", c.Item1), new XElement("FullName", c.Item2), new XElement("Sublevel", c.Item3))));
            }
            else if (name == "VendorQueryRq")
            {
                rs.SetAttributeValue("statusCode", "1");
                rs.SetAttributeValue("statusMessage", "A query request did not find a matching object in QuickBooks");
                rs.Add(new XAttribute("iteratorRemainingCount", 0), new XAttribute("iteratorID", "{vend-1}"));
            }
            else if (name == "ItemQueryRq")
                rs.Add(XElement.Parse("<ItemServiceRet><Name>Install</Name><FullName>Install</FullName><SalesOrPurchase><Price>75.00</Price><AccountRef><FullName>Sales</FullName></AccountRef></SalesOrPurchase></ItemServiceRet>"));
            else if (name == "GeneralDetailReportQueryRq")
                rs.Add(Journal(rq));
            return new XDocument(new XElement("QBXML", new XElement("QBXMLMsgsRs", rs))).ToString();
        }

        private static XElement Journal(XElement rq)
        {
            Assert.Equal("Journal", (string?)rq.Element("GeneralDetailReportType"));
            var from = DateOnly.Parse((string)rq.Element("ReportPeriod")!.Element("FromReportDate")!, CultureInfo.InvariantCulture);
            var to = DateOnly.Parse((string)rq.Element("ReportPeriod")!.Element("ToReportDate")!, CultureInfo.InvariantCulture);
            var columns = rq.Elements("IncludeColumn").Select(c => c.Value).ToList();
            var report = new XElement("ReportRet", new XElement("ReportTitle", "Journal"), new XElement("ReportBasis", "Accrual"),
                new XElement("ColDesc", new XAttribute("colID", 1), new XAttribute("dataType", "STRTYPE"), new XElement("ColTitle", new XAttribute("titleRow", 1)), new XElement("ColType", "Blank")),
                columns.Select((c, i) => new XElement("ColDesc", new XAttribute("colID", i + 2), new XElement("ColTitle", new XAttribute("titleRow", 1), new XAttribute("value", c)), new XElement("ColType", c))));
            string Col(string type) => (columns.IndexOf(type) + 2).ToString(CultureInfo.InvariantCulture);
            var data = new XElement("ReportData");
            var row = 0;
            foreach (var t in Transactions.Where(t => DateOnly.Parse(t.Date, CultureInfo.InvariantCulture) is var d && d >= from && d <= to))
            {
                for (var i = 0; i < t.Lines.Length; i++)
                {
                    var line = t.Lines[i];
                    // Like the report on screen: the transaction's details on its first row only (the payment's rows lack the TxnID too).
                    var cells = new List<(string, string?)> { ("Account", line.Account), ("Debit", Amount(line.Debit)), ("Credit", Amount(line.Credit)) };
                    if (i == 0)
                        cells.AddRange([("TxnID", t.Id == "T3" ? null : t.Id), ("TxnType", t.Type), ("Date", t.Date), ("RefNumber", t.Number), ("Name", t.Name), ("Memo", t.Memo)]);
                    else if (t.Id != "T3")
                        cells.Add(("TxnID", t.Id));
                    data.Add(new XElement("DataRow", new XAttribute("rowNumber", ++row),
                        cells.Where(c => c.Item2 is not null).Select(c => new XElement("ColData", new XAttribute("colID", Col(c.Item1)), new XAttribute("value", c.Item2!)))));
                }

                data.Add(new XElement("SubtotalRow", new XAttribute("rowNumber", ++row),
                    new XElement("ColData", new XAttribute("colID", Col("Debit")), new XAttribute("value", Amount(t.Lines.Sum(l => l.Debit)) ?? "0.00"))));
            }

            report.Add(data);
            return report;
        }

        private static string? Amount(decimal value) => value == 0m ? null : value.ToString("0.00", CultureInfo.InvariantCulture);

        public void Dispose()
        {
        }
    }
}
