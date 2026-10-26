using OpenBook.Domain.Common;

namespace OpenBook.Domain.Inventory;

/// <summary>
/// What's known about one lot of an item beyond its movements: its expiry date. Automatic picks take the lot that
/// expires first and skip expired lots; a sale can still name an expired lot on purpose.
/// </summary>
public sealed class LotDetail
{
    private LotDetail() => Lot = string.Empty;

    public Guid ItemId { get; private set; }
    public string Lot { get; private set; }
    public DateOnly? ExpiresOn { get; private set; }

    public static LotDetail For(Guid itemId, string lot)
    {
        var trimmed = lot?.Trim() ?? string.Empty;
        if (trimmed.Length == 0 || trimmed.Length > LotText.MaxLotLength)
            throw new DomainException("lot.invalid", "Name the lot.");
        return new LotDetail { ItemId = itemId, Lot = trimmed };
    }

    public void Expire(DateOnly? on) => ExpiresOn = on;

    /// <summary>Whether the lot can no longer be picked automatically on <paramref name="date"/>.</summary>
    public bool IsExpiredOn(DateOnly date) => ExpiresOn is { } expires && expires < date;
}
