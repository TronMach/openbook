using OpenBook.Domain.Common;

namespace OpenBook.Domain.Channels;

/// <summary>Where the channel's payouts come from: a live connection (Stripe, Shopify) or a downloaded report.</summary>
public enum ChannelKind
{
    Shopify,
    Stripe,
    Amazon,
    Etsy,
    Ebay,
    PayPal,

    /// <summary>Any other channel, imported from a CSV with date, type and amount columns.</summary>
    Other,
}

/// <summary>What a payout line is, which decides the account it posts to. Amounts are signed: positive is money to you.</summary>
public enum ChannelLineType
{
    Sale,
    Shipping,

    /// <summary>Sales tax you collected (yours to file and pay).</summary>
    Tax,

    /// <summary>Sales tax the marketplace collected and paid for you (usually netting a <see cref="Tax"/> line to zero).</summary>
    MarketplaceTax,
    Refund,
    Fee,
    Adjustment,
}

/// <param name="DepositAccountId">Where the payout lands: a Bank account, or an Other current asset to clear later.</param>
/// <remarks>The service turns an empty account id into the usual account, created on first use.</remarks>
public sealed record ChannelDetails(
    string Name,
    ChannelKind Kind,
    Guid DepositAccountId,
    Guid SalesAccountId = default,
    Guid ShippingAccountId = default,
    Guid RefundsAccountId = default,
    Guid FeesAccountId = default,
    Guid TaxAccountId = default,
    Guid AdjustmentsAccountId = default);

/// <summary>
/// A place you sell (a Shopify store, an Amazon seller account, an Etsy shop…) and the accounts its payouts post to.
/// Deactivated, never deleted.
/// </summary>
public sealed class SalesChannel
{
    private SalesChannel() => Name = string.Empty;

    public Guid Id { get; private set; }
    public string Name { get; private set; }
    public ChannelKind Kind { get; private set; }
    public Guid DepositAccountId { get; private set; }
    public Guid SalesAccountId { get; private set; }
    public Guid ShippingAccountId { get; private set; }
    public Guid RefundsAccountId { get; private set; }
    public Guid FeesAccountId { get; private set; }
    public Guid TaxAccountId { get; private set; }
    public Guid AdjustmentsAccountId { get; private set; }
    public bool IsActive { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    /// <summary>Accounts are resolved (and created if needed) by the service before this is called.</summary>
    public static SalesChannel Create(ChannelDetails details, DateTimeOffset createdAt)
    {
        var channel = new SalesChannel { Id = Guid.CreateVersion7(), IsActive = true, CreatedAt = createdAt.ToUniversalTime() };
        channel.Update(details);
        return channel;
    }

    public void Update(ChannelDetails details)
    {
        ArgumentNullException.ThrowIfNull(details);
        Name = Text.Optional(details.Name, 100, "channel.name_too_long", "Name")
            ?? throw new DomainException("channel.name_required", "Give the channel a name.");
        Kind = Enum.IsDefined(details.Kind) ? details.Kind : throw new DomainException("channel.kind_invalid", "Unknown kind of channel.");
        if (new[] { details.DepositAccountId, details.SalesAccountId, details.ShippingAccountId, details.RefundsAccountId, details.FeesAccountId,
                details.TaxAccountId, details.AdjustmentsAccountId }.Any(a => a == Guid.Empty))
            throw new DomainException("channel.account_required", "Choose the account for each part of a payout.");
        DepositAccountId = details.DepositAccountId;
        SalesAccountId = details.SalesAccountId;
        ShippingAccountId = details.ShippingAccountId;
        RefundsAccountId = details.RefundsAccountId;
        FeesAccountId = details.FeesAccountId;
        TaxAccountId = details.TaxAccountId;
        AdjustmentsAccountId = details.AdjustmentsAccountId;
    }

    public Guid AccountFor(ChannelLineType type) => type switch
    {
        ChannelLineType.Sale => SalesAccountId,
        ChannelLineType.Shipping => ShippingAccountId,
        ChannelLineType.Tax or ChannelLineType.MarketplaceTax => TaxAccountId,
        ChannelLineType.Refund => RefundsAccountId,
        ChannelLineType.Fee => FeesAccountId,
        _ => AdjustmentsAccountId,
    };

    public void Deactivate() => IsActive = false;

    public void Reactivate() => IsActive = true;
}

/// <summary>One transaction in a payout, as the channel reported it.</summary>
/// <param name="Amount">Signed: positive is money to you (a sale), negative money taken (a fee, a refund).</param>
/// <param name="Region">The ship-to state or region, when the report gives it (for sales tax by state).</param>
public sealed record ChannelLineDraft(DateOnly Date, ChannelLineType Type, decimal Amount, string? Reference = null, string? Description = null, string? Region = null);

public sealed class ChannelPayoutLine
{
    private ChannelPayoutLine() { }

    internal ChannelPayoutLine(int lineNumber, ChannelLineDraft draft)
    {
        LineNumber = lineNumber;
        Date = draft.Date;
        Type = Enum.IsDefined(draft.Type) ? draft.Type : throw new DomainException("channel.line_type_invalid", "Unknown payout line type.");
        Amount = decimal.Round(draft.Amount, 2) == draft.Amount ? draft.Amount : throw new DomainException("channel.amount_cents", "Payout amounts are to the cent.");
        Reference = Trim(draft.Reference, 100);
        Description = Trim(draft.Description, 300);
        Region = Trim(draft.Region, 50)?.ToUpperInvariant();
    }

    public int LineNumber { get; private set; }
    public DateOnly Date { get; private set; }
    public ChannelLineType Type { get; private set; }
    public decimal Amount { get; private set; }
    public string? Reference { get; private set; }
    public string? Description { get; private set; }
    public string? Region { get; private set; }

    private static string? Trim(string? text, int max)
    {
        var t = text?.Trim();
        return string.IsNullOrEmpty(t) ? null : t.Length > max ? t[..max] : t;
    }
}

public enum PayoutStatus
{
    /// <summary>Imported, waiting to be checked and posted.</summary>
    Pending,
    Posted,

    /// <summary>Left out on purpose (a duplicate or a test); posts nothing.</summary>
    Dismissed,
}

/// <summary>
/// A payout (Amazon settlement, Shopify or Stripe payout, an Etsy or PayPal statement period): the sales, shipping,
/// tax, refunds and fees behind one deposit. Imported as Pending; posting makes one journal entry (Dr deposit for the
/// net, each type to its account). Voiding the entry puts it back to Pending. The lines are never edited.
/// </summary>
public sealed class ChannelPayout
{
    public const string Source = "channels.payout";

    private readonly List<ChannelPayoutLine> _lines = [];

    private ChannelPayout() => ExternalId = string.Empty;

    public Guid Id { get; private set; }
    public Guid ChannelId { get; private set; }

    /// <summary>The channel's id for the payout (settlement id, payout id, or file and period); unique per channel.</summary>
    public string ExternalId { get; private set; }

    /// <summary>When the money reached the deposit account (or the end of the period): the posting date.</summary>
    public DateOnly Date { get; private set; }

    public DateOnly? PeriodStart { get; private set; }
    public DateOnly? PeriodEnd { get; private set; }

    /// <summary>The net the channel says it paid, when the report gives it; posting is refused if the lines don't add up to it.</summary>
    public decimal? ReportedNet { get; private set; }

    public PayoutStatus Status { get; private set; }
    public Guid? JournalEntryId { get; private set; }
    public DateTimeOffset ImportedAt { get; private set; }

    public IReadOnlyList<ChannelPayoutLine> Lines => _lines;

    public decimal Net => _lines.Sum(l => l.Amount);

    public decimal Total(ChannelLineType type) => _lines.Where(l => l.Type == type).Sum(l => l.Amount);

    /// <summary>Reported net less the lines' net; zero (or no reported net) when it adds up.</summary>
    public decimal Difference => ReportedNet is { } reported ? reported - Net : 0m;

    public static ChannelPayout Import(
        Guid channelId, string externalId, DateOnly date, IReadOnlyList<ChannelLineDraft> lines, DateTimeOffset importedAt,
        DateOnly? periodStart = null, DateOnly? periodEnd = null, decimal? reportedNet = null)
    {
        ArgumentNullException.ThrowIfNull(lines);
        var payout = new ChannelPayout
        {
            Id = Guid.CreateVersion7(),
            ChannelId = channelId,
            ExternalId = Text.Optional(externalId, 200, "channel.payout_id_too_long", "Payout id")
                ?? throw new DomainException("channel.payout_id_required", "A payout needs an id."),
            Date = date,
            PeriodStart = periodStart,
            PeriodEnd = periodEnd,
            ReportedNet = reportedNet,
            Status = PayoutStatus.Pending,
            ImportedAt = importedAt.ToUniversalTime(),
        };
        if (lines.Count == 0)
            throw new DomainException("channel.payout_empty", "The payout has no transactions.");
        payout._lines.AddRange(lines.Select((l, i) => new ChannelPayoutLine(i + 1, l)));
        return payout;
    }

    /// <summary>
    /// The journal lines: the deposit account takes the net (debit when positive), and each type's total goes to its
    /// account with the opposite sign (sales and shipping credited, fees and refunds debited). Both tax types share
    /// the tax account, so marketplace-collected tax nets out there.
    /// </summary>
    public IReadOnlyList<Ledger.JournalLineDraft> Postings(SalesChannel channel)
    {
        ArgumentNullException.ThrowIfNull(channel);
        var lines = new List<Ledger.JournalLineDraft>();
        if (Net != 0m)
            lines.Add(Net > 0m ? Ledger.JournalLineDraft.Dr(channel.DepositAccountId, Net, "Payout") : Ledger.JournalLineDraft.Cr(channel.DepositAccountId, -Net, "Payout"));
        foreach (var group in _lines.GroupBy(l => channel.AccountFor(l.Type)))
        {
            var amount = group.Sum(l => l.Amount);
            var memo = string.Join(", ", group.Select(l => Label(l.Type)).Distinct());
            if (amount > 0m)
                lines.Add(Ledger.JournalLineDraft.Cr(group.Key, amount, memo));
            else if (amount < 0m)
                lines.Add(Ledger.JournalLineDraft.Dr(group.Key, -amount, memo));
        }

        return lines;
    }

    public void Posted(Guid entryId)
    {
        if (Status != PayoutStatus.Pending)
            throw new DomainException("channel.payout_not_pending", "Only a payout waiting to be posted can be posted.");
        if (Difference != 0m)
            throw new DomainException("channel.payout_unbalanced",
                $"The transactions add up to {Net:N2}, but the channel reported {ReportedNet:N2}. Check the file before posting.");
        Status = PayoutStatus.Posted;
        JournalEntryId = entryId;
    }

    /// <summary>After its entry is voided: waiting again.</summary>
    public void Unpost()
    {
        Status = PayoutStatus.Pending;
        JournalEntryId = null;
    }

    public void Dismiss()
    {
        if (Status == PayoutStatus.Posted)
            throw new DomainException("channel.payout_posted", "Void the payout's entry before dismissing it.");
        Status = PayoutStatus.Dismissed;
    }

    public void Restore()
    {
        if (Status == PayoutStatus.Dismissed)
            Status = PayoutStatus.Pending;
    }

    public static string Label(ChannelLineType type) => type switch
    {
        ChannelLineType.MarketplaceTax => "Tax paid by marketplace",
        ChannelLineType.Tax => "Sales tax",
        _ => type.ToString(),
    };
}
