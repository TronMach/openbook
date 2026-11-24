using OpenBook.Domain.Common;

namespace OpenBook.Domain.Nonprofit;

/// <summary>
/// Donors who give as one (a couple, a family): their year's giving goes out as one statement and letter, addressed by
/// <see cref="Salutation"/> to the <see cref="PrimaryDonorId"/>'s address and email (C622). Each member keeps their own
/// gifts, receipts and history; a household posts nothing.
/// </summary>
public sealed class Household
{
    public const int MaxNameLength = 100;
    public const int MaxMembers = 20;

    private readonly List<HouseholdMember> _members = [];

    private Household()
    {
    }

    public Guid Id { get; private set; }

    /// <summary>How lists and reports name it ("The Smith household").</summary>
    public string Name { get; private set; } = "";

    /// <summary>Who the statement is addressed to ("Pat and Sam Smith"); the name when blank.</summary>
    public string? Salutation { get; private set; }

    /// <summary>The member whose address and email the household's statement goes to.</summary>
    public Guid PrimaryDonorId { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public IReadOnlyList<HouseholdMember> Members => _members;

    public string AddressedTo => Salutation ?? Name;

    public static Household Create(string? name, string? salutation, Guid primaryDonorId, DateTimeOffset createdAt)
    {
        var household = new Household { Id = Guid.CreateVersion7(), CreatedAt = createdAt.ToUniversalTime() };
        household.Rename(name, salutation);
        household.Add(primaryDonorId);
        household.PrimaryDonorId = primaryDonorId;
        return household;
    }

    public void Rename(string? name, string? salutation)
    {
        Name = Text.Optional(name, MaxNameLength, "household.name_too_long", "Household name")
               ?? throw new DomainException("household.name_required", "Give the household a name, such as \"The Smith household\".");
        Salutation = Text.Optional(salutation, MaxNameLength, "household.salutation_too_long", "Addressed to");
    }

    public bool Has(Guid donorId) => _members.Any(m => m.DonorId == donorId);

    public void Add(Guid donorId)
    {
        if (donorId == Guid.Empty)
            throw new DomainException("household.donor_required", "Choose the donor to add.");
        if (Has(donorId))
            return;
        if (_members.Count >= MaxMembers)
            throw new DomainException("household.too_many", $"A household has at most {MaxMembers} members.");
        _members.Add(new HouseholdMember(Id, donorId));
    }

    /// <summary>Takes a member out; the primary one only once another is primary, and never the last one (remove the household instead).</summary>
    public void Remove(Guid donorId)
    {
        if (_members.FirstOrDefault(m => m.DonorId == donorId) is not { } member)
            return;
        if (_members.Count == 1)
            throw new DomainException("household.last_member", "That's the only member: remove the household instead.");
        if (donorId == PrimaryDonorId)
            throw new DomainException("household.primary_member", "Make another member the one statements go to first.");
        _members.Remove(member);
    }

    public void MakePrimary(Guid donorId) =>
        PrimaryDonorId = Has(donorId) ? donorId : throw new DomainException("household.not_member", "Statements go to one of the household's members.");
}

/// <summary>One donor in a household; a donor is in one household at most.</summary>
public sealed class HouseholdMember
{
    private HouseholdMember()
    {
    }

    internal HouseholdMember(Guid householdId, Guid donorId)
    {
        Id = Guid.CreateVersion7();
        HouseholdId = householdId;
        DonorId = donorId;
    }

    public Guid Id { get; private set; }
    public Guid HouseholdId { get; private set; }
    public Guid DonorId { get; private set; }
}

/// <summary>
/// Recognition for a gift someone else paid for (C622): a spouse, a fundraiser who brought it in, a company's
/// employee. It shows on donor reports and recognition lists, never on receipts, statements or letters, which are the
/// payer's; it posts nothing.
/// </summary>
public sealed class SoftCredit
{
    public const int MaxNoteLength = 100;

    private SoftCredit()
    {
    }

    public Guid Id { get; private set; }
    public Guid DonationId { get; private set; }

    /// <summary>The donor credited (not the one who paid).</summary>
    public Guid DonorId { get; private set; }

    /// <summary>How much of the gift they're credited with, up to all of it.</summary>
    public decimal Amount { get; private set; }

    /// <summary>Why ("Spouse", "Raised it at the gala").</summary>
    public string? Note { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    /// <summary>The gift's amount came down: no more than all of it is credited. False when it already wasn't.</summary>
    public bool LimitTo(decimal giftAmount)
    {
        if (Amount <= giftAmount)
            return false;
        Amount = giftAmount;
        return true;
    }

    public static SoftCredit Create(Donation gift, Guid donorId, decimal? amount, string? note, DateTimeOffset createdAt)
    {
        ArgumentNullException.ThrowIfNull(gift);
        if (gift.Status != DocumentStatus.Posted)
            throw new DomainException("soft_credit.gift_voided", "This gift is voided.");
        if (donorId == Guid.Empty)
            throw new DomainException("soft_credit.donor_required", "Choose who to credit.");
        if (donorId == gift.DonorId)
            throw new DomainException("soft_credit.own_gift", "The gift is already theirs: credit someone else.");
        var credited = amount ?? gift.Amount;
        if (credited <= 0m || credited > gift.Amount || decimal.Round(credited, 2) != credited)
            throw new DomainException("soft_credit.amount", "Credit an amount above zero, up to the gift, in cents.");
        return new SoftCredit
        {
            Id = Guid.CreateVersion7(),
            DonationId = gift.Id,
            DonorId = donorId,
            Amount = credited,
            Note = Text.Optional(note, MaxNoteLength, "soft_credit.note_too_long", "Note"),
            CreatedAt = createdAt.ToUniversalTime(),
        };
    }
}
