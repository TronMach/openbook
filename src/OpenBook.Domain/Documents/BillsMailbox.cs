using OpenBook.Domain.Common;
using OpenBook.Domain.Companies;

namespace OpenBook.Domain.Documents;

/// <summary>
/// The mailbox bills are forwarded to (one row): its attachments become documents in the inbox for a person to check.
/// OpenBook only reads it, over IMAP; messages stay as they are. The password is stored encrypted by the host's secret
/// protector; with Microsoft's sign-in, see <see cref="MicrosoftMailSignIn"/>.
/// </summary>
public sealed class BillsMailbox
{
    public const int Singleton = 1;
    public const int MaxSenders = 100;

    private BillsMailbox() => Folder = "INBOX";

    public int Id { get; private set; } = Singleton;
    public bool Enabled { get; private set; }
    public SmtpSignIn SignIn { get; private set; }
    public string? Host { get; private set; }
    public int Port { get; private set; } = 993;
    public string? Username { get; private set; }
    public string? ProtectedPassword { get; private set; }
    public string Folder { get; private set; }

    /// <summary>Whose mail is read: addresses ("ana@paper.example") and domains ("@paper.example"), one a line; null for anyone's.</summary>
    public string? AllowedSenders { get; private set; }

    /// <summary>Whether Claude may read what arrives, where document reading has it switched on.</summary>
    public bool UseClaude { get; private set; }

    public static BillsMailbox Create() => new();

    /// <param name="protectedPassword">Null keeps the current password; "" clears it.</param>
    public void Update(bool enabled, SmtpSignIn signIn, string? host, int port, string? username, string? protectedPassword, string? folder, string? allowedSenders, bool useClaude)
    {
        if (!Enum.IsDefined(signIn))
            throw new DomainException("mailbox.sign_in_invalid", "Choose how OpenBook signs in to the mailbox.");
        if (port is < 1 or > 65535)
            throw new DomainException("mailbox.port_invalid", "The port is a number from 1 to 65535.");
        var cleanHost = Text.Optional(host, 255, "mailbox.host_too_long", "Server") ?? (signIn == SmtpSignIn.Microsoft ? "outlook.office365.com" : null);
        var cleanUser = Text.Optional(username, 320, "mailbox.username_too_long", "User name");
        var password = protectedPassword is null ? ProtectedPassword : protectedPassword.Length == 0 ? null : protectedPassword;
        var senders = Senders(allowedSenders);
        if (enabled && (cleanHost is null || cleanUser is null || signIn == SmtpSignIn.Password && password is null))
            throw new DomainException("mailbox.incomplete", signIn == SmtpSignIn.Password
                ? "Enter the server, the user name and the password before switching it on."
                : "Enter the mailbox's address as the user name before switching it on.");
        Enabled = enabled;
        SignIn = signIn;
        Host = cleanHost;
        Port = port;
        Username = cleanUser;
        ProtectedPassword = password;
        Folder = Text.Optional(folder, 200, "mailbox.folder_too_long", "Folder") ?? "INBOX";
        AllowedSenders = senders.Count == 0 ? null : string.Join('\n', senders);
        UseClaude = useClaude;
    }

    /// <summary>Whether mail from this address is read. The address a message gives can be made up: whatever arrives still waits for a person.</summary>
    public bool Allows(string? from)
    {
        if (AllowedSenders is null)
            return true;
        var address = from?.Trim().ToLowerInvariant();
        return !string.IsNullOrEmpty(address)
            && AllowedSenders.Split('\n').Any(s => s[0] == '@' ? address.EndsWith(s, StringComparison.Ordinal) : address == s);
    }

    private static List<string> Senders(string? text)
    {
        var all = (text ?? "").Split(['\n', '\r', ',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => s.ToLowerInvariant()).Distinct(StringComparer.Ordinal).ToList();
        if (all.Count > MaxSenders)
            throw new DomainException("mailbox.senders_too_many", $"List at most {MaxSenders} senders.");
        foreach (var s in all)
        {
            var at = s.IndexOf('@', StringComparison.Ordinal);
            if (s.Length > 320 || at < 0 || at != s.LastIndexOf('@') || s.IndexOf('.', at) < at + 2 || s.EndsWith('.'))
                throw new DomainException("mailbox.sender_invalid", $"'{s}' isn't an address (ana@paper.example) or a domain (@paper.example).");
        }

        return all;
    }
}

/// <summary>How far the mailbox has been read. Kept apart from the settings, so a check doesn't make someone's open settings page stale.</summary>
public sealed class BillsMailboxProgress
{
    private BillsMailboxProgress() { }

    public int Id { get; private set; } = BillsMailbox.Singleton;

    /// <summary>Which mailbox and folder the numbers are for; another one starts afresh.</summary>
    public string? Source { get; private set; }

    /// <summary>The folder's UIDVALIDITY: when the server changes it, its message numbers start over.</summary>
    public long? UidValidity { get; private set; }

    /// <summary>The highest message number read.</summary>
    public long LastUid { get; private set; }

    public DateTimeOffset? LastCheckedAt { get; private set; }
    public DateTimeOffset? LastReceivedAt { get; private set; }
    public string? LastError { get; private set; }

    public static BillsMailboxProgress Create() => new();

    /// <summary>The message number to read on from; null when the mailbox is new, whose mail so far is left alone.</summary>
    public long? From(string source, long uidValidity) => Source == source && UidValidity == uidValidity ? LastUid : null;

    public void Read(string source, long uidValidity, long uid) =>
        (Source, UidValidity, LastUid) = (source, uidValidity, Source == source && UidValidity == uidValidity ? Math.Max(LastUid, uid) : uid);

    public void Checked(DateTimeOffset at, bool received, string? error)
    {
        LastCheckedAt = at.ToUniversalTime();
        if (received)
            LastReceivedAt = LastCheckedAt;
        LastError = error is { Length: > 1000 } long_ ? long_[..1000] : error;
    }
}
