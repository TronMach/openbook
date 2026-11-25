using OpenBook.Domain.Common;

namespace OpenBook.Domain.Companies;

/// <summary>How OpenBook signs in to the mail server.</summary>
public enum SmtpSignIn
{
    /// <summary>A user name and password (or app password), or none.</summary>
    Password,

    /// <summary>Microsoft's sign-in (OAuth), for Microsoft 365 and Outlook.com, which are closing password sign-in.</summary>
    Microsoft,
}

/// <summary>What a mailbox signed in to at Microsoft is for; each has its own sign-in, so they can be different mailboxes.</summary>
public enum MailPurpose
{
    Sending,

    /// <summary>Reading bills people forward to a mailbox.</summary>
    Bills,
}

/// <summary>
/// A mailbox signed in to at Microsoft: the company's own app registration in Microsoft Entra, and the refresh token
/// Microsoft gave for the mailbox. Secrets are stored encrypted by the host's secret protector, never shown or exported.
/// Microsoft may give a new refresh token with any access token, so this row is kept apart from the settings people edit.
/// </summary>
public sealed class MicrosoftMailSignIn
{
    private MicrosoftMailSignIn()
    {
        ClientId = string.Empty;
        Tenant = string.Empty;
    }

    public MailPurpose Purpose { get; private set; }

    /// <summary>The app registration's Application (client) ID.</summary>
    public string ClientId { get; private set; }

    /// <summary>The Directory (tenant) ID or domain; "common" for any account, "consumers" for Outlook.com.</summary>
    public string Tenant { get; private set; }

    /// <summary>The app's client secret; null for one registered as a desktop app, which has none.</summary>
    public string? ProtectedSecret { get; private set; }

    public string? ProtectedRefreshToken { get; private set; }

    /// <summary>Who signed in, as Microsoft names them.</summary>
    public string? Mailbox { get; private set; }

    public DateTimeOffset? ConnectedAt { get; private set; }

    public bool HasApp => ClientId.Length > 0;

    public bool IsConnected => ProtectedRefreshToken is not null;

    public static MicrosoftMailSignIn Create(MailPurpose purpose) => new() { Purpose = purpose, Tenant = "common" };

    /// <param name="protectedSecret">Null keeps the saved secret; "" clears it.</param>
    /// <returns>Whether the app changed, which ends the sign-in: its token belongs to the old app.</returns>
    public bool SetApp(string? clientId, string? tenant, string? protectedSecret)
    {
        var id = (clientId ?? "").Trim();
        if (id.Length > 0 && !Guid.TryParse(id, out _))
            throw new DomainException("email.microsoft_client_id", "The application (client) ID looks like 1b4e28ba-2fa1-11d2-883f-0016d3cca427, as Microsoft Entra shows it.");
        var directory = string.IsNullOrWhiteSpace(tenant) ? "common" : tenant.Trim().ToLowerInvariant();
        if (directory.Length > 100 || !directory.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '.'))
            throw new DomainException("email.microsoft_tenant", "The directory (tenant) ID is as Microsoft Entra shows it, or your domain.");
        var changed = !string.Equals(id, ClientId, StringComparison.OrdinalIgnoreCase) || directory != Tenant;
        ClientId = id.ToLowerInvariant();
        Tenant = directory;
        if (protectedSecret is not null)
            ProtectedSecret = protectedSecret.Length == 0 ? null : protectedSecret;
        else if (changed)
            ProtectedSecret = null;
        if (changed)
            Disconnect();
        return changed;
    }

    public void Connect(string protectedRefreshToken, string? mailbox, DateTimeOffset at)
    {
        ArgumentException.ThrowIfNullOrEmpty(protectedRefreshToken);
        ProtectedRefreshToken = protectedRefreshToken;
        Mailbox = Text.Optional(mailbox, 320, "email.microsoft_mailbox", "Mailbox");
        ConnectedAt = at.ToUniversalTime();
    }

    /// <summary>Microsoft gave a newer refresh token with an access token.</summary>
    public void Renew(string protectedRefreshToken)
    {
        if (IsConnected)
            ProtectedRefreshToken = protectedRefreshToken;
    }

    public void Disconnect() => (ProtectedRefreshToken, Mailbox, ConnectedAt) = (null, null, null);
}
