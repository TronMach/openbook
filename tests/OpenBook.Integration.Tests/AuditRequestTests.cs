using Microsoft.Extensions.DependencyInjection;
using OpenBook.Application.Reporting;
using OpenBook.Domain.Common;
using OpenBook.Domain.Reporting;

namespace OpenBook.Integration.Tests;

/// <summary>The audit request (PBC) list (C306). The test clock reads January 31, 2026.</summary>
public sealed class AuditRequestTests : IAsyncLifetime
{
    private readonly LedgerDatabase _db = new();

    public Task InitializeAsync() => _db.InitializeAsync();

    public Task DisposeAsync() => _db.DisposeAsync();

    private Task<T> In<T>(Func<AuditRequestService, Task<T>> work) => _db.InScope(sp => work(sp.GetRequiredService<AuditRequestService>()));

    [Fact]
    public async Task Requests_are_tracked_to_received_with_overdue_ones_counted()
    {
        var bank = await In(s => s.CreateAsync(new AuditRequestDetails(2025, "Bank reconciliations, December", "Pat", new DateOnly(2026, 1, 15))));
        await In(s => s.CreateAsync(new AuditRequestDetails(2025, "Fixed asset roll-forward", "Sam", new DateOnly(2026, 2, 15))));
        var leases = await In(s => s.CreateAsync(new AuditRequestDetails(2025, "Lease agreements")));
        await In(s => s.CreateAsync(new AuditRequestDetails(2024, "Last year's item")));

        var list = await In(s => s.ListAsync(2025));
        Assert.Equal(3, list.Count);
        Assert.Equal("Bank reconciliations, December", list[0].Item);
        var summary = await In(s => Task.FromResult(s.Summarize(list)));
        Assert.Equal(new AuditRequestSummary(3, 3, 1, 0, 0), summary);

        var received = await In(s => s.SetStatusAsync(bank.Id, AuditRequestStatus.Received));
        Assert.Equal(new DateOnly(2026, 1, 31), received.DoneOn);
        Assert.False(received.IsOverdue(new DateOnly(2026, 1, 31)));
        await In(s => s.UpdateAsync(leases.Id, new AuditRequestDetails(1999, "Lease agreements (all)", "Pat", null, "Only the new ones"), AuditRequestStatus.NotNeeded));
        list = await In(s => s.ListAsync(2025));
        Assert.Equal(new AuditRequestSummary(3, 1, 0, 1, 1), await In(s => Task.FromResult(s.Summarize(list))));
        Assert.Equal(2025, list.Single(r => r.Id == leases.Id).Year);
        Assert.Equal([2025, 2024], await In(s => s.YearsAsync()));

        // Opened again: the date it went over is cleared.
        Assert.Null((await In(s => s.SetStatusAsync(bank.Id, AuditRequestStatus.Open))).DoneOn);

        await In(async s =>
        {
            await s.DeleteAsync(leases.Id);
            return true;
        });
        Assert.Equal(2, (await In(s => s.ListAsync(2025))).Count);
        var audit = await _db.InDb(db => Task.FromResult(db.AuditRecords.Where(a => a.EntityType == AuditRequestService.EntityType).ToList()));
        Assert.Contains(audit, a => a.Action == Domain.Auditing.AuditAction.Deleted && a.EntityId == leases.Id);
        Assert.Equal("audit_request.item_required", (await Assert.ThrowsAsync<DomainException>(() =>
            In(s => s.CreateAsync(new AuditRequestDetails(2025, "  "))))).Code);
    }

    [Fact]
    public async Task Last_years_list_is_copied_open_with_due_dates_moved_on()
    {
        var bank = await In(s => s.CreateAsync(new AuditRequestDetails(2024, "Bank reconciliations", "Pat", new DateOnly(2025, 1, 15), "All accounts")));
        await In(s => s.SetStatusAsync(bank.Id, AuditRequestStatus.Received));
        await In(s => s.CreateAsync(new AuditRequestDetails(2024, "Payroll registers")));
        await In(s => s.CreateAsync(new AuditRequestDetails(2025, "payroll registers")));

        Assert.Equal(1, await In(s => s.CopyYearAsync(2024, 2025)));
        var copy = (await In(s => s.ListAsync(2025))).Single(r => r.Item == "Bank reconciliations");
        Assert.Equal(AuditRequestStatus.Open, copy.Status);
        Assert.Null(copy.DoneOn);
        Assert.Equal(new DateOnly(2026, 1, 15), copy.DueOn);
        Assert.Equal("All accounts", copy.Notes);
        Assert.Equal("audit_request.nothing_to_copy", (await Assert.ThrowsAsync<DomainException>(() => In(s => s.CopyYearAsync(2020, 2025)))).Code);
        Assert.Equal("audit_request.same_year", (await Assert.ThrowsAsync<DomainException>(() => In(s => s.CopyYearAsync(2025, 2025)))).Code);
    }

    [Fact]
    public async Task The_auditors_spreadsheet_is_imported_all_or_nothing_and_written_back()
    {
        await In(s => s.CreateAsync(new AuditRequestDetails(2025, "Trial balance")));
        const string csv = "Request,Assigned to,Due date,Comments\r\nTrial balance,Pat,2026-01-10,\r\nAR aging,Sam,2026-02-01,\"As of 12/31, with \"\"credits\"\"\"\r\n,,,\r\nLoan statements,,,\r\n";
        Assert.Equal(2, await In(s => s.ImportCsvAsync(2025, csv)));
        var aging = (await In(s => s.ListAsync(2025))).Single(r => r.Item == "AR aging");
        Assert.Equal(("Sam", new DateOnly(2026, 2, 1)), (aging.Owner, aging.DueOn));
        Assert.Equal("As of 12/31, with \"credits\"", aging.Notes);

        Assert.Equal("import.bad_date", (await Assert.ThrowsAsync<DomainException>(() =>
            In(s => s.ImportCsvAsync(2025, "Item,Due\r\nInventory count,soon\r\n")))).Code);
        Assert.Equal("import.no_item_column", (await Assert.ThrowsAsync<DomainException>(() =>
            In(s => s.ImportCsvAsync(2025, "Who,Due\r\nPat,2026-01-01\r\n")))).Code);
        Assert.Equal("import.nothing_new", (await Assert.ThrowsAsync<DomainException>(() =>
            In(s => s.ImportCsvAsync(2025, "Item\r\nAR aging\r\n")))).Code);
        Assert.Equal(3, (await In(s => s.ListAsync(2025))).Count);

        var written = await In(s => s.CsvAsync(2025));
        Assert.StartsWith("Item,Owner,Due,Status,Done on,Notes\r\n", written);
        Assert.Contains("AR aging,Sam,2026-02-01,Open,,\"As of 12/31, with \"\"credits\"\"\"", written);
        Assert.Contains("Loan statements,,,Open,,", written);
    }
}
