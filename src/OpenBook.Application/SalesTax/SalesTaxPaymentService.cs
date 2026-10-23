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
    public async Task<IReadOnlyDictionary<Guid, decimal>> OwedAsync(DateOnly asOf, CancellationToken ct = default)
    {
        var agencies = await store.ListAgenciesAsync(ct);
        var balances = (await ledger.LinesAsync(ReportBasis.Accrual, null, asOf, ct))
            .GroupBy(l => l.AccountId)
            .ToDictionary(g => g.Key, g => g.Sum(l => l.Amount));
        return agencies.ToDictionary(a => a.Id, a => -balances.GetValueOrDefault(a.LiabilityAccountId));
    }

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
