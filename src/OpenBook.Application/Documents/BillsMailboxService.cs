using System.Security.Cryptography;
using System.Text.Json;
using OpenBook.Application.Companies;
using OpenBook.Application.Forms;
using OpenBook.Domain.Auditing;
using OpenBook.Domain.Common;
using OpenBook.Domain.Companies;
using OpenBook.Domain.Documents;

namespace OpenBook.Application.Documents;

/// <param name="Secret">The password, or with <paramref name="Microsoft"/> the access token.</param>
public sealed record MailboxConnection(string Host, int Port, string Username, string Secret, bool Microsoft, string Folder);

public sealed record MailboxFile(string FileName, byte[] Content);

/// <param name="Note">Why something in it was left out, if anything was.</param>
public sealed record MailboxMessage(long Uid, string? From, string? Subject, IReadOnlyList<MailboxFile> Files, string? Note = null);

/// <param name="HighestUid">The folder's highest message number now, whether or not it was read.</param>
public sealed record MailboxBatch(long UidValidity, long HighestUid, IReadOnlyList<MailboxMessage> Messages);

/// <summary>Reads new messages' attachments from a mailbox over IMAP, changing nothing in it. Implemented with MailKit in Infrastructure.</summary>
public interface IMailboxReader
{
    /// <param name="from">Asked the folder's UIDVALIDITY: the message number to read on from, or null to read nothing and only say where the folder stands.</param>
    /// <param name="max">How many messages at most, oldest first.</param>
    Task<MailboxBatch> ReadAsync(MailboxConnection connection, Func<long, long?> from, int max, CancellationToken ct);
}

/// <param name="Password">A new password; null or blank keeps the saved one.</param>
public sealed record BillsMailboxCommand(
    bool Enabled, SmtpSignIn SignIn, string? Host, int Port, string? Username, string? Password, bool ClearPassword, string? Folder, string? AllowedSenders, bool UseClaude);

/// <summary>What a check of the mailbox did.</summary>
public sealed record MailboxCheck(int Documents, int Messages, int NotAllowed, int Duplicates, IReadOnlyList<string> Notes, bool Started = false);

/// <summary>
/// Bills by email: new messages in the mailbox are looked at every few minutes, and their PDFs, scans and photos become
/// vendor invoices in the document inbox, read and matched like an upload, for a person to check. Nothing is posted.
/// Mail already there when the mailbox is first connected is left alone.
/// </summary>
public sealed class BillsMailboxService(
    IDocumentStore store, DocumentCaptureService documents, IMailboxReader reader, IMicrosoftMailTokens microsoft, ICompanyStore companies, ISecretProtector secrets,
    IUnitOfWork unit, IClock clock, ICurrentUser user)
{
    public const string EntityType = "bills_mailbox";

    /// <summary>Messages read in one check; the rest wait for the next.</summary>
    public const int MaxMessages = 25;

    public async Task<BillsMailbox> GetAsync(CancellationToken ct = default) => await store.GetBillsMailboxAsync(ct) ?? BillsMailbox.Create();

    public async Task<BillsMailboxProgress> ProgressAsync(CancellationToken ct = default) =>
        await store.GetBillsMailboxProgressAsync(ct) ?? BillsMailboxProgress.Create();

    public async Task<BillsMailbox> SaveAsync(BillsMailboxCommand command, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        var company = await companies.GetRequiredCompanyAsync(ct);
        var mailbox = await store.GetBillsMailboxAsync(ct);
        if (mailbox is null)
        {
            mailbox = BillsMailbox.Create();
            store.Add(mailbox);
        }

        var before = Snapshot(mailbox);
        var password = command.ClearPassword ? "" : string.IsNullOrEmpty(command.Password) ? null : secrets.Protect(command.Password);
        mailbox.Update(command.Enabled, command.SignIn, command.Host, command.Port, command.Username, password, command.Folder, command.AllowedSenders, command.UseClaude);
        store.Add(AuditRecord.Create(clock.UtcNow, user.Actor, AuditAction.Updated, EntityType, company.Id, before, Snapshot(mailbox)));
        await store.SaveChangesAsync(ct);
        return mailbox;
    }

    /// <summary>Looks for new messages now. A failure is kept on the progress and thrown.</summary>
    public async Task<MailboxCheck> CheckAsync(CancellationToken ct = default)
    {
        var mailbox = await GetAsync(ct);
        if (!mailbox.Enabled)
            throw new DomainException("mailbox.off", "Bills by email is switched off.");
        try
        {
            return await ReadAsync(mailbox, ct);
        }
        catch (DomainException ex)
        {
            unit.DiscardPending();
            var progress = await RowAsync(ct);
            progress.Checked(clock.UtcNow, received: false, ex.Message);
            await store.SaveChangesAsync(ct);
            throw;
        }
    }

    private async Task<MailboxCheck> ReadAsync(BillsMailbox mailbox, CancellationToken ct)
    {
        var secret = mailbox.SignIn == SmtpSignIn.Microsoft
            ? await microsoft.AccessTokenAsync(MailPurpose.Bills, ct)
            : secrets.Unprotect(mailbox.ProtectedPassword ?? "") ?? throw new DomainException("mailbox.password_unreadable", "The mailbox's saved password can't be read on this computer. Enter it again under Settings, Bills by email.");
        var connection = new MailboxConnection(mailbox.Host!, mailbox.Port, mailbox.Username!, secret, mailbox.SignIn == SmtpSignIn.Microsoft, mailbox.Folder);
        var source = $"{connection.Username}@{connection.Host}:{connection.Port}/{connection.Folder}";
        var progress = await RowAsync(ct);
        var batch = await reader.ReadAsync(connection, validity => progress.From(source, validity), MaxMessages, ct);
        if (progress.From(source, batch.UidValidity) is null)
        {
            progress.Read(source, batch.UidValidity, batch.HighestUid);
            progress.Checked(clock.UtcNow, received: false, null);
            await store.SaveChangesAsync(ct);
            return new MailboxCheck(0, 0, 0, 0, [], Started: true);
        }

        int made = 0, notAllowed = 0, duplicates = 0;
        var notes = new List<string>();
        foreach (var message in batch.Messages.OrderBy(m => m.Uid))
        {
            if (!mailbox.Allows(message.From))
            {
                notAllowed++;
            }
            else
            {
                if (message.Note is { } note)
                    notes.Add($"{message.From}: {note}");
                foreach (var file in message.Files)
                {
                    // A message read again after a stop in the middle, or the same bill sent twice, isn't taken twice.
                    var hash = Convert.ToHexStringLower(SHA256.HashData(file.Content));
                    if ((await store.ListDocumentsByFileHashAsync(hash, ct)).Count > 0)
                    {
                        duplicates++;
                        continue;
                    }

                    try
                    {
                        using var content = new MemoryStream(file.Content);
                        await documents.UploadAsync(DocumentKind.VendorInvoice, file.FileName, content, mailbox.UseClaude, receivedFrom: Sender(message), ct: ct);
                        made++;
                    }
                    catch (DomainException ex)
                    {
                        unit.DiscardPending();
                        progress = await RowAsync(ct);
                        notes.Add($"{file.FileName}: {ex.Message}");
                    }
                }
            }

            progress.Read(source, batch.UidValidity, message.Uid);
            await store.SaveChangesAsync(ct);
        }

        progress.Checked(clock.UtcNow, made > 0, notes.Count == 0 ? null : string.Join(" ", notes));
        await store.SaveChangesAsync(ct);
        return new MailboxCheck(made, batch.Messages.Count, notAllowed, duplicates, notes);
    }

    private static string Sender(MailboxMessage message) =>
        TextCleaning.Cut(string.Join(": ", new[] { message.From, message.Subject }.Where(s => !string.IsNullOrWhiteSpace(s))), CapturedDocument.MaxReceivedFrom) is { Length: > 0 } text ? text : "an email";

    private async Task<BillsMailboxProgress> RowAsync(CancellationToken ct)
    {
        if (await store.GetBillsMailboxProgressAsync(ct) is { } found)
            return found;
        var created = BillsMailboxProgress.Create();
        store.Add(created);
        return created;
    }

    /// <summary>No password in here: audit records feed webhooks.</summary>
    private static string Snapshot(BillsMailbox m) => JsonSerializer.Serialize(new
    {
        m.Enabled,
        SignIn = m.SignIn.ToString(),
        m.Host,
        m.Port,
        m.Username,
        HasPassword = m.ProtectedPassword is not null,
        m.Folder,
        m.AllowedSenders,
        m.UseClaude,
    });
}
