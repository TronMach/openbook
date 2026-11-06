using OpenBook.Domain.Common;

namespace OpenBook.Domain.Assets;

public enum DepreciationMethod
{
    /// <summary>Cost less salvage spread evenly over the useful life.</summary>
    StraightLine,

    /// <summary>A fixed percentage of book value each month (200% = double declining), switching to straight-line when that's more.</summary>
    DecliningBalance,

    /// <summary>US tax depreciation (GDS): the IRS percentage tables, half-year convention, or mid-month for real property.</summary>
    Macrs,

    /// <summary>Not depreciated (land, works of art).</summary>
    None,
}

/// <summary>MACRS (GDS) property classes.</summary>
public enum MacrsClass
{
    Year3,
    Year5,
    Year7,
    Year10,
    Year15,
    Year20,

    /// <summary>Residential rental property, 27.5 years, mid-month.</summary>
    Residential,

    /// <summary>Nonresidential real property, 39 years, mid-month.</summary>
    Nonresidential,
}

public enum FixedAssetStatus
{
    Active,
    Disposed,
}

/// <param name="LifeMonths">Useful life for straight-line and declining balance.</param>
/// <param name="DecliningPercent">For declining balance: 200 is double declining, 150 is one-and-a-half.</param>
/// <param name="PriorDepreciation">Depreciation already in the books (from before OpenBook) as of <paramref name="PriorThrough"/>.</param>
public sealed record FixedAssetDetails(
    string Name,
    Guid AssetAccountId,
    Guid AccumulatedAccountId,
    Guid ExpenseAccountId,
    DateOnly InServiceDate,
    decimal Cost,
    DepreciationMethod Method,
    int LifeMonths = 0,
    decimal Salvage = 0m,
    decimal DecliningPercent = 200m,
    MacrsClass? MacrsClass = null,
    decimal PriorDepreciation = 0m,
    DateOnly? PriorThrough = null,
    string? Number = null,
    string? Description = null);

/// <summary>
/// Something the business owns and uses for more than a year. The asset record posts nothing when created (the
/// purchase is a bill, expense or journal entry to its asset account); depreciation runs post Dr depreciation
/// expense, Cr accumulated depreciation each period, and disposal takes it off the books with a gain or loss.
/// </summary>
public sealed class FixedAsset
{
    public const string DisposalSource = "assets.disposal";

    private FixedAsset()
    {
        Name = string.Empty;
    }

    public Guid Id { get; private set; }
    public string Name { get; private set; }
    public string? Number { get; private set; }
    public string? Description { get; private set; }
    public Guid AssetAccountId { get; private set; }
    public Guid AccumulatedAccountId { get; private set; }
    public Guid ExpenseAccountId { get; private set; }
    public DateOnly InServiceDate { get; private set; }
    public decimal Cost { get; private set; }
    public decimal Salvage { get; private set; }
    public DepreciationMethod Method { get; private set; }
    public int LifeMonths { get; private set; }
    public decimal DecliningPercent { get; private set; }
    public MacrsClass? MacrsClass { get; private set; }
    public decimal PriorDepreciation { get; private set; }
    public DateOnly? PriorThrough { get; private set; }
    public FixedAssetStatus Status { get; private set; }

    public DateOnly? DisposedOn { get; private set; }
    public decimal Proceeds { get; private set; }
    public Guid? ProceedsAccountId { get; private set; }

    /// <summary>Depreciation for the months up to disposal not yet posted by a run, posted with the disposal.</summary>
    public decimal DisposalDepreciation { get; private set; }

    public Guid? DisposalEntryId { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    public bool IsDisposed => Status == FixedAssetStatus.Disposed;

    /// <summary>What's depreciated in total: cost less salvage (MACRS ignores salvage).</summary>
    public decimal Base => Method switch
    {
        DepreciationMethod.None => 0m,
        DepreciationMethod.Macrs => Cost,
        _ => Cost - Salvage,
    };

    public static FixedAsset Create(FixedAssetDetails details, DateTimeOffset createdAt)
    {
        var asset = new FixedAsset { Id = Guid.CreateVersion7(), Status = FixedAssetStatus.Active, CreatedAt = createdAt.ToUniversalTime() };
        asset.Update(details);
        return asset;
    }

    public void Update(FixedAssetDetails details)
    {
        ArgumentNullException.ThrowIfNull(details);
        if (IsDisposed)
            throw new DomainException("asset.disposed", $"{Name} is disposed. Undo the disposal to change it.");
        Money.EnsureCents(details.Cost);
        Money.EnsureCents(details.Salvage);
        Money.EnsureCents(details.PriorDepreciation);
        if (details.Cost <= 0m)
            throw new DomainException("asset.cost_not_positive", "Enter what the asset cost.");
        if (details.Salvage < 0m || details.Salvage >= details.Cost)
            throw new DomainException("asset.salvage_invalid", "Salvage value is from zero up to less than the cost.");
        if (!Enum.IsDefined(details.Method))
            throw new DomainException("asset.method_invalid", "Choose a depreciation method.");
        if (details.Method is DepreciationMethod.StraightLine or DepreciationMethod.DecliningBalance && details.LifeMonths is < 1 or > 1200)
            throw new DomainException("asset.life_invalid", "Enter a useful life from 1 month to 100 years.");
        if (details.Method == DepreciationMethod.DecliningBalance && (details.DecliningPercent is <= 100m or > 300m || decimal.Round(details.DecliningPercent, 2) != details.DecliningPercent))
            throw new DomainException("asset.declining_invalid", "The declining balance rate is above 100% and at most 300% (200% is double declining).");
        if (details.Method == DepreciationMethod.Macrs && (details.MacrsClass is not { } c || !Enum.IsDefined(c)))
            throw new DomainException("asset.macrs_class_required", "Choose the MACRS property class.");
        if (details.PriorDepreciation < 0m || details.PriorDepreciation > details.Cost)
            throw new DomainException("asset.prior_invalid", "Depreciation to date is from zero up to the cost.");
        if (details.PriorDepreciation > 0m && details.PriorThrough is null)
            throw new DomainException("asset.prior_date_required", "Say what date the depreciation to date runs through.");
        if (details.PriorThrough is { } through && through < details.InServiceDate)
            throw new DomainException("asset.prior_date_invalid", "Depreciation to date can't run through a date before the asset was in service.");

        Name = Text.Optional(details.Name, 200, "asset.name_too_long", "Name") ?? throw new DomainException("asset.name_required", "Enter the asset's name.");
        Number = Text.Optional(details.Number, 30, "asset.number_too_long", "Number");
        Description = Text.Optional(details.Description, 1000, "asset.description_too_long", "Description");
        AssetAccountId = details.AssetAccountId;
        AccumulatedAccountId = details.AccumulatedAccountId;
        ExpenseAccountId = details.ExpenseAccountId;
        InServiceDate = details.InServiceDate;
        Cost = details.Cost;
        Salvage = details.Method == DepreciationMethod.Macrs ? 0m : details.Salvage;
        Method = details.Method;
        LifeMonths = details.Method is DepreciationMethod.StraightLine or DepreciationMethod.DecliningBalance ? details.LifeMonths : 0;
        DecliningPercent = details.Method == DepreciationMethod.DecliningBalance ? details.DecliningPercent : 0m;
        MacrsClass = details.Method == DepreciationMethod.Macrs ? details.MacrsClass : null;
        PriorDepreciation = details.PriorDepreciation;
        PriorThrough = details.PriorDepreciation > 0m || details.PriorThrough is not null ? details.PriorThrough : null;
    }

    /// <summary>Only the name, number and description, once depreciation has been posted.</summary>
    public void Rename(string name, string? number, string? description)
    {
        Name = Text.Optional(name, 200, "asset.name_too_long", "Name") ?? throw new DomainException("asset.name_required", "Enter the asset's name.");
        Number = Text.Optional(number, 30, "asset.number_too_long", "Number");
        Description = Text.Optional(description, 1000, "asset.description_too_long", "Description");
    }

    public void Dispose(DateOnly date, decimal proceeds, Guid? proceedsAccountId, decimal depreciation, Guid entryId)
    {
        if (IsDisposed)
            throw new DomainException("asset.disposed", $"{Name} is already disposed.");
        Status = FixedAssetStatus.Disposed;
        DisposedOn = date;
        Proceeds = proceeds;
        ProceedsAccountId = proceedsAccountId;
        DisposalDepreciation = depreciation;
        DisposalEntryId = entryId;
    }

    public void UndoDisposal()
    {
        if (!IsDisposed)
            throw new DomainException("asset.not_disposed", $"{Name} isn't disposed.");
        Status = FixedAssetStatus.Active;
        DisposedOn = null;
        Proceeds = 0m;
        ProceedsAccountId = null;
        DisposalDepreciation = 0m;
        DisposalEntryId = null;
    }
}

/// <summary>One month's depreciation in an asset's schedule.</summary>
public readonly record struct DepreciationPeriod(DateOnly PeriodEnd, decimal Amount);

/// <summary>Depreciation schedules, one amount per calendar month from the month the asset is placed in service.</summary>
public static class Depreciation
{
    /// <summary>IRS Publication 946 table A-1 (GDS, 200% or 150% declining balance, half-year convention), percent per tax year.</summary>
    private static readonly Dictionary<MacrsClass, decimal[]> HalfYear = new()
    {
        [MacrsClass.Year3] = [33.33m, 44.45m, 14.81m, 7.41m],
        [MacrsClass.Year5] = [20.00m, 32.00m, 19.20m, 11.52m, 11.52m, 5.76m],
        [MacrsClass.Year7] = [14.29m, 24.49m, 17.49m, 12.49m, 8.93m, 8.92m, 8.93m, 4.46m],
        [MacrsClass.Year10] = [10.00m, 18.00m, 14.40m, 11.52m, 9.22m, 7.37m, 6.55m, 6.55m, 6.56m, 6.55m, 3.28m],
        [MacrsClass.Year15] = [5.00m, 9.50m, 8.55m, 7.70m, 6.93m, 6.23m, 5.90m, 5.90m, 5.91m, 5.90m, 5.91m, 5.90m, 5.91m, 5.90m, 5.91m, 2.95m],
        [MacrsClass.Year20] =
        [
            3.750m, 7.219m, 6.677m, 6.177m, 5.713m, 5.285m, 4.888m, 4.522m, 4.462m, 4.461m, 4.462m,
            4.461m, 4.462m, 4.461m, 4.462m, 4.461m, 4.462m, 4.461m, 4.462m, 4.461m, 2.231m,
        ],
    };

    /// <summary>Year 1 of Pub 946 Table A-7a by month placed in service: not quite the exact fraction, so it's held rather than worked out.</summary>
    private static readonly decimal[] Nonresidential =
        [2.461m, 2.247m, 2.033m, 1.819m, 1.605m, 1.391m, 1.177m, 0.963m, 0.749m, 0.535m, 0.321m, 0.107m];

    private const decimal NonresidentialYear = 2.564m;
    private const int NonresidentialFullYears = 38;

    public static string Label(MacrsClass c) => c switch
    {
        MacrsClass.Year3 => "3-year",
        MacrsClass.Year5 => "5-year (cars, computers)",
        MacrsClass.Year7 => "7-year (furniture, equipment)",
        MacrsClass.Year10 => "10-year",
        MacrsClass.Year15 => "15-year (land improvements)",
        MacrsClass.Year20 => "20-year",
        MacrsClass.Residential => "27.5-year residential rental",
        _ => "39-year nonresidential real property",
    };

    /// <summary>The MACRS percentage for each tax year (calendar years from the year placed in service).</summary>
    public static IReadOnlyList<decimal> MacrsPercents(MacrsClass c, int monthPlacedInService)
    {
        if (HalfYear.TryGetValue(c, out var table))
            return table;
        // Table A-7a, the figures a tax preparer works from: years 2-39 at one rate, year 40 whatever is left.
        if (c == MacrsClass.Nonresidential)
        {
            var first = Nonresidential[monthPlacedInService - 1];
            return [first, .. Enumerable.Repeat(NonresidentialYear, NonresidentialFullYears), 100m - first - NonresidentialFullYears * NonresidentialYear];
        }

        // Mid-month convention: the first year counts from the middle of the month placed in service. To three
        // places this is Table A-6.
        var full = 100m / 27.5m;
        var percents = new List<decimal> { full * (12.5m - monthPlacedInService) / 12m };
        while (percents.Sum() + full < 100m)
            percents.Add(full);
        percents.Add(100m - percents.Sum());
        return percents;
    }

    /// <summary>Every month's depreciation, to the cent, adding up to the asset's <see cref="FixedAsset.Base"/>.</summary>
    public static IReadOnlyList<DepreciationPeriod> Schedule(FixedAsset asset)
    {
        ArgumentNullException.ThrowIfNull(asset);
        var start = new DateOnly(asset.InServiceDate.Year, asset.InServiceDate.Month, 1);
        DateOnly MonthEnd(int index) => start.AddMonths(index + 1).AddDays(-1);
        var amounts = asset.Method switch
        {
            DepreciationMethod.StraightLine => Even(asset.Base, asset.LifeMonths),
            DepreciationMethod.DecliningBalance => Declining(asset.Cost, asset.Salvage, asset.LifeMonths, asset.DecliningPercent),
            DepreciationMethod.Macrs => Macrs(asset.Cost, asset.MacrsClass!.Value, asset.InServiceDate.Month),
            _ => [],
        };
        return amounts.Select((a, i) => new DepreciationPeriod(MonthEnd(i), a)).ToList();
    }

    /// <summary>
    /// Accumulated depreciation the schedule calls for through <paramref name="date"/> (month ends on or before it):
    /// depreciation before OpenBook counts as recorded through its date, and the schedule continues after it.
    /// </summary>
    public static decimal ScheduledThrough(FixedAsset asset, DateOnly date)
    {
        ArgumentNullException.ThrowIfNull(asset);
        var schedule = Schedule(asset);
        decimal total;
        if (asset.PriorThrough is { } prior)
        {
            if (date <= prior)
                return asset.PriorDepreciation;
            total = asset.PriorDepreciation + schedule.Where(p => p.PeriodEnd > prior && p.PeriodEnd <= date).Sum(p => p.Amount);

            // What was brought in rarely matches the schedule: the last month takes the difference, so the asset still ends at its base.
            if (schedule.Count > 0 && date >= schedule[^1].PeriodEnd)
                total = asset.Base;
        }
        else
        {
            total = schedule.Where(p => p.PeriodEnd <= date).Sum(p => p.Amount);
        }

        return Math.Min(total, asset.Base);
    }

    /// <summary>Spreads an amount over months with cumulative rounding, so the months add up exactly.</summary>
    private static List<decimal> Even(decimal amount, int months)
    {
        var result = new List<decimal>(months);
        var before = 0m;
        for (var k = 1; k <= months; k++)
        {
            var through = decimal.Round(amount * k / months, 2, MidpointRounding.AwayFromZero);
            result.Add(through - before);
            before = through;
        }

        return result;
    }

    private static List<decimal> Declining(decimal cost, decimal salvage, int months, decimal percent)
    {
        var rate = percent / 100m / months;
        var result = new List<decimal>(months);
        var book = cost;
        for (var k = 0; k < months; k++)
        {
            var remaining = months - k;
            var left = book - salvage;
            decimal amount;
            if (remaining == 1)
            {
                amount = left;
            }
            else
            {
                var declining = book * rate;
                var straight = left / remaining;
                amount = Math.Min(left, decimal.Round(Math.Max(declining, straight), 2, MidpointRounding.AwayFromZero));
            }

            result.Add(amount);
            book -= amount;
        }

        return result;
    }

    private static List<decimal> Macrs(decimal cost, MacrsClass c, int month)
    {
        var percents = MacrsPercents(c, month);
        var result = new List<decimal>();
        var cumulative = 0m;
        var before = 0m;
        for (var y = 0; y < percents.Count; y++)
        {
            cumulative += percents[y];
            var through = y == percents.Count - 1 ? cost : decimal.Round(cost * cumulative / 100m, 2, MidpointRounding.AwayFromZero);
            var yearAmount = through - before;
            before = through;
            // The first tax year runs from the month placed in service to December; later years are whole calendar years.
            result.AddRange(Even(yearAmount, y == 0 ? 13 - month : 12));
        }

        return result;
    }
}

/// <summary>A posted batch of depreciation through a date: Dr each asset's expense account, Cr its accumulated depreciation.</summary>
public sealed class DepreciationRun
{
    public const string Source = "assets.depreciation";

    private readonly List<DepreciationRunLine> _lines = [];

    private DepreciationRun() { }

    public Guid Id { get; private set; }
    public DateOnly Date { get; private set; }
    public DocumentStatus Status { get; private set; }
    public Guid JournalEntryId { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public IReadOnlyList<DepreciationRunLine> Lines => _lines;
    public decimal Total => _lines.Sum(l => l.Amount);

    public static DepreciationRun Create(Guid id, DateOnly date, IReadOnlyList<(Guid AssetId, decimal Amount)> lines, Guid journalEntryId, DateTimeOffset createdAt)
    {
        ArgumentNullException.ThrowIfNull(lines);
        var run = new DepreciationRun { Id = id, Date = date, Status = DocumentStatus.Posted, JournalEntryId = journalEntryId, CreatedAt = createdAt.ToUniversalTime() };
        run._lines.AddRange(lines.Select((l, i) => new DepreciationRunLine(i + 1, l.AssetId, l.Amount)));
        return run;
    }

    public void Void()
    {
        if (Status == DocumentStatus.Voided)
            throw new DomainException("depreciation.already_voided", "This depreciation is already voided.");
        Status = DocumentStatus.Voided;
    }
}

public sealed class DepreciationRunLine
{
    private DepreciationRunLine() { }

    internal DepreciationRunLine(int lineNumber, Guid assetId, decimal amount)
    {
        LineNumber = lineNumber;
        AssetId = assetId;
        Amount = amount;
    }

    public int LineNumber { get; private set; }
    public Guid AssetId { get; private set; }
    public decimal Amount { get; private set; }
}
