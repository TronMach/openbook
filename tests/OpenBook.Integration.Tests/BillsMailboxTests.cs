using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MimeKit;
using OpenBook.Application.Documents;
using OpenBook.Domain.Common;
using OpenBook.Domain.Companies;
using OpenBook.Domain.Documents;
using OpenBook.Infrastructure.Documents;
using OpenBook.Infrastructure.Persistence;

namespace OpenBook.Integration.Tests;

/// <summary>A mailbox with messages in it, standing in for an IMAP server.</summary>
public sealed class FakeMailbox : IMailboxReader
{
    public List<MailboxMessage> Messages { get; } = [];
    public long Validity { get; set; } = 7;
    public string? Failure { get; set; }
    public MailboxConnection? Asked { get; private set; }

    public Task<MailboxBatch> ReadAsync(MailboxConnection connection, Func<long, long?> from, int max, CancellationToken ct)
    {
        Asked = connection;
        if (Failure is { } failure)
            throw new DomainException("mailbox.unreachable", failure);
        var highest = Messages.Count == 0 ? 0 : Messages.Max(m => m.Uid);
        return Task.FromResult(new MailboxBatch(Validity, highest,
            from(Validity) is { } last ? [.. Messages.Where(m => m.Uid > last).OrderBy(m => m.Uid).Take(max)] : []));
    }
}

/// <summary>Bills by email: the mailbox's settings, what's taken from it and what isn't, and reading real messages.</summary>
public sealed class BillsMailboxTests : IAsyncLifetime
{
    private readonly LedgerDatabase _db = new();

    public Task InitializeAsync() => _db.InitializeAsync();

    public Task DisposeAsync() => _db.DisposeAsync();

    private FakeMailbox Box => _db.Mailbox;

    private Task<T> In<TService, T>(Func<TService, Task<T>> work) where TService : notnull => _db.InScope(sp => work(sp.GetRequiredService<TService>()));

    private Task<T> Mailbox<T>(Func<BillsMailboxService, Task<T>> work) => In(work);

    private Task<BillsMailbox> Connect(string? senders = null, string? username = "bills@studio.test") =>
        Mailbox(s => s.SaveAsync(new BillsMailboxCommand(true, SmtpSignIn.Password, "imap.studio.test", 993, username, "app-password", false, null, senders, false)));

    private Task<MailboxCheck> Check() => Mailbox(s => s.CheckAsync());

    private Task<IReadOnlyList<CapturedDocument>> Documents() => In<DocumentCaptureService, IReadOnlyList<CapturedDocument>>(s => s.ListAsync());

    private static MailboxFile Bill(string number) =>
        new($"{number}.txt", Encoding.UTF8.GetBytes($"Paper Co\nInvoice {number}\nDate 2026-01-05\nTotal $42.50\n"));

    private static async Task<string> Refused(Func<Task> work) => (await Assert.ThrowsAsync<DomainException>(work)).Code;

    [Fact]
    public async Task What_arrives_after_connecting_goes_in_the_inbox_once()
    {
        Assert.Equal("mailbox.off", await Refused(Check));
        Assert.Equal("mailbox.incomplete", await Refused(() => Connect(username: null)));
        await Connect();
        Box.Messages.Add(new MailboxMessage(3, "old@paper.example", "Before", [Bill("OLD-1")]));

        // Mail already there is left alone.
        Assert.True((await Check()).Started);
        Assert.Empty(await Documents());
        Assert.Equal(("imap.studio.test", "app-password", "INBOX"), (Box.Asked!.Host, Box.Asked.Secret.Replace("protected:", "", StringComparison.Ordinal), Box.Asked.Folder));

        Box.Messages.Add(new MailboxMessage(4, "ana@paper.example", "Invoice 1042", [Bill("1042"), Bill("1043")]));
        Box.Messages.Add(new MailboxMessage(5, "ana@paper.example", "Fwd: Invoice 1042", [Bill("1042")]));
        Box.Messages.Add(new MailboxMessage(6, "ana@paper.example", "Photos", [new MailboxFile("virus.exe", [1, 2, 3])]));
        var check = await Check();
        Assert.Equal((2, 3, 1), (check.Documents, check.Messages, check.Duplicates));
        Assert.Contains("virus.exe", Assert.Single(check.Notes), StringComparison.Ordinal);
        var documents = await Documents();
        Assert.Equal(2, documents.Count);
        Assert.All(documents, d => Assert.Equal((DocumentKind.VendorInvoice, CapturedDocumentStatus.New, "ana@paper.example: Invoice 1042"), (d.Kind, d.Status, d.ReceivedFrom)));
        Assert.Equal("1042", documents.Single(d => d.FileName == "1042.txt").Number);

        Assert.Equal(0, (await Check()).Messages);
        // The server numbers its messages afresh: what's there now is left alone, as with a new mailbox.
        Box.Validity = 8;
        Assert.True((await Check()).Started);
        Assert.Equal(2, (await Documents()).Count);
    }

    [Fact]
    public async Task Only_listed_senders_are_read_and_a_failure_is_kept_for_the_page()
    {
        Assert.Equal("mailbox.sender_invalid", await Refused(() => Connect("not-an-address")));
        Assert.Equal("ana@paper.example\n@studio.test", (await Connect("Ana@Paper.example, @studio.test")).AllowedSenders);
        await Check();
        Box.Messages.Add(new MailboxMessage(1, "ana@paper.example", "a", [Bill("A")]));
        Box.Messages.Add(new MailboxMessage(2, "pat@studio.test", "b", [Bill("B")]));
        Box.Messages.Add(new MailboxMessage(3, "pat@evilstudio.test", "c", [Bill("C")]));
        Box.Messages.Add(new MailboxMessage(4, null, "d", [Bill("D")]));
        var check = await Check();
        Assert.Equal((2, 2), (check.Documents, check.NotAllowed));

        Box.Failure = "The mailbox couldn't be read: no route to host";
        Assert.Equal("mailbox.unreachable", await Refused(Check));
        var progress = await Mailbox(s => s.ProgressAsync());
        Assert.Contains("no route", progress.LastError, StringComparison.Ordinal);
        Box.Failure = null;
        await Check();
        Assert.Null((await Mailbox(s => s.ProgressAsync())).LastError);

        // The password isn't in the audit log.
        var audit = await In<OpenBookDbContext, List<string?>>(db => db.AuditRecords.Where(a => a.EntityType == BillsMailboxService.EntityType).Select(a => a.After).ToListAsync());
        Assert.All(audit, a => Assert.DoesNotContain("app-password", a, StringComparison.Ordinal));
    }

    [Fact]
    public void A_messages_bills_are_taken_and_its_signature_pictures_left()
    {
        var message = new MimeMessage();
        message.From.Add(new MailboxAddress("Ana", "ana@paper.example"));
        message.Subject = "Invoice 1042";
        var body = new BodyBuilder { HtmlBody = "<p>Attached.</p><img src=\"cid:logo\">" };
        body.LinkedResources.Add("logo.png", new byte[500], new ContentType("image", "png")).ContentId = "logo";
        body.Attachments.Add("..\\..\\Invoice: 1042.PDF", Encoding.ASCII.GetBytes("%PDF-1.4"), new ContentType("application", "pdf"));
        body.Attachments.Add("scan.jpg", new byte[60_000], new ContentType("image", "jpeg"));
        body.Attachments.Add("terms.docx", new byte[100], new ContentType("application", "msword"));
        body.Attachments.Add("huge.pdf", new byte[OpenBook.Domain.Attachments.Attachment.MaxBytes + 5], new ContentType("application", "pdf"));
        message.Body = body.ToMessageBody();

        var read = MailboxFiles.Read(9, message);
        Assert.Equal((9L, "ana@paper.example", "Invoice 1042"), (read.Uid, read.From, read.Subject));
        Assert.Equal(["Invoice_ 1042.pdf", "scan.jpg"], read.Files.Select(f => f.FileName));
        Assert.Contains("huge.pdf", read.Note, StringComparison.Ordinal);
    }
}
