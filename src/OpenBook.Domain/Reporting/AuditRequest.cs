using OpenBook.Domain.Common;

namespace OpenBook.Domain.Reporting;

public enum AuditRequestStatus
{
    Open,
    Received,
    NotNeeded,
}

/// <param name="Year">The fiscal year the request is for, by the calendar year it ends in.</param>
/// <param name="Owner">Who prepares it: a person or a team.</param>
public sealed record AuditRequestDetails(int Year, string Item, string? Owner = null, DateOnly? DueOn = null, string? Notes = null);

/// <summary>
/// One item on the "prepared by client" (PBC) list an auditor or reviewer hands over for a year: a schedule, a
/// reconciliation, a set of documents. Tracks who prepares it, when it's due and whether it went over (C306). Posts
/// nothing; deleted freely.
/// </summary>
public sealed class AuditRequest
{
    private AuditRequest() => Item = string.Empty;

    public Guid Id { get; private set; }
    public int Year { get; private set; }
    public string Item { get; private set; }
    public string? Owner { get; private set; }
    public DateOnly? DueOn { get; private set; }
    public string? Notes { get; private set; }
    public AuditRequestStatus Status { get; private set; }

    /// <summary>When it was handed over (received) or set aside (not needed).</summary>
    public DateOnly? DoneOn { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public static AuditRequest Create(AuditRequestDetails details, DateTimeOffset createdAt)
    {
        var request = new AuditRequest { Id = Guid.CreateVersion7(), CreatedAt = createdAt.ToUniversalTime(), Status = AuditRequestStatus.Open };
        request.Update(details);
        return request;
    }

    public void Update(AuditRequestDetails details)
    {
        Year = details.Year is >= 1900 and <= 9999 ? details.Year : throw new DomainException("audit_request.year", "Enter the year as four digits.");
        Item = Text.Optional(details.Item, 200, "audit_request.item_too_long", "Item") ?? throw new DomainException("audit_request.item_required", "Say what's asked for.");
        Owner = Text.Optional(details.Owner, 100, "audit_request.owner_too_long", "Owner");
        DueOn = details.DueOn;
        Notes = Text.Optional(details.Notes, 1000, "audit_request.notes_too_long", "Notes");
    }

    public void SetStatus(AuditRequestStatus status, DateOnly today)
    {
        if (!Enum.IsDefined(status))
            throw new DomainException("audit_request.status", "Choose open, received or not needed.");
        if (status == Status)
            return;
        Status = status;
        DoneOn = status == AuditRequestStatus.Open ? null : today;
    }

    public bool IsOverdue(DateOnly today) => Status == AuditRequestStatus.Open && DueOn is { } due && due < today;
}
