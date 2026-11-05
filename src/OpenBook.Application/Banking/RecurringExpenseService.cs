using OpenBook.Application.Companies;
using System.Text.Json;
using OpenBook.Application.Accounts;
using OpenBook.Application.Ledger;
using OpenBook.Domain.Accounts;
using OpenBook.Domain.Auditing;
using OpenBook.Domain.Banking;
using OpenBook.Domain.Common;
using OpenBook.Domain.Ledger;

namespace OpenBook.Application.Banking;

public sealed record RecurringExpenseResult(RecurringExpense Template, IReadOnlyList<BankTransaction> Created, string? Error);

/// <summary>Recurring expenses: templates that record a spend from a bank or card account on a schedule.</summary>
public sealed class RecurringExpenseService(
    IUnitOfWork unit, IBankingStore store, Purchases.IPurchasesStore purchases, AccountService accounts, BankingService banking, PostingEngine engine, IClock clock, ICurrentUser user)
{
    public const string EntityType = "recurring_expense";
    public const int MaxCatchUp = 60;

    public Task<IReadOnlyList<RecurringExpense>> ListAsync(CancellationToken ct = default) => store.ListRecurringExpensesAsync(ct);

    public Task<RecurringExpense?> GetAsync(Guid id, CancellationToken ct = default) => store.GetRecurringExpenseAsync(id, ct);

    public async Task<RecurringExpense> CreateAsync(RecurringExpenseDetails details, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(details);
        await CheckAsync(details, ct);
        var template = RecurringExpense.Create(details, clock.UtcNow);
        store.Add(template);
        store.Add(Audit(AuditAction.Created, template, before: null));
        await store.SaveChangesAsync(ct);
        return template;
    }

    public async Task<RecurringExpense> UpdateAsync(Guid id, RecurringExpenseDetails details, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(details);
        var template = await store.GetRecurringExpenseAsync(id, ct) ?? throw new EntityNotFoundException(EntityType, id);
        await CheckAsync(details, ct);
        var before = Snapshot(template);
        template.Update(details);
        store.Add(Audit(AuditAction.Updated, template, before));
        await store.SaveChangesAsync(ct);
        return template;
    }

    public Task<RecurringExpense> SetActiveAsync(Guid id, bool active, CancellationToken ct = default) => SetActiveAsync(id, active, false, ct);

    /// <param name="catchUp">Turning it back on makes every date it missed; otherwise those are passed over.</param>
    public async Task<RecurringExpense> SetActiveAsync(Guid id, bool active, bool catchUp, CancellationToken ct = default)
    {
        var template = await store.GetRecurringExpenseAsync(id, ct) ?? throw new EntityNotFoundException(EntityType, id);
        var before = Snapshot(template);
        if (active)
            template.Reactivate(catchUp ? null : CompanyTime.Today);
        else
            template.Deactivate();
        store.Add(Audit(AuditAction.Updated, template, before));
        await store.SaveChangesAsync(ct);
        return template;
    }

    /// <summary>
    /// Records every expense due on or before <paramref name="today"/> through <see cref="BankingService.RecordAsync"/>,
    /// each in its own save. A template stops at its first refusal (an inactive account, a closed period).
    /// </summary>
    /// <param name="automaticOnly">True for the background job, which only runs templates set to record automatically.</param>
    public async Task<IReadOnlyList<RecurringExpenseResult>> CreateDueAsync(DateOnly today, bool automaticOnly, CancellationToken ct = default)
    {
        var results = new List<RecurringExpenseResult>();
        foreach (var listed in await store.ListRecurringExpensesAsync(ct))
        {
            if (!listed.IsDue(today) || (automaticOnly && !listed.AutoCreate))
                continue;

            // Afresh: a refused occurrence below forgets everything this scope was tracking.
            var template = await store.GetRecurringExpenseAsync(listed.Id, ct);
            if (template is null)
                continue;


            var created = new List<BankTransaction>();
            string? error = null;
            while (template.IsDue(today) && created.Count < MaxCatchUp)
            {
                try
                {
                    // The document and the template's count in one transaction, so a failure in between can't make it twice.
                    await using var transaction = await unit.BeginAsync(ct);
                    var spend = await banking.RecordAsync(new RecordBankTransactionCommand(
                        BankTransactionKind.Spend, template.AccountId, template.NextDate!.Value,
                        template.Lines.OrderBy(l => l.LineNumber).Select(l => l.ToSplit()).ToList(),
                        template.Payee, Memo: template.Memo ?? template.Name, VendorId: template.VendorId, LocationId: template.LocationId, TagIds: template.TagIds), ct);
                    template.RecordCreated(spend.Id);
                    await store.SaveChangesAsync(ct);
                    await transaction.CommitAsync(ct);
                    created.Add(spend);
                }
                catch (DomainException ex)
                {
                    unit.DiscardPending();
                    error = ex.Message;
                    break;
                }
            }

            results.Add(new RecurringExpenseResult(template, created, error));
        }

        return results;
    }

    private async Task CheckAsync(RecurringExpenseDetails details, CancellationToken ct)
    {
        var account = (await accounts.GetChartAsync(ct)).Find(details.AccountId) ?? throw new EntityNotFoundException("account", details.AccountId);
        if (account.Type is not (AccountType.Bank or AccountType.CreditCard))
            throw new DomainException("recurringexpense.account_type", $"'{account.Name}' isn't a bank or credit card account.");
        if (!account.IsActive)
            throw new DomainException("account.inactive", $"'{account.Name}' is inactive.");
        if (details.VendorId is { } vendorId && vendorId != Guid.Empty)
        {
            var vendor = await purchases.GetVendorAsync(vendorId, ct) ?? throw new EntityNotFoundException("vendor", vendorId);
            if (!vendor.IsActive)
                throw new DomainException("vendor.inactive", $"{vendor.DisplayName} is inactive.");
        }

        await engine.EnsureSegmentsAsync((details.Lines ?? []).Select(l => l.ClassId), details.LocationId == Guid.Empty ? null : details.LocationId,
            Recurrence.ParseTags(Recurrence.JoinTags(details.TagIds)), ct);
    }

    private AuditRecord Audit(AuditAction action, RecurringExpense t, string? before) =>
        AuditRecord.Create(clock.UtcNow, user.Actor, action, EntityType, t.Id, before, Snapshot(t));

    private static string Snapshot(RecurringExpense t) => JsonSerializer.Serialize(new
    {
        t.Name,
        t.AccountId,
        t.VendorId,
        t.Payee,
        t.Memo,
        Frequency = t.Frequency.ToString(),
        t.Interval,
        t.StartDate,
        t.EndDate,
        t.MaxOccurrences,
        t.AutoCreate,
        t.IsActive,
        t.LocationId,
        t.Tags,
        Lines = t.Lines.Select(l => new { l.AccountId, l.Amount, l.Description, l.ClassId }),
    });
}
