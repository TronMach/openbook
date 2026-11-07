using OpenBook.Domain.Sales;

namespace OpenBook.Application.Sales;

/// <summary>The addresses a sales document is made out to: the billed customer's billing address, and the job's (else the billed customer's) shipping address.</summary>
public static class SalesAddresses
{
    public static async Task<(string? BillTo, string? ShipTo)> ForAsync(ISalesStore store, Guid? customerId, CancellationToken ct)
    {
        if (customerId is not { } id)
            return (null, null);
        var all = (await store.ListCustomersAsync(ct)).ToDictionary(c => c.Id);
        if (!all.TryGetValue(id, out var job))
            return (null, null);
        var billed = all[CustomerTree.BillTo(id, all)];
        return (billed.BillingAddress, job.ShippingAddress ?? billed.ShippingAddress);
    }

    /// <summary>Keeps the addresses on a document as it's made.</summary>
    internal static async Task KeepAsync(ISalesStore store, Guid? customerId, Action<string?, string?> keep, CancellationToken ct)
    {
        var (billTo, shipTo) = await ForAsync(store, customerId, ct);
        keep(billTo, shipTo);
    }
}
