using System.Globalization;
using System.Text;
using System.Xml.Linq;

namespace OpenBook.QuickBooksExport;

/// <param name="To">The last day of transactions to export (usually today).</param>
/// <param name="From">The first day; null goes back month by month until five years in a row have nothing.</param>
/// <param name="MaxFileBytes">OpenBook takes import files up to 20 MB; bigger books are written as several files.</param>
public sealed record ExportOptions(string OutputFolder, DateOnly To, DateOnly? From = null, long MaxFileBytes = ExportOptions.DefaultMaxFileBytes)
{
    public const long DefaultMaxFileBytes = 19_000_000;
}

/// <param name="Lines">Journal lines (debits and credits) exported.</param>
public sealed record ExportResult(string CompanyName, IReadOnlyList<string> Files, int Accounts, int Customers, int Vendors, int Items, int Lines, DateOnly? FirstDate);

/// <summary>
/// Asks QuickBooks for what OpenBook imports and writes it as qbXML response files: the company (CompanyQueryRs), every
/// account, active customers, vendors and items (paged with iterators), and the Journal report (GeneralDetailReportQueryRs)
/// one month at a time. The journal is every posting QuickBooks made, whatever the transaction type, so the history comes
/// over complete as journal entries. When it doesn't fit one file, each file repeats the company and accounts (OpenBook
/// leaves names it has alone, and needs the accounts to read the journal's account names) and the lists go in the first.
/// </summary>
public sealed class QuickBooksExporter(IQuickBooksSession session, TextWriter log)
{
    private const int PageSize = 500;
    private const int EmptyMonthsToStop = 60;
    private static readonly DateOnly Earliest = new(1980, 1, 1);
    private static readonly decimal Newest = 16.0m;

    /// <summary>The Journal report's columns OpenBook's reader uses.</summary>
    public static readonly string[] JournalColumns = ["TxnID", "TxnNumber", "TxnType", "Date", "RefNumber", "Name", "Memo", "Account", "Debit", "Credit"];

    private string _version = "";
    private int _requestId;

    public ExportResult Export(ExportOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _version = PickVersion(session.Versions);
        var iterate = Number(_version) >= 5.0m;

        var company = Query(new XElement("CompanyQueryRq"));
        var companyName = company.Descendants("CompanyName").FirstOrDefault()?.Value.Trim() is { Length: > 0 } n ? n : "QuickBooks company";
        log.WriteLine($"Company: {companyName} (qbXML {_version})");

        var accounts = Query(new XElement("AccountQueryRq", new XElement("ActiveStatus", "All")));
        var accountCount = Count(accounts, "AccountRet");
        log.WriteLine($"Accounts: {accountCount:N0}");
        var customers = Pages("CustomerQueryRq", iterate);
        var vendors = Pages("VendorQueryRq", iterate);
        var items = Pages("ItemQueryRq", iterate);
        int customerCount = customers.Sum(c => Count(c, "CustomerRet")), vendorCount = vendors.Sum(v => Count(v, "VendorRet"));
        var itemCount = items.Sum(i => i.Elements().Count(e => e.Name.LocalName.StartsWith("Item", StringComparison.Ordinal)));
        log.WriteLine($"Customers and jobs: {customerCount:N0} · Vendors: {vendorCount:N0} · Items: {itemCount:N0}");

        var header = new[] { Compact(company), Compact(accounts) };
        var lists = customers.Concat(vendors).Concat(items).Select(Compact).ToList();
        var (start, end) = Envelope();
        var budget = options.MaxFileBytes - Bytes(start) - Bytes(end) - header.Sum(Bytes);
        if (budget <= 0)
            throw new QuickBooksException("The company's accounts alone are larger than one file may be; pass a larger --max-mb.");

        var journal = Journal(options, budget, out var lines, out var firstDate);
        log.WriteLine($"Journal lines: {lines:N0}{(firstDate is { } f ? $", from {f:yyyy-MM-dd}" : "")}");

        var files = Write(options, companyName, header, Pack(lists, journal, budget));
        return new ExportResult(companyName, files, accountCount, customerCount, vendorCount, itemCount, lines, firstDate);
    }

    /// <summary>The newest version both sides know; country editions' "CA3.0" and the like are left out.</summary>
    public static string PickVersion(IEnumerable<string> versions)
    {
        var known = versions.Select(v => (text: v, number: Number(v))).Where(v => v.number is > 0m).ToList();
        if (known.Count == 0 || known.Max(v => v.number) < 3.0m)
            throw new QuickBooksException("This QuickBooks is too old for the export (it needs qbXML 3.0: QuickBooks 2004 or later).");
        var best = known.Where(v => v.number <= Newest).MaxBy(v => v.number);
        return best.text ?? Newest.ToString("0.0", CultureInfo.InvariantCulture);
    }

    private static decimal? Number(string version) =>
        decimal.TryParse(version, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var n) ? n : null;

    private List<XElement> Pages(string request, bool iterate)
    {
        var pages = new List<XElement>();
        string? iteratorId = null;
        while (true)
        {
            var rq = new XElement(request);
            if (iterate)
            {
                rq.SetAttributeValue("iterator", iteratorId is null ? "Start" : "Continue");
                rq.SetAttributeValue("iteratorID", iteratorId);
                rq.Add(new XElement("MaxReturned", PageSize));
            }

            var rs = Query(rq);
            pages.Add(rs);
            iteratorId = (string?)rs.Attribute("iteratorID");
            if (!iterate || iteratorId is null || !int.TryParse((string?)rs.Attribute("iteratorRemainingCount"), CultureInfo.InvariantCulture, out var left) || left <= 0)
                return pages;
        }
    }

    /// <summary>Months from the last back to the first, kept in date order; a month too big for one file is halved until it fits.</summary>
    private List<string> Journal(ExportOptions options, long budget, out int lines, out DateOnly? firstDate)
    {
        var floor = options.From ?? Earliest;
        var months = new List<List<(string Xml, int Lines)>>();
        lines = 0;
        firstDate = null;
        var month = new DateOnly(options.To.Year, options.To.Month, 1);
        int empty = 0, year = month.Year, yearLines = 0;
        while (month.AddMonths(1) > floor && month >= Earliest)
        {
            var from = month < floor ? floor : month;
            var monthEnd = month.AddMonths(1).AddDays(-1);
            var to = monthEnd > options.To ? options.To : monthEnd;
            var pieces = new List<(string, int)>();
            Range(from, to, budget, pieces);
            var count = pieces.Sum(p => p.Item2);
            if (month.Year != year)
            {
                if (yearLines > 0)
                    log.WriteLine($"  {year}: {yearLines:N0} lines");
                year = month.Year;
                yearLines = 0;
            }

            yearLines += count;
            if (count > 0)
            {
                months.Add(pieces);
                lines += count;
                firstDate = from;
                empty = 0;
            }
            else if (options.From is null && ++empty >= EmptyMonthsToStop)
                break;

            month = month.AddMonths(-1);
        }

        if (yearLines > 0)
            log.WriteLine($"  {year}: {yearLines:N0} lines");
        months.Reverse();
        return months.SelectMany(m => m.Select(p => p.Xml)).ToList();
    }

    private void Range(DateOnly from, DateOnly to, long budget, List<(string, int)> pieces)
    {
        var rq = new XElement("GeneralDetailReportQueryRq",
            new XElement("GeneralDetailReportType", "Journal"),
            new XElement("DisplayReport", "false"),
            new XElement("ReportPeriod",
                new XElement("FromReportDate", from.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
                new XElement("ToReportDate", to.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))),
            JournalColumns.Select(c => new XElement("IncludeColumn", c)));
        var rs = Query(rq);
        var rows = rs.Descendants("DataRow").Count();
        if (rows == 0)
            return;
        var xml = Compact(rs);
        if (Bytes(xml) > budget && from < to)
        {
            var middle = from.AddDays((to.DayNumber - from.DayNumber) / 2);
            Range(from, middle, budget, pieces);
            Range(middle.AddDays(1), to, budget, pieces);
            return;
        }

        if (Bytes(xml) > budget)
            log.WriteLine($"  Warning: {from:yyyy-MM-dd} alone is larger than one file may be; OpenBook will refuse that file.");
        pieces.Add((xml, rows));
    }

    /// <summary>Lists first, then the journal in date order, starting a new file when the next piece wouldn't fit.</summary>
    private static List<List<string>> Pack(List<string> lists, List<string> journal, long budget)
    {
        var parts = new List<List<string>>();
        var current = new List<string>(lists);
        var size = lists.Sum(Bytes);
        foreach (var piece in journal)
        {
            var bytes = Bytes(piece);
            if (current.Count > 0 && size + bytes > budget)
            {
                parts.Add(current);
                current = [];
                size = 0;
            }

            current.Add(piece);
            size += bytes;
        }

        parts.Add(current);
        return parts;
    }

    private List<string> Write(ExportOptions options, string companyName, string[] header, List<List<string>> parts)
    {
        Directory.CreateDirectory(options.OutputFolder);
        var invalid = Path.GetInvalidFileNameChars();
        var safe = new string(companyName.Select(c => invalid.Contains(c) ? '-' : c).ToArray()).Trim(' ', '.');
        var files = new List<string>();
        for (var i = 0; i < parts.Count; i++)
        {
            var name = parts.Count == 1 ? $"{safe} - for OpenBook.qbxml" : $"{safe} - for OpenBook - part {i + 1} of {parts.Count}.qbxml";
            var path = Path.Combine(options.OutputFolder, name);
            var (start, end) = Envelope();
            using (var writer = new StreamWriter(path, false, new UTF8Encoding(false)))
            {
                writer.Write(start);
                foreach (var element in header.Concat(parts[i]))
                    writer.Write(element);
                writer.Write(end);
            }

            files.Add(path);
        }

        return files;
    }

    private (string Start, string End) Envelope() =>
        ($"<?xml version=\"1.0\" encoding=\"utf-8\"?>\r\n<?qbxml version=\"{_version}\"?>\r\n<QBXML><QBXMLMsgsRs>\r\n", "</QBXMLMsgsRs></QBXML>\r\n");

    /// <summary>Sends one request and returns its …Rs element; "nothing found" is an empty answer, other errors stop the export.</summary>
    private XElement Query(XElement rq)
    {
        rq.SetAttributeValue("requestID", (++_requestId).ToString(CultureInfo.InvariantCulture));
        var message = new XDocument(new XElement("QBXML", new XElement("QBXMLMsgsRq", new XAttribute("onError", "stopOnError"), rq)));
        var text = $"<?xml version=\"1.0\" encoding=\"utf-8\"?><?qbxml version=\"{_version}\"?>{message.Root!.ToString(SaveOptions.DisableFormatting)}";
        XDocument response;
        try
        {
            response = XDocument.Parse(session.Process(text));
        }
        catch (System.Xml.XmlException ex)
        {
            throw new QuickBooksException($"QuickBooks' answer to {rq.Name} couldn't be read ({ex.Message}).");
        }

        var rs = response.Descendants().FirstOrDefault(e => e.Name.LocalName.EndsWith("Rs", StringComparison.Ordinal) && e.Name.LocalName != "QBXMLMsgsRs")
            ?? throw new QuickBooksException($"QuickBooks didn't answer {rq.Name}.");
        var code = (string?)rs.Attribute("statusCode") ?? "0";
        var severity = (string?)rs.Attribute("statusSeverity") ?? "Info";
        if (severity.Equals("Error", StringComparison.OrdinalIgnoreCase) && code != "1")
            throw new QuickBooksException($"QuickBooks refused {rq.Name}: {(string?)rs.Attribute("statusMessage")} (status {code}).");
        if (severity.Equals("Warn", StringComparison.OrdinalIgnoreCase) && code != "1")
            log.WriteLine($"  QuickBooks: {(string?)rs.Attribute("statusMessage")} (status {code})");
        rs.Remove();
        return rs;
    }

    private static int Count(XElement rs, string name) => rs.Elements(name).Count();

    private static string Compact(XElement element) => element.ToString(SaveOptions.DisableFormatting);

    private static long Bytes(string text) => Encoding.UTF8.GetByteCount(text);
}
