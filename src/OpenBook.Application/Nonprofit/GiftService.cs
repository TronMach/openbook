using System.Globalization;
using System.Text.Json;
using OpenBook.Application.Accounts;
using OpenBook.Application.Companies;
using OpenBook.Application.Forms;
using OpenBook.Application.Ledger;
using OpenBook.Application.Sales;
using OpenBook.Application.Segments;
using OpenBook.Domain.Accounts;
using OpenBook.Domain.Auditing;
using OpenBook.Domain.Common;
using OpenBook.Domain.Ledger;
using OpenBook.Domain.Nonprofit;
using OpenBook.Domain.Sales;

namespace OpenBook.Application.Nonprofit;

/// <param name="DebitAccountId">Cash: the bank account it went into. In kind: the asset or expense it was recorded to.</param>
/// <param name="IncomeAccountId">Null: "Contributions" (cash) or "In-kind Contributions", created on first use; a pledge payment takes the pledge's.</param>
/// <param name="ClassId">The purpose; a pledge payment takes the pledge's.</param>
/// <summary>A gift's corrected details; the kind and any pledge stay as they are.</summary>
public sealed record ChangeGiftCommand(
    Guid DonorId,
    DateOnly Date,
    decimal Amount,
    Guid DebitAccountId,
    string Reason,
    Guid? IncomeAccountId = null,
    Guid? ClassId = null,
    string? Reference = null,
    string? Memo = null,
    string? Description = null,
    decimal GoodsValue = 0m,
    string? GoodsDescription = null);

public sealed record RecordGiftCommand(
    Guid DonorId,
    DateOnly Date,
    decimal Amount,
    Guid DebitAccountId,
    GiftKind Kind = GiftKind.Cash,
    Guid? IncomeAccountId = null,
    Guid? PledgeId = null,
    Guid? ClassId = null,
    string? Reference = null,
    string? Memo = null,
    string? Description = null,
    decimal GoodsValue = 0m,
    string? GoodsDescription = null,
    PaymentMethod? Method = null,
    string? Campaign = null);

/// <param name="IncomeAccountId">Null: "Contributions", created on first use.</param>
public sealed record PledgeCommand(Guid DonorId, DateOnly Date, decimal Amount, Guid? IncomeAccountId = null, Guid? ClassId = null, DateOnly? DueDate = null, string? Memo = null);

/// <summary>A pledge with what's been paid on it by posted gifts.</summary>
public sealed record PledgeBalance(Pledge Pledge, string Donor, decimal Paid)
{
    public decimal Balance => Pledge.Status == DocumentStatus.Posted ? Pledge.Amount - Paid - Pledge.CancelledAmount : 0m;
}

/// <summary>A donor's giving in a year: who they are, how to reach them, the gifts and their total.</summary>
public sealed record Giver(Customer Donor, IReadOnlyList<Donation> Gifts)
{
    public decimal Total => Gifts.Sum(g => g.Amount);
}

/// <summary>A donor at a glance. <paramref name="OpenPledges"/> is what's still to come on their pledges.</summary>
public sealed record DonorGiving(decimal ThisYear, decimal LastYear, decimal AllTime, int Gifts, DateOnly? LastGift, decimal OpenPledges, decimal Grants)
{
    public bool Any => Gifts > 0 || OpenPledges > 0m || Grants > 0m;
}

/// <summary>One gift of a batch.</summary>
/// <param name="ClassId">Its purpose, when it isn't the batch's.</param>
public sealed record GiftBatchRow(Guid DonorId, decimal Amount, string? Reference = null, string? Memo = null, Guid? ClassId = null);

/// <summary>Gifts opened together, such as a day's mail, with what they share.</summary>
/// <param name="ControlTotal">What the batch should add up to: the deposit slip's figure. Null checks nothing.</param>
public sealed record GiftBatchCommand(DateOnly Date, GiftBatchDefaults Defaults, IReadOnlyList<GiftBatchRow> Rows, decimal? ControlTotal = null);

public sealed record GiftSettingsCommand(string? Ein, string? SignerName, string? SignerTitle, string? Intro);

/// <summary>
/// Gifts and pledges from donors (customer records), and the letters that thank them: an acknowledgment per gift and a
/// giving statement per year, with the IRS wording on what the donor received in return.
/// </summary>
public sealed class GiftService(
    INonprofitStore store, ISalesStore sales, AccountService accounts, SegmentService segments, PostingEngine engine, NonprofitService nonprofit,
    ICompanyStore companies, IFormRenderer renderer, FormDesignService designs, IUnitOfWork unitOfWork, IClock clock, ICurrentUser user,
    Banking.IBankingStore bank)
{
    public const string DonationEntity = "donation";
    public const string PledgeEntity = "pledge";
    public const string SettingsEntity = "gift_settings";
    public const string ContributionsName = "Contributions";
    public const string InKindName = "In-kind Contributions";
    public const string PledgesReceivableName = "Pledges Receivable";

    public async Task<GiftSettings> GetSettingsAsync(CancellationToken ct = default) => await store.GetSettingsAsync(ct) ?? new GiftSettings();

    public async Task<GiftSettings> SaveSettingsAsync(GiftSettingsCommand command, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        var settings = await store.GetSettingsAsync(ct);
        var isNew = settings is null;
        settings ??= new GiftSettings();
        var before = isNew ? null : Snapshot(settings);
        settings.Update(command.Ein, command.SignerName, command.SignerTitle, command.Intro);
        if (isNew)
            store.Add(settings);
        if (Snapshot(settings) != before)
        {
            store.Add(AuditRecord.Create(clock.UtcNow, user.Actor, isNew ? AuditAction.Created : AuditAction.Updated, SettingsEntity, settings.Id, before, Snapshot(settings)));
            await store.SaveChangesAsync(ct);
        }

        return settings;
    }

    /// <summary>Gifts newest first; voided ones included.</summary>
    public Task<int> UnthankedCountAsync(CancellationToken ct = default) => store.CountUnthankedDonationsAsync(ct);

    public async Task<IReadOnlyList<Donation>> ListAsync(DateOnly? from = null, DateOnly? to = null, Guid? donorId = null, CancellationToken ct = default) =>
        (await store.ListDonationsAsync(ct))
            .Where(d => (from is null || d.Date >= from) && (to is null || d.Date <= to) && (donorId is null || d.DonorId == donorId))
            .OrderByDescending(d => d.Date).ThenByDescending(d => d.Number).ToList();

    public Task<Donation?> GetAsync(Guid id, CancellationToken ct = default) => store.GetDonationAsync(id, ct);

    /// <summary>What a donor has given and promised, for their page.</summary>
    public async Task<DonorGiving> GivingAsync(Guid donorId, CancellationToken ct = default)
    {
        var all = (await store.ListDonationsAsync(ct)).Where(d => d.Status == DocumentStatus.Posted).ToList();
        var theirs = all.Where(d => d.DonorId == donorId).ToList();
        var year = CompanyTime.Today.Year;
        var pledged = (await store.ListPledgesAsync(ct)).Where(p => p.DonorId == donorId && p.Status == DocumentStatus.Posted)
            .Sum(p => p.Amount - p.CancelledAmount - Paid(p.Id, all));
        var grants = (await store.ListGrantsAsync(ct)).Where(g => g.FunderId == donorId && g.Status != GrantStatus.Voided).Sum(g => g.Amount);
        return new DonorGiving(theirs.Where(d => d.Date.Year == year).Sum(d => d.Amount), theirs.Where(d => d.Date.Year == year - 1).Sum(d => d.Amount),
            theirs.Sum(d => d.Amount), theirs.Count, theirs.Count == 0 ? null : theirs.Max(d => d.Date), pledged, grants);
    }

    /// <summary>A batch of gifts, all or none. A refusal names the donor it's about.</summary>
    public async Task<IReadOnlyList<Donation>> RecordBatchAsync(GiftBatchCommand command, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(command.Rows);
        ArgumentNullException.ThrowIfNull(command.Defaults);
        if (command.Rows.Count == 0)
            throw new DomainException("donation.batch_empty", "Enter at least one gift.");
        var total = command.Rows.Sum(r => r.Amount);
        if (command.ControlTotal is { } control && control != total)
            throw new DomainException("donation.batch_total", $"The gifts add up to {total.ToString("N2", CultureInfo.CurrentCulture)}, not {control.ToString("N2", CultureInfo.CurrentCulture)}.");

        var gifts = new List<Donation>();
        await using var transaction = await unitOfWork.BeginAsync(ct);
        foreach (var row in command.Rows)
        {
            try
            {
                gifts.Add(await RecordAsync(new RecordGiftCommand(row.DonorId, command.Date, row.Amount, command.Defaults.DebitAccountId, IncomeAccountId: command.Defaults.IncomeAccountId,
                    ClassId: row.ClassId ?? command.Defaults.ClassId, Reference: row.Reference, Memo: row.Memo, Method: command.Defaults.Method,
                    Campaign: command.Defaults.Campaign), ct));
            }
            catch (DomainException ex) when (ex is not ConcurrencyConflictException)
            {
                var donor = await sales.GetCustomerAsync(row.DonorId, ct);
                throw new DomainException(ex.Code, $"{donor?.DisplayName}: {ex.Message}");
            }
        }

        await transaction.CommitAsync(ct);
        return gifts;
    }

    public async Task<Donation> RecordAsync(RecordGiftCommand command, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        Donation.Check(command.Kind, command.Amount, command.Description, command.GoodsValue, command.GoodsDescription, command.PledgeId);
        var donor = await DonorAsync(command.DonorId, ct);
        var chart = await accounts.GetChartAsync(ct);

        var debit = chart.Find(command.DebitAccountId) ?? throw new DomainException("donation.account_required",
            command.Kind == GiftKind.Cash ? "Choose the bank account it went into." : "Choose the account the gift was recorded to.");
        if (!debit.IsActive)
            throw new DomainException("donation.account_inactive", $"'{debit.Name}' is inactive.");
        if (command.Kind == GiftKind.Cash && debit.Type is not (AccountType.Bank or AccountType.OtherCurrentAsset))
            throw new DomainException("donation.deposit_bank", "Money gifts go into a bank account or Undeposited Funds.");
        if (command.Kind == GiftKind.InKind && (debit.Type is AccountType.Bank or AccountType.AccountsReceivable or AccountType.AccountsPayable or AccountType.CreditCard
                                                || debit.Type.ClassOf() is AccountClass.Liability or AccountClass.Equity or AccountClass.Income))
            throw new DomainException("donation.in_kind_account", "Record an in-kind gift to the asset or expense it became.");

        Pledge? pledge = null;
        Guid incomeId;
        var classId = command.ClassId == Guid.Empty ? null : command.ClassId;
        if (command.PledgeId is { } pledgeId)
        {
            pledge = await store.GetPledgeAsync(pledgeId, ct) ?? throw new EntityNotFoundException(PledgeEntity, pledgeId);
            if (pledge.Status != DocumentStatus.Posted)
                throw new DomainException("donation.pledge_voided", "That pledge is voided.");
            if (pledge.DonorId != donor.Id)
                throw new DomainException("donation.pledge_donor", "That pledge is from another donor.");
            var balance = pledge.Amount - pledge.CancelledAmount - Paid(pledge.Id, await store.ListDonationsAsync(ct));
            if (command.Amount > balance)
                throw new DomainException("donation.pledge_overpaid", $"Only {balance.ToString("N2", CultureInfo.CurrentCulture)} is left on that pledge.");
            incomeId = pledge.IncomeAccountId;
            classId = pledge.ClassId;
        }
        else
            incomeId = (await IncomeAccountAsync(chart, command.IncomeAccountId, command.Kind == GiftKind.InKind ? InKindName : ContributionsName, ct)).Id;

        var id = Guid.CreateVersion7();
        var memo = $"Gift from {donor.DisplayName}";
        var debitClass = debit.Type.ClassOf() == AccountClass.Expense ? classId : null;
        JournalEntry entry;
        JournalEntry? cash = null;
        if (pledge is null)
        {
            entry = await engine.StageAsync(new PostJournalEntryCommand(command.Date,
                [JournalLineDraft.Dr(debit.Id, command.Amount, memo, debitClass), JournalLineDraft.Cr(incomeId, command.Amount, memo, classId)],
                command.Memo ?? memo, Donation.Source, id), ct);
        }
        else
        {
            entry = await engine.StageAsync(new PostJournalEntryCommand(command.Date,
                [JournalLineDraft.Dr(debit.Id, command.Amount, memo), JournalLineDraft.Cr(pledge.ReceivableAccountId, command.Amount, memo, classId)],
                command.Memo ?? memo, Donation.Source, id), ct);
            // Cash basis left the pledge out, so it recognizes the income as the money comes in.
            cash = await engine.StageAsync(new PostJournalEntryCommand(command.Date,
                [JournalLineDraft.Dr(pledge.ReceivableAccountId, command.Amount, memo, classId), JournalLineDraft.Cr(incomeId, command.Amount, memo, classId)],
                $"{memo} (pledge {pledge.Number}, cash basis)", Donation.Source, id, EntryBasis.CashOnly), ct);
        }

        var number = (await store.ListDonationsAsync(ct)).Select(d => d.Number).DefaultIfEmpty(0).Max() + 1;
        var donation = Donation.Create(id, number, donor.Id, command.Date, command.Kind, command.Amount, debit.Id, incomeId, pledge?.Id, classId,
            command.Reference, command.Memo, command.Description, command.GoodsValue, command.GoodsDescription, entry.Id, cash?.Id, clock.UtcNow);
        donation.SetMethod(command.Kind == GiftKind.Cash ? command.Method : null);
        donation.SetCampaign(command.Campaign);
        store.Add(donation);
        store.Add(Audit(AuditAction.Posted, donation, null));
        await store.SaveChangesAsync(ct);
        return donation;
    }

    /// <summary>Changes the campaign a gift is credited to (posts nothing; a voided gift too, for the record).</summary>
    public async Task<Donation> SetCampaignAsync(Guid id, string? campaign, CancellationToken ct = default)
    {
        var donation = await store.GetDonationAsync(id, ct) ?? throw new EntityNotFoundException(DonationEntity, id);
        var before = Snapshot(donation);
        donation.SetCampaign(campaign);
        store.Add(Audit(AuditAction.Updated, donation, before));
        await store.SaveChangesAsync(ct);
        return donation;
    }

    /// <summary>The campaigns gifts have been credited to, for the forms' suggestions; the most recently used first.</summary>
    public async Task<IReadOnlyList<string>> CampaignsAsync(CancellationToken ct = default) =>
        [.. (await store.ListDonationsAsync(ct)).Where(d => d.Campaign is not null)
            .GroupBy(d => d.Campaign!, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Max(d => d.Date)).ThenBy(g => g.Key, StringComparer.CurrentCultureIgnoreCase)
            .Select(g => g.OrderByDescending(d => d.Date).First().Campaign!)];

    /// <summary>
    /// Corrects a posted gift in place (same id and number): its entry is voided (the reversal dated the original date) and
    /// a new one posted, in one save. The kind stays; a payment on a pledge is voided and recorded again instead. While
    /// the gift is in a bank deposit, a processor's payout or matched to a bank feed line, its amount and account stay.
    /// Taking restricted money off a purpose needs it not to have been released.
    /// </summary>
    public async Task<Donation> ChangeAsync(Guid id, ChangeGiftCommand command, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (string.IsNullOrWhiteSpace(command.Reason))
            throw new DomainException("donation.change_reason_required", "Say why the gift is being changed.");
        var donation = await store.GetDonationAsync(id, ct) ?? throw new EntityNotFoundException(DonationEntity, id);
        if (donation.Status != DocumentStatus.Posted)
            throw new DomainException("donation.voided", "This gift is voided, so it can't be changed.");
        if (donation.PledgeId is not null)
            throw new DomainException("donation.change_pledge", "This gift pays a pledge. Void it and record it again to change it.");
        Donation.Check(donation.Kind, command.Amount, command.Description, command.GoodsValue, command.GoodsDescription, null);
        var donor = await DonorAsync(command.DonorId, ct);
        var chart = await accounts.GetChartAsync(ct);
        var debit = chart.Find(command.DebitAccountId) ?? throw new DomainException("donation.account_required", "Choose the account the gift went to.");
        if (debit.Id != donation.DebitAccountId && !debit.IsActive)
            throw new DomainException("donation.account_inactive", $"'{debit.Name}' is inactive.");
        if (donation.Kind == GiftKind.Cash && debit.Type is not (AccountType.Bank or AccountType.OtherCurrentAsset))
            throw new DomainException("donation.deposit_bank", "Money gifts go into a bank account or Undeposited Funds.");
        if (donation.Kind == GiftKind.InKind && (debit.Type is AccountType.Bank or AccountType.AccountsReceivable or AccountType.AccountsPayable or AccountType.CreditCard
                                                 || debit.Type.ClassOf() is AccountClass.Liability or AccountClass.Equity or AccountClass.Income))
            throw new DomainException("donation.in_kind_account", "Record an in-kind gift to the asset or expense it became.");

        var moneyStays = command.Amount == donation.Amount && debit.Id == donation.DebitAccountId;
        if ((donation.DepositId is not null || donation.PayoutId is not null) && !moneyStays)
            throw new DomainException("donation.deposited_fixed",
                $"Gift {donation.Number} is in a {(donation.DepositId is not null ? "bank deposit" : "processor's payout")}, so its amount and account stay. Void that first to change them.");
        var feed = await Banking.BankFeedService.TiedToAsync(bank, donation.DebitAccountId, donation.JournalEntryId, null, ct);
        if (feed.Count > 0 && !moneyStays)
            throw new DomainException("donation.feed_matched", $"Gift {donation.Number} is matched to a line from the bank feed, so its amount and account stay. Undo the match first.");

        var classId = command.ClassId == Guid.Empty ? null : command.ClassId;
        var incomeId = command.IncomeAccountId is { } chosen && chosen == donation.IncomeAccountId
            ? chosen
            : (await IncomeAccountAsync(chart, command.IncomeAccountId, donation.Kind == GiftKind.InKind ? InKindName : ContributionsName, ct)).Id;
        // What leaves the old purpose: all of it when the purpose changes, else what the amount drops by.
        var samePurpose = incomeId == donation.IncomeAccountId && classId == donation.ClassId;
        var leaving = samePurpose ? Math.Max(0m, donation.Amount - command.Amount) : donation.Amount;
        if (leaving > 0m)
            await EnsureNotReleasedAsync(donation.IncomeAccountId, donation.ClassId, leaving, ct);

        var before = Snapshot(donation);
        var oldEntry = donation.JournalEntryId;
        await engine.StageVoidAsync(oldEntry, $"Gift changed: {command.Reason.Trim()}", fromDocument: true, keepFeedLines: true, ct);
        var memo = $"Gift from {donor.DisplayName}";
        var debitClass = debit.Type.ClassOf() == AccountClass.Expense ? classId : null;
        var entry = await engine.StageAsync(new PostJournalEntryCommand(command.Date,
            [JournalLineDraft.Dr(debit.Id, command.Amount, memo, debitClass), JournalLineDraft.Cr(incomeId, command.Amount, memo, classId)],
            command.Memo ?? memo, Donation.Source, donation.Id), ct);
        donation.Change(donor.Id, command.Date, command.Amount, debit.Id, incomeId, classId, command.Reference, command.Memo,
            command.Description, command.GoodsValue, command.GoodsDescription, entry.Id);
        foreach (var item in feed)
            item.FollowEntry(oldEntry, entry.Id);
        store.Add(AuditRecord.Create(clock.UtcNow, user.Actor, AuditAction.Updated, DonationEntity, donation.Id, before,
            DocumentChanges.WithReason(Snapshot(donation), command.Reason)));
        await store.SaveChangesAsync(ct);
        return donation;
    }

    public async Task<Donation> VoidAsync(Guid id, string reason, CancellationToken ct = default)
    {
        var donation = await store.GetDonationAsync(id, ct) ?? throw new EntityNotFoundException(DonationEntity, id);
        if (donation.Status == DocumentStatus.Voided)
            throw new DomainException("donation.already_voided", "This gift is already voided.");
        await EnsureNotReleasedAsync(donation.IncomeAccountId, donation.ClassId, donation.Amount, ct);
        var before = Snapshot(donation);
        await engine.StageVoidAsync(donation.JournalEntryId, reason, fromDocument: true, ct);
        if (donation.CashEntryId is { } cash)
            await engine.StageVoidAsync(cash, reason, fromDocument: true, ct);
        donation.Void();
        store.Add(Audit(AuditAction.Voided, donation, before));
        await store.SaveChangesAsync(ct);
        return donation;
    }

    /// <summary>Taking back restricted money that's been released would leave the purpose below zero: the release goes first.</summary>
    private async Task EnsureNotReleasedAsync(Guid incomeAccountId, Guid? classId, decimal amount, CancellationToken ct)
    {
        if (!(await accounts.GetChartAsync(ct)).Get(incomeAccountId).DonorRestricted)
            return;
        var held = (await nonprofit.PurposesAsync(CompanyTime.TodayAt(clock.UtcNow, CompanyTime.Zone), ct)).FirstOrDefault(p => p.ClassId == classId)?.Balance ?? 0m;
        if (held < amount)
            throw new DomainException("gift.released", $"Only {held:N2} of this purpose is still restricted; the rest has been released. Void that release first.");
    }

    /// <summary>Marks gifts as thanked (letters printed or sent another way).</summary>
    public async Task<int> MarkAcknowledgedAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(ids);
        var marked = 0;
        foreach (var id in ids.Distinct())
        {
            var donation = await store.GetDonationAsync(id, ct) ?? throw new EntityNotFoundException(DonationEntity, id);
            if (donation.AcknowledgedAt is not null)
                continue;
            var before = Snapshot(donation);
            donation.MarkAcknowledged(clock.UtcNow);
            store.Add(Audit(AuditAction.Updated, donation, before));
            marked++;
        }

        if (marked > 0)
            await store.SaveChangesAsync(ct);
        return marked;
    }

    public async Task<IReadOnlyList<PledgeBalance>> ListPledgesAsync(Guid? donorId = null, CancellationToken ct = default)
    {
        var gifts = await store.ListDonationsAsync(ct);
        var names = await DonorNamesAsync(ct);
        return (await store.ListPledgesAsync(ct)).Where(p => donorId is null || p.DonorId == donorId)
            .OrderByDescending(p => p.Date).ThenByDescending(p => p.Number)
            .Select(p => new PledgeBalance(p, names.GetValueOrDefault(p.DonorId, "?"), Paid(p.Id, gifts))).ToList();
    }

    public async Task<PledgeBalance?> GetPledgeAsync(Guid id, CancellationToken ct = default) =>
        await store.GetPledgeAsync(id, ct) is { } p
            ? new PledgeBalance(p, (await DonorNamesAsync(ct)).GetValueOrDefault(p.DonorId, "?"), Paid(p.Id, await store.ListDonationsAsync(ct)))
            : null;

    /// <summary>Records a promise to give: Dr "Pledges Receivable" / Cr the contribution account, accrual basis only.</summary>
    public async Task<Pledge> PledgeAsync(PledgeCommand command, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        Pledge.Check(command.Date, command.Amount, command.DueDate);
        var donor = await DonorAsync(command.DonorId, ct);
        var chart = await accounts.GetChartAsync(ct);
        var income = await IncomeAccountAsync(chart, command.IncomeAccountId, ContributionsName, ct);
        var receivable = await PledgesReceivableAsync(ct);
        var classId = command.ClassId == Guid.Empty ? null : command.ClassId;

        var id = Guid.CreateVersion7();
        var memo = $"Pledge from {donor.DisplayName}";
        var entry = await engine.StageAsync(new PostJournalEntryCommand(command.Date,
            [JournalLineDraft.Dr(receivable.Id, command.Amount, memo, classId), JournalLineDraft.Cr(income.Id, command.Amount, memo, classId)],
            command.Memo ?? memo, Pledge.Source, id, EntryBasis.AccrualOnly), ct);
        var number = (await store.ListPledgesAsync(ct)).Select(p => p.Number).DefaultIfEmpty(0).Max() + 1;
        var pledge = Pledge.Create(id, number, donor.Id, command.Date, command.Amount, receivable.Id, income.Id, classId, command.DueDate, command.Memo, entry.Id, clock.UtcNow);
        store.Add(pledge);
        store.Add(AuditRecord.Create(clock.UtcNow, user.Actor, AuditAction.Posted, PledgeEntity, pledge.Id, null, Snapshot(pledge)));
        await store.SaveChangesAsync(ct);
        return pledge;
    }

    public async Task<Pledge> VoidPledgeAsync(Guid id, string reason, CancellationToken ct = default)
    {
        var pledge = await store.GetPledgeAsync(id, ct) ?? throw new EntityNotFoundException(PledgeEntity, id);
        if (pledge.Status == DocumentStatus.Voided)
            throw new DomainException("pledge.already_voided", "This pledge is already voided.");
        if (Paid(pledge.Id, await store.ListDonationsAsync(ct)) > 0m)
            throw new DomainException("pledge.has_payments", "Gifts have been paid on this pledge. Void them first.");
        await EnsureNotReleasedAsync(pledge.IncomeAccountId, pledge.ClassId, pledge.Amount, ct);
        var before = Snapshot(pledge);
        await engine.StageVoidAsync(pledge.JournalEntryId, reason, fromDocument: true, ct);
        pledge.Void();
        store.Add(AuditRecord.Create(clock.UtcNow, user.Actor, AuditAction.Voided, PledgeEntity, pledge.Id, before, Snapshot(pledge)));
        await store.SaveChangesAsync(ct);
        return pledge;
    }

    /// <summary>
    /// Replaces a pledge's installment schedule (posting nothing; empty clears it), audited <c>Updated</c> on the pledge.
    /// </summary>
    public async Task<Pledge> SetInstallmentsAsync(Guid id, IReadOnlyList<(DateOnly DueDate, decimal Amount)> installments, CancellationToken ct = default)
    {
        var pledge = await store.GetPledgeAsync(id, ct) ?? throw new EntityNotFoundException(PledgeEntity, id);
        var before = Schedule(pledge);
        pledge.SetInstallments(installments);
        store.Add(AuditRecord.Create(clock.UtcNow, user.Actor, AuditAction.Updated, PledgeEntity, pledge.Id, before, Schedule(pledge)));
        await store.SaveChangesAsync(ct);
        return pledge;
    }

    private static string Schedule(Pledge p) => JsonSerializer.Serialize(new
    {
        Installments = p.Installments.Select(i => $"{i.DueDate:yyyy-MM-dd} {i.Amount.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)}").ToList(),
    });

    /// <summary>
    /// The donor won't pay the rest: reverses the unpaid part (Dr the contribution account / Cr the receivable, on its
    /// class, accrual basis only). Payments can't be recorded after it, unless it's undone.
    /// </summary>
    public async Task<Pledge> CancelRestAsync(Guid id, DateOnly on, CancellationToken ct = default)
    {
        var pledge = await store.GetPledgeAsync(id, ct) ?? throw new EntityNotFoundException(PledgeEntity, id);
        var unpaid = pledge.Amount - Paid(pledge.Id, await store.ListDonationsAsync(ct));
        pledge.Cancel(unpaid, on, Guid.Empty); // checks before anything is staged
        pledge.UndoCancel();
        await EnsureNotReleasedAsync(pledge.IncomeAccountId, pledge.ClassId, unpaid, ct);
        var before = Snapshot(pledge);
        var memo = $"Rest of pledge {pledge.Number} cancelled";
        var entry = await engine.StageAsync(new PostJournalEntryCommand(on,
            [JournalLineDraft.Dr(pledge.IncomeAccountId, unpaid, memo, pledge.ClassId), JournalLineDraft.Cr(pledge.ReceivableAccountId, unpaid, memo, pledge.ClassId)],
            memo, Pledge.CancelSource, pledge.Id, EntryBasis.AccrualOnly), ct);
        pledge.Cancel(unpaid, on, entry.Id);
        store.Add(AuditRecord.Create(clock.UtcNow, user.Actor, AuditAction.Updated, PledgeEntity, pledge.Id, before, Snapshot(pledge)));
        await store.SaveChangesAsync(ct);
        return pledge;
    }

    public async Task<Pledge> UndoCancelAsync(Guid id, string reason, CancellationToken ct = default)
    {
        var pledge = await store.GetPledgeAsync(id, ct) ?? throw new EntityNotFoundException(PledgeEntity, id);
        if (pledge.CancelEntryId is not { } entry)
            throw new DomainException("pledge.not_cancelled", "Nothing on this pledge is cancelled.");
        var before = Snapshot(pledge);
        await engine.StageVoidAsync(entry, reason, fromDocument: true, ct);
        pledge.UndoCancel();
        store.Add(AuditRecord.Create(clock.UtcNow, user.Actor, AuditAction.Updated, PledgeEntity, pledge.Id, before, Snapshot(pledge)));
        await store.SaveChangesAsync(ct);
        return pledge;
    }

    /// <summary>Every donor who gave in the year (posted gifts), by name.</summary>
    public async Task<IReadOnlyList<Giver>> GiversAsync(int year, CancellationToken ct = default)
    {
        var gifts = (await store.ListDonationsAsync(ct)).Where(d => d.Status == DocumentStatus.Posted && d.Date.Year == year).ToList();
        var donors = (await sales.ListCustomersAsync(ct)).ToDictionary(c => c.Id);
        return gifts.GroupBy(g => g.DonorId)
            .Select(g => new Giver(donors[g.Key], g.OrderBy(d => d.Date).ThenBy(d => d.Number).ToList()))
            .OrderBy(g => g.Donor.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    /// <summary>The thank-you letter for one gift.</summary>
    public async Task<RenderedForm> AcknowledgmentAsync(Guid id, CancellationToken ct = default)
    {
        var donation = await store.GetDonationAsync(id, ct) ?? throw new EntityNotFoundException(DonationEntity, id);
        if (donation.Status != DocumentStatus.Posted)
            throw new DomainException("donation.voided", "This gift is voided.");
        var company = await companies.GetRequiredCompanyAsync(ct);
        using var region = CompanyRegion.Use(company);
        var donor = await DonorAsync(donation.DonorId, ct, activeOnly: false);
        var letter = await LetterAsync(company, donor, "Thank you", donation.Date, [donation], year: null, ct);
        return new RenderedForm(FormService.FileName(company, "Gift acknowledgment", donation.Number.ToString(CultureInfo.InvariantCulture)),
            renderer.Render([letter]), donor.Id, $"Gift acknowledgment {donation.Number}");
    }

    /// <summary>Thank-you letters for several gifts in one PDF, to print together (voided gifts are left out).</summary>
    public async Task<RenderedForm> AcknowledgmentsAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(ids);
        var company = await companies.GetRequiredCompanyAsync(ct);
        using var region = CompanyRegion.Use(company);
        var letters = new List<DonorLetter>();
        foreach (var id in ids.Distinct())
        {
            if (await store.GetDonationAsync(id, ct) is not { Status: DocumentStatus.Posted } donation)
                continue;
            var donor = await DonorAsync(donation.DonorId, ct, activeOnly: false);
            letters.Add(await LetterAsync(company, donor, "Thank you", donation.Date, [donation], year: null, ct));
        }

        if (letters.Count == 0)
            throw new DomainException("donation.none_chosen", "Tick at least one gift.");
        return new RenderedForm(FormService.FileName(company, "Gift acknowledgments", ""), renderer.Render(letters), Guid.Empty, "Gift acknowledgments");
    }

    /// <summary>A donor's giving statement for a calendar year.</summary>
    public async Task<RenderedForm> YearStatementAsync(Guid donorId, int year, CancellationToken ct = default)
    {
        var giver = (await GiversAsync(year, ct)).FirstOrDefault(g => g.Donor.Id == donorId)
                    ?? throw new DomainException("donation.none_in_year", $"No gifts from this donor in {year}.");
        var company = await companies.GetRequiredCompanyAsync(ct);
        using var region = CompanyRegion.Use(company);
        var letter = await LetterAsync(company, giver.Donor, $"Giving statement {year}", YearEnd(year), giver.Gifts, year, ct);
        return new RenderedForm(FormService.FileName(company, $"Giving statement {year}", giver.Donor.DisplayName), renderer.Render([letter]), donorId,
            $"Giving statement {year}");
    }

    /// <summary>Every donor's giving statement for the year in one PDF, a letter per donor, to print.</summary>
    public async Task<RenderedForm> YearStatementsAsync(int year, CancellationToken ct = default)
    {
        var givers = await GiversAsync(year, ct);
        if (givers.Count == 0)
            throw new DomainException("donation.none_in_year", $"No gifts were recorded in {year}.");
        var company = await companies.GetRequiredCompanyAsync(ct);
        using var region = CompanyRegion.Use(company);
        var letters = new List<DonorLetter>();
        foreach (var giver in givers)
            letters.Add(await LetterAsync(company, giver.Donor, $"Giving statement {year}", YearEnd(year), giver.Gifts, year, ct));
        return new RenderedForm(FormService.FileName(company, $"Giving statements {year}", ""), renderer.Render(letters), Guid.Empty, $"Giving statements {year}");
    }

    /// <summary>The donors who gave in the year (posted gifts), in name order, for emailing their statements.</summary>
    public async Task<IReadOnlyList<Guid>> DonorsInYearAsync(int year, CancellationToken ct = default) =>
        [.. (await GiversAsync(year, ct)).Select(g => g.Donor.Id)];

    /// <summary>Marks a gift thanked once its acknowledgment is emailed (saved by the caller) and gives the audit record.</summary>
    internal async Task<AuditRecord> MarkAcknowledgedAsync(Guid id, CancellationToken ct)
    {
        var donation = await store.GetDonationAsync(id, ct) ?? throw new EntityNotFoundException(DonationEntity, id);
        var before = Snapshot(donation);
        donation.MarkAcknowledged(clock.UtcNow);
        return Audit(AuditAction.Emailed, donation, before);
    }

    /// <summary>The letter's wording: the gifts, what the donor got in return (or that they got nothing), and the tax-exempt statement.</summary>
    private async Task<DonorLetter> LetterAsync(
        Domain.Companies.Company company, Customer donor, string title, DateOnly date, IReadOnlyList<Donation> gifts, int? year, CancellationToken ct)
    {
        var settings = await GetSettingsAsync(ct);
        var names = await segments.NamesAsync(ct);
        var pledges = (await store.ListPledgesAsync(ct)).ToDictionary(p => p.Id, p => p.Number);
        var org = company.LegalName ?? company.Name;
        string Money(decimal amount) => $"{company.HomeCurrency.Code} {amount.ToString("N2", CultureInfo.CurrentCulture)}";

        string Describe(Donation d)
        {
            var text = d.Kind == GiftKind.InKind ? $"In-kind gift: {d.Description}" : d.PledgeId is { } p ? $"Payment on pledge {pledges.GetValueOrDefault(p)}" : "Gift";
            if (d.ClassId is { } c && names.GetValueOrDefault(c) is { } purpose)
                text += $" for {purpose[(purpose.LastIndexOf(':') + 1)..]}";
            if (d.Reference is { } reference)
                text += $" (ref. {reference})";
            return text;
        }

        var intro = settings.Intro ?? (year is { } y
            ? $"Thank you for your generous support of {org} in {y}. These are the gifts we received from you this year."
            : $"Thank you for your generous gift to {org}.");
        var paragraphs = new List<string>();
        var withGoods = gifts.Where(g => g.GoodsValue > 0m).ToList();
        foreach (var g in withGoods)
        {
            // An in-kind gift isn't valued here: only what the donor received is.
            paragraphs.Add(g.Kind == GiftKind.InKind
                ? $"In return for your gift on {g.Date.ToString("d", CultureInfo.CurrentCulture)}, you received {g.GoodsDescription}, with an estimated fair market value of {Money(g.GoodsValue)}."
                : $"In return for your gift of {Money(g.Amount)} on {g.Date.ToString("d", CultureInfo.CurrentCulture)}, you received {g.GoodsDescription}, " +
                  $"with an estimated fair market value of {Money(g.GoodsValue)}. The part of that gift you may deduct is limited to {Money(g.Deductible)}.");
        }

        if (withGoods.Count == 0)
            paragraphs.Add(gifts.Count == 1 ? "No goods or services were provided in exchange for this contribution." : "No goods or services were provided in exchange for these contributions.");
        else if (withGoods.Count < gifts.Count)
            paragraphs.Add("No goods or services were provided in exchange for your other contributions.");
        if (gifts.Any(g => g.Kind == GiftKind.InKind))
            paragraphs.Add("In-kind gifts are described, not valued: their value for tax purposes is for you to determine.");
        paragraphs.Add(settings.Ein is { } ein
            ? $"{org} is a tax-exempt organization under section 501(c)(3) of the Internal Revenue Code (EIN {ein}). Please keep this letter for your tax records."
            : "Please keep this letter for your tax records.");

        return new DonorLetter(
            FormService.CompanyParty(company),
            FormService.CustomerParty(donor),
            title,
            date,
            intro,
            gifts.Select(g => new DonorLetterGift(g.Date, Describe(g), g.Kind == GiftKind.InKind ? 0m : g.Amount)).ToList(),
            paragraphs,
            settings.SignerName,
            settings.SignerTitle,
            company.HomeCurrency.Code,
            await designs.DefaultStyleAsync(ct));
    }

    private static DateOnly YearEnd(int year) => new(year, 12, 31);

    internal static decimal Paid(Guid pledgeId, IEnumerable<Donation> gifts) =>
        gifts.Where(d => d.PledgeId == pledgeId && d.Status == DocumentStatus.Posted).Sum(d => d.Amount);

    private async Task<Customer> DonorAsync(Guid id, CancellationToken ct, bool activeOnly = true)
    {
        var donor = await sales.GetCustomerAsync(id, ct) ?? throw new DomainException("donation.donor_required", "Choose the donor.");
        if (activeOnly && !donor.IsActive)
            throw new DomainException("donation.donor_inactive", $"{donor.DisplayName} is inactive.");
        return donor;
    }

    private async Task<Dictionary<Guid, string>> DonorNamesAsync(CancellationToken ct) =>
        (await sales.ListCustomersAsync(ct)).ToDictionary(c => c.Id, c => c.DisplayName);

    private async Task<Account> IncomeAccountAsync(ChartOfAccounts chart, Guid? id, string defaultName, CancellationToken ct)
    {
        if (id is { } chosen && chosen != Guid.Empty)
        {
            var account = chart.Find(chosen) ?? throw new EntityNotFoundException("account", chosen);
            if (account.Type is not (AccountType.Income or AccountType.OtherIncome))
                throw new DomainException("donation.income_type", $"'{account.Name}' isn't an income account.");
            if (!account.IsActive)
                throw new DomainException("donation.account_inactive", $"'{account.Name}' is inactive.");
            return account;
        }

        return chart.Ordered(includeInactive: false).Select(x => x.Account)
                   .FirstOrDefault(a => a.Type == AccountType.Income && string.Equals(a.Name, defaultName, StringComparison.OrdinalIgnoreCase))
               ?? await accounts.CreateAsync(new CreateAccountCommand(defaultName, AccountType.Income), ct);
    }

    private async Task<Account> PledgesReceivableAsync(CancellationToken ct) =>
        (await accounts.GetChartAsync(ct)).Ordered(includeInactive: false).Select(x => x.Account)
            .FirstOrDefault(a => a.Type == AccountType.OtherCurrentAsset && string.Equals(a.Name, PledgesReceivableName, StringComparison.OrdinalIgnoreCase))
        ?? await accounts.CreateAsync(new CreateAccountCommand(PledgesReceivableName, AccountType.OtherCurrentAsset, Description: "Promised gifts not yet received."), ct);

    private AuditRecord Audit(AuditAction action, Donation d, string? before) =>
        AuditRecord.Create(clock.UtcNow, user.Actor, action, DonationEntity, d.Id, before, Snapshot(d));

    private static string Snapshot(Donation d) => JsonSerializer.Serialize(new
    {
        d.Number,
        d.DonorId,
        d.Date,
        Kind = d.Kind.ToString(),
        d.Amount,
        d.DebitAccountId,
        d.IncomeAccountId,
        d.PledgeId,
        d.ClassId,
        d.Reference,
        d.Memo,
        d.Description,
        d.GoodsValue,
        d.GoodsDescription,
        d.Campaign,
        Status = d.Status.ToString(),
        d.AcknowledgedAt,
    });

    private static string Snapshot(Pledge p) => JsonSerializer.Serialize(new
    {
        p.Number,
        p.DonorId,
        p.Date,
        p.Amount,
        p.IncomeAccountId,
        p.ClassId,
        p.DueDate,
        p.Memo,
        Status = p.Status.ToString(),
        p.CancelledAmount,
        p.CancelledOn,
    });

    private static string Snapshot(GiftSettings s) => JsonSerializer.Serialize(new { s.Ein, s.SignerName, s.SignerTitle, s.Intro });
}
