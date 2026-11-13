using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using OpenBook.Application.Forms;
using OpenBook.Domain.Companies;
using OpenBook.Infrastructure;

namespace OpenBook.Integration.Tests;

/// <summary>
/// The real SMTP sender (MailKit) talking to a small SMTP server inside the test, so a MailKit update that changes how
/// a message goes out is caught. The other email tests use a stand-in sender. No database.
/// </summary>
public sealed class SmtpSendTests
{
    [Fact]
    public async Task An_email_goes_out_over_SMTP_with_its_recipients_body_and_attachment()
    {
        using var server = new FakeSmtpServer();
        var services = new ServiceCollection()
            .AddOpenBookInfrastructure("Data Source=:memory:", backupDirectory: Path.GetTempPath())
            .BuildServiceProvider();
        var sender = services.GetRequiredService<IEmailSender>();
        var settings = EmailSettings.Default();
        settings.Update(EmailDelivery.Smtp, "127.0.0.1", server.Port, SmtpSecurity.None, null, null, "books@northwind.example", "Northwind Studio", null);
        var message = new EmailMessage("accounts@bosbakery.example", "Invoice 1042 from Northwind Studio", "Hello,\n\nYour invoice is attached.",
            [new EmailAttachment("Invoice 1042.pdf", "application/pdf", Encoding.ASCII.GetBytes("%PDF-1.4 pretend"))])
        {
            Cc = ["owner@bosbakery.example"],
            Bcc = ["archive@northwind.example"],
            HtmlBody = "<p>Hello,</p><p>Your invoice is attached.</p>",
        };

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var received = server.ReceiveOneAsync(timeout.Token);
        await sender.SendAsync(settings, null, message, timeout.Token);
        var mail = await received;

        Assert.Equal("<books@northwind.example>", mail.From);
        Assert.Equal(["<accounts@bosbakery.example>", "<owner@bosbakery.example>", "<archive@northwind.example>"], mail.Recipients);
        Assert.Contains("Subject: Invoice 1042 from Northwind Studio", mail.Data, StringComparison.Ordinal);
        Assert.Contains("To: accounts@bosbakery.example", mail.Data, StringComparison.Ordinal);
        Assert.DoesNotContain("archive@northwind.example", mail.Data, StringComparison.Ordinal);
        Assert.Contains("Invoice 1042.pdf", mail.Data, StringComparison.Ordinal);
        Assert.Contains("text/html", mail.Data, StringComparison.Ordinal);
        Assert.Contains("Your invoice is attached.", mail.Data, StringComparison.Ordinal);
    }

    private sealed record ReceivedMail(string From, IReadOnlyList<string> Recipients, string Data);

    /// <summary>Answers one SMTP session on a free loopback port: no TLS, no sign-in, every command accepted.</summary>
    private sealed class FakeSmtpServer : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);

        public FakeSmtpServer() => _listener.Start();

        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        public async Task<ReceivedMail> ReceiveOneAsync(CancellationToken ct)
        {
            using var client = await _listener.AcceptTcpClientAsync(ct);
            await using var stream = client.GetStream();
            using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
            await using var writer = new StreamWriter(stream, Encoding.ASCII, leaveOpen: true) { NewLine = "\r\n", AutoFlush = true };
            await writer.WriteLineAsync("220 test.local ESMTP");
            string from = "";
            var recipients = new List<string>();
            var data = new StringBuilder();
            while (await reader.ReadLineAsync(ct) is { } line)
            {
                var command = line.Split(' ', 2)[0].ToUpperInvariant();
                switch (command)
                {
                    case "EHLO":
                        await writer.WriteLineAsync("250-test.local");
                        await writer.WriteLineAsync("250 8BITMIME");
                        break;
                    case "MAIL":
                        from = line[(line.IndexOf(':', StringComparison.Ordinal) + 1)..].Trim().Split(' ')[0];
                        await writer.WriteLineAsync("250 OK");
                        break;
                    case "RCPT":
                        recipients.Add(line[(line.IndexOf(':', StringComparison.Ordinal) + 1)..].Trim());
                        await writer.WriteLineAsync("250 OK");
                        break;
                    case "DATA":
                        await writer.WriteLineAsync("354 Go ahead");
                        while (await reader.ReadLineAsync(ct) is { } body && body != ".")
                            data.Append(body).Append('\n');
                        await writer.WriteLineAsync("250 Queued");
                        break;
                    case "QUIT":
                        await writer.WriteLineAsync("221 Bye");
                        return new ReceivedMail(from, recipients, data.ToString());
                    default:
                        await writer.WriteLineAsync("250 OK");
                        break;
                }
            }

            return new ReceivedMail(from, recipients, data.ToString());
        }

        public void Dispose() => _listener.Stop();
    }
}
