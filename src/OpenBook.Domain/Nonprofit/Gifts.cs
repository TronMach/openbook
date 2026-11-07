using OpenBook.Domain.Common;

namespace OpenBook.Domain.Nonprofit;

public enum GiftKind
{
    /// <summary>Money: cash, check, card or transfer, deposited to a bank account.</summary>
    Cash,

    /// <summary>Goods or services given instead of money, recorded at fair value.</summary>
    InKind,
}

/// <summary>
/// A gift from a donor (a customer record). A cash gift posts Dr the bank account / Cr the contribution account on
/// its class; an in-kind gift Dr the asset or expense it became / Cr the contribution account. A gift paying a pledge
/// credits the pledge's receivable instead, and adds a cash-basis-only entry recognizing the income then (the pledge
/// itself is accrual-only), so cash-basis reports count pledged money when it arrives. Voided, never edited.
/// </summary>
public sealed class Donation
{
    public const string Source = "nonprofit.donation";
    public const int MaxTextLength = 500;
    public const int MaxReferenceLength = 50;
    public const int MaxCampaignLength = 100;

    private Donation()
    {
    }

    public Guid Id { get; private set; }
    public int Number { get; private set; }
    public Guid DonorId { get; private set; }
    public DateOnly Date { get; private set; }
    public GiftKind Kind { get; private set; }
    public decimal Amount { get; private set; }

    /// <summary>Cash: the bank account it went into (or a holding account such as Undeposited Funds). In kind: the asset or expense account it was recorded to.</summary>
    public Guid DebitAccountId { get; private set; }

    /// <summary>The bank deposit that took it out of the holding account.</summary>
    public Guid? DepositId { get; private set; }

    /// <summary>The contribution account (for a pledge payment, the pledge's).</summary>
    public Guid IncomeAccountId { get; private set; }

    public Guid? PledgeId { get; private set; }
    public Guid? ClassId { get; private set; }

    /// <summary>Check number or other reference.</summary>
    public string? Reference { get; private set; }

    /// <summary>How it was paid, for deposit slips; null when not given, and for gifts in kind.</summary>
    public OpenBook.Domain.Sales.PaymentMethod? Method { get; private set; }

    public void SetMethod(OpenBook.Domain.Sales.PaymentMethod? method) => Method = OpenBook.Domain.Sales.PaymentMethods.Valid(method);

    /// <summary>The appeal or campaign that brought it in (a year-end letter, a gala); free text, changeable at any time, beside the books.</summary>
    public string? Campaign { get; private set; }

    public void SetCampaign(string? campaign) =>
        Campaign = Text.Optional(campaign, MaxCampaignLength, "donation.campaign_too_long", "Campaign");

    /// <summary>Reclassifying the income line: the letter names the new purpose.</summary>
    public void SetClass(Guid? classId) => ClassId = classId == Guid.Empty ? null : classId;

    public string? Memo { get; private set; }

    /// <summary>In kind: what was given (the acknowledgment describes it but doesn't value it).</summary>
    public string? Description { get; private set; }

    /// <summary>Quid pro quo: the fair value of goods or services the donor received in return (a dinner, a gift).</summary>
    public decimal GoodsValue { get; private set; }

    /// <summary>
    /// Where the exchange part (what the donor received, up to the gift) is credited, as earned revenue such as a gala's
    /// ticket income; null keeps all of it on the contribution account.
    /// </summary>
    public Guid? ExchangeAccountId { get; private set; }

    /// <summary>The part credited to <see cref="ExchangeAccountId"/>: the goods' value, up to the gift; 0 without one.</summary>
    public decimal ExchangePart => ExchangeAccountId is null ? 0m : Math.Min(GoodsValue, Amount);

    public void SetExchangeAccount(Guid? accountId) => ExchangeAccountId = accountId == Guid.Empty ? null : accountId;

    public string? GoodsDescription { get; private set; }
    public DocumentStatus Status { get; private set; }
    public Guid JournalEntryId { get; private set; }

    /// <summary>For a pledge payment, the cash-basis-only entry recognizing the income.</summary>
    public Guid? CashEntryId { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset? AcknowledgedAt { get; private set; }

    /// <summary>What the donor may deduct: the amount less what they received in return.</summary>
    public decimal Deductible => Math.Max(0m, Amount - GoodsValue);

    public static Donation Create(
        Guid id, int number, Guid donorId, DateOnly date, GiftKind kind, decimal amount, Guid debitAccountId, Guid incomeAccountId,
        Guid? pledgeId, Guid? classId, string? reference, string? memo, string? description, decimal goodsValue, string? goodsDescription,
        Guid journalEntryId, Guid? cashEntryId, DateTimeOffset createdAt)
    {
        Check(kind, amount, description, goodsValue, goodsDescription, pledgeId);
        return new Donation
        {
            Id = id,
            Number = number,
            DonorId = donorId,
            Date = date,
            Kind = kind,
            Amount = amount,
            DebitAccountId = debitAccountId,
            IncomeAccountId = incomeAccountId,
            PledgeId = pledgeId,
            ClassId = classId == Guid.Empty ? null : classId,
            Reference = Text.Optional(reference, MaxReferenceLength, "donation.reference_too_long", "Reference"),
            Memo = Text.Optional(memo, MaxTextLength, "donation.memo_too_long", "Memo"),
            Description = Text.Optional(description, MaxTextLength, "donation.description_too_long", "Description"),
            GoodsValue = goodsValue,
            GoodsDescription = Text.Optional(goodsDescription, MaxTextLength, "donation.goods_too_long", "What the donor received"),
            Status = DocumentStatus.Posted,
            JournalEntryId = journalEntryId,
            CashEntryId = cashEntryId,
            CreatedAt = createdAt.ToUniversalTime(),
        };
    }

    /// <summary>The rules a gift must meet, checked before anything posts.</summary>
    public static void Check(GiftKind kind, decimal amount, string? description, decimal goodsValue, string? goodsDescription, Guid? pledgeId)
    {
        if (!Enum.IsDefined(kind))
            throw new DomainException("donation.kind_invalid", "Choose cash or in kind.");
        if (amount <= 0m)
            throw new DomainException("donation.amount", "Enter an amount above zero.");
        if (decimal.Round(amount, 2) != amount || decimal.Round(goodsValue, 2) != goodsValue)
            throw new DomainException("donation.amount_cents", "Amounts are in cents.");
        // Worth more than the payment is allowed (an auction buyer's bargain): nothing is deductible then, and the letter says what it was worth.
        if (goodsValue < 0m)
            throw new DomainException("donation.goods_value", "What the donor received can't be worth less than nothing.");
        if (goodsValue > 0m && string.IsNullOrWhiteSpace(goodsDescription))
            throw new DomainException("donation.goods_description", "Say what the donor received in return.");
        if (kind == GiftKind.InKind && string.IsNullOrWhiteSpace(description))
            throw new DomainException("donation.description_required", "Describe what was given.");
        if (kind == GiftKind.InKind && pledgeId is not null)
            throw new DomainException("donation.pledge_in_kind", "Pledges are paid in money.");
    }

    public void MarkAcknowledged(DateTimeOffset at) => AcknowledgedAt ??= at.ToUniversalTime();

    /// <summary>A correction in place: same id and number, the new entry; the kind, pledge and anything beside the books stay.</summary>
    public void Change(Guid donorId, DateOnly date, decimal amount, Guid debitAccountId, Guid incomeAccountId, Guid? classId,
        string? reference, string? memo, string? description, decimal goodsValue, string? goodsDescription, Guid journalEntryId)
    {
        if (Status != DocumentStatus.Posted)
            throw new DomainException("donation.voided", "This gift is voided, so it can't be changed.");
        Check(Kind, amount, description, goodsValue, goodsDescription, PledgeId);
        DonorId = donorId;
        Date = date;
        Amount = amount;
        DebitAccountId = debitAccountId;
        IncomeAccountId = incomeAccountId;
        ClassId = classId == Guid.Empty ? null : classId;
        Reference = Text.Optional(reference, MaxReferenceLength, "donation.reference_too_long", "Reference");
        Memo = Text.Optional(memo, MaxTextLength, "donation.memo_too_long", "Memo");
        Description = Text.Optional(description, MaxTextLength, "donation.description_too_long", "Description");
        GoodsValue = goodsValue;
        GoodsDescription = Text.Optional(goodsDescription, MaxTextLength, "donation.goods_too_long", "What the donor received");
        JournalEntryId = journalEntryId;
    }

    public void MarkDeposited(Guid depositId)
    {
        if (Status != DocumentStatus.Posted || Kind != GiftKind.Cash)
            throw new DomainException("donation.not_depositable", "Only posted money gifts are deposited.");
        if (DepositId is not null)
            throw new DomainException("deposit.already_deposited", "This gift is already in a deposit.");
        DepositId = depositId;
    }

    public void ClearDeposit() => DepositId = null;

    /// <summary>The processor's payout that brought the money in, for a gift made online: it isn't for a bank deposit.</summary>
    public Guid? PayoutId { get; private set; }

    public void SettleByPayout(Guid payoutId) => PayoutId = payoutId;

    public void ClearPayout() => PayoutId = null;

    public void Void()
    {
        if (PayoutId is not null)
            throw new DomainException("donation.in_payout", "This gift is part of a posted payout. Void the payout first.");
        if (Status == DocumentStatus.Voided)
            throw new DomainException("donation.already_voided", "This gift is already voided.");
        if (DepositId is not null)
            throw new DomainException("donation.deposited", "This gift is in a bank deposit. Void the deposit first.");
        Status = DocumentStatus.Voided;
    }
}

/// <summary>One payment a pledge's schedule expects: beside the books, posting nothing.</summary>
public sealed class PledgeInstallment
{
    private PledgeInstallment()
    {
    }

    internal PledgeInstallment(int position, DateOnly dueDate, decimal amount)
    {
        Position = position;
        DueDate = dueDate;
        Amount = amount;
    }

    public int Position { get; private set; }
    public DateOnly DueDate { get; private set; }
    public decimal Amount { get; private set; }
}

/// <summary>
/// A donor's promise to give: Dr "Pledges Receivable" / Cr the contribution account on its class, accrual basis only
/// (cash basis counts the money as it's paid). Paid by gifts that name it. Voided only while nothing is paid. When the
/// donor won't pay the rest, it's cancelled: the reverse for the unpaid part, also accrual only, and undoable.
/// </summary>
public sealed class Pledge
{
    public const int MaxInstallments = 120;

    private readonly List<PledgeInstallment> _installments = [];

    /// <summary>The payment schedule, by due date; empty for a pledge due all at once (on <see cref="DueDate"/>).</summary>
    public IReadOnlyList<PledgeInstallment> Installments => [.. _installments.OrderBy(i => i.DueDate).ThenBy(i => i.Position)];

    /// <summary>Replaces the schedule: amounts above zero to the cent, adding up to the pledge, due on or after its date. Empty clears it.</summary>
    public void SetInstallments(IReadOnlyList<(DateOnly DueDate, decimal Amount)> installments)
    {
        ArgumentNullException.ThrowIfNull(installments);
        if (Status != DocumentStatus.Posted)
            throw new DomainException("pledge.voided", "This pledge is voided.");
        if (installments.Count > MaxInstallments)
            throw new DomainException("pledge.too_many_installments", $"A pledge takes at most {MaxInstallments} installments.");
        if (installments.Any(i => i.Amount <= 0m || decimal.Round(i.Amount, 2) != i.Amount))
            throw new DomainException("pledge.installment_amount", "Each installment needs an amount above zero, in cents.");
        if (installments.Any(i => i.DueDate < Date))
            throw new DomainException("pledge.installment_before_date", "An installment can't be due before the pledge was made.");
        if (installments.Count > 0 && installments.Sum(i => i.Amount) != Amount)
            throw new DomainException("pledge.installments_total",
                $"The installments add up to {installments.Sum(i => i.Amount):N2}; the pledge is {Amount:N2}.");
        _installments.Clear();
        _installments.AddRange(installments.Select((i, n) => new PledgeInstallment(n + 1, i.DueDate, i.Amount)));
    }

    /// <summary>What the schedule expects to have been paid by a date: the installments due by then, else the whole pledge once its due date has come.</summary>
    public decimal DueBy(DateOnly date) =>
        _installments.Count > 0 ? _installments.Where(i => i.DueDate <= date).Sum(i => i.Amount)
        : DueDate is { } due && due <= date ? Amount : 0m;

    /// <summary>The next due date after a date, if the pledge has one.</summary>
    public DateOnly? NextDueAfter(DateOnly date) =>
        _installments.Count > 0 ? _installments.Where(i => i.DueDate > date).Select(i => (DateOnly?)i.DueDate).Min()
        : DueDate is { } due && due > date ? due : null;

    /// <summary>
    /// Evenly spread installments (the last takes the cents left over), every <paramref name="months"/> months from
    /// <paramref name="first"/>.
    /// </summary>
    public static IReadOnlyList<(DateOnly DueDate, decimal Amount)> Spread(decimal amount, DateOnly first, int count, int months)
    {
        if (count is < 1 or > MaxInstallments)
            throw new DomainException("pledge.installment_count", $"Choose between 1 and {MaxInstallments} installments.");
        if (months is < 1 or > 12)
            throw new DomainException("pledge.installment_interval", "Installments are 1 to 12 months apart.");
        var each = decimal.Floor(amount / count * 100m) / 100m;
        return [.. Enumerable.Range(0, count).Select(n => (first.AddMonths(n * months), n == count - 1 ? amount - each * (count - 1) : each))];
    }

    public const string Source = "nonprofit.pledge";
    public const string CancelSource = "nonprofit.pledge_cancel";

    private Pledge()
    {
    }

    public Guid Id { get; private set; }
    public int Number { get; private set; }
    public Guid DonorId { get; private set; }
    public DateOnly Date { get; private set; }
    public decimal Amount { get; private set; }
    public Guid ReceivableAccountId { get; private set; }
    public Guid IncomeAccountId { get; private set; }
    public Guid? ClassId { get; private set; }

    /// <summary>When the donor expects to have paid it all.</summary>
    public DateOnly? DueDate { get; private set; }

    public string? Memo { get; private set; }
    public DocumentStatus Status { get; private set; }
    public Guid JournalEntryId { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    /// <summary>The unpaid part written off, when the donor won't pay the rest.</summary>
    public decimal CancelledAmount { get; private set; }

    public DateOnly? CancelledOn { get; private set; }
    public Guid? CancelEntryId { get; private set; }

    public static Pledge Create(
        Guid id, int number, Guid donorId, DateOnly date, decimal amount, Guid receivableAccountId, Guid incomeAccountId, Guid? classId,
        DateOnly? dueDate, string? memo, Guid journalEntryId, DateTimeOffset createdAt)
    {
        Check(date, amount, dueDate);
        return new Pledge
        {
            Id = id,
            Number = number,
            DonorId = donorId,
            Date = date,
            Amount = amount,
            ReceivableAccountId = receivableAccountId,
            IncomeAccountId = incomeAccountId,
            ClassId = classId == Guid.Empty ? null : classId,
            DueDate = dueDate,
            Memo = Text.Optional(memo, Donation.MaxTextLength, "pledge.memo_too_long", "Memo"),
            Status = DocumentStatus.Posted,
            JournalEntryId = journalEntryId,
            CreatedAt = createdAt.ToUniversalTime(),
        };
    }

    public static void Check(DateOnly date, decimal amount, DateOnly? dueDate)
    {
        if (amount <= 0m)
            throw new DomainException("pledge.amount", "Enter an amount above zero.");
        if (decimal.Round(amount, 2) != amount)
            throw new DomainException("pledge.amount_cents", "Amounts are in cents.");
        if (dueDate < date)
            throw new DomainException("pledge.due_before_date", "The due date can't be before the pledge.");
    }

    /// <param name="unpaid">What's left after posted payments.</param>
    /// <summary>Reclassifying the income line: payments from here on post their cash-basis income to the new purpose.</summary>
    public void SetClass(Guid? classId) => ClassId = classId == Guid.Empty ? null : classId;

    public void Cancel(decimal unpaid, DateOnly on, Guid entryId)
    {
        if (Status != DocumentStatus.Posted)
            throw new DomainException("pledge.voided", "This pledge is voided.");
        if (CancelEntryId is not null)
            throw new DomainException("pledge.already_cancelled", "The rest of this pledge is already cancelled.");
        if (unpaid <= 0m)
            throw new DomainException("pledge.paid", "This pledge is fully paid.");
        if (on < Date)
            throw new DomainException("pledge.cancel_before", "It can't be cancelled before it was made.");
        (CancelledAmount, CancelledOn, CancelEntryId) = (unpaid, on, entryId);
    }

    public void UndoCancel() => (CancelledAmount, CancelledOn, CancelEntryId) = (0m, null, null);

    public void Void()
    {
        if (Status == DocumentStatus.Voided)
            throw new DomainException("pledge.already_voided", "This pledge is already voided.");
        if (CancelEntryId is not null)
            throw new DomainException("pledge.cancelled", "Undo the cancellation first.");
        Status = DocumentStatus.Voided;
    }
}

/// <summary>What goes on acknowledgment letters and giving statements (one row).</summary>
public sealed class GiftSettings
{
    public const int MaxTextLength = 2000;

    public GiftSettings()
    {
        Id = Guid.CreateVersion7();
    }

    public Guid Id { get; private set; }

    /// <summary>The organization's EIN, printed with the tax-exempt statement when given.</summary>
    public string? Ein { get; private set; }

    public string? SignerName { get; private set; }
    public string? SignerTitle { get; private set; }

    /// <summary>The thank-you paragraph that opens each letter; a default is used when empty.</summary>
    public string? Intro { get; private set; }

    public void Update(string? ein, string? signerName, string? signerTitle, string? intro)
    {
        var digits = ein is null ? "" : new string(ein.Where(char.IsDigit).ToArray());
        if (digits.Length is not (0 or 9))
            throw new DomainException("gifts.ein_invalid", "An EIN has nine digits (12-3456789).");
        Ein = digits.Length == 0 ? null : $"{digits[..2]}-{digits[2..]}";
        SignerName = Text.Optional(signerName, 100, "gifts.signer_too_long", "Signer");
        SignerTitle = Text.Optional(signerTitle, 100, "gifts.title_too_long", "Title");
        Intro = Text.Optional(intro, MaxTextLength, "gifts.intro_too_long", "Opening paragraph");
    }
}
