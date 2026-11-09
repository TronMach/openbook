using System.Text.Json;
using OpenBook.Application.Accounts;
using OpenBook.Application.Ledger;
using OpenBook.Application.Reporting;
using OpenBook.Application.Sales;
using OpenBook.Domain.Accounts;
using OpenBook.Domain.Auditing;
using OpenBook.Domain.Common;
using OpenBook.Domain.Ledger;
using OpenBook.Domain.SalesTax;

namespace OpenBook.Application.SalesTax;

/// <param name="Adjustment">Positive for a discount or credit from the agency; negative for a penalty or interest.</param>
/// <param name="PeriodEnd">The last day of the filing period being paid.</param>
public sealed record PaySalesTaxCommand(
    Guid AgencyId,
    DateOnly Date,
    Guid PaymentAccountId,
    decimal Amount,
    decimal Adjustment = 0m,
    Guid? AdjustmentAccountId = null,
    DateOnly? PeriodEnd = null,
    string? Reference = null,
    string? Memo = null);

/// <summary>A sales tax return still to file: the agency, its period, when it's due and what's owed for it.</summary>
public sealed record ReturnDue(TaxAgency Agency, DateOnly From, DateOnly To, DateOnly DueOn, decimal Owed);

/// <summary>Records that an agency's return for the period ending <paramref name="PeriodTo"/> (any day of it) was filed.</summary>
public sealed record RecordFilingCommand(Guid AgencyId, DateOnly PeriodTo, DateOnly FiledOn, decimal AmountReported, string? Reference = null);

/// <summary>Paying collected sales tax to an agency, and what's owed to each.</summary>
public sealed class SalesTaxPaymentService(
    ISalesTaxStore store,
    SalesAccounts salesAccounts,
    AccountService accounts,
    ReportLedger ledger,
    PostingEngine engine,
    IClock clock,
    ICurrentUser user)
{
    public const string EntityType = "sales_tax_payment";

    public Task<SalesTaxPayment?> GetAsync(Guid id, CancellationToken ct = default) => store.GetPaymentAsync(id, ct);

    public Task<IReadOnlyList<SalesTaxPayment>> ListAsync(Guid? agencyId = null, CancellationToken ct = default) => store.ListPaymentsAsync(agencyId, ct);

    /// <summary>What's owed to each agency as of a date: its liability account's balance in the books (accrual).</summary>
    /// <param name="basis">Cash: what's been collected (the ledger's cash-basis lines) less what's been paid.</param>
    public async Task<IReadOnlyDictionary<Guid, decimal>> OwedAsync(DateOnly asOf, ReportBasis basis = ReportBasis.Accrual, CancellationToken ct = default)
    {
        var agencies = await store.ListAgenciesAsync(ct);
        if (agencies.Count == 0)
            return new Dictionary<Guid, decimal>();
        // Only the agencies' accounts: on the accrual basis the database adds up just their lines.
        var balances = await ledger.BalancesAsync(basis, agencies.Select(a => a.LiabilityAccountId).ToList(), asOf, ct);
        return agencies.ToDictionary(a => a.Id, a => -balances.GetValueOrDefault(a.LiabilityAccountId));
    }

    /// <summary>
    /// What's left to pay for the period ending on the date: the balance then, less payments made since for that period.
    /// A payment dated after the period doesn't touch the balance on its last day, so it would otherwise look unpaid.
    /// </summary>
    public async Task<IReadOnlyDictionary<Guid, decimal>> OwedForPeriodAsync(DateOnly periodEnd, CancellationToken ct = default)
    {
        var owed = await OwedAsync(periodEnd, ct: ct);
        return LessPaidSince(owed, periodEnd, await store.ListPaymentsAsync(null, ct));
    }

    private static Dictionary<Guid, decimal> LessPaidSince(IReadOnlyDictionary<Guid, decimal> owed, DateOnly periodEnd, IReadOnlyList<SalesTaxPayment> payments)
    {
        var paid = payments
            .Where(p => p.Status == DocumentStatus.Posted && p.PeriodEnd == periodEnd && p.Date > periodEnd)
            .GroupBy(p => p.AgencyId)
            .ToDictionary(g => g.Key, g => g.Sum(p => p.LiabilitySettled));
        return owed.ToDictionary(o => o.Key, o => o.Value - paid.GetValueOrDefault(o.Key));
    }

    /// <summary>
    /// Returns still to file: for each active agency, the last whole filing period if anything is owed for it, with the
    /// date it's due (the period's end plus the agency's due days), soonest first.
    /// </summary>
    public async Task<IReadOnlyList<ReturnDue>> ReturnsDueAsync(DateOnly today, CancellationToken ct = default)
    {
        var due = new List<ReturnDue>();
        var active = (await store.ListAgenciesAsync(ct)).Where(a => a.IsActive).ToList();
        if (active.Count == 0)
            return due;
        var filed = (await store.ListFilingsAsync(null, ct)).Select(f => (f.AgencyId, f.PeriodTo)).ToHashSet();
        var payments = await store.ListPaymentsAsync(null, ct);
        // Agencies filing on the same schedule share a period end, so each balance is read once.
        var byPeriodEnd = new Dictionary<DateOnly, IReadOnlyDictionary<Guid, decimal>>();
        foreach (var agency in active)
        {
            var (from, to) = FilingPeriods.LastCompleted(agency.FilingFrequency, today);
            if (!byPeriodEnd.TryGetValue(to, out var forPeriod))
                byPeriodEnd[to] = forPeriod = LessPaidSince(await OwedAsync(to, ct: ct), to, payments);
            var owed = forPeriod.GetValueOrDefault(agency.Id);
            if (owed > 0m && !filed.Contains((agency.Id, to)))
                due.Add(new ReturnDue(agency, from, to, agency.DueOn(to), owed));
        }

        return [.. due.OrderBy(d => d.DueOn).ThenBy(d => d.Agency.Name, StringComparer.OrdinalIgnoreCase)];
    }

    public const string FilingEntity = "sales_tax_filing";

    public Task<IReadOnlyList<SalesTaxFiling>> FilingsAsync(Guid? agencyId = null, CancellationToken ct = default) => store.ListFilingsAsync(agencyId, ct);

    /// <summary>Notes a return as filed for the agency's period containing <see cref="RecordFilingCommand.PeriodTo"/>; it then leaves the to-do list.</summary>
    public async Task<SalesTaxFiling> RecordFilingAsync(RecordFilingCommand command, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        var agency = await store.GetAgencyAsync(command.AgencyId, ct) ?? throw new EntityNotFoundException(SalesTaxService.AgencyEntity, command.AgencyId);
        var (from, to) = FilingPeriods.Containing(agency.FilingFrequency, command.PeriodTo);
        if ((await store.ListFilingsAsync(agency.Id, ct)).Any(f => f.PeriodTo == to))
            throw new DomainException("taxfiling.already_filed", $"The {agency.Name} return for {from:MMM d} – {to:MMM d, yyyy} is recorded as filed already.");
        var filing = SalesTaxFiling.Create(agency.Id, from, to, command.FiledOn, command.AmountReported, command.Reference, user.Actor, clock.UtcNow);
        store.Add(filing);
        store.Add(AuditRecord.Create(clock.UtcNow, user.Actor, AuditAction.Created, FilingEntity, filing.Id, null, FilingSnapshot(filing)));
        await store.SaveChangesAsync(ct);
        return filing;
    }

    public async Task RemoveFilingAsync(Guid id, CancellationToken ct = default)
    {
        var filing = await store.GetFilingAsync(id, ct) ?? throw new EntityNotFoundException(FilingEntity, id);
        store.Remove(filing);
        store.Add(AuditRecord.Create(clock.UtcNow, user.Actor, AuditAction.Deleted, FilingEntity, filing.Id, FilingSnapshot(filing), null));
        await store.SaveChangesAsync(ct);
    }

    private static string FilingSnapshot(SalesTaxFiling f) => System.Text.Json.JsonSerializer.Serialize(new
    {
        f.AgencyId,
        f.PeriodFrom,
        f.PeriodTo,
        f.FiledOn,
        f.AmountReported,
        f.Reference,
    });

    public async Task<SalesTaxPayment> PayAsync(PaySalesTaxCommand command, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        var agency = await store.GetAgencyAsync(command.AgencyId, ct) ?? throw new EntityNotFoundException(SalesTaxService.AgencyEntity, command.AgencyId);
        var paidFrom = await salesAccounts.GetPaymentAccountAsync(command.PaymentAccountId, ct);
        Guid? adjustmentAccountId = null;
        if (command.Adjustment != 0m)
        {
            var adjustmentAccount = (await accounts.GetChartAsync(ct)).Get(command.AdjustmentAccountId
                ?? throw new DomainException("taxpayment.adjustment_account_required", "Choose the account for the adjustment."));
            if (adjustmentAccount.Type.ClassOf() is not (AccountClass.Income or AccountClass.Expense))
                throw new DomainException("taxpayment.adjustment_account_type", $"'{adjustmentAccount.Name}' isn't an income or expense account.");
            if (!adjustmentAccount.IsActive)
                throw new DomainException("taxpayment.adjustment_account_inactive", $"'{adjustmentAccount.Name}' is inactive.");
            adjustmentAccountId = adjustmentAccount.Id;
        }

        var id = Guid.CreateVersion7();
        var memo = $"Sales tax payment · {agency.Name}";
        var entry = await engine.StageAsync(new PostJournalEntryCommand(
            command.Date,
            SalesTaxPayment.Postings(agency.LiabilityAccountId, paidFrom.Id, command.Amount, command.Adjustment, adjustmentAccountId, memo),
            memo, SalesTaxPayment.Source, id), ct);

        var payment = SalesTaxPayment.Create(id, agency, command.Date, command.Amount, paidFrom.Id, entry.Id, clock.UtcNow,
            command.Adjustment, adjustmentAccountId, command.PeriodEnd, command.Reference, command.Memo);
        store.Add(payment);
        store.Add(AuditRecord.Create(clock.UtcNow, user.Actor, AuditAction.Created, EntityType, id, null, Snapshot(payment)));
        await store.SaveChangesAsync(ct);
        return payment;
    }

    public async Task<SalesTaxPayment> VoidAsync(Guid id, string reason, CancellationToken ct = default)
    {
        var payment = await store.GetPaymentAsync(id, ct) ?? throw new EntityNotFoundException(EntityType, id);
        if (payment.Status == DocumentStatus.Voided)
            throw new DomainException("taxpayment.already_voided", "This sales tax payment is already voided.");
        var before = Snapshot(payment);
        await engine.StageVoidAsync(payment.JournalEntryId, reason, fromDocument: true, ct);
        payment.Void();
        store.Add(AuditRecord.Create(clock.UtcNow, user.Actor, AuditAction.Voided, EntityType, id, before, Snapshot(payment)));
        await store.SaveChangesAsync(ct);
        return payment;
    }

    private static string Snapshot(SalesTaxPayment p) => JsonSerializer.Serialize(new
    {
        p.AgencyId,
        p.Date,
        p.PeriodEnd,
        p.Amount,
        p.PaymentAccountId,
        p.Adjustment,
        p.AdjustmentAccountId,
        p.Reference,
        p.Memo,
        Status = p.Status.ToString(),
    });
}
