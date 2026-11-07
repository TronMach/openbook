using System.Text;
using OpenBook.Domain.Common;

namespace OpenBook.Application.Import;

/// <summary>One CSV record and the line it starts on, for error messages.</summary>
public sealed record CsvRecord(int Line, IReadOnlyList<string> Fields);

/// <summary>
/// RFC 4180 reader: quoted fields, doubled quotes, line breaks inside quotes, CRLF or LF. The delimiter
/// (comma, semicolon or tab) is taken from whichever appears most in the first line, which covers
/// European spreadsheet exports.
/// </summary>
public static class Csv
{
    public static IReadOnlyList<CsvRecord> Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        text = text.TrimStart('﻿');
        var delimiter = DetectDelimiter(text);

        var records = new List<CsvRecord>();
        var fields = new List<string>();
        var field = new StringBuilder();
        var line = 1;
        var recordLine = 1;
        var inQuotes = false;
        var fieldWasQuoted = false;
        var fieldHasText = false;

        void EndField()
        {
            fields.Add(fieldWasQuoted ? field.ToString() : field.ToString().Trim());
            field.Clear();
            fieldWasQuoted = false;
            fieldHasText = false;
        }

        void EndRecord()
        {
            EndField();
            if (fields.Count > 1 || fields[0].Length > 0)
                records.Add(new CsvRecord(recordLine, fields.ToArray()));
            fields.Clear();
        }

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (inQuotes)
            {
                if (c == '"' && i + 1 < text.Length && text[i + 1] == '"')
                {
                    field.Append('"');
                    i++;
                }
                else if (c == '"')
                {
                    inQuotes = false;
                }
                else
                {
                    if (c == '\n')
                        line++;
                    field.Append(c);
                }
            }
            else if (c == '"' && !fieldHasText)
            {
                inQuotes = true;
                fieldWasQuoted = true;
                field.Clear();
            }
            else if (c == delimiter)
            {
                EndField();
            }
            else if (c is '\r' or '\n')
            {
                if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
                    i++;
                EndRecord();
                line++;
                recordLine = line;
            }
            else if (!fieldWasQuoted)
            {
                fieldHasText |= !char.IsWhiteSpace(c);
                field.Append(c);
            }
        }

        if (inQuotes)
            throw new DomainException("csv.unterminated_quote", $"A quoted value starting on line {recordLine} is never closed.");
        if (field.Length > 0 || fields.Count > 0)
            EndRecord();

        return records;
    }

    /// <summary>Lower-cased letters and digits only, so "Accnt. #" and "account number" can be matched loosely.</summary>
    public static string Normalize(string value) =>
        new(value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    /// <summary>
    /// The records from the first of the first <paramref name="scan"/> that <paramref name="isHeader"/> takes for the header
    /// (given its fields), so lines a bank puts above it (account name, balance, a title) are left out; unchanged when none is.
    /// </summary>
    public static IReadOnlyList<CsvRecord> FromHeader(IReadOnlyList<CsvRecord> records, Func<IReadOnlyList<string>, bool> isHeader, int scan = 20)
    {
        ArgumentNullException.ThrowIfNull(records);
        ArgumentNullException.ThrowIfNull(isHeader);
        for (var i = 0; i < Math.Min(scan, records.Count); i++)
        {
            if (isHeader(records[i].Fields))
                return i == 0 ? records : records.Skip(i).ToList();
        }

        return records;
    }

    /// <summary>
    /// The candidate found most often, outside quotes, on any of the first 20 lines: the header has the most columns, and
    /// lines a bank puts above it (an account name, a balance) have fewer. Ties go to the comma, then the semicolon.
    /// </summary>
    private static char DetectDelimiter(string text)
    {
        char[] candidates = [',', ';', '\t'];
        var best = new int[candidates.Length];
        var counts = new int[candidates.Length];
        var lines = 0;
        var inQuotes = false;
        foreach (var c in text)
        {
            if (c == '"')
            {
                inQuotes = !inQuotes;
            }
            else if (!inQuotes && (c == '\r' || c == '\n'))
            {
                for (var k = 0; k < candidates.Length; k++)
                    best[k] = Math.Max(best[k], counts[k]);
                Array.Clear(counts);
                if (c == '\n' && ++lines >= 20)
                    break;
            }
            else if (!inQuotes && Array.IndexOf(candidates, c) is var k and >= 0)
            {
                counts[k]++;
            }
        }

        var pick = 0;
        for (var k = 0; k < candidates.Length; k++)
        {
            best[k] = Math.Max(best[k], counts[k]);
            if (best[k] > best[pick])
                pick = k;
        }

        return candidates[pick];
    }
}
