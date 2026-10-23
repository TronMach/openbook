using OpenBook.Domain.Common;

namespace OpenBook.Domain.Companies;

public enum EmailDelivery
{
    /// <summary>Not set up: nothing can be emailed.</summary>
    None,
    Smtp,

    /// <summary>Writes each message as an .eml file to a folder, for trying things out without sending.</summary>
    Folder,
}

public enum SmtpSecurity
{
    /// <summary>STARTTLS when the server offers it (usually port 587).</summary>
    Auto,
    StartTls,

    /// <summary>TLS from the start (usually port 465).</summary>
    SslOnConnect,
    None,
}

/// <summary>How OpenBook sends email. One row per company file; the SMTP password is stored encrypted.</summary>
public sealed class EmailSettings
{
    public const int Singleton = 1;

    private EmailSettings() { }

    public int Id { get; private set; } = Singleton;
    public EmailDelivery Delivery { get; private set; }
    public string? Host { get; private set; }
    public int Port { get; private set; } = 587;
    public SmtpSecurity Security { get; private set; }
    public string? Username { get; private set; }

    /// <summary>Encrypted by the host (ASP.NET Data Protection); never stored in plain text.</summary>
    public string? ProtectedPassword { get; private set; }

    public string? FromAddress { get; private set; }
    public string? FromName { get; private set; }

    /// <summary>Copies every message to this address, e.g. for your own records.</summary>
    public string? BccAddress { get; private set; }

    public bool IsConfigured => Delivery switch
    {
        EmailDelivery.Smtp => Host is not null && FromAddress is not null,
        EmailDelivery.Folder => FromAddress is not null,
        _ => false,
    };

    public static EmailSettings Default() => new() { Delivery = EmailDelivery.None, Security = SmtpSecurity.Auto };

    /// <param name="protectedPassword">Null keeps the current password.</param>
    public void Update(
        EmailDelivery delivery, string? host, int port, SmtpSecurity security, string? username, string? protectedPassword,
        string? fromAddress, string? fromName, string? bccAddress)
    {
        if (!Enum.IsDefined(delivery) || !Enum.IsDefined(security))
            throw new DomainException("email.settings_invalid", "Unknown email setting.");
        var cleanHost = Text.Optional(host, 255, "email.host_too_long", "Server");
        if (delivery == EmailDelivery.Smtp && cleanHost is null)
            throw new DomainException("email.host_required", "Enter the SMTP server.");
        if (port is < 1 or > 65535)
            throw new DomainException("email.port_invalid", "The port is a number from 1 to 65535.");
        var from = Address(fromAddress, "From address");
        if (delivery != EmailDelivery.None && from is null)
            throw new DomainException("email.from_required", "Enter the address email is sent from.");

        Delivery = delivery;
        Host = cleanHost;
        Port = port;
        Security = security;
        Username = Text.Optional(username, 320, "email.username_too_long", "User name");
        if (protectedPassword is not null)
            ProtectedPassword = protectedPassword.Length == 0 ? null : protectedPassword;
        FromAddress = from;
        FromName = Text.Optional(fromName, 200, "email.from_name_too_long", "From name");
        BccAddress = Address(bccAddress, "Copy address");
    }

    /// <summary>A trimmed address with one @ and a dot after it, or null when blank.</summary>
    public static string? Address(string? value, string label)
    {
        var address = Text.Optional(value, 320, "email.address_too_long", label);
        if (address is null)
            return null;
        var at = address.IndexOf('@', StringComparison.Ordinal);
        if (at < 1 || at != address.LastIndexOf('@') || address.IndexOf('.', at) < at + 2 || address.EndsWith('.') || address.Any(char.IsWhiteSpace))
            throw new DomainException("email.address_invalid", $"'{address}' isn't an email address.");
        return address;
    }
}
