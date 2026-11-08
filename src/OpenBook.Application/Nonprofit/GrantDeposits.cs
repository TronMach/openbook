using OpenBook.Application.Sales;
using OpenBook.Domain.Common;
using OpenBook.Domain.Nonprofit;
using OpenBook.Domain.Sales;

namespace OpenBook.Application.Nonprofit;

/// <summary>
/// Grant payments recorded to a holding account such as Undeposited Funds, for Make deposit. A payment is a row of its
/// grant, so the id the deposit keeps is the payment's journal entry.
/// </summary>
public sealed class GrantDeposits(INonprofitStore store) : IUndepositedSource
{
    public DepositedKind Kind => DepositedKind.Grant;

    public async Task<IReadOnlyList<UndepositedItem>> WaitingAsync(Func<Guid, bool> holding, IReadOnlyDictionary<Guid, string> customers, CancellationToken ct) =>
        (await store.ListGrantsAsync(ct))
            .SelectMany(g => g.Receipts.Where(r => !r.IsVoided && r.DepositId is null && holding(r.BankAccountId)).Select(r => Item(g, r, customers)))
            .ToList();

    public async Task<IReadOnlyList<UndepositedItem>> DescribeAsync(IReadOnlyCollection<Guid> ids, IReadOnlyDictionary<Guid, string> customers, CancellationToken ct) =>
        (await store.ListGrantsAsync(ct))
            .SelectMany(g => g.Receipts.Where(r => ids.Contains(r.EntryId)).Select(r => Item(g, r, customers)))
            .ToList();

    public async Task MarkAsync(IReadOnlyCollection<Guid> ids, Guid depositId, CancellationToken ct)
    {
        var found = 0;
        foreach (var grant in await store.ListGrantsAsync(ct))
            foreach (var receipt in grant.Receipts.Where(r => ids.Contains(r.EntryId)))
            {
                grant.MarkReceiptDeposited(receipt.LineNumber, depositId);
                found++;
            }
        if (found != ids.Count)
            throw new EntityNotFoundException("grant_receipt", ids.First());
    }

    public async Task ClearAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct)
    {
        foreach (var grant in await store.ListGrantsAsync(ct))
            foreach (var receipt in grant.Receipts.Where(r => ids.Contains(r.EntryId)))
                grant.ClearReceiptDeposit(receipt.LineNumber);
    }

    private static UndepositedItem Item(Grant g, GrantReceipt r, IReadOnlyDictionary<Guid, string> customers) =>
        new(DepositedKind.Grant, r.EntryId, r.Date, g.FunderId, $"Grant {g.Name} · {customers.GetValueOrDefault(g.FunderId)}", g.Reference, r.BankAccountId, r.Amount, null);
}
