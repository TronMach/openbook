using System.IO.Compression;
using System.Net;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OpenBook.Application.Accounts;
using OpenBook.Application.Companies;
using OpenBook.Application.Import.Migration;
using OpenBook.Domain.Accounts;
using OpenBook.Domain.Common;
using OpenBook.Domain.Companies;
using OpenBook.Domain.Ledger;
using OpenBook.Infrastructure.Backups;

namespace OpenBook.Integration.Tests;

/// <summary>L8: moving in from GnuCash, QuickBooks (qbXML, Online), Xero, Wave and FreshBooks; scheduled backups.</summary>
public sealed class MigrationFlowTests : IAsyncLifetime
{
    private readonly LedgerDatabase _db = new();

    public Task InitializeAsync() => _db.InitializeAsync();

    public Task DisposeAsync() => _db.DisposeAsync();

    [Fact]
    public async Task An_item_that_posts_elsewhere_is_left_out_and_stops_nothing()
    {
        const string iif = "!ACCNT\tNAME\tACCNTTYPE\n"
            + "ACCNT\tRetainage\tOCASSET\n"
            + "ACCNT\tSales\tINC\n"
            + "!INVITEM\tNAME\tINVITEMTYPE\tACCNT\tPRICE\n"
            + "INVITEM\tRetainer\tSERV\tRetainage\t0\n"
            + "INVITEM\tConsulting\tSERV\tSales\t100\n";

        var preview = await Preview(MigrationSource.QuickBooksIif, iif);

        Assert.Equal(0, preview.ErrorCount);
        Assert.Equal(MigrationRowStatus.Skipped, preview.Rows.Single(r => r.Name == "Retainer").Status);
        Assert.Equal(MigrationRowStatus.New, preview.Rows.Single(r => r.Name == "Consulting").Status);
    }

    private Task<MigrationResult> Preview(MigrationSource source, string content) =>
        _db.InScope(sp => sp.GetRequiredService<MigrationImportService>().PreviewAsync(source, Encoding.UTF8.GetBytes(content)));

    private Task<MigrationResult> Import(MigrationSource source, byte[] content) =>
        _db.InScope(sp => sp.GetRequiredService<MigrationImportService>().ImportAsync(source, content));

    private Task<ChartOfAccounts> Chart() => _db.InScope(sp => sp.GetRequiredService<AccountService>().GetChartAsync());

    private Task<List<JournalEntry>> Imported() =>
        _db.InDb(db => db.JournalEntries.AsNoTracking().Include(e => e.Lines).Where(e => e.Source == JournalEntry.ImportSource).ToListAsync());

    private const string GnuCash = """
        <?xml version="1.0" encoding="utf-8" ?>
        <gnc-v2 xmlns:gnc="http://www.gnucash.org/XML/gnc" xmlns:act="http://www.gnucash.org/XML/act" xmlns:book="http://www.gnucash.org/XML/book"
                xmlns:cmdty="http://www.gnucash.org/XML/cmdty" xmlns:trn="http://www.gnucash.org/XML/trn" xmlns:split="http://www.gnucash.org/XML/split"
                xmlns:ts="http://www.gnucash.org/XML/ts" xmlns:slot="http://www.gnucash.org/XML/slot">
        <gnc:book version="2.0.0">
          <gnc:account version="2.0.0"><act:name>Root Account</act:name><act:id type="guid">root</act:id><act:type>ROOT</act:type></gnc:account>
          <gnc:account version="2.0.0"><act:name>Assets</act:name><act:id type="guid">assets</act:id><act:type>ASSET</act:type><act:parent type="guid">root</act:parent></gnc:account>
          <gnc:account version="2.0.0"><act:name>Checking</act:name><act:id type="guid">checking</act:id><act:type>BANK</act:type><act:code>1010</act:code><act:parent type="guid">assets</act:parent></gnc:account>
          <gnc:account version="2.0.0"><act:name>Expenses</act:name><act:id type="guid">expenses</act:id><act:type>EXPENSE</act:type><act:parent type="guid">root</act:parent></gnc:account>
          <gnc:account version="2.0.0"><act:name>Supplies</act:name><act:id type="guid">supplies</act:id><act:type>EXPENSE</act:type><act:parent type="guid">expenses</act:parent></gnc:account>
          <gnc:account version="2.0.0"><act:name>Income</act:name><act:id type="guid">income</act:id><act:type>INCOME</act:type><act:parent type="guid">root</act:parent></gnc:account>
          <gnc:transaction version="2.0.0">
            <trn:id type="guid">t1</trn:id>
            <trn:currency><cmdty:space>CURRENCY</cmdty:space><cmdty:id>USD</cmdty:id></trn:currency>
            <trn:num>101</trn:num>
            <trn:date-posted><ts:date>2026-03-10 10:59:00 +0000</ts:date></trn:date-posted>
            <trn:description>Office supplies</trn:description>
            <trn:splits>
              <trn:split><split:id type="guid">s1</split:id><split:value>4250/100</split:value><split:account type="guid">supplies</split:account></trn:split>
              <trn:split><split:id type="guid">s2</split:id><split:value>-4250/100</split:value><split:account type="guid">checking</split:account></trn:split>
            </trn:splits>
          </gnc:transaction>
          <gnc:transaction version="2.0.0">
            <trn:id type="guid">t2</trn:id>
            <trn:currency><cmdty:space>CURRENCY</cmdty:space><cmdty:id>EUR</cmdty:id></trn:currency>
            <trn:date-posted><ts:date>2026-03-11 10:59:00 +0000</ts:date></trn:date-posted>
            <trn:description>Euro sale</trn:description>
            <trn:splits>
              <trn:split><split:value>100/1</split:value><split:account type="guid">checking</split:account></trn:split>
              <trn:split><split:value>-100/1</split:value><split:account type="guid">income</split:account></trn:split>
            </trn:splits>
          </gnc:transaction>
        </gnc:book>
        </gnc-v2>
        """;

    [Fact]
    public async Task GnuCash_books_import_with_types_kept_consistent_and_compressed_files_read()
    {
        var preview = await Preview(MigrationSource.GnuCash, GnuCash);
        Assert.Contains(preview.Rows, r => r is { Kind: MigrationKind.Account, Name: "Checking", Status: MigrationRowStatus.New } && r.Message!.Contains("Assets", StringComparison.Ordinal));
        Assert.Contains(preview.Rows, r => r is { Kind: MigrationKind.Account, Name: "Expenses:Supplies", Status: MigrationRowStatus.New });
        var euro = Assert.Single(preview.Rows, r => r.Kind == MigrationKind.Transaction && r.Status == MigrationRowStatus.Error);
        Assert.Contains("EUR", euro.Message, StringComparison.Ordinal);
        await Assert.ThrowsAsync<DomainException>(() => Import(MigrationSource.GnuCash, Encoding.UTF8.GetBytes(GnuCash)));

        // Without the euro transaction, compressed as GnuCash saves it.
        var second = GnuCash.LastIndexOf("<gnc:transaction", StringComparison.Ordinal);
        var withoutEuro = GnuCash[..second] + GnuCash[(GnuCash.LastIndexOf("</gnc:transaction>", StringComparison.Ordinal) + "</gnc:transaction>".Length)..];
        using var gz = new MemoryStream();
        await using (var zip = new GZipStream(gz, CompressionLevel.Fastest, leaveOpen: true))
            await zip.WriteAsync(Encoding.UTF8.GetBytes(withoutEuro));
        var result = await Import(MigrationSource.GnuCash, gz.ToArray());
        Assert.Equal(0, result.ErrorCount);

        var chart = await Chart();
        Assert.Equal(AccountType.Bank, chart.FindByFullName("Checking")!.Type);
        Assert.Equal("1010", chart.FindByFullName("Checking")!.Number);
        Assert.Equal(AccountType.Expense, chart.FindByFullName("Expenses:Supplies")!.Type);
        var entry = Assert.Single(await Imported());
        Assert.Equal(new DateOnly(2026, 3, 10), entry.Date);
        Assert.Equal(42.50m, entry.Lines.Single(l => l.AccountId == chart.FindByFullName("Expenses:Supplies")!.Id).Amount);
        Assert.Equal("Office supplies (101)", entry.Memo);
    }

    [Fact]
    public async Task QuickBooks_desktop_qbxml_lists_and_journal_entries_import()
    {
        const string qbxml = """
            <?xml version="1.0" ?>
            <?qbxml version="13.0"?>
            <QBXML><QBXMLMsgsRs>
              <AccountQueryRs statusCode="0">
                <AccountRet><Name>Checking</Name><FullName>Checking</FullName><Sublevel>0</Sublevel><AccountType>Bank</AccountType><AccountNumber>1000</AccountNumber></AccountRet>
                <AccountRet><Name>Consulting</Name><FullName>Consulting</FullName><Sublevel>0</Sublevel><AccountType>Income</AccountType><TaxLineInfoRet><TaxLineID>1</TaxLineID><TaxLineName>Sch C: Gross receipts or sales</TaxLineName></TaxLineInfoRet></AccountRet>
                <AccountRet><Name>Estimates</Name><FullName>Estimates</FullName><Sublevel>0</Sublevel><AccountType>NonPosting</AccountType></AccountRet>
              </AccountQueryRs>
              <CustomerQueryRs statusCode="0">
                <CustomerRet><Name>Acme</Name><FullName>Acme</FullName><Sublevel>0</Sublevel><CompanyName>Acme Inc</CompanyName><Email>ap@acme.test</Email>
                  <BillAddress><Addr1>1 Main St</Addr1><City>Springfield</City><State>IL</State><PostalCode>62701</PostalCode></BillAddress></CustomerRet>
                <CustomerRet><Name>Project X</Name><FullName>Acme:Project X</FullName><Sublevel>1</Sublevel></CustomerRet>
              </CustomerQueryRs>
              <VendorQueryRs statusCode="0"><VendorRet><Name>Paper Co</Name><IsVendorEligibleFor1099>true</IsVendorEligibleFor1099></VendorRet></VendorQueryRs>
              <ItemServiceQueryRs statusCode="0"><ItemServiceRet><Name>Hourly</Name><FullName>Hourly</FullName>
                <SalesOrPurchase><Desc>Consulting hour</Desc><Price>150.00</Price><AccountRef><FullName>Consulting</FullName></AccountRef></SalesOrPurchase></ItemServiceRet></ItemServiceQueryRs>
              <JournalEntryQueryRs statusCode="0"><JournalEntryRet><TxnDate>2026-02-01</TxnDate><RefNumber>JE-7</RefNumber><Memo>Opening cash</Memo>
                <JournalDebitLine><AccountRef><FullName>Checking</FullName></AccountRef><Amount>500.00</Amount></JournalDebitLine>
                <JournalCreditLine><AccountRef><FullName>Consulting</FullName></AccountRef><Amount>500.00</Amount></JournalCreditLine>
              </JournalEntryRet></JournalEntryQueryRs>
              <InvoiceQueryRs statusCode="0"><InvoiceRet><TxnID>1</TxnID></InvoiceRet></InvoiceQueryRs>
            </QBXMLMsgsRs></QBXML>
            """;
        var preview = await Preview(MigrationSource.QuickBooksQbxml, qbxml);
        Assert.Equal(0, preview.ErrorCount);
        Assert.Contains(preview.Rows, r => r is { Kind: MigrationKind.Account, Name: "Estimates", Status: MigrationRowStatus.Skipped });
        Assert.Contains(preview.Rows, r => r is { Kind: MigrationKind.Customer, Name: "Project X", Detail: "Job of Acme", Status: MigrationRowStatus.New });
        Assert.Contains(preview.Warnings, w => w.Contains("1 Invoice", StringComparison.Ordinal));

        await Import(MigrationSource.QuickBooksQbxml, Encoding.UTF8.GetBytes(qbxml));
        var customer = await _db.InDb(db => db.Customers.AsNoTracking().SingleAsync(c => c.DisplayName == "Acme"));
        Assert.Equal("1 Main St\nSpringfield IL 62701", customer.BillingAddress);
        Assert.True((await _db.InDb(db => db.Vendors.AsNoTracking().SingleAsync())).Track1099);
        Assert.Equal(150m, (await _db.InDb(db => db.Items.AsNoTracking().SingleAsync())).Rate);
        Assert.Equal("Journal entry JE-7 · Opening cash", Assert.Single(await Imported()).Memo);
        Assert.Equal("c.1", (await _db.InDb(db => db.Accounts.AsNoTracking().SingleAsync(a => a.Name == "Consulting"))).TaxLineKey);
    }

    [Fact]
    public async Task QuickBooks_online_json_resolves_accounts_by_id()
    {
        const string json = """
            [
              {"QueryResponse": {"Account": [
                {"Id": "35", "Name": "Checking", "FullyQualifiedName": "Checking", "AccountType": "Bank", "AcctNum": "1000"},
                {"Id": "79", "Name": "Sales", "FullyQualifiedName": "Sales", "AccountType": "Income"},
                {"Id": "80", "Name": "Online", "FullyQualifiedName": "Sales:Online", "AccountType": "Income"}
              ]}, "time": "2026-03-01T10:00:00-08:00"},
              {"QueryResponse": {"Customer": [{"DisplayName": "Bo's Bakery", "PrimaryEmailAddr": {"Address": "bo@bakery.test"}}]}},
              {"QueryResponse": {"JournalEntry": [{"TxnDate": "2026-03-05", "DocNumber": "12", "PrivateNote": "Web sales",
                "Line": [
                  {"Amount": 80.0, "Description": "Deposit", "DetailType": "JournalEntryLineDetail", "JournalEntryLineDetail": {"PostingType": "Debit", "AccountRef": {"value": "35", "name": "Checking"}}},
                  {"Amount": 80.0, "DetailType": "JournalEntryLineDetail", "JournalEntryLineDetail": {"PostingType": "Credit", "AccountRef": {"value": "80", "name": "Online"}}}
                ]}]}}
            ]
            """;
        var result = await Import(MigrationSource.QuickBooksOnline, Encoding.UTF8.GetBytes(json));
        Assert.Equal(5, result.NewCount);
        var chart = await Chart();
        var entry = Assert.Single(await Imported());
        Assert.Equal(-80m, entry.Lines.Single(l => l.AccountId == chart.FindByFullName("Sales:Online")!.Id).Amount);
        Assert.Equal("bo@bakery.test", (await _db.InDb(db => db.Customers.AsNoTracking().SingleAsync())).Email);
    }

    [Fact]
    public async Task Xero_charts_contacts_and_journals_import_in_turn()
    {
        const string chart = "*Code,*Name,*Type,*Tax Code,Description\n090,Business Bank,Bank,No VAT,\n200,Sales,Revenue,20% (VAT on Income),\n610,Accounts Receivable,Current Asset,No VAT,\n";
        await Import(MigrationSource.Xero, Encoding.UTF8.GetBytes(chart));
        Assert.Equal(AccountType.AccountsReceivable, (await Chart()).FindByFullName("Accounts Receivable")!.Type);

        const string contacts = "*ContactName,EmailAddress,POAddressLine1,POCity,IsSupplier\nRed Ltd,red@test.example,1 High St,Leeds,false\nPaper Ltd,,,,true\n";
        await Import(MigrationSource.Xero, Encoding.UTF8.GetBytes(contacts));
        Assert.Equal("1 High St\nLeeds", (await _db.InDb(db => db.Customers.AsNoTracking().SingleAsync())).BillingAddress);
        Assert.Equal("Paper Ltd", (await _db.InDb(db => db.Vendors.AsNoTracking().SingleAsync())).DisplayName);

        const string journal = "*Narration,*Date,Description,*AccountCode,*TaxRate,*Amount\nCash sales,2026-03-31,Till,090,No VAT,250.00\nCash sales,2026-03-31,Till,200,No VAT,-250.00\n";
        var preview = await Preview(MigrationSource.Xero, journal);
        Assert.Equal(1, preview.Count(MigrationKind.Transaction, MigrationRowStatus.New));
        await Import(MigrationSource.Xero, Encoding.UTF8.GetBytes(journal));
        var entry = Assert.Single(await Imported());
        var bank = (await Chart()).FindByFullName("Business Bank")!.Id;
        Assert.Equal(250m, entry.Lines.Single(l => l.AccountId == bank).Amount);
    }

    [Fact]
    public async Task Wave_transactions_bring_their_accounts_and_contacts()
    {
        const string wave = "Transaction ID,Transaction Date,Account Name,Transaction Description,Transaction Line Description,Amount (One column),Debit Amount (Two Column Approach),Credit Amount (Two Column Approach),Customer,Vendor,Account Group,Account Type\n"
            + "T1,2026-04-02,Cash on Hand,Invoice payment,,120.00,120.00,,Dana Co,,Assets,Cash and Bank\n"
            + "T1,2026-04-02,Design Income,Invoice payment,Logo,-120.00,,120.00,Dana Co,,Income,Income\n"
            + "T2,2026-04-03,Software,Monthly plan,,30.00,30.00,,,Tools Inc,Expenses,Operating Expense\n"
            + "T2,2026-04-03,Cash on Hand,Monthly plan,,-30.00,,30.00,,Tools Inc,Assets,Cash and Bank\n";
        var result = await Import(MigrationSource.Wave, Encoding.UTF8.GetBytes(wave));
        Assert.Equal(0, result.ErrorCount);
        var chart = await Chart();
        Assert.Equal(AccountType.Bank, chart.FindByFullName("Cash on Hand")!.Type);
        Assert.Equal(2, (await Imported()).Count);
        Assert.Equal(1, await _db.InDb(db => db.Vendors.CountAsync()));
    }

    [Fact]
    public async Task FreshBooks_clients_become_customers()
    {
        const string clients = "Organization,First Name,Last Name,Email,Business Phone,Address,City,Province/State,Postal/Zip\nNorth Studio,Ana,Lee,ana@north.test,555-0100,9 Elm St,Portland,OR,97201\n,Sam,Ortiz,sam@test.example,,,,,\n";
        await Import(MigrationSource.FreshBooks, Encoding.UTF8.GetBytes(clients));
        var customers = await _db.InDb(db => db.Customers.AsNoTracking().OrderBy(c => c.DisplayName).ToListAsync());
        Assert.Equal(["North Studio", "Sam Ortiz"], customers.Select(c => c.DisplayName));
        Assert.Equal("North Studio", customers[0].CompanyName);
        Assert.Null(customers[1].CompanyName);
    }

    [Fact]
    public async Task Unreadable_files_are_refused_with_a_reason()
    {
        Assert.Equal("import.not_gnucash", (await Assert.ThrowsAsync<DomainException>(() => Preview(MigrationSource.GnuCash, "<root/>"))).Code);
        Assert.Equal("import.not_xml", (await Assert.ThrowsAsync<DomainException>(() => Preview(MigrationSource.QuickBooksQbxml, "not xml"))).Code);
        Assert.Equal("import.not_json", (await Assert.ThrowsAsync<DomainException>(() => Preview(MigrationSource.QuickBooksOnline, "{"))).Code);
        // DTDs (and so external entities) are refused outright.
        Assert.Equal("import.not_xml", (await Assert.ThrowsAsync<DomainException>(() => Preview(MigrationSource.GnuCash,
            "<!DOCTYPE x [<!ENTITY e SYSTEM \"file:///etc/passwd\">]><gnc-v2>&e;</gnc-v2>"))).Code);
    }

    [SqliteFact]
    public async Task Scheduled_backups_go_to_the_folder_and_keep_the_newest()
    {
        var folder = Path.Combine(_db.BackupDirectory, "scheduled");
        await _db.InScope(sp => sp.GetRequiredService<ScheduledBackupService>().UpdateAsync(
            new BackupScheduleDetails(true, BackupFrequency.Daily, DayOfWeek.Sunday, 2, BackupDestination.Folder, folder, Keep: 2), null));

        for (var i = 0; i < 3; i++)
        {
            _db.Clock.UtcNow = _db.Clock.UtcNow.AddDays(1);
            var result = await _db.InScope(sp => sp.GetRequiredService<ScheduledBackupService>().RunDueAsync(TimeZoneInfo.Utc));
            Assert.True(result!.Succeeded, result.Error);
            // Not due again until the next day's slot.
            Assert.Null(await _db.InScope(sp => sp.GetRequiredService<ScheduledBackupService>().RunDueAsync(TimeZoneInfo.Utc)));
        }

        var files = Directory.GetFiles(folder).Select(Path.GetFileName).Order().ToList();
        Assert.Equal(2, files.Count);
        Assert.All(files, f => Assert.StartsWith(ScheduledBackupService.NamePrefix, f, StringComparison.Ordinal));
        var schedule = await _db.InScope(sp => sp.GetRequiredService<ScheduledBackupService>().GetAsync());
        Assert.Equal(files[^1], schedule.LastFileName);
        Assert.Null(schedule.LastError);
    }

    [SqliteFact]
    public async Task Backups_before_quitting_keep_their_own_last_few_beside_the_daily_ones()
    {
        var folder = Path.Combine(_db.BackupDirectory, "scheduled");
        Assert.Null(await _db.InScope(sp => sp.GetRequiredService<ScheduledBackupService>().RunBeforeQuitAsync()));
        await _db.InScope(sp => sp.GetRequiredService<ScheduledBackupService>().UpdateAsync(
            new BackupScheduleDetails(true, BackupFrequency.Daily, DayOfWeek.Sunday, 2, BackupDestination.Folder, folder, Keep: 2), null));
        for (var i = 0; i < 2; i++)
        {
            _db.Clock.UtcNow = _db.Clock.UtcNow.AddDays(1);
            Assert.True((await _db.InScope(sp => sp.GetRequiredService<ScheduledBackupService>().RunDueAsync(TimeZoneInfo.Utc)))!.Succeeded);
        }

        for (var i = 0; i < ScheduledBackupService.QuitKeep + 2; i++)
        {
            _db.Clock.UtcNow = _db.Clock.UtcNow.AddMinutes(1);
            var result = await _db.InScope(sp => sp.GetRequiredService<ScheduledBackupService>().RunBeforeQuitAsync());
            Assert.True(result!.Succeeded, result.Error);
            Assert.StartsWith(ScheduledBackupService.QuitPrefix, result.FileName, StringComparison.Ordinal);
        }

        // And a daily one afterwards still keeps two daily ones, whatever the quit backups are called.
        _db.Clock.UtcNow = _db.Clock.UtcNow.AddDays(1);
        Assert.True((await _db.InScope(sp => sp.GetRequiredService<ScheduledBackupService>().RunDueAsync(TimeZoneInfo.Utc)))!.Succeeded);

        var files = Directory.GetFiles(folder).Select(Path.GetFileName).ToList();
        Assert.Equal(ScheduledBackupService.QuitKeep, files.Count(f => f!.StartsWith(ScheduledBackupService.QuitPrefix, StringComparison.Ordinal)));
        Assert.Equal(2, files.Count(f => !f!.StartsWith(ScheduledBackupService.QuitPrefix, StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Scheduled_backups_record_failures_and_keep_the_secret_out_of_the_audit()
    {
        await _db.InScope(sp => sp.GetRequiredService<ScheduledBackupService>().UpdateAsync(
            new BackupScheduleDetails(false, BackupFrequency.Weekly, DayOfWeek.Monday, 3, BackupDestination.S3, null, "https://s3.example.test", "eu-west-1", "books", "ob", "AKID"),
            "very-secret"));
        var audit = await _db.InDb(db => db.AuditRecords.AsNoTracking().SingleAsync(r => r.EntityType == "backup_schedule"));
        Assert.DoesNotContain("very-secret", audit.After, StringComparison.Ordinal);
        Assert.Contains("\"HasSecretKey\":true", audit.After, StringComparison.Ordinal);

        Assert.Equal("backup.folder_required", (await Assert.ThrowsAsync<DomainException>(() => _db.InScope(sp => sp.GetRequiredService<ScheduledBackupService>().UpdateAsync(
            new BackupScheduleDetails(true, BackupFrequency.Daily, DayOfWeek.Sunday, 2, BackupDestination.Folder), null)))).Code);
    }

    [Fact]
    public void Signature_v4_matches_the_aws_example()
    {
        // "GET Object" from the AWS S3 Signature Version 4 documentation.
        var headers = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["host"] = "examplebucket.s3.amazonaws.com",
            ["range"] = "bytes=0-9",
            ["x-amz-content-sha256"] = AwsSignatureV4.EmptyHash,
            ["x-amz-date"] = "20130524T000000Z",
        };
        var authorization = AwsSignatureV4.Authorization("GET", "/test.txt", "", headers, AwsSignatureV4.EmptyHash,
            "AKIAIOSFODNN7EXAMPLE", "wJalrXUtnFEMI/K7MDENG/bPxRfiCYEXAMPLEKEY", "us-east-1", "s3", new DateTimeOffset(2013, 5, 24, 0, 0, 0, TimeSpan.Zero));
        Assert.Equal("AWS4-HMAC-SHA256 Credential=AKIAIOSFODNN7EXAMPLE/20130524/us-east-1/s3/aws4_request,SignedHeaders=host;range;x-amz-content-sha256;x-amz-date,"
            + "Signature=f0e8bdb87c964420e857bd35b5d6ed310bd44f0170aba48dd91039c6036bdb41", authorization);
    }

    [Fact]
    public async Task S3_target_puts_lists_and_deletes_with_signed_requests()
    {
        var handler = new FakeStorage();
        using var target = new S3BackupTarget(handler, () => new DateTimeOffset(2026, 3, 1, 2, 0, 0, TimeSpan.Zero));
        var schedule = BackupSchedule.Default();
        schedule.Update(true, BackupFrequency.Daily, DayOfWeek.Sunday, 2, BackupDestination.S3, null, "https://storage.example.test", "auto", "books", "ob/", "AKID", "protected", 5);
        var file = Path.GetTempFileName();
        await File.WriteAllTextAsync(file, "backup bytes");
        try
        {
            await target.UploadAsync(schedule, "secret", file, "openbook-scheduled-1.openbook.db", CancellationToken.None);
            var names = await target.ListAsync(schedule, "secret", "openbook-scheduled-", CancellationToken.None);
            await target.DeleteAsync(schedule, "secret", names[0], CancellationToken.None);
        }
        finally
        {
            File.Delete(file);
        }

        Assert.Equal(["PUT /books/ob/openbook-scheduled-1.openbook.db", "GET /books?list-type=2&prefix=ob%2Fopenbook-scheduled-", "DELETE /books/ob/openbook-scheduled-1.openbook.db"],
            handler.Requests.Select(r => r.Line));
        Assert.All(handler.Requests, r => Assert.StartsWith("AWS4-HMAC-SHA256 Credential=AKID/20260301/auto/s3/aws4_request", r.Authorization, StringComparison.Ordinal));
        Assert.Equal("backup bytes", handler.Uploaded);
    }

    private sealed class FakeStorage : HttpMessageHandler
    {
        public List<(string Line, string Authorization)> Requests { get; } = [];
        public string? Uploaded { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(($"{request.Method} {request.RequestUri!.PathAndQuery}", request.Headers.GetValues("Authorization").Single()));
            if (request.Method == HttpMethod.Put)
                Uploaded = await request.Content!.ReadAsStringAsync(cancellationToken);
            var body = request.Method == HttpMethod.Get
                ? "<ListBucketResult><Contents><Key>ob/openbook-scheduled-1.openbook.db</Key></Contents></ListBucketResult>"
                : "";
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) };
        }
    }
}
