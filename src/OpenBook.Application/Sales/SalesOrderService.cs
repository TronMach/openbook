using System.Text.Json;
using OpenBook.Application.Inventory;
using OpenBook.Domain.Auditing;
using OpenBook.Domain.Common;
using OpenBook.Domain.Sales;

namespace OpenBook.Application.Sales;

/// <param name="Number">Defaults to the next sales order number.</param>
/// <param name="TaxCodeId">Null takes the customer's tax code; <see cref="Guid.Empty"/> charges no sales tax.</param>
public sealed record SaveSalesOrderCommand(
    Guid CustomerId,
    DateOnly Date,
    IReadOnlyList<SalesLineDraft> Lines,
    DateOnly? ExpectedDate = null,
    string? Number = null,
    string? CustomerReference = null,
    string? Memo = null,
    Guid? TaxCodeId = null,
    Guid? LocationId = null,
    Guid? WarehouseId = null);

/// <param name="Lines">How much of each item line to bill; null for everything still to invoice.</param>
public sealed record InvoiceFromSourceCommand(
    DateOnly Date,
    IReadOnlyList<InvoiceSourceLine>? Lines = null,
    decimal? Percent = null,
    string? Number = null,
    DateOnly? DueDate = null);

/// <summary>A sales order or estimate with what's been invoiced of it so far.</summary>
/// <param name="Remaining">By line number: what's left of each item line (and amount discount, 1 or 0).</param>
public sealed record InvoicingProgress(IReadOnlyList<Invoice> Invoices, IReadOnlyDictionary<int, decimal> Invoiced, IReadOnlyDictionary<int, decimal> Remaining)
{
    /// <summary>Invoiced so far, before tax.</summary>
    public decimal InvoicedSubtotal => Invoices.Where(i => i.Status == DocumentStatus.Posted).Sum(i => i.Subtotal);

    public bool Started => Invoices.Any(i => i.Status == DocumentStatus.Posted);
}

/// <summary>Sales orders: what customers have ordered, invoiced in full or in parts as it's delivered.</summary>
public sealed class SalesOrderService(ISalesStore store, SalesLineBuilder lineBuilder, InvoiceService invoices, WarehouseService warehouses, CustomFields.CustomFieldStamper customFields, IClock clock, ICurrentUser user)
{
    internal const string EntityType = "sales_order";

    public async Task<string> SuggestNumberAsync(CancellationToken ct = default) =>
        SalesAccounts.NextNumber(await store.ListSalesOrderNumbersAsync(ct));

    public Task<SalesOrder?> GetAsync(Guid id, CancellationToken ct = default) => store.GetSalesOrderAsync(id, ct);

    public Task<IReadOnlyList<SalesOrder>> ListAsync(Guid? customerId = null, CancellationToken ct = default) => store.ListSalesOrdersAsync(customerId, ct);

    public async Task<SalesOrder> CreateAsync(SaveSalesOrderCommand command, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        var number = SalesOrder.NormalizeNumber(command.Number ?? await SuggestNumberAsync(ct));
        if ((await store.ListSalesOrderNumbersAsync(ct)).Contains(number, StringComparer.OrdinalIgnoreCase))
            throw new DomainException("salesorder.number_taken", $"Sales order number {number} is already used.");
        var order = SalesOrder.Create(Guid.CreateVersion7(), number, clock.UtcNow);
        await FillAsync(order, command, ct);
        store.Add(order);
        store.Add(Audit(AuditAction.Created, order, null));
        await customFields.StampAsync(EntityType, order.Id, order.CustomerId, order.Lines.Select(l => (l.LineNumber, l.ItemId)), ct);
        await store.SaveChangesAsync(ct);
        return order;
    }

    /// <summary>Replaces everything but the number, until anything has been invoiced.</summary>
    public async Task<SalesOrder> UpdateAsync(Guid id, SaveSalesOrderCommand command, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        var order = await store.GetSalesOrderAsync(id, ct) ?? throw new EntityNotFoundException(EntityType, id);
        if ((await ProgressAsync(order, ct)).Started)
            throw new DomainException("salesorder.invoiced", $"Sales order {order.Number} has been invoiced, so it can't be changed. Cancel the rest instead.");
        var before = Snapshot(order);
        await FillAsync(order, command, ct);
        store.Add(Audit(AuditAction.Updated, order, before));
        await customFields.StampAsync(EntityType, order.Id, order.CustomerId, order.Lines.Select(l => (l.LineNumber, l.ItemId)), ct, replaceLines: true);
        await store.SaveChangesAsync(ct);
        return order;
    }

    /// <summary>Stops the rest of the order from being invoiced; what's invoiced stays.</summary>
    public async Task<SalesOrder> CancelAsync(Guid id, CancellationToken ct = default)
    {
        var order = await store.GetSalesOrderAsync(id, ct) ?? throw new EntityNotFoundException(EntityType, id);
        var before = Snapshot(order);
        order.Cancel();
        store.Add(Audit(AuditAction.Updated, order, before));
        await store.SaveChangesAsync(ct);
        return order;
    }

    public async Task<InvoicingProgress> ProgressAsync(Guid id, CancellationToken ct = default) =>
        await ProgressAsync(await store.GetSalesOrderAsync(id, ct) ?? throw new EntityNotFoundException(EntityType, id), ct);

    /// <summary>Invoices the quantities chosen (or everything left), priced as on the order, with the order's tax code.</summary>
    public async Task<Invoice> InvoiceAsync(Guid id, InvoiceFromSourceCommand command, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        var order = await store.GetSalesOrderAsync(id, ct) ?? throw new EntityNotFoundException(EntityType, id);
        if (order.Status != SalesOrderStatus.Open)
            throw new DomainException("salesorder.not_open", $"Sales order {order.Number} is {order.Status.ToString().ToLowerInvariant()}.");
        var progress = await ProgressAsync(order, ct);
        var picks = Picks(order.Lines, progress.Remaining, command);
        var (drafts, sources, completes) = PartialInvoicing.Build(order.Lines, progress.Remaining, picks);
        var memo = order.CustomerReference is null ? order.Memo : $"Your PO {order.CustomerReference}" + (order.Memo is null ? "" : $"\n{order.Memo}");
        return await invoices.CreateAsync(
            new CreateInvoiceCommand(order.CustomerId, command.Date, drafts, command.DueDate, command.Number, memo,
                TaxCodeId: order.TaxCodeId ?? Guid.Empty, LocationId: order.LocationId, WarehouseId: order.WarehouseId),
            recurringInvoiceId: null, ct, source: new InvoiceSource(order.Id, sources, completes));
    }

    /// <summary>The picks for a command: the lines given, a percentage of every line, or everything left.</summary>
    internal static IReadOnlyList<InvoiceSourceLine> Picks(IReadOnlyList<SalesLine> lines, IReadOnlyDictionary<int, decimal> remaining, InvoiceFromSourceCommand command) =>
        command.Lines is { } chosen ? chosen
        : command.Percent is { } percent ? PartialInvoicing.ByPercent(lines, remaining, percent)
        : remaining.Where(r => r.Value != 0m && lines.Any(l => l.LineNumber == r.Key && l.Kind == SalesLineKind.Item))
            .Select(r => new InvoiceSourceLine(r.Key, r.Value)).ToList();

    internal async Task<InvoicingProgress> ProgressAsync(SalesOrder order, CancellationToken ct)
    {
        var made = (await store.ListInvoicesAsync(order.CustomerId, ct)).Where(i => i.SalesOrderId == order.Id).OrderBy(i => i.Date).ToList();
        var invoiced = PartialInvoicing.Invoiced(made);
        return new InvoicingProgress(made, invoiced, PartialInvoicing.Remaining(order.Lines, invoiced));
    }

    private async Task FillAsync(SalesOrder order, SaveSalesOrderCommand command, CancellationToken ct)
    {
        var customer = await store.GetCustomerAsync(command.CustomerId, ct) ?? throw new EntityNotFoundException("customer", command.CustomerId);
        if (!customer.IsActive)
            throw new DomainException("customer.inactive", $"{customer.DisplayName} is inactive.");
        var (lines, taxCodeId, exemption) = await lineBuilder.BuildAsync(command.Lines ?? [], customer, command.TaxCodeId, command.Date, ct);
        var warehouseId = await warehouses.CheckAsync(command.WarehouseId, ct);
        order.Update(customer.Id, command.Date, command.ExpectedDate, lines, command.CustomerReference, command.Memo, taxCodeId, exemption,
            command.LocationId, warehouseId);
    }

    private AuditRecord Audit(AuditAction action, SalesOrder o, string? before) =>
        AuditRecord.Create(clock.UtcNow, user.Actor, action, EntityType, o.Id, before, Snapshot(o));

    internal static string Snapshot(SalesOrder o) => JsonSerializer.Serialize(new
    {
        o.Number,
        o.CustomerId,
        o.Date,
        o.ExpectedDate,
        o.CustomerReference,
        o.Total,
        Status = o.Status.ToString(),
        o.Memo,
        o.TaxCodeId,
        o.TaxExemption,
        o.LocationId,
        o.WarehouseId,
        Lines = o.Lines.Select(l => new { l.LineNumber, Kind = l.Kind.ToString(), l.ItemId, l.Description, l.Quantity, l.Rate, l.Amount, l.Taxable }),
    });
}
