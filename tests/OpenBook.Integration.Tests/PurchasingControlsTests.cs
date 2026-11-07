using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OpenBook.Application.Accounts;
using OpenBook.Application.Inventory;
using OpenBook.Application.Purchases;
using OpenBook.Application.Sales;
using OpenBook.Domain.Accounts;
using OpenBook.Domain.Common;
using OpenBook.Domain.Purchases;
using OpenBook.Domain.Sales;

namespace OpenBook.Integration.Tests;

/// <summary>L3 slice 3: purchase orders with receiving and 3-way match, bill approvals, NACHA files.</summary>
public sealed class PurchasingControlsTests : IAsyncLifetime
{
    private static readonly DateOnly Mar1 = new(2026, 3, 1);

    private readonly LedgerDatabase _db = new();
    private Account _checking = null!;
    private Account _freight = null!;
    private Item _widget = null!;
    private Vendor _supplier = null!;

    public async Task InitializeAsync()
    {
        await _db.InitializeAsync();
        _checking = await _db.CreateAccount("Checking", AccountType.Bank, "1000");
        _freight = await _db.CreateAccount("Freight In", AccountType.CostOfGoodsSold, "5100");
        var sales = await _db.CreateAccount("Sales", AccountType.Income, "4000");
        _widget = await In<ItemService, Item>(s => s.CreateAsync(new ItemDetails("Widget", ItemType.Inventory, sales.Id, 20m)));
        _supplier = await In<VendorService, Vendor>(s => s.CreateAsync(new VendorDetails("Parts Supply Co", AccountNumber: "C-1001")));
    }

    public Task DisposeAsync() => _db.DisposeAsync();

    private Task<T> In<TService, T>(Func<TService, Task<T>> work) where TService : notnull =>
        _db.InScope(sp => work(sp.GetRequiredService<TService>()));

    private static async Task<string> Refused(Func<Task> action) => (await Assert.ThrowsAsync<DomainException>(action)).Code;

    private Task<PurchaseOrder> Order(decimal quantity = 10m, decimal cost = 5m) =>
        In<PurchaseOrderService, PurchaseOrder>(s => s.CreateAsync(new SavePurchaseOrderCommand(_supplier.Id, Mar1,
            [new PurchaseOrderLineDraft(_widget.Id, null, null, quantity, cost), new PurchaseOrderLineDraft(null, _freight.Id, "Freight", 1m, 50m)])));

    private Task<IReadOnlyList<BillPayment>> Pay(Bill bill, DateOnly? date = null) =>
        In<BillPaymentService, IReadOnlyList<BillPayment>>(s => s.PayAsync(new PayBillsCommand(date ?? Mar1.AddDays(30), _checking.Id, [new ApplyToBill(bill.Id, bill.Balance)])));

    private Task<PurchasingSettings> Settings(decimal? threshold = null, decimal tolerance = 0m, bool requireReceipt = true, bool selfApproval = false) =>
        In<BillApprovalService, PurchasingSettings>(s => s.SaveSettingsAsync(new PurchasingSettingsCommand(threshold, tolerance, requireReceipt, selfApproval)));

    // ---- Purchase orders and 3-way match ----

    [Fact]
    public async Task A_landed_cost_on_a_bill_from_an_order_reaches_stock_received_before_the_bill()
    {
        await In<BillApprovalService, PurchasingSettings>(s => s.SaveSettingsAsync(new PurchasingSettingsCommand(null, 0m, false, false, ReceiveIntoStock: true)));
        var order = await Order();
        await In<PurchaseOrderService, int>(s => s.ReceiveAsync(order.Id, new ReceiveGoodsCommand(Mar1.AddDays(2), [new ReceiveLine(1, 6m)])));
        var bill = await In<PurchaseOrderService, Bill>(s => s.CreateBillAsync(order.Id, new BillFromOrderCommand(Mar1.AddDays(5), "INV-10", [new BillOrderLine(1, 6m, 5m)])));

        // The bill's lines are all accrued (the stock came in with the delivery), yet the freight still lands on it.
        var candidate = Assert.Single(await In<LandedCostService, IReadOnlyList<LandedCostCandidate>>(s => s.CandidatesAsync()), c => c.Bill.Id == bill.Id);
        Assert.Equal(6m, candidate.StockQuantity);
        await In<LandedCostService, OpenBook.Domain.Inventory.LandedCost>(s => s.CreateAsync(new CreateLandedCostCommand(Mar1.AddDays(6), 12m, [bill.Id])));
        var stock = (await In<InventoryService, StockLevel?>(s => s.StockForAsync(_widget.Id)))!;
        Assert.Equal((6m, 42m), (stock.Quantity, stock.Value));
    }

    [Fact]
    public async Task Received_goods_go_into_stock_and_the_bill_clears_received_not_billed()
    {
        await In<BillApprovalService, PurchasingSettings>(s => s.SaveSettingsAsync(new PurchasingSettingsCommand(null, 0m, false, false, ReceiveIntoStock: true)));
        var order = await Order();
        await In<PurchaseOrderService, int>(s => s.ReceiveAsync(order.Id, new ReceiveGoodsCommand(Mar1.AddDays(2), [new ReceiveLine(1, 6m)])));
        Assert.Equal(6m, (await In<InventoryService, StockLevel?>(s => s.StockForAsync(_widget.Id)))!.Quantity);

        var chart = await In<AccountService, ChartOfAccounts>(s => s.GetChartAsync());
        var accrual = chart.FindByFullName(PurchaseOrderService.ReceivedNotBilledName)!.Id;
        async Task<decimal> Balance(Guid account) =>
            (await _db.InScope(sp => sp.GetRequiredService<OpenBook.Application.Reporting.ReportLedger>().LinesAsync(OpenBook.Domain.Ledger.ReportBasis.Accrual, null, Mar1.AddDays(60))))
            .Where(l => l.AccountId == account).Sum(l => l.Amount);
        Assert.Equal((-30m, 30m), (await Balance(accrual), await Balance(_widget.InventoryAssetAccountId!.Value)));

        // Billing the 6 received at 5.50 clears the accrual at 30 and puts 3 into price variance; 2 more not yet received go into stock with the bill.
        var bill = await In<PurchaseOrderService, Bill>(s => s.CreateBillAsync(order.Id, new BillFromOrderCommand(Mar1.AddDays(5), "INV-9", [new BillOrderLine(1, 8m, 5.5m)])));
        Assert.Equal(44m, bill.Total);
        var variance = (await In<AccountService, ChartOfAccounts>(s => s.GetChartAsync())).FindByFullName(PurchaseOrderService.PriceVarianceName)!.Id;
        Assert.Equal((0m, 3m), (await Balance(accrual), await Balance(variance)));
        Assert.Equal(8m, (await In<InventoryService, StockLevel?>(s => s.StockForAsync(_widget.Id)))!.Quantity);
        Assert.Equal(41m, await Balance(_widget.InventoryAssetAccountId!.Value));
        Assert.Equal(("1", 8m, 6m, 30m), ("1", bill.SourceLines.Single(l => l.LineNumber == 1).Quantity, bill.SourceLines.Single(l => l.LineNumber == 1).AccruedQuantity, bill.SourceLines.Single(l => l.LineNumber == 1).AccruedAmount));

        Assert.Equal("purchaseorder.receipt_billed", await Refused(() => In<PurchaseOrderService, PurchaseOrder>(s => s.VoidReceiptAsync(order.Id, 1))));
        await In<BillService, Bill>(s => s.VoidAsync(bill.Id, "Wrong price"));
        Assert.Equal((-30m, 0m), (await Balance(accrual), await Balance(variance)));
        await In<PurchaseOrderService, PurchaseOrder>(s => s.VoidReceiptAsync(order.Id, 1));
        Assert.Equal(0m, await Balance(accrual));
        Assert.Equal(0m, (await In<InventoryService, StockLevel?>(s => s.StockForAsync(_widget.Id)))?.Quantity ?? 0m);
    }

    [Fact]
    public async Task An_order_prints_as_a_pdf_and_is_emailed_to_the_vendor()
    {
        var order = await Order();
        var pdf = await In<PurchaseOrderForms, OpenBook.Application.Forms.RenderedForm>(f => f.RenderAsync(order.Id));
        Assert.Equal("%PDF"u8.ToArray(), pdf.Pdf[..4]);
        Assert.Equal("Purchase order 1001", pdf.Title);

        var source = new OpenBook.Application.Forms.EmailSource(Domain.Email.EmailKind.PurchaseOrder, order.Id);
        var draft = await In<OpenBook.Application.Forms.EmailService, OpenBook.Application.Forms.EmailDraft>(f => f.DraftAsync(source));
        Assert.Equal("email.to_required", await Refused(() => In<OpenBook.Application.Forms.EmailService, Domain.Email.SentEmail>(f => f.SendAsync(source, draft))));
        await _db.InScope(sp => sp.GetRequiredService<OpenBook.Application.Forms.EmailService>().SaveSettingsAsync(new OpenBook.Application.Forms.EmailSettingsCommand(
            Domain.Companies.EmailDelivery.Smtp, "smtp.test", 587, Domain.Companies.SmtpSecurity.Auto, "books", "s3cret", false, "books@studio.test", "Studio", null)));
        await In<OpenBook.Application.Forms.EmailService, Domain.Email.SentEmail>(f => f.SendAsync(source, draft with { To = "orders@parts.test" }));
        var sent = Assert.Single(_db.Mail.Sent);
        Assert.Equal(("orders@parts.test", "application/pdf"), (sent.Message.To, sent.Message.Attachments.Single().ContentType));
        Assert.Contains(await _db.InDb(db => db.AuditRecords.Where(a => a.EntityId == order.Id).Select(a => a.Action).ToListAsync()), a => a == Domain.Auditing.AuditAction.Emailed);
    }

    [Fact]
    public async Task Orders_are_received_and_billed_in_parts_and_bills_beyond_receipts_wait_for_approval()
    {
        var order = await Order();
        Assert.Equal((100m, "1001", "Widget"), (order.Total, order.Number, order.Lines[0].Description));

        Assert.Equal(1, await In<PurchaseOrderService, int>(s => s.ReceiveAsync(order.Id, new ReceiveGoodsCommand(Mar1.AddDays(3), [new ReceiveLine(1, 6m)], "Partial delivery"))));
        Assert.Equal("purchaseorder.over_received", await Refused(() =>
            In<PurchaseOrderService, int>(s => s.ReceiveAsync(order.Id, new ReceiveGoodsCommand(Mar1.AddDays(4), [new ReceiveLine(1, 5m)])))));
        Assert.Equal("purchaseorder.received", await Refused(() =>
            In<PurchaseOrderService, PurchaseOrder>(s => s.UpdateAsync(order.Id, new SavePurchaseOrderCommand(_supplier.Id, Mar1, [new PurchaseOrderLineDraft(_widget.Id, null, null, 1m, 5m)])))));

        // By default a bill takes what's been received and isn't billed yet (and the freight).
        var first = await In<PurchaseOrderService, Bill>(s => s.CreateBillAsync(order.Id, new BillFromOrderCommand(Mar1.AddDays(5), "INV-1")));
        Assert.Equal((80m, BillApproval.NotRequired, order.Id), (first.Total, first.Approval, first.PurchaseOrderId));
        Assert.Equal(6m, (await In<InventoryService, StockLevel?>(s => s.StockForAsync(_widget.Id)))!.Quantity);
        Assert.Single(await Pay(first));

        // Billing the other 4 before they arrive: held for approval, and it finishes the order.
        var second = await In<PurchaseOrderService, Bill>(s => s.CreateBillAsync(order.Id, new BillFromOrderCommand(Mar1.AddDays(8), "INV-2", [new BillOrderLine(1, 4m)])));
        Assert.Equal(BillApproval.Pending, second.Approval);
        Assert.Contains("billed 10 but 6 received", second.HoldReason, StringComparison.Ordinal);
        Assert.Equal(PurchaseOrderStatus.Closed, (await In<PurchaseOrderService, PurchaseOrder?>(s => s.GetAsync(order.Id)))!.Status);
        Assert.Equal("bill.not_approved", await Refused(() => Pay(second)));
        Assert.Single(await In<BillApprovalService, IReadOnlyList<Bill>>(s => s.PendingAsync()));

        // Whoever entered it can't approve it, unless the settings allow it.
        Assert.Equal("approval.own_bill", await Refused(() => In<BillApprovalService, Bill>(s => s.ApproveAsync(second.Id, null))));
        await Settings(selfApproval: true);
        var approved = await In<BillApprovalService, Bill>(s => s.ApproveAsync(second.Id, "Rest arrived on the 9th"));
        Assert.Equal((BillApproval.Approved, LedgerDatabase.OwnerUsername), (approved.Approval, approved.ApprovedBy));

        await In<BillService, Bill>(s => s.VoidAsync(second.Id, "Wrong quantity"));
        var reopened = await In<PurchaseOrderService, PurchaseOrderProgress>(s => s.ProgressAsync(order.Id));
        Assert.Equal(4m, reopened.Lines[0].ToBill);
        Assert.Equal(PurchaseOrderStatus.Open, (await In<PurchaseOrderService, PurchaseOrder?>(s => s.GetAsync(order.Id)))!.Status);
    }

    [Fact]
    public async Task Costs_over_the_order_beyond_the_tolerance_hold_the_bill()
    {
        await Settings(tolerance: 2m, requireReceipt: false);
        var order = await Order();
        var close = await In<PurchaseOrderService, Bill>(s => s.CreateBillAsync(order.Id, new BillFromOrderCommand(Mar1, "A", [new BillOrderLine(1, 5m, 5.10m)])));
        Assert.Equal(BillApproval.NotRequired, close.Approval);

        var over = await In<PurchaseOrderService, Bill>(s => s.CreateBillAsync(order.Id, new BillFromOrderCommand(Mar1, "B",
            [new BillOrderLine(1, 5m, 5.20m), new BillOrderLine(2, 1m, 60m)])));
        Assert.Equal(BillApproval.Pending, over.Approval);
        var reasons = over.HoldReason!.Split('\n');
        Assert.Equal(2, reasons.Length);
        Assert.Contains("over the order's 5.00", reasons[0], StringComparison.Ordinal);
        Assert.Contains("Freight: billed 60.00 in all, over the order's 50.00", reasons[1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Bills_over_the_threshold_need_approval_and_rejected_ones_cant_be_paid()
    {
        await Settings(threshold: 1000m, selfApproval: true);
        var small = await In<BillService, Bill>(s => s.CreateAsync(new CreateBillCommand(_supplier.Id, Mar1, [new BillLineDraft(_freight.Id, 999.99m)], Reference: "S")));
        var big = await In<BillService, Bill>(s => s.CreateAsync(new CreateBillCommand(_supplier.Id, Mar1, [new BillLineDraft(_freight.Id, 1000m)], Reference: "B")));
        Assert.Equal((BillApproval.NotRequired, BillApproval.Pending), (small.Approval, big.Approval));
        Assert.Equal(LedgerDatabase.OwnerUsername, big.EnteredBy);

        Assert.Equal("approval.reason_required", await Refused(() => In<BillApprovalService, Bill>(s => s.RejectAsync(big.Id, " "))));
        await In<BillApprovalService, Bill>(s => s.RejectAsync(big.Id, "Not ours"));
        Assert.Equal("bill.not_approved", await Refused(() => Pay(big)));
        await In<BillApprovalService, Bill>(s => s.ApproveAsync(big.Id, "It was ours after all"));
        Assert.Single(await Pay(big));
        Assert.Equal("approval.not_pending", await Refused(() => In<BillApprovalService, Bill>(s => s.ApproveAsync(small.Id, null))));
    }

    // ---- ACH ----

    [Fact]
    public async Task Ach_files_stored_in_clear_are_encrypted_and_can_still_be_read()
    {
        var old = AchBatch.Create(_checking.Id, Mar1, 'A', 1, 10m, "x", DateTimeOffset.UtcNow);
        await _db.InDb(async db =>
        {
            db.AchBatches.Add(old);
            await db.SaveChangesAsync();
            await db.Database.ExecuteSqlRawAsync("UPDATE \"AchBatches\" SET \"Content\" = 'FILE 123456789', \"ProtectedContent\" = NULL");
            return true;
        });
        var before = await _db.InDb(db => db.AchBatches.AsNoTracking().SingleAsync());
        Assert.Equal("FILE 123456789", await In<AchService, string>(s => Task.FromResult(s.FileText(before))));

        Assert.Equal(1, await In<AchService, int>(s => s.ProtectStoredFilesAsync()));

        var after = await _db.InDb(db => db.AchBatches.AsNoTracking().SingleAsync());
        Assert.Equal(("protected:FILE 123456789", ""), (after.ProtectedContent, after.UnprotectedContent));
        Assert.Equal("FILE 123456789", await In<AchService, string>(s => Task.FromResult(s.FileText(after))));
        Assert.Equal(0, await In<AchService, int>(s => s.ProtectStoredFilesAsync()));
    }

    [Fact]
    public async Task Routing_numbers_and_ach_settings_are_checked()
    {
        Assert.Equal("021000021", Ach.Routing(" 021000021 "));
        Assert.Equal("ach.routing_invalid", Assert.Throws<DomainException>(() => Ach.Routing("021000022")).Code);
        Assert.Equal("ach.routing_invalid", Assert.Throws<DomainException>(() => Ach.Routing("12345")).Code);
        Assert.Equal("ach.bank_account_required", await Refused(async () =>
        {
            var card = await _db.CreateAccount("Visa", AccountType.CreditCard, "2100");
            await In<AchService, AchSettings>(s => s.SaveSettingsAsync(card.Id, new AchSettingsCommand("011000015", "Fed", "1234567890", "Studio", "1234567890", "Studio")));
        }));
    }

    [Fact]
    public async Task An_ach_file_the_bank_never_got_is_withdrawn_and_a_voided_payment_flags_its_file()
    {
        await In<AchService, VendorBankAccount>(s => s.SetVendorBankAccountAsync(_supplier.Id, new VendorBankAccountCommand("021000021", "1234-56789", BankAccountKind.Checking)));
        await In<AchService, AchSettings>(s => s.SaveSettingsAsync(_checking.Id,
            new AchSettingsCommand("011000015", "Federal Reserve Bank", "1234567890", "Studio LLC", "1234567890", "Studio LLC", "Payables")));
        async Task<BillPayment> PaidBill(decimal amount, string reference) =>
            Assert.Single(await Pay(await In<BillService, Bill>(s => s.CreateAsync(new CreateBillCommand(_supplier.Id, Mar1, [new BillLineDraft(_freight.Id, amount)], Reference: reference)))));
        var payment = await PaidBill(100m, "W1");
        var effective = new DateOnly(2026, 4, 2);
        var batch = await In<AchService, AchBatch>(s => s.CreateFileAsync(_checking.Id, effective, [payment.Id]));

        Assert.Equal("ach.reason_required", await Refused(() => In<AchService, AchBatch>(s => s.WithdrawFileAsync(batch.Id, " "))));
        var withdrawn = await In<AchService, AchBatch>(s => s.WithdrawFileAsync(batch.Id, "Bank portal rejected the upload"));
        Assert.NotNull(withdrawn.WithdrawnAt);
        Assert.Null((await In<BillPaymentService, BillPayment?>(s => s.GetAsync(payment.Id)))!.AchBatchId);
        Assert.Equal(payment.Id, Assert.Single(await In<AchService, IReadOnlyList<AchCandidate>>(s => s.CandidatesAsync(_checking.Id))).Payment.Id);
        Assert.Equal("ach.withdrawn", await Refused(() => In<AchService, string>(async s => s.FileText((await s.GetBatchAsync(batch.Id))!))));
        Assert.Equal("ach.withdrawn", await Refused(() => In<AchService, AchBatch>(s => s.WithdrawFileAsync(batch.Id, "again"))));

        // Sent again; then voided, the file it's in says so.
        var again = await In<AchService, AchBatch>(s => s.CreateFileAsync(_checking.Id, effective, [payment.Id]));
        await In<BillPaymentService, BillPayment>(s => s.VoidAsync(payment.Id, "Paid twice"));
        var files = await In<AchService, IReadOnlyList<AchFile>>(s => s.FilesAsync(_checking.Id));
        Assert.Equal(1, files.Single(f => f.Batch.Id == again.Id).VoidedPayments);
        Assert.Equal(0, files.Single(f => f.Batch.Id == batch.Id).VoidedPayments);

        // A file whose payment has come through the bank stays.
        var cleared = await PaidBill(75m, "W2");
        var third = await In<AchService, AchBatch>(s => s.CreateFileAsync(_checking.Id, effective, [cleared.Id]));
        await In<OpenBook.Application.Banking.BankFeedService, OpenBook.Application.Banking.FeedImportResult>(s => s.QueueAsync(_checking.Id,
            [new OpenBook.Application.Banking.FeedLine(cleared.Date, -75m, "PARTS SUPPLY", null, null, "ACH-2")]));
        var line = Assert.Single(await In<OpenBook.Application.Banking.BankFeedService, IReadOnlyList<OpenBook.Application.Banking.FeedReviewItem>>(s => s.ReviewAsync(_checking.Id)));
        await _db.InScope(async sp => { await sp.GetRequiredService<OpenBook.Application.Banking.BankFeedService>().MatchAsync(line.Item.Id, cleared.JournalEntryId); return true; });
        Assert.Equal("ach.payment_cleared", await Refused(() => In<AchService, AchBatch>(s => s.WithdrawFileAsync(third.Id, "Mistake"))));
    }

    [Fact]
    public async Task Ach_files_pay_vendors_once_in_nacha_format()
    {
        var bank = await In<AchService, VendorBankAccount>(s => s.SetVendorBankAccountAsync(_supplier.Id, new VendorBankAccountCommand("021000021", "1234-56789", BankAccountKind.Checking)));
        Assert.Equal("•••• 6789", bank.Masked);
        Assert.Equal("protected:123456789", bank.ProtectedAccountNumber);
        var history = await _db.InDb(db => db.AuditRecords.AsNoTracking().Where(a => a.EntityId == _supplier.Id).Select(a => a.After).ToListAsync());
        Assert.DoesNotContain(history, h => h != null && h.Contains("123456789", StringComparison.Ordinal));

        var other = await In<VendorService, Vendor>(s => s.CreateAsync(new VendorDetails("Check Only Ltd")));
        var bill = await In<BillService, Bill>(s => s.CreateAsync(new CreateBillCommand(_supplier.Id, Mar1, [new BillLineDraft(_freight.Id, 1234.56m)], Reference: "X")));
        var otherBill = await In<BillService, Bill>(s => s.CreateAsync(new CreateBillCommand(other.Id, Mar1, [new BillLineDraft(_freight.Id, 10m)], Reference: "Y")));
        var payment = Assert.Single(await Pay(bill));
        await Pay(otherBill);

        Assert.Equal("ach.not_set_up", await Refused(() => In<AchService, AchBatch>(s => s.CreateFileAsync(_checking.Id, Mar1.AddDays(31), [payment.Id]))));
        await In<AchService, AchSettings>(s => s.SaveSettingsAsync(_checking.Id,
            new AchSettingsCommand("011000015", "Federal Reserve Bank", "1234567890", "Studio LLC", "1234567890", "Studio LLC", "Payables")));
        var candidate = Assert.Single(await In<AchService, IReadOnlyList<AchCandidate>>(s => s.CandidatesAsync(_checking.Id)));
        Assert.Equal(payment.Id, candidate.Payment.Id);

        var batch = await In<AchService, AchBatch>(s => s.CreateFileAsync(_checking.Id, new DateOnly(2026, 4, 2), [payment.Id]));
        var file = await In<AchService, string>(s => Task.FromResult(s.FileText(batch)));
        var lines = file.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(10, lines.Length);
        Assert.All(lines, l => Assert.Equal(94, l.Length));
        Assert.StartsWith("101 0110000151234567890", lines[0], StringComparison.Ordinal);
        Assert.Equal('A', lines[0][33]);
        Assert.StartsWith("5220STUDIO LLC", lines[1], StringComparison.Ordinal);
        Assert.Equal("CCDPAYABLES  ", lines[1][50..63]);
        Assert.Equal("260402", lines[1][69..75]);
        Assert.Equal("622021000021123456789        0000123456C-1001         PARTS SUPPLY CO         ", lines[2][..78]);
        Assert.Equal("011000010000001", lines[2][79..]);
        Assert.Equal("82200000010002100002000000000000000000123456", lines[3][..44]);
        Assert.StartsWith("9000001000001000000010002100002000000000000000000123456", lines[4], StringComparison.Ordinal);
        Assert.Equal(new string('9', 94), lines[9]);

        // The file holds the vendor's full account number, so it's stored encrypted...
        var stored = await _db.InDb(db => db.AchBatches.AsNoTracking().SingleAsync(b => b.Id == batch.Id));
        Assert.Equal(("protected:" + file, ""), (stored.ProtectedContent, stored.UnprotectedContent));
        Assert.Equal("ach.file_unreadable", await Refused(() => In<AchService, string>(s =>
            Task.FromResult(s.FileText(AchBatch.Create(_checking.Id, Mar1, 'A', 1, 1m, "not ours", DateTimeOffset.UtcNow))))));

        // ...and is left out of the data export.
        var export = await _db.InScope(sp => sp.GetRequiredService<OpenBook.Application.IDataExporter>().ExportAsync(default));
        try
        {
            using var zip = System.IO.Compression.ZipFile.OpenRead(export.Path);
            foreach (var entry in zip.Entries.Where(e => e.Name.EndsWith(".csv", StringComparison.Ordinal) || e.Name == "README.md"))
            {
                using var reader = new StreamReader(entry.Open());
                Assert.DoesNotContain(lines[2][..21], await reader.ReadToEndAsync(), StringComparison.Ordinal);
            }
        }
        finally
        {
            File.Delete(export.Path);
        }

        Assert.Equal(batch.Id, (await In<BillPaymentService, BillPayment?>(s => s.GetAsync(payment.Id)))!.AchBatchId);
        Assert.Empty(await In<AchService, IReadOnlyList<AchCandidate>>(s => s.CandidatesAsync(_checking.Id)));

        // Sent by ACH, so never a check as well.
        Assert.DoesNotContain(await In<OpenBook.Application.Banking.CheckService, IReadOnlyList<OpenBook.Application.Banking.PrintableCheck>>(s => s.ListAsync()), c => c.Id == payment.Id);
        Assert.Equal("check.sent_by_ach", await Refused(() => In<OpenBook.Application.Banking.CheckService, OpenBook.Application.Banking.PrintedChecks>(s =>
            s.PrintAsync([(OpenBook.Application.Banking.CheckKind.BillPayment, payment.Id)]))));

        // A payment already through the bank isn't one to send.
        var cleared = Assert.Single(await Pay(await In<BillService, Bill>(s => s.CreateAsync(new CreateBillCommand(_supplier.Id, Mar1, [new BillLineDraft(_freight.Id, 75m)], Reference: "Z")))));
        Assert.Single(await In<AchService, IReadOnlyList<AchCandidate>>(s => s.CandidatesAsync(_checking.Id)));
        await In<OpenBook.Application.Banking.BankFeedService, OpenBook.Application.Banking.FeedImportResult>(s => s.QueueAsync(_checking.Id,
            [new OpenBook.Application.Banking.FeedLine(cleared.Date, -75m, "PARTS SUPPLY", null, null, "ACH-1")]));
        var line = Assert.Single(await In<OpenBook.Application.Banking.BankFeedService, IReadOnlyList<OpenBook.Application.Banking.FeedReviewItem>>(s => s.ReviewAsync(_checking.Id)));
        await _db.InScope(async sp => { await sp.GetRequiredService<OpenBook.Application.Banking.BankFeedService>().MatchAsync(line.Item.Id, cleared.JournalEntryId); return true; });
        Assert.Empty(await In<AchService, IReadOnlyList<AchCandidate>>(s => s.CandidatesAsync(_checking.Id)));
        Assert.Equal("ach.payment_not_eligible", await Refused(() => In<AchService, AchBatch>(s => s.CreateFileAsync(_checking.Id, Mar1.AddDays(31), [payment.Id]))));
    }
}
