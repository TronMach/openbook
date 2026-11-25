using OpenBook.Domain.Common;
using OpenBook.Domain.Sales;

namespace OpenBook.Domain.Nonprofit;

public enum DonationCheckoutStatus
{
    Open,
    Paid,
    Expired,
}

/// <summary>
/// A gift started on the public giving page (C091): the processor's checkout session for it, and the gift recorded once
/// the processor reports it paid. Recorded once, however often the processor reports it.
/// </summary>
public sealed class DonationCheckout
{
    public const int MaxNameLength = 100;
    public const int MaxEmailLength = 200;
    public const int MaxNoteLength = 500;
    public const decimal MinAmount = 1m;
    public const decimal MaxAmount = 100_000m;

    private DonationCheckout()
    {
        SessionId = Url = Name = Email = string.Empty;
    }

    public Guid Id { get; private set; }
    public string SessionId { get; private set; }
    /// <summary>Whose checkout it is; null on those from before this was kept (the provider then set).</summary>
    public PaymentProvider? Provider { get; private set; }

    /// <summary>The provider's id for the link itself, where it differs from the checkout's (Square: deleting it cancels the order).</summary>
    public string? ProviderLinkId { get; private set; }

    public string Url { get; private set; }
    public decimal Amount { get; private set; }
    public string Name { get; private set; }
    public string Email { get; private set; }
    public string? Note { get; private set; }
    public DonationCheckoutStatus Status { get; private set; }

    /// <summary>The gift recorded for it, once paid.</summary>
    public Guid? DonationId { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    /// <summary>Checks what a donor typed on the public page, before the processor is asked for a checkout.</summary>
    public static (string Name, string Email, string? Note) Check(string? name, string? email, decimal amount, string? note)
    {
        var cleanName = Text.Optional(name, MaxNameLength, "giving.name_too_long", "Your name") ?? throw new DomainException("giving.name_required", "Enter your name.");
        var cleanEmail = Text.Optional(email, MaxEmailLength, "giving.email_too_long", "Your email");
        if (cleanEmail is null || !System.Text.RegularExpressions.Regex.IsMatch(cleanEmail, @"^[^@\s]+@[^@\s]+\.[^@\s]+$"))
            throw new DomainException("giving.email", "Enter your email address, so we can send your receipt.");
        if (amount < MinAmount || amount > MaxAmount || decimal.Round(amount, 2) != amount)
            throw new DomainException("giving.amount", $"Give at least {MinAmount:0.00} and at most {MaxAmount:N0}, in cents.");
        return (cleanName, cleanEmail, Text.Optional(note, MaxNoteLength, "giving.note_too_long", "Your note"));
    }

    public static DonationCheckout Create(Guid id, PaymentProvider provider, string sessionId, string url, decimal amount, string? name, string? email, string? note,
        DateTimeOffset createdAt, string? providerLinkId = null)
    {
        var (cleanName, cleanEmail, cleanNote) = Check(name, email, amount, note);
        return new DonationCheckout
        {
            Id = id,
            Provider = provider,
            ProviderLinkId = providerLinkId,
            SessionId = string.IsNullOrWhiteSpace(sessionId) ? throw new DomainException("giving.session", "The processor didn't start a checkout.") : sessionId,
            Url = url,
            Amount = amount,
            Name = cleanName,
            Email = cleanEmail,
            Note = cleanNote,
            Status = DonationCheckoutStatus.Open,
            CreatedAt = createdAt.ToUniversalTime(),
        };
    }

    /// <summary>Marked before its gift is saved, so a second report of the same payment is refused by then.</summary>
    public void MarkPaid()
    {
        if (Status == DonationCheckoutStatus.Paid)
            throw new DomainException("giving.already_recorded", "This gift is recorded already.");
        Status = DonationCheckoutStatus.Paid;
    }

    public void RecordGift(Guid donationId) => DonationId = donationId;

    public void Expire()
    {
        if (Status == DonationCheckoutStatus.Open)
            Status = DonationCheckoutStatus.Expired;
    }
}
