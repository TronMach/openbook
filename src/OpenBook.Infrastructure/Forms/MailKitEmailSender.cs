using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;
using OpenBook.Application.Forms;
using OpenBook.Domain.Companies;
using OpenBook.Infrastructure.Persistence;

namespace OpenBook.Infrastructure.Forms;

/// <summary>Sends through SMTP with MailKit, or writes .eml files to the "mail" folder next to the company file.</summary>
internal sealed class MailKitEmailSender(OpenBookStorage storage) : IEmailSender
{
    public async Task SendAsync(EmailSettings settings, string? password, EmailMessage message, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(message);
        var mime = Build(settings, message);

        if (settings.Delivery == EmailDelivery.Folder)
        {
            Directory.CreateDirectory(storage.MailDirectory);
            var path = Path.Combine(storage.MailDirectory, $"{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.eml");
            await mime.WriteToAsync(path, ct);
            return;
        }

        using var client = new SmtpClient { Timeout = 30_000 };
        var security = settings.Security switch
        {
            SmtpSecurity.StartTls => SecureSocketOptions.StartTls,
            SmtpSecurity.SslOnConnect => SecureSocketOptions.SslOnConnect,
            SmtpSecurity.None => SecureSocketOptions.None,
            _ => SecureSocketOptions.Auto,
        };
        await client.ConnectAsync(settings.Host ?? throw new InvalidOperationException("No SMTP server is set."), settings.Port, security, ct);
        // Automatic takes a plain connection when the server offers no encryption: never send a password over one
        // unless the admin chose None.
        if (!string.IsNullOrEmpty(settings.Username) && !client.IsSecure && settings.Security != SmtpSecurity.None)
        {
            await client.DisconnectAsync(quit: true, ct);
            throw new OpenBook.Domain.Common.DomainException("email.not_encrypted",
                "The mail server didn't offer an encrypted connection, so the password wasn't sent. Check the server and port, or choose None under Security if you're sure.");
        }

        if (!string.IsNullOrEmpty(settings.Username))
            await client.AuthenticateAsync(settings.Username, password ?? "", ct);
        await client.SendAsync(new MailKit.SendRequest(mime), ct);
        try
        {
            await client.DisconnectAsync(quit: true, ct);
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or MailKit.ProtocolException or MailKit.ServiceNotConnectedException)
        {
            // It's gone: failing here would report a sent email as unsent, and the next try would send it twice.
        }
    }

    private static MimeMessage Build(EmailSettings settings, EmailMessage message)
    {
        var mime = new MimeMessage();
        var from = settings.FromAddress ?? throw new InvalidOperationException("No from address is set.");
        mime.From.Add(new MailboxAddress(settings.FromName ?? from, from));
        foreach (var to in message.To.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            mime.To.Add(MailboxAddress.Parse(to));
        foreach (var cc in message.Cc)
            mime.Cc.Add(MailboxAddress.Parse(cc));
        foreach (var hidden in message.Bcc)
            mime.Bcc.Add(MailboxAddress.Parse(hidden));
        if (settings.BccAddress is { } bcc)
            mime.Bcc.Add(MailboxAddress.Parse(bcc));
        if (message.ReplyTo is { } replyTo)
            mime.ReplyTo.Add(MailboxAddress.Parse(replyTo));
        mime.Subject = message.Subject;

        var body = new BodyBuilder { TextBody = message.Body, HtmlBody = message.HtmlBody };
        foreach (var image in message.InlineImages)
        {
            var resource = body.LinkedResources.Add(image.ContentId, image.Content, ContentType.Parse(image.ContentType));
            resource.ContentId = image.ContentId;
        }

        foreach (var attachment in message.Attachments)
            body.Attachments.Add(attachment.FileName, attachment.Content, ContentType.Parse(attachment.ContentType));
        mime.Body = body.ToMessageBody();
        return mime;
    }
}
