using System.Globalization;
using System.Text.RegularExpressions;
using OpenBook.Application.Import;
using OpenBook.Domain.Common;
using OpenBook.Domain.Settlements;

namespace OpenBook.Application.Settlements;

/// <summary>
/// Finds a statement's lines among the words of its pages, by where they stand: parts by their headings, amounts under
/// their column's heading, miles under theirs. Words read from a picture are often wrong, so a line says why it needs
/// checking (<see cref="SettlementLineDraft.Doubt"/>) whenever a word wasn't sure, an amount had to be guessed at, or its
/// part doesn't add up to the total printed under it. What it can't place it leaves out rather than inventing.
/// </summary>
public static partial class SettlementTextParser
{
    /// <summary>A word surer than this still turns out wrong at times; one less sure is always pointed out.</summary>
    public const double SureEnough = 92;

    public const string NotAdding = "This part's lines don't add up to the total printed under it.";
    public const string NoTotal = "No printed total could be read to check this part against.";
    public const string NoAmount = "No amount could be read.";
    public const string GuessedAmount = "The amount was hard to read.";
    public const string SeveralAmounts = "There's more than one amount on the line.";
    public const string NotSure = "Some of this line was hard to read.";

    private const int MaxWords = 50_000;

    /// <summary>How far from its heading's right edge, in row heights, a number of miles can end.</summary>
    private const double MilesReach = 1.6;

    private sealed record Columns(double? Amount, double? Loaded, double? Empty);

    private sealed record Row(int Page, List<StatementWord> Words)
    {
        public string Upper { get; } = string.Join(' ', Words.Select(w => w.Text)).ToUpperInvariant();
        public double Height => Words.Average(w => w.Height);
    }

    public static SettlementReading Parse(StatementWords statement, DateOnly reference)
    {
        ArgumentNullException.ThrowIfNull(statement);
        var lines = new List<SettlementLineDraft>();
        var totals = new Dictionary<SettlementSection, decimal>();
        decimal? net = null;
        SettlementSection? section = null;
        var columns = new Columns(null, null, null);
        var headings = new List<StatementWord>();

        foreach (var row in Rows(statement.Words))
        {
            var money = row.Words.Where(w => MoneyOf(w.Text) is not null).ToList();
            if (money.Count == 0 && HeadingOf(row) is { } heading)
            {
                (section, columns, headings) = (heading.Section, new Columns(null, null, null), []);
                continue;
            }

            if (money.Count == 0 && IsColumnHeadings(row))
            {
                // A heading can take two or three rows; the first ones start over.
                if (headings.Count > 0 && row.Words[0].Page != headings[^1].Page)
                    headings = [];
                headings.AddRange(row.Words);
                columns = ColumnsOf(section, headings);
                continue;
            }

            if (NetWords().IsMatch(row.Upper) && money.Count > 0)
                net = MoneyOf(money[^1].Text);
            if (row.Upper.Contains("TOTAL", StringComparison.Ordinal))
            {
                if (section is { } of && money.Count > 0 && !row.Upper.Contains("SUBTOTAL", StringComparison.Ordinal))
                    totals[of] = MoneyOf(money[^1].Text)!.Value;
                continue;
            }

            if (section is { } inside && LineOf(row, inside, columns, reference, statement.FromPicture) is { } line)
                lines.Add(line);
        }

        // A part that doesn't add up has at least one wrong line, and nothing says which.
        foreach (var part in lines.Select(l => l.Section).Distinct().ToList())
        {
            var sum = lines.Where(l => l.Section == part).Sum(l => l.Amount);
            var doubt = !totals.TryGetValue(part, out var printed) ? (statement.FromPicture ? NoTotal : null)
                : SettlementSections.Counted(part, printed) != sum ? NotAdding : null;
            if (doubt is null)
                continue;
            for (var i = 0; i < lines.Count; i++)
            {
                if (lines[i].Section == part)
                    lines[i] = lines[i] with { Doubt = lines[i].Doubt ?? doubt };
            }
        }

        return new SettlementReading(null, null, null, null, net, null, lines,
            [.. totals.Where(t => Math.Abs(t.Value) <= Money.MaxAmount).Select(t => new SettlementTotalDraft(t.Key, SettlementSections.Counted(t.Key, t.Value)))],
            ["Only lines in parts headed revenue, reimbursements, deductions or wages were read: add reserves by the mile, interest and balances brought forward under Other."]);
    }

    /// <summary>The words in rows, top to bottom and left to right.</summary>
    private static IEnumerable<Row> Rows(IReadOnlyList<StatementWord> words)
    {
        foreach (var page in words.Where(w => w.Text.Trim().Length > 0 && double.IsFinite(w.Left) && double.IsFinite(w.Top) && double.IsFinite(w.Width) && w.Height > 0 && double.IsFinite(w.Height))
                     .Take(MaxWords).GroupBy(w => w.Page).OrderBy(g => g.Key))
        {
            var row = new List<StatementWord>();
            var middle = 0d;
            foreach (var word in page.OrderBy(w => w.Middle))
            {
                if (row.Count > 0 && word.Middle - middle > Math.Max(row.Average(w => w.Height), word.Height) * 0.5)
                {
                    yield return new Row(page.Key, [.. row.OrderBy(w => w.Left)]);
                    row = [];
                }

                row.Add(word);
                middle = row.Average(w => w.Middle);
            }

            if (row.Count > 0)
                yield return new Row(page.Key, [.. row.OrderBy(w => w.Left)]);
        }
    }

    /// <summary>A part's heading; a heading of a part that isn't read (a recap) ends the one before.</summary>
    private static (SettlementSection? Section, bool _)? HeadingOf(Row row)
    {
        var text = NotLetters().Replace(row.Upper, " ").Trim();
        var count = text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
        if (count is 0 or > 3)
            return null;
        return text switch
        {
            _ when text.StartsWith("REVENUE", StringComparison.Ordinal) || text.StartsWith("EARNING", StringComparison.Ordinal) || text.StartsWith("LOAD PAY", StringComparison.Ordinal) => (SettlementSection.Revenue, true),
            _ when text.StartsWith("REIMBURSEMENT", StringComparison.Ordinal) => (SettlementSection.Reimbursement, true),
            _ when text.StartsWith("DEDUCTION", StringComparison.Ordinal) => (SettlementSection.Deduction, true),
            _ when text.StartsWith("WAGE EXPENSE", StringComparison.Ordinal) || text is "WAGES" or "WAGE SECTION" => (SettlementSection.Wage, true),
            _ when text is "ACCOUNTS" or "INTEREST" or "SETTLEMENT RECAP" or "RECAP" or "SUMMARY" or "SETTLEMENT SUMMARY" or "OPERATING STATEMENT" or "BALANCE SHEET ITEMS" => (null, true),
            _ => null,
        };
    }

    private static readonly HashSet<string> HeadingWords = new(StringComparer.Ordinal)
    {
        "ORDER", "NUMBER", "DATE", "MMDD", "DESCRIPTION", "MILES", "LOADED", "EMPTY", "TRIP", "TOTAL", "REVENUE", "OWNER", "RATE", "AMOUNT", "CURRENT", "BALANCE", "CHARGES", "DEDUCTED",
        "EXPENSE", "REIMBURSED", "DISCNT", "DISCOUNT", "FUEL", "CODE", "LOAD", "DSP", "DISPATCH", "PAY", "GALLONS", "SQ", "RPM",
    };

    private static bool IsColumnHeadings(Row row)
    {
        var known = row.Words.Count(w => HeadingWords.Contains(NotLetters().Replace(w.Text.ToUpperInvariant(), "")));
        return known >= 2 && known * 2 >= row.Words.Count;
    }

    private static Columns ColumnsOf(SettlementSection? section, List<StatementWord> headings)
    {
        double? Right(string word) => headings.LastOrDefault(w => NotLetters().Replace(w.Text.ToUpperInvariant(), "") == word)?.Right;
        var amount = section switch
        {
            SettlementSection.Revenue => Right("OWNER") ?? Right("AMOUNT") ?? Right("PAY") ?? Right("REVENUE"),
            SettlementSection.Reimbursement => Right("REIMBURSED") ?? Right("EXPENSE") ?? Right("AMOUNT"),
            _ => Right("DEDUCTED") ?? Right("CHARGES") ?? Right("AMOUNT"),
        };
        return new Columns(amount, Right("LOADED"), Right("EMPTY"));
    }

    private static SettlementLineDraft? LineOf(Row row, SettlementSection section, Columns columns, DateOnly reference, bool scanned)
    {
        var words = row.Words;
        var code = Trimmed(words[0].Text).ToUpperInvariant();
        if (words.Count < 2 || !CodeShape().IsMatch(code))
            return null;

        // Amounts: what's surely one first; on a scan, what might be one with letters read for digits.
        var sure = words.Skip(1).Where(w => MoneyOf(w.Text) is not null).ToList();
        var guessed = scanned ? words.Skip(1).Where(w => MoneyOf(w.Text) is null && MoneyOf(AsDigits(w.Text)) is not null && MoneyShape().IsMatch(AsDigits(w.Text))).ToList() : [];
        var candidates = sure.Concat(guessed).ToList();
        StatementWord? amountWord = null;
        string? doubt = null;
        if (columns.Amount is { } right && candidates.Count > 0)
        {
            var nearest = candidates.MinBy(w => Math.Abs(w.Right - right))!;
            // Under its heading, give or take a few characters.
            if (Math.Abs(nearest.Right - right) <= row.Height * 4)
                amountWord = nearest;
        }

        if (amountWord is null && candidates.Count > 0 && columns.Amount is null)
        {
            amountWord = candidates[^1];
            if (candidates.Count > 1)
                doubt = SeveralAmounts;
        }

        // Without an amount it's a line only if it starts like one; a place name or a note doesn't.
        var rest = words.Skip(1).TakeWhile(w => w != amountWord && !candidates.Contains(w)).ToList();
        // A whole number just before another amount is a column of its own (the load's miles), not part of the description.
        if (rest.Count > 0 && words.Skip(1 + rest.Count).FirstOrDefault() is { } next && next != amountWord && Miles().IsMatch(rest[^1].Text))
            rest.RemoveAt(rest.Count - 1);
        // ".00" read as letters: a balance of nothing printed beside the amount.
        if (scanned)
            rest.RemoveAll(w => Nothing().IsMatch(AsDigits(w.Text)));
        var (load, dispatch, date, description) = Details(rest, section, columns, reference, scanned, row.Height, out var guessedDetail);
        if (amountWord is null && date is null && load is null)
            return null;

        var amount = 0m;
        if (amountWord is null)
        {
            doubt = NoAmount;
        }
        else
        {
            var text = amountWord.Text;
            if (guessed.Contains(amountWord))
            {
                text = AsDigits(text);
                doubt ??= GuessedAmount;
            }

            amount = MoneyOf(text) ?? 0m;
            // A minus printed after the amount can be read as a word of its own.
            var after = words.SkipWhile(w => w != amountWord).Skip(1).FirstOrDefault();
            if (after is { Text: "-" } && after.Left - amountWord.Right < row.Height)
                amount = -Math.Abs(amount);
            if (scanned && (amountWord.Confidence < SureEnough || words[0].Confidence < SureEnough || guessedDetail))
                doubt ??= amountWord.Confidence < SureEnough ? GuessedAmount : NotSure;
        }

        var (loaded, empty) = section == SettlementSection.Revenue ? (Under(columns.Loaded), Under(columns.Empty)) : (null, null);
        decimal? Under(double? column) => column is not { } at ? null
            : words.Skip(1).Where(w => MoneyOf(w.Text) is null && Miles().IsMatch(w.Text)).Where(w => Math.Abs(w.Right - at) <= row.Height * MilesReach).Select(w => SettlementReader.Quantity(w.Text)).FirstOrDefault();
        var gallons = Gallons().Match(row.Upper) is { Success: true } g ? SettlementReader.Quantity(g.Groups[1].Value) : null;
        return new SettlementLineDraft(section, code, SettlementSections.Counted(section, amount), date, load, dispatch, description, LoadedMiles: loaded, EmptyMiles: empty, Gallons: gallons, Doubt: doubt);
    }

    private static (string? Load, string? Dispatch, DateOnly? Date, string? Description) Details(
        List<StatementWord> words, SettlementSection section, Columns columns, DateOnly reference, bool scanned, double height, out bool guessed)
    {
        guessed = false;
        string? load = null, dispatch = null;
        DateOnly? date = null;
        var at = 0;
        string Digits(string text) => scanned ? AsDigits(text) : text;
        if (at < words.Count && LoadShape().IsMatch(Digits(words[at].Text)))
        {
            load = Digits(words[at].Text);
            guessed |= load != words[at].Text;
            at++;
        }

        // A dispatch number and a sequence number, then the date as a month and day.
        for (var skipped = 0; at < words.Count && skipped < 3; skipped++)
        {
            var text = Digits(words[at].Text);
            if (MonthDay().IsMatch(text) && SettlementReader.ParseDate(text, DateOrder.MonthFirst, reference) is { } d)
            {
                date = d;
                guessed |= text != words[at].Text;
                at++;
                break;
            }

            // On a scan "01" can come out as three characters.
            if (scanned && text.Length == 3 && text.StartsWith("00", StringComparison.Ordinal))
            {
                text = text[1..];
                guessed = true;
            }

            if (!ShortNumber().IsMatch(text))
                break;
            if (text.Length == 2 && load is not null)
                dispatch = text;
            at++;
        }

        // Miles under their headings aren't part of what the line says.
        bool IsMiles(StatementWord w) => section == SettlementSection.Revenue && Miles().IsMatch(w.Text)
            && new[] { columns.Loaded, columns.Empty }.OfType<double>().Any(c => Math.Abs(w.Right - c) <= height * MilesReach);
        var description = words.Skip(at).Where(w => !IsMiles(w)).Select(w => w.Text.Trim()).ToList();
        return (load, dispatch, date, description.Count > 0 ? string.Join(' ', description) : null);
    }

    /// <summary>An amount with cents, as printed: "1,234.56", ".07", "345.60-", "(12.00)".</summary>
    private static decimal? MoneyOf(string text)
    {
        var t = text.Trim().TrimEnd('*', '+', '#', ':');
        return MoneyShape().IsMatch(t) && StatementValues.TryParseAmount(t.StartsWith('.') ? "0" + t : t.StartsWith("-.", StringComparison.Ordinal) ? "-0" + t[1..] : t, out var amount) ? amount : null;
    }

    /// <summary>Letters an OCR program reads for digits, as those digits.</summary>
    private static string AsDigits(string text) => string.Concat(text.Trim().Select(c => c switch
    {
        'O' or 'o' or 'Q' or 'D' => '0',
        'l' or 'I' or '|' or 'i' => '1',
        'Z' or 'z' => '2',
        'S' or 's' or '$' or '§' => '5',
        '?' => '7',
        'B' => '8',
        _ => c,
    }));

    private static string Trimmed(string text) => text.Trim().Trim('.', ',', ':', ';', '|', '_');

    [GeneratedRegex(@"^[(\-]?\$?[0-9]{0,3}(,?[0-9]{3})*\.[0-9]{2}\)?-?$")]
    private static partial Regex MoneyShape();

    [GeneratedRegex("^[A-Z0-9]{2,3}$")]
    private static partial Regex CodeShape();

    [GeneratedRegex("^#?[0-9]{5,12}$")]
    private static partial Regex LoadShape();

    [GeneratedRegex("^(0[1-9]|1[0-2])[0-3][0-9]$")]
    private static partial Regex MonthDay();

    [GeneratedRegex("^[-.]?0{2,3}$")]
    private static partial Regex Nothing();

    [GeneratedRegex("^[0-9]{1,2}$")]
    private static partial Regex ShortNumber();

    [GeneratedRegex("^[0-9]{1,3}(,?[0-9]{3})?$")]
    private static partial Regex Miles();

    [GeneratedRegex(@"([0-9]{1,4}\.[0-9]{1,3})\s*GAL")]
    private static partial Regex Gallons();

    [GeneratedRegex("DIRECT DEPOSIT|NET PAY|NET PAID|NET SETTLEMENT|SETTLEMENT GROSS DUE")]
    private static partial Regex NetWords();

    [GeneratedRegex("[^A-Z ]")]
    private static partial Regex NotLetters();
}
