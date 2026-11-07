using OpenBook.Domain.Common;

namespace OpenBook.Domain.Nonprofit;

/// <summary>A class's share of a rule, as a percentage.</summary>
public sealed record AllocationShareDraft(Guid ClassId, decimal Percent);

/// <param name="AccountId">The expense account being spread.</param>
/// <param name="PoolClassId">Where its cost sits until it's spread; null for lines with no class.</param>
public sealed record AllocationRuleDetails(string Name, Guid AccountId, Guid? PoolClassId, IReadOnlyList<AllocationShareDraft> Shares);

/// <summary>
/// How a shared cost (rent, say) is spread across programs and functions: one account, from a pool, by percentages that
/// add up to 100. A run moves what's in the pool for a period into the classes, so running it twice moves nothing more.
/// </summary>
public sealed class AllocationRule
{
    public const int MaxNameLength = 100;

    private readonly List<AllocationShare> _shares = [];

    private AllocationRule() => Name = string.Empty;

    public Guid Id { get; private set; }
    public string Name { get; private set; }
    public Guid AccountId { get; private set; }
    public Guid? PoolClassId { get; private set; }
    public bool IsActive { get; private set; }
    public IReadOnlyList<AllocationShare> Shares => _shares;

    public static AllocationRule Create(AllocationRuleDetails details)
    {
        var rule = new AllocationRule { Id = Guid.CreateVersion7(), IsActive = true };
        rule.Update(details);
        return rule;
    }

    public void Update(AllocationRuleDetails details)
    {
        ArgumentNullException.ThrowIfNull(details);
        ArgumentNullException.ThrowIfNull(details.Shares);
        Name = Text.Optional(details.Name, MaxNameLength, "allocation.name_too_long", "Name") ?? throw new DomainException("allocation.name_required", "Give the rule a name.");
        if (details.AccountId == Guid.Empty)
            throw new DomainException("allocation.account_required", "Choose the account to spread.");
        var shares = details.Shares.Where(s => s.Percent != 0m).ToList();
        if (shares.Count < 2)
            throw new DomainException("allocation.shares_few", "A rule spreads a cost over at least two classes.");
        if (shares.Any(s => s.ClassId == Guid.Empty || s.ClassId == details.PoolClassId))
            throw new DomainException("allocation.share_class", "Choose a class for each share, other than the pool.");
        if (shares.Select(s => s.ClassId).Distinct().Count() != shares.Count)
            throw new DomainException("allocation.share_repeat", "Each class can have one share.");
        if (shares.Any(s => s.Percent < 0m) || shares.Sum(s => s.Percent) != 100m)
            throw new DomainException("allocation.shares_total", $"The shares add up to {shares.Sum(s => s.Percent):0.####}%, not 100%.");
        AccountId = details.AccountId;
        PoolClassId = details.PoolClassId == Guid.Empty ? null : details.PoolClassId;
        _shares.Clear();
        _shares.AddRange(shares.Select((s, i) => new AllocationShare(i + 1, s.ClassId, s.Percent)));
    }

    public void SetActive(bool active) => IsActive = active;
}

public sealed class AllocationShare
{
    private AllocationShare()
    {
    }

    internal AllocationShare(int lineNumber, Guid classId, decimal percent)
    {
        LineNumber = lineNumber;
        ClassId = classId;
        Percent = percent;
    }

    public int LineNumber { get; private set; }
    public Guid ClassId { get; private set; }
    public decimal Percent { get; private set; }
}

/// <summary>A rule run for a period: one entry moving the cost out of the pool and into the classes.</summary>
public sealed class AllocationRun
{
    public const string Source = "nonprofit.allocation";

    private AllocationRun()
    {
    }

    public Guid Id { get; private set; }
    public Guid RuleId { get; private set; }
    public DateOnly From { get; private set; }
    public DateOnly To { get; private set; }
    public decimal Amount { get; private set; }
    public Guid JournalEntryId { get; private set; }
    public DocumentStatus Status { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    public static AllocationRun Create(Guid id, Guid ruleId, DateOnly from, DateOnly to, decimal amount, Guid entryId, DateTimeOffset createdAt) =>
        new() { Id = id, RuleId = ruleId, From = from, To = to, Amount = amount, JournalEntryId = entryId, Status = DocumentStatus.Posted, CreatedAt = createdAt.ToUniversalTime() };

    public void Void()
    {
        if (Status == DocumentStatus.Voided)
            throw new DomainException("allocation.already_voided", "This run is already voided.");
        Status = DocumentStatus.Voided;
    }
}
