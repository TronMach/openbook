using OpenBook.Domain.Common;

namespace OpenBook.Domain.Reporting;

public enum ScenarioRecurrence
{
    Once,
    Weekly,
    Monthly,
}

/// <param name="Amount">Money in (positive) or out (negative).</param>
public sealed record ScenarioItemDetails(string Description, decimal Amount, DateOnly StartDate, ScenarioRecurrence Recurrence = ScenarioRecurrence.Once, DateOnly? EndDate = null);

/// <param name="CollectionDelayDays">How many days after the due date customers pay (negative: early).</param>
/// <param name="PaymentDelayDays">How many days after the due date you pay bills.</param>
public sealed record ForecastScenarioDetails(string Name, int CollectionDelayDays = 0, int PaymentDelayDays = 0, string? Notes = null);

/// <summary>
/// A "what if" for the cash-flow forecast: customers paying later or earlier, bills paid later, and extra money in or
/// out (a new hire, a loan, a big order). Scenarios post nothing.
/// </summary>
public sealed class ForecastScenario
{
    private readonly List<ScenarioItem> _items = [];

    private ForecastScenario() => Name = string.Empty;

    public Guid Id { get; private set; }
    public string Name { get; private set; }
    public int CollectionDelayDays { get; private set; }
    public int PaymentDelayDays { get; private set; }
    public string? Notes { get; private set; }
    public IReadOnlyList<ScenarioItem> Items => _items;

    public static ForecastScenario Create(ForecastScenarioDetails details)
    {
        var s = new ForecastScenario { Id = Guid.CreateVersion7() };
        s.Update(details);
        return s;
    }

    public void Update(ForecastScenarioDetails details)
    {
        ArgumentNullException.ThrowIfNull(details);
        if (details.CollectionDelayDays is < -90 or > 365 || details.PaymentDelayDays is < -90 or > 365)
            throw new DomainException("scenario.delay_invalid", "Delays are from -90 to 365 days.");
        Name = Text.Optional(details.Name, 100, "scenario.name_too_long", "Name") ?? throw new DomainException("scenario.name_required", "Name the scenario.");
        CollectionDelayDays = details.CollectionDelayDays;
        PaymentDelayDays = details.PaymentDelayDays;
        Notes = Text.Optional(details.Notes, 1000, "scenario.notes_too_long", "Notes");
    }

    public ScenarioItem AddItem(ScenarioItemDetails details)
    {
        ArgumentNullException.ThrowIfNull(details);
        if (_items.Count >= 100)
            throw new DomainException("scenario.too_many_items", "A scenario can have at most 100 items.");
        Money.EnsureCents(details.Amount);
        if (details.Amount == 0m)
            throw new DomainException("scenario.amount_zero", "Enter an amount: positive for money in, negative for money out.");
        if (!Enum.IsDefined(details.Recurrence))
            throw new DomainException("scenario.recurrence_invalid", "Choose how often.");
        if (details.EndDate is { } end && end < details.StartDate)
            throw new DomainException("scenario.dates_invalid", "The end date can't be before the start.");
        var item = new ScenarioItem(
            Guid.CreateVersion7(),
            Text.Optional(details.Description, 200, "scenario.description_too_long", "Description") ?? throw new DomainException("scenario.description_required", "Describe the item."),
            details.Amount, details.StartDate, details.Recurrence, details.Recurrence == ScenarioRecurrence.Once ? null : details.EndDate);
        _items.Add(item);
        return item;
    }

    public void RemoveItem(Guid itemId) => _items.RemoveAll(i => i.Id == itemId);
}

public sealed class ScenarioItem
{
    private ScenarioItem() => Description = string.Empty;

    internal ScenarioItem(Guid id, string description, decimal amount, DateOnly startDate, ScenarioRecurrence recurrence, DateOnly? endDate)
    {
        Id = id;
        Description = description;
        Amount = amount;
        StartDate = startDate;
        Recurrence = recurrence;
        EndDate = endDate;
    }

    public Guid Id { get; private set; }
    public string Description { get; private set; }
    public decimal Amount { get; private set; }
    public DateOnly StartDate { get; private set; }
    public ScenarioRecurrence Recurrence { get; private set; }
    public DateOnly? EndDate { get; private set; }

    /// <summary>The dates it happens from <paramref name="from"/> through <paramref name="to"/>.</summary>
    public IEnumerable<DateOnly> Dates(DateOnly from, DateOnly to)
    {
        for (var i = 0; i < 1000; i++)
        {
            var date = Recurrence switch
            {
                ScenarioRecurrence.Weekly => StartDate.AddDays(7 * i),
                ScenarioRecurrence.Monthly => StartDate.AddMonths(i),
                _ => i == 0 ? StartDate : DateOnly.MaxValue,
            };
            if (date > to || (EndDate is { } end && date > end))
                yield break;
            if (date >= from)
                yield return date;
        }
    }
}

/// <summary>
/// Marks a posted journal entry as an adjusting entry (an accountant's year-end correction). The ledger itself is
/// untouched; reports use the marks to show balances before and after adjustments.
/// </summary>
public sealed class AdjustingEntry
{
    private AdjustingEntry() => MarkedBy = string.Empty;

    public Guid JournalEntryId { get; private set; }
    public string MarkedBy { get; private set; }
    public DateTimeOffset MarkedAt { get; private set; }

    public static AdjustingEntry Create(Guid journalEntryId, string markedBy, DateTimeOffset markedAt) =>
        new() { JournalEntryId = journalEntryId, MarkedBy = markedBy, MarkedAt = markedAt.ToUniversalTime() };
}
