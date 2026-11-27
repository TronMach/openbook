using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OpenBook.Application;
using OpenBook.Application.Accounts;
using OpenBook.Application.Attachments;
using OpenBook.Application.Banking;
using OpenBook.Application.Purchases;
using OpenBook.Application.Segments;
using OpenBook.Application.Settlements;
using OpenBook.Domain.Accounts;
using OpenBook.Domain.Auditing;
using OpenBook.Domain.Banking;
using OpenBook.Domain.Common;
using OpenBook.Domain.Purchases;
using OpenBook.Domain.Segments;
using OpenBook.Domain.Settlements;

namespace OpenBook.Integration.Tests;

/// <summary>Receipts typed in by hand: an owner-operator's on the road (fuel with gallons and state), and any paper receipt in Purchases.</summary>
public sealed class TruckReceiptTests : IAsyncLifetime
{
    private static readonly DateOnly Day = new(2026, 3, 10);

    private readonly LedgerDatabase _db = new();
    private Account _checking = null!;
    private Account _card = null!;
    private ChartOfAccounts _chart = null!;
    private Segment _truck = null!;

    public async Task InitializeAsync()
    {
        await _db.InitializeAsync();
        _checking = await _db.CreateAccount("Checking", AccountType.Bank, "1000");
        _card = await _db.CreateAccount("Fuel Card", AccountType.CreditCard, "2100");
        await In<ChartTemplates, ChartImportResult>(s => s.AddTruckingAsync());
        _chart = await In<AccountService, ChartOfAccounts>(s => s.GetChartAsync());
        _truck = await In<SegmentService, Segment>(s => s.CreateAsync(SegmentKind.Class, "Truck 4411"));
    }

    public Task DisposeAsync() => _db.DisposeAsync();

    private Task<T> In<TService, T>(Func<TService, Task<T>> work) where TService : notnull => _db.InScope(sp => work(sp.GetRequiredService<TService>()));

    private Task<List<AuditRecord>> Audit(Guid id) =>
        _db.InDb(db => db.AuditRecords.AsNoTracking().Where(r => r.EntityType == TruckReceiptService.EntityType && r.EntityId == id).OrderBy(r => r.Id).ToListAsync());

    private Guid Account(string name) => _chart.FindByFullName(name)?.Id ?? throw new InvalidOperationException($"No account '{name}'.");

    private static async Task<string> Refused(Func<Task> action) => (await Assert.ThrowsAnyAsync<DomainException>(action)).Code;

    private TruckReceiptCommand Fuel(decimal amount = 412.50m, decimal? gallons = 110m, string? state = "TX") =>
        new(TruckReceiptKind.Fuel, Day, amount, _card.Id, ClassId: _truck.Id, Payee: "Pilot #612", Gallons: gallons, Jurisdiction: state, City: "Amarillo", Number: "88231");

    private Task<TruckReceipt> Record(TruckReceiptCommand command, string? fileName = null, byte[]? photo = null) =>
        In<TruckReceiptService, TruckReceipt>(s => s.RecordAsync(command, fileName, photo is null ? null : new MemoryStream(photo)));

    private Task<TruckReceiptView> View(Guid id) => In<TruckReceiptService, TruckReceiptView>(async s => (await s.GetAsync(id))!);

    private Task<IReadOnlyList<BankTransaction>> Spends() =>
        _db.InScope(async sp => (IReadOnlyList<BankTransaction>)[.. (await sp.GetRequiredService<BankingService>().GetRegisterAsync(_card.Id)).Lines.Select(l => l.Document).OfType<BankTransaction>()]);

    [Fact]
    public async Task A_fuel_receipt_is_an_expense_on_the_card_with_the_truck_and_keeps_its_gallons_and_state()
    {
        var receipt = await Record(Fuel());
        var view = await View(receipt.Id);

        Assert.Equal((TruckReceiptKind.Fuel, 110m, "TX", "Amarillo", "88231"), (receipt.Kind, receipt.Gallons, receipt.Jurisdiction, receipt.City, receipt.Number));
        Assert.Equal((BankTransactionKind.Spend, _card.Id, Day, 412.50m, "Pilot #612"), (view.Spend.Kind, view.Spend.AccountId, view.Date, view.Amount, view.Spend.Payee));
        // No account chosen: the trucking chart's Fuel.
        Assert.Equal((Account("Fuel"), _truck.Id), (view.AccountId, view.ClassId));
        Assert.Equal(3.750m, view.PricePerGallon);
        Assert.False(view.IsVoided);

        var splits = await _db.InScope(async sp => await sp.GetRequiredService<BankingService>().SplitsAsync(view.Spend));
        Assert.Equal("Fuel · 110 gal · Amarillo, TX", Assert.Single(splits).Memo);
        Assert.Equal(receipt.Id, (await In<TruckReceiptService, TruckReceipt?>(s => s.ForTransactionAsync(view.Spend.Id)))!.Id);

        var audit = await Audit(receipt.Id);
        Assert.Equal(AuditAction.Created, Assert.Single(audit).Action);
        Assert.Contains("\"Gallons\":110", audit[0].After, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Fuel_by_state_adds_up_the_fuel_receipts_that_stand_in_the_period()
    {
        await Record(Fuel(400m, 100m, "TX"));
        await Record(Fuel(150m, 40m, "tx") with { Date = Day.AddDays(1) });
        var oklahoma = await Record(Fuel(300m, 80m, "Oklahoma"));
        await Record(Fuel(90m, 25m, "NM") with { Date = Day.AddMonths(1) });
        // Diesel exhaust fluid and a scale ticket aren't fuel.
        await Record(new TruckReceiptCommand(TruckReceiptKind.Def, Day, 30m, _card.Id, Gallons: 2.5m, Jurisdiction: "TX"));
        await Record(new TruckReceiptCommand(TruckReceiptKind.Scale, Day, 13.75m, _card.Id, Jurisdiction: "TX"));
        var voided = await Record(Fuel(200m, 50m, "TX"));
        await In<TruckReceiptService, TruckReceiptView>(s => s.VoidAsync(voided.Id, "Entered twice"));

        var fuel = await In<TruckReceiptService, IReadOnlyList<FuelByJurisdiction>>(s => s.FuelByJurisdictionAsync(new DateOnly(2026, 3, 1), new DateOnly(2026, 3, 31)));
        Assert.Equal([new("OK", 1, 80m, 300m), new("TX", 2, 140m, 550m)], fuel);
        Assert.Equal("OK", (await View(oklahoma.Id)).Receipt.Jurisdiction);
        Assert.Equal(6, (await In<TruckReceiptService, IReadOnlyList<TruckReceiptView>>(s => s.ListAsync(new DateOnly(2026, 3, 1), new DateOnly(2026, 3, 31)))).Count);
    }

    [Fact]
    public async Task What_a_receipt_needs_is_checked_before_anything_is_recorded()
    {
        Assert.Equal("truck_receipt.gallons_required", await Refused(() => Record(Fuel(gallons: null))));
        Assert.Equal("truck_receipt.jurisdiction_required", await Refused(() => Record(Fuel(state: null))));
        Assert.Equal("truck_receipt.jurisdiction_unknown", await Refused(() => Record(Fuel(state: "ZZ"))));
        Assert.Equal("truck_receipt.gallons_range", await Refused(() => Record(Fuel(gallons: 0m))));
        Assert.Equal("truck_receipt.amount_not_positive", await Refused(() => Record(Fuel(amount: 0m))));
        Assert.Equal("bank.not_banking_account", await Refused(() => Record(Fuel() with { PaidFromAccountId = Account("Fuel") })));
        Assert.Equal("truck_receipt.account_is_bank", await Refused(() => Record(Fuel() with { AccountId = _checking.Id })));
        // "Other" has no usual account until one is chosen.
        Assert.Equal("truck_receipt.account_required", await Refused(() => Record(new TruckReceiptCommand(TruckReceiptKind.Other, Day, 20m, _card.Id))));
        Assert.Equal("attachment.type_not_allowed", await Refused(() => Record(Fuel(), "virus.exe", [1, 2, 3])));
        Assert.Empty(await Spends());
        Assert.Empty(await In<TruckReceiptService, IReadOnlyList<TruckReceiptView>>(s => s.ListAsync()));
    }

    [Fact]
    public async Task Gallons_are_kept_only_for_what_is_bought_by_the_gallon()
    {
        var scale = await Record(new TruckReceiptCommand(TruckReceiptKind.Scale, Day, 13.75m, _card.Id, Gallons: 5m));
        Assert.Null(scale.Gallons);
        Assert.Equal(Account("Tolls and Scales"), (await View(scale.Id)).AccountId);
        var def = await Record(new TruckReceiptCommand(TruckReceiptKind.Def, Day, 30m, _card.Id, Gallons: 2.5m));
        Assert.Equal((2.5m, Account("Diesel Exhaust Fluid")), (def.Gallons, (await View(def.Id)).AccountId));
    }

    [Fact]
    public async Task A_kind_goes_where_its_last_receipt_went_and_a_new_one_starts_on_the_last_truck()
    {
        var shop = await _db.CreateAccount("Road Service", AccountType.Expense, "6935");
        await Record(new TruckReceiptCommand(TruckReceiptKind.Repair, Day, 250m, _checking.Id, shop.Id, _truck.Id));
        var next = await Record(new TruckReceiptCommand(TruckReceiptKind.Repair, Day, 80m, _checking.Id));
        Assert.Equal(shop.Id, (await View(next.Id)).AccountId);
        Assert.Equal(shop.Id, await In<TruckReceiptService, Guid?>(s => s.UsualAccountAsync(TruckReceiptKind.Repair)));
        // The latest receipt has no class: a new one starts on none.
        Assert.Null(await In<TruckReceiptService, Guid?>(s => s.UsualClassAsync()));
        await Record(Fuel());
        Assert.Equal(_truck.Id, await In<TruckReceiptService, Guid?>(s => s.UsualClassAsync()));

        // The form's defaults, from one reading.
        var defaults = await In<TruckReceiptService, TruckReceiptDefaults>(s => s.DefaultsAsync());
        Assert.Equal((shop.Id, Account("Fuel"), _truck.Id, _card.Id),
            (defaults.Accounts[TruckReceiptKind.Repair], defaults.Accounts[TruckReceiptKind.Fuel], defaults.ClassId, defaults.PaidFromAccountId));
        Assert.False(defaults.Accounts.ContainsKey(TruckReceiptKind.Other));
    }

    [Fact]
    public async Task Voiding_a_receipt_voids_its_expense_and_one_voided_in_banking_shows_as_voided()
    {
        var receipt = await Record(Fuel());
        var voided = await In<TruckReceiptService, TruckReceiptView>(s => s.VoidAsync(receipt.Id, "Entered twice"));
        Assert.True(voided.IsVoided);
        Assert.Equal(BankTransactionStatus.Voided, voided.Spend.Status);
        Assert.Equal("truck_receipt.voided", await Refused(() => In<TruckReceiptService, TruckReceiptView>(s => s.VoidAsync(receipt.Id, null))));
        var audit = await Audit(receipt.Id);
        Assert.Contains(audit, a => a.Action == AuditAction.Voided);

        var other = await Record(Fuel(100m, 25m));
        await In<BankingService, BankTransaction>(async s => await s.VoidAsync((await View(other.Id)).Spend.Id, "Card refunded it"));
        Assert.True((await View(other.Id)).IsVoided);
    }

    [Fact]
    public async Task A_photo_goes_on_the_expense()
    {
        var receipt = await Record(Fuel(), "pilot.jpg", [0xFF, 0xD8, 0xFF, 0xE0]);
        var spend = (await View(receipt.Id)).Spend;
        var files = await In<AttachmentService, IReadOnlyList<OpenBook.Domain.Attachments.Attachment>>(s => s.ListAsync("bank_transaction", spend.Id));
        Assert.Equal("pilot.jpg", Assert.Single(files).FileName);
    }

    [Fact]
    public async Task An_uploaded_receipt_can_be_recorded_as_a_trucking_receipt()
    {
        var text = "PILOT TRAVEL CENTER 612\n03/10/2026\nDIESEL 110.000 GAL\nTotal 412.50\n";
        var upload = await In<ReceiptService, ReceiptCapture>(s => s.UploadAsync("pilot.txt", new MemoryStream(Encoding.UTF8.GetBytes(text))));
        var receipt = await Record(Fuel() with { ReceiptId = upload.Id, Payee = null });

        var view = await View(receipt.Id);
        Assert.Equal("PILOT TRAVEL CENTER 612", view.Spend.Payee);
        var after = (await In<ReceiptService, ReceiptCapture?>(s => s.GetAsync(upload.Id)))!;
        Assert.Equal((ReceiptStatus.Recorded, view.Spend.Id), (after.Status, after.BankTransactionId));
        var files = await In<AttachmentService, IReadOnlyList<OpenBook.Domain.Attachments.Attachment>>(s => s.ListAsync("bank_transaction", view.Spend.Id));
        Assert.Equal("pilot.txt", Assert.Single(files).FileName);
        Assert.Equal("receipt.done", await Refused(() => Record(Fuel() with { ReceiptId = upload.Id })));
        Assert.Single(await In<TruckReceiptService, IReadOnlyList<TruckReceiptView>>(s => s.ListAsync()));
    }

    [Fact]
    public async Task A_paper_receipt_typed_in_under_purchases_is_recorded_at_once()
    {
        var supplies = await _db.CreateAccount("Office Supplies", AccountType.Expense, "6100");
        var spend = await In<ReceiptService, BankTransaction>(s => s.EnterAsync(new RecordReceiptCommand(_checking.Id, supplies.Id, Day, 18.40m, Payee: "Corner Hardware", Memo: "Rope")));
        Assert.Equal((BankTransactionKind.Spend, 18.40m, "Corner Hardware", "Rope"), (spend.Kind, spend.Amount, spend.Payee, spend.Memo));
        var typed = Assert.Single(await In<ReceiptService, IReadOnlyList<ReceiptCapture>>(s => s.ListAsync()));
        Assert.Equal((string.Empty, ReceiptStatus.Recorded, spend.Id, 18.40m, "Corner Hardware"), (typed.FileName, typed.Status, typed.BankTransactionId, typed.Total, typed.VendorName));

        // With a photo, the photo is on the receipt and on the expense.
        var photographed = await In<ReceiptService, BankTransaction>(s => s.EnterAsync(new RecordReceiptCommand(_checking.Id, supplies.Id, Day, 9m), "till.jpg", new MemoryStream([0xFF, 0xD8, 0xFF, 0xE0])));
        var files = await In<AttachmentService, IReadOnlyList<OpenBook.Domain.Attachments.Attachment>>(s => s.ListAsync("bank_transaction", photographed.Id));
        Assert.Equal("till.jpg", Assert.Single(files).FileName);

        // A refusal leaves nothing behind.
        Assert.Equal("receipt.category_required", await Refused(() => In<ReceiptService, BankTransaction>(s => s.EnterAsync(new RecordReceiptCommand(_checking.Id, null, Day, 5m)))));
        Assert.Equal(2, (await In<ReceiptService, IReadOnlyList<ReceiptCapture>>(s => s.ListAsync())).Count);
    }
}
