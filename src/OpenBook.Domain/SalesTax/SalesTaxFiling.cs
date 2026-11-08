using OpenBook.Domain.Common;

namespace OpenBook.Domain.SalesTax;

/// <summary>
/// A note that an agency's return for a period was filed: when, the confirmation number and what it reported. It
/// posts nothing (paying the tax is a <see cref="SalesTaxPayment"/>) and can be removed if entered by mistake.
/// </summary>
public sealed class SalesTaxFiling
{
    public const int MaxReferenceLength = 100;

    private SalesTaxFiling()
    {
        FiledBy = string.Empty;
    }

    public Guid Id { get; private set; }
    public Guid AgencyId { get; private set; }
    public DateOnly PeriodFrom { get; private set; }
    public DateOnly PeriodTo { get; private set; }
    public DateOnly FiledOn { get; private set; }

    /// <summary>The tax the return reported.</summary>
    public decimal AmountReported { get; private set; }

    /// <summary>The agency's confirmation number.</summary>
    public string? Reference { get; private set; }

    public string FiledBy { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    public static SalesTaxFiling Create(Guid agencyId, DateOnly periodFrom, DateOnly periodTo, DateOnly filedOn, decimal amountReported, string? reference, string by, DateTimeOffset at)
    {
        Money.EnsureCents(amountReported);
        if (amountReported < 0m)
            throw new DomainException("taxfiling.amount_negative", "The amount reported can't be below zero.");
        if (filedOn < periodFrom)
            throw new DomainException("taxfiling.before_period", "A return can't be filed before its period starts.");
        return new SalesTaxFiling
        {
            Id = Guid.CreateVersion7(),
            AgencyId = agencyId,
            PeriodFrom = periodFrom,
            PeriodTo = periodTo,
            FiledOn = filedOn,
            AmountReported = amountReported,
            Reference = Text.Optional(reference, MaxReferenceLength, "taxfiling.reference_too_long", "Confirmation number"),
            FiledBy = Text.Optional(by, 100, "taxfiling.by_too_long", "Filed by") ?? "unknown",
            CreatedAt = at.ToUniversalTime(),
        };
    }
}
