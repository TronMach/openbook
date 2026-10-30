using OpenBook.Domain.Common;

namespace OpenBook.Domain.Sales;

public enum ScheduledEmailStatus
{
    Scheduled,
    Sent,
    Failed,
    Cancelled,
}

/// <summary>
/// An invoice email to send later: the message as written when it was scheduled, sent with the invoice PDF as it
/// stands at <see cref="SendAt"/>. Sent, failed (with the reason; can be tried again) or cancelled; never deleted.
/// </summary>
public sealed class ScheduledEmail
{
    public const int MaxSubject = 300;
    public const int MaxBody = 20000;

    private ScheduledEmail()
    {
        To = string.Empty;
        Subject = string.Empty;
        Body = string.Empty;
        ScheduledBy = string.Empty;
    }

    public Guid Id { get; private set; }
    public Guid InvoiceId { get; private set; }
    public string To { get; private set; }
    public string Subject { get; private set; }
    public string Body { get; private set; }

    /// <summary>When to send (UTC).</summary>
    public DateTimeOffset SendAt { get; private set; }

    public ScheduledEmailStatus Status { get; private set; }
    public string ScheduledBy { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset? SentAt { get; private set; }

    /// <summary>Why the last attempt failed, or why it was cancelled without sending.</summary>
    public string? Error { get; private set; }

    public bool IsPending => Status == ScheduledEmailStatus.Scheduled;

    public static ScheduledEmail Create(Guid invoiceId, string to, string? subject, string? body, DateTimeOffset sendAt, DateTimeOffset now, string actor)
    {
        if (string.IsNullOrWhiteSpace(to))
            throw new DomainException("email.to_required", "Enter the customer's email address.");
        if (sendAt <= now)
            throw new DomainException("scheduled_email.in_past", "Choose a time later than now.");
        if (sendAt > now.AddYears(1))
            throw new DomainException("scheduled_email.too_far", "Schedule the email within a year.");
        subject = subject?.Trim() ?? "";
        if (subject.Length > MaxSubject)
            throw new DomainException("email.subject_too_long", $"The subject can have at most {MaxSubject} characters.");
        if ((body ?? "").Length > MaxBody)
            throw new DomainException("scheduled_email.body_too_long", $"The message can have at most {MaxBody:N0} characters.");
        return new ScheduledEmail
        {
            Id = Guid.CreateVersion7(),
            InvoiceId = invoiceId,
            To = to.Trim(),
            Subject = subject,
            Body = body ?? "",
            SendAt = sendAt.ToUniversalTime(),
            Status = ScheduledEmailStatus.Scheduled,
            ScheduledBy = actor,
            CreatedAt = now.ToUniversalTime(),
        };
    }

    public bool IsDue(DateTimeOffset now) => IsPending && SendAt <= now;

    public void MarkSent(DateTimeOffset now)
    {
        EnsurePending();
        Status = ScheduledEmailStatus.Sent;
        SentAt = now.ToUniversalTime();
        Error = null;
    }

    public void MarkFailed(string error)
    {
        EnsurePending();
        Status = ScheduledEmailStatus.Failed;
        Error = error;
    }

    /// <param name="reason">Why it won't be sent when OpenBook cancels it (an invoice voided since); null when someone cancels.</param>
    public void Cancel(string? reason = null)
    {
        if (Status is not (ScheduledEmailStatus.Scheduled or ScheduledEmailStatus.Failed))
            throw new DomainException("scheduled_email.not_pending", "Only an email that hasn't been sent can be cancelled.");
        Status = ScheduledEmailStatus.Cancelled;
        Error = reason;
    }

    /// <summary>Puts a failed email back in the queue, to go at <paramref name="sendAt"/>.</summary>
    public void Retry(DateTimeOffset sendAt)
    {
        if (Status != ScheduledEmailStatus.Failed)
            throw new DomainException("scheduled_email.not_failed", "Only a failed email can be tried again.");
        Status = ScheduledEmailStatus.Scheduled;
        SendAt = sendAt.ToUniversalTime();
        Error = null;
    }

    private void EnsurePending()
    {
        if (!IsPending)
            throw new DomainException("scheduled_email.not_pending", "This email isn't waiting to be sent.");
    }
}
