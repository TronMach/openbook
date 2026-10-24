using OpenBook.Domain.Common;

namespace OpenBook.Domain.Webhooks;

/// <summary>
/// An HTTP endpoint told about changes. Events come from the audit log, so every change that's audited can be
/// sent: their type is the audit entity type and action, e.g. "invoice.created" or "bill.voided".
/// <see cref="Cursor"/> is the last audit record looked at, so each change is sent once per subscription.
/// </summary>
public sealed class WebhookSubscription
{
    public const int MaxEvents = 30;

    private WebhookSubscription()
    {
        Url = string.Empty;
        Events = string.Empty;
        ProtectedSecret = string.Empty;
    }

    public Guid Id { get; private set; }
    public string Url { get; private set; }
    public string? Description { get; private set; }

    /// <summary>Comma-separated patterns: "*", "invoice.*", "*.voided" or "invoice.created".</summary>
    public string Events { get; private set; }

    /// <summary>The signing secret, encrypted by the host.</summary>
    public string ProtectedSecret { get; private set; }

    public bool IsActive { get; private set; }
    public long Cursor { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    public static WebhookSubscription Create(string url, string? events, string? description, string protectedSecret, long cursor, DateTimeOffset createdAt)
    {
        var subscription = new WebhookSubscription
        {
            Id = Guid.CreateVersion7(),
            ProtectedSecret = protectedSecret,
            Cursor = cursor,
            IsActive = true,
            CreatedAt = createdAt.ToUniversalTime(),
        };
        subscription.Update(url, events, description);
        return subscription;
    }

    public void Update(string url, string? events, string? description)
    {
        Url = NormalizeUrl(url);
        Events = string.Join(',', NormalizeEvents(events));
        Description = Text.Optional(description, 200, "webhook.description_too_long", "Description");
    }

    public void RotateSecret(string protectedSecret) => ProtectedSecret = protectedSecret;

    public void Deactivate() => IsActive = false;

    /// <summary>Picks up from <paramref name="cursor"/>: changes made while it was off aren't sent.</summary>
    public void Reactivate(long cursor)
    {
        IsActive = true;
        Cursor = Math.Max(Cursor, cursor);
    }

    public void Advance(long cursor) => Cursor = Math.Max(Cursor, cursor);

    public bool Matches(string eventType) => Events.Split(',').Any(p => Matches(p, eventType));

    private static bool Matches(string pattern, string eventType)
    {
        if (pattern == "*")
            return true;
        var (pEntity, pAction) = Split(pattern);
        var (entity, action) = Split(eventType);
        return (pEntity == "*" || pEntity == entity) && (pAction == "*" || pAction == action);
    }

    private static (string, string) Split(string value)
    {
        var dot = value.IndexOf('.', StringComparison.Ordinal);
        return dot < 0 ? (value, "*") : (value[..dot], value[(dot + 1)..]);
    }

    /// <summary>An absolute http(s) URL without user name or password.</summary>
    public static string NormalizeUrl(string? url)
    {
        if (!Uri.TryCreate(url?.Trim(), UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http") || uri.Host.Length == 0)
            throw new DomainException("webhook.url_invalid", "Enter the full http:// or https:// address to send events to.");
        if (uri.UserInfo.Length > 0)
            throw new DomainException("webhook.url_credentials", "Don't put a user name or password in the address; use the signing secret to check requests.");
        if (uri.AbsoluteUri.Length > 500)
            throw new DomainException("webhook.url_too_long", "That address is too long.");
        return uri.AbsoluteUri;
    }

    /// <summary>Lower-case, de-duplicated patterns; empty means every event.</summary>
    public static IReadOnlyList<string> NormalizeEvents(string? events)
    {
        var patterns = (events ?? string.Empty)
            .Split([',', ' ', '\n', '\r', ';'], StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Trim().ToLowerInvariant())
            .Distinct()
            .ToList();
        if (patterns.Count == 0)
            return ["*"];
        if (patterns.Count > MaxEvents)
            throw new DomainException("webhook.too_many_events", $"Choose at most {MaxEvents} event patterns.");
        foreach (var p in patterns)
        {
            var parts = p.Split('.');
            if (p != "*" && (parts.Length != 2 || parts.Any(x => x.Length == 0 || !(x == "*" || x.All(c => char.IsAsciiLetterOrDigit(c) || c == '_')))))
                throw new DomainException("webhook.event_invalid", $"'{p}' isn't an event pattern. Use forms like invoice.created, invoice.* or *.voided.");
        }

        return patterns.Contains("*") ? ["*"] : patterns;
    }
}

public enum WebhookDeliveryStatus
{
    Pending,
    Succeeded,

    /// <summary>Gave up after <see cref="WebhookDelivery.MaxAttempts"/> tries; can be redelivered by hand.</summary>
    Failed,
}

/// <summary>One event for one subscription, with its attempts. Retried with growing delays until it succeeds or runs out.</summary>
public sealed class WebhookDelivery
{
    public const int MaxAttempts = 6;
    private static readonly TimeSpan[] Backoff =
        [TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(30), TimeSpan.FromHours(2), TimeSpan.FromHours(12)];

    private WebhookDelivery()
    {
        EventType = string.Empty;
        Payload = string.Empty;
    }

    public Guid Id { get; private set; }
    public Guid SubscriptionId { get; private set; }
    public string EventType { get; private set; }

    /// <summary>The audit record it reports; null for a test ping.</summary>
    public long? AuditRecordId { get; private set; }

    /// <summary>The JSON body, fixed when created so retries send the same thing.</summary>
    public string Payload { get; private set; }

    public WebhookDeliveryStatus Status { get; private set; }
    public int Attempts { get; private set; }
    public DateTimeOffset NextAttemptAt { get; private set; }
    public DateTimeOffset? LastAttemptAt { get; private set; }
    public int? LastStatusCode { get; private set; }
    public string? LastError { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    public static WebhookDelivery Create(Guid id, Guid subscriptionId, string eventType, long? auditRecordId, string payload, DateTimeOffset now) => new()
    {
        Id = id,
        SubscriptionId = subscriptionId,
        EventType = eventType,
        AuditRecordId = auditRecordId,
        Payload = payload,
        Status = WebhookDeliveryStatus.Pending,
        NextAttemptAt = now.ToUniversalTime(),
        CreatedAt = now.ToUniversalTime(),
    };

    /// <summary>2xx responses succeed; anything else (or no response) is retried.</summary>
    public void RecordAttempt(DateTimeOffset now, int? statusCode, string? error)
    {
        Attempts++;
        LastAttemptAt = now.ToUniversalTime();
        LastStatusCode = statusCode;
        if (statusCode is >= 200 and < 300)
        {
            Status = WebhookDeliveryStatus.Succeeded;
            LastError = null;
            return;
        }

        var message = error ?? (statusCode is { } code ? $"HTTP {code}" : "No response");
        LastError = message.Length > 500 ? message[..500] : message;
        if (Attempts >= MaxAttempts)
            Status = WebhookDeliveryStatus.Failed;
        else
            NextAttemptAt = LastAttemptAt.Value + Backoff[Math.Min(Attempts - 1, Backoff.Length - 1)];
    }

    /// <summary>Sends it again at the next run, with a fresh set of attempts.</summary>
    public void Redeliver(DateTimeOffset now)
    {
        Status = WebhookDeliveryStatus.Pending;
        Attempts = 0;
        NextAttemptAt = now.ToUniversalTime();
    }
}
