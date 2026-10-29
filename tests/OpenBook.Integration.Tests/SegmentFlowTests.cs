using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OpenBook.Application.Banking;
using OpenBook.Application.Import;
using OpenBook.Application.Ledger;
using OpenBook.Application.Purchases;
using OpenBook.Application.Reporting;
using OpenBook.Application.Sales;
using OpenBook.Application.Segments;
using OpenBook.Domain.Accounts;
using OpenBook.Domain.Banking;
using OpenBook.Domain.Common;
using OpenBook.Domain.Ledger;
using OpenBook.Domain.Purchases;
using OpenBook.Domain.Sales;
using OpenBook.Domain.Segments;

namespace OpenBook.Integration.Tests;

/// <summary>L1: classes, locations and tags on transactions; reports by segment; reclassifying; budgets by segment.</summary>
public sealed class SegmentFlowTests : IAsyncLifetime
{
    private static readonly DateOnly Mar10 = new(2026, 3, 10);
    private static readonly DateOnly Mar20 = new(2026, 3, 20);

    private readonly LedgerDatabase _db = new();
    private Account _checking = null!;
    private Account _sales = null!;
    private Account _rent = null!;
    private Item _widget = null!;
    private Customer _acme = null!;
    private Vendor _landlord = null!;
    private Segment _retail = null!;
    private Segment _online = null!;
    private Segment _east = null!;
    private Segment _promo = null!;

    public async Task InitializeAsync()
    {
        await _db.InitializeAsync();
        _checking = await _db.CreateAccount("Checking", AccountType.Bank, "1000");
        _sales = await _db.CreateAccount("Sales", AccountType.Income, "4000");
        _rent = await _db.CreateAccount("Rent", AccountType.Expense, "6000");
        _widget = await In<ItemService, Item>(s => s.CreateAsync(new ItemDetails("Widget", ItemType.Service, _sales.Id, 100m)));
        _acme = await In<CustomerService, Customer>(s => s.CreateAsync(new CustomerDetails("Acme")));
        _landlord = await In<VendorService, Vendor>(s => s.CreateAsync(new VendorDetails("Landlord")));
        _retail = await Seg(s => s.CreateAsync(SegmentKind.Class, "Retail"));
        _online = await Seg(s => s.CreateAsync(SegmentKind.Class, "Online"));
        _east = await Seg(s => s.CreateAsync(SegmentKind.Location, "East"));
        _promo = await Seg(s => s.CreateAsync(SegmentKind.Tag, "Spring promo"));
    }

    public Task DisposeAsync() => _db.DisposeAsync();

    private Task<T> In<TService, T>(Func<TService, Task<T>> work) where TService : notnull =>
        _db.InScope(sp => work(sp.GetRequiredService<TService>()));

    private Task<T> Seg<T>(Func<SegmentService, Task<T>> work) => In(work);

    private Task<ReportTable> Report(string key, string query) =>
        In<ReportCatalog, ReportTable>(c => c.RunAsync(key, ReportParameters.Parse(query)));

    private Task<Invoice> Invoice(params SalesLineDraft[] lines) =>
        In<InvoiceService, Invoice>(s => s.CreateAsync(new CreateInvoiceCommand(_acme.Id, Mar10, lines, LocationId: _east.Id, TagIds: [_promo.Id])));

    private SalesLineDraft Widgets(decimal qty, Segment? cls) => new(_widget.Id, null, qty, 100m, ClassId: cls?.Id);

    private Task<IReadOnlyList<LineSegment>> Segments(Guid entryId) =>
        _db.InDb<IReadOnlyList<LineSegment>>(async db => await db.LineSegments.AsNoTracking().Where(s => s.JournalEntryId == entryId).ToListAsync());

    [Fact]
    public async Task Segments_nest_and_names_are_unique_among_siblings()
    {
        var web = await Seg(s => s.CreateAsync(SegmentKind.Class, "Web", _online.Id));
        Assert.Equal("Online:Web", (await Seg(s => s.NamesAsync()))[web.Id]);
        var tree = await Seg(s => s.ListAsync(SegmentKind.Class));
        Assert.Equal(["Online", "Online:Web", "Retail"], tree.Select(t => t.FullName));

        Assert.Equal("segment.name_taken", (await Assert.ThrowsAsync<DomainException>(() => Seg(s => s.CreateAsync(SegmentKind.Class, "retail")))).Code);
        Assert.Equal("segment.parent_kind", (await Assert.ThrowsAsync<DomainException>(() => Seg(s => s.CreateAsync(SegmentKind.Location, "Sub", _online.Id)))).Code);
        Assert.Equal("segment.cycle", (await Assert.ThrowsAsync<DomainException>(() => Seg(s => s.UpdateAsync(_online.Id, "Online", web.Id)))).Code);
        Assert.Equal("segment.has_active_children", (await Assert.ThrowsAsync<DomainException>(() => Seg(s => s.SetActiveAsync(_online.Id, false)))).Code);
    }

    [Fact]
    public async Task Invoices_post_one_line_per_account_and_class_with_the_location_on_every_line()
    {
        var invoice = await Invoice(Widgets(2, _retail), Widgets(1, _online), Widgets(1, _retail));
        var entry = await In<PostingEngine, JournalEntry?>(e => e.GetAsync(invoice.JournalEntryId));
        Assert.Equal(3, entry!.Lines.Count); // A/R, Sales·Retail, Sales·Online

        var rows = await Segments(invoice.JournalEntryId);
        Assert.Equal(3, rows.Count);
        Assert.All(rows, r => Assert.Equal(_east.Id, r.LocationId));
        Assert.Equal(-300m, entry.Lines.Single(l => rows.Single(r => r.JournalLineId == l.Id).ClassId == _retail.Id).Amount);
        Assert.Equal(_east.Id, invoice.LocationId);
        Assert.Equal([_promo.Id], await Seg(s => s.GetTagsAsync(invoice.JournalEntryId)));

        var byClass = await Report("profit-and-loss-by-class", "preset=Custom&from=2026-03-01&to=2026-03-31&basis=Accrual");
        Assert.Equal(["", "Online", "Retail", "Total"], byClass.Columns.Select(c => c.Header));
        Assert.Equal(100m, byClass.Find("Net income", 1));
        Assert.Equal(300m, byClass.Find("Net income", 2));
        Assert.Equal(400m, byClass.Find("Net income", 3));

        var retailOnly = await Report("profit-and-loss", $"preset=Custom&from=2026-03-01&to=2026-03-31&basis=Accrual&classId={_retail.Id}");
        Assert.Equal(300m, retailOnly.Find("Net income"));
        Assert.Contains("Class: Retail", retailOnly.Subtitles[1], StringComparison.Ordinal);
        var tagged = await Report("profit-and-loss", $"preset=Custom&from=2026-03-01&to=2026-03-31&basis=Accrual&tagId={_promo.Id}");
        Assert.Equal(400m, tagged.Find("Net income"));
    }

    [Fact]
    public async Task Voids_copy_segments_and_tags_so_reports_net_to_zero()
    {
        var invoice = await Invoice(Widgets(1, _retail));
        await In<InvoiceService, Invoice>(s => s.VoidAsync(invoice.Id, "Wrong customer"));
        var reversalId = await In<PostingEngine, Guid?>(e => e.FindReversalIdAsync(invoice.JournalEntryId));
        Assert.Equal(2, (await Segments(reversalId!.Value)).Count);
        Assert.Equal([_promo.Id], await Seg(s => s.GetTagsAsync(reversalId.Value)));

        var retail = await Report("profit-and-loss", $"preset=Custom&from=2026-03-01&to=2026-03-31&basis=Accrual&classId={_retail.Id}");
        Assert.Equal(0m, retail.Find("Net income") ?? 0m);
    }

    [Fact]
    public async Task Cash_basis_payments_take_the_paid_documents_classes_and_location()
    {
        var invoice = await Invoice(Widgets(3, _retail), Widgets(1, _online));
        await In<PaymentService, CustomerPayment>(s => s.ReceiveAsync(new ReceivePaymentCommand(_acme.Id, Mar20, _checking.Id, [new ApplyToInvoice(invoice.Id, 200m)])));
        var bill = await In<BillService, Bill>(s => s.CreateAsync(new CreateBillCommand(_landlord.Id, Mar10,
            [new BillLineDraft(_rent.Id, 80m, ClassId: _online.Id), new BillLineDraft(_rent.Id, 20m, ClassId: _retail.Id)], LocationId: _east.Id)));
        await In<BillPaymentService, IReadOnlyList<BillPayment>>(s => s.PayAsync(new PayBillsCommand(Mar20, _checking.Id, [new ApplyToBill(bill.Id, 100m)])));

        var byClass = await Report("profit-and-loss-by-class", "preset=Custom&from=2026-03-01&to=2026-03-31&basis=Cash");
        Assert.Equal(-30m, byClass.Find("Net income", 1)); // Online: 50 income less 80 rent
        Assert.Equal(130m, byClass.Find("Net income", 2)); // Retail: 150 less 20
        var east = await Report("profit-and-loss", $"preset=Custom&from=2026-03-01&to=2026-03-31&basis=Cash&locationId={_east.Id}");
        Assert.Equal(100m, east.Find("Net income"));
        var none = await Report("profit-and-loss", "preset=Custom&from=2026-03-01&to=2026-03-31&basis=Cash&locationId=none");
        Assert.Equal(0m, none.Find("Net income") ?? 0m);
    }

    [Fact]
    public async Task Reclassifying_moves_ledger_lines_and_the_documents_with_them()
    {
        var invoice = await Invoice(Widgets(1, null));
        var bill = await In<BillService, Bill>(s => s.CreateAsync(new CreateBillCommand(_landlord.Id, Mar10, [new BillLineDraft(_rent.Id, 40m)])));
        var lines = await Seg(s => s.ListLinesAsync(new DateOnly(2026, 3, 1), new DateOnly(2026, 3, 31), null, Guid.Empty, null));
        var income = lines.Single(l => l.Line.AccountId == _sales.Id);
        var rent = lines.Single(l => l.Line.AccountId == _rent.Id);

        Assert.Equal(1, await Seg(s => s.ReclassifyAsync(new ReclassifyCommand([income.Line.JournalLineId], true, _online.Id, false, null))));
        Assert.Equal(_online.Id, (await In<InvoiceService, Invoice?>(s => s.GetAsync(invoice.Id)))!.Lines[0].ClassId);
        await Seg(s => s.ReclassifyAsync(new ReclassifyCommand([rent.Line.JournalLineId], true, _retail.Id, true, _east.Id)));
        var savedBill = await In<BillService, Bill?>(s => s.GetAsync(bill.Id));
        Assert.Equal(_retail.Id, savedBill!.Lines[0].ClassId);
        Assert.Equal(_east.Id, savedBill.LocationId);
        // A document's location is the whole entry's: the A/P line moved too.
        Assert.All(await Segments(bill.JournalEntryId), r => Assert.Equal(_east.Id, r.LocationId));

        Assert.True(await _db.InDb(db => db.AuditRecords.AnyAsync(r => r.EntityType == "journal_entry" && r.EntityId == bill.JournalEntryId)));
        Assert.Equal("segment.inactive", (await Assert.ThrowsAsync<DomainException>(async () =>
        {
            var old = await Seg(s => s.CreateAsync(SegmentKind.Class, "Old"));
            await Seg(s => s.SetActiveAsync(old.Id, false));
            await Seg(s => s.ReclassifyAsync(new ReclassifyCommand([rent.Line.JournalLineId], true, old.Id, false, null)));
        })).Code);
    }

    [Fact]
    public async Task Bank_transactions_and_journal_entries_carry_segments()
    {
        var spend = await In<BankingService, BankTransaction>(s => s.RecordAsync(new RecordBankTransactionCommand(BankTransactionKind.Spend, _checking.Id, Mar10,
            [new BankSplit(_rent.Id, 50m, ClassId: _retail.Id)], "Landlord", LocationId: _east.Id)));
        Assert.Equal(_east.Id, spend.LocationId);
        Assert.Equal(2, (await Segments(spend.JournalEntryId)).Count);

        var entry = await In<PostingEngine, JournalEntry>(e => e.PostAsync(new PostJournalEntryCommand(Mar10,
            [JournalLineDraft.Dr(_rent.Id, 10m, classId: _online.Id), JournalLineDraft.Cr(_checking.Id, 10m)], "Accrual")));
        var rows = await Segments(entry.Id);
        Assert.Equal(_online.Id, Assert.Single(rows).ClassId);

        Assert.Equal("segment.kind_mismatch", (await Assert.ThrowsAsync<DomainException>(() => In<PostingEngine, JournalEntry>(e => e.PostAsync(
            new PostJournalEntryCommand(Mar10, [JournalLineDraft.Dr(_rent.Id, 1m, classId: _east.Id), JournalLineDraft.Cr(_checking.Id, 1m)]))))).Code);

        await Seg(s => s.SetTagsAsync(entry.Id, [_promo.Id]));
        var tagged = await Report("general-ledger", $"preset=Custom&from=2026-03-01&to=2026-03-31&basis=Accrual&tagId={_promo.Id}");
        Assert.Contains(tagged.Rows, r => r.Cells[0].Value as string == "Accrual");
        Assert.DoesNotContain(tagged.Rows, r => r.Cells[0].Value as string == "Landlord");
    }

    [Fact]
    public async Task Budgets_for_a_class_compare_with_that_class_only()
    {
        await Invoice(Widgets(2, _retail), Widgets(5, _online));
        var budget = await In<BudgetService, Domain.Reporting.Budget>(s => s.CreateAsync(new CreateBudgetCommand("Retail 2026", 2026, ClassId: _retail.Id)));
        var table = await In<StatementReports, ReportTable>(s => s.BudgetVsActualAsync(budget.Id, 1, 12, ReportBasis.Accrual));
        Assert.Equal(200m, table.Find("Net income", 1));
        Assert.Contains("Class: Retail", table.Subtitles[1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Tracking_settings_and_the_location_label_are_saved()
    {
        var tracking = await Seg(s => s.SetTrackingAsync(new SegmentTracking(true, true, "Department")));
        Assert.Equal(new SegmentTracking(true, true, "Department"), await Seg(s => s.GetTrackingAsync()));
        Assert.True(tracking.Classes);
        var byLocation = await Report("profit-and-loss-by-location", "preset=Custom&from=2026-03-01&to=2026-03-31");
        Assert.Equal("Profit and loss by department", byLocation.Title);
    }

    private Task<IReadOnlyList<Guid>> Tags(Guid entryId) =>
        _db.InDb<IReadOnlyList<Guid>>(async db => await db.EntryTags.AsNoTracking().Where(t => t.JournalEntryId == entryId).Select(t => t.TagId).ToListAsync());

    [Fact]
    public async Task Recurring_templates_keep_segments_and_give_them_to_what_they_make()
    {
        var journal = await In<RecurringEntryService, RecurringEntry>(s => s.CreateAsync(new RecurringEntryDetails("Accrual",
            [JournalLineDraft.Dr(_rent.Id, 10m, classId: _online.Id), JournalLineDraft.Cr(_checking.Id, 10m)], RecurrenceFrequency.Monthly, Mar10,
            MaxOccurrences: 1, LocationId: _east.Id, TagIds: [_promo.Id])));
        Assert.Equal([_promo.Id], journal.TagIds);
        var posted = Assert.Single(Assert.Single(await In<RecurringEntryService, IReadOnlyList<RecurringPostResult>>(s => s.PostDueAsync(Mar20, false))).Posted);
        var rows = await Segments(posted.Id);
        Assert.Equal(2, rows.Count);
        Assert.All(rows, r => Assert.Equal(_east.Id, r.LocationId));
        Assert.Single(rows, r => r.ClassId == _online.Id);
        Assert.Equal([_promo.Id], await Tags(posted.Id));

        await In<RecurringInvoiceService, RecurringInvoice>(s => s.CreateAsync(new RecurringInvoiceDetails("Monthly", _acme.Id,
            [Widgets(1, _retail)], RecurrenceFrequency.Monthly, Mar10, MaxOccurrences: 1, LocationId: _east.Id, TagIds: [_promo.Id])));
        var invoice = Assert.Single(Assert.Single(await In<RecurringInvoiceService, IReadOnlyList<RecurringInvoiceResult>>(s => s.CreateDueAsync(Mar20, false))).Created);
        Assert.Equal(_east.Id, invoice.LocationId);
        Assert.Equal(_retail.Id, Assert.Single(invoice.Lines).ClassId);
        Assert.Equal([_promo.Id], await Tags(invoice.JournalEntryId));

        await In<RecurringBillService, RecurringBill>(s => s.CreateAsync(new RecurringBillDetails("Rent", _landlord.Id,
            [new BillLineDraft(_rent.Id, 500m, ClassId: _retail.Id)], RecurrenceFrequency.Monthly, Mar10, MaxOccurrences: 1, LocationId: _east.Id, TagIds: [_promo.Id])));
        var bill = Assert.Single(Assert.Single(await In<RecurringBillService, IReadOnlyList<RecurringBillResult>>(s => s.CreateDueAsync(Mar20, false))).Created);
        Assert.Equal(_east.Id, bill.LocationId);
        Assert.Equal(_retail.Id, Assert.Single(bill.Lines).ClassId);
        Assert.Equal([_promo.Id], await Tags(bill.JournalEntryId));

        // Checked when the template is saved, not only when it runs.
        Assert.Equal("segment.kind_mismatch", (await Assert.ThrowsAsync<DomainException>(() => In<RecurringBillService, RecurringBill>(s => s.CreateAsync(
            new RecurringBillDetails("Bad", _landlord.Id, [new BillLineDraft(_rent.Id, 1m)], RecurrenceFrequency.Monthly, Mar10, LocationId: _retail.Id))))).Code);
    }

    [Fact]
    public async Task Journal_imports_take_class_location_and_tag_columns()
    {
        var web = await Seg(s => s.CreateAsync(SegmentKind.Class, "Web", _online.Id));
        const string head = "Entry,Date,Account,Debit,Credit,Class,Location,Tags\n";
        var csv = head
            + "1,2026-03-10,Rent,25,,online:web,East,Spring promo\n"
            + "1,2026-03-10,Checking,,25,,,\n";
        var result = await In<JournalImportService, JournalImportResult>(s => s.ImportEntriesAsync(csv));
        var imported = Assert.Single(result.Entries);
        Assert.Equal(_east.Id, imported.LocationId);

        var (entries, _) = await In<PostingEngine, (IReadOnlyList<JournalEntry>, int)>(e => e.ListAsync(new Application.JournalEntryFilter(Source: JournalEntry.ImportSource)));
        var entry = Assert.Single(entries);
        var rows = await Segments(entry.Id);
        Assert.Single(rows, r => r.ClassId == web.Id);
        Assert.All(rows, r => Assert.Equal(_east.Id, r.LocationId));
        Assert.Equal([_promo.Id], await Tags(entry.Id));

        var bad = await In<JournalImportService, JournalImportResult>(s => s.PreviewEntriesAsync(head
            + "1,2026-03-10,Rent,5,,Nowhere,East,\n"
            + "1,2026-03-10,Checking,,5,,Retail,Missing\n"));
        var error = Assert.Single(bad.Entries).Error!;
        Assert.Contains("no active class 'Nowhere'", error, StringComparison.Ordinal);
        Assert.Contains("no active location 'Retail'", error, StringComparison.Ordinal);
        Assert.Contains("no active tag 'Missing'", error, StringComparison.Ordinal);
    }
}
