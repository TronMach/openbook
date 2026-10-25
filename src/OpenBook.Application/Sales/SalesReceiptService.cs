using System.Text.Json;
using OpenBook.Application.Inventory;
using OpenBook.Application.Ledger;
using OpenBook.Domain.Auditing;
using OpenBook.Domain.Common;
using OpenBook.Domain.Inventory;
using OpenBook.Domain.Sales;

namespace OpenBook.Application.Sales;

/// <param name="TaxCodeId">Null takes the customer's tax code; <see cref="Guid.Empty"/> charges no sales tax.</param>
public sealed record CreateSalesReceiptCommand(
    Guid? CustomerId,
    DateOnly Date,
    Guid DepositAccountId,
    IReadOnlyList<SalesLineDraft> Lines,
    string? Number = null,
    string? Reference = null,
    string? Memo = null,
    Guid? TaxCodeId = null,
    Guid? LocationId = null,
    IReadOnlyCollection<Guid>? TagIds = null);

public sealed class SalesReceiptService(
    ISalesStore store,
    SalesAccounts salesAccounts,
    SalesLineBuilder lineBuilder,
    PostingEngine engine,
    InventoryService inventory,
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

        var (lines, taxCodeId, exemption) = await lineBuilder.BuildAsync(command.Lines, customer, command.TaxCodeId, command.Date, ct);
        var stock = await inventory.PlanSaleAsync(lines, command.Date, ct);

        var id = Guid.CreateVersion7();
        var memo = customer is null ? $"Sales receipt {number}" : $"Sales receipt {number} · {customer.DisplayName}";
        var entry = await engine.StageAsync(
            new PostJournalEntryCommand(command.Date, [.. SalesLine.Postings(deposit.Id, lines, memo), .. stock.Postings], memo, SalesReceipt.Source, id,
                LocationId: command.LocationId, TagIds: command.TagIds), ct);
        inventory.Apply(stock, InventorySources.SalesReceipt, id);

        var receipt = SalesReceipt.Create(id, number, customer?.Id, command.Date, deposit.Id, lines, entry.Id, clock.UtcNow, command.Reference, command.Memo, taxCodeId, exemption);
        receipt.SetLocation(command.LocationId);
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
        if (receipt.DepositId is not null)
            throw new DomainException("receipt.deposited", $"Sales receipt {receipt.Number} is in a bank deposit. Void the deposit first.");

        var stock = await inventory.PlanReversalAsync(InventorySources.SalesReceipt, receipt.Id, receipt.Date, $"sales receipt {receipt.Number}", ct);
        var before = Snapshot(receipt);
        await engine.StageVoidAsync(receipt.JournalEntryId, reason, fromDocument: true, ct);
        inventory.Apply(stock, InventorySources.SalesReceipt, receipt.Id);
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
        r.DepositId,
        r.TaxCodeId,
        r.TaxExemption,
        Lines = r.Lines.Select(l => new { Kind = l.Kind.ToString(), l.ItemId, l.Description, l.Quantity, l.Rate, l.Amount, l.Taxable, l.TaxRateId }),
    });
}
