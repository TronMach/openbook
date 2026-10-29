using System.Text.Json;
using OpenBook.Application.Accounts;
using OpenBook.Application.Banking;
using OpenBook.Application.Ledger;
using OpenBook.Application.Purchases;
using OpenBook.Domain.Accounts;
using OpenBook.Domain.Auditing;
using OpenBook.Domain.Banking;
using OpenBook.Domain.Common;
using OpenBook.Domain.Sales;

namespace OpenBook.Application.Sales;

/// <summary>A category line of a bill or expense that could be billed to a customer, and whether it is.</summary>
public sealed record BillableLine(int LineNumber, Guid AccountId, string Description, decimal Cost, BillableExpense? Billable);

public sealed record MarkBillableCommand(BillableSource Kind, Guid DocumentId, int LineNumber, Guid CustomerId, decimal MarkupPercent = 0m);

/// <summary>
/// Billable expenses: costs on bills and expenses marked to be passed on to a customer, then added to one of their
/// invoices as "Billable Expenses" lines (a service item with income "Billable Expense Income", created on first use).
/// </summary>
public sealed class BillableExpenseService(
    ISalesStore store, IPurchasesStore purchases, IBankingStore bank, ILedgerStore ledger, ItemService items, AccountService accounts, IClock clock, ICurrentUser user)
{
    public const string EntityType = "billable_expense";
    public const string ItemName = "Billable Expenses";
    public const string IncomeAccountName = "Billable Expense Income";

    /// <summary>The price source on invoice lines made from billable expenses.</summary>
    public const string PriceSource = "Billable expense";

    /// <summary>The bill's or expense's category lines with a cost, and which are billable already.</summary>
    public async Task<IReadOnlyList<BillableLine>> LinesAsync(BillableSource kind, Guid documentId, CancellationToken ct = default)
    {
        var marked = (await store.ListBillableForDocumentAsync(kind, documentId, ct)).ToDictionary(b => b.LineNumber);
        var (_, _, lines) = await DocumentAsync(kind, documentId, ct);
        return lines.Select(l => l with { Billable = marked.GetValueOrDefault(l.LineNumber) }).ToList();
    }

    /// <summary>Costs waiting to be billed to the customer, oldest first.</summary>
    public async Task<IReadOnlyList<BillableExpense>> OpenAsync(Guid customerId, CancellationToken ct = default) =>
        (await store.ListBillableExpensesAsync(customerId, ct)).Where(b => b.InvoiceId is null).ToList();

    public Task<IReadOnlyList<BillableExpense>> ListAsync(Guid? customerId = null, CancellationToken ct = default) => store.ListBillableExpensesAsync(customerId, ct);

    public async Task<BillableExpense> MarkAsync(MarkBillableCommand command, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        var customer = await store.GetCustomerAsync(command.CustomerId, ct) ?? throw new EntityNotFoundException("customer", command.CustomerId);
        if (!customer.IsActive)
            throw new DomainException("customer.inactive", $"{customer.DisplayName} is inactive.");
        var (date, prefix, lines) = await DocumentAsync(command.Kind, command.DocumentId, ct);
        var line = lines.FirstOrDefault(l => l.LineNumber == command.LineNumber)
            ?? throw new DomainException("billable.line_invalid", "That line can't be billed: choose a category line with a cost.");
        if ((await store.ListBillableForDocumentAsync(command.Kind, command.DocumentId, ct)).Any(b => b.LineNumber == command.LineNumber))
            throw new DomainException("billable.already_marked", "That line is already billable.");

        var billable = BillableExpense.Create(command.Kind, command.DocumentId, command.LineNumber, customer.Id, date,
            $"{prefix}: {line.Description}", line.Cost, command.MarkupPercent, clock.UtcNow);
        store.Add(billable);
        store.Add(Audit(AuditAction.Created, billable, null));
        await store.SaveChangesAsync(ct);
        return billable;
    }

    /// <summary>Stops a line being billable; refused once it's on an invoice.</summary>
    public async Task RemoveAsync(Guid id, CancellationToken ct = default)
    {
        var billable = (await store.GetBillableExpensesAsync([id], ct)).SingleOrDefault() ?? throw new EntityNotFoundException(EntityType, id);
        if (billable.InvoiceId is not null)
            throw new DomainException("billable.invoiced", $"'{billable.Description}' is on an invoice. Void the invoice first.");
        store.Remove(billable);
        store.Add(AuditRecord.Create(clock.UtcNow, user.Actor, AuditAction.Deleted, EntityType, billable.Id, Snapshot(billable), null));
        await store.SaveChangesAsync(ct);
    }

    /// <summary>The billable expenses chosen for a new invoice of the customer, checked and tracked.</summary>
    internal async Task<IReadOnlyList<BillableExpense>> ForInvoiceAsync(Guid customerId, IReadOnlyList<Guid> ids, CancellationToken ct)
    {
        if (ids.Count == 0)
            return [];
        var found = await store.GetBillableExpensesAsync(ids.Distinct().ToList(), ct);
        if (found.Count != ids.Distinct().Count())
            throw new EntityNotFoundException(EntityType, ids.First(id => found.All(b => b.Id != id)));
        foreach (var b in found)
        {
            if (b.CustomerId != customerId)
                throw new DomainException("billable.wrong_customer", $"'{b.Description}' is billable to a different customer.");
            if (b.InvoiceId is not null)
                throw new DomainException("billable.already_invoiced", $"'{b.Description}' is already on an invoice.");
        }

        return found.OrderBy(b => b.Date).ThenBy(b => b.CreatedAt).ToList();
    }

    /// <summary>
    /// Before a bill or expense is voided: refuses if any of its costs is on an invoice, else stops them being
    /// billable (in the same save as the void).
    /// </summary>
    internal static async Task ReleaseAsync(ISalesStore store, BillableSource kind, Guid documentId, IClock clock, ICurrentUser user, CancellationToken ct)
    {
        var billable = await store.ListBillableForDocumentAsync(kind, documentId, ct);
        if (billable.FirstOrDefault(b => b.InvoiceId is not null) is { } invoiced)
            throw new DomainException("billable.invoiced", $"'{invoiced.Description}' has been billed to a customer. Void that invoice first.");
        foreach (var b in billable)
        {
            store.Remove(b);
            store.Add(AuditRecord.Create(clock.UtcNow, user.Actor, AuditAction.Deleted, EntityType, b.Id, Snapshot(b), null));
        }
    }

    /// <summary>The "Billable Expenses" item, found or created with "Billable Expense Income".</summary>
    internal async Task<Guid> ItemIdAsync(CancellationToken ct)
    {
        var item = (await items.ListAsync(includeInactive: false, ct)).FirstOrDefault(i => string.Equals(i.Name, ItemName, StringComparison.OrdinalIgnoreCase));
        if (item is not null)
            return item.Id;
        var chart = await accounts.GetChartAsync(ct);
        var income = chart.Ordered(includeInactive: false).Select(x => x.Account)
            .FirstOrDefault(a => a.Type.ClassOf() == AccountClass.Income && string.Equals(a.Name, IncomeAccountName, StringComparison.OrdinalIgnoreCase))
            ?? await accounts.CreateAsync(new CreateAccountCommand(IncomeAccountName, AccountType.Income,
                Description: "Costs passed on to customers on their invoices."), ct);
        return (await items.CreateAsync(new ItemDetails(ItemName, ItemType.Service, income.Id, 0m, "Costs passed on to the customer", Taxable: false), ct)).Id;
    }

    private async Task<(DateOnly Date, string Prefix, IReadOnlyList<BillableLine> Lines)> DocumentAsync(BillableSource kind, Guid documentId, CancellationToken ct)
    {
        var chart = await accounts.GetChartAsync(ct);
        if (kind == BillableSource.Bill)
        {
            var bill = await purchases.GetBillAsync(documentId, ct) ?? throw new EntityNotFoundException("bill", documentId);
            if (bill.Status != DocumentStatus.Posted)
                throw new DomainException("billable.document_voided", "That bill is voided.");
            var vendor = await purchases.GetVendorAsync(bill.VendorId, ct);
            return (bill.Date, vendor?.DisplayName ?? "Bill",
                bill.Lines.Where(l => l.ItemId is null && l.Amount > 0m)
                    .Select(l => new BillableLine(l.LineNumber, l.AccountId, l.Description ?? chart.Get(l.AccountId).Name, l.Amount, null)).ToList());
        }

        var spend = await bank.GetTransactionAsync(documentId, ct) ?? throw new EntityNotFoundException("bank_transaction", documentId);
        if (spend.Kind != BankTransactionKind.Spend || spend.Status != BankTransactionStatus.Posted)
            throw new DomainException("billable.document_invalid", "Only posted expenses can be billed on.");
        var entry = await ledger.GetEntryAsync(spend.JournalEntryId, ct) ?? throw new EntityNotFoundException("journal_entry", spend.JournalEntryId);
        return (spend.Date, spend.Payee ?? "Expense",
            entry.Lines.Where(l => l.AccountId != spend.AccountId && l.Amount > 0m)
                .Select(l => new BillableLine(l.LineNumber, l.AccountId, l.Memo ?? spend.Memo ?? chart.Get(l.AccountId).Name, l.Amount, null)).ToList());
    }

    private AuditRecord Audit(AuditAction action, BillableExpense b, string? before) =>
        AuditRecord.Create(clock.UtcNow, user.Actor, action, EntityType, b.Id, before, Snapshot(b));

    internal static string Snapshot(BillableExpense b) => JsonSerializer.Serialize(new
    {
        Kind = b.Kind.ToString(),
        b.DocumentId,
        b.LineNumber,
        b.CustomerId,
        b.Date,
        b.Description,
        b.Cost,
        b.MarkupPercent,
        b.InvoiceId,
    });
}
