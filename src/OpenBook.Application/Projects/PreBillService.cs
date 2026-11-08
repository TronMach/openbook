using System.Text.Json;
using OpenBook.Application.Forms;
using OpenBook.Application.Sales;
using OpenBook.Domain.Auditing;
using OpenBook.Domain.Common;
using OpenBook.Domain.Projects;
using OpenBook.Domain.Sales;

namespace OpenBook.Application.Projects;

/// <summary>A client's unbilled work on the billing worksheet.</summary>
/// <param name="Warning">Over a project's not-to-exceed amount, say.</param>
public sealed record BillingRow(Customer Client, decimal Hours, decimal Time, decimal Mileage, decimal Expenses, DateOnly Oldest, string? Warning)
{
    public decimal Total => Time + Mileage + Expenses;
}

/// <summary>What a batch post did.</summary>
public sealed record BillingRun(int Posted, int Emailed, IReadOnlyList<string> Problems);

/// <summary>What's being changed on a draft bill's line.</summary>
public sealed record PreBillLineChange(int LineNumber, decimal Billed, string? Reason, bool Remove = false);

/// <summary>
/// The billing run: a worksheet of who has unbilled work through a date, draft bills to review, and posting them. A
/// client takes the work of sub-customers billed with them. Work on a draft stays out of other drafts, and until
/// it's posted it can still be ticked on an ordinary invoice.
/// </summary>
public sealed class PreBillService(
    IProjectStore store,
    ISalesStore sales,
    TimeService time,
    InvoiceService invoices,
    EmailService email,
    IUnitOfWork unitOfWork,
    IClock clock,
    ICurrentUser user)
{
    public const string EntityType = "pre_bill";

    public Task<IReadOnlyList<PreBill>> DraftsAsync(CancellationToken ct = default) => store.ListPreBillsAsync(PreBillStatus.Draft, ct);

    public Task<PreBill?> GetAsync(Guid id, CancellationToken ct = default) => store.GetPreBillAsync(id, ct);

    private sealed record Piece(Guid Client, PreBillLineDraft Line, Guid Owner);

    private async Task<IReadOnlyList<Piece>> UnbilledAsync(DateOnly through, CancellationToken ct)
    {
        var customers = (await sales.ListCustomersAsync(ct)).ToDictionary(c => c.Id);
        var held = (await store.ListPreBillsAsync(PreBillStatus.Draft, ct)).SelectMany(b => b.Lines).Select(l => l.SourceId).ToHashSet();
        var workers = (await store.ListWorkersAsync(ct)).ToDictionary(w => w.Id);
        var fixedFee = (await store.ListProjectsAsync(ct)).Where(p => p.Arrangement == BillingArrangement.FixedFee).Select(p => p.CustomerId).ToHashSet();
        var pieces = new List<Piece>();
        void Add(Guid owner, PreBillLineDraft line)
        {
            if (!held.Contains(line.SourceId) && customers.ContainsKey(owner))
                pieces.Add(new Piece(CustomerTree.BillTo(owner, customers), line, owner));
        }

        foreach (var t in (await store.ListTimeAsync(new TimeFilter(To: through), ct)).Where(t => t.Billable && !t.IsRunning && t.InvoiceId is null && !t.WrittenOff && t.CustomerId is { } c && !fixedFee.Contains(c)))
            Add(t.CustomerId!.Value, new PreBillLineDraft(PreBillLineKind.Time, t.Id, t.Date, $"{workers.GetValueOrDefault(t.WorkerId)?.Name}{(t.Notes is { } n ? ": " + n : "")}", t.Hours, t.Amount));
        foreach (var m in (await store.ListMileageAsync(null, through, null, ct)).Where(m => m.Billable && m.InvoiceId is null && m.CustomerId is not null))
            Add(m.CustomerId!.Value, new PreBillLineDraft(PreBillLineKind.Mileage, m.Id, m.Date, $"{m.Miles:0.#} miles: {m.Purpose}", 0m, m.Amount));
        foreach (var e in (await sales.ListBillableExpensesAsync(null, ct)).Where(e => e.IsBillable && e.InvoiceId is null && e.Date <= through))
            Add(e.CustomerId, new PreBillLineDraft(PreBillLineKind.Expense, e.Id, e.Date, e.Description, 0m, e.Amount));
        return pieces;
    }

    /// <summary>Everyone with unbilled work through the date, not counting what's on a draft already.</summary>
    public async Task<IReadOnlyList<BillingRow>> WorksheetAsync(DateOnly through, CancellationToken ct = default)
    {
        var customers = (await sales.ListCustomersAsync(ct)).ToDictionary(c => c.Id);
        var capped = (await store.ListProjectsAsync(ct)).Where(p => p.Arrangement == BillingArrangement.NotToExceed && p.Fee is not null).ToDictionary(p => p.CustomerId);
        var rows = new List<BillingRow>();
        foreach (var group in (await UnbilledAsync(through, ct)).GroupBy(p => p.Client))
        {
            string? warning = null;
            foreach (var project in group.GroupBy(p => p.Owner).Where(g => capped.ContainsKey(g.Key)))
            {
                var limit = capped[project.Key].Fee!.Value;
                var billed = (await store.ListTimeAsync(new TimeFilter(CustomerId: project.Key), ct)).Where(t => t.InvoiceId is not null).Sum(t => t.Amount);
                var after = billed + project.Sum(p => p.Line.Worked);
                if (after > limit)
                    warning = $"{customers[project.Key].DisplayName} would go {after - limit:N2} over its limit of {limit:N2}.";
            }

            decimal Of(PreBillLineKind kind) => group.Where(p => p.Line.Kind == kind).Sum(p => p.Line.Worked);
            rows.Add(new BillingRow(customers[group.Key], group.Sum(p => p.Line.Hours), Of(PreBillLineKind.Time), Of(PreBillLineKind.Mileage), Of(PreBillLineKind.Expense), group.Min(p => p.Line.Date), warning));
        }

        return rows.OrderBy(r => r.Client.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    /// <summary>A draft for each client chosen, one transaction.</summary>
    public async Task<IReadOnlyList<PreBill>> MakeDraftsAsync(IReadOnlyCollection<Guid> clientIds, DateOnly through, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(clientIds);
        var pieces = (await UnbilledAsync(through, ct)).ToLookup(p => p.Client);
        var drafts = new List<PreBill>();
        foreach (var client in clientIds.Distinct())
        {
            var draft = PreBill.Create(client, through, pieces[client].OrderBy(p => p.Line.Date).ThenBy(p => p.Line.Kind).Select(p => p.Line), clock.UtcNow, user.Actor);
            store.Add(draft);
            store.Add(Audit(AuditAction.Created, draft, null));
            drafts.Add(draft);
        }

        await store.SaveChangesAsync(ct);
        return drafts;
    }

    public async Task<PreBill> SaveAsync(Guid id, string? memo, IReadOnlyList<PreBillLineChange> changes, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(changes);
        var draft = await RequiredAsync(id, ct);
        var before = Snapshot(draft);
        draft.SetMemo(memo);
        foreach (var change in changes)
        {
            if (change.Remove)
                draft.Remove(change.LineNumber);
            else
                draft.Bill(change.LineNumber, change.Billed, change.Reason);
        }

        store.Add(AuditRecord.Create(clock.UtcNow, user.Actor, AuditAction.Updated, EntityType, id, before, Snapshot(draft)));
        await store.SaveChangesAsync(ct);
        return draft;
    }

    public async Task DiscardAsync(Guid id, CancellationToken ct = default)
    {
        var draft = await RequiredAsync(id, ct);
        var before = Snapshot(draft);
        draft.Discard();
        store.Add(AuditRecord.Create(clock.UtcNow, user.Actor, AuditAction.Updated, EntityType, id, before, Snapshot(draft)));
        await store.SaveChangesAsync(ct);
    }

    private static string WriteDown(decimal? billed, string? reason) => JsonSerializer.Serialize(new { BilledAmount = billed, WriteDownReason = reason });

    /// <summary>
    /// Writes the draft's amounts onto its time and makes the invoice, in one transaction. A line billed at nothing is
    /// written off: the time leaves the unbilled list with its reason, and there's no invoice if that's all there was.
    /// </summary>
    /// <returns>The invoice, or null when everything was written off.</returns>
    public async Task<Invoice?> PostAsync(Guid id, DateOnly date, CancellationToken ct = default)
    {
        var draft = await RequiredAsync(id, ct);
        if (draft.Status != PreBillStatus.Draft)
            throw new DomainException("prebill.not_draft", "This draft has already been posted or discarded.");
        await using var transaction = await unitOfWork.BeginAsync(ct);
        var before = Snapshot(draft);
        var timeLines = draft.Lines.Where(l => l.Kind == PreBillLineKind.Time).ToList();
        var entries = (await store.GetTimeEntriesAsync(timeLines.Select(l => l.SourceId).ToList(), ct)).ToDictionary(t => t.Id);
        foreach (var line in timeLines.Where(l => entries.ContainsKey(l.SourceId) && l.Billed != l.Worked))
            await time.AdjustAsync(entries[line.SourceId], line.Billed, line.Reason ?? string.Empty, date, ct);

        // Mileage and costs are written up or down the same way; nothing billed writes them off.
        var tripLines = draft.Lines.Where(l => l.Kind == PreBillLineKind.Mileage && l.Billed != l.Worked).ToList();
        foreach (var trip in await store.GetMileageTripsAsync([.. tripLines.Select(l => l.SourceId)], ct))
        {
            var line = tripLines.First(l => l.SourceId == trip.Id);
            var tripBefore = WriteDown(trip.BilledAmount, trip.WriteDownReason);
            trip.Adjust(line.Billed, line.Reason ?? string.Empty, date, user.Actor);
            store.Add(AuditRecord.Create(clock.UtcNow, user.Actor, AuditAction.Updated, MileageService.EntityType, trip.Id, tripBefore, WriteDown(trip.BilledAmount, trip.WriteDownReason)));
        }

        var costLines = draft.Lines.Where(l => l.Kind == PreBillLineKind.Expense && l.Billed != l.Worked).ToList();
        foreach (var cost in await sales.GetBillableExpensesAsync([.. costLines.Select(l => l.SourceId)], ct))
        {
            var line = costLines.First(l => l.SourceId == cost.Id);
            var costBefore = WriteDown(cost.BilledAmount, cost.WriteDownReason);
            cost.Adjust(line.Billed, line.Reason ?? string.Empty, date, user.Actor);
            store.Add(AuditRecord.Create(clock.UtcNow, user.Actor, AuditAction.Updated, BillableExpenseService.EntityType, cost.Id, costBefore, WriteDown(cost.BilledAmount, cost.WriteDownReason)));
        }

        List<Guid> timeIds = [.. timeLines.Where(l => l.Billed > 0m).Select(l => l.SourceId)];
        List<Guid> tripIds = [.. draft.Lines.Where(l => l.Kind == PreBillLineKind.Mileage && l.Billed > 0m).Select(l => l.SourceId)];
        List<Guid> costIds = [.. draft.Lines.Where(l => l.Kind == PreBillLineKind.Expense && l.Billed > 0m).Select(l => l.SourceId)];
        Invoice? invoice = null;
        if (timeIds.Count + tripIds.Count + costIds.Count > 0)
        {
            invoice = await invoices.CreateAsync(new CreateInvoiceCommand(draft.CustomerId, date, [], Memo: draft.Memo, TaxCodeId: Guid.Empty,
                BillableExpenseIds: costIds, TimeEntryIds: timeIds, MileageTripIds: tripIds), ct);
        }

        draft.Post(invoice?.Id);
        store.Add(AuditRecord.Create(clock.UtcNow, user.Actor, AuditAction.Updated, EntityType, id, before, Snapshot(draft)));
        await store.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return invoice;
    }

    /// <summary>Posts drafts in turn, each on its own, and emails the invoices when asked. A refusal is reported and the rest go on.</summary>
    public async Task<BillingRun> PostManyAsync(IReadOnlyCollection<Guid> ids, DateOnly date, bool send, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(ids);
        var (posted, emailed) = (0, 0);
        var problems = new List<string>();
        foreach (var id in ids.Distinct())
        {
            var client = await store.GetPreBillAsync(id, ct) is { } draft ? await sales.GetCustomerAsync(draft.CustomerId, ct) : null;
            try
            {
                var invoice = await PostAsync(id, date, ct);
                posted++;
                if (send && invoice is not null)
                {
                    await email.SendAsync(new EmailSource(Domain.Email.EmailKind.Invoice, invoice.Id), await email.DraftAsync(new EmailSource(Domain.Email.EmailKind.Invoice, invoice.Id), ct), ct);
                    emailed++;
                }
            }
            catch (DomainException ex)
            {
                problems.Add($"{client?.DisplayName}: {ex.Message}");
            }
        }

        return new BillingRun(posted, emailed, problems);
    }

    private async Task<PreBill> RequiredAsync(Guid id, CancellationToken ct) => await store.GetPreBillAsync(id, ct) ?? throw new EntityNotFoundException(EntityType, id);

    private AuditRecord Audit(AuditAction action, PreBill draft, string? before) =>
        AuditRecord.Create(clock.UtcNow, user.Actor, action, EntityType, draft.Id, before, Snapshot(draft));

    private static string Snapshot(PreBill b) => JsonSerializer.Serialize(new
    {
        b.CustomerId,
        b.Through,
        Status = b.Status.ToString(),
        b.Memo,
        b.InvoiceId,
        b.Worked,
        b.Billed,
        Lines = b.Lines.Count,
    });
}
