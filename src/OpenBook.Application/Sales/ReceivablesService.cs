using OpenBook.Domain.Common;
using OpenBook.Domain.Sales;

namespace OpenBook.Application.Sales;

/// <summary>Amounts owed by age: not yet due, then 1–30, 31–60, 61–90 and over 90 days past due.</summary>
public sealed record AgingBuckets(decimal Current, decimal Days1To30, decimal Days31To60, decimal Days61To90, decimal Over90)
{
    public static readonly AgingBuckets Zero = new(0m, 0m, 0m, 0m, 0m);

    public decimal Total => Current + Days1To30 + Days31To60 + Days61To90 + Over90;
    public decimal PastDue => Days1To30 + Days31To60 + Days61To90 + Over90;

    public AgingBuckets Add(int daysPastDue, decimal amount) => daysPastDue switch
    {
        <= 0 => this with { Current = Current + amount },
        <= 30 => this with { Days1To30 = Days1To30 + amount },
        <= 60 => this with { Days31To60 = Days31To60 + amount },
        <= 90 => this with { Days61To90 = Days61To90 + amount },
        _ => this with { Over90 = Over90 + amount },
    };

    public static AgingBuckets operator +(AgingBuckets a, AgingBuckets b) => new(
        a.Current + b.Current, a.Days1To30 + b.Days1To30, a.Days31To60 + b.Days31To60, a.Days61To90 + b.Days61To90, a.Over90 + b.Over90);
}

/// <summary>An invoice still owed on the as-of date.</summary>
public sealed record OpenInvoice(Invoice Invoice, decimal Balance, int DaysPastDue);

/// <summary>Unused credit on the as-of date (negative in the totals).</summary>
public sealed record OpenCredit(CreditKind Kind, Guid Id, string Label, DateOnly Date, decimal Amount);

public sealed record CustomerAging(Customer Customer, AgingBuckets Buckets, IReadOnlyList<OpenInvoice> Invoices, IReadOnlyList<OpenCredit> Credits);

public sealed record AgingReport(DateOnly AsOf, IReadOnlyList<CustomerAging> Customers)
{
    public AgingBuckets Totals => Customers.Aggregate(AgingBuckets.Zero, (sum, c) => sum + c.Buckets);
}

public enum StatementLineKind
{
    Invoice,
    Payment,
    Discount,
    CreditMemo,
    Refund,
    WriteOff,
}

/// <param name="Amount">Positive adds to what the customer owes.</param>
public sealed record StatementLine(DateOnly Date, StatementLineKind Kind, Guid DocumentId, string Reference, string Description, decimal Amount, decimal Balance);

public sealed record CustomerStatement(
    Customer Customer, DateOnly From, DateOnly To, decimal OpeningBalance, IReadOnlyList<StatementLine> Lines, decimal ClosingBalance, AgingBuckets Aging);

/// <summary>
/// What customers owe, from the sales documents: A/R aging and customer statements. Balances on a past date
/// count only payments and credits applied by then.
/// </summary>
public sealed class ReceivablesService(ISalesStore store)
{
    public async Task<AgingReport> AgingAsync(DateOnly asOf, CancellationToken ct = default)
    {
        var data = await LoadAsync(ct);
        var result = new List<CustomerAging>();
        foreach (var (customer, _) in CustomerTree.Ordered(data.Customers))
        {
            var aging = AgingFor(customer, new HashSet<Guid> { customer.Id }, data, asOf);
            if (aging.Invoices.Count > 0 || aging.Credits.Count > 0)
                result.Add(aging);
        }

        return new AgingReport(asOf, result);
    }

    public async Task<CustomerAging> CustomerAgingAsync(Guid customerId, DateOnly asOf, CancellationToken ct = default)
    {
        var data = await LoadAsync(ct);
        var customer = data.Customers.SingleOrDefault(c => c.Id == customerId) ?? throw new EntityNotFoundException("customer", customerId);
        return AgingFor(customer, new HashSet<Guid> { customer.Id }, data, asOf);
    }

    /// <summary>
    /// Activity between the dates with a running balance, and the aging on the closing date. A sub-customer billed
    /// with its parent gets the parent's statement, which includes the sub-customers billed to it.
    /// </summary>
    public async Task<CustomerStatement> StatementAsync(Guid customerId, DateOnly from, DateOnly to, CancellationToken ct = default)
    {
        if (to < from)
            throw new DomainException("statement.dates_invalid", "The statement's end date is before its start date.");
        var data = await LoadAsync(ct);
        var byId = data.Customers.ToDictionary(c => c.Id);
        if (!byId.ContainsKey(customerId))
            throw new EntityNotFoundException("customer", customerId);
        var customer = byId[CustomerTree.BillTo(customerId, byId)];
        var billed = CustomerTree.Billed(customer.Id, byId);
        var names = CustomerTree.FullNames(data.Customers);
        string For(Guid id) => id == customer.Id ? "" : $" · {names[id]}";

        var activity = new List<(DateOnly Date, StatementLineKind Kind, Guid Id, string Reference, string Description, decimal Amount, DateTimeOffset Order)>();
        foreach (var i in data.Invoices.Where(i => i.Status == DocumentStatus.Posted && billed.Contains(i.CustomerId)))
            activity.Add((i.Date, StatementLineKind.Invoice, i.Id, i.Number,
                (i.LateFeeForInvoiceId is null ? "Invoice" : "Late fee") + $", due {i.DueDate.ToString("d", System.Globalization.CultureInfo.CurrentCulture)}{For(i.CustomerId)}",
                i.Total, i.CreatedAt));
        foreach (var w in data.WriteOffs.Where(w => w.Status == DocumentStatus.Posted && billed.Contains(w.CustomerId)))
            activity.Add((w.Date, StatementLineKind.WriteOff, w.Id, data.Invoices.FirstOrDefault(i => i.Id == w.InvoiceId)?.Number ?? "",
                $"Written off{For(w.CustomerId)}", -w.Amount, w.CreatedAt));
        foreach (var p in data.Payments.Where(p => p.Status == DocumentStatus.Posted && billed.Contains(p.CustomerId)))
        {
            if (p.Amount > 0m)
                activity.Add((p.Date, StatementLineKind.Payment, p.Id, p.Reference ?? "", "Payment received, thank you", -p.Amount, p.CreatedAt));
            if (p.DiscountTaken > 0m)
                activity.Add((p.Date, StatementLineKind.Discount, p.Id, p.Reference ?? "", "Early payment discount", -p.DiscountTaken, p.CreatedAt));
        }

        foreach (var c in data.CreditMemos.Where(c => c.Status == DocumentStatus.Posted && billed.Contains(c.CustomerId)))
            activity.Add((c.Date, StatementLineKind.CreditMemo, c.Id, c.Number, $"Credit memo{For(c.CustomerId)}", -c.Total, c.CreatedAt));
        foreach (var r in data.Refunds.Where(r => r.Status == DocumentStatus.Posted && billed.Contains(r.CustomerId)))
            activity.Add((r.Date, StatementLineKind.Refund, r.Id, r.Reference ?? "", $"Refund paid to you{For(r.CustomerId)}", r.Amount, r.CreatedAt));

        var opening = activity.Where(a => a.Date < from).Sum(a => a.Amount);
        var balance = opening;
        var lines = new List<StatementLine>();
        foreach (var a in activity.Where(a => a.Date >= from && a.Date <= to).OrderBy(a => a.Date).ThenBy(a => a.Order))
        {
            balance += a.Amount;
            lines.Add(new StatementLine(a.Date, a.Kind, a.Id, a.Reference, a.Description, a.Amount, balance));
        }

        return new CustomerStatement(customer, from, to, opening, lines, balance, AgingFor(customer, billed, data, to).Buckets);
    }

    /// <param name="customerIds">Whose invoices and credits count: the customer, plus sub-customers billed to it on a statement.</param>
    private static CustomerAging AgingFor(Customer customer, IReadOnlySet<Guid> customerIds, Ledger data, DateOnly asOf)
    {
        // Settlements by invoice up to the as-of date, from anyone (a parent can pay a sub-customer's invoice).
        var settled = new Dictionary<Guid, decimal>();
        foreach (var p in data.Payments.Where(p => p.Status == DocumentStatus.Posted))
            foreach (var a in p.Applications.Where(a => a.AppliedOn <= asOf))
                settled[a.InvoiceId] = settled.GetValueOrDefault(a.InvoiceId) + a.Settled;
        foreach (var c in data.CreditMemos.Where(c => c.Status == DocumentStatus.Posted))
            foreach (var a in c.Applications.Where(a => a.AppliedOn <= asOf))
                settled[a.InvoiceId] = settled.GetValueOrDefault(a.InvoiceId) + a.Amount;
        foreach (var w in data.WriteOffs.Where(w => w.Status == DocumentStatus.Posted && w.Date <= asOf))
            settled[w.InvoiceId] = settled.GetValueOrDefault(w.InvoiceId) + w.Amount;

        var buckets = AgingBuckets.Zero;
        var invoices = new List<OpenInvoice>();
        foreach (var invoice in data.Invoices.Where(i => customerIds.Contains(i.CustomerId) && i.Status == DocumentStatus.Posted && i.Date <= asOf)
                     .OrderBy(i => i.DueDate).ThenBy(i => i.Number, StringComparer.Ordinal))
        {
            var balance = invoice.Total - settled.GetValueOrDefault(invoice.Id);
            if (balance == 0m)
                continue;
            var daysPastDue = asOf.DayNumber - invoice.DueDate.DayNumber;
            invoices.Add(new OpenInvoice(invoice, balance, Math.Max(0, daysPastDue)));
            buckets = buckets.Add(daysPastDue, balance);
        }

        var refunds = data.Refunds.Where(r => customerIds.Contains(r.CustomerId) && r.Status == DocumentStatus.Posted && r.Date <= asOf).ToList();
        var credits = new List<OpenCredit>();
        foreach (var c in data.CreditMemos.Where(c => customerIds.Contains(c.CustomerId) && c.Status == DocumentStatus.Posted && c.Date <= asOf))
        {
            var left = c.Total - c.Applications.Where(a => a.AppliedOn <= asOf).Sum(a => a.Amount)
                - refunds.Where(r => r.CreditMemoId == c.Id).Sum(r => r.Amount);
            if (left != 0m)
                credits.Add(new OpenCredit(CreditKind.CreditMemo, c.Id, $"Credit memo {c.Number}", c.Date, left));
        }

        foreach (var p in data.Payments.Where(p => customerIds.Contains(p.CustomerId) && p.Status == DocumentStatus.Posted && p.Date <= asOf))
        {
            var left = p.Amount - p.Applications.Where(a => a.AppliedOn <= asOf).Sum(a => a.Amount)
                - refunds.Where(r => r.CustomerPaymentId == p.Id).Sum(r => r.Amount);
            if (left != 0m)
                credits.Add(new OpenCredit(CreditKind.Payment, p.Id, p.Reference is null ? "Unapplied payment" : $"Unapplied payment {p.Reference}", p.Date, left));
        }

        // Unused credit reduces what's owed now, as in QuickBooks.
        buckets = buckets with { Current = buckets.Current - credits.Sum(c => c.Amount) };
        return new CustomerAging(customer, buckets, invoices, credits.OrderBy(c => c.Date).ToList());
    }

    private async Task<Ledger> LoadAsync(CancellationToken ct) => new(
        await store.ListCustomersAsync(ct),
        await store.ListInvoicesAsync(null, ct),
        await store.ListPaymentsAsync(null, ct),
        await store.ListCreditMemosAsync(null, ct),
        await store.ListRefundsAsync(null, ct),
        await store.ListWriteOffsAsync(null, ct));

    private sealed record Ledger(
        IReadOnlyList<Customer> Customers,
        IReadOnlyList<Invoice> Invoices,
        IReadOnlyList<CustomerPayment> Payments,
        IReadOnlyList<CreditMemo> CreditMemos,
        IReadOnlyList<CustomerRefund> Refunds,
        IReadOnlyList<BadDebtWriteOff> WriteOffs);
}
