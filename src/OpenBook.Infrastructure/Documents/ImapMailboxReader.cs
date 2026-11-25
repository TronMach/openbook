using System.Net.Sockets;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Security;
using MimeKit;
using OpenBook.Application.Documents;
using OpenBook.Domain.Attachments;
using OpenBook.Domain.Common;

namespace OpenBook.Infrastructure.Documents;

/// <summary>Reads a mailbox over IMAP with MailKit, always encrypted, the folder opened read-only so nothing in it changes.</summary>
internal sealed class ImapMailboxReader : IMailboxReader
{
    /// <summary>A message bigger than this isn't downloaded.</summary>
    private const uint MaxMessageBytes = 40 * 1024 * 1024;

    public async Task<MailboxBatch> ReadAsync(MailboxConnection connection, Func<long, long?> from, int max, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(from);
        try
        {
            using var client = new ImapClient { Timeout = 60_000 };
            await client.ConnectAsync(connection.Host, connection.Port, connection.Port == 993 ? SecureSocketOptions.SslOnConnect : SecureSocketOptions.StartTls, ct);
            if (connection.Microsoft)
                await client.AuthenticateAsync(new SaslMechanismOAuth2(connection.Username, connection.Secret), ct);
            else
                await client.AuthenticateAsync(connection.Username, connection.Secret, ct);

            var folder = string.Equals(connection.Folder, "INBOX", StringComparison.OrdinalIgnoreCase) ? client.Inbox! : await client.GetFolderAsync(connection.Folder, ct);
            await folder.OpenAsync(FolderAccess.ReadOnly, ct);
            var validity = (long)folder.UidValidity;
            var highest = (long)(folder.UidNext?.Id ?? 1) - 1;
            var messages = new List<MailboxMessage>();
            if (from(validity) is { } last && highest > last)
            {
                var range = new UniqueIdRange(new UniqueId(folder.UidValidity, (uint)(last + 1)), UniqueId.MaxValue);
                var found = await folder.FetchAsync(range, MessageSummaryItems.UniqueId | MessageSummaryItems.Size, ct);
                foreach (var summary in found.Where(s => s.UniqueId.Id > last).OrderBy(s => s.UniqueId.Id).Take(max))
                    messages.Add(summary.Size > MaxMessageBytes
                        ? new MailboxMessage(summary.UniqueId.Id, null, null, [], $"a message over {MaxMessageBytes / 1024 / 1024} MB wasn't read.")
                        : MailboxFiles.Read(summary.UniqueId.Id, await folder.GetMessageAsync(summary.UniqueId, ct)));
            }

            await client.DisconnectAsync(quit: true, ct);
            return new MailboxBatch(validity, highest, messages);
        }
        catch (AuthenticationException ex)
        {
            throw new DomainException("mailbox.sign_in_refused", $"The mail server refused the sign-in: {ex.Message}");
        }
        catch (FolderNotFoundException)
        {
            throw new DomainException("mailbox.folder_missing", $"The mailbox has no folder named '{connection.Folder}'.");
        }
        catch (Exception ex) when (ex is IOException or SocketException or ProtocolException or CommandException or SslHandshakeException or NotSupportedException
            || ex is OperationCanceledException && !ct.IsCancellationRequested)
        {
            throw new DomainException("mailbox.unreachable", $"The mailbox couldn't be read: {ex.Message}");
        }
    }
}

/// <summary>The files in a message worth keeping: PDFs, scans and photos, but not the pictures in someone's signature.</summary>
public static class MailboxFiles
{
    public const int MaxFiles = 20;

    /// <summary>A picture smaller than this is a logo or a button, not a bill.</summary>
    private const int MinPictureBytes = 20_000;

    private static readonly HashSet<string> Pictures = new(StringComparer.OrdinalIgnoreCase) { ".png", ".jpg", ".jpeg", ".webp", ".tif", ".tiff" };

    public static MailboxMessage Read(long uid, MimeMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        var files = new List<MailboxFile>();
        string? note = null;
        foreach (var part in message.BodyParts.OfType<MimePart>())
        {
            if (Name(part) is not { } name)
                continue;
            if (files.Count == MaxFiles)
            {
                note = $"only the first {MaxFiles} files were taken.";
                break;
            }

            using var content = new Limited(Attachment.MaxBytes + 1);
            try
            {
                part.Content?.DecodeTo(content);
            }
            catch (IOException)
            {
                note = $"{name} is over {Attachment.MaxBytes / 1024 / 1024} MB, so it wasn't taken.";
                continue;
            }

            var picture = Pictures.Contains(Path.GetExtension(name));
            if (content.Length == 0 || picture && content.Length < MinPictureBytes)
                continue;
            files.Add(new MailboxFile(name, content.ToArray()));
        }

        return new MailboxMessage(uid, message.From.Mailboxes.FirstOrDefault()?.Address, message.Subject, files, note);
    }

    /// <summary>The file's name when it's one to keep, with the ending its content type has when it was sent without.</summary>
    private static string? Name(MimePart part)
    {
        var type = part.ContentType.MimeType;
        var name = part.FileName is { Length: > 0 } given ? Path.GetFileName(given.Replace('\\', '/')) : null;
        var ending = name is null ? "" : Path.GetExtension(name);
        if (string.Equals(type, "application/pdf", StringComparison.OrdinalIgnoreCase) || string.Equals(ending, ".pdf", StringComparison.OrdinalIgnoreCase))
            return Clean(name, ".pdf");
        if (Pictures.Contains(ending))
            return Clean(name, ending);
        return type.ToLowerInvariant() switch
        {
            "image/jpeg" => Clean(name, ".jpg"),
            "image/png" => Clean(name, ".png"),
            "image/tiff" => Clean(name, ".tif"),
            "image/webp" => Clean(name, ".webp"),
            _ => null,
        };
    }

    private static string Clean(string? name, string ending)
    {
        var stem = new string(Path.GetFileNameWithoutExtension(name ?? "").Select(c => char.IsControl(c) || "<>:\"/\\|?*".Contains(c, StringComparison.Ordinal) ? '_' : c).ToArray()).Trim();
        return (stem.Length == 0 ? "document" : stem.Length > 100 ? stem[..100] : stem) + ending.ToLowerInvariant();
    }

    /// <summary>Memory that stops taking more: a message says nothing true about how big its parts are.</summary>
    private sealed class Limited(long max) : MemoryStream
    {
        public override void Write(byte[] buffer, int offset, int count)
        {
            if (Length + count > max)
                throw new IOException("Too big.");
            base.Write(buffer, offset, count);
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            if (Length + buffer.Length > max)
                throw new IOException("Too big.");
            base.Write(buffer);
        }
    }
}
