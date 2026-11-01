using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OpenBook.Application.Companies;
using OpenBook.Domain.Common;
using OpenBook.Domain.Companies;
using OpenBook.Web.Security;

namespace OpenBook.Integration.Tests;

public sealed class SingleSignOnTests : IAsyncLifetime
{
    private const string Issuer = "https://accounts.google.com";
    private readonly LedgerDatabase _db = new();

    public Task InitializeAsync() => _db.InitializeAsync();

    public Task DisposeAsync() => _db.DisposeAsync();

    private Task<T> Run<T>(Func<IServiceProvider, Task<T>> work) => _db.InScope(work);

    private Task<SignInSettings> TurnOn(bool ssoOnly = false) => Run(sp => sp.GetRequiredService<SingleSignOnService>().SaveAsync(
        new SsoSettingsCommand(true, SsoProvider.Google, null, null, "client-1", "very secret", null, ssoOnly)));

    private Task<SignInResult> SignIn(string subject, string? email, bool verified = true) =>
        Run(sp => sp.GetRequiredService<SingleSignOnService>().SignInAsync(new ExternalIdentity(Issuer, subject, email, verified)));

    private Task<User> AddUser(string name, UserRole role = UserRole.Standard, string? email = null) => Run(async sp =>
    {
        var user = await sp.GetRequiredService<UserService>().CreateAsync(name, "a long password", role);
        return email is null ? user : await sp.GetRequiredService<UserService>().SetDetailsAsync(user.Id, email, null);
    });

    [Fact]
    public async Task Settings_need_a_client_and_keep_the_secret_protected()
    {
        var missing = await Assert.ThrowsAsync<DomainException>(() => Run(sp => sp.GetRequiredService<SingleSignOnService>().SaveAsync(
            new SsoSettingsCommand(true, SsoProvider.Microsoft, "contoso.com", null, "client-1", null, null, false))));
        Assert.Equal("sso.secret_required", missing.Code);
        var http = await Assert.ThrowsAsync<DomainException>(() => Run(sp => sp.GetRequiredService<SingleSignOnService>().SaveAsync(
            new SsoSettingsCommand(true, SsoProvider.Other, null, "http://idp.example.com", "client-1", "s", null, false))));
        Assert.Equal("sso.authority_invalid", http.Code);

        var saved = await Run(sp => sp.GetRequiredService<SingleSignOnService>().SaveAsync(
            new SsoSettingsCommand(true, SsoProvider.Microsoft, "contoso.com", null, "client-1", "very secret", null, false)));
        Assert.Equal("https://login.microsoftonline.com/contoso.com/v2.0", saved.Authority);
        Assert.Equal("protected:very secret", saved.ProtectedSsoClientSecret);
        Assert.Equal("Sign in with Microsoft", saved.ButtonText);

        // Saving again without a secret keeps it.
        var again = await Run(sp => sp.GetRequiredService<SingleSignOnService>().SaveAsync(
            new SsoSettingsCommand(true, SsoProvider.Microsoft, "contoso.com", null, "client-2", null, "Sign in with Contoso", true)));
        Assert.Equal("protected:very secret", again.ProtectedSsoClientSecret);
        Assert.Equal("very secret", (await Run(sp => sp.GetRequiredService<SingleSignOnService>().ClientAsync()))!.ClientSecret);
        Assert.All(await _db.InDb(db => db.AuditRecords.Where(r => r.EntityType == "sign_in_settings").Select(r => r.After).ToListAsync()),
            a => Assert.DoesNotContain("very secret", a!, StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_verified_email_finds_the_user_and_the_account_is_remembered()
    {
        await TurnOn();
        var pat = await AddUser("pat", email: "pat@example.com");
        Assert.Equal(SignInStatus.NotInvited, (await SignIn("g-1", "pat@example.com", verified: false)).Status);

        var first = await SignIn("g-1", "PAT@example.com");
        Assert.Equal(SignInStatus.Succeeded, first.Status);
        Assert.Equal(pat.Id, first.User!.Id);
        Assert.False(first.MustSetUpTwoFactor);
        Assert.Equal($"{Issuer}|g-1", (await _db.InDb(db => db.Users.SingleAsync(u => u.Id == pat.Id))).ExternalLogin);

        // Later the account signs in by its subject, whatever email the provider says then.
        Assert.Equal(pat.Id, (await SignIn("g-1", "pat@elsewhere.example", verified: false)).User!.Id);
        Assert.Equal(SignInStatus.NotInvited, (await SignIn("g-2", "nobody@example.com")).Status);

        await Run(sp => sp.GetRequiredService<UserService>().SetActiveAsync(pat.Id, false));
        Assert.Equal(SignInStatus.InvalidCredentials, (await SignIn("g-1", null)).Status);
    }

    [Fact]
    public async Task A_waiting_invitation_is_accepted_through_single_sign_on()
    {
        await TurnOn();
        await Run(sp => sp.GetRequiredService<InviteService>().InviteAsync(
            new InviteCommand("lee@example.com", UserRole.Accountant, SendEmail: false), "https://books.test"));

        var result = await SignIn("g-9", "lee@example.com");
        Assert.Equal(SignInStatus.Succeeded, result.Status);
        Assert.Equal(UserRole.Accountant, result.User!.Role);
        var lee = await _db.InDb(db => db.Users.SingleAsync(u => u.Email == "lee@example.com"));
        Assert.Equal("lee", lee.Username);
        Assert.Equal(InviteStatus.Accepted, (await _db.InDb(db => db.UserInvites.SingleAsync())).Status);
        // No password was chosen, so password sign-in doesn't work for them.
        Assert.Equal(SignInStatus.InvalidCredentials, (await Run(sp => sp.GetRequiredService<AuthService>().SignInAsync("lee", "a long password"))).Status);
    }

    [Fact]
    public async Task Single_sign_on_only_leaves_passwords_to_admins()
    {
        await AddUser("pat");
        await TurnOn(ssoOnly: true);
        Assert.Equal(SignInStatus.SsoRequired, (await Run(sp => sp.GetRequiredService<AuthService>().SignInAsync("pat", "a long password"))).Status);
        Assert.Equal(SignInStatus.InvalidCredentials, (await Run(sp => sp.GetRequiredService<AuthService>().SignInAsync("pat", "wrong password"))).Status);
        Assert.Equal(SignInStatus.Succeeded,
            (await Run(sp => sp.GetRequiredService<AuthService>().SignInAsync(LedgerDatabase.OwnerUsername, LedgerDatabase.OwnerPassword))).Status);
    }

    [Fact]
    public async Task Accounts_can_be_linked_by_their_user_and_unlinked()
    {
        await TurnOn();
        var pat = await AddUser("pat");
        var lee = await AddUser("lee");
        var identity = new ExternalIdentity(Issuer, "g-5", null, false);
        await _db.InScope(sp => sp.GetRequiredService<SingleSignOnService>().LinkAsync(pat.Id, identity));
        Assert.Equal(pat.Id, (await SignIn("g-5", null, verified: false)).User!.Id);

        var taken = await Assert.ThrowsAsync<DomainException>(() => _db.InScope(sp => sp.GetRequiredService<SingleSignOnService>().LinkAsync(lee.Id, identity)));
        Assert.Equal("sso.linked_elsewhere", taken.Code);

        await _db.InScope(sp => sp.GetRequiredService<SingleSignOnService>().UnlinkAsync(pat.Id));
        Assert.Equal(SignInStatus.NotInvited, (await SignIn("g-5", null, verified: false)).Status);
    }

    [Theory]
    [InlineData(SsoProvider.Google, null, "true", true)]
    [InlineData(SsoProvider.Google, null, null, false)]
    [InlineData(SsoProvider.Microsoft, "contoso.com", null, true)]
    [InlineData(SsoProvider.Microsoft, "organizations", null, false)]
    public void Emails_count_when_the_provider_or_the_companys_own_directory_vouches(SsoProvider provider, string? tenant, string? verified, bool expected)
    {
        var settings = SignInSettings.Create();
        settings.ConfigureSso(false, provider, tenant, null, null, null, null, false);
        List<Claim> claims = [new("iss", "https://issuer.test"), new("sub", "s-1"), new("oid", "o-1"), new("email", "pat@contoso.com")];
        if (verified is not null)
            claims.Add(new Claim("email_verified", verified));
        var identity = SingleSignOn.Identity(new ClaimsPrincipal(new ClaimsIdentity(claims, "test")), settings);
        Assert.Equal(expected, identity.EmailVerified);
        Assert.Equal(provider == SsoProvider.Microsoft ? "o-1" : "s-1", identity.Subject);
        Assert.Equal("pat@contoso.com", identity.Email);
    }
}
