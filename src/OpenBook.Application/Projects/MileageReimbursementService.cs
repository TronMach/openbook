using OpenBook.Application.Accounts;
using OpenBook.Application.Purchases;
using OpenBook.Domain.Accounts;
using OpenBook.Domain.Auditing;
using OpenBook.Domain.Common;
using OpenBook.Domain.Projects;
using OpenBook.Domain.Purchases;

namespace OpenBook.Application.Projects;

/// <summary>
/// Pays people back for business trips in their own cars: a bill to the person's vendor record for their trips not
/// paid back yet (each at its year's mileage rate) on "Mileage Reimbursement" (expense, created on first use), paid
/// like any bill. A trip whose bill is voided counts as not paid back again. One transaction.
/// </summary>
public sealed class MileageReimbursementService(
    IProjectStore store, IPurchasesStore purchases, BillService bills, MileageService mileage, AccountService accounts, IUnitOfWork unitOfWork,
    IClock clock, ICurrentUser user)
{
    public const string AccountName = "Mileage Reimbursement";

    /// <summary>The person's trips through the date not paid back yet (or whose bill was voided).</summary>
    public async Task<IReadOnlyList<MileageTrip>> OwedAsync(Guid workerId, DateOnly through, CancellationToken ct = default)
    {
        var trips = (await store.ListMileageAsync(null, through, null, ct)).Where(t => t.WorkerId == workerId).ToList();
        var billIds = trips.Select(t => t.ReimbursedBillId).OfType<Guid>().Distinct().ToList();
        var standing = billIds.Count == 0 ? [] : (await purchases.GetBillsAsync(billIds, ct)).Where(b => b.Status == DocumentStatus.Posted).Select(b => b.Id).ToHashSet();
        return [.. trips.Where(t => t.ReimbursedBillId is not { } bill || !standing.Contains(bill)).OrderBy(t => t.Date)];
    }

    public async Task<Bill> PayBackAsync(Guid workerId, DateOnly through, DateOnly billDate, CancellationToken ct = default)
    {
        var worker = await store.GetWorkerAsync(workerId, ct) ?? throw new EntityNotFoundException(WorkerService.EntityType, workerId);
        if (worker.VendorId is not { } vendorId)
            throw new DomainException("mileage.worker_vendor", $"Link {worker.Name} to a vendor record on People and rates first: the bill to pay them back goes to it.");
        await using var transaction = await unitOfWork.BeginAsync(ct);
        var trips = await OwedAsync(workerId, through, ct);
        if (trips.Count == 0)
            throw new DomainException("mileage.nothing_to_pay_back", $"{worker.Name} has no trips through {through:MMM d, yyyy} waiting to be paid back.");
        var rates = await mileage.RatesAsync(ct);
        var account = (await accounts.GetChartAsync(ct)).Ordered(includeInactive: false).Select(x => x.Account)
            .FirstOrDefault(a => a.Type == AccountType.Expense && string.Equals(a.Name, AccountName, StringComparison.OrdinalIgnoreCase))
            ?? await accounts.CreateUsualAsync(new CreateAccountCommand(AccountName, AccountType.Expense, Description: "Paying people back for business trips in their own cars."), ct);
        var lines = trips.Select(t => new BillLineDraft(account.Id, decimal.Round(t.Miles * MileageRate.For(rates, t.Date.Year), 2, MidpointRounding.AwayFromZero),
            TextCleaning.Cut($"{t.Date:MMM d, yyyy} · {t.Purpose} ({t.Miles:0.#} mi)", 1000))).Where(l => l.Amount > 0m).ToList();
        var bill = await bills.CreateAsync(new CreateBillCommand(vendorId, billDate, lines,
            Reference: $"Miles {trips[0].Date:yyMMdd}-{trips[^1].Date:yyMMdd}", Memo: $"Mileage for {worker.Name}, {trips[0].Date:MMM d} to {trips[^1].Date:MMM d, yyyy}"), ct);
        var tracked = await store.GetMileageTripsAsync([.. trips.Select(t => t.Id)], ct);
        foreach (var trip in tracked)
        {
            var before = MileageService.Snapshot(trip);
            trip.MarkReimbursed(bill.Id);
            store.Add(AuditRecord.Create(clock.UtcNow, user.Actor, AuditAction.Updated, MileageService.EntityType, trip.Id, before, MileageService.Snapshot(trip)));
        }

        await store.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return bill;
    }
}
