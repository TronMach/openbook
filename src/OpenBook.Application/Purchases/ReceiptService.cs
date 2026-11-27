using System.Text.Json;
using OpenBook.Application.Attachments;
using OpenBook.Application.Banking;
using OpenBook.Domain.Auditing;
using OpenBook.Domain.Banking;
using OpenBook.Domain.Common;
using OpenBook.Domain.Purchases;

namespace OpenBook.Application.Purchases;

/// <summary>Reads the text of a receipt file: PDFs and text directly, images through an OCR engine when one is set up.</summary>
public interface IReceiptTextExtractor
{
    /// <summary>The text, or null when the file can't be read (an image with no OCR engine, a scanned PDF without text).</summary>
    Task<string?> ExtractAsync(string fileName, byte[] content, CancellationToken ct);

    /// <summary>Whether images can be read (an OCR engine is installed and configured).</summary>
    bool CanReadImages { get; }
}

/// <param name="AccountId">The bank or card account it was paid from.</param>
/// <param name="CategoryId">What it was for; null takes the vendor's default category.</param>
public sealed record RecordReceiptCommand(Guid AccountId, Guid? CategoryId, DateOnly Date, decimal Amount, Guid? VendorId = null, string? Payee = null, string? Memo = null);

/// <summary>
/// Receipt capture: upload a receipt (photo, PDF, text), have its vendor, date, total and tax read from it, then record it
/// as an expense with the file attached, or dismiss it.
/// </summary>
public sealed class ReceiptService(
    IUnitOfWork unit, IPurchasesStore store, AttachmentService attachments, IReceiptTextExtractor extractor, BankingService banking, IClock clock, ICurrentUser user)
{
    public const string EntityType = "receipt";

    public bool CanReadImages => extractor.CanReadImages;

    public Task<ReceiptCapture?> GetAsync(Guid id, CancellationToken ct = default) => store.GetReceiptAsync(id, ct);

    public Task<IReadOnlyList<ReceiptCapture>> ListAsync(CancellationToken ct = default) => store.ListReceiptsAsync(ct);

    public Task<int> NewCountAsync(CancellationToken ct = default) => store.CountNewReceiptsAsync(ct);

    /// <summary>Saves the file on a new capture, then reads it. A file that can't be read is still kept, with no fields filled in.</summary>
    public async Task<ReceiptCapture> UploadAsync(string fileName, Stream content, CancellationToken ct = default)
    {
        using var buffer = await CheckedFileAsync(fileName, content, ct);
        var capture = ReceiptCapture.Create(fileName, clock.UtcNow, user.Actor);
        capture.SetFileHash(Hash(buffer));
        store.Add(capture);
        store.Add(Audit(AuditAction.Created, capture, null));
        await store.SaveChangesAsync(ct);
        var file = await attachments.AddAsync(EntityType, capture.Id, fileName, buffer, ct);

        var bytes = await attachments.GetContentAsync(file.Id, ct) ?? [];
        var text = await extractor.ExtractAsync(fileName, bytes, ct);
        var vendors = await store.ListVendorsAsync(ct);
        var fields = ReceiptParser.Parse(text, vendors.Where(v => v.IsActive).Select(v => v.DisplayName));
        var vendor = fields.Vendor is { } name ? vendors.FirstOrDefault(v => v.IsActive && string.Equals(v.DisplayName, name, StringComparison.OrdinalIgnoreCase)) : null;
        capture.Read(text, fields, vendor?.Id);
        await store.SaveChangesAsync(ct);
        return capture;
    }

    /// <summary>
    /// A receipt typed in from paper, recorded as an expense at once, with a photo of it attached when one is given.
    /// It's listed with the uploaded ones (as typed in), so every receipt has one place.
    /// </summary>
    public async Task<BankTransaction> EnterAsync(RecordReceiptCommand command, string? fileName = null, Stream? content = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        var name = string.IsNullOrWhiteSpace(fileName) ? "receipt" : fileName;
        using var buffer = content is null ? null : await CheckedFileAsync(name, content, ct);
        await using var transaction = await unit.BeginAsync(ct);
        var capture = buffer is null ? ReceiptCapture.Typed(clock.UtcNow, user.Actor) : ReceiptCapture.Create(name, clock.UtcNow, user.Actor);
        if (buffer is not null)
            capture.SetFileHash(Hash(buffer));
        var vendor = command.VendorId is { } vendorId ? await store.GetVendorAsync(vendorId, ct) ?? throw new EntityNotFoundException("vendor", vendorId) : null;
        capture.Read(null, new ReceiptFields(vendor?.DisplayName ?? command.Payee, command.Date, command.Amount, null), vendor?.Id);
        store.Add(capture);
        store.Add(Audit(AuditAction.Created, capture, null));
        await store.SaveChangesAsync(ct);
        if (buffer is not null)
            await attachments.AddAsync(EntityType, capture.Id, name, buffer, ct);

        var spend = await RecordSpendAsync(capture, command, ct);
        await transaction.CommitAsync(ct);
        return spend;
    }

    /// <summary>Other receipts with the same file: the same receipt uploaded twice.</summary>
    public async Task<IReadOnlyList<ReceiptCapture>> SameFileAsync(ReceiptCapture capture, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(capture);
        return capture.FileHash is { } hash ? (await store.ListReceiptsByFileHashAsync(hash, ct)).Where(r => r.Id != capture.Id).ToList() : [];
    }

    /// <summary>Records the expense (one category line) and attaches the receipt file to it.</summary>
    public async Task<BankTransaction> RecordAsync(Guid id, RecordReceiptCommand command, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        var capture = await store.GetReceiptAsync(id, ct) ?? throw new EntityNotFoundException(EntityType, id);
        if (capture.Status != ReceiptStatus.New)
            throw new DomainException("receipt.done", "This receipt has already been dealt with.");

        // All or nothing: the receipt's version stops a second press, and a failure leaves no expense behind.
        await using var transaction = await unit.BeginAsync(ct);
        var spend = await RecordSpendAsync(capture, command, ct);
        await transaction.CommitAsync(ct);
        return spend;
    }

    /// <summary>
    /// Marks a waiting receipt as recorded by an expense made elsewhere (a trucking receipt), copying its files onto the
    /// expense, without saving: the caller saves it with the expense, in its transaction.
    /// </summary>
    public async Task RecordedByAsync(ReceiptCapture capture, Guid spendId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(capture);
        if (capture.Status != ReceiptStatus.New)
            throw new DomainException("receipt.done", "This receipt has already been dealt with.");
        foreach (var file in await attachments.ListAsync(EntityType, capture.Id, ct))
            if (await attachments.GetContentAsync(file.Id, ct) is { } bytes)
                await attachments.AddAsync("bank_transaction", spendId, file.FileName, new MemoryStream(bytes), ct);

        var before = Snapshot(capture);
        capture.Recorded(spendId);
        store.Add(Audit(AuditAction.Updated, capture, before));
    }

    private async Task<BankTransaction> RecordSpendAsync(ReceiptCapture capture, RecordReceiptCommand command, CancellationToken ct)
    {
        Money.EnsureCents(command.Amount);
        if (command.Amount <= 0m)
            throw new DomainException("receipt.amount_not_positive", "Enter the amount paid.");
        var vendor = command.VendorId is { } vendorId ? await store.GetVendorAsync(vendorId, ct) ?? throw new EntityNotFoundException("vendor", vendorId) : null;
        var category = command.CategoryId ?? vendor?.DefaultExpenseAccountId
            ?? throw new DomainException("receipt.category_required", "Choose what the expense was for.");

        var spend = await banking.RecordAsync(new RecordBankTransactionCommand(
            BankTransactionKind.Spend, command.AccountId, command.Date, [new BankSplit(category, command.Amount, command.Memo)],
            Payee: command.Payee ?? vendor?.DisplayName ?? capture.VendorName, Memo: command.Memo, VendorId: vendor?.Id), ct);
        await RecordedByAsync(capture, spend.Id, ct);
        await store.SaveChangesAsync(ct);
        return spend;
    }

    /// <summary>The file, read into memory once its name, type and size have been checked like an attachment's.</summary>
    public async Task<MemoryStream> CheckedFileAsync(string fileName, Stream content, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await content.ReadAsync(chunk, ct)) > 0 && buffer.Length <= Domain.Attachments.Attachment.MaxBytes)
            buffer.Write(chunk, 0, read);
        // Checks the name, type and size before anything is saved.
        Domain.Attachments.Attachment.Create(EntityType, Guid.Empty, fileName, buffer.Length, clock.UtcNow, user.Actor);
        buffer.Position = 0;
        return buffer;
    }

    private static string Hash(MemoryStream buffer) =>
        Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(buffer.GetBuffer().AsSpan(0, (int)buffer.Length)));

    public async Task<ReceiptCapture> DismissAsync(Guid id, CancellationToken ct = default)
    {
        var capture = await store.GetReceiptAsync(id, ct) ?? throw new EntityNotFoundException(EntityType, id);
        var before = Snapshot(capture);
        capture.Dismiss();
        store.Add(Audit(AuditAction.Updated, capture, before));
        await store.SaveChangesAsync(ct);
        return capture;
    }

    private AuditRecord Audit(AuditAction action, ReceiptCapture r, string? before) =>
        AuditRecord.Create(clock.UtcNow, user.Actor, action, EntityType, r.Id, before, Snapshot(r));

    private static string Snapshot(ReceiptCapture r) => JsonSerializer.Serialize(new
    {
        r.FileName,
        Status = r.Status.ToString(),
        r.VendorName,
        r.VendorId,
        r.Date,
        r.Total,
        r.Tax,
        r.BankTransactionId,
    });
}
