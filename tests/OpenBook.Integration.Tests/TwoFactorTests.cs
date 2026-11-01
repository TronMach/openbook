using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OpenBook.Application.Companies;
using OpenBook.Domain.Common;
using OpenBook.Domain.Companies;

namespace OpenBook.Integration.Tests;

public sealed class TwoFactorTests : IAsyncLifetime
{
    private const string Password = "a long password";
    private readonly LedgerDatabase _db = new();

    public Task InitializeAsync() => _db.InitializeAsync();

    public Task DisposeAsync() => _db.DisposeAsync();

    private Task<T> Run<T>(Func<IServiceProvider, Task<T>> work) => _db.InScope(work);

    private Task<User> AddUser(string name, UserRole role = UserRole.Standard) =>
        Run(sp => sp.GetRequiredService<UserService>().CreateAsync(name, Password, role));

    private string CodeNow(string secret, int stepsAhead = 0) =>
        Totp.Code(Base32.Decode(secret)!, Totp.Step(_db.Clock.UtcNow) + stepsAhead);

    /// <summary>Sets two-factor up for the user; returns the secret and the recovery codes.</summary>
    private async Task<(string Secret, IReadOnlyList<string> Codes)> TurnOn(User user)
    {
        var setup = await Run(sp => sp.GetRequiredService<TwoFactorService>().NewSetupAsync(user.Id));
        var enabled = await Run(sp => sp.GetRequiredService<TwoFactorService>().EnableAsync(user.Id, setup.Secret, CodeNow(setup.Secret)));
        return (setup.Secret, enabled.RecoveryCodes);
    }

    private Task<SignInResult> SignIn(string name, string password = Password) =>
        Run(sp => sp.GetRequiredService<AuthService>().SignInAsync(name, password));

    private Task<SignInResult> SecondStep(SignInResult first, string code) =>
        Run(sp => sp.GetRequiredService<TwoFactorService>().SignInAsync(first.User!.Id, first.User.SecurityStamp, code));

    [Fact]
    public async Task Setting_up_needs_a_code_from_the_app_and_stores_only_protected_secrets()
    {
        var user = await AddUser("clerk");
        var setup = await Run(sp => sp.GetRequiredService<TwoFactorService>().NewSetupAsync(user.Id));
        Assert.Contains("OpenBook:clerk%20%28", setup.Uri, StringComparison.Ordinal);

        var wrong = await Assert.ThrowsAsync<DomainException>(() =>
            Run(sp => sp.GetRequiredService<TwoFactorService>().EnableAsync(user.Id, setup.Secret, CodeNow(setup.Secret, 5))));
        Assert.Equal("twofactor.wrong_code", wrong.Code);

        var enabled = await Run(sp => sp.GetRequiredService<TwoFactorService>().EnableAsync(user.Id, setup.Secret, CodeNow(setup.Secret)));
        Assert.Equal(10, enabled.RecoveryCodes.Count);

        var stored = await _db.InDb(db => db.Users.SingleAsync(u => u.Id == user.Id));
        Assert.Equal("protected:" + setup.Secret, stored.ProtectedTwoFactorSecret);
        Assert.DoesNotContain(enabled.RecoveryCodes[0], stored.RecoveryCodeHashes!, StringComparison.Ordinal);
        var audit = await _db.InDb(db => db.AuditRecords.Where(r => r.EntityType == "user" && r.EntityId == user.Id).Select(r => r.After).ToListAsync());
        Assert.Contains(audit, a => a!.Contains("two-factor sign-in turned on", StringComparison.Ordinal));
        Assert.All(audit, a => Assert.DoesNotContain(setup.Secret, a!, StringComparison.Ordinal));

        var again = await Assert.ThrowsAsync<DomainException>(() =>
            Run(sp => sp.GetRequiredService<TwoFactorService>().EnableAsync(user.Id, setup.Secret, CodeNow(setup.Secret, 1))));
        Assert.Equal("twofactor.already_on", again.Code);
    }

    [Fact]
    public async Task Signing_in_takes_the_password_then_a_fresh_code()
    {
        var user = await AddUser("clerk");
        var (secret, _) = await TurnOn(user);

        var first = await SignIn("clerk");
        Assert.Equal(SignInStatus.TwoFactorRequired, first.Status);
        Assert.Null((await _db.InDb(db => db.Users.SingleAsync(u => u.Id == user.Id))).LastLoginAt);

        // The code that confirmed the setup can't be used again.
        Assert.Equal(SignInStatus.InvalidCode, (await SecondStep(first, CodeNow(secret))).Status);

        _db.Clock.UtcNow += TimeSpan.FromSeconds(30);
        var done = await SecondStep(first, CodeNow(secret));
        Assert.Equal(SignInStatus.Succeeded, done.Status);
        Assert.Equal(user.Id, done.User!.Id);
        Assert.NotNull(done.TwoFactorSince);
        Assert.Equal(SignInStatus.InvalidCode, (await SecondStep(first, CodeNow(secret))).Status);
    }

    [Fact]
    public async Task A_recovery_code_signs_in_once()
    {
        var user = await AddUser("clerk");
        var (_, codes) = await TurnOn(user);
        var first = await SignIn("clerk");

        var done = await SecondStep(first, codes[0].ToUpperInvariant());
        Assert.Equal(SignInStatus.Succeeded, done.Status);
        Assert.True(done.UsedRecoveryCode);
        Assert.Equal(9, done.RecoveryCodesLeft);
        Assert.Equal(SignInStatus.InvalidCode, (await SecondStep(first, codes[0])).Status);
        Assert.Contains(await _db.InDb(db => db.AuditRecords.Select(r => r.After).ToListAsync()),
            a => a!.Contains("signed in with a recovery code", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Wrong_codes_count_toward_the_lockout_and_a_password_change_restarts_the_sign_in()
    {
        var user = await AddUser("clerk");
        var (secret, _) = await TurnOn(user);
        _db.Clock.UtcNow += TimeSpan.FromMinutes(1);

        // Four wrong passwords and one wrong code lock the account: the password step doesn't reset the count.
        for (var i = 0; i < 4; i++)
            Assert.Equal(SignInStatus.InvalidCredentials, (await SignIn("clerk", "wrong password")).Status);
        var first = await SignIn("clerk");
        var locked = await SecondStep(first, "000000" == CodeNow(secret) ? "111111" : "000000");
        Assert.Equal(SignInStatus.LockedOut, locked.Status);
        Assert.Equal(SignInStatus.LockedOut, (await SecondStep(first, CodeNow(secret))).Status);

        _db.Clock.UtcNow += User.LockoutDuration + TimeSpan.FromSeconds(30);
        first = await SignIn("clerk");
        await Run(sp => sp.GetRequiredService<UserService>().ResetPasswordAsync(user.Id, "another long password"));
        Assert.Equal(SignInStatus.InvalidCredentials, (await SecondStep(first, CodeNow(secret))).Status);
    }

    [Fact]
    public async Task Turning_it_off_needs_the_password_and_a_code()
    {
        var user = await AddUser("clerk");
        var (secret, codes) = await TurnOn(user);
        _db.Clock.UtcNow += TimeSpan.FromSeconds(30);

        var noCode = await Assert.ThrowsAsync<DomainException>(() =>
            _db.InScope(sp => sp.GetRequiredService<TwoFactorService>().DisableAsync(user.Id, Password, "123")));
        Assert.Equal("twofactor.wrong_code", noCode.Code);
        var noPassword = await Assert.ThrowsAsync<DomainException>(() =>
            _db.InScope(sp => sp.GetRequiredService<TwoFactorService>().DisableAsync(user.Id, "wrong password", CodeNow(secret))));
        Assert.Equal("auth.wrong_password", noPassword.Code);

        var fresh = await Run(sp => sp.GetRequiredService<TwoFactorService>().NewRecoveryCodesAsync(user.Id, Password, CodeNow(secret)));
        Assert.Equal(SignInStatus.InvalidCode, (await SecondStep(await SignIn("clerk"), codes[1])).Status);

        await _db.InScope(sp => sp.GetRequiredService<TwoFactorService>().DisableAsync(user.Id, Password, fresh[0]));
        Assert.Equal(SignInStatus.Succeeded, (await SignIn("clerk")).Status);
        Assert.False((await _db.InDb(db => db.Users.SingleAsync(u => u.Id == user.Id))).HasTwoFactor);
    }

    [Fact]
    public async Task A_remembered_browser_only_counts_for_the_same_setup_and_stamp()
    {
        var user = await AddUser("clerk");
        await TurnOn(user);
        var first = await SignIn("clerk");
        var since = first.TwoFactorSince!.Value;

        var remembered = await Run(sp => sp.GetRequiredService<TwoFactorService>().SignInRememberedAsync(user.Id, first.User!.SecurityStamp, since));
        Assert.Equal(SignInStatus.Succeeded, remembered.Status);
        Assert.Equal(SignInStatus.InvalidCredentials, (await Run(sp => sp.GetRequiredService<TwoFactorService>()
            .SignInRememberedAsync(user.Id, first.User!.SecurityStamp, since.AddSeconds(-1)))).Status);

        await Run(sp => sp.GetRequiredService<AuthService>().EndOtherSessionsAsync(user.Id));
        Assert.Equal(SignInStatus.InvalidCredentials, (await Run(sp => sp.GetRequiredService<TwoFactorService>()
            .SignInRememberedAsync(user.Id, first.User!.SecurityStamp, since))).Status);
    }

    [Fact]
    public async Task Required_roles_must_set_it_up_and_admins_or_the_command_line_can_turn_it_off()
    {
        var clerk = await AddUser("clerk");
        var accountant = await AddUser("books", UserRole.Accountant);
        await Run(sp => sp.GetRequiredService<TwoFactorService>().RequireAsync(UserRole.Accountant));

        Assert.False((await SignIn("clerk")).MustSetUpTwoFactor);
        var books = await SignIn("books");
        Assert.Equal(SignInStatus.Succeeded, books.Status);
        Assert.True(books.MustSetUpTwoFactor);
        Assert.True(await Run(sp => sp.GetRequiredService<AuthService>().MustSetUpTwoFactorAsync(accountant.Id)));
        Assert.Contains(await _db.InDb(db => db.AuditRecords.Where(r => r.EntityType == "sign_in_settings").Select(r => r.After).ToListAsync()),
            a => a!.Contains("Accountant", StringComparison.Ordinal));

        await TurnOn(accountant);
        Assert.False(await Run(sp => sp.GetRequiredService<AuthService>().MustSetUpTwoFactorAsync(accountant.Id)));
        var stamp = (await _db.InDb(db => db.Users.SingleAsync(u => u.Id == accountant.Id))).SecurityStamp;

        await Run(sp => sp.GetRequiredService<UserService>().ResetTwoFactorAsync(accountant.Id));
        var reset = await _db.InDb(db => db.Users.SingleAsync(u => u.Id == accountant.Id));
        Assert.False(reset.HasTwoFactor);
        Assert.NotEqual(stamp, reset.SecurityStamp);

        await TurnOn(clerk);
        await Run(sp => sp.GetRequiredService<AuthService>().ResetPasswordAsync("clerk", "a new long password", "console:tester", clearTwoFactor: true));
        Assert.Equal(SignInStatus.Succeeded, (await SignIn("clerk", "a new long password")).Status);

        var self = await Assert.ThrowsAsync<DomainException>(() => Run(sp => sp.GetRequiredService<UserService>().ResetTwoFactorAsync(_db.Owner!.Id)));
        Assert.Equal("user.self", self.Code);
    }
}
