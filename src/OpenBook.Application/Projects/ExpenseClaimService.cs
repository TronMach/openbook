using System.Text.Json;
using OpenBook.Application.Accounts;
using OpenBook.Application.Attachments;
using OpenBook.Application.Purchases;
using OpenBook.Application.Sales;
using OpenBook.Domain.Auditing;
using OpenBook.Domain.Common;
using OpenBook.Domain.Projects;
using OpenBook.Domain.Purchases;
using OpenBook.Domain.Sales;

namespace OpenBook.Application.Projects;

/// <summary>A claim with the person it's for and the bill paying it back, if any.</summary>
public sealed record ExpenseClaimView(ExpenseClaim Claim, Worker Worker, Bill? Bill)
{
    /// <summary>Approved, but its bill was voided: it can be changed, returned or approved again.</summary>
    public bool BillVoided => Claim.Status == ExpenseClaimStatus.Approved && Bill?.Status != DocumentStatus.Posted;

    /// <summary>Waiting for someone to approve or return it.</summary>
    public bool Waiting => Claim.Status == ExpenseClaimStatus.Submitted || BillVoided;
}

/// <summary>
/// Staff expense claims (C345): a person hands in what they paid for themselves (receipts attached to the claim); an
/// approver approves it, which makes a bill to the person's vendor record (lines on the claim's accounts and the
/// person's class, billable lines marked to their customers, receipts copied to the bill, already approved), or
/// returns it with a note. The person who handed a claim in, or whose claim it is, can't approve it unless self-approval
/// is on in the purchasing settings.
/// </summary>
public sealed class ExpenseClaimService(
    IProjectStore store, IPurchasesStore purchases, BillService bills, BillableExpenseService billable, AttachmentService attachments, AccountService accounts,
    ISalesStore sales, IUnitOfWork unitOfWork, IClock clock, ICurrentUser user)
{
    public const string EntityType = "expense_claim";

    public async Task<IReadOnlyList<ExpenseClaimView>> ListAsync(Guid? workerId = null, CancellationToken ct = default)
    {
        var claims = await store.ListExpenseClaimsAsync(workerId, ct);
        var workers = (await store.ListWorkersAsync(ct)).ToDictionary(w => w.Id);
        var billIds = claims.Select(c => c.BillId).OfType<Guid>().Distinct().ToList();
        var billsById = billIds.Count == 0 ? [] : (await purchases.GetBillsAsync(billIds, ct)).ToDictionary(b => b.Id);
        return [.. claims.OrderByDescending(c => c.Number)
            .Select(c => new ExpenseClaimView(c, workers[c.WorkerId], c.BillId is { } b ? billsById.GetValueOrDefault(b) : null))];
    }

    public async Task<ExpenseClaimView?> GetAsync(Guid id, CancellationToken ct = default)
    {
        if (await store.GetExpenseClaimAsync(id, ct) is not { } claim)
            return null;
        var worker = await store.GetWorkerAsync(claim.WorkerId, ct) ?? throw new EntityNotFoundException(WorkerService.EntityType, claim.WorkerId);
        return new(claim, worker, claim.BillId is { } b ? await purchases.GetBillAsync(b, ct) : null);
    }

    /// <summary>Hands in a claim for <paramref name="workerId"/>, or for the signed-in person's own worker record.</summary>
    public async Task<ExpenseClaim> SubmitAsync(Guid? workerId, string? memo, IReadOnlyList<ExpenseClaimLineDraft> lines, CancellationToken ct = default)
    {
        var worker = await WorkerAsync(workerId, ct);
        await CheckLinesAsync(lines, ct);
        var number = (await store.ListExpenseClaimsAsync(null, ct)).Select(c => c.Number).DefaultIfEmpty(0).Max() + 1;
        var claim = ExpenseClaim.Submit(number, worker.Id, memo, lines, clock.UtcNow, user.Actor);
        store.Add(claim);
        store.Add(AuditRecord.Create(clock.UtcNow, user.Actor, AuditAction.Created, EntityType, claim.Id, null, Snapshot(claim)));
        await store.SaveChangesAsync(ct);
        return claim;
    }

    /// <summary>Changes a claim and hands it in again. Only the person it's for, or who handed it in, may.</summary>
    public async Task<ExpenseClaim> ChangeAsync(Guid id, string? memo, IReadOnlyList<ExpenseClaimLineDraft> lines, CancellationToken ct = default)
    {
        var view = await RequiredAsync(id, ct);
        if (!IsTheirs(view))
            throw new DomainException("expense_claim.not_yours", "Only the person the claim is for, or who handed it in, can change it.");
        await CheckLinesAsync(lines, ct);
        var before = Snapshot(view.Claim);
        view.Claim.Change(memo, lines, clock.UtcNow, user.Actor, view.BillVoided);
        store.Add(AuditRecord.Create(clock.UtcNow, user.Actor, AuditAction.Updated, EntityType, view.Claim.Id, before, Snapshot(view.Claim)));
        await store.SaveChangesAsync(ct);
        return view.Claim;
    }

    /// <summary>Approves a claim: a bill to the person's vendor record, in one transaction.</summary>
    public async Task<Bill> ApproveAsync(Guid id, DateOnly billDate, string? note = null, CancellationToken ct = default)
    {
        await using var transaction = await unitOfWork.BeginAsync(ct);
        var view = await RequiredAsync(id, ct);
        var (claim, worker) = (view.Claim, view.Worker);
        await CheckNotOwnAsync(view, ct);
        if (worker.VendorId is not { } vendorId)
            throw new DomainException("expense_claim.worker_vendor", $"Link {worker.Name} to a vendor record on People and rates first: the bill paying them back goes to it.");
        if (!view.Waiting)
            throw new DomainException("expense_claim.not_waiting", claim.Status == ExpenseClaimStatus.Returned
                ? "This claim was returned. It can be approved once it's handed in again."
                : "This claim is approved already.");
        await CheckLinesAsync([.. claim.Lines.Select(l => new ExpenseClaimLineDraft(l.Date, l.AccountId, l.Description, l.Amount, l.CustomerId))], ct);

        var lines = claim.Lines.Select(l => new BillLineDraft(l.AccountId, l.Amount, TextCleaning.Cut($"{l.Date:MMM d, yyyy} · {l.Description}", 1000), ClassId: worker.ClassId)).ToList();
        var bill = await bills.CreateAsync(new CreateBillCommand(vendorId, billDate, lines, Reference: $"Claim {claim.Number}",
            Memo: TextCleaning.Cut($"Expense claim {claim.Number} for {worker.Name}{(claim.Memo is null ? "" : $": {claim.Memo}")}", 500)),
            recurringBillId: null, ct, approvedAs: $"Approved as expense claim {claim.Number}.");
        foreach (var line in claim.Lines.Where(l => l.CustomerId is not null))
            await billable.MarkAsync(new MarkBillableCommand(BillableSource.Bill, bill.Id, line.LineNumber, line.CustomerId!.Value), ct);
        foreach (var file in await attachments.ListAsync(EntityType, claim.Id, ct))
            if (await attachments.GetContentAsync(file.Id, ct) is { } bytes)
                await attachments.AddAsync("bill", bill.Id, file.FileName, new MemoryStream(bytes), ct);

        var before = Snapshot(claim);
        claim.Approve(bill.Id, clock.UtcNow, user.Actor, note, view.BillVoided);
        store.Add(AuditRecord.Create(clock.UtcNow, user.Actor, AuditAction.Updated, EntityType, claim.Id, before, Snapshot(claim)));
        await store.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return bill;
    }

    /// <summary>Sends a claim back to be fixed, with a note.</summary>
    public async Task<ExpenseClaim> ReturnAsync(Guid id, string note, CancellationToken ct = default)
    {
        var view = await RequiredAsync(id, ct);
        await CheckNotOwnAsync(view, ct);
        var before = Snapshot(view.Claim);
        view.Claim.Return(clock.UtcNow, user.Actor, note, view.BillVoided);
        store.Add(AuditRecord.Create(clock.UtcNow, user.Actor, AuditAction.Updated, EntityType, view.Claim.Id, before, Snapshot(view.Claim)));
        await store.SaveChangesAsync(ct);
        return view.Claim;
    }

    /// <summary>Whether the signed-in person is the one the claim is for, or who handed it in.</summary>
    public bool IsTheirs(ExpenseClaimView view) =>
        string.Equals(view.Claim.SubmittedBy, user.Actor, StringComparison.OrdinalIgnoreCase)
        || string.Equals(view.Worker.Username, user.Actor, StringComparison.OrdinalIgnoreCase);

    private async Task CheckNotOwnAsync(ExpenseClaimView view, CancellationToken ct)
    {
        if (IsTheirs(view) && (await purchases.GetPurchasingSettingsAsync(ct))?.AllowSelfApproval != true)
            throw new DomainException("expense_claim.own_claim", "This is your own claim (or you handed it in), so someone else has to approve or return it.");
    }

    private async Task<Worker> WorkerAsync(Guid? workerId, CancellationToken ct)
    {
        if (workerId is { } wid && wid != Guid.Empty)
        {
            var worker = await store.GetWorkerAsync(wid, ct) ?? throw new EntityNotFoundException(WorkerService.EntityType, wid);
            return worker.IsActive ? worker : throw new DomainException("expense_claim.worker_inactive", $"{worker.Name} is inactive.");
        }

        return (await store.ListWorkersAsync(ct)).FirstOrDefault(w => w.IsActive && string.Equals(w.Username, user.Actor, StringComparison.OrdinalIgnoreCase))
            ?? throw new DomainException("expense_claim.no_worker", "You aren't on People and rates yet, so there's no one to pay back. Ask an accountant to add you there.");
    }

    private async Task CheckLinesAsync(IReadOnlyList<ExpenseClaimLineDraft> lines, CancellationToken ct)
    {
        var chart = await accounts.GetChartAsync(ct);
        foreach (var (line, i) in (lines ?? []).Select((l, i) => (l, i + 1)))
        {
            if (line.AccountId == Guid.Empty || chart.Find(line.AccountId) is not { } account)
                throw new DomainException("expense_claim.account_required", $"Line {i}: choose what it was for.");
            if (!account.IsActive)
                throw new DomainException("expense_claim.account_inactive", $"Line {i}: {account.Name} is inactive.");
            Bill.EnsureCategoryAccount(account);
            if (line.CustomerId is { } customerId && customerId != Guid.Empty)
            {
                var customer = await sales.GetCustomerAsync(customerId, ct) ?? throw new EntityNotFoundException("customer", customerId);
                if (!customer.IsActive)
                    throw new DomainException("customer.inactive", $"Line {i}: {customer.DisplayName} is inactive.");
            }
        }
    }

    private async Task<ExpenseClaimView> RequiredAsync(Guid id, CancellationToken ct) =>
        await GetAsync(id, ct) ?? throw new EntityNotFoundException(EntityType, id);

    internal static string Snapshot(ExpenseClaim c) => JsonSerializer.Serialize(new
    {
        c.Number,
        c.WorkerId,
        Status = c.Status.ToString(),
        c.Memo,
        Lines = c.Lines.Select(l => new { l.Date, l.AccountId, l.Description, l.Amount, l.CustomerId }),
        c.Total,
        c.SubmittedBy,
        c.ReviewedBy,
        c.Note,
        c.BillId,
    });
}
