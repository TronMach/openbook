using OpenBook.Application.Purchases;
using OpenBook.Application.Sales;
using OpenBook.Domain.Common;
using OpenBook.Domain.Ledger;
using OpenBook.Domain.Purchases;
using OpenBook.Domain.Sales;

namespace OpenBook.Application.Reporting;

/// <summary>
/// The lines every report is built from, for one basis. Accrual: the ledger as posted. Cash: invoices and
/// bills (and the payments' own A/R and A/P postings) are left out; instead each payment is recognized on
/// its date as income or expense, split across the paid documents' accounts in proportion to their lines.
/// </summary>
public sealed class ReportLedger(ILedgerStore ledger, ISalesStore sales, IPurchasesStore purchases)
{
    /// <summary>Entries whose effect cash basis replaces with payment-time recognition.</summary>
    private static readonly HashSet<string> AccrualDocuments = [Invoice.Source, Bill.Source, CustomerPayment.Source, BillPayment.Source];

    /// <param name="from">Null for "since the beginning".</param>
    public async Task<IReadOnlyList<LedgerLine>> LinesAsync(ReportBasis basis, DateOnly? from, DateOnly to, CancellationToken ct = default)
    {
        var lines = await ledger.GetLinesAsync(from, to, ct);
        if (basis == ReportBasis.Accrual)
            return lines.Where(l => l.Basis.AppliesTo(ReportBasis.Accrual)).ToList();

        var cash = lines.Where(l => l.Basis.AppliesTo(ReportBasis.Cash) && !AccrualDocuments.Contains(l.Source)).ToList();
        var entryNumbers = lines.GroupBy(l => l.JournalEntryId).ToDictionary(g => g.Key, g => g.First().EntryNumber);
        bool InRange(DateOnly d) => d <= to && (from is null || d >= from);

        var customerPayments = (await sales.ListPaymentsAsync(null, ct)).Where(p => p.Status == DocumentStatus.Posted && InRange(p.Date)).ToList();
        if (customerPayments.Count > 0)
        {
            var invoices = (await sales.ListInvoicesAsync(null, ct)).ToDictionary(i => i.Id);
            foreach (var payment in customerPayments)
            {
                var number = entryNumbers.GetValueOrDefault(payment.JournalEntryId);
                var memo = lines.FirstOrDefault(l => l.JournalEntryId == payment.JournalEntryId)?.EntryMemo;
                cash.Add(Derived(payment.JournalEntryId, number, payment.Date, payment.DepositAccountId, payment.Amount, memo, CustomerPayment.Source, payment.Id));
                foreach (var application in payment.Applications)
                {
                    var invoiceLines = invoices[application.InvoiceId].Lines
                        .Where(l => l.IncomeAccountId is not null && l.Amount != 0m)
                        .Select(l => (l.IncomeAccountId!.Value, l.Amount))
                        .ToList();
                    foreach (var (accountId, share) in Allocate(application.Amount, invoiceLines))
                        cash.Add(Derived(payment.JournalEntryId, number, payment.Date, accountId, -share, memo, CustomerPayment.Source, payment.Id));
                }
            }
        }

        var billPayments = (await purchases.ListPaymentsAsync(null, ct)).Where(p => p.Status == DocumentStatus.Posted && InRange(p.Date)).ToList();
        if (billPayments.Count > 0)
        {
            var bills = (await purchases.ListBillsAsync(null, ct)).ToDictionary(b => b.Id);
            foreach (var payment in billPayments)
            {
                var number = entryNumbers.GetValueOrDefault(payment.JournalEntryId);
                var memo = lines.FirstOrDefault(l => l.JournalEntryId == payment.JournalEntryId)?.EntryMemo;
                cash.Add(Derived(payment.JournalEntryId, number, payment.Date, payment.PaymentAccountId, -payment.Amount, memo, BillPayment.Source, payment.Id));
                foreach (var application in payment.Applications)
                {
                    var billLines = bills[application.BillId].Lines.Select(l => (l.AccountId, l.Amount)).ToList();
                    foreach (var (accountId, share) in Allocate(application.Amount, billLines))
                        cash.Add(Derived(payment.JournalEntryId, number, payment.Date, accountId, share, memo, BillPayment.Source, payment.Id));
                }
            }
        }

        return cash.OrderBy(l => l.Date).ThenBy(l => l.EntryNumber).ToList();
    }

    /// <summary>
    /// Splits <paramref name="amount"/> across accounts in proportion to the document's line amounts, in cents,
    /// with the rounding remainder on the last line so the parts add up exactly.
    /// </summary>
    public static IReadOnlyList<(Guid AccountId, decimal Amount)> Allocate(decimal amount, IReadOnlyList<(Guid AccountId, decimal Amount)> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        var total = lines.Sum(l => l.Amount);
        if (lines.Count == 0 || total == 0m)
            return [];

        var parts = new List<(Guid, decimal)>(lines.Count);
        var allocated = 0m;
        for (var i = 0; i < lines.Count; i++)
        {
            var share = i == lines.Count - 1
                ? amount - allocated
                : decimal.Round(amount * lines[i].Amount / total, 2, MidpointRounding.AwayFromZero);
            allocated += share;
            parts.Add((lines[i].AccountId, share));
        }

        return parts
            .GroupBy(p => p.Item1)
            .Select(g => (g.Key, g.Sum(p => p.Item2)))
            .Where(p => p.Item2 != 0m)
            .ToList();
    }

    private static LedgerLine Derived(Guid entryId, long number, DateOnly date, Guid accountId, decimal amount, string? memo, string source, Guid sourceId) =>
        new(Guid.Empty, entryId, number, date, accountId, amount, null, memo, source, sourceId, EntryBasis.Both);
}
