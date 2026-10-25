using OpenBook.Domain.Ledger;

namespace OpenBook.Web.Components.Journal;

/// <summary>One editable debit/credit line, as typed in <see cref="JournalLinesEditor"/>.</summary>
public sealed class JournalLineModel
{
    public Guid? AccountId { get; set; }
    public decimal? Debit { get; set; }
    public decimal? Credit { get; set; }
    public string? Memo { get; set; }
    public Guid? ClassId { get; set; }

    public static JournalLineModel From(JournalLineDraft draft) => new()
    {
        AccountId = draft.AccountId,
        Debit = draft.Debit > 0m ? draft.Debit : null,
        Credit = draft.Credit > 0m ? draft.Credit : null,
        Memo = draft.Memo,
        ClassId = draft.ClassId,
    };
}

public static class JournalLines
{
    public static IEnumerable<JournalLineModel> Filled(IEnumerable<JournalLineModel> lines) =>
        lines.Where(l => l.AccountId is not null || l.Debit > 0 || l.Credit > 0);

    public static decimal Debits(IEnumerable<JournalLineModel> lines) => lines.Sum(l => l.Debit ?? 0m);

    public static decimal Credits(IEnumerable<JournalLineModel> lines) => lines.Sum(l => l.Credit ?? 0m);

    public static bool IsBalanced(IEnumerable<JournalLineModel> lines) => Debits(lines) == Credits(lines) && Debits(lines) > 0m;

    /// <summary>Balanced, with at least two complete lines and each line either a debit or a credit.</summary>
    public static bool IsComplete(IReadOnlyCollection<JournalLineModel> lines)
    {
        var filled = Filled(lines).ToList();
        return IsBalanced(lines) && filled.Count >= 2 && filled.All(l => l.AccountId is not null && (l.Debit > 0) != (l.Credit > 0));
    }

    public static List<JournalLineDraft> Drafts(IEnumerable<JournalLineModel> lines) =>
        Filled(lines).Select(l => new JournalLineDraft(l.AccountId!.Value, l.Debit ?? 0m, l.Credit ?? 0m, l.Memo, l.ClassId)).ToList();
}
