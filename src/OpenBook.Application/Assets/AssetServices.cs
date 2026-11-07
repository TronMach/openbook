using System.Text.Json;
using OpenBook.Application.Accounts;
using OpenBook.Application.Ledger;
using OpenBook.Domain.Accounts;
using OpenBook.Domain.Assets;
using OpenBook.Domain.Auditing;
using OpenBook.Domain.Common;
using OpenBook.Domain.Ledger;

namespace OpenBook.Application.Assets;

public interface IAssetStore
{
    Task<IReadOnlyList<FixedAsset>> ListAssetsAsync(CancellationToken ct);
    Task<FixedAsset?> GetAssetAsync(Guid id, CancellationToken ct);
    Task<IReadOnlyList<DepreciationRun>> ListRunsAsync(CancellationToken ct);
    Task<DepreciationRun?> GetRunAsync(Guid id, CancellationToken ct);
    Task<IReadOnlyList<Loan>> ListLoansAsync(CancellationToken ct);
    Task<Loan?> GetLoanAsync(Guid id, CancellationToken ct);

    void Add(FixedAsset asset);
    void Add(DepreciationRun run);
    void Add(Loan loan);
    void Add(AuditRecord record);

    Task SaveChangesAsync(CancellationToken ct);
}

/// <summary>An asset with what's been depreciated so far.</summary>
public sealed record AssetPosition(FixedAsset Asset, decimal Accumulated)
{
    public decimal BookValue => Asset.Cost - Accumulated;
}

/// <param name="GainLossAccountId">Null uses "Gain or Loss on Disposal of Assets" (other income, created on first use).</param>
public sealed record DisposeAssetCommand(DateOnly Date, decimal Proceeds = 0m, Guid? ProceedsAccountId = null, Guid? GainLossAccountId = null);

/// <summary>Accounts the asset and loan services create on first use.</summary>
public sealed class AssetAccounts(AccountService accounts)
{
    public const string AccumulatedName = "Accumulated Depreciation";
    public const string ExpenseName = "Depreciation Expense";
    public const string GainLossName = "Gain or Loss on Disposal of Assets";
    public const string InterestName = "Interest Expense";

    public Task<Account> AccumulatedAsync(CancellationToken ct) =>
        GetOrCreateAsync(AccumulatedName, AccountType.FixedAsset, "Depreciation taken on fixed assets (a negative asset).", ct);

    public Task<Account> ExpenseAsync(CancellationToken ct) =>
        GetOrCreateAsync(ExpenseName, AccountType.Expense, "The cost of fixed assets spread over their useful lives.", ct);

    public Task<Account> GainLossAsync(CancellationToken ct) =>
        GetOrCreateAsync(GainLossName, AccountType.OtherIncome, "What was made or lost selling or scrapping fixed assets.", ct);

    /// <summary>The chart's interest account, an expense or other expense (the starter charts' is the latter), else a new one.</summary>
    public async Task<Account> InterestAsync(CancellationToken ct) =>
        (await accounts.GetChartAsync(ct)).Ordered(includeInactive: false).Select(x => x.Account)
            .FirstOrDefault(a => a.Type == AccountType.OtherExpense && string.Equals(a.Name, InterestName, StringComparison.OrdinalIgnoreCase))
        ?? await GetOrCreateAsync(InterestName, AccountType.Expense, "Interest paid on loans.", ct);

    /// <summary>An active account of one of the given types, or refused.</summary>
    public async Task<Account> CheckAsync(Guid id, string role, CancellationToken ct, params AccountType[] types)
    {
        var account = (await accounts.GetChartAsync(ct)).Find(id) ?? throw new EntityNotFoundException("account", id);
        if (!types.Contains(account.Type))
            throw new DomainException("assets.account_type", $"'{account.Name}' can't be the {role}.");
        if (!account.IsActive)
            throw new DomainException("assets.account_inactive", $"'{account.Name}' is inactive.");
        return account;
    }

    private async Task<Account> GetOrCreateAsync(string name, AccountType type, string description, CancellationToken ct)
    {
        var chart = await accounts.GetChartAsync(ct);
        return chart.Ordered(includeInactive: false).Select(x => x.Account)
                   .FirstOrDefault(a => a.Type == type && string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase))
               ?? await accounts.CreateAsync(new CreateAccountCommand(name, type, Description: description), ct);
    }
}

/// <summary>The fixed asset register: assets, their depreciation so far, and disposal.</summary>
public sealed class FixedAssetService(IAssetStore store, AssetAccounts accounts, PostingEngine engine, IClock clock, ICurrentUser user)
{
    public const string EntityType = "fixed_asset";

    public async Task<IReadOnlyList<AssetPosition>> ListAsync(CancellationToken ct = default)
    {
        var runs = await store.ListRunsAsync(ct);
        return (await store.ListAssetsAsync(ct))
            .OrderBy(a => a.Number ?? "￿", StringComparer.OrdinalIgnoreCase).ThenBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
            .Select(a => new AssetPosition(a, Accumulated(a, runs)))
            .ToList();
    }

    public async Task<AssetPosition?> GetAsync(Guid id, CancellationToken ct = default) =>
        await store.GetAssetAsync(id, ct) is { } asset ? new AssetPosition(asset, Accumulated(asset, await store.ListRunsAsync(ct))) : null;

    /// <summary>Depreciation in the books: before OpenBook, posted by runs, and posted with the disposal.</summary>
    public static decimal Accumulated(FixedAsset asset, IEnumerable<DepreciationRun> runs)
    {
        ArgumentNullException.ThrowIfNull(asset);
        return asset.PriorDepreciation + Posted(asset, runs) + asset.DisposalDepreciation;
    }

    /// <summary>Depreciation posted by runs that aren't voided.</summary>
    public static decimal Posted(FixedAsset asset, IEnumerable<DepreciationRun> runs) =>
        runs.Where(r => r.Status == DocumentStatus.Posted).SelectMany(r => r.Lines).Where(l => l.AssetId == asset.Id).Sum(l => l.Amount);

    /// <summary>
    /// Adds an asset to the register (nothing is posted: record its purchase to the asset account the usual way).
    /// Empty accumulated depreciation and expense accounts use "Accumulated Depreciation" and "Depreciation Expense".
    /// </summary>
    public async Task<FixedAsset> CreateAsync(FixedAssetDetails details, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(details);
        details = await CheckAccountsAsync(details, ct);
        var asset = FixedAsset.Create(details, clock.UtcNow);
        store.Add(asset);
        store.Add(Audit(AuditAction.Created, asset, null));
        await store.SaveChangesAsync(ct);
        return asset;
    }

    /// <summary>Changes an asset; once depreciation is posted only its name, number and description can change.</summary>
    public async Task<FixedAsset> UpdateAsync(Guid id, FixedAssetDetails details, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(details);
        var asset = await store.GetAssetAsync(id, ct) ?? throw new EntityNotFoundException(EntityType, id);
        if (asset.IsRemoved)
            throw new DomainException("asset.removed", $"{asset.Name} is removed from the register. Put it back first.");
        var before = Snapshot(asset);
        if (asset.IsDisposed || Posted(asset, await store.ListRunsAsync(ct)) > 0m)
        {
            details = await CheckAccountsAsync(details, ct);
            var changed = details.AssetAccountId != asset.AssetAccountId || details.AccumulatedAccountId != asset.AccumulatedAccountId
                || details.ExpenseAccountId != asset.ExpenseAccountId || details.InServiceDate != asset.InServiceDate || details.Cost != asset.Cost
                || details.Method != asset.Method || details.LifeMonths != asset.LifeMonths || details.Salvage != asset.Salvage
                || details.PriorDepreciation != asset.PriorDepreciation || (details.PriorDepreciation > 0m && details.PriorThrough != asset.PriorThrough)
                || (details.Method == DepreciationMethod.Macrs && details.MacrsClass != asset.MacrsClass)
                || (details.Method == DepreciationMethod.DecliningBalance && details.DecliningPercent != asset.DecliningPercent);
            if (changed)
                throw new DomainException("asset.depreciated", $"{asset.Name} has depreciation posted, so only its name, number and description can change. Void the depreciation first.");
            asset.Rename(details.Name, details.Number, details.Description);
            asset.SetClass(details.ClassId);
        }
        else
        {
            asset.Update(await CheckAccountsAsync(details, ct));
        }

        store.Add(Audit(AuditAction.Updated, asset, before));
        await store.SaveChangesAsync(ct);
        return asset;
    }

    /// <summary>
    /// Takes the asset off the books: posts depreciation not yet posted through the disposal date, removes its cost
    /// and accumulated depreciation, records any proceeds and the gain or loss.
    /// </summary>
    public async Task<FixedAsset> DisposeAsync(Guid id, DisposeAssetCommand command, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        var asset = await store.GetAssetAsync(id, ct) ?? throw new EntityNotFoundException(EntityType, id);
        if (asset.IsDisposed)
            throw new DomainException("asset.disposed", $"{asset.Name} is already disposed.");
        if (command.Date < asset.InServiceDate)
            throw new DomainException("asset.disposal_date", "An asset can't be disposed of before it was placed in service.");
        Money.EnsureCents(command.Proceeds);
        if (command.Proceeds < 0m)
            throw new DomainException("asset.proceeds_negative", "Proceeds can't be negative.");
        Account? proceedsAccount = null;
        if (command.Proceeds > 0m)
        {
            var proceedsId = command.ProceedsAccountId ?? throw new DomainException("asset.proceeds_account", "Choose where the sale money went.");
            proceedsAccount = await accounts.CheckAsync(proceedsId, "account the proceeds went to", ct, AccountType.Bank, AccountType.OtherCurrentAsset);
        }

        var gainLoss = command.GainLossAccountId is { } chosen
            ? await accounts.CheckAsync(chosen, "gain or loss account", ct, AccountType.OtherIncome, AccountType.OtherExpense, AccountType.Income, AccountType.Expense)
            : await accounts.GainLossAsync(ct);

        var runs = await store.ListRunsAsync(ct);
        if (runs.FirstOrDefault(r => r.Status == DocumentStatus.Posted && r.Date > command.Date && r.Lines.Any(l => l.AssetId == asset.Id && l.Amount != 0m)) is { } later)
            throw new DomainException("asset.depreciated_after", $"Depreciation is posted through {later.Date:yyyy-MM-dd}, after this date. Void the runs back to {command.Date:yyyy-MM-dd} first, or it would stay in the books after the sale.");

        var posted = Posted(asset, runs);
        var catchUp = Math.Max(0m, Depreciation.ScheduledThrough(asset, command.Date) - asset.PriorDepreciation - posted);
        var booked = asset.PriorDepreciation + posted;
        var gain = command.Proceeds - (asset.Cost - booked - catchUp);

        var lines = new List<JournalLineDraft>();
        if (booked > 0m)
            lines.Add(JournalLineDraft.Dr(asset.AccumulatedAccountId, booked, "Accumulated depreciation removed"));
        if (catchUp > 0m)
            lines.Add(JournalLineDraft.Dr(asset.ExpenseAccountId, catchUp, "Depreciation to disposal", asset.ClassId));
        if (proceedsAccount is not null)
            lines.Add(JournalLineDraft.Dr(proceedsAccount.Id, command.Proceeds, "Proceeds"));
        lines.Add(JournalLineDraft.Cr(asset.AssetAccountId, asset.Cost, "Cost removed"));
        if (gain > 0m)
            lines.Add(JournalLineDraft.Cr(gainLoss.Id, gain, "Gain on disposal"));
        else if (gain < 0m)
            lines.Add(JournalLineDraft.Dr(gainLoss.Id, -gain, "Loss on disposal"));

        var before = Snapshot(asset);
        var entry = await engine.StageAsync(new PostJournalEntryCommand(command.Date, lines, $"Disposal · {asset.Name}", FixedAsset.DisposalSource, asset.Id), ct);
        asset.Dispose(command.Date, command.Proceeds, proceedsAccount?.Id, catchUp, entry.Id);
        store.Add(Audit(AuditAction.Updated, asset, before));
        await store.SaveChangesAsync(ct);
        return asset;
    }

    /// <summary>
    /// Takes an asset entered by mistake off the register (nothing posts), refused once depreciation has been posted for it
    /// or it was disposed: those are undone first. Its files and notes stay; <see cref="PutBackAsync"/> undoes it.
    /// </summary>
    public async Task<FixedAsset> RemoveAsync(Guid id, CancellationToken ct = default)
    {
        var asset = await store.GetAssetAsync(id, ct) ?? throw new EntityNotFoundException(EntityType, id);
        if (Posted(asset, await store.ListRunsAsync(ct)) > 0m)
            throw new DomainException("asset.depreciated", $"{asset.Name} has depreciation posted. Void the depreciation runs first.");
        var before = Snapshot(asset);
        asset.Remove();
        store.Add(Audit(AuditAction.Updated, asset, before));
        await store.SaveChangesAsync(ct);
        return asset;
    }

    public async Task<FixedAsset> PutBackAsync(Guid id, CancellationToken ct = default)
    {
        var asset = await store.GetAssetAsync(id, ct) ?? throw new EntityNotFoundException(EntityType, id);
        var before = Snapshot(asset);
        asset.PutBack();
        store.Add(Audit(AuditAction.Updated, asset, before));
        await store.SaveChangesAsync(ct);
        return asset;
    }

    public async Task<FixedAsset> UndoDisposalAsync(Guid id, string reason, CancellationToken ct = default)
    {
        var asset = await store.GetAssetAsync(id, ct) ?? throw new EntityNotFoundException(EntityType, id);
        var entryId = asset.DisposalEntryId ?? throw new DomainException("asset.not_disposed", $"{asset.Name} isn't disposed.");
        var before = Snapshot(asset);
        await engine.StageVoidAsync(entryId, reason, fromDocument: true, ct);
        asset.UndoDisposal();
        store.Add(Audit(AuditAction.Updated, asset, before));
        await store.SaveChangesAsync(ct);
        return asset;
    }

    private async Task<FixedAssetDetails> CheckAccountsAsync(FixedAssetDetails details, CancellationToken ct)
    {
        await accounts.CheckAsync(details.AssetAccountId, "asset account", ct, AccountType.FixedAsset, AccountType.OtherAsset);
        var accumulated = details.AccumulatedAccountId == Guid.Empty
            ? (await accounts.AccumulatedAsync(ct)).Id
            : (await accounts.CheckAsync(details.AccumulatedAccountId, "accumulated depreciation account", ct, AccountType.FixedAsset, AccountType.OtherAsset)).Id;
        var expense = details.ExpenseAccountId == Guid.Empty
            ? (await accounts.ExpenseAsync(ct)).Id
            : (await accounts.CheckAsync(details.ExpenseAccountId, "depreciation expense account", ct, AccountType.Expense, AccountType.OtherExpense)).Id;
        if (accumulated == details.AssetAccountId)
            throw new DomainException("asset.same_accounts", "Accumulated depreciation needs its own account, not the asset's.");
        return details with { AccumulatedAccountId = accumulated, ExpenseAccountId = expense };
    }

    private AuditRecord Audit(AuditAction action, FixedAsset a, string? before) =>
        AuditRecord.Create(clock.UtcNow, user.Actor, action, EntityType, a.Id, before, Snapshot(a));

    private static string Snapshot(FixedAsset a) => JsonSerializer.Serialize(new
    {
        a.Name,
        a.ClassId,
        a.Number,
        a.AssetAccountId,
        a.AccumulatedAccountId,
        a.ExpenseAccountId,
        a.InServiceDate,
        a.Cost,
        a.Salvage,
        Method = a.Method.ToString(),
        a.LifeMonths,
        a.DecliningPercent,
        MacrsClass = a.MacrsClass?.ToString(),
        a.PriorDepreciation,
        a.PriorThrough,
        Status = a.Status.ToString(),
        a.DisposedOn,
        a.Proceeds,
        a.DisposalDepreciation,
    });
}

/// <summary>One asset's depreciation due in a run.</summary>
public sealed record DepreciationDue(FixedAsset Asset, decimal Amount);

/// <summary>Posts depreciation: for every active asset, what its schedule calls for through a date less what's in the books.</summary>
public sealed class DepreciationService(IAssetStore store, PostingEngine engine, IClock clock, ICurrentUser user)
{
    public const string EntityType = "depreciation_run";

    public async Task<IReadOnlyList<DepreciationRun>> ListRunsAsync(CancellationToken ct = default) =>
        (await store.ListRunsAsync(ct)).OrderByDescending(r => r.Date).ThenByDescending(r => r.CreatedAt).ToList();

    public Task<DepreciationRun?> GetRunAsync(Guid id, CancellationToken ct = default) => store.GetRunAsync(id, ct);

    public async Task<IReadOnlyList<DepreciationDue>> PreviewAsync(DateOnly through, CancellationToken ct = default)
    {
        var runs = await store.ListRunsAsync(ct);
        return (await store.ListAssetsAsync(ct))
            .Where(a => a.Status == FixedAssetStatus.Active && a.Method != DepreciationMethod.None && a.InServiceDate <= through)
            .Select(a => new DepreciationDue(a, Depreciation.ScheduledThrough(a, through) - a.PriorDepreciation - FixedAssetService.Posted(a, runs)))
            .Where(d => d.Amount > 0m)
            .OrderBy(d => d.Asset.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Posts one entry with every asset's depreciation due through the date (each asset's own lines).</summary>
    public async Task<DepreciationRun> RunAsync(DateOnly through, CancellationToken ct = default)
    {
        var due = await PreviewAsync(through, ct);
        if (due.Count == 0)
            throw new DomainException("depreciation.nothing_due", $"No depreciation is due through {through:MMM d, yyyy}.");
        var id = Guid.CreateVersion7();
        var lines = due.SelectMany(d => new[]
        {
            JournalLineDraft.Dr(d.Asset.ExpenseAccountId, d.Amount, d.Asset.Name, d.Asset.ClassId),
            JournalLineDraft.Cr(d.Asset.AccumulatedAccountId, d.Amount, d.Asset.Name),
        }).ToList();
        var entry = await engine.StageAsync(new PostJournalEntryCommand(through, lines, $"Depreciation through {through:MMM d, yyyy}", DepreciationRun.Source, id), ct);
        var run = DepreciationRun.Create(id, through, due.Select(d => (d.Asset.Id, d.Amount)).ToList(), entry.Id, clock.UtcNow);
        store.Add(run);
        store.Add(Audit(AuditAction.Posted, run, null));
        await store.SaveChangesAsync(ct);
        return run;
    }

    /// <summary>Voids a run; only the latest one, and not once one of its assets is disposed (undo the disposal first).</summary>
    public async Task<DepreciationRun> VoidAsync(Guid id, string reason, CancellationToken ct = default)
    {
        var run = await store.GetRunAsync(id, ct) ?? throw new EntityNotFoundException(EntityType, id);
        if (run.Status == DocumentStatus.Voided)
            throw new DomainException("depreciation.already_voided", "This depreciation is already voided.");
        var runs = await store.ListRunsAsync(ct);
        var latest = runs.Where(r => r.Status == DocumentStatus.Posted).OrderBy(r => r.Date).ThenBy(r => r.CreatedAt).Last();
        if (latest.Id != run.Id)
            throw new DomainException("depreciation.not_latest", "Only the latest depreciation can be voided. Void the later ones first.");
        var assets = (await store.ListAssetsAsync(ct)).ToDictionary(a => a.Id);
        if (run.Lines.FirstOrDefault(l => assets[l.AssetId].IsDisposed) is { } disposed)
            throw new DomainException("depreciation.asset_disposed", $"{assets[disposed.AssetId].Name} has been disposed of since. Undo the disposal first.");

        var before = Snapshot(run);
        await engine.StageVoidAsync(run.JournalEntryId, reason, fromDocument: true, ct);
        run.Void();
        store.Add(Audit(AuditAction.Voided, run, before));
        await store.SaveChangesAsync(ct);
        return run;
    }

    private AuditRecord Audit(AuditAction action, DepreciationRun r, string? before) =>
        AuditRecord.Create(clock.UtcNow, user.Actor, action, EntityType, r.Id, before, Snapshot(r));

    private static string Snapshot(DepreciationRun r) => JsonSerializer.Serialize(new
    {
        r.Date,
        Status = r.Status.ToString(),
        r.Total,
        Lines = r.Lines.Select(l => new { l.AssetId, l.Amount }),
    });
}

/// <param name="ProceedsAccountId">Where the borrowed money went, to record it now (Dr this account, Cr the loan); null when the loan is already in the books.</param>
public sealed record CreateLoanCommand(LoanDetails Details, Guid? ProceedsAccountId = null, DateOnly? ProceedsDate = null);

/// <param name="Amount">Null pays the next scheduled payment.</param>
public sealed record LoanPaymentCommand(DateOnly Date, Guid PaymentAccountId, decimal? Amount = null);

/// <summary>Loans: amortization schedules and payments split into interest and principal.</summary>
public sealed class LoanService(IAssetStore store, AssetAccounts accounts, PostingEngine engine, IClock clock, ICurrentUser user)
{
    public const string EntityType = "loan";

    public async Task<IReadOnlyList<Loan>> ListAsync(CancellationToken ct = default) =>
        (await store.ListLoansAsync(ct)).Where(l => !l.IsVoided).OrderBy(l => l.Name, StringComparer.OrdinalIgnoreCase).ToList();

    public Task<Loan?> GetAsync(Guid id, CancellationToken ct = default) => store.GetLoanAsync(id, ct);

    /// <summary>What's left to pay from today's balance, numbered after the payments made.</summary>
    public static IReadOnlyList<Installment> RemainingSchedule(Loan loan)
    {
        ArgumentNullException.ThrowIfNull(loan);
        var made = loan.PaymentsMade.Where(p => !p.IsVoided).ToList();
        // Counted from the first date, so a loan due on the 31st comes back to it after a short month or a late payment.
        return Amortization.Schedule(loan.Balance, loan.AnnualRatePercent, loan.PaymentAmount, loan.FirstPaymentDate, made.Count + 1, made.Count);
    }

    public async Task<Loan> CreateAsync(CreateLoanCommand command, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        var details = await CheckAccountsAsync(command.Details, ct);
        var loan = Loan.Create(details, clock.UtcNow);
        if (command.ProceedsAccountId is { } proceedsId)
        {
            var account = await accounts.CheckAsync(proceedsId, "account the loan went into", ct,
                AccountType.Bank, AccountType.OtherCurrentAsset, AccountType.FixedAsset, AccountType.OtherAsset);
            var date = command.ProceedsDate ?? CompanyTime.TodayAt(clock.UtcNow, CompanyTime.Zone);
            var entry = await engine.StageAsync(new PostJournalEntryCommand(date,
                [JournalLineDraft.Dr(account.Id, loan.Principal), JournalLineDraft.Cr(loan.LiabilityAccountId, loan.Principal)],
                $"Loan received · {loan.Name}", Loan.Source, loan.Id), ct);
            loan.RecordProceeds(account.Id, date, entry.Id);
        }

        store.Add(loan);
        store.Add(Audit(AuditAction.Created, loan, null));
        await store.SaveChangesAsync(ct);
        return loan;
    }

    public async Task<Loan> UpdateAsync(Guid id, LoanDetails details, CancellationToken ct = default)
    {
        var loan = await store.GetLoanAsync(id, ct) ?? throw new EntityNotFoundException(EntityType, id);
        details = await CheckAccountsAsync(details, ct);
        if (loan.ProceedsEntryId is not null && (details.Principal != loan.Principal || details.LiabilityAccountId != loan.LiabilityAccountId))
            throw new DomainException("loan.proceeds_posted", "The amount and loan account can't change once the loan money is recorded.");
        var before = Snapshot(loan);
        loan.Update(details);
        store.Add(Audit(AuditAction.Updated, loan, before));
        await store.SaveChangesAsync(ct);
        return loan;
    }

    /// <summary>The name and notes, which stay changeable after payments.</summary>
    /// <param name="classId">The class interest goes to from now on; null keeps it, <see cref="Guid.Empty"/> clears it.</param>
    public async Task<Loan> DescribeAsync(Guid id, string name, string? notes, Guid? classId = null, CancellationToken ct = default)
    {
        var loan = await store.GetLoanAsync(id, ct) ?? throw new EntityNotFoundException(EntityType, id);
        var before = Snapshot(loan);
        loan.Update(new LoanDetails(name, loan.LiabilityAccountId, loan.InterestAccountId, loan.Principal, loan.AnnualRatePercent, loan.Payments,
            loan.FirstPaymentDate, loan.PaymentAmount, loan.LenderVendorId, notes, classId is null ? loan.ClassId : classId == Guid.Empty ? null : classId));
        store.Add(Audit(AuditAction.Updated, loan, before));
        await store.SaveChangesAsync(ct);
        return loan;
    }

    /// <summary>Takes back a loan entered by mistake, with the entry recording its money; its payments must be voided first.</summary>
    public async Task<Loan> VoidAsync(Guid id, string reason, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(reason))
            throw new DomainException("void.reason_required", "Say why.");
        var loan = await store.GetLoanAsync(id, ct) ?? throw new EntityNotFoundException(EntityType, id);
        var before = Snapshot(loan);
        loan.Void(clock.UtcNow);
        if (loan.ProceedsEntryId is { } entry)
            await engine.StageVoidAsync(entry, reason.Trim(), fromDocument: true, ct);
        store.Add(Audit(AuditAction.Voided, loan, before));
        await store.SaveChangesAsync(ct);
        return loan;
    }

    public async Task<LoanPayment> PayAsync(Guid id, LoanPaymentCommand command, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        var loan = await store.GetLoanAsync(id, ct) ?? throw new EntityNotFoundException(EntityType, id);
        var from = await accounts.CheckAsync(command.PaymentAccountId, "account paid from", ct, AccountType.Bank, AccountType.CreditCard);
        if (loan.LatestPayment is { } latest && command.Date < latest.Date)
            throw new DomainException("loan.payment_order", "Payments are recorded in date order; this one is before the last.");
        var (interest, principal) = loan.Split(command.Amount ?? loan.NextPayment);

        var lines = new List<JournalLineDraft>();
        if (principal > 0m)
            lines.Add(JournalLineDraft.Dr(loan.LiabilityAccountId, principal, "Principal"));
        if (interest > 0m)
            lines.Add(JournalLineDraft.Dr(loan.InterestAccountId, interest, "Interest", loan.ClassId));
        lines.Add(JournalLineDraft.Cr(from.Id, interest + principal));

        var before = Snapshot(loan);
        var paymentId = Guid.CreateVersion7();
        var entry = await engine.StageAsync(new PostJournalEntryCommand(command.Date, lines, $"Loan payment · {loan.Name}", Loan.PaymentSource, loan.Id), ct);
        var payment = LoanPayment.Create(paymentId, command.Date, interest, principal, from.Id, entry.Id);
        loan.AddPayment(payment);
        store.Add(Audit(AuditAction.Updated, loan, before));
        await store.SaveChangesAsync(ct);
        return payment;
    }

    /// <summary>Draws money on a line of credit into a bank account: Dr the bank / Cr the loan.</summary>
    public async Task<LoanDraw> DrawAsync(Guid id, DateOnly date, decimal amount, Guid bankAccountId, CancellationToken ct = default)
    {
        var loan = await store.GetLoanAsync(id, ct) ?? throw new EntityNotFoundException(EntityType, id);
        var bank = await accounts.CheckAsync(bankAccountId, "account it went into", ct, AccountType.Bank, AccountType.OtherCurrentAsset);
        var before = Snapshot(loan);
        loan.CheckDraw(amount);
        var drawId = Guid.CreateVersion7();
        var entry = await engine.StageAsync(new PostJournalEntryCommand(date,
            [JournalLineDraft.Dr(bank.Id, amount, "Drawn"), JournalLineDraft.Cr(loan.LiabilityAccountId, amount, "Drawn")],
            $"Drawn on {loan.Name}", Loan.DrawSource, loan.Id), ct);
        var draw = loan.AddDraw(drawId, date, amount, bank.Id, entry.Id);
        store.Add(Audit(AuditAction.Updated, loan, before));
        await store.SaveChangesAsync(ct);
        return draw;
    }

    public async Task<Loan> VoidDrawAsync(Guid id, Guid drawId, string reason, CancellationToken ct = default)
    {
        var loan = await store.GetLoanAsync(id, ct) ?? throw new EntityNotFoundException(EntityType, id);
        var before = Snapshot(loan);
        var draw = loan.VoidDraw(drawId);
        await engine.StageVoidAsync(draw.EntryId, reason, fromDocument: true, ct);
        store.Add(Audit(AuditAction.Updated, loan, before));
        await store.SaveChangesAsync(ct);
        return loan;
    }

    public async Task<Loan> VoidPaymentAsync(Guid id, Guid paymentId, string reason, CancellationToken ct = default)
    {
        var loan = await store.GetLoanAsync(id, ct) ?? throw new EntityNotFoundException(EntityType, id);
        var payment = loan.PaymentsMade.FirstOrDefault(p => p.Id == paymentId) ?? throw new EntityNotFoundException("loan_payment", paymentId);
        if (payment.IsVoided)
            throw new DomainException("loan.payment_voided", "This payment is already voided.");
        if (loan.LatestPayment?.Id != payment.Id)
            throw new DomainException("loan.payment_not_latest", "Only the latest payment can be voided, since each payment's interest depends on the ones before.");
        var before = Snapshot(loan);
        await engine.StageVoidAsync(payment.JournalEntryId, reason, fromDocument: true, ct);
        payment.Void();
        store.Add(Audit(AuditAction.Updated, loan, before));
        await store.SaveChangesAsync(ct);
        return loan;
    }

    private async Task<LoanDetails> CheckAccountsAsync(LoanDetails details, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(details);
        await accounts.CheckAsync(details.LiabilityAccountId, "loan account", ct, AccountType.LongTermLiability, AccountType.OtherCurrentLiability);
        var interest = details.InterestAccountId == Guid.Empty
            ? (await accounts.InterestAsync(ct)).Id
            : (await accounts.CheckAsync(details.InterestAccountId, "interest account", ct, AccountType.Expense, AccountType.OtherExpense)).Id;
        return details with { InterestAccountId = interest };
    }

    private AuditRecord Audit(AuditAction action, Loan l, string? before) =>
        AuditRecord.Create(clock.UtcNow, user.Actor, action, EntityType, l.Id, before, Snapshot(l));

    private static string Snapshot(Loan l) => JsonSerializer.Serialize(new
    {
        l.Name,
        l.ClassId,
        l.LiabilityAccountId,
        l.InterestAccountId,
        l.Principal,
        l.AnnualRatePercent,
        l.Payments,
        l.FirstPaymentDate,
        l.PaymentAmount,
        l.LenderVendorId,
        l.Balance,
        PaymentsMade = l.PaymentsMade.Select(p => new { p.Id, p.Date, p.Amount, p.Interest, p.Principal, p.IsVoided }),
    });
}
