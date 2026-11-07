using Microsoft.Extensions.DependencyInjection;
using OpenBook.Application;
using OpenBook.Application.Attachments;
using OpenBook.Application.Banking;
using OpenBook.Application.Companies;
using OpenBook.Application.Import;
using OpenBook.Application.Nonprofit;
using OpenBook.Application.Sales;
using OpenBook.Domain.Accounts;
using OpenBook.Domain.Banking;
using OpenBook.Domain.Common;
using OpenBook.Domain.Nonprofit;
using OpenBook.Domain.Sales;

namespace OpenBook.Integration.Tests;

/// <summary>Small gaps from the audit: files on more records, bank files with lines above the header, reconciling from the feed, numbers.</summary>
public sealed class SmallGapTests : IAsyncLifetime
{
    private static readonly DateOnly Mar1 = new(2026, 3, 1);
    private static readonly DateOnly Mar31 = new(2026, 3, 31);

    private readonly LedgerDatabase _db = new();
    private Account _checking = null!;
    private Account _rent = null!;

    public async Task InitializeAsync()
    {
        await _db.InitializeAsync();
        _checking = await _db.CreateAccount("Checking", AccountType.Bank, "1000");
        _rent = await _db.CreateAccount("Rent", AccountType.Expense, "6500");
    }

    public Task DisposeAsync() => _db.DisposeAsync();

    private Task<T> In<TService, T>(Func<TService, Task<T>> work) where TService : notnull =>
        _db.InScope(sp => work(sp.GetRequiredService<TService>()));

    [Fact]
    public async Task Gifts_grants_assets_loans_and_projects_take_files()
    {
        foreach (var type in new[] { "grant", "donation", "pledge", "fixed_asset", "loan", "project" })
            Assert.Contains(type, AttachmentService.EntityTypes);
        Assert.Equal("nonprofit", RecordAreas.Of("donation"));
        Assert.Equal("projects", RecordAreas.Of("project"));

        var donor = await In<CustomerService, Customer>(s => s.CreateAsync(new CustomerDetails("Pat")));
        var gift = await In<GiftService, Donation>(s => s.RecordAsync(new RecordGiftCommand(donor.Id, Mar1, 50m, _checking.Id)));
        using var file = new MemoryStream("Thank you"u8.ToArray());
        await In<AttachmentService, OpenBook.Domain.Attachments.Attachment>(s => s.AddAsync("donation", gift.Id, "letter.txt", file));
        Assert.Single(await In<AttachmentService, IReadOnlyList<OpenBook.Domain.Attachments.Attachment>>(s => s.ListAsync("donation", gift.Id)));
    }

    [Fact]
    public async Task A_bank_file_with_lines_above_its_header_is_read_from_the_header()
    {
        const string csv = "Account Name: Checking\nAccount Number: ****1234\nBalance;1,234.56\n\nDate,Description,Amount\n03/01/2026,Coffee,-4.50\n03/02/2026,\"Deposit, branch\",100.00\n";
        var preview = await In<BankImportService, BankImportPreview>(s => s.PreviewAsync(_checking.Id, csv, new BankImportOptions(DateOrder.MonthFirst)));
        Assert.Equal(2, preview.Rows.Count);
        Assert.All(preview.Rows, r => Assert.True(r.IsValid));
        Assert.Equal([-4.50m, 100m], preview.Rows.Select(r => r.Amount!.Value));
        Assert.Equal(6, preview.Rows[0].Line);
    }

    [Fact]
    public async Task Reconciling_starts_with_the_lines_the_bank_feed_added_or_matched_ticked()
    {
        await In<BankFeedService, FeedImportResult>(s => s.QueueAsync(_checking.Id,
            [new FeedLine(Mar1, -100m, "Landlord", null, null, "A1"), new FeedLine(Mar1, -40m, "Shop", null, null, "A2"), new FeedLine(Mar1, -7m, "Later", null, null, "A3")]));
        var pending = await In<BankFeedService, IReadOnlyList<FeedReviewItem>>(s => s.ReviewAsync(_checking.Id, BankFeedStatus.Pending));
        await In<BankFeedService, BankTransaction>(s => s.AddAsync(pending.Single(p => p.Item.FitId == "A1").Item.Id, new AddFeedItemCommand(_rent.Id)));
        var spend = await In<BankingService, BankTransaction>(s => s.RecordAsync(new RecordBankTransactionCommand(BankTransactionKind.Spend, _checking.Id, Mar1, [new BankSplit(_rent.Id, 40m)])));
        await _db.InScope(sp => sp.GetRequiredService<BankFeedService>().MatchAsync(pending.Single(p => p.Item.FitId == "A2").Item.Id, spend.JournalEntryId));
        await In<BankingService, BankTransaction>(s => s.RecordAsync(new RecordBankTransactionCommand(BankTransactionKind.Spend, _checking.Id, Mar1, [new BankSplit(_rent.Id, 9m)])));

        var rec = await In<ReconciliationService, Reconciliation>(s => s.StartAsync(_checking.Id, Mar31, -149m));
        var sheet = await In<ReconciliationService, ReconciliationWorksheet>(s => s.GetWorksheetAsync(rec.Id));
        Assert.Equal(-140m, sheet.ClearedTotal);
        Assert.Equal(3, sheet.Items.Count);
    }

    [Theory]
    [InlineData(new string[0], "1001")]
    [InlineData(new[] { "1001", "1002" }, "1003")]
    [InlineData(new[] { "INV-0041", "INV-0042" }, "INV-0043")]
    [InlineData(new[] { "0099" }, "0100")]
    [InlineData(new[] { "A7", "B7" }, "B8")]
    [InlineData(new[] { "INV-9", "INV-10", "Misc" }, "INV-11")]
    [InlineData(new[] { "5", "6", "7" }, "8")]
    public void Suggested_numbers_keep_their_prefix_and_padding(string[] used, string next) =>
        Assert.Equal(next, SalesAccounts.NextNumber(used));

    [Fact]
    public async Task An_invoice_and_a_credit_memo_cant_share_a_number()
    {
        await _db.CreateAccount("Sales", AccountType.Income, "4000");
        var customer = await In<CustomerService, Customer>(s => s.CreateAsync(new CustomerDetails("Acme")));
        var item = await _db.InScope(sp => sp.GetRequiredService<OpenBook.Application.Sales.ItemService>().CreateQuickAsync("Work", OpenBook.Domain.Sales.ItemType.Service, 100m, false, null));
        await In<CreditService, CreditMemo>(s => s.CreateCreditMemoAsync(new CreateCreditMemoCommand(customer.Id, Mar1, [new SalesLineDraft(item.Id, null, 1m, 10m)], Number: "5000")));
        var refused = await Assert.ThrowsAsync<DomainException>(() => In<InvoiceService, Invoice>(s => s.CreateAsync(new CreateInvoiceCommand(customer.Id, Mar1,
            [new SalesLineDraft(item.Id, null, 1m, 100m)], Number: "5000"))));
        Assert.Equal("invoice.number_taken", refused.Code);
    }
}
