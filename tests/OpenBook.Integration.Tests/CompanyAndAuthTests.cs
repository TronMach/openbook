using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OpenBook.Application.Companies;
using OpenBook.Application.Ledger;
using OpenBook.Domain.Accounts;
using OpenBook.Domain.Common;
using OpenBook.Domain.Companies;
using static OpenBook.Domain.Ledger.JournalLineDraft;

namespace OpenBook.Integration.Tests;

public sealed class CompanySetupTests : IAsyncLifetime
{
    private readonly LedgerDatabase _db = new(setUpCompany: false);

    public Task InitializeAsync() => _db.InitializeAsync();

    public Task DisposeAsync() => _db.DisposeAsync();

    private Task<User> SetUp(SetUpCompanyCommand command) =>
        _db.InScope(sp => sp.GetRequiredService<CompanySetupService>().SetUpAsync(command));

    private static SetUpCompanyCommand Command(string password = LedgerDatabase.OwnerPassword, string currency = "usd") =>
        new("  Acme Widgets ", BusinessType.SCorporation, currency, 7, " Owner ", password, Address: "1 Main St\nSpringfield");

    [Fact]
    public async Task Books_cannot_be_used_before_setup()
    {
        var cash = await _db.CreateAccount("Cash", AccountType.Bank);
        var sales = await _db.CreateAccount("Sales", AccountType.Income);

        var ex = await Assert.ThrowsAsync<DomainException>(() => _db.InScope(sp => sp.GetRequiredService<PostingEngine>()
            .PostAsync(new PostJournalEntryCommand(new DateOnly(2026, 1, 1), [Dr(cash.Id, 1m), Cr(sales.Id, 1m)]))));
        Assert.Equal("company.not_set_up", ex.Code);
    }

    [Fact]
    public async Task Setup_creates_company_and_owner_with_audit_but_no_password_in_audit()
    {
        var owner = await SetUp(Command());

        var company = await _db.InDb(db => db.Companies.SingleAsync());
        Assert.Equal("Acme Widgets", company.Name);
        Assert.Equal(new Currency("USD"), company.HomeCurrency);
        Assert.Equal(7, company.FiscalYearStartMonth);
        Assert.Equal("owner", owner.Username);
        Assert.NotEqual(LedgerDatabase.OwnerPassword, owner.PasswordHash);

        var audit = await _db.InDb(db => db.AuditRecords.OrderBy(r => r.Id).ToListAsync());
        Assert.Equal(["company", "user"], audit.Select(r => r.EntityType));
        Assert.All(audit, r =>
        {
            Assert.Equal("owner", r.Actor);
            Assert.DoesNotContain(owner.PasswordHash, r.After, StringComparison.Ordinal);
            Assert.DoesNotContain(LedgerDatabase.OwnerPassword, r.After, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task Setup_runs_only_once()
    {
        await SetUp(Command());
        var ex = await Assert.ThrowsAsync<DomainException>(() => SetUp(Command()));
        Assert.Equal("company.already_set_up", ex.Code);
    }

    [Fact]
    public async Task Reset_before_setup_points_to_the_wizard()
    {
        var ex = await Assert.ThrowsAsync<DomainException>(() => _db.InScope(sp => sp.GetRequiredService<AuthService>()
            .ResetPasswordAsync(null, LedgerDatabase.OwnerPassword, "console:tester")));
        Assert.Equal("company.not_set_up", ex.Code);
    }

    [Theory]
    [InlineData("short", "USD", "user.password_too_short")]
    [InlineData(LedgerDatabase.OwnerPassword, "US", "currency.invalid")]
    public async Task Invalid_setup_saves_nothing(string password, string currency, string code)
    {
        var ex = await Assert.ThrowsAsync<DomainException>(() => SetUp(Command(password, currency)));

        Assert.Equal(code, ex.Code);
        Assert.Equal(0, await _db.InDb(db => db.Companies.CountAsync()));
        Assert.Equal(0, await _db.InDb(db => db.Users.CountAsync()));
    }
}

public sealed class AuthTests : IAsyncLifetime
{
    private readonly LedgerDatabase _db = new();

    public Task InitializeAsync() => _db.InitializeAsync();

    public Task DisposeAsync() => _db.DisposeAsync();

    private Task<SignInResult> SignIn(string password, string username = LedgerDatabase.OwnerUsername) =>
        _db.InScope(sp => sp.GetRequiredService<AuthService>().SignInAsync(username, password));

    private Task<bool> SessionValid(AuthenticatedUser user) =>
        _db.InScope(sp => sp.GetRequiredService<AuthService>().IsSessionValidAsync(user.Id, user.SecurityStamp));

    [Fact]
    public async Task Correct_password_signs_in_with_case_insensitive_username()
    {
        var result = await SignIn(LedgerDatabase.OwnerPassword, "  OWNER ");

        Assert.Equal(SignInStatus.Succeeded, result.Status);
        Assert.Equal(_db.Owner!.Id, result.User!.Id);
        Assert.True(await SessionValid(result.User));
        Assert.Equal(_db.Clock.UtcNow, (await _db.InDb(db => db.Users.SingleAsync())).LastLoginAt);
    }

    [Theory]
    [InlineData(LedgerDatabase.OwnerUsername, "wrong password")]
    [InlineData("nobody", LedgerDatabase.OwnerPassword)]
    [InlineData("x", LedgerDatabase.OwnerPassword)]
    public async Task Bad_credentials_are_rejected_the_same_way(string username, string password) =>
        Assert.Equal(SignInStatus.InvalidCredentials, (await SignIn(password, username)).Status);

    [Fact]
    public async Task Repeated_failures_lock_the_account_until_the_lockout_expires()
    {
        for (var i = 1; i < User.MaxFailedLogins; i++)
            Assert.Equal(SignInStatus.InvalidCredentials, (await SignIn("wrong password")).Status);

        var locked = await SignIn("wrong password");
        Assert.Equal(SignInStatus.LockedOut, locked.Status);
        Assert.Equal(_db.Clock.UtcNow + User.LockoutDuration, locked.LockedUntil);

        // Even the right password is refused while locked.
        Assert.Equal(SignInStatus.LockedOut, (await SignIn(LedgerDatabase.OwnerPassword)).Status);

        _db.Clock.UtcNow += User.LockoutDuration + TimeSpan.FromSeconds(1);
        Assert.Equal(SignInStatus.Succeeded, (await SignIn(LedgerDatabase.OwnerPassword)).Status);
    }

    [Fact]
    public async Task A_pause_after_wrong_passwords_and_a_sign_in_from_a_new_device_are_audited()
    {
        for (var i = 0; i < User.MaxFailedLogins; i++)
            await SignIn("wrong password");
        Task<List<string?>> Notes() => _db.InDb(db => db.AuditRecords.AsNoTracking().Where(r => r.EntityType == "user").Select(r => r.After).ToListAsync());
        Assert.Single(await Notes(), n => n!.Contains("sign-in from it paused", StringComparison.Ordinal));
        Assert.DoesNotContain(await Notes(), n => n!.Contains("wrong password", StringComparison.Ordinal) && n.Contains("Password\"", StringComparison.Ordinal));

        _db.Clock.UtcNow += User.LockoutDuration + TimeSpan.FromSeconds(1);
        var user = (await SignIn(LedgerDatabase.OwnerPassword)).User!;
        Task<Guid> Start(string device) => _db.InScope(sp => sp.GetRequiredService<AuthService>().StartSessionAsync(user, device, "203.0.113.7"));
        await Start("Firefox on Windows");
        await Start("Firefox on Windows");
        Assert.DoesNotContain(await Notes(), n => n!.Contains("new device", StringComparison.Ordinal));
        await Start("Safari on iPhone");
        Assert.Single(await Notes(), n => n!.Contains("new device", StringComparison.Ordinal) && n.Contains("Safari on iPhone", StringComparison.Ordinal));
        // What the log keeps of a typed username: one line, cut short.
        Assert.Equal(("ab", 61), (OpenBook.Web.Security.SignInLog.Name("a\r\nb"), OpenBook.Web.Security.SignInLog.Name(new string('x', 70)).Length));
    }

    [Fact]
    public async Task Password_reconfirmation_failures_count_toward_lockout()
    {
        for (var i = 0; i < User.MaxFailedLogins; i++)
            Assert.False(await _db.InScope(sp => sp.GetRequiredService<AuthService>().VerifyPasswordAsync(_db.Owner!.Id, "wrong password")));

        Assert.Equal(SignInStatus.LockedOut, (await SignIn(LedgerDatabase.OwnerPassword)).Status);
    }

    [Fact]
    public async Task Changing_password_invalidates_existing_sessions()
    {
        var before = (await SignIn(LedgerDatabase.OwnerPassword)).User!;

        var after = await _db.InScope(sp => sp.GetRequiredService<AuthService>()
            .ChangePasswordAsync(before.Id, LedgerDatabase.OwnerPassword, "a brand new passphrase"));

        Assert.False(await SessionValid(before));
        Assert.True(await SessionValid(after));
        Assert.Equal(SignInStatus.InvalidCredentials, (await SignIn(LedgerDatabase.OwnerPassword)).Status);
        Assert.Equal(SignInStatus.Succeeded, (await SignIn("a brand new passphrase")).Status);
    }

    private Task<string> Reset(string? username, string password = "recovered passphrase") =>
        _db.InScope(sp => sp.GetRequiredService<AuthService>().ResetPasswordAsync(username, password, "console:tester"));

    [Fact]
    public async Task Reset_sets_new_password_clears_lockout_ends_sessions_and_audits()
    {
        var session = (await SignIn(LedgerDatabase.OwnerPassword)).User!;
        for (var i = 0; i < User.MaxFailedLogins; i++)
            await SignIn("wrong password");
        Assert.Equal(SignInStatus.LockedOut, (await SignIn(LedgerDatabase.OwnerPassword)).Status);

        Assert.Equal(LedgerDatabase.OwnerUsername, await Reset(username: null));

        Assert.False(await SessionValid(session));
        Assert.Equal(SignInStatus.InvalidCredentials, (await SignIn(LedgerDatabase.OwnerPassword)).Status);
        Assert.Equal(SignInStatus.Succeeded, (await SignIn("recovered passphrase")).Status);

        var audit = await _db.InDb(db => db.AuditRecords.OrderBy(r => r.Id).LastAsync());
        Assert.Equal("console:tester", audit.Actor);
        Assert.Contains("\"Via\":\"reset\"", audit.After, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Reset_needs_a_username_when_there_are_several_users()
    {
        await _db.InDb(db =>
        {
            db.Users.Add(User.Create("bookkeeper", "unused-hash", _db.Clock.UtcNow));
            return db.SaveChangesAsync();
        });

        var ex = await Assert.ThrowsAsync<DomainException>(() => Reset(username: null));
        Assert.Equal("auth.username_required", ex.Code);
        Assert.Contains("bookkeeper, owner", ex.Message, StringComparison.Ordinal);

        Assert.Equal("bookkeeper", await Reset(" BookKeeper "));
    }

    [Theory]
    [InlineData("nobody", "recovered passphrase", "user.not_found")]
    [InlineData(null, "short", "user.password_too_short")]
    public async Task Invalid_reset_changes_nothing(string? username, string password, string code)
    {
        var ex = await Assert.ThrowsAsync<DomainException>(() => Reset(username, password));

        Assert.Equal(code, ex.Code);
        Assert.Equal(SignInStatus.Succeeded, (await SignIn(LedgerDatabase.OwnerPassword)).Status);
    }

    [Fact]
    public async Task Changing_password_requires_the_current_one()
    {
        var ex = await Assert.ThrowsAsync<DomainException>(() => _db.InScope(sp => sp.GetRequiredService<AuthService>()
            .ChangePasswordAsync(_db.Owner!.Id, "not my password", "a brand new passphrase")));
        Assert.Equal("auth.wrong_password", ex.Code);
    }
}
