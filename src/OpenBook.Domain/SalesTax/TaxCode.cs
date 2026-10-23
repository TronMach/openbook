using OpenBook.Domain.Common;

namespace OpenBook.Domain.SalesTax;

/// <summary>One of a tax code's rates, in the order they print.</summary>
public sealed class TaxCodeRate
{
    private TaxCodeRate() { }

    internal TaxCodeRate(int position, Guid taxRateId)
    {
        Position = position;
        TaxRateId = taxRateId;
    }

    public int Position { get; private set; }
    public Guid TaxRateId { get; private set; }
}

/// <summary>
/// What a sales document or customer is taxed at: one rate, or a combined group of rates (state + county +
/// city) whose percentages add up. Each rate becomes its own tax line, so every agency's share is tracked.
/// </summary>
public sealed class TaxCode
{
    public const int MaxRates = 10;

    private readonly List<TaxCodeRate> _rates = [];

    private TaxCode() => Name = string.Empty;

    public Guid Id { get; private set; }

    /// <summary>Unique, ignoring case.</summary>
    public string Name { get; private set; }

    public string? Description { get; private set; }
    public bool IsActive { get; private set; }

    public IReadOnlyList<TaxCodeRate> Rates => _rates;

    public IReadOnlyList<Guid> RateIds => _rates.OrderBy(r => r.Position).Select(r => r.TaxRateId).ToList();

    /// <summary>More than one rate.</summary>
    public bool IsCombined => _rates.Count > 1;

    public static TaxCode Create(TaxCodeDetails details)
    {
        var code = new TaxCode { Id = Guid.CreateVersion7(), IsActive = true };
        code.Update(details);
        return code;
    }

    public void Update(TaxCodeDetails details)
    {
        ArgumentNullException.ThrowIfNull(details);
        ArgumentNullException.ThrowIfNull(details.RateIds);
        Name = Text.Optional(details.Name, 100, "tax.code_name_too_long", "Tax code name")
            ?? throw new DomainException("tax.code_name_required", "A tax code needs a name.");
        Description = Text.Optional(details.Description, 500, "tax.code_description_too_long", "Description");
        var rateIds = details.RateIds.Where(id => id != Guid.Empty).ToList();
        if (rateIds.Count == 0)
            throw new DomainException("tax.code_no_rates", "A tax code needs at least one rate.");
        if (rateIds.Count > MaxRates)
            throw new DomainException("tax.code_too_many_rates", $"A tax code can combine at most {MaxRates} rates.");
        if (rateIds.Distinct().Count() != rateIds.Count)
            throw new DomainException("tax.code_duplicate_rate", "Each rate can be in a tax code only once.");

        _rates.Clear();
        _rates.AddRange(rateIds.Select((id, i) => new TaxCodeRate(i + 1, id)));
    }

    public void Deactivate() => IsActive = false;

    public void Reactivate() => IsActive = true;
}

public sealed record TaxCodeDetails(string Name, IReadOnlyList<Guid> RateIds, string? Description = null);
