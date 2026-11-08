using OpenBook.Domain.Common;

namespace OpenBook.Domain.Accounts;

/// <summary>
/// A partner (or member) of a partnership or LLC: their share of profit and loss in percent and the equity account their
/// capital is kept in. Switched off, never deleted.
/// </summary>
public sealed class Partner
{
    private Partner() => Name = string.Empty;

    public Guid Id { get; private set; }
    public string Name { get; private set; }

    /// <summary>Their share of profit and loss, percent to four decimals.</summary>
    public decimal SharePercent { get; private set; }

    /// <summary>The equity account their capital is kept in.</summary>
    public Guid CapitalAccountId { get; private set; }

    public bool IsActive { get; private set; }

    public static Partner Create(string name, decimal sharePercent, Guid capitalAccountId)
    {
        var partner = new Partner { Id = Guid.CreateVersion7(), IsActive = true };
        partner.Update(name, sharePercent, capitalAccountId);
        return partner;
    }

    public void Update(string name, decimal sharePercent, Guid capitalAccountId)
    {
        Name = Text.Optional(name, 100, "partner.name_too_long", "Name") ?? throw new DomainException("partner.name_required", "Enter the partner's name.");
        SharePercent = sharePercent is > 0m and <= 100m && decimal.Round(sharePercent, 4) == sharePercent
            ? sharePercent
            : throw new DomainException("partner.share", "A partner's share is above 0 and up to 100 percent, to four decimals.");
        CapitalAccountId = capitalAccountId == Guid.Empty ? throw new DomainException("partner.account_required", "Choose the partner's capital account.") : capitalAccountId;
    }

    public void SetActive(bool active) => IsActive = active;
}

/// <summary>
/// A fiscal year's net income (or loss) split among the partners: Dr Retained Earnings / Cr each partner's capital (the
/// reverse for a loss), dated the year's last day. One standing allocation per year; voided to redo it.
/// </summary>
public sealed class PartnerAllocation
{
    public const string Source = "partners.allocation";

    private PartnerAllocation()
    {
    }

    public Guid Id { get; private set; }
    public DateOnly YearEnd { get; private set; }
    public decimal NetIncome { get; private set; }
    public Guid JournalEntryId { get; private set; }
    public bool IsVoided { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    public static PartnerAllocation Create(Guid id, DateOnly yearEnd, decimal netIncome, Guid journalEntryId, DateTimeOffset createdAt) =>
        new() { Id = id, YearEnd = yearEnd, NetIncome = netIncome, JournalEntryId = journalEntryId, CreatedAt = createdAt.ToUniversalTime() };

    public void Void()
    {
        if (IsVoided)
            throw new DomainException("partners.allocation_voided", "This allocation is already voided.");
        IsVoided = true;
    }
}
