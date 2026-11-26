using System.Text;
using Microsoft.Extensions.DependencyInjection;
using OpenBook.Application.Accounts;
using OpenBook.Application.Attachments;
using OpenBook.Application.Documents;
using OpenBook.Application.Settlements;
using OpenBook.Domain.Accounts;
using OpenBook.Domain.Common;
using OpenBook.Domain.Documents;
using OpenBook.Domain.Settlements;
using OpenBook.Infrastructure.Documents;
using UglyToad.PdfPig;
using static OpenBook.Domain.Settlements.SettlementSection;

namespace OpenBook.Integration.Tests;

/// <summary>Settlements read from a statement's pages, by Claude or on this server; the statements are made up.</summary>
public sealed class SettlementCaptureTests : IAsyncLifetime
{
    private static readonly DateOnly Friday = new(2026, 3, 13);

    private readonly LedgerDatabase _db = new();
    private Carrier _carrier = null!;

    // Printed the way carriers do: columns under headings, amounts to the right, a discount with its minus after it.
    internal const string Statement = """
         SETTLEMENTS DATE: 26/03/13                   EXAMPLE LINES
                                   REVENUE SECTION
                                               TRIP   TRIP
            ORDER  DSP   DATE                 LOADED  EMPTY     TRIP    OWNER   REC
            NUMBER  #  SQ MMDD DESCRIPTION     MILES  MILES   REVENUE  REVENUE  RPM
         TG             1 0312 Training 1200 M                          112.30
         FS 8800001 01  1 0309 FUEL SURCG FLAT                          400.00
         RV 8800001 01  1 0309 TRIP REVENUE      600    100  2,000.00 1,440.00 + TEAM
            Oakville FL L Riverton NY                                           1.640
         RV 8800002 02  1 0311 TRIP REVENUE      300         1,000.00   720.00 + SOLO
                              TOTAL REC. REVENUE  900    100          2,672.30 *
                                   REIMBURSEMENT SECTION
            ORDER  DSP   DATE                            CURRENT    EXPENSE
            NUMBER  #  SQ MMDD DESCRIPTION               BALANCE REIMBURSED
         RP             1 0312 REPAIRS 12345/67890           .00     300.00
                                               TOTAL REIMBURSED:                300.00 *
                                   DEDUCTION SECTION
            ORDER  DSP   DATE                            CURRENT    CHARGES   FUEL
            NUMBER  #  SQ MMDD DESCRIPTION               BALANCE   DEDUCTED  DISCNT
         EZ             1 0310 TOLLS                         .00        .35
         NP             1 1103 TRUCK PAYMENT                 .00     915.00
         NE             2 0303 DEFERRED PAYMENT           612.40      30.00
         UF 8800001 01  1 0310 TRACTOR FUEL 100.5 GALS.      .00     500.00   80.00
         UD         01  1 0312 TRAC FUEL DSCNT               .00      80.00-
                                        TOTAL DEDUCTIONS FROM TRUCK           1,365.35 *
                                   WAGE EXPENSE
            NUMBER  #  SQ MMDD DESCRIPTION               BALANCE   DEDUCTED
         WD             1 0312 WAGES PAID FOR SOMEONE        .00     200.00
                                       SUBTOTAL PRIOR WAGE D:                 1,606.95 **
                                          TOTAL WAGE EXPENSE:                   200.00 *
                                   ACCOUNTS
         OWNER BALANCE FORWARD          .00
         DIRECT DEPOSIT TO BANK                                               1,406.95
        """;

    public async Task InitializeAsync()
    {
        await _db.InitializeAsync();
        var checking = await _db.CreateAccount("Checking", AccountType.Bank, "1000");
        var income = await _db.CreateAccount("Freight", AccountType.Income);
        _carrier = await In<SettlementService, Carrier>(s => s.CreateCarrierAsync(new CarrierDetails("Example Lines", checking.Id)));
        // Every code the statement has, to one account: where they go is tested elsewhere.
        await In<SettlementService, Carrier>(s => s.SetCodesAsync(_carrier.Id,
            [.. new[] { (Revenue, "TG"), (Revenue, "FS"), (Revenue, "RV"), (Reimbursement, "RP"), (Deduction, "EZ"), (Deduction, "NP"), (Deduction, "NE"), (Deduction, "UF"), (Deduction, "UD"), (Wage, "WD") }
                .Select(c => new CarrierCodeDraft(c.Item1, c.Item2, income.Id))]));
    }

    public Task DisposeAsync() => _db.DisposeAsync();

    private Task<T> In<TService, T>(Func<TService, Task<T>> work) where TService : notnull => _db.InScope(sp => work(sp.GetRequiredService<TService>()));

    private Task<Settlement> Read(byte[] content, IReadOnlyCollection<int> pages, SettlementReadWith with, string fileName = "week.pdf") =>
        In<SettlementCaptureService, Settlement>(s => s.ReadAsync(_carrier.Id, fileName, content, pages, with, Friday));

    private static async Task<string> Refused(Func<Task> work) => (await Assert.ThrowsAnyAsync<DomainException>(work)).Code;

    private Task<DocumentReadingSettings> UseClaude() => In<DocumentCaptureService, DocumentReadingSettings>(s => s.SaveSettingsAsync(new DocumentReadingSettingsCommand(true, null, "sk-test")));

    private static string TextOf(byte[] pdf)
    {
        using var document = PdfDocument.Open(pdf);
        return string.Join('\n', document.GetPages().Select(p => string.Join(' ', p.GetWords().Select(w => w.Text))));
    }

    private static SettlementReading Answer(string? number = "W10") => new(number, new DateOnly(2026, 3, 12), null, null, 300m, null,
        [new(Revenue, "RV", 1000m, Friday, "L1", "01", "Trip revenue", LoadedMiles: 500m), new(Deduction, "NP", -700m, Description: "Truck payment")], [new(Deduction, -700m)]);

    [Fact]
    public async Task Claude_gets_and_the_settlement_keeps_only_the_pages_chosen()
    {
        await UseClaude();
        _db.Claude.Settlement = Answer();
        var file = FakeStatementPages.Pdf("THE SETTLEMENT", "MORE OF IT", "SOMEONE ELSES PAY STUB", "ROUTING NUMBER");
        var pages = await In<SettlementCaptureService, IReadOnlyList<StatementPage>>(s => Task.FromResult(s.Inspect("week.pdf", file)));
        Assert.Equal(["THE SETTLEMENT", "MORE OF IT", "SOMEONE ELSES PAY STUB", "ROUTING NUMBER"], pages.Select(p => p.Start!));

        var settlement = await Read(file, [2, 1], SettlementReadWith.Claude);
        Assert.Equal("THE SETTLEMENT\nMORE OF IT", TextOf(Assert.Single(_db.Claude.Statements)));
        var kept = Assert.Single(await In<AttachmentService, IReadOnlyList<Domain.Attachments.Attachment>>(s => s.ListAsync("settlement", settlement.Id)));
        Assert.Equal("statement pages 1-2.pdf", kept.FileName);
        Assert.Equal("THE SETTLEMENT\nMORE OF IT", TextOf((await In<AttachmentService, byte[]?>(s => s.GetContentAsync(kept.Id)))!));

        Assert.Equal(("W10", new DateOnly(2026, 3, 12), 300m, SettlementReadMethods.Claude, SettlementStatus.Waiting), (settlement.Number, settlement.Date, settlement.NetPaid, settlement.ReadMethod, settlement.Status));
        Assert.Equal((2, 500m, 0m, -700m), (settlement.Lines.Count, settlement.LoadedMiles, settlement.Difference, settlement.StatedTotals.Single().Amount));

        // The same file again is said so, under a number of its own.
        var again = await Read(file, [1], SettlementReadWith.Claude);
        Assert.Equal("W10 (2)", again.Number);
        Assert.Contains("read before", again.ReadNote, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Nothing_is_sent_or_kept_when_it_should_not_be()
    {
        var file = FakeStatementPages.Pdf("ONE", "TWO");
        Assert.Equal("settlement.claude_off", await Refused(() => Read(file, [1], SettlementReadWith.Claude)));
        await UseClaude();
        _db.Claude.Settlement = Answer();
        Assert.Equal("settlement.no_pages", await Refused(() => Read(file, [], SettlementReadWith.Claude)));
        Assert.Equal("settlement.page_missing", await Refused(() => Read(file, [3], SettlementReadWith.Claude)));
        Assert.Equal("settlement.file_unreadable", await Refused(() => Read(Encoding.UTF8.GetBytes("not a pdf"), [1], SettlementReadWith.Claude)));
        Assert.Equal("settlement.file_type", await Refused(() => Read(file, [1], SettlementReadWith.Claude, "week.docx")));
        Assert.Empty(_db.Claude.Statements);

        _db.Claude.Failure = "Claude declined to read this document";
        Assert.Equal("settlement.read_failed", await Refused(() => Read(file, [1], SettlementReadWith.Claude)));
        _db.Claude.Failure = null;
        _db.Claude.Settlement = Answer() with { Lines = [] };
        Assert.Equal("settlement.file_no_lines", await Refused(() => Read(file, [1], SettlementReadWith.Claude)));
        Assert.Empty(await In<SettlementService, IReadOnlyList<Settlement>>(s => s.ListAsync()));
    }

    [Fact]
    public async Task What_claude_answers_is_made_to_fit()
    {
        await UseClaude();
        _db.Claude.Settlement = new SettlementReading(new string('9', 300), null, new DateOnly(2026, 3, 13), new DateOnly(2026, 3, 1), -25m, null,
            [new(Revenue, "RV", 10m, LoadedMiles: -4m), new(Revenue, null, 5m), new(Deduction, "NP", -1.005m), new(Deduction, "XX", -35m, Gallons: 9_999_999m)]);
        var settlement = await Read(FakeStatementPages.Pdf("ONE"), [1], SettlementReadWith.Claude);
        Assert.Equal((50, Friday, 0m, -25m, 0m), (settlement.Number.Length, settlement.Date, settlement.NetPaid, settlement.CarriedForward, settlement.Difference));
        Assert.Null(settlement.PeriodStart);
        Assert.Equal(["RV", "XX"], settlement.Lines.Select(l => l.Code));
        Assert.Contains("2 line(s)", settlement.ReadNote, StringComparison.Ordinal);
    }

    [Fact]
    public void Claudes_answer_is_read_from_its_json()
    {
        var read = ClaudeSettlementReader.Parse("""
            {"number":"A1","pay_date":"2026-03-13","period_start":null,"period_end":null,"net_paid":905.00,"carried_forward":null,
             "totals":{"revenue":1000.00,"reimbursement":null,"deduction":90.00,"wage":null},
             "lines":[{"section":"revenue","code":"RV","date":"2026-03-09","load":"1","dispatch":"01","description":"Trip","amount":1000.00,"loaded_miles":500,"empty_miles":20.5,"gallons":null},
                      {"section":"deduction","code":"UF","date":null,"load":null,"dispatch":null,"description":"Fuel","amount":100.00,"loaded_miles":null,"empty_miles":null,"gallons":40.25},
                      {"section":"deduction","code":"UD","date":null,"load":null,"dispatch":null,"description":"Discount","amount":-10.00,"loaded_miles":null,"empty_miles":null,"gallons":null},
                      {"section":"other","code":"TR","date":null,"load":null,"dispatch":null,"description":"Tires","amount":-5.00,"loaded_miles":null,"empty_miles":null,"gallons":null},
                      {"section":"nonsense","code":"ZZ","amount":1}]}
            """);
        Assert.Equal([1000m, -100m, 10m, -5m], read.Lines.Select(l => l.Amount));
        Assert.Equal((905m, 905m), (read.NetPaid, read.Lines.Sum(l => l.Amount)));
        Assert.Equal([(Revenue, 1000m), (Deduction, -90m)], read.StatedTotals!.Select(t => (t.Section, t.Amount)));
        Assert.Throws<DocumentReadException>(() => ClaudeSettlementReader.Parse("[]"));
        Assert.Throws<DocumentReadException>(() => ClaudeSettlementReader.Parse("{"));

        var missing = ClaudeSettlementReader.Parse("""{"totals":{},"lines":[{"section":"revenue","amount":12.50},{"section":"deduction"}]}""");
        Assert.Equal([12.50m], missing.Lines.Select(l => l.Amount));
        Assert.Null(missing.NetPaid);
    }

    // Anthropic refuses a schema with more than 16 nullable (union) fields or 24 optional ones; a live read found the first.
    [Fact]
    public void The_schemas_sent_to_Claude_stay_within_its_limits()
    {
        foreach (var schema in new[] { ClaudeSettlementReader.Schema(), ClaudeDocumentReader.Schema() })
        {
            var root = System.Text.Json.JsonSerializer.SerializeToElement(schema);
            var (unions, optional) = Count(root);
            Assert.True(unions <= 16, $"{unions} union fields");
            Assert.True(optional <= 24, $"{optional} optional fields");
        }

        static (int Unions, int Optional) Count(System.Text.Json.JsonElement e)
        {
            var (unions, optional) = (0, 0);
            if (e.ValueKind == System.Text.Json.JsonValueKind.Object)
            {
                if (e.TryGetProperty("type", out var type) && type.ValueKind == System.Text.Json.JsonValueKind.Array || e.TryGetProperty("anyOf", out _))
                    unions++;
                if (e.TryGetProperty("properties", out var properties))
                {
                    var required = e.TryGetProperty("required", out var r) ? r.EnumerateArray().Select(x => x.GetString()).ToHashSet() : [];
                    optional += properties.EnumerateObject().Count(p => !required.Contains(p.Name));
                }

                foreach (var p in e.EnumerateObject())
                {
                    var (u, o) = Count(p.Value);
                    (unions, optional) = (unions + u, optional + o);
                }
            }

            return (unions, optional);
        }
    }

    [Fact]
    public async Task A_pdf_with_text_is_read_exactly_here()
    {
        var settlement = await Read(FakeStatementPages.Pdf(Statement, "PAY STUB"), [1], SettlementReadWith.ThisComputer);
        Assert.Equal(SettlementReadMethods.Text, settlement.ReadMethod);
        Assert.Equal(
            [("TG", 112.30m), ("FS", 400m), ("RV", 1440m), ("RV", 720m), ("RP", 300m), ("EZ", -0.35m), ("NP", -915m), ("NE", -30m), ("UF", -500m), ("UD", 80m), ("WD", -200m)],
            settlement.Lines.Select(l => (l.Code, l.Amount)));
        Assert.All(settlement.Lines, l => Assert.Null(l.Doubt));
        Assert.Equal((1406.95m, 0m, 900m, 100m, 100.5m), (settlement.NetPaid, settlement.Difference, settlement.LoadedMiles, settlement.EmptyMiles, settlement.Gallons));
        var trip = settlement.Lines.First(l => l.Code == "RV");
        Assert.Equal(("8800001", "01", new DateOnly(2026, 3, 9), "TRIP REVENUE"), (trip.Load, trip.Dispatch, trip.Date!.Value, trip.Description));
        Assert.Equal(new DateOnly(2025, 11, 3), settlement.Lines.Single(l => l.Code == "NP").Date);
        Assert.Equal("Training 1200 M", settlement.Lines[0].Description);
        Assert.Equal([(Revenue, 2672.30m), (Reimbursement, 300m), (Deduction, -1365.35m), (Wage, -200m)], settlement.StatedTotals.Select(t => (t.Section, t.Amount)));

        // Exact, so nothing has to be confirmed first.
        await In<SettlementService, Settlement>(s => s.PostAsync(settlement.Id));
    }

    [Fact]
    public async Task A_picture_needs_an_ocr_program()
    {
        var file = FakeStatementPages.Pdf(null, "TEXT");
        var pages = await In<SettlementCaptureService, IReadOnlyList<StatementPage>>(s => Task.FromResult(s.Inspect("week.pdf", file)));
        Assert.Null(pages[0].Start);
        Assert.Equal([0x89, 0x50, 0x4E, 0x47], pages[0].Preview![..4]);
        Assert.Equal("settlement.cannot_read_here", await Refused(() => Read(file, [1], SettlementReadWith.ThisComputer)));
        Assert.Equal("settlement.cannot_read_here", await Refused(() => Read([1, 2, 3], [1], SettlementReadWith.ThisComputer, "photo.jpg")));
    }

    /// <summary>The statement as a scan, some of it misread.</summary>
    private static List<StatementWord> Misread(params (string Was, string Read)[] mistakes)
    {
        var text = Statement;
        foreach (var (was, read) in mistakes)
        {
            Assert.Contains(was, text, StringComparison.Ordinal);
            text = text.Replace(was, read, StringComparison.Ordinal);
        }

        return FakeStatementPages.Typed(text);
    }

    private async Task<Settlement> Scan(params (string Was, string Read)[] mistakes)
    {
        _db.Statement.Scanned = Misread(mistakes);
        return await Read(FakeStatementPages.Pdf((string?)null), [1], SettlementReadWith.ThisComputer);
    }

    private static string? DoubtOf(Settlement s, string code) => s.Lines.Last(l => l.Code == code).Doubt;

    [Fact]
    public async Task A_scan_waits_for_a_person_to_check_it_and_type_the_net()
    {
        var settlement = await Scan(("  915.00", "  915.00~"));
        Assert.True(settlement.IsScanned);
        // The net printed on it isn't taken.
        Assert.Equal(0m, settlement.NetPaid);
        Assert.Equal(["NP"], settlement.Lines.Where(l => l.NeedsChecking).Select(l => l.Code));
        var line = settlement.Lines.Single(l => l.Code == "NP").LineNumber;

        Task<Settlement> Confirm(decimal net, int[] lines, bool all) => In<SettlementService, Settlement>(s => s.ConfirmAsync(settlement.Id, net, lines, all));
        Assert.Equal("settlement.not_checked", await Refused(() => In<SettlementService, Settlement>(s => s.PostAsync(settlement.Id))));
        Assert.Equal("settlement.still_doubtful", await Refused(() => Confirm(1406.95m, [], true)));
        Assert.Equal("settlement.not_all_checked", await Refused(() => Confirm(1406.95m, [line], false)));

        // A net that isn't the lines' is allowed to be typed, and stops it posting.
        await Confirm(1406.96m, [line], true);
        Assert.Equal("settlement.unbalanced", await Refused(() => In<SettlementService, Settlement>(s => s.PostAsync(settlement.Id))));
        var confirmed = await Confirm(1406.95m, [], true);
        Assert.NotNull(confirmed.CheckedBy);
        Assert.Equal(SettlementStatus.Posted, (await In<SettlementService, Settlement>(s => s.PostAsync(settlement.Id))).Status);
    }

    [Fact]
    public async Task Changing_a_scan_asks_for_it_to_be_checked_again_and_keeps_what_was_pointed_out()
    {
        var settlement = await Scan(("  915.00", "  916.00~"), ("     30.00", "     30.00~"));
        // The wrong digit makes the whole part suspect.
        Assert.Equal(SettlementTextParser.NotAdding, DoubtOf(settlement, "UF"));
        await In<SettlementService, Settlement>(s => s.ConfirmAsync(settlement.Id, 1406.95m, [.. settlement.Lines.Where(l => l.NeedsChecking).Select(l => l.LineNumber)], true));

        // The wrong line is put right; the others keep what was said of them, checked already.
        var changed = await In<SettlementService, Settlement>(s => s.ChangeAsync(settlement.Id, new SettlementDetails(settlement.Number, settlement.Date, 1406.95m,
            [.. settlement.Lines.Select(l => new SettlementLineDraft(l.Section, l.Code, l.Code == "NP" ? -915m : l.Amount, l.Date, l.Load, l.Dispatch, l.Description, LoadedMiles: l.LoadedMiles, EmptyMiles: l.EmptyMiles, Gallons: l.Gallons))])));
        Assert.Null(changed.CheckedBy);
        Assert.Null(DoubtOf(changed, "NP"));
        Assert.NotNull(DoubtOf(changed, "NE"));
        Assert.Equal("settlement.not_checked", await Refused(() => In<SettlementService, Settlement>(s => s.PostAsync(settlement.Id))));
        await In<SettlementService, Settlement>(s => s.ConfirmAsync(settlement.Id, 1406.95m, [], true));
        await In<SettlementService, Settlement>(s => s.PostAsync(settlement.Id));
    }

    [Fact]
    public async Task Mistakes_a_scan_makes_are_pointed_out()
    {
        // A wrong digit, read surely: only the part's total gives it away, so all of the part is suspect.
        var digit = await Scan(("  915.00", "  916.00"));
        Assert.All(digit.Lines.Where(l => l.Section == Deduction), l => Assert.Equal(SettlementTextParser.NotAdding, l.Doubt));
        Assert.All(digit.Lines.Where(l => l.Section == Revenue), l => Assert.Null(l.Doubt));

        // A lost decimal point leaves no amount, and the line is kept to be filled in.
        var point = await Scan(("  915.00", "   91500"));
        Assert.Equal((0m, SettlementTextParser.NoAmount), (point.Lines.Single(l => l.Code == "NP").Amount, DoubtOf(point, "NP")));

        // A lost minus sign turns the discount into a charge: the part doesn't add up.
        var minus = await Scan(("     80.00-", "     80.00 "));
        Assert.Equal((-80m, SettlementTextParser.NotAdding), (minus.Lines.Single(l => l.Code == "UD").Amount, DoubtOf(minus, "UD")));

        // A minus read as a word of its own still counts.
        Assert.Equal(80m, (await Scan(("     80.00-", "    80.00 -"))).Lines.Single(l => l.Code == "UD").Amount);

        // Letters for digits are put back, and said to be guessed at.
        var letters = await Scan(("   500.00   80.00", "   5OO.OO   80.00"));
        Assert.Equal((-500m, SettlementTextParser.GuessedAmount), (letters.Lines.Single(l => l.Code == "UF").Amount, DoubtOf(letters, "UF")));

        // Two mistakes that cancel add up, so nothing is pointed out: which is why everything has to be checked.
        var cancel = await Scan(("  915.00", "  916.00"), ("     30.00", "     29.00"));
        Assert.DoesNotContain(cancel.Lines, l => l.NeedsChecking);
        Assert.Equal("settlement.not_checked", await Refused(() => In<SettlementService, Settlement>(s => s.PostAsync(cancel.Id))));

        // No printed total: the part can't be checked against anything.
        var total = await Scan(("TOTAL REIMBURSED:                300.00 *", "                                       "));
        Assert.Equal(SettlementTextParser.NoTotal, DoubtOf(total, "RP"));
    }

    [Fact]
    public void The_ocr_programs_table_of_words_is_read()
    {
        var words = StatementPages.ReadTsv("level\tpage_num\tblock\tpar\tline\tword\tleft\ttop\twidth\theight\tconf\ttext\n1\t1\t0\t0\t0\t0\t0\t0\t2000\t2589\t-1\t\n5\t1\t1\t1\t1\t1\t21\t24\t30\t12\t91.5\tRV\n5\t1\t1\t1\t1\t2\t60\t24\t30\t12\t-1\t \n5\tbroken\n", 3);
        Assert.Equal(new StatementWord(3, "RV", 91.5, 21, 24, 30, 12), Assert.Single(words));
    }

    [Fact]
    public void Pages_are_written_the_way_people_write_them()
    {
        Assert.Equal("1-4, 7", SettlementCaptureService.PageList([1, 2, 3, 4, 7]));
        Assert.Equal([1, 2, 3, 4, 7], SettlementCaptureService.ParsePages("7, 1-4"));
        Assert.Empty(SettlementCaptureService.ParsePages(""));
        foreach (var bad in new[] { "a", "4-1", "0", "1-2-3", "1-999999999999" })
            Assert.Equal("settlement.pages_unreadable", Assert.Throws<DomainException>(() => SettlementCaptureService.ParsePages(bad)).Code);
    }
}
