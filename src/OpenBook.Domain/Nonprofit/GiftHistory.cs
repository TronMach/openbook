using OpenBook.Domain.Common;

namespace OpenBook.Domain.Nonprofit;

/// <summary>
/// A gift recorded in the old system before the move (C080): kept for the donor's history and their giving statements,
/// never posted (its money is in the opening balances). Imported from a CSV; removed freely.
/// </summary>
public sealed class GiftHistory
{
    public const int MaxTextLength = Donation.MaxTextLength;

    private GiftHistory()
    {
    }

    public Guid Id { get; private set; }
    public Guid DonorId { get; private set; }
    public DateOnly Date { get; private set; }

    /// <summary>The money given; zero for a gift in kind, which is described, not valued.</summary>
    public decimal Amount { get; private set; }

    public GiftKind Kind { get; private set; }
    public string? Description { get; private set; }
    public string? Reference { get; private set; }

    /// <summary>What the donor received in return, valued, as on a gift.</summary>
    public decimal GoodsValue { get; private set; }

    public string? GoodsDescription { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    public decimal Deductible => Kind == GiftKind.InKind ? 0m : Math.Max(0m, Amount - GoodsValue);

    public static GiftHistory Create(Guid donorId, DateOnly date, decimal amount, GiftKind kind, string? description, string? reference,
        decimal goodsValue, string? goodsDescription, DateTimeOffset createdAt)
    {
        if (!Enum.IsDefined(kind))
            throw new DomainException("gift_history.kind", "A gift is money or in kind.");
        if (kind == GiftKind.Cash && (amount <= 0m || amount > Money.MaxAmount || decimal.Round(amount, 2) != amount))
            throw new DomainException("gift_history.amount", "A money gift's amount is above zero, in cents.");
        if (kind == GiftKind.InKind && amount != 0m && (amount < 0m || amount > Money.MaxAmount || decimal.Round(amount, 2) != amount))
            throw new DomainException("gift_history.amount", "An amount is zero or more, in cents.");
        if (goodsValue < 0m || goodsValue > Money.MaxAmount || decimal.Round(goodsValue, 2) != goodsValue || (kind == GiftKind.Cash && goodsValue > amount))
            throw new DomainException("gift_history.goods_value", "What the donor received is valued from zero up to the gift, in cents.");
        if (goodsValue > 0m && string.IsNullOrWhiteSpace(goodsDescription))
            throw new DomainException("gift_history.goods_description", "Say what the donor received in return.");
        return new GiftHistory
        {
            Id = Guid.CreateVersion7(),
            DonorId = donorId == Guid.Empty ? throw new DomainException("gift_history.donor", "A gift needs its donor.") : donorId,
            Date = date,
            Amount = amount,
            Kind = kind,
            Description = Text.Optional(description, MaxTextLength, "gift_history.description_too_long", "Description"),
            Reference = Text.Optional(reference, Donation.MaxReferenceLength, "gift_history.reference_too_long", "Reference"),
            GoodsValue = goodsValue,
            GoodsDescription = Text.Optional(goodsDescription, MaxTextLength, "gift_history.goods_too_long", "What the donor received"),
            CreatedAt = createdAt.ToUniversalTime(),
        };
    }
}
