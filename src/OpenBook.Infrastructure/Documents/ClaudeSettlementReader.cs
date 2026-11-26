using System.Text.Json;
using OpenBook.Application.Documents;
using OpenBook.Application.Settlements;
using OpenBook.Domain.Common;
using OpenBook.Domain.Settlements;

namespace OpenBook.Infrastructure.Documents;

/// <summary>
/// Reads a settlement statement with Claude through Anthropic's SDK: the pages chosen go as a PDF or image, and the answer
/// comes back as JSON in a fixed schema. A statement can have a hundred lines, so the answer is streamed with room for them.
/// </summary>
public sealed class ClaudeSettlementReader : IClaudeSettlementReader
{
    private const int MaxTokens = 48_000;

    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(8);

    private const string Instructions = """
        You read a trucking carrier's settlement statement to an owner-operator for a bookkeeping program, and copy what it says into the given JSON schema.
        Copy values exactly as printed; never guess or calculate a value that isn't on the statement. For anything missing or unreadable, use an empty string for text and null for a number.
        - lines: every line of the settlement itself that adds to or takes from what the owner is paid, in order. Leave out totals, subtotals, recaps,
          statistics, income statements, driver payroll stubs, toll lists, shop invoices and freight bills: what they show is already on a line.
        - section: "revenue" (pay for loads, surcharges, accessorials), "reimbursement" (money given back), "deduction" (charges taken), "wage" (a second driver's
          pay and taxes charged to the owner), or "other" (reserve by the mile, interest, a balance brought forward).
        - amount: as printed in its own section. Deductions and wages are positive when they take money, and negative only when printed with a minus sign
          (which can stand after the amount) or in parentheses. In "other", money taken from the owner is negative. Take the owner's share or the charge,
          not the load's gross or a balance printed beside it.
        - code: the short code printed on the line (like RV or UF), description: the words on it.
        - load: the order or load number; dispatch: the dispatch number after it.
        - loaded_miles and empty_miles: only on the line the statement prints them on, once per dispatch. gallons: on fuel lines that give them.
        - Dates as YYYY-MM-DD. A date printed as a month and day belongs to the year that puts it on or before the pay date.
        - net_paid: what reaches the owner (the direct deposit), 0 when nothing is paid. carried_forward: a balance left for the next settlement, negative when the
          owner owes it; null when there is none.
        - totals: the total printed for each section, with the same signs as its lines; null for one that isn't printed.
        """;

    public async Task<SettlementReading> ReadAsync(string apiKey, string model, string fileName, byte[] content, DateOnly reference, CancellationToken ct)
    {
        var answer = await ClaudeRequest.AskAsync(new ClaudeQuestion(
            apiKey, model, Instructions, $"This is a settlement statement. It was paid around {reference:yyyy-MM-dd}. Read it into the schema.", fileName, content, Schema(), MaxTokens, Timeout, Streamed: true), ct);
        return Parse(answer);
    }

    /// <summary>Maps Claude's JSON answer to a reading; anything malformed is a read failure.</summary>
    public static SettlementReading Parse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw new DocumentReadException("the answer wasn't in the expected form");
            var lines = root.TryGetProperty("lines", out var array) && array.ValueKind == JsonValueKind.Array
                ? array.EnumerateArray().Where(l => l.ValueKind == JsonValueKind.Object).Take(Settlement.MaxLines).Select(Line).OfType<SettlementLineDraft>().ToList()
                : [];
            var totals = new List<SettlementTotalDraft>();
            if (root.TryGetProperty("totals", out var t) && t.ValueKind == JsonValueKind.Object)
            {
                foreach (var (name, section) in Sections.Where(s => s.Value != SettlementSection.Other))
                {
                    if (Number(t, name) is { } printed && Math.Abs(printed) <= Money.MaxAmount)
                        totals.Add(new SettlementTotalDraft(section, SettlementSections.Counted(section, printed)));
                }
            }

            return new SettlementReading(
                Text(root, "number"), Date(root, "pay_date"), Date(root, "period_start"), Date(root, "period_end"), Limited(Number(root, "net_paid")), Limited(Number(root, "carried_forward")), lines, totals);
        }
        catch (JsonException)
        {
            throw new DocumentReadException("the answer wasn't in the expected form");
        }
    }

    private static readonly Dictionary<string, SettlementSection> Sections = new(StringComparer.OrdinalIgnoreCase)
    {
        ["revenue"] = SettlementSection.Revenue,
        ["reimbursement"] = SettlementSection.Reimbursement,
        ["deduction"] = SettlementSection.Deduction,
        ["wage"] = SettlementSection.Wage,
        ["other"] = SettlementSection.Other,
    };

    private static SettlementLineDraft? Line(JsonElement l) =>
        Text(l, "section") is { } name && Sections.TryGetValue(name, out var section) && Limited(Number(l, "amount")) is { } amount
            ? new SettlementLineDraft(section, Text(l, "code"), SettlementSections.Counted(section, amount), Date(l, "date"), Text(l, "load"), Text(l, "dispatch"), Text(l, "description"),
                LoadedMiles: Limited(Number(l, "loaded_miles")), EmptyMiles: Limited(Number(l, "empty_miles")), Gallons: Limited(Number(l, "gallons")))
            : null;

    private static decimal? Limited(decimal? value) => value is { } v && Math.Abs(v) <= Money.MaxAmount ? v : null;

    private static string? Text(JsonElement e, string name) => ClaudeRequest.Text(e, name);

    private static decimal? Number(JsonElement e, string name) => ClaudeRequest.Number(e, name);

    private static DateOnly? Date(JsonElement e, string name) => ClaudeRequest.Date(e, name);

    // Every field is required, and only numbers are nullable: Anthropic allows 16 nullable (union) fields in a schema, and optional fields made it
    // "too complex" to compile. Empty text is read as missing.
    public static Dictionary<string, JsonElement> Schema()
    {
        static object Of(string type) => new { type };
        static object Nullable(string type) => new { type = new[] { type, "null" } };
        var line = new
        {
            type = "object",
            additionalProperties = false,
            required = new[] { "section", "code", "date", "load", "dispatch", "description", "amount", "loaded_miles", "empty_miles", "gallons" },
            properties = new
            {
                section = new { type = "string", @enum = Sections.Keys.ToArray() },
                code = Of("string"),
                date = Of("string"),
                load = Of("string"),
                dispatch = Of("string"),
                description = Of("string"),
                amount = Nullable("number"),
                loaded_miles = Nullable("number"),
                empty_miles = Nullable("number"),
                gallons = Nullable("number"),
            },
        };
        var totals = new
        {
            type = "object",
            additionalProperties = false,
            required = new[] { "revenue", "reimbursement", "deduction", "wage" },
            properties = new { revenue = Nullable("number"), reimbursement = Nullable("number"), deduction = Nullable("number"), wage = Nullable("number") },
        };
        return ClaudeRequest.Schema(
            ["number", "pay_date", "period_start", "period_end", "net_paid", "carried_forward", "totals", "lines"],
            new
            {
                number = Of("string"),
                pay_date = Of("string"),
                period_start = Of("string"),
                period_end = Of("string"),
                net_paid = Nullable("number"),
                carried_forward = Nullable("number"),
                totals,
                lines = new { type = "array", items = line },
            });
    }
}
