using Microsoft.EntityFrameworkCore;
using OpenBook.Application.Sales;
using OpenBook.Domain.Common;
using OpenBook.Domain.Sales;

namespace OpenBook.Infrastructure.Persistence;

internal sealed class EfOnlineMoneyStore(OpenBookDbContext db) : IOnlineMoneyStore
{
    public Task<OnlineRefund?> GetRefundAsync(Guid id, CancellationToken ct) => db.OnlineRefunds.FirstOrDefaultAsync(r => r.Id == id, ct);

    public async Task<IReadOnlyList<OnlineRefund>> ListRefundsAsync(Guid customerPaymentId, CancellationToken ct) =>
        await db.OnlineRefunds.AsNoTracking().Where(r => r.CustomerPaymentId == customerPaymentId).OrderByDescending(r => r.Number).ToListAsync(ct);

    public async Task<IReadOnlyList<OnlineRefund>> ListRefundsOfAsync(string paymentReference, CancellationToken ct) =>
        await db.OnlineRefunds.AsNoTracking().Where(r => r.PaymentReference == paymentReference).OrderBy(r => r.Number).ToListAsync(ct);

    public Task<OnlineRefund?> FindRefundAsync(string providerRefundId, CancellationToken ct) =>
        db.OnlineRefunds.AsNoTracking().FirstOrDefaultAsync(r => r.ProviderRefundId == providerRefundId, ct);

    // SQLite can't ORDER BY a DateTimeOffset, so these are ordered in memory.
    public async Task<IReadOnlyList<OnlineRefund>> ListPendingRefundsAsync(CancellationToken ct) =>
        [.. (await db.OnlineRefunds.AsNoTracking().Where(r => r.Status == OnlineRefundStatus.Pending).ToListAsync(ct)).OrderBy(r => r.CreatedAt)];

    public async Task<Guid?> LastRefundAccountAsync(CancellationToken ct) =>
        (await db.OnlineRefunds.AsNoTracking().Select(r => new { r.PaymentAccountId, r.CreatedAt }).ToListAsync(ct)).MaxBy(r => r.CreatedAt)?.PaymentAccountId;

    public Task<PaymentLink?> FindLinkOfPaymentAsync(Guid customerPaymentId, CancellationToken ct) =>
        db.PaymentLinks.AsNoTracking().FirstOrDefaultAsync(l => l.CustomerPaymentId == customerPaymentId, ct);

    public Task<bool> HasPaidLinkAsync(Guid invoiceId, CancellationToken ct) =>
        db.PaymentLinks.AnyAsync(l => l.InvoiceId == invoiceId && l.Status == PaymentLinkStatus.Paid, ct);

    public Task<CardCharge?> FindChargeOfPaymentAsync(Guid customerPaymentId, CancellationToken ct) =>
        db.CardCharges.AsNoTracking().FirstOrDefaultAsync(c => c.CustomerPaymentId == customerPaymentId, ct);

    public Task<CardCharge?> FindChargeAsync(string intentId, CancellationToken ct) => db.CardCharges.FirstOrDefaultAsync(c => c.IntentId == intentId, ct);

    public Task<CardCharge?> GetChargeAsync(Guid id, CancellationToken ct) => db.CardCharges.FirstOrDefaultAsync(c => c.Id == id, ct);

    public async Task<IReadOnlyList<CardCharge>> ListChargesAsync(Guid invoiceId, CancellationToken ct) =>
        await db.CardCharges.AsNoTracking().Where(c => c.InvoiceId == invoiceId).OrderByDescending(c => c.Attempt).ToListAsync(ct);

    public async Task<IReadOnlyList<CardCharge>> ListPendingChargesAsync(CancellationToken ct) =>
        [.. (await db.CardCharges.AsNoTracking().Where(c => c.Status == CardChargeStatus.Pending).ToListAsync(ct)).OrderBy(c => c.CreatedAt)];

    public async Task<IReadOnlyList<CardCharge>> ListDeclinedAsync(IReadOnlyCollection<Guid> cardIds, CancellationToken ct) =>
        await db.CardCharges.AsNoTracking().Where(c => c.Automatic && c.Status == CardChargeStatus.Declined && cardIds.Contains(c.SavedCardId)).ToListAsync(ct);

    public async Task<IReadOnlyList<SavedCard>> ListCardsAsync(Guid customerId, CancellationToken ct) =>
        [.. (await db.SavedCards.Where(c => c.CustomerId == customerId).ToListAsync(ct)).OrderByDescending(c => c.AgreedAt)];

    public async Task<IReadOnlyList<SavedCard>> ListCardsAsync(SavedCardStatus status, CancellationToken ct) =>
        [.. (await db.SavedCards.AsNoTracking().Where(c => c.Status == status).ToListAsync(ct)).OrderBy(c => c.AgreedAt)];

    public async Task<IReadOnlyList<SavedCard>> ListCardsToDetachAsync(CancellationToken ct) =>
        await db.SavedCards.AsNoTracking().Where(c => c.Status == SavedCardStatus.Removed && c.PaymentMethodId != null && c.DetachedAt == null).ToListAsync(ct);

    public Task<SavedCard?> GetCardAsync(Guid id, CancellationToken ct) => db.SavedCards.FirstOrDefaultAsync(c => c.Id == id, ct);

    public Task<SavedCard?> FindCardAsync(string sessionId, CancellationToken ct) => db.SavedCards.FirstOrDefaultAsync(c => c.SessionId == sessionId, ct);

    public async Task<IReadOnlyList<Guid>> ListDueInvoicesAsync(IReadOnlyCollection<Guid> customerIds, DateOnly today, CancellationToken ct) =>
        await db.Invoices
            .Where(i => customerIds.Contains(i.CustomerId) && i.Status == DocumentStatus.Posted && !i.HoldAutoCharge && i.DueDate <= today && i.Total > i.AmountPaid)
            .OrderBy(i => i.DueDate).ThenBy(i => i.Number).Select(i => i.Id).ToListAsync(ct);

    public void Add(OnlineRefund refund) => db.OnlineRefunds.Add(refund);

    public void Add(SavedCard card) => db.SavedCards.Add(card);

    public void Add(CardCharge charge) => db.CardCharges.Add(charge);
}
