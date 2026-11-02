using OpenBook.Domain.Common;

namespace OpenBook.Domain.Companies;

/// <summary>
/// Which server process runs a background job for now, so two copies of OpenBook on one database (two containers
/// behind a load balancer) don't both send the same reminders. The holder renews it on every run; when it stops,
/// another takes over once <see cref="UntilUnixMs"/> has passed. Not audited: it's housekeeping, not the books.
/// </summary>
public sealed class JobLease
{
    public const int MaxNameLength = 100;
    public const int MaxHolderLength = 200;

    private JobLease()
    {
        Name = string.Empty;
        Holder = string.Empty;
    }

    public JobLease(string name, string holder, DateTimeOffset until)
    {
        Name = Text.Optional(name, MaxNameLength, "lease.name", "Job name") ?? throw new DomainException("lease.name", "A lease needs a job name.");
        Holder = Text.Optional(holder, MaxHolderLength, "lease.holder", "Holder") ?? throw new DomainException("lease.holder", "A lease needs a holder.");
        UntilUnixMs = until.ToUnixTimeMilliseconds();
    }

    public string Name { get; private set; }
    public string Holder { get; private set; }

    /// <summary>When the lease lapses, in Unix milliseconds (SQLite can't compare DateTimeOffsets in a query).</summary>
    public long UntilUnixMs { get; private set; }
}

/// <summary>
/// An API write sent with an <c>Idempotency-Key</c> header: the key (per caller), what was asked (a hash of the method,
/// address and body) and, once done, the response, so the same request sent again (a retry after a timeout) gets
/// the same answer instead of making a second invoice. <see cref="StatusCode"/> 0 means still running. Kept a day.
/// Not audited, and left out of the data export (responses can hold anything the API returns).
/// </summary>
public sealed class IdempotentRequest
{
    public const int MaxKeyLength = 100;
    public const int MaxBodyBytes = 1024 * 1024;

    /// <summary>How long a key is remembered.</summary>
    public static readonly TimeSpan KeptFor = TimeSpan.FromDays(1);

    private IdempotentRequest()
    {
        Key = string.Empty;
        Caller = string.Empty;
        RequestHash = string.Empty;
    }

    public IdempotentRequest(string key, string caller, string requestHash, DateTimeOffset now)
    {
        Id = Guid.CreateVersion7();
        Key = key;
        Caller = caller;
        RequestHash = requestHash;
        CreatedUnixMs = now.ToUnixTimeMilliseconds();
    }

    public Guid Id { get; private set; }
    public string Key { get; private set; }

    /// <summary>Who sent it (the signed-in name, or the API key's), so callers' keys never meet.</summary>
    public string Caller { get; private set; }

    /// <summary>SHA-256 (hex) of the method, path, query and body.</summary>
    public string RequestHash { get; private set; }

    public long CreatedUnixMs { get; private set; }
    public int StatusCode { get; private set; }
    public string? ContentType { get; private set; }
    public byte[]? Body { get; private set; }

    public bool IsDone => StatusCode != 0;

    public void Complete(int statusCode, string? contentType, byte[] body)
    {
        ArgumentNullException.ThrowIfNull(body);
        StatusCode = statusCode;
        ContentType = contentType;
        Body = body;
    }
}
