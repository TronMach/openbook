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
    string? Notes = null,
    Guid? ClassId = null,
    decimal? CreditLimit = null,
    decimal? FactorFee = null);

/// <summary>One line of an amortization schedule.</summary>
public readonly record struct Installment(int Number, DateOnly Date, decimal Payment, decimal Interest, decimal Principal, decimal Balance);

/// <summary>
/// Money borrowed and repaid monthly. The loan record holds the terms and its schedule; each payment posts Dr the
/// liability (principal), Dr interest expense, Cr the account it's paid from, with interest worked out on the
/// balance still owed, so extra payments shorten the loan.
/// </summary>
/// <summary>A variable rate's change: the annual rate from a date, and the monthly payment from then if the lender changed it.</summary>
public sealed class LoanRateChange
{
    private LoanRateChange()
    {
    }

    internal LoanRateChange(DateOnly effectiveFrom, decimal ratePercent, decimal? paymentAmount)
    {
        EffectiveFrom = effectiveFrom;
        RatePercent = ratePercent;
        PaymentAmount = paymentAmount;
    }

    public DateOnly EffectiveFrom { get; private set; }
    public decimal RatePercent { get; private set; }
    public decimal? PaymentAmount { get; private set; }
}

public sealed class Loan
{
    public const int MaxRateChanges = 60;

    private readonly List<LoanRateChange> _rateChanges = [];

    /// <summary>A variable rate's changes in date order; before the first, <see cref="AnnualRatePercent"/> applies.</summary>
    public IReadOnlyList<LoanRateChange> RateChanges => [.. _rateChanges.OrderBy(c => c.EffectiveFrom)];

    /// <summary>The annual rate on a date: the latest change by then, else the loan's own rate.</summary>
    public decimal RateOn(DateOnly date) => _rateChanges.Where(c => c.EffectiveFrom <= date).MaxBy(c => c.EffectiveFrom)?.RatePercent ?? AnnualRatePercent;

    /// <summary>The monthly payment on a date: the latest change that set one by then, else the loan's own.</summary>
    public decimal PaymentOn(DateOnly date) =>
        _rateChanges.Where(c => c.EffectiveFrom <= date && c.PaymentAmount is not null).MaxBy(c => c.EffectiveFrom)?.PaymentAmount ?? PaymentAmount;

    /// <summary>
    /// Sets the rate from a date (replacing a change on that date), with the new monthly payment if the lender changed
    /// it. Payments already recorded keep the interest they were split with, so the date must be after the latest.
    /// </summary>
    public void SetRateChange(DateOnly from, decimal ratePercent, decimal? paymentAmount)
    {
        if (FactorFee is not null)
            throw new DomainException("loan.rate_factor", "An advance repaid with a fixed fee has no interest rate to change.");
        if (ratePercent is < 0m or > 100m || decimal.Round(ratePercent, 4) != ratePercent)
            throw new DomainException("loan.rate_invalid", "Enter the yearly rate as a percentage from 0 to 100, to four decimals.");
        if (paymentAmount is { } payment && (payment <= 0m || decimal.Round(payment, 2) != payment))
            throw new DomainException("loan.payment_invalid", "Enter the new monthly payment in dollars and cents, or leave it blank.");
        EnsureAfterPayments(from);
        var existing = _rateChanges.FirstOrDefault(c => c.EffectiveFrom == from);
        if (existing is null && _rateChanges.Count >= MaxRateChanges)
            throw new DomainException("loan.too_many_rates", $"A loan keeps at most {MaxRateChanges} rate changes.");
        if (existing is not null)
            _rateChanges.Remove(existing);
        _rateChanges.Add(new LoanRateChange(from, ratePercent, paymentAmount));
    }

    public void RemoveRateChange(DateOnly from)
    {
        var existing = _rateChanges.FirstOrDefault(c => c.EffectiveFrom == from) ?? throw new DomainException("loan.rate_unknown", "There's no rate change on that date.");
        EnsureAfterPayments(from);
        _rateChanges.Remove(existing);
    }

    private void EnsureAfterPayments(DateOnly from)
    {
        if (LatestPayment is { } latest && from <= latest.Date)
            throw new DomainException("loan.rate_after_payments", $"Payments up to {latest.Date:yyyy-MM-dd} keep the rate they were recorded at. Start the change after the latest payment, or void the later payments first.");
    }

    public const string Source = "assets.loan";
    public const string PaymentSource = "assets.loan_payment";
    public const string DrawSource = "assets.loan_draw";

    private readonly List<LoanPayment> _payments = [];
    private readonly List<LoanDraw> _draws = [];

    /// <summary>A line of credit: more can be drawn, up to this balance. Null for a term loan.</summary>
    public decimal? CreditLimit { get; private set; }

    /// <summary>A merchant cash advance: a fixed fee instead of interest, paid in proportion with each repayment. Null otherwise.</summary>
    public decimal? FactorFee { get; private set; }

    public IReadOnlyList<LoanDraw> Draws => _draws;

    public decimal Drawn => _draws.Where(d => !d.IsVoided).Sum(d => d.Amount);

    /// <summary>The fee paid so far, on an advance.</summary>
    public decimal FeePaid => FactorFee is null ? 0m : _payments.Where(p => !p.IsVoided).Sum(p => p.Interest);

    /// <summary>Draws money on a line of credit, up to its limit.</summary>
    public LoanDraw AddDraw(Guid id, DateOnly date, decimal amount, Guid bankAccountId, Guid entryId)
    {
        CheckDraw(amount);
        var draw = new LoanDraw(id, date, amount, bankAccountId, entryId);
        _draws.Add(draw);
        return draw;
    }

    public void CheckDraw(decimal amount)
    {
        if (IsVoided)
            throw new DomainException("loan.voided", "This loan is voided.");
        if (CreditLimit is not { } limit)
            throw new DomainException("loan.not_revolving", $"{Name} isn't a line of credit, so nothing more can be drawn on it.");
        Money.EnsureCents(amount);
        if (amount <= 0m)
            throw new DomainException("loan.draw_not_positive", "Enter the amount drawn.");
        if (Balance + amount > limit)
            throw new DomainException("loan.over_limit", $"That would take the balance to {Balance + amount:N2}, over the {limit:N2} limit.");
    }

    /// <summary>A draw can be voided while what's owed covers it (payments since may have repaid it).</summary>
    public LoanDraw VoidDraw(Guid drawId)
    {
        var draw = _draws.FirstOrDefault(d => d.Id == drawId && !d.IsVoided) ?? throw new DomainException("loan.draw_unknown", "There's no such draw standing.");
        if (Balance - draw.Amount < 0m)
            throw new DomainException("loan.draw_repaid", "Payments since have repaid this draw. Void them first.");
        draw.Void();
        return draw;
    }

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

    public decimal Balance => IsVoided ? 0m : Principal + Drawn - _payments.Where(p => !p.IsVoided).Sum(p => p.Principal);

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
        if (details.CreditLimit is { } limit && (limit < details.Principal || decimal.Round(limit, 2) != limit))
            throw new DomainException("loan.limit_invalid", "A line of credit's limit is at least what's drawn now, in cents.");
        if (details.FactorFee is { } factorFee && (factorFee <= 0m || decimal.Round(factorFee, 2) != factorFee || details.AnnualRatePercent != 0m || details.CreditLimit is not null))
            throw new DomainException("loan.factor_invalid", "An advance's fee is an amount above zero, with no interest rate, and it isn't a line of credit.");
        if (termsChange && _draws.Any(d => !d.IsVoided))
            throw new DomainException("loan.has_payments", "The terms can't change once money has been drawn.");
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
        ClassId = details.ClassId;
        CreditLimit = details.CreditLimit;
        FactorFee = details.FactorFee;
    }

    /// <summary>The class its interest is posted to; null for none. Changes only later payments.</summary>
    public Guid? ClassId { get; private set; }

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

    /// <summary>A payment's split: interest is one month on the balance now owed at the rate on <paramref name="date"/>; the rest is principal.</summary>
    public (decimal Interest, decimal Principal) Split(decimal amount, DateOnly date)
    {
        Money.EnsureCents(amount);
        if (IsPaidOff)
            throw new DomainException("loan.paid_off", $"{Name} is paid off.");
        var interest = FactorFee is { } fee
            ? FeeShare(amount, fee)
            : decimal.Round(Balance * RateOn(date) / 1200m, 2, MidpointRounding.AwayFromZero);
        if (FactorFee is { } total && amount > Balance + total - FeePaid)
            throw new DomainException("loan.overpaid", $"Only {Balance + total - FeePaid:N2} is left to repay with the fee.");
        if (amount <= 0m)
            throw new DomainException("loan.payment_not_positive", "Enter the amount paid.");
        if (amount > Balance + interest)
            throw new DomainException("loan.overpaid", $"Only {Balance + interest:N2} is owed with this month's interest.");
        return amount < interest ? (amount, 0m) : (interest, amount - interest);
    }

    // An advance's fee is repaid in proportion: the last repayment takes what's left of it.
    private decimal FeeShare(decimal amount, decimal fee)
    {
        var left = fee - FeePaid;
        var owed = Balance + left;
        return amount >= owed ? left : Math.Min(left, decimal.Round(amount * fee / (Principal + Drawn + fee), 2, MidpointRounding.AwayFromZero));
    }

    /// <summary>The next scheduled payment: the regular payment, or what's left with interest.</summary>
    public decimal NextPayment => Math.Min(PaymentAmount, Balance + decimal.Round(Balance * MonthlyRate, 2, MidpointRounding.AwayFromZero));

    /// <summary>The payment due on a date, at the rate and payment then: the regular payment, or what's left with interest.</summary>
    public decimal NextPaymentOn(DateOnly date) => Math.Min(PaymentOn(date), Balance + decimal.Round(Balance * RateOn(date) / 1200m, 2, MidpointRounding.AwayFromZero));

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
    public static IReadOnlyList<Installment> Schedule(decimal balance, decimal annualRatePercent, decimal payment, DateOnly firstDate, int startNumber = 1, int monthsIn = 0)
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
            result.Add(new Installment(startNumber + n, firstDate.AddMonths(monthsIn + n), interest + principal, interest, principal, balance));
            n++;
        }

        return result;
    }
}

/// <summary>Money drawn on a line of credit: Dr the bank / Cr the loan.</summary>
public sealed class LoanDraw
{
    private LoanDraw()
    {
    }

    internal LoanDraw(Guid id, DateOnly date, decimal amount, Guid bankAccountId, Guid entryId) =>
        (Id, Date, Amount, BankAccountId, EntryId) = (id, date, amount, bankAccountId, entryId);

    public Guid Id { get; private set; }
    public DateOnly Date { get; private set; }
    public decimal Amount { get; private set; }
    public Guid BankAccountId { get; private set; }
    public Guid EntryId { get; private set; }
    public bool IsVoided { get; private set; }

    internal void Void() => IsVoided = true;
}
