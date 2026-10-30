using OpenBook.Domain.Common;

namespace OpenBook.Domain.Channels;

/// <summary>
/// Items from posted channel orders that came back into stock. The refund money is in the payouts; this only puts the
/// stock back, at the item's latest sale cost as a credit memo does (Dr inventory asset / Cr cost of goods sold).
/// Voided, never edited. An order batch can't be voided while returns from it stand.
/// </summary>
public sealed class ChannelReturn
{
    public const string Source = "channels.return";
    public const int MaxMemoLength = 200;

    private readonly List<ChannelReturnLine> _lines = [];

    private ChannelReturn()
    {
    }

    public Guid Id { get; private set; }
    public Guid ChannelId { get; private set; }
    public Guid BatchId { get; private set; }
    public DateOnly Date { get; private set; }
    public string? Memo { get; private set; }
    public DocumentStatus Status { get; private set; }

    /// <summary>Null when nothing had a cost to put back.</summary>
    public Guid? JournalEntryId { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }
    public IReadOnlyList<ChannelReturnLine> Lines => _lines;

    /// <param name="lines">Order line numbers of the batch and the quantity back.</param>
    public static ChannelReturn Create(
        Guid id, Guid channelId, Guid batchId, DateOnly date, IReadOnlyList<(int OrderLine, decimal Quantity)> lines, string? memo, Guid? entryId,
        DateTimeOffset createdAt)
    {
        ArgumentNullException.ThrowIfNull(lines);
        if (lines.Count == 0 || lines.All(l => l.Quantity == 0m))
            throw new DomainException("channel.return_empty", "Enter how many of something came back.");
        if (lines.Any(l => l.Quantity < 0m))
            throw new DomainException("channel.return_quantity", "Quantities returned are above zero.");
        var created = new ChannelReturn
        {
            Id = id,
            ChannelId = channelId,
            BatchId = batchId,
            Date = date,
            Memo = Text.Optional(memo, MaxMemoLength, "channel.return_memo_too_long", "Memo"),
            Status = DocumentStatus.Posted,
            JournalEntryId = entryId,
            CreatedAt = createdAt.ToUniversalTime(),
        };
        created._lines.AddRange(lines.Where(l => l.Quantity > 0m).Select((l, i) => new ChannelReturnLine(i + 1, l.OrderLine, l.Quantity)));
        return created;
    }

    public void Void()
    {
        if (Status == DocumentStatus.Voided)
            throw new DomainException("channel.return_voided", "This return is already voided.");
        Status = DocumentStatus.Voided;
    }
}

public sealed class ChannelReturnLine
{
    private ChannelReturnLine()
    {
    }

    internal ChannelReturnLine(int lineNumber, int orderLine, decimal quantity)
    {
        LineNumber = lineNumber;
        OrderLine = orderLine;
        Quantity = quantity;
    }

    public int LineNumber { get; private set; }

    /// <summary>The <see cref="ChannelOrderLine.LineNumber"/> in the batch.</summary>
    public int OrderLine { get; private set; }

    public decimal Quantity { get; private set; }
}
