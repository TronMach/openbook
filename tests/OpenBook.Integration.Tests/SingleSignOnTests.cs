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

    private const string ContosoId = "6f1c2d3e-4b5a-4c6d-8e7f-0a1b2c3d4e5f";
    private const string ContosoIssuer = $"https://login.microsoftonline.com/{ContosoId}/v2.0";

    private Task<SignInSettings> TurnOnMicrosoft(string tenant) => Run(sp => sp.GetRequiredService<SingleSignOnService>().SaveAsync(
        new SsoSettingsCommand(true, SsoProvider.Microsoft, tenant, null, "client-1", "very secret", null, false)));

    private Task<SignInResult> SignInMicrosoft(SignInSettings settings, string oid, string? email) =>
        Run(sp => sp.GetRequiredService<SingleSignOnService>().SignInAsync(SingleSignOn.Identity(new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim("iss", ContosoIssuer), new Claim("oid", oid), new Claim("tid", ContosoId), .. email is null ? Array.Empty<Claim>() : [new Claim("email", email)]],
            "test")), settings)));

    [Fact]
    public async Task Microsoft_logins_keep_working_when_the_tenant_is_retyped()
    {
        // C532: the login is keyed on the token's issuer (with the directory's id), not on how the tenant was typed.
        var byDomain = await TurnOnMicrosoft("contoso.com");
        var pat = await AddUser("pat", email: "pat@contoso.com");
        Assert.Equal(pat.Id, (await SignInMicrosoft(byDomain, "o-7", "pat@contoso.com")).User!.Id);
        Assert.Equal($"{ContosoIssuer}|o-7", (await _db.InDb(db => db.Users.SingleAsync(u => u.Id == pat.Id))).ExternalLogin);

        var byId = await TurnOnMicrosoft(ContosoId);
        Assert.Equal(pat.Id, (await SignInMicrosoft(byId, "o-7", null)).User!.Id);
    }

    [Fact]
    public async Task Logins_recorded_under_the_typed_tenant_move_to_the_tokens_issuer()
    {
        // Earlier versions dropped "iss" and recorded the configured authority instead.
        var settings = await TurnOnMicrosoft("contoso.com");
        var pat = await AddUser("pat");
        await _db.InScope(sp => sp.GetRequiredService<SingleSignOnService>().LinkAsync(pat.Id,
            new ExternalIdentity("https://login.microsoftonline.com/contoso.com/v2.0", "o-8", null, false)));

        Assert.Equal(pat.Id, (await SignInMicrosoft(settings, "o-8", null)).User!.Id);
        Assert.Equal($"{ContosoIssuer}|o-8", (await _db.InDb(db => db.Users.SingleAsync(u => u.Id == pat.Id))).ExternalLogin);
    }

    [Fact]
    public async Task A_verified_email_relinks_the_same_person_but_not_someone_else()
    {
        var settings = await TurnOnMicrosoft("contoso.com");
        var pat = await AddUser("pat", email: "pat@contoso.com");
        var lee = await AddUser("lee", email: "lee@contoso.com");
        // Linked under an issuer that will never come again (the old typed tenant, then retyped).
        foreach (var (user, oid) in new[] { (pat, "o-1"), (lee, "o-2") })
            await _db.InScope(sp => sp.GetRequiredService<SingleSignOnService>().LinkAsync(user.Id,
                new ExternalIdentity("https://login.microsoftonline.com/old.example/v2.0", oid, null, false)));

        Assert.Equal(pat.Id, (await SignInMicrosoft(settings, "o-1", "pat@contoso.com")).User!.Id);
        // Lee's address with another person's id doesn't take over Lee's account.
        Assert.Equal(SignInStatus.NotInvited, (await SignInMicrosoft(settings, "o-3", "lee@contoso.com")).Status);
        Assert.Equal("https://login.microsoftonline.com/old.example/v2.0|o-2", (await _db.InDb(db => db.Users.SingleAsync(u => u.Id == lee.Id))).ExternalLogin);
    }

    [Theory]
    [InlineData("organizations", true, false)]
    [InlineData("common", true, false)]
    [InlineData("consumers", false, false)]
    [InlineData("contoso.com", false, true)]
    public void Microsoft_directories_are_told_apart(string tenant, bool any, bool own)
    {
        var settings = SignInSettings.Create();
        settings.ConfigureSso(false, SsoProvider.Microsoft, tenant, null, null, null, null, false);
        Assert.Equal(any, settings.IsAnyMicrosoftDirectory);
        Assert.Equal(own, settings.IsOwnMicrosoftDirectory);
    }

    private static Microsoft.IdentityModel.JsonWebTokens.JsonWebToken Token(string? tid) =>
        new(new Microsoft.IdentityModel.JsonWebTokens.JsonWebTokenHandler().CreateToken(new Microsoft.IdentityModel.Tokens.SecurityTokenDescriptor
        {
            Claims = tid is null ? new Dictionary<string, object> { ["sub"] = "s" } : new Dictionary<string, object> { ["tid"] = tid },
        }));

    [Fact]
    public void Any_directory_tokens_must_name_their_own_directory_as_issuer()
    {
        var parameters = new Microsoft.IdentityModel.Tokens.TokenValidationParameters();
        Assert.Equal(ContosoIssuer, SingleSignOn.MicrosoftAnyDirectoryIssuer(ContosoIssuer, Token(ContosoId), parameters));
        Assert.Equal(ContosoIssuer, SingleSignOn.MicrosoftAnyDirectoryIssuer(ContosoIssuer, Token(ContosoId.ToUpperInvariant()), parameters));

        foreach (var (issuer, tid) in new[]
        {
            ($"https://login.microsoftonline.com/{Guid.Empty}/v2.0", ContosoId), // another directory's issuer
            (ContosoIssuer, (string?)null),                                       // no tid
            ("https://evil.example/" + ContosoId + "/v2.0", ContosoId),           // not Microsoft
            ("https://login.microsoftonline.com/{tenantid}/v2.0", ContosoId),     // the placeholder itself
        })
            Assert.Throws<Microsoft.IdentityModel.Tokens.SecurityTokenInvalidIssuerException>(() =>
                SingleSignOn.MicrosoftAnyDirectoryIssuer(issuer, Token(tid), parameters));
    }

    [Fact]
    public async Task The_handler_keeps_the_issuer_and_checks_any_directory_tokens()
    {
        await TurnOnMicrosoft("organizations");
        var options = new Microsoft.AspNetCore.Authentication.OpenIdConnect.OpenIdConnectOptions();
        new SsoOptionsSetup(_db.Services.GetRequiredService<IServiceScopeFactory>()).Configure(SingleSignOn.Scheme, options);
        Assert.DoesNotContain(options.ClaimActions, a => a.ClaimType == "iss");
        Assert.Contains(options.ClaimActions, a => a.ClaimType == "nonce");
        Assert.NotNull(options.TokenValidationParameters.IssuerValidator);

        await TurnOnMicrosoft("contoso.com");
        var named = new Microsoft.AspNetCore.Authentication.OpenIdConnect.OpenIdConnectOptions();
        new SsoOptionsSetup(_db.Services.GetRequiredService<IServiceScopeFactory>()).Configure(SingleSignOn.Scheme, named);
        Assert.Null(named.TokenValidationParameters.IssuerValidator);
    }
}
