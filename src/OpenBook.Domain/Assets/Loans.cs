using OpenBook.Domain.Common;

namespace OpenBook.Domain.Assets;

/// <param name="Principal">What's borrowed (or, for a loan already on the books, what's still owed).</param>
/// <param name="AnnualRatePercent">Yearly interest rate, to 4 decimals (6.5 = 6.5%).</param>
/// <param name="Payments">Number of monthly payments left.</param>
/// <param name="PaymentAmount">The monthly payment; null works it out from the rate and term.</param>
public sealed record LoanDetails(
    string Name,
    Guid LiabilityAccountId,
    Guid InterestAccountId,
    decimal Principal,
    decimal AnnualRatePercent,
    int Payments,
    DateOnly FirstPaymentDate,
    decimal? PaymentAmount = null,
    Guid? LenderVendorId = null,
    string? Notes = null);

/// <summary>One line of an amortization schedule.</summary>
public readonly record struct Installment(int Number, DateOnly Date, decimal Payment, decimal Interest, decimal Principal, decimal Balance);

/// <summary>
/// Money borrowed and repaid monthly. The loan record holds the terms and its schedule; each payment posts Dr the
/// liability (principal), Dr interest expense, Cr the account it's paid from, with interest worked out on the
/// balance still owed, so extra payments shorten the loan.
/// </summary>
public sealed class Loan
{
    public const string Source = "assets.loan";
    public const string PaymentSource = "assets.loan_payment";

    private readonly List<LoanPayment> _payments = [];

    private Loan() => Name = string.Empty;

    public Guid Id { get; private set; }
    public string Name { get; private set; }
    public Guid LiabilityAccountId { get; private set; }
    public Guid InterestAccountId { get; private set; }
    public decimal Principal { get; private set; }
    public decimal AnnualRatePercent { get; private set; }
    public int Payments { get; private set; }
    public DateOnly FirstPaymentDate { get; private set; }
    public decimal PaymentAmount { get; private set; }
    public Guid? LenderVendorId { get; private set; }
    public string? Notes { get; private set; }

    /// <summary>When the loan money was recorded as received (Dr this account, Cr the liability); null if it was already on the books.</summary>
    public Guid? ProceedsAccountId { get; private set; }

    public DateOnly? ProceedsDate { get; private set; }
    public Guid? ProceedsEntryId { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    /// <summary>Set when the loan was entered by mistake and voided; it then leaves the lists, reports and forecast.</summary>
    public DateTimeOffset? VoidedAt { get; private set; }

    public bool IsVoided => VoidedAt is not null;
    public IReadOnlyList<LoanPayment> PaymentsMade => _payments;

    public decimal MonthlyRate => AnnualRatePercent / 1200m;

    public decimal Balance => IsVoided ? 0m : Principal - _payments.Where(p => !p.IsVoided).Sum(p => p.Principal);

    public bool IsPaidOff => Balance == 0m;

    public static Loan Create(LoanDetails details, DateTimeOffset createdAt)
    {
        var loan = new Loan { Id = Guid.CreateVersion7(), CreatedAt = createdAt.ToUniversalTime() };
        loan.Update(details);
        return loan;
    }

    public void Update(LoanDetails details)
    {
        ArgumentNullException.ThrowIfNull(details);
        if (IsVoided)
            throw new DomainException("loan.voided", "This loan is voided.");
        var termsChange = details.LiabilityAccountId != LiabilityAccountId || details.InterestAccountId != InterestAccountId || details.Principal != Principal
            || details.AnnualRatePercent != AnnualRatePercent || details.Payments != Payments || details.FirstPaymentDate != FirstPaymentDate
            || (details.PaymentAmount is { } amount && amount != PaymentAmount);
        if (termsChange && _payments.Any(p => !p.IsVoided))
            throw new DomainException("loan.has_payments", "The terms can't change once payments are recorded.");
        Money.EnsureCents(details.Principal);
        if (details.Principal <= 0m)
            throw new DomainException("loan.principal_not_positive", "Enter the amount borrowed.");
        if (details.AnnualRatePercent is < 0m or > 100m || decimal.Round(details.AnnualRatePercent, 4) != details.AnnualRatePercent)
            throw new DomainException("loan.rate_invalid", "The interest rate is a percentage from 0 to 100, with at most 4 decimals.");
        if (details.Payments is < 1 or > 600)
            throw new DomainException("loan.term_invalid", "Enter the number of monthly payments, from 1 to 600.");
        Name = Text.Optional(details.Name, 200, "loan.name_too_long", "Name") ?? throw new DomainException("loan.name_required", "Enter a name for the loan.");
        LiabilityAccountId = details.LiabilityAccountId;
        InterestAccountId = details.InterestAccountId;
        Principal = details.Principal;
        AnnualRatePercent = details.AnnualRatePercent;
        Payments = details.Payments;
        FirstPaymentDate = details.FirstPaymentDate;
        var payment = details.PaymentAmount ?? Amortization.Payment(details.Principal, details.AnnualRatePercent, details.Payments);
        Money.EnsureCents(payment);
        if (payment <= decimal.Round(details.Principal * details.AnnualRatePercent / 1200m, 2, MidpointRounding.AwayFromZero))
            throw new DomainException("loan.payment_too_small", "The payment doesn't cover the first month's interest, so the loan would never be repaid.");
        PaymentAmount = payment;
        LenderVendorId = details.LenderVendorId;
        Notes = Text.Optional(details.Notes, 1000, "loan.notes_too_long", "Notes");
    }

    public void Void(DateTimeOffset at)
    {
        if (IsVoided)
            throw new DomainException("loan.voided", "This loan is voided.");
        if (_payments.Any(p => !p.IsVoided))
            throw new DomainException("loan.has_payments", "Void its payments first, latest first.");
        VoidedAt = at.ToUniversalTime();
    }

    public void RecordProceeds(Guid accountId, DateOnly date, Guid entryId)
    {
        ProceedsAccountId = accountId;
        ProceedsDate = date;
        ProceedsEntryId = entryId;
    }

    /// <summary>A payment's split: interest is one month on the balance now owed; the rest is principal.</summary>
    public (decimal Interest, decimal Principal) Split(decimal amount)
    {
        Money.EnsureCents(amount);
        if (IsPaidOff)
            throw new DomainException("loan.paid_off", $"{Name} is paid off.");
        var interest = decimal.Round(Balance * MonthlyRate, 2, MidpointRounding.AwayFromZero);
        if (amount <= 0m)
            throw new DomainException("loan.payment_not_positive", "Enter the amount paid.");
        if (amount > Balance + interest)
            throw new DomainException("loan.overpaid", $"Only {Balance + interest:N2} is owed with this month's interest.");
        return amount < interest ? (amount, 0m) : (interest, amount - interest);
    }

    /// <summary>The next scheduled payment: the regular payment, or what's left with interest.</summary>
    public decimal NextPayment => Math.Min(PaymentAmount, Balance + decimal.Round(Balance * MonthlyRate, 2, MidpointRounding.AwayFromZero));

    public void AddPayment(LoanPayment payment)
    {
        ArgumentNullException.ThrowIfNull(payment);
        _payments.Add(payment);
    }

    /// <summary>The latest payment that isn't voided: only it can be voided, since later interest depends on earlier balances.</summary>
    public LoanPayment? LatestPayment => _payments.Where(p => !p.IsVoided).OrderBy(p => p.Date).ThenBy(p => p.Id).LastOrDefault();
}

public sealed class LoanPayment
{
    private LoanPayment() { }

    public Guid Id { get; private set; }
    public DateOnly Date { get; private set; }
    public decimal Amount { get; private set; }
    public decimal Interest { get; private set; }
    public decimal Principal { get; private set; }
    public Guid PaymentAccountId { get; private set; }
    public Guid JournalEntryId { get; private set; }
    public bool IsVoided { get; private set; }

    public static LoanPayment Create(Guid id, DateOnly date, decimal interest, decimal principal, Guid paymentAccountId, Guid journalEntryId) => new()
    {
        Id = id,
        Date = date,
        Amount = interest + principal,
        Interest = interest,
        Principal = principal,
        PaymentAccountId = paymentAccountId,
        JournalEntryId = journalEntryId,
    };

    public void Void()
    {
        if (IsVoided)
            throw new DomainException("loan.payment_voided", "This payment is already voided.");
        IsVoided = true;
    }
}

/// <summary>Loan payment math.</summary>
public static class Amortization
{
    /// <summary>The level monthly payment that repays <paramref name="principal"/> in <paramref name="payments"/> months, to the cent (rounded up).</summary>
    public static decimal Payment(decimal principal, decimal annualRatePercent, int payments)
    {
        if (payments < 1)
            throw new DomainException("loan.term_invalid", "Enter the number of monthly payments.");
        var r = annualRatePercent / 1200m;
        if (r == 0m)
            return Math.Ceiling(principal / payments * 100m) / 100m;
        var factor = 1m;
        for (var i = 0; i < payments; i++)
            factor *= 1m + r;
        var payment = principal * r * factor / (factor - 1m);
        return Math.Ceiling(decimal.Round(payment, 6) * 100m) / 100m;
    }

    /// <summary>The schedule from a balance: each month's interest on the balance, the rest to principal, the last payment clearing it.</summary>
    public static IReadOnlyList<Installment> Schedule(decimal balance, decimal annualRatePercent, decimal payment, DateOnly firstDate, int startNumber = 1)
    {
        var r = annualRatePercent / 1200m;
        var result = new List<Installment>();
        var n = 0;
        while (balance > 0m && n < 1200)
        {
            var interest = decimal.Round(balance * r, 2, MidpointRounding.AwayFromZero);
            var principal = Math.Min(balance, payment - interest);
            if (principal <= 0m)
                break;
            balance -= principal;
            result.Add(new Installment(startNumber + n, firstDate.AddMonths(n), interest + principal, interest, principal, balance));
            n++;
        }

        return result;
    }
}
