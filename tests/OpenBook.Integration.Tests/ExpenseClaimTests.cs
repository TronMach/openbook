using Microsoft.Extensions.DependencyInjection;
using OpenBook.Application.Attachments;
using OpenBook.Application.Projects;
using OpenBook.Application.Purchases;
using OpenBook.Application.Sales;
using OpenBook.Domain.Accounts;
using OpenBook.Domain.Attachments;
using OpenBook.Domain.Common;
using OpenBook.Domain.Projects;
using OpenBook.Domain.Purchases;
using OpenBook.Domain.Sales;

namespace OpenBook.Integration.Tests;

/// <summary>Staff expense claims (C345). Tests run as the owner, who hands each claim in.</summary>
public sealed class ExpenseClaimTests : IAsyncLifetime
{
    private static readonly DateOnly Mar1 = new(2026, 3, 1);
    private static readonly DateOnly Mar5 = new(2026, 3, 5);

    private readonly LedgerDatabase _db = new();
    private Account _travel = null!;
    private Account _meals = null!;
    private Vendor _danaVendor = null!;
    private Worker _dana = null!;
    private Customer _acme = null!;

    public async Task InitializeAsync()
    {
        await _db.InitializeAsync();
        _travel = await _db.CreateAccount("Travel", AccountType.Expense, "6100");
        _meals = await _db.CreateAccount("Meals", AccountType.Expense, "6200");
        _danaVendor = await In<VendorService, Vendor>(s => s.CreateAsync(new VendorDetails("Dana Reyes")));
        _dana = await In<WorkerService, Worker>(s => s.CreateAsync(new WorkerDetails("Dana", 50m, VendorId: _danaVendor.Id)));
        _acme = await In<CustomerService, Customer>(s => s.CreateAsync(new CustomerDetails("Acme")));
    }

    public Task DisposeAsync() => _db.DisposeAsync();

    private Task<T> In<TService, T>(Func<TService, Task<T>> work) where TService : notnull =>
        _db.InScope(sp => work(sp.GetRequiredService<TService>()));

    private static async Task<string> Refused(Func<Task> work) => (await Assert.ThrowsAsync<DomainException>(work)).Code;

    private Task<PurchasingSettings> SelfApproval(bool allowed, decimal? threshold = null) =>
        In<BillApprovalService, PurchasingSettings>(s => s.SaveSettingsAsync(new PurchasingSettingsCommand(threshold, 0m, false, allowed)));

    [Fact]
    public async Task An_approved_claim_becomes_an_approved_bill_with_its_receipts_and_billable_lines()
    {
        var claim = await In<ExpenseClaimService, ExpenseClaim>(s => s.SubmitAsync(_dana.Id, "Denver visit",
        [
            new ExpenseClaimLineDraft(Mar1, _travel.Id, "Train to Denver", 84.50m, _acme.Id),
            new ExpenseClaimLineDraft(Mar1, _meals.Id, "Lunch", 15.50m),
        ]));
        Assert.Equal((1, ExpenseClaimStatus.Submitted, 100m), (claim.Number, claim.Status, claim.Total));
        await In<AttachmentService, Attachment>(s => s.AddAsync(ExpenseClaimService.EntityType, claim.Id, "train.pdf", new MemoryStream("%PDF-1.4 ticket"u8.ToArray())));

        // The owner handed it in, so they can't approve or return it themselves unless the settings allow it.
        Assert.Equal("expense_claim.own_claim", await Refused(() => In<ExpenseClaimService, Bill>(s => s.ApproveAsync(claim.Id, Mar5))));
        Assert.Equal("expense_claim.own_claim", await Refused(() => In<ExpenseClaimService, ExpenseClaim>(s => s.ReturnAsync(claim.Id, "Missing receipt"))));
        // Over the approval limit, the bill would be held: approving the claim approves it.
        await SelfApproval(true, threshold: 50m);

        var bill = await In<ExpenseClaimService, Bill>(s => s.ApproveAsync(claim.Id, Mar5, "Thanks"));
        Assert.Equal((_danaVendor.Id, Mar5, 100m, "Claim 1"), (bill.VendorId, bill.Date, bill.Total, bill.Reference));
        Assert.Equal(BillApproval.Approved, bill.Approval);
        Assert.Equal([_travel.Id, _meals.Id], bill.Lines.Select(l => l.AccountId));
        Assert.Contains("Train to Denver", bill.Lines[0].Description, StringComparison.Ordinal);
        var receipts = await In<AttachmentService, IReadOnlyList<Attachment>>(s => s.ListAsync("bill", bill.Id));
        Assert.Equal("train.pdf", Assert.Single(receipts).FileName);
        var billable = Assert.Single(await In<BillableExpenseService, IReadOnlyList<BillableExpense>>(s => s.OpenAsync(_acme.Id)));
        Assert.Equal((bill.Id, 1, 84.50m), (billable.DocumentId, billable.LineNumber, billable.Cost));

        var view = (await In<ExpenseClaimService, ExpenseClaimView?>(s => s.GetAsync(claim.Id)))!;
        Assert.Equal((ExpenseClaimStatus.Approved, bill.Id, "Thanks", false), (view.Claim.Status, view.Claim.BillId, view.Claim.Note, view.Waiting));
        Assert.Equal("expense_claim.not_waiting", await Refused(() => In<ExpenseClaimService, Bill>(s => s.ApproveAsync(claim.Id, Mar5))));
        Assert.Equal("expense_claim.approved", await Refused(() =>
            In<ExpenseClaimService, ExpenseClaim>(s => s.ChangeAsync(claim.Id, null, [new ExpenseClaimLineDraft(Mar1, _meals.Id, "Lunch", 15m)]))));

        // Its bill voided: the claim waits again and can be changed and approved again.
        await In<BillableExpenseService, bool>(async s =>
        {
            await s.RemoveAsync(billable.Id);
            return true;
        });
        await In<BillService, Bill>(s => s.VoidAsync(bill.Id, "Wrong date"));
        Assert.True((await In<ExpenseClaimService, ExpenseClaimView?>(s => s.GetAsync(claim.Id)))!.Waiting);
        await In<ExpenseClaimService, ExpenseClaim>(s => s.ChangeAsync(claim.Id, "Denver visit", [new ExpenseClaimLineDraft(Mar1, _meals.Id, "Lunch", 15.50m)]));
        var again = await In<ExpenseClaimService, Bill>(s => s.ApproveAsync(claim.Id, Mar5));
        Assert.Equal(15.50m, again.Total);
    }

    [Fact]
    public async Task A_returned_claim_is_fixed_and_handed_in_again()
    {
        await SelfApproval(true);
        var claim = await In<ExpenseClaimService, ExpenseClaim>(s => s.SubmitAsync(_dana.Id, null, [new ExpenseClaimLineDraft(Mar1, _meals.Id, "Lunch", 150m)]));
        Assert.Equal("expense_claim.note_required", await Refused(() => In<ExpenseClaimService, ExpenseClaim>(s => s.ReturnAsync(claim.Id, " "))));
        var returned = await In<ExpenseClaimService, ExpenseClaim>(s => s.ReturnAsync(claim.Id, "That's a lot for lunch: attach the receipt"));
        Assert.Equal(ExpenseClaimStatus.Returned, returned.Status);
        Assert.Equal("expense_claim.not_waiting", await Refused(() => In<ExpenseClaimService, Bill>(s => s.ApproveAsync(claim.Id, Mar5))));

        var fixedClaim = await In<ExpenseClaimService, ExpenseClaim>(s => s.ChangeAsync(claim.Id, "Team lunch", [new ExpenseClaimLineDraft(Mar1, _meals.Id, "Team lunch, 6 people", 150m)]));
        Assert.Equal((ExpenseClaimStatus.Submitted, null, "Team lunch"), (fixedClaim.Status, fixedClaim.Note, fixedClaim.Memo));
        Assert.Equal(2, (await In<ExpenseClaimService, ExpenseClaim>(s => s.SubmitAsync(_dana.Id, null, [new ExpenseClaimLineDraft(Mar1, _meals.Id, "Coffee", 4m)]))).Number);
        Assert.Equal(2, (await In<ExpenseClaimService, IReadOnlyList<ExpenseClaimView>>(s => s.ListAsync(_dana.Id))).Count(c => c.Waiting));
    }

    [Fact]
    public async Task Claims_are_checked_before_they_are_handed_in()
    {
        var receivable = await _db.CreateAccount("Accounts Receivable", AccountType.AccountsReceivable, "1200");
        Assert.Equal("expense_claim.no_lines", await Refused(() => In<ExpenseClaimService, ExpenseClaim>(s => s.SubmitAsync(_dana.Id, null, []))));
        Assert.Equal("expense_claim.amount", await Refused(() =>
            In<ExpenseClaimService, ExpenseClaim>(s => s.SubmitAsync(_dana.Id, null, [new ExpenseClaimLineDraft(Mar1, _meals.Id, "Lunch", -5m)]))));
        Assert.Equal("expense_claim.description_required", await Refused(() =>
            In<ExpenseClaimService, ExpenseClaim>(s => s.SubmitAsync(_dana.Id, null, [new ExpenseClaimLineDraft(Mar1, _meals.Id, " ", 5m)]))));
        Assert.Equal("expense_claim.account_required", await Refused(() =>
            In<ExpenseClaimService, ExpenseClaim>(s => s.SubmitAsync(_dana.Id, null, [new ExpenseClaimLineDraft(Mar1, Guid.Empty, "Lunch", 5m)]))));
        await Assert.ThrowsAsync<DomainException>(() =>
            In<ExpenseClaimService, ExpenseClaim>(s => s.SubmitAsync(_dana.Id, null, [new ExpenseClaimLineDraft(Mar1, receivable.Id, "Lunch", 5m)])));
        // No worker record for the signed-in person, and none chosen.
        Assert.Equal("expense_claim.no_worker", await Refused(() =>
            In<ExpenseClaimService, ExpenseClaim>(s => s.SubmitAsync(null, null, [new ExpenseClaimLineDraft(Mar1, _meals.Id, "Lunch", 5m)]))));

        // Someone without a vendor record can hand a claim in, but it can't be approved until they have one.
        await SelfApproval(true);
        var sam = await In<WorkerService, Worker>(s => s.CreateAsync(new WorkerDetails("Sam", Username: LedgerDatabase.OwnerUsername)));
        var claim = await In<ExpenseClaimService, ExpenseClaim>(s => s.SubmitAsync(null, null, [new ExpenseClaimLineDraft(Mar1, _meals.Id, "Lunch", 5m)]));
        Assert.Equal(sam.Id, claim.WorkerId);
        Assert.Equal("expense_claim.worker_vendor", await Refused(() => In<ExpenseClaimService, Bill>(s => s.ApproveAsync(claim.Id, Mar5))));
    }
}
