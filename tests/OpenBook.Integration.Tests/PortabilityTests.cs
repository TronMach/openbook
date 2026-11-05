using System.IO.Compression;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OpenBook.Application;
using OpenBook.Application.Accounts;
using OpenBook.Application.Attachments;
using OpenBook.Application.Companies;
using OpenBook.Application.Import;
using OpenBook.Application.Import.Migration;
using OpenBook.Application.Ledger;
using OpenBook.Application.Notes;
using OpenBook.Application.Purchases;
using OpenBook.Application.Reporting;
using OpenBook.Application.Sales;
using OpenBook.Domain.Accounts;
using OpenBook.Domain.Auditing;
using OpenBook.Domain.Common;
using OpenBook.Domain.Ledger;
using OpenBook.Domain.Purchases;

namespace OpenBook.Integration.Tests;

public sealed class PortabilityTests : IAsyncLifetime
{
    private readonly LedgerDatabase _db = new();
    private Account _checking = null!;
    private Account _rent = null!;
    private Account _equity = null!;

    public async Task InitializeAsync()
    {
        await _db.InitializeAsync();
        _checking = await _db.CreateAccount("Checking", AccountType.Bank, "1000");
        _rent = await _db.CreateAccount("Rent", AccountType.Expense, "6500");
        _equity = await _db.CreateAccount("Owner's Equity", AccountType.Equity, "3000");
    }

    public Task DisposeAsync() => _db.DisposeAsync();

    private Task<T> Run<T>(Func<IServiceProvider, Task<T>> work) => _db.InScope(work);

    private Task<decimal> Balance(Guid accountId) =>
        Run(async sp => (await sp.GetRequiredService<ReportLedger>().LinesAsync(ReportBasis.Accrual, null, new DateOnly(2030, 1, 1))).Where(l => l.AccountId == accountId).Sum(l => l.Amount));

    [Fact]
    public async Task Journal_csv_groups_by_entry_number_and_posts_all_entries_as_imports()
    {
        const string csv = """
            Entry,Date,Account,Debit,Credit,Memo
            1,2026-01-31,Rent,"1,200.00",,January rent
            1,2026-01-31,Checking,,1200.00,
            2,2026-02-01,1000,500.00,,Owner contribution
            2,2026-02-01,Owner's Equity,,500.00,
            """;
        var preview = await Run(sp => sp.GetRequiredService<JournalImportService>().PreviewEntriesAsync(csv));
        Assert.True(preview.CanImport);
        Assert.Equal(["1", "2"], preview.Entries.Select(e => e.Key));
        Assert.Equal(1200m, preview.Entries[0].Total);
        Assert.Equal(0m, await Balance(_rent.Id));

        await Run(sp => sp.GetRequiredService<JournalImportService>().ImportEntriesAsync(csv));
        Assert.Equal(1200m, await Balance(_rent.Id));
        Assert.Equal(-700m, await Balance(_checking.Id));

        var (entries, _) = await Run(sp => sp.GetRequiredService<PostingEngine>().ListAsync(new JournalEntryFilter(Source: JournalEntry.ImportSource)));
        Assert.Equal(2, entries.Count);
        Assert.Single(entries.Select(e => e.SourceId).Distinct());
        Assert.Equal("January rent", entries.Single(e => e.Date == new DateOnly(2026, 1, 31)).Memo);

        // Imported entries are journal entries: voided from the journal.
        await Run(sp => sp.GetRequiredService<PostingEngine>().VoidAsync(entries[0].Id, "Wrong file"));
    }

    [Fact]
    public async Task A_whole_import_is_undone_at_once_or_not_at_all()
    {
        const string csv = """
            Entry,Date,Account,Debit,Credit
            1,2026-01-31,Rent,1200.00,
            1,2026-01-31,Checking,,1200.00
            2,2026-02-28,Rent,1200.00,
            2,2026-02-28,Checking,,1200.00
            3,2026-03-31,Rent,1200.00,
            3,2026-03-31,Checking,,1200.00
            """;
        var imports = (Func<IServiceProvider, JournalImportService>)(sp => sp.GetRequiredService<JournalImportService>());
        await Run(sp => imports(sp).ImportEntriesAsync(csv));
        await Run(sp => sp.GetRequiredService<PostingEngine>().PostAsync(new PostJournalEntryCommand(new DateOnly(2026, 2, 1),
            [new JournalLineDraft(_rent.Id, 50m, 0m), new JournalLineDraft(_checking.Id, 0m, 50m)])));
        var (entries, _) = await Run(sp => sp.GetRequiredService<PostingEngine>().ListAsync(new JournalEntryFilter(Source: JournalEntry.ImportSource)));
        var batch = entries[0].SourceId!.Value;
        Assert.Equal(3, (await Run(sp => imports(sp).BatchAsync(batch))).Count);

        // One entry already voided by hand: the rest are voided, the manual entry is left alone.
        await Run(sp => sp.GetRequiredService<PostingEngine>().VoidAsync(entries[0].Id, "Wrong month"));
        Assert.Equal(2, await Run(sp => imports(sp).VoidBatchAsync(batch, "Wrong file")));
        Assert.Equal(50m, await Balance(_rent.Id));
        // The batch now holds the three entries, all voided, and their three reversals.
        var after = await Run(sp => imports(sp).BatchAsync(batch));
        Assert.All(after.Where(e => e.Kind == JournalEntryKind.Standard), e => Assert.Equal(JournalEntryStatus.Voided, e.Status));
        Assert.Equal(3, after.Count(e => e.Kind == JournalEntryKind.Reversal));

        var again = await Assert.ThrowsAsync<DomainException>(() => Run(sp => imports(sp).VoidBatchAsync(batch, "Twice")));
        Assert.Equal("import.nothing_to_void", again.Code);
    }

    [Fact]
    public async Task Without_an_entry_column_lines_group_until_they_balance()
    {
        const string csv = """
            Date,Account,Amount,Description
            01/31/2026,Rent,100,
            01/31/2026,Checking,-60,
            01/31/2026,Checking,-40,
            02/28/2026,Rent,50,
            02/28/2026,Checking,-50,
            """;
        var preview = await Run(sp => sp.GetRequiredService<JournalImportService>().PreviewEntriesAsync(csv));
        Assert.Equal([3, 2], preview.Entries.Select(e => e.Lines.Count));
        Assert.Equal(new DateOnly(2026, 2, 28), preview.Entries[1].Date);
        Assert.True(preview.CanImport);
    }

    [Fact]
    public async Task Any_bad_entry_stops_the_whole_import()
    {
        const string csv = """
            Entry,Date,Account,Debit,Credit
            1,2026-01-31,Rent,100,
            1,2026-01-31,Checking,,100
            2,2026-01-31,Rent,100,
            2,2026-01-31,Nowhere,,90
            """;
        var preview = await Run(sp => sp.GetRequiredService<JournalImportService>().PreviewEntriesAsync(csv));
        Assert.True(preview.Entries[0].IsValid);
        Assert.Contains("no account 'Nowhere'", preview.Entries[1].Error, StringComparison.Ordinal);
        Assert.Contains("differ by 10.00", preview.Entries[1].Error, StringComparison.Ordinal);

        var ex = await Assert.ThrowsAsync<DomainException>(() => Run(sp => sp.GetRequiredService<JournalImportService>().ImportEntriesAsync(csv)));
        Assert.Equal("import.has_errors", ex.Code);
        Assert.Equal(0m, await Balance(_rent.Id));
    }

    [Fact]
    public async Task Opening_balances_import_in_each_accounts_normal_direction()
    {
        var card = await _db.CreateAccount("Visa", AccountType.CreditCard);
        await _db.CreateAccount("Accounts Receivable", AccountType.AccountsReceivable);
        const string csv = """
            Account,Balance
            Checking,1000
            Visa,250
            """;
        var entry = await Run(sp => sp.GetRequiredService<JournalImportService>().ImportBalancesAsync(csv, new DateOnly(2025, 12, 31)));
        Assert.Equal(JournalEntry.OpeningBalanceSource, entry.Source);
        Assert.Equal(1000m, await Balance(_checking.Id));
        Assert.Equal(-250m, await Balance(card.Id)); // a credit: owed on the card

        var refused = await Run(sp => sp.GetRequiredService<JournalImportService>().PreviewBalancesAsync("Account,Debit,Credit\nAccounts Receivable,10,\nRent,5,\n"));
        Assert.Contains("open invoices", refused.Rows[0].Error, StringComparison.Ordinal);
        Assert.Contains("balance sheet", refused.Rows[1].Error, StringComparison.Ordinal);
        Assert.False(refused.CanImport);
    }

    private static string Iif(params string[] lines) => string.Join("\r\n", lines.Select(l => l.Replace('|', '\t')));

    private static readonly string Sample = Iif(
        "!ACCNT|NAME|ACCNTTYPE|DESC|ACCNUM",
        "ACCNT|Business Checking|BANK|Main account|1010",
        "ACCNT|Consulting Income|INC||4010",
        "ACCNT|Office|EXP||",
        "ACCNT|Office:Supplies|EXP||",
        "ACCNT|Estimates|NONPOSTING||",
        "ACCNT|Rent|EXP||",
        "!CUST|NAME|COMPANYNAME|EMAIL|BADDR1|BADDR2",
        "CUST|Acme Ltd|Acme Limited|ap@acme.test|1 Main St|Springfield",
        "!VEND|NAME|ADDR1|1099",
        "VEND|Office Depot|PO Box 1|N",
        "VEND|Jane Contractor||Y",
        "!INVITEM|NAME|INVITEMTYPE|DESC|PRICE|ACCNT",
        "INVITEM|Consulting|SERV|Hourly consulting|150.00|Consulting Income",
        "INVITEM|Discount|DISC|||Consulting Income",
        "!TRNS|TRNSID|TRNSTYPE|DATE|ACCNT|NAME|AMOUNT|DOCNUM|MEMO",
        "!SPL|SPLID|TRNSTYPE|DATE|ACCNT|NAME|AMOUNT|DOCNUM|MEMO",
        "!ENDTRNS",
        "TRNS||DEPOSIT|1/15/2026|Business Checking||900.00|D1|Deposit",
        "SPL||DEPOSIT|1/15/2026|Consulting Income|Acme Ltd|-900.00||",
        "ENDTRNS",
        "TRNS||CHECK|1/20/2026|Business Checking|Office Depot|-75.50|101|",
        "SPL||CHECK|1/20/2026|Office:Supplies|Office Depot|75.50||Paper",
        "ENDTRNS",
        "TRNS||ESTIMATE|1/21/2026|Estimates|Acme Ltd|500.00|E1|",
        "SPL||ESTIMATE|1/21/2026|Consulting Income|Acme Ltd|-500.00||",
        "ENDTRNS",
        "!CLASS|NAME",
        "CLASS|West");

    [Fact]
    public async Task Iif_import_brings_in_lists_and_transactions()
    {
        var preview = await Run(sp => sp.GetRequiredService<IifImportService>().PreviewAsync(Sample));
        Assert.Equal(0, preview.ErrorCount);
        Assert.Equal(4, preview.Count(MigrationKind.Account, MigrationRowStatus.New));
        Assert.Equal(1, preview.Count(MigrationKind.Account, MigrationRowStatus.Exists)); // Rent
        Assert.Equal(1, preview.Count(MigrationKind.Account, MigrationRowStatus.Skipped)); // non-posting
        Assert.Equal((1, 1), (preview.Count(MigrationKind.Item, MigrationRowStatus.New), preview.Count(MigrationKind.Item, MigrationRowStatus.Skipped)));
        Assert.Equal((2, 1), (preview.Count(MigrationKind.Transaction, MigrationRowStatus.New), preview.Count(MigrationKind.Transaction, MigrationRowStatus.Skipped)));
        Assert.Contains(preview.Warnings, w => w.Contains("CLASS", StringComparison.Ordinal));

        await Run(sp => sp.GetRequiredService<IifImportService>().ImportAsync(Sample));

        var chart = await Run(sp => sp.GetRequiredService<AccountService>().GetChartAsync());
        var supplies = chart.FindByFullName("Office:Supplies")!;
        Assert.Equal(chart.FindByFullName("Office")!.Id, supplies.ParentId);
        Assert.Equal("1010", chart.FindByFullName("Business Checking")!.Number);
        Assert.Equal(824.50m, await Balance(chart.FindByFullName("Business Checking")!.Id));
        Assert.Equal(75.50m, await Balance(supplies.Id));

        var customer = (await Run(sp => sp.GetRequiredService<CustomerService>().ListAsync(new DateOnly(2026, 1, 31)))).Single(c => c.Customer.DisplayName == "Acme Ltd").Customer;
        Assert.Equal("1 Main St\nSpringfield", customer.BillingAddress);
        var vendors = await Run(sp => sp.GetRequiredService<VendorService>().ListAsync(new DateOnly(2026, 1, 31)));
        Assert.True(vendors.Single(v => v.Vendor.DisplayName == "Jane Contractor").Vendor.Track1099);
        var item = (await Run(sp => sp.GetRequiredService<ItemService>().ListAsync())).Single();
        Assert.Equal((150m, chart.FindByFullName("Consulting Income")!.Id), (item.Rate, item.IncomeAccountId));

        // Importing the same file again: everything already exists, transactions included.
        var again = await Run(sp => sp.GetRequiredService<IifImportService>().PreviewAsync(Sample));
        Assert.Equal(0, again.Count(MigrationKind.Account, MigrationRowStatus.New));
        Assert.Equal(1, again.Count(MigrationKind.Customer, MigrationRowStatus.Exists));
        Assert.Equal(0, again.Count(MigrationKind.Transaction, MigrationRowStatus.New));
        Assert.True(again.Count(MigrationKind.Transaction, MigrationRowStatus.Exists) > 0);
        Assert.Contains(again.Warnings, w => w.Contains("earlier import", StringComparison.Ordinal));
        Assert.False(again.CanImport);
    }

    private static string Deposits(params string[] rows) => Iif(
        [
            "!ACCNT|NAME|ACCNTTYPE", "ACCNT|Business Checking|BANK", "ACCNT|Consulting Income|INC",
            "!TRNS|TRNSID|TRNSTYPE|DATE|ACCNT|NAME|AMOUNT|DOCNUM|MEMO", "!SPL|SPLID|TRNSTYPE|DATE|ACCNT|NAME|AMOUNT|DOCNUM|MEMO", "!ENDTRNS",
            .. rows.SelectMany(r => r.Split(';')),
        ]);

    private static string Deposit(string date, string amount) =>
        $"TRNS||DEPOSIT|{date}|Business Checking||{amount}|D1|Deposit;SPL||DEPOSIT|{date}|Consulting Income||-{amount}||;ENDTRNS";

    private Task<decimal> CheckingBalance() => Run(async sp =>
    {
        var chart = await sp.GetRequiredService<AccountService>().GetChartAsync();
        return await Balance(chart.FindByFullName("Business Checking")!.Id);
    });

    [Fact]
    public async Task Importing_a_file_twice_adds_nothing_the_second_time()
    {
        var file = Deposits(Deposit("1/15/2026", "900.00"), Deposit("1/16/2026", "100.00"));
        await Run(sp => sp.GetRequiredService<IifImportService>().ImportAsync(file));

        var refused = await Assert.ThrowsAsync<DomainException>(() => Run(sp => sp.GetRequiredService<IifImportService>().ImportAsync(file)));

        Assert.Equal("import.nothing_new", refused.Code);
        Assert.Equal(1_000m, await CheckingBalance());
    }

    [Fact]
    public async Task A_second_file_brings_in_only_what_is_new()
    {
        await Run(sp => sp.GetRequiredService<IifImportService>().ImportAsync(Deposits(Deposit("1/15/2026", "900.00"))));

        await Run(sp => sp.GetRequiredService<IifImportService>().ImportAsync(Deposits(Deposit("1/15/2026", "900.00"), Deposit("2/1/2026", "50.00"))));

        Assert.Equal(950m, await CheckingBalance());
    }

    [Fact]
    public async Task Two_identical_transactions_in_a_file_both_come_in_once()
    {
        var file = Deposits(Deposit("1/15/2026", "25.00"), Deposit("1/15/2026", "25.00"));
        await Run(sp => sp.GetRequiredService<IifImportService>().ImportAsync(file));
        Assert.Equal(50m, await CheckingBalance());

        var again = await Run(sp => sp.GetRequiredService<IifImportService>().PreviewAsync(file));

        Assert.Equal(2, again.Count(MigrationKind.Transaction, MigrationRowStatus.Exists));
    }

    [Fact]
    public async Task An_import_undone_can_come_in_again()
    {
        var file = Deposits(Deposit("1/15/2026", "900.00"));
        await Run(sp => sp.GetRequiredService<IifImportService>().ImportAsync(file));
        var batch = await _db.InDb(db => db.JournalEntries.Where(e => e.Source == JournalEntry.ImportSource).Select(e => e.SourceId!.Value).FirstAsync());
        await Run(sp => sp.GetRequiredService<JournalImportService>().VoidBatchAsync(batch, "Wrong file"));
        Assert.Equal(0m, await CheckingBalance());

        await Run(sp => sp.GetRequiredService<IifImportService>().ImportAsync(file));

        Assert.Equal(900m, await CheckingBalance());
    }

    [Fact]
    public async Task A_journal_csv_imported_twice_adds_nothing_the_second_time()
    {
        var csv = "Date,Account,Debit,Credit,Memo\n2026-01-15,Checking,100,,Owner money\n2026-01-15,Owner's Equity,,100,Owner money\n";
        await Run(sp => sp.GetRequiredService<JournalImportService>().ImportEntriesAsync(csv));

        var again = await Run(sp => sp.GetRequiredService<JournalImportService>().PreviewEntriesAsync(csv));

        Assert.Empty(again.Entries);
        Assert.False(again.CanImport);
        Assert.Contains(again.Warnings, w => w.Contains("earlier import", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Iif_errors_are_reported_and_nothing_is_imported()
    {
        var bad = Iif(
            "!ACCNT|NAME|ACCNTTYPE",
            "ACCNT|Savings|BANK",
            "ACCNT|Orphan:Child|EXP",
            "ACCNT|Odd|WEIRD",
            "!TRNS|TRNSTYPE|DATE|ACCNT|AMOUNT",
            "!SPL|TRNSTYPE|DATE|ACCNT|AMOUNT",
            "TRNS|GENERAL JOURNAL|13/45/2026|Savings|10",
            "SPL|GENERAL JOURNAL|13/45/2026|Rent|-9",
            "ENDTRNS");
        var preview = await Run(sp => sp.GetRequiredService<IifImportService>().PreviewAsync(bad));
        var errors = preview.Rows.Where(r => r.Status == MigrationRowStatus.Error).ToList();
        Assert.Contains(errors, r => r.Name == "Orphan:Child" && r.Message!.Contains("parent", StringComparison.Ordinal));
        Assert.Contains(errors, r => r.Name == "Odd" && r.Message!.Contains("WEIRD", StringComparison.Ordinal));
        Assert.Contains(errors, r => r.Kind == MigrationKind.Transaction && r.Message!.Contains("can't be read", StringComparison.Ordinal) && r.Message.Contains("don't balance", StringComparison.Ordinal));

        await Assert.ThrowsAsync<DomainException>(() => Run(sp => sp.GetRequiredService<IifImportService>().ImportAsync(bad)));
        Assert.Null((await Run(sp => sp.GetRequiredService<AccountService>().GetChartAsync())).FindByFullName("Savings"));

        var notIif = await Assert.ThrowsAsync<DomainException>(() => Run(sp => sp.GetRequiredService<IifImportService>().PreviewAsync("Name,Type\nA,B")));
        Assert.Equal("import.not_iif", notIif.Code);
    }

    [Fact]
    public async Task A_unit_of_work_keeps_nothing_unless_committed()
    {
        await Run(async sp =>
        {
            await using var transaction = await sp.GetRequiredService<IUnitOfWork>().BeginAsync(default);
            await sp.GetRequiredService<AccountService>().CreateAsync(new CreateAccountCommand("Temporary", AccountType.Expense));
            return 0; // disposed without committing
        });
        Assert.Null((await Run(sp => sp.GetRequiredService<AccountService>().GetChartAsync())).FindByFullName("Temporary"));
    }

    [Fact]
    public async Task The_export_has_every_table_documented_without_secrets()
    {
        await _db.InScope(sp => sp.GetRequiredService<PostingEngine>().PostAsync(new PostJournalEntryCommand(
            new DateOnly(2026, 1, 5), [JournalLineDraft.Dr(_rent.Id, 12.34m), JournalLineDraft.Cr(_checking.Id, 12.34m)], "Exported, with a comma")));
        var vendor = await Run(sp => sp.GetRequiredService<VendorService>().CreateAsync(new VendorDetails("Taxed Vendor", Track1099: true, TaxId: "12-3456789")));
        await Run(sp => sp.GetRequiredService<AttachmentService>().AddAsync("vendor", vendor.Id, "w9.pdf", new MemoryStream("%PDF-1.4 test"u8.ToArray())));

        var file = await Run(sp => sp.GetRequiredService<IDataExporter>().ExportAsync(default));
        try
        {
            Assert.EndsWith(".zip", file.FileName, StringComparison.Ordinal);
            using var zip = ZipFile.OpenRead(file.Path);
            string Read(string name)
            {
                using var reader = new StreamReader(zip.GetEntry(name)!.Open());
                return reader.ReadToEnd().TrimStart('﻿');
            }

            var names = zip.Entries.Select(e => e.FullName).ToHashSet();
            Assert.Contains("README.md", names);
            Assert.Contains("JournalLines.csv", names);
            Assert.Contains("AuditRecords.csv", names);
            Assert.Contains(names, n => n.StartsWith("attachments/", StringComparison.Ordinal) && n.EndsWith("-w9.pdf", StringComparison.Ordinal));

            var lines = Read("JournalLines.csv");
            Assert.Contains("AmountMinor", lines.Split('\n')[0], StringComparison.Ordinal);
            Assert.Contains("1234", lines, StringComparison.Ordinal); // cents
            Assert.Contains("\"Exported, with a comma\"", Read("JournalEntries.csv"), StringComparison.Ordinal);

            var users = Read("Users.csv");
            Assert.DoesNotContain("PasswordHash", users, StringComparison.Ordinal);
            Assert.Contains(LedgerDatabase.OwnerUsername, users, StringComparison.Ordinal);
            Assert.DoesNotContain("123456789", Read("Vendors.csv"), StringComparison.Ordinal);
            Assert.DoesNotContain("Data", Read("AttachmentContents.csv").Split('\n')[0], StringComparison.Ordinal);

            var readme = Read("README.md");
            Assert.Contains("### JournalLines.csv", readme, StringComparison.Ordinal);
            Assert.Contains("| `AmountMinor` | integer | cents", readme, StringComparison.Ordinal);
            Assert.Contains("→ `Accounts`", readme, StringComparison.Ordinal);
            Assert.Contains("`Vendors`: `TaxId`", readme, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(file.Path);
        }
    }

    [Fact]
    public async Task Notes_go_on_any_record_and_are_audited_there()
    {
        var notes = (Func<IServiceProvider, NoteService>)(sp => sp.GetRequiredService<NoteService>());
        var note = await Run(sp => notes(sp).AddAsync("account", _rent.Id, "  Lease renews in March.\r\nCall the landlord.  "));
        Assert.Equal("Lease renews in March.\nCall the landlord.", note.Text);
        Assert.Equal(LedgerDatabase.OwnerUsername, note.CreatedBy);
        Assert.Single(await Run(sp => notes(sp).ListAsync("account", _rent.Id)));

        await Run(sp => notes(sp).RemoveAsync(note.Id));
        Assert.Empty(await Run(sp => notes(sp).ListAsync("account", _rent.Id)));
        var audit = await Run(sp => sp.GetRequiredService<ILedgerStore>().ListAuditAsync("account", _rent.Id, null, default));
        Assert.Equal([AuditAction.NoteRemoved, AuditAction.NoteAdded], audit.Take(2).Select(a => a.Action));

        Assert.Equal("note.entity_type", (await Assert.ThrowsAsync<DomainException>(() => Run(sp => notes(sp).AddAsync("budget", _rent.Id, "x")))).Code);
        await Assert.ThrowsAsync<EntityNotFoundException>(() => Run(sp => notes(sp).AddAsync("invoice", Guid.NewGuid(), "x")));
        Assert.Equal("note.empty", (await Assert.ThrowsAsync<DomainException>(() => Run(sp => notes(sp).AddAsync("account", _rent.Id, "   ")))).Code);
    }
}
