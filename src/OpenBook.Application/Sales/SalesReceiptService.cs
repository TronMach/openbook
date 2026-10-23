using System.Text.Json;
using OpenBook.Application.Ledger;
using OpenBook.Domain.Auditing;
using OpenBook.Domain.Common;
using OpenBook.Domain.Sales;

namespace OpenBook.Application.Sales;

public sealed record CreateSalesReceiptCommand(
    Guid? CustomerId,
    DateOnly Date,
    Guid DepositAccountId,
    IReadOnlyList<SalesLineDraft> Lines,
    string? Number = null,
    string? Reference = null,
    string? Memo = null);

public sealed class SalesReceiptService(
    ISalesStore store,
    SalesAccounts salesAccounts,
    PostingEngine engine,
    IClock clock,
    ICurrentUser user)
{
    private const string EntityType = "sales_receipt";

    public async Task<string> SuggestNumberAsync(CancellationToken ct = default) =>
        SalesAccounts.NextNumber(await store.ListReceiptNumbersAsync(ct));

    public Task<SalesReceipt?> GetAsync(Guid id, CancellationToken ct = default) => store.GetReceiptAsync(id, ct);

    public Task<IReadOnlyList<SalesReceipt>> ListAsync(CancellationToken ct = default) => store.ListReceiptsAsync(ct);

    public async Task<SalesReceipt> CreateAsync(CreateSalesReceiptCommand command, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(command.Lines);

        Customer? customer = null;
        if (command.CustomerId is { } customerId)
        {
            customer = await store.GetCustomerAsync(customerId, ct) ?? throw new EntityNotFoundException("customer", customerId);
            if (!customer.IsActive)
                throw new DomainException("customer.inactive", $"{customer.DisplayName} is inactive.");
        }

        var deposit = await salesAccounts.GetDepositAccountAsync(command.DepositAccountId, ct);
        var number = command.Number?.Trim() is { Length: > 0 } given ? given : await SuggestNumberAsync(ct);
        if ((await store.ListReceiptNumbersAsync(ct)).Contains(number, StringComparer.OrdinalIgnoreCase))
            throw new DomainException("receipt.number_taken", $"Sales receipt number {number} is already used.");

        var items = await store.GetItemsAsync(command.Lines.Where(l => l.ItemId is not null).Select(l => l.ItemId!.Value).Distinct().ToList(), ct);
        var lines = SalesLine.FromDrafts(command.Lines, items);

        var id = Guid.CreateVersion7();
        var memo = customer is null ? $"Sales receipt {number}" : $"Sales receipt {number} · {customer.DisplayName}";
        var entry = await engine.StageAsync(
            new PostJournalEntryCommand(command.Date, SalesLine.Postings(deposit.Id, lines, memo), memo, SalesReceipt.Source, id), ct);

        var receipt = SalesReceipt.Create(id, number, customer?.Id, command.Date, deposit.Id, lines, entry.Id, clock.UtcNow, command.Reference, command.Memo);
        store.Add(receipt);
        store.Add(Audit(AuditAction.Created, receipt, before: null));
        await store.SaveChangesAsync(ct);
        return receipt;
    }

    public async Task<SalesReceipt> VoidAsync(Guid id, string reason, CancellationToken ct = default)
    {
        var receipt = await store.GetReceiptAsync(id, ct) ?? throw new EntityNotFoundException(EntityType, id);
        if (receipt.Status == DocumentStatus.Voided)
            throw new DomainException("receipt.already_voided", $"Sales receipt {receipt.Number} is already voided.");

        var before = Snapshot(receipt);
        await engine.StageVoidAsync(receipt.JournalEntryId, reason, fromDocument: true, ct);
        receipt.Void();
        store.Add(Audit(AuditAction.Voided, receipt, before));
        await store.SaveChangesAsync(ct);
        return receipt;
    }

    private AuditRecord Audit(AuditAction action, SalesReceipt r, string? before) =>
        AuditRecord.Create(clock.UtcNow, user.Actor, action, EntityType, r.Id, before, Snapshot(r));

    private static string Snapshot(SalesReceipt r) => JsonSerializer.Serialize(new
    {
        r.Number,
        r.CustomerId,
        r.Date,
        r.DepositAccountId,
        r.Total,
        r.Reference,
        r.Memo,
        Status = r.Status.ToString(),
        Lines = r.Lines.Select(l => new { l.ItemId, l.Description, l.Quantity, l.Rate, l.Amount }),
    });
}
