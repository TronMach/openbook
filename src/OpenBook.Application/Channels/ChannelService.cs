using System.Text;
using System.Text.Json;
using OpenBook.Application.Accounts;
using OpenBook.Application.Ledger;
using OpenBook.Domain.Accounts;
using OpenBook.Domain.Auditing;
using OpenBook.Domain.Channels;
using OpenBook.Domain.Common;

namespace OpenBook.Application.Channels;

public interface IChannelStore
{
    Task<IReadOnlyList<SalesChannel>> ListChannelsAsync(CancellationToken ct);
    Task<SalesChannel?> GetChannelAsync(Guid id, CancellationToken ct);

    /// <summary>Newest first; every channel's when <paramref name="channelId"/> is null.</summary>
    Task<IReadOnlyList<ChannelPayout>> ListPayoutsAsync(Guid? channelId, CancellationToken ct);

    Task<ChannelPayout?> GetPayoutAsync(Guid id, CancellationToken ct);
    Task<IReadOnlySet<string>> PayoutIdsAsync(Guid channelId, CancellationToken ct);

    void Add(SalesChannel channel);
    void Add(ChannelPayout payout);
    void Remove(ChannelPayout payout);
    void Add(AuditRecord record);
    Task SaveChangesAsync(CancellationToken ct);
}

/// <summary>What an import found: payouts to add, and ones already imported (skipped).</summary>
public sealed record PayoutImportResult(IReadOnlyList<PayoutData> New, IReadOnlyList<PayoutData> AlreadyImported, IReadOnlyList<string> Warnings, IReadOnlyList<ChannelPayout> Saved)
{
    public bool CanImport => New.Count > 0;
}

/// <summary>
/// Sales channels and their payouts. A report is read by <see cref="ChannelReports"/>; payouts not seen before (by the
/// channel's id for them) wait as Pending until posted, each as one journal entry (<see cref="ChannelPayout.Source"/>).
/// </summary>
public sealed partial class ChannelService(IChannelStore store, IChannelOrderStore orders, AccountService accounts, PostingEngine engine, Sales.ISalesStore sales, IClock clock, ICurrentUser user, OpenBook.Application.Companies.ICompanyStore companies)
{
    public const string ChannelEntity = "sales_channel";
    public const string PayoutEntity = "channel_payout";

    public const string SalesName = "Online Sales";
    public const string ShippingName = "Shipping Income";
    public const string RefundsName = "Refunds and Returns";
    public const string FeesName = "Merchant and Marketplace Fees";
    public const string TaxName = "Sales Tax Payable";
    public const string AdjustmentsName = "Marketplace Adjustments";

    public async Task<IReadOnlyList<SalesChannel>> ListAsync(bool includeInactive = false, CancellationToken ct = default) =>
        (await store.ListChannelsAsync(ct)).Where(c => includeInactive || c.IsActive).OrderBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase).ToList();

    public Task<SalesChannel?> GetAsync(Guid id, CancellationToken ct = default) => store.GetChannelAsync(id, ct);

    public Task<IReadOnlyList<ChannelPayout>> PayoutsAsync(Guid? channelId = null, CancellationToken ct = default) => store.ListPayoutsAsync(channelId, ct);

    public Task<ChannelPayout?> GetPayoutAsync(Guid id, CancellationToken ct = default) => store.GetPayoutAsync(id, ct);

    public async Task<SalesChannel> CreateAsync(ChannelDetails details, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(details);
        var channel = SalesChannel.Create(await ResolveAsync(details, ct), clock.UtcNow);
        await EnsureUniqueAsync(channel, ct);
        store.Add(channel);
        store.Add(Audit(AuditAction.Created, ChannelEntity, channel.Id, null, Snapshot(channel)));
        await store.SaveChangesAsync(ct);
        return channel;
    }

    public async Task<SalesChannel> UpdateAsync(Guid id, ChannelDetails details, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(details);
        var channel = await store.GetChannelAsync(id, ct) ?? throw new EntityNotFoundException(ChannelEntity, id);
        var before = Snapshot(channel);
        channel.Update(await ResolveAsync(details, ct));
        await EnsureUniqueAsync(channel, ct);
        store.Add(Audit(AuditAction.Updated, ChannelEntity, channel.Id, before, Snapshot(channel)));
        await store.SaveChangesAsync(ct);
        return channel;
    }

    public async Task<SalesChannel> SetActiveAsync(Guid id, bool active, CancellationToken ct = default)
    {
        var channel = await store.GetChannelAsync(id, ct) ?? throw new EntityNotFoundException(ChannelEntity, id);
        var before = Snapshot(channel);
        if (active)
            channel.Reactivate();
        else
            channel.Deactivate();
        store.Add(Audit(AuditAction.Updated, ChannelEntity, channel.Id, before, Snapshot(channel)));
        await store.SaveChangesAsync(ct);
        return channel;
    }

    /// <summary>Reads a report and says what's new; with <paramref name="save"/> the new payouts are saved as Pending.</summary>
    public async Task<PayoutImportResult> ImportAsync(Guid channelId, string fileName, byte[] content, bool save, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        var channel = await store.GetChannelAsync(channelId, ct) ?? throw new EntityNotFoundException(ChannelEntity, channelId);
        if (!channel.IsActive)
            throw new DomainException("channel.inactive", $"{channel.Name} is inactive.");
        var text = OpenBook.Application.Import.TextFiles.Decode(content);
        ChannelCurrency.Check(text, (await OpenBook.Application.Companies.CompanyStoreExtensions.GetRequiredCompanyAsync(companies, ct)).HomeCurrency.Code);
        var read = ChannelReports.Read(channel.Kind, text, fileName);
        var warnings = channel.Kind == ChannelKind.Stripe
            ? (IReadOnlyList<string>)[.. read.Warnings, "Charges for invoices paid online are recognized by their payment reference, which a downloaded file may not have. Check the sales in this payout before posting, or connect the channel."]
            : read.Warnings;
        return await AddAsync(channel, await SplitByOrdersAsync(channel, read.Payouts, ct), warnings, save, ct);
    }

    /// <summary>A Shopify payout file has charges gross: the order reports already imported say what's tax and shipping.</summary>
    private async Task<IReadOnlyList<PayoutData>> SplitByOrdersAsync(SalesChannel channel, IReadOnlyList<PayoutData> payouts, CancellationToken ct)
    {
        if (channel.Kind != ChannelKind.Shopify)
            return payouts;
        var held = (await orders.ListBatchesAsync(channel.Id, ct)).Where(b => b.Status != PayoutStatus.Dismissed).SelectMany(b => b.Lines)
            .Select(l => new ChannelOrderDraft(l.OrderId, l.Date, l.Sku, l.Title, l.Quantity, l.Sales, l.Shipping, l.Tax, l.Region)).ToList();
        return ChannelSyncService.SplitCharges(payouts, held);
    }

    /// <summary>
    /// Lines of a file with no payout ids that are already held: such a file's id comes from its dates and total, so an
    /// overlapping one looks new. Each line held answers for one incoming, so identical lines in a file both come in once.
    /// </summary>
    private async Task<(IReadOnlyList<PayoutData> Payouts, int Left)> WithoutHeldLinesAsync(Guid channelId, IReadOnlyList<PayoutData> payouts, CancellationToken ct)
    {
        if (!payouts.Any(p => DerivedId().IsMatch(p.ExternalId)))
            return (payouts, 0);
        var held = (await store.ListPayoutsAsync(channelId, ct)).Where(p => p.Status != PayoutStatus.Dismissed).SelectMany(p => p.Lines)
            .GroupBy(l => (l.Date, l.Type, l.Amount, l.Reference)).ToDictionary(g => g.Key, g => g.Count());
        var left = 0;
        var kept = new List<PayoutData>();
        foreach (var payout in payouts)
        {
            if (!DerivedId().IsMatch(payout.ExternalId))
            {
                kept.Add(payout);
                continue;
            }

            var lines = new List<ChannelLineDraft>();
            foreach (var line in payout.Lines)
            {
                var key = (line.Date, line.Type, line.Amount, line.Reference);
                if (held.GetValueOrDefault(key) > 0)
                {
                    held[key]--;
                    left++;
                }
                else
                {
                    lines.Add(line);
                }
            }

            if (lines.Count > 0)
                kept.Add(payout with { Lines = lines });
        }

        return (kept, left);
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"^\w+:\d{8}-\d{8}:")]
    private static partial System.Text.RegularExpressions.Regex DerivedId();

    /// <summary>Records a connection change on the channel (the credential itself never goes in the audit log).</summary>
    internal async Task SaveConnectionAsync(SalesChannel channel, string before, CancellationToken ct)
    {
        store.Add(Audit(AuditAction.Updated, ChannelEntity, channel.Id, before, Snapshot(channel)));
        await store.SaveChangesAsync(ct);
    }

    internal static string SnapshotOf(SalesChannel channel) => Snapshot(channel);

    /// <summary>Saves payouts not seen before (live connections use this too).</summary>
    internal async Task<PayoutImportResult> AddAsync(SalesChannel channel, IReadOnlyList<PayoutData> payouts, IReadOnlyList<string> warnings, bool save, CancellationToken ct)
    {
        var known = await store.PayoutIdsAsync(channel.Id, ct);
        var fresh = payouts.Where(p => !known.Contains(p.ExternalId)).GroupBy(p => p.ExternalId).Select(g => g.First()).ToList();
        var old = payouts.Where(p => known.Contains(p.ExternalId)).ToList();
        (var unheld, var heldLines) = await WithoutHeldLinesAsync(channel.Id, fresh, ct);
        fresh = [.. unheld];
        if (heldLines > 0)
            warnings = [.. warnings, $"{heldLines} line(s) of this file were imported before (same date, type, amount and reference), so they're left out."];
        if (!save || fresh.Count == 0)
            return new PayoutImportResult(fresh, old, warnings, []);

        // A dismissed payout never posted: a fresh import of it takes its place.
        var ids = fresh.Select(p => p.ExternalId).ToHashSet(StringComparer.Ordinal);
        var replaced = (await store.ListPayoutsAsync(channel.Id, ct)).Where(p => p.Status == PayoutStatus.Dismissed && ids.Contains(p.ExternalId)).ToList();
        foreach (var old2 in replaced)
        {
            store.Remove(old2);
            store.Add(Audit(AuditAction.Deleted, PayoutEntity, old2.Id, PayoutSnapshot(old2, channel), null));
        }

        if (replaced.Count > 0)
            await store.SaveChangesAsync(ct);

        var saved = new List<ChannelPayout>();
        foreach (var p in fresh)
        {
            var payout = ChannelPayout.Import(channel.Id, p.ExternalId, p.Date, p.Lines, clock.UtcNow, p.PeriodStart, p.PeriodEnd, p.ReportedNet);
            store.Add(payout);
            store.Add(Audit(AuditAction.Created, PayoutEntity, payout.Id, null, PayoutSnapshot(payout, channel)));
            saved.Add(payout);
        }

        await store.SaveChangesAsync(ct);
        return new PayoutImportResult(fresh, old, warnings, saved);
    }

    /// <summary>Posts the payout as one journal entry on its date (closed periods apply).</summary>
    public async Task<ChannelPayout> PostAsync(Guid payoutId, CancellationToken ct = default)
    {
        var payout = await store.GetPayoutAsync(payoutId, ct) ?? throw new EntityNotFoundException(PayoutEntity, payoutId);
        var channel = await store.GetChannelAsync(payout.ChannelId, ct) ?? throw new EntityNotFoundException(ChannelEntity, payout.ChannelId);
        if (payout.Status != PayoutStatus.Pending)
            throw new DomainException("channel.payout_not_pending", "Only a payout waiting to be posted can be posted.");
        if (payout.Difference != 0m)
            throw new DomainException("channel.payout_unbalanced",
                $"The transactions add up to {payout.Net:N2}, but the channel reported {payout.ReportedNet:N2}. Check the report before posting.");
        var before = PayoutSnapshot(payout, channel);
        var recorded = await RecordedChargesAsync(channel, payout, ct);
        var postings = payout.Postings(channel);
        if (postings.Count < 2)
            throw new DomainException("channel.payout_zero", "Everything in this payout nets to zero; there's nothing to post. Dismiss it instead.");
        var entry = await engine.StageAsync(new PostJournalEntryCommand(payout.Date, postings, $"{channel.Name} payout {payout.ExternalId}", ChannelPayout.Source, payout.Id), ct);
        payout.Posted(entry.Id);
        foreach (var payment in recorded)
        {
            payment.SettleByPayout(payout.Id);
            store.Add(AuditRecord.Create(clock.UtcNow, user.Actor, AuditAction.Updated, "payment", payment.Id, null, JsonSerializer.Serialize(new { Payout = payout.ExternalId })));
        }

        store.Add(Audit(AuditAction.Updated, PayoutEntity, payout.Id, before, PayoutSnapshot(payout, channel)));
        await store.SaveChangesAsync(ct);
        return payout;
    }

    /// <summary>
    /// Stripe charges for payments recorded by now, marked here and not at sync, since a payment can be recorded after its
    /// payout is fetched. The money reaches the books through the payment: as a sale too, it would count twice.
    /// </summary>
    private async Task<IReadOnlyList<Domain.Sales.CustomerPayment>> RecordedChargesAsync(SalesChannel channel, ChannelPayout payout, CancellationToken ct)
    {
        if (channel.Kind != ChannelKind.Stripe)
            return [];
        var references = payout.Lines.Where(l => l.Type is ChannelLineType.Sale or ChannelLineType.Cleared).Select(l => l.Reference).OfType<string>().Distinct().ToList();
        if (references.Count == 0)
            return [];
        var payments = (await sales.ListPaymentsByReferenceAsync(references, ct))
            .Where(p => p.Status == DocumentStatus.Posted && p.DepositId is null && p.PayoutId is null).ToList();
        if (payments.Count == 0)
            return [];
        if (channel.ClearingAccountId is null)
        {
            if ((await sales.GetOnlinePaymentSettingsAsync(ct))?.DepositAccountId is not { } clearing)
                return [];
            channel.UseClearingAccount(clearing);
        }

        payout.ClearCharges(payments.Select(p => p.Reference!).ToHashSet(StringComparer.Ordinal));
        return payments;
    }

    /// <summary>Voids the payout's entry (a reversal on the same date); the payout waits to be posted again.</summary>
    public async Task<ChannelPayout> VoidAsync(Guid payoutId, string reason, CancellationToken ct = default)
    {
        var payout = await store.GetPayoutAsync(payoutId, ct) ?? throw new EntityNotFoundException(PayoutEntity, payoutId);
        var channel = await store.GetChannelAsync(payout.ChannelId, ct) ?? throw new EntityNotFoundException(ChannelEntity, payout.ChannelId);
        if (payout.JournalEntryId is not { } entryId)
            throw new DomainException("channel.payout_not_posted", "That payout isn't posted.");
        var before = PayoutSnapshot(payout, channel);
        await engine.StageVoidAsync(entryId, reason, fromDocument: true, ct);
        payout.Unpost();
        foreach (var payment in await sales.ListPaymentsInPayoutAsync(payout.Id, ct))
            payment.ClearPayout();
        store.Add(Audit(AuditAction.Voided, PayoutEntity, payout.Id, before, PayoutSnapshot(payout, channel)));
        await store.SaveChangesAsync(ct);
        return payout;
    }

    public async Task<ChannelPayout> SetDismissedAsync(Guid payoutId, bool dismissed, CancellationToken ct = default)
    {
        var payout = await store.GetPayoutAsync(payoutId, ct) ?? throw new EntityNotFoundException(PayoutEntity, payoutId);
        var channel = await store.GetChannelAsync(payout.ChannelId, ct) ?? throw new EntityNotFoundException(ChannelEntity, payout.ChannelId);
        var before = PayoutSnapshot(payout, channel);
        if (dismissed)
            payout.Dismiss();
        else
            payout.Restore();
        store.Add(Audit(AuditAction.Updated, PayoutEntity, payout.Id, before, PayoutSnapshot(payout, channel)));
        await store.SaveChangesAsync(ct);
        return payout;
    }

    /// <summary>Empty account ids become the usual accounts (created on first use); chosen ones are checked.</summary>
    private async Task<ChannelDetails> ResolveAsync(ChannelDetails d, CancellationToken ct)
    {
        var chart = await accounts.GetChartAsync(ct);

        async Task<Guid> Pick(Guid id, string name, AccountType type, string description, string role, params AccountType[] allowed)
        {
            if (id != Guid.Empty)
            {
                var account = chart.Find(id) ?? throw new EntityNotFoundException("account", id);
                if (!account.IsActive)
                    throw new DomainException("channel.account_inactive", $"'{account.Name}' is inactive.");
                if (account.Type is AccountType.AccountsReceivable or AccountType.AccountsPayable || allowed.Length > 0 && !allowed.Contains(account.Type))
                    throw new DomainException("channel.account_type", $"'{account.Name}' can't take the {role}.");
                return id;
            }

            var existing = chart.Ordered(includeInactive: false).Select(x => x.Account)
                .FirstOrDefault(a => a.Type == type && string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase));
            if (existing is not null)
                return existing.Id;
            var created = await accounts.CreateAsync(new CreateAccountCommand(name, type, Description: description), ct);
            chart = await accounts.GetChartAsync(ct);
            return created.Id;
        }

        if (d.DepositAccountId == Guid.Empty)
            throw new DomainException("channel.deposit_required", "Choose the account payouts are paid into.");

        // Stripe payouts can include invoices paid online, already sitting in the online payments deposit account.
        var clearing = d.ClearingAccountId is { } chosen && chosen != Guid.Empty
            ? await Pick(chosen, "", AccountType.OtherCurrentAsset, "", "invoice payments", AccountType.Bank, AccountType.OtherCurrentAsset)
            : d.Kind == ChannelKind.Stripe ? (await sales.GetOnlinePaymentSettingsAsync(ct))?.DepositAccountId : null;
        return d with
        {
            DepositAccountId = await Pick(d.DepositAccountId, "", AccountType.Bank, "", "payouts", AccountType.Bank, AccountType.OtherCurrentAsset),
            SalesAccountId = await Pick(d.SalesAccountId, SalesName, AccountType.Income, "Sales through online channels.", "sales"),
            ShippingAccountId = await Pick(d.ShippingAccountId, ShippingName, AccountType.Income, "Shipping charged to customers.", "shipping"),
            RefundsAccountId = await Pick(d.RefundsAccountId, RefundsName, AccountType.Income, "Refunds given (reduces income).", "refunds"),
            FeesAccountId = await Pick(d.FeesAccountId, FeesName, AccountType.Expense, "Payment processing and marketplace fees.", "fees"),
            TaxAccountId = await Pick(d.TaxAccountId, TaxName, AccountType.OtherCurrentLiability, "Sales tax collected and not yet paid.", "sales tax"),
            AdjustmentsAccountId = await Pick(d.AdjustmentsAccountId, AdjustmentsName, AccountType.Expense, "Reserves, reimbursements and other channel adjustments.", "adjustments"),
            ClearingAccountId = clearing,
        };
    }

    private async Task EnsureUniqueAsync(SalesChannel channel, CancellationToken ct)
    {
        if ((await store.ListChannelsAsync(ct)).Any(c => c.Id != channel.Id && string.Equals(c.Name, channel.Name, StringComparison.OrdinalIgnoreCase)))
            throw new DomainException("channel.name_taken", $"There's already a channel called {channel.Name}.");
    }

    private AuditRecord Audit(AuditAction action, string type, Guid id, string? before, string? after) =>
        AuditRecord.Create(clock.UtcNow, user.Actor, action, type, id, before, after);

    private static string Snapshot(SalesChannel c) => JsonSerializer.Serialize(new
    {
        c.Name,
        Kind = c.Kind.ToString(),
        c.DepositAccountId,
        c.SalesAccountId,
        c.ShippingAccountId,
        c.RefundsAccountId,
        c.FeesAccountId,
        c.TaxAccountId,
        c.AdjustmentsAccountId,
        c.ClearingAccountId,
        c.IsActive,
        Connected = c.IsConnected,
        c.ShopDomain,
    });

    private static string PayoutSnapshot(ChannelPayout p, SalesChannel c) => JsonSerializer.Serialize(new
    {
        Channel = c.Name,
        p.ExternalId,
        p.Date,
        Status = p.Status.ToString(),
        p.Net,
        p.ReportedNet,
        Lines = p.Lines.Count,
        p.JournalEntryId,
    });
}

/// <summary>Posted payouts over a period, by channel: what each part came to (dated by the payout).</summary>
public sealed class ChannelSalesReport(ChannelService channels, OpenBook.Application.Companies.ICompanyStore companies)
{
    public async Task<OpenBook.Application.Reporting.ReportTable> TableAsync(DateOnly from, DateOnly to, CancellationToken ct = default)
    {
        var company = await OpenBook.Application.Companies.CompanyStoreExtensions.GetRequiredCompanyAsync(companies, ct);
        var payouts = (await channels.PayoutsAsync(null, ct)).Where(p => p.Status == PayoutStatus.Posted && p.Date >= from && p.Date <= to).ToList();
        var rows = new List<OpenBook.Application.Reporting.ReportTableRow>();
        var types = new[] { ChannelLineType.Sale, ChannelLineType.Shipping, ChannelLineType.Refund, ChannelLineType.Fee, ChannelLineType.Tax, ChannelLineType.MarketplaceTax, ChannelLineType.Adjustment };

        OpenBook.Application.Reporting.ReportTableRow Row(OpenBook.Application.Reporting.ReportCell name, IReadOnlyCollection<ChannelPayout> ps, OpenBook.Application.Reporting.ReportRowStyle style) =>
            new([name, new OpenBook.Application.Reporting.ReportCell((decimal)ps.Count), .. types.Select(t => (OpenBook.Application.Reporting.ReportCell)ps.Sum(p => p.Total(t))), ps.Sum(p => p.Net)], 0, style);

        foreach (var channel in await channels.ListAsync(includeInactive: true, ct))
        {
            var mine = payouts.Where(p => p.ChannelId == channel.Id).ToList();
            if (mine.Count > 0)
                rows.Add(Row(new OpenBook.Application.Reporting.ReportCell(channel.Name, new OpenBook.Application.Reporting.ReportLink("sales_channel", channel.Id)), mine,
                    OpenBook.Application.Reporting.ReportRowStyle.Normal));
        }

        rows.Add(Row("Total", payouts, OpenBook.Application.Reporting.ReportRowStyle.Total));
        return new OpenBook.Application.Reporting.ReportTable(
            "Sales by channel",
            [company.Name, OpenBook.Application.Reporting.Comparisons.Describe(from, to)],
            [
                new("Channel"), new("Payouts", OpenBook.Application.Reporting.ColumnFormat.Number), new("Sales", OpenBook.Application.Reporting.ColumnFormat.Money),
                new("Shipping", OpenBook.Application.Reporting.ColumnFormat.Money), new("Refunds", OpenBook.Application.Reporting.ColumnFormat.Money),
                new("Fees", OpenBook.Application.Reporting.ColumnFormat.Money), new("Sales tax", OpenBook.Application.Reporting.ColumnFormat.Money),
                new("Tax paid by marketplace", OpenBook.Application.Reporting.ColumnFormat.Money), new("Adjustments", OpenBook.Application.Reporting.ColumnFormat.Money),
                new("Net paid", OpenBook.Application.Reporting.ColumnFormat.Money),
            ],
            rows,
            Note: "Posted payouts dated in the period. Refunds, fees and marketplace-paid tax are negative. Payouts waiting to be posted aren't counted.");
    }
}
