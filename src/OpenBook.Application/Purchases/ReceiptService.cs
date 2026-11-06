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
        ArgumentNullException.ThrowIfNull(content);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await content.ReadAsync(chunk, ct)) > 0 && buffer.Length <= Domain.Attachments.Attachment.MaxBytes)
            buffer.Write(chunk, 0, read);
        // Checks the name, type and size before anything is saved.
        Domain.Attachments.Attachment.Create(EntityType, Guid.Empty, fileName, buffer.Length, clock.UtcNow, user.Actor);

        var capture = ReceiptCapture.Create(fileName, clock.UtcNow, user.Actor);
        capture.SetFileHash(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(buffer.GetBuffer().AsSpan(0, (int)buffer.Length))));
        store.Add(capture);
        store.Add(Audit(AuditAction.Created, capture, null));
        await store.SaveChangesAsync(ct);
        buffer.Position = 0;
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
        Money.EnsureCents(command.Amount);
        if (command.Amount <= 0m)
            throw new DomainException("receipt.amount_not_positive", "Enter the amount paid.");
        var vendor = command.VendorId is { } vendorId ? await store.GetVendorAsync(vendorId, ct) ?? throw new EntityNotFoundException("vendor", vendorId) : null;
        var category = command.CategoryId ?? vendor?.DefaultExpenseAccountId
            ?? throw new DomainException("receipt.category_required", "Choose what the expense was for.");

        // All or nothing: the receipt's version stops a second press, and a failure leaves no expense behind.
        await using var transaction = await unit.BeginAsync(ct);
        var spend = await banking.RecordAsync(new RecordBankTransactionCommand(
            BankTransactionKind.Spend, command.AccountId, command.Date, [new BankSplit(category, command.Amount, command.Memo)],
            Payee: command.Payee ?? vendor?.DisplayName ?? capture.VendorName, Memo: command.Memo, VendorId: vendor?.Id), ct);

        foreach (var file in await attachments.ListAsync(EntityType, capture.Id, ct))
            if (await attachments.GetContentAsync(file.Id, ct) is { } bytes)
                await attachments.AddAsync("bank_transaction", spend.Id, file.FileName, new MemoryStream(bytes), ct);

        var before = Snapshot(capture);
        capture.Recorded(spend.Id);
        store.Add(Audit(AuditAction.Updated, capture, before));
        await store.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return spend;
    }

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
