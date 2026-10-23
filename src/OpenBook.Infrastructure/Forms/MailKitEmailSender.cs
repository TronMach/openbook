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
        if (!string.IsNullOrEmpty(settings.Username))
            await client.AuthenticateAsync(settings.Username, password ?? "", ct);
        await client.SendAsync(mime, ct);
        await client.DisconnectAsync(quit: true, ct);
    }

    private static MimeMessage Build(EmailSettings settings, EmailMessage message)
    {
        var mime = new MimeMessage();
        var from = settings.FromAddress ?? throw new InvalidOperationException("No from address is set.");
        mime.From.Add(new MailboxAddress(settings.FromName ?? from, from));
        mime.To.Add(MailboxAddress.Parse(message.To));
        if (settings.BccAddress is { } bcc)
            mime.Bcc.Add(MailboxAddress.Parse(bcc));
        if (message.ReplyTo is { } replyTo)
            mime.ReplyTo.Add(MailboxAddress.Parse(replyTo));
        mime.Subject = message.Subject;

        var body = new BodyBuilder { TextBody = message.Body };
        foreach (var attachment in message.Attachments)
            body.Attachments.Add(attachment.FileName, attachment.Content, ContentType.Parse(attachment.ContentType));
        mime.Body = body.ToMessageBody();
        return mime;
    }
}
