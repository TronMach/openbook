using OpenBook.Application.Import;
using OpenBook.Domain.Common;

namespace OpenBook.Application.Channels;

/// <summary>
/// The books hold one currency, so a report in another is refused: read as it stands, CAD 1,000 would post as 1,000 dollars
/// against a deposit of far less.
/// </summary>
public static class ChannelCurrency
{
    private static readonly string[] Headings = ["currency", "currency code", "transaction currency", "payout currency", "settlement currency"];

    public static void Check(string text, string home)
    {
        var records = Csv.Parse(text);
        var header = records.Take(30).Select(r => r.Fields).FirstOrDefault(f => f.Any(IsHeading));
        if (header is null)
            return;
        var columns = Enumerable.Range(0, header.Count).Where(i => IsHeading(header[i])).ToList();
        foreach (var record in records)
        {
            foreach (var column in columns.Where(c => c < record.Fields.Count))
            {
                var value = record.Fields[column].Trim();
                if (value.Length == 3 && value.All(char.IsAsciiLetter) && !value.Equals(home, StringComparison.OrdinalIgnoreCase))
                    throw new DomainException("channel.other_currency", $"This report has amounts in {value.ToUpperInvariant()}, and these books are in {home}. OpenBook keeps one currency, so it can't come in.");
            }
        }
    }

    private static bool IsHeading(string cell) => Headings.Contains(cell.Trim().Replace('-', ' ').Replace('_', ' '), StringComparer.OrdinalIgnoreCase);
}
