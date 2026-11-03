using OpenBook.Application.Sales;
using OpenBook.Domain.Common;
using OpenBook.Domain.Nonprofit;
using OpenBook.Domain.Sales;

namespace OpenBook.Application.Nonprofit;

/// <summary>Money gifts recorded to Undeposited Funds, for Make deposit.</summary>
public sealed class GiftDeposits(INonprofitStore store) : IUndepositedSource
{
    public DepositedKind Kind => DepositedKind.Gift;

    public async Task<IReadOnlyList<UndepositedItem>> WaitingAsync(Func<Guid, bool> holding, IReadOnlyDictionary<Guid, string> customers, CancellationToken ct) =>
        (await store.ListDonationsAsync(ct))
            .Where(d => d.Status == DocumentStatus.Posted && d.Kind == GiftKind.Cash && d.DepositId is null && holding(d.DebitAccountId))
            .Select(d => Item(d, customers))
            .ToList();

    public async Task<IReadOnlyList<UndepositedItem>> DescribeAsync(IReadOnlyCollection<Guid> ids, IReadOnlyDictionary<Guid, string> customers, CancellationToken ct)
    {
        var items = new List<UndepositedItem>();
        foreach (var id in ids)
        {
            if (await store.GetDonationAsync(id, ct) is { } d)
                items.Add(Item(d, customers));
        }

        return items;
    }

    public async Task MarkAsync(IReadOnlyCollection<Guid> ids, Guid depositId, CancellationToken ct)
    {
        foreach (var id in ids)
            ((await store.GetDonationAsync(id, ct)) ?? throw new EntityNotFoundException(GiftService.DonationEntity, id)).MarkDeposited(depositId);
    }

    public async Task ClearAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct)
    {
        foreach (var id in ids)
            (await store.GetDonationAsync(id, ct))?.ClearDeposit();
    }

    private static UndepositedItem Item(Donation d, IReadOnlyDictionary<Guid, string> customers) =>
        new(DepositedKind.Gift, d.Id, d.Date, d.DonorId, $"Gift {d.Number} · {customers.GetValueOrDefault(d.DonorId)}", d.Reference, d.DebitAccountId, d.Amount);
}
