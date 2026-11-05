using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using OpenBook.Application.Capture;
using OpenBook.Application.Companies;
using OpenBook.Application.Reporting;
using OpenBook.Domain.Capture;
using OpenBook.Domain.Common;
using OpenBook.Domain.Companies;
using OpenBook.Domain.Reporting;
using OpenBook.Web.Security;

namespace OpenBook.Integration.Tests;

/// <summary>The audit's Medium findings on access.</summary>
public sealed class SecurityFindingsTests : IAsyncLifetime
{
    private readonly LedgerDatabase _db = new();

    public Task InitializeAsync() => _db.InitializeAsync();

    public Task DisposeAsync() => _db.DisposeAsync();

    private Task<T> In<TService, T>(Func<TService, Task<T>> work) where TService : notnull =>
        _db.InScope(sp => work(sp.GetRequiredService<TService>()));

    private static async Task<string> Refused(Func<Task> action) => (await Assert.ThrowsAsync<DomainException>(action)).Code;

    private static ClaimsPrincipal WithSet(params (string Area, UserRole Level)[] areas)
    {
        var claims = new List<Claim> { new(ClaimTypes.Role, UserRole.Standard.ToString()), new(AuthClaims.PermissionSet, Guid.NewGuid().ToString()) };
        claims.AddRange(areas.Select(a => new Claim(AuthClaims.Area, $"{a.Area}:{a.Level}")));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));
    }

    [Theory]
    [InlineData("/ok", true)]
    [InlineData("/ok?x=1", true)]
    [InlineData("//example.org", false)]
    [InlineData("/\\example.org", false)]
    [InlineData("/\t/example.org", false)]
    [InlineData("https://example.org", false)]
    public void Only_paths_on_this_site_are_local(string url, bool local) =>
        Assert.Equal(local, AccessRules.LocalPath(url) is not null);

    [Fact]
    public void A_saved_return_address_with_a_tab_goes_home() =>
        Assert.Equal("/", WebSecurity.SafeReturnUrl("/\t/example.org"));

    [Fact]
    public void A_file_is_read_with_its_record()
    {
        var sales = WithSet(("sales", UserRole.ViewOnly));

        Assert.True(AccessRules.Allows(sales, "/attachments/" + Guid.NewGuid(), "GET", out _));
        Assert.False(AccessRules.Allows(sales, "/attachments/remove", "POST", out _));
        Assert.Equal("sales", RecordAreas.Of("invoice"));
        Assert.Null(AuthClaims.LevelIn(sales, RecordAreas.Of("bill")));
    }

    [Fact]
    public void Printing_belongs_with_sales()
    {
        Assert.True(AccessRules.Allows(WithSet(("sales", UserRole.Standard)), "/print/pdf", "POST", out _));
        Assert.False(AccessRules.Allows(WithSet(("data", UserRole.Standard)), "/print/pdf", "POST", out _));
        Assert.Equal("sales", ApiScopes.AreaOf("/api/print/queue"));
    }

    [Fact]
    public void A_limited_key_writes_only_to_its_areas()
    {
        var identity = new ClaimsIdentity([new Claim(ClaimTypes.Role, UserRole.Standard.ToString()), new Claim(AuthClaims.Scoped, "true"), new Claim(AuthClaims.Scope, "capture:write"), new Claim(AuthClaims.Scope, "projects:write")], "key");
        var key = new ClaimsPrincipal(identity);

        Assert.True(AuthClaims.MayWrite(key, CaptureService.AreaOf(CaptureKind.Time)));
        Assert.False(AuthClaims.MayWrite(key, CaptureService.AreaOf(CaptureKind.Invoice)));
        Assert.False(AuthClaims.MayWrite(WithSet(("capture", UserRole.Standard), ("sales", UserRole.ViewOnly)), CaptureService.AreaOf(CaptureKind.Invoice)));
    }

    [Fact]
    public async Task A_scheduled_report_can_be_changed_only_by_whoever_saved_it()
    {
        var saved = await In<SavedReportService, SavedReport>(s => s.SaveAsync("Monthly", "profit-and-loss", "preset=ThisMonth"));
        await In<ReportSubscriptionService, ReportSubscription>(s => s.CreateAsync(new ReportSubscriptionDetails(saved.Id, "a@b.test", ReportFrequency.Daily, 0, 8, ReportFileFormat.Pdf)));
        await _db.InDb(async db =>
        {
            await Microsoft.EntityFrameworkCore.RelationalDatabaseFacadeExtensions.ExecuteSqlRawAsync(db.Database, "UPDATE \"SavedReports\" SET \"CreatedBy\" = 'someone else'");
            return true;
        });

        Assert.Equal("report.scheduled", await Refused(() => In<SavedReportService, SavedReport>(s => s.SaveAsync("Monthly", "profit-and-loss", "preset=ThisYear"))));
        Assert.Equal("report.scheduled", await Refused(() => _db.InScope<bool>(async sp =>
        {
            await sp.GetRequiredService<SavedReportService>().DeleteAsync(saved.Id);
            return true;
        })));
    }

    [Fact]
    public async Task The_last_admin_is_never_made_inactive_by_their_end_date()
    {
        var other = await In<UserService, User>(s => s.CreateAsync("second", "a long password", UserRole.Admin));
        var today = OpenBook.Application.CompanyTime.Today;
        await In<UserService, User>(s => s.SetDetailsAsync(other.Id, null, today.AddDays(30)));
        await _db.InDb(async db =>
        {
            await Microsoft.EntityFrameworkCore.RelationalDatabaseFacadeExtensions.ExecuteSqlRawAsync(db.Database, "UPDATE \"Users\" SET \"IsActive\" = FALSE WHERE \"Username\" <> 'second'");
            return true;
        });

        var ended = await In<UserService, IReadOnlyList<string>>(s => s.EndExpiredAccessAsync(today.AddDays(31)));

        Assert.Empty(ended);
    }

    [Fact]
    public async Task Reset_password_can_bring_back_an_admin()
    {
        var clerk = await In<UserService, User>(s => s.CreateAsync("clerk", "a long password", UserRole.Standard));
        await In<UserService, User>(s => s.SetActiveAsync(clerk.Id, false));

        await In<AuthService, string>(s => s.ResetPasswordAsync("clerk", "a new long password", "console:tester", restoreAdmin: true));

        var restored = (await In<UserService, IReadOnlyList<User>>(s => s.ListAsync())).Single(u => u.Username == "clerk");
        Assert.Equal((UserRole.Admin, true), (restored.Role, restored.IsActive));
    }

    [Fact]
    public async Task Linking_a_single_sign_on_account_asks_who_it_is_and_never_replaces_one()
    {
        var owner = (await In<UserService, IReadOnlyList<User>>(s => s.ListAsync())).Single(u => u.Username == LedgerDatabase.OwnerUsername);

        Assert.Equal("auth.wrong_password", await Refused(() => _db.InScope<bool>(async sp =>
        {
            await sp.GetRequiredService<TwoFactorService>().CheckIdentityAsync(owner.Id, "not the password", "");
            return true;
        })));

        await _db.InScope<bool>(async sp =>
        {
            await sp.GetRequiredService<SingleSignOnService>().LinkAsync(owner.Id, new ExternalIdentity("https://idp.test", "first", null, false));
            return true;
        });
        Assert.Equal("sso.already_linked", await Refused(() => _db.InScope<bool>(async sp =>
        {
            await sp.GetRequiredService<SingleSignOnService>().LinkAsync(owner.Id, new ExternalIdentity("https://idp.test", "second", null, false));
            return true;
        })));
    }
}
