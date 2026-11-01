using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OpenBook.Application;
using OpenBook.Application.Companies;
using OpenBook.Application.Forms;
using OpenBook.Domain.Common;
using OpenBook.Domain.Companies;

namespace OpenBook.Integration.Tests;

public sealed class InviteTests : IAsyncLifetime
{
    private const string Base = "https://books.test";
    private readonly LedgerDatabase _db = new();

    public Task InitializeAsync() => _db.InitializeAsync();

    public Task DisposeAsync() => _db.DisposeAsync();

    private Task<T> Run<T>(Func<IServiceProvider, Task<T>> work) => _db.InScope(work);

    private Task<SentInvite> Invite(string email, UserRole role = UserRole.Accountant, DateOnly? ends = null, bool send = true) =>
        Run(sp => sp.GetRequiredService<InviteService>().InviteAsync(new InviteCommand(email, role, "Pat", ends, SendEmail: send), Base));

    private static string Token(SentInvite sent) => sent.Link[(Base.Length + "/invite/".Length)..];

    private Task<AcceptedInvite> Accept(SentInvite sent, string username = "pat") =>
        Run(sp => sp.GetRequiredService<InviteService>().AcceptAsync(Token(sent), username, "a long password"));

    private DateOnly Today => CompanyTime.TodayAt(_db.Clock.UtcNow, CompanyTime.Zone);

    [Fact]
    public async Task An_invitation_link_lets_someone_choose_their_own_sign_in()
    {
        var sent = await Invite(" Pat@Example.COM ", ends: Today.AddDays(60), send: false);
        Assert.StartsWith($"{Base}/invite/obi_", sent.Link, StringComparison.Ordinal);
        Assert.False(sent.Emailed);
        Assert.Equal("pat@example.com", sent.Invite.Email);
        Assert.NotEqual(Token(sent), sent.Invite.TokenHash);

        var open = await Run(sp => sp.GetRequiredService<InviteService>().OpenAsync(Token(sent)));
        Assert.Equal("pat", open!.SuggestedUsername);
        Assert.Null(await Run(sp => sp.GetRequiredService<InviteService>().OpenAsync("obi_wrong")));

        var accepted = await Accept(sent);
        Assert.Equal(UserRole.Accountant, accepted.User.Role);
        var user = await _db.InDb(db => db.Users.SingleAsync(u => u.Username == "pat"));
        Assert.Equal("pat@example.com", user.Email);
        Assert.Equal(Today.AddDays(60), user.AccessEndsOn);
        Assert.Equal(SignInStatus.Succeeded, (await Run(sp => sp.GetRequiredService<AuthService>().SignInAsync("pat", "a long password"))).Status);

        // Used once.
        var again = await Assert.ThrowsAsync<DomainException>(() => Accept(sent, "pat2"));
        Assert.Equal("invite.closed", again.Code);
        var audit = await _db.InDb(db => db.AuditRecords.Where(r => r.EntityType == InviteService.EntityType).Select(r => r.After).ToListAsync());
        Assert.Contains(audit, a => a!.Contains("Accepted", StringComparison.Ordinal));
        Assert.All(audit, a => Assert.DoesNotContain(Token(sent), a!, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Invitations_are_emailed_when_email_is_set_up_and_can_be_sent_again_or_withdrawn()
    {
        var unsent = await Invite("pat@example.com");
        Assert.False(unsent.Emailed);
        Assert.Contains("isn't set up", unsent.EmailError, StringComparison.Ordinal);

        await Run(sp => sp.GetRequiredService<EmailService>().SaveSettingsAsync(new EmailSettingsCommand(
            EmailDelivery.Smtp, "smtp.test", 587, SmtpSecurity.Auto, "books", "s3cret-pass", false, "books@co.test", "Co", null)));
        var resent = await Run(sp => sp.GetRequiredService<InviteService>().ResendAsync(unsent.Invite.Id, Base));
        Assert.True(resent.Emailed);
        var mail = Assert.Single(_db.Mail.Sent).Message;
        Assert.Equal("pat@example.com", mail.To);
        Assert.Contains(resent.Link, mail.Body, StringComparison.Ordinal);
        Assert.Contains("as accountant", mail.Body, StringComparison.Ordinal);

        // The first link stopped working when it was sent again.
        Assert.Null(await Run(sp => sp.GetRequiredService<InviteService>().OpenAsync(Token(unsent))));
        var duplicate = await Assert.ThrowsAsync<DomainException>(() => Invite("PAT@example.com"));
        Assert.Equal("invite.pending", duplicate.Code);

        await Run(sp => sp.GetRequiredService<InviteService>().RevokeAsync(resent.Invite.Id));
        Assert.Equal("invite.closed", (await Assert.ThrowsAsync<DomainException>(() => Accept(resent))).Code);
    }

    [Fact]
    public async Task Links_expire_and_taken_names_are_refused()
    {
        var sent = await Invite("pat@example.com", send: false);
        Assert.Equal("user.username_taken", (await Assert.ThrowsAsync<DomainException>(() => Accept(sent, LedgerDatabase.OwnerUsername))).Code);

        _db.Clock.UtcNow += TimeSpan.FromDays(UserInvite.DefaultDays) + TimeSpan.FromMinutes(1);
        Assert.Equal("invite.closed", (await Assert.ThrowsAsync<DomainException>(() => Accept(sent))).Code);
        Assert.True((await Run(sp => sp.GetRequiredService<InviteService>().ListAsync())).Single().IsExpired(_db.Clock.UtcNow));

        var fresh = await Invite("pat@example.com", send: false);
        await Accept(fresh);
        Assert.Equal("user.email_taken", (await Assert.ThrowsAsync<DomainException>(() => Invite("pat@example.com"))).Code);
        Assert.Equal("user.access_end_past", (await Assert.ThrowsAsync<DomainException>(() => Invite("lee@example.com", ends: Today))).Code);
    }

    [Fact]
    public async Task Access_ends_on_its_date_and_the_hourly_job_makes_them_inactive()
    {
        var sent = await Invite("pat@example.com", UserRole.Standard, ends: Today.AddDays(2), send: false);
        var pat = (await Accept(sent)).User;
        var key = await Run(sp => sp.GetRequiredService<ApiKeyService>().CreateAsync(pat.Id, "script"));
        Assert.NotNull(await Run(sp => sp.GetRequiredService<ApiKeyService>().AuthenticateAsync(key.Token)));

        _db.Clock.UtcNow += TimeSpan.FromDays(2);
        Assert.Equal(SignInStatus.InvalidCredentials, (await Run(sp => sp.GetRequiredService<AuthService>().SignInAsync("pat", "a long password"))).Status);
        Assert.False(await Run(sp => sp.GetRequiredService<AuthService>().IsSessionValidAsync(pat.Id, pat.SecurityStamp)));
        Assert.Null(await Run(sp => sp.GetRequiredService<ApiKeyService>().AuthenticateAsync(key.Token)));

        Assert.Equal(["pat"], await Run(sp => sp.GetRequiredService<UserService>().EndExpiredAccessAsync(Today)));
        Assert.False((await _db.InDb(db => db.Users.SingleAsync(u => u.Id == pat.Id))).IsActive);
        Assert.Empty(await Run(sp => sp.GetRequiredService<UserService>().EndExpiredAccessAsync(Today)));
    }

    [Fact]
    public async Task Admins_set_a_users_email_and_access_end()
    {
        var clerk = await Run(sp => sp.GetRequiredService<UserService>().CreateAsync("clerk", "a long password", UserRole.Standard));
        var updated = await Run(sp => sp.GetRequiredService<UserService>().SetDetailsAsync(clerk.Id, "Clerk@Co.test", Today.AddDays(10)));
        Assert.Equal("clerk@co.test", updated.Email);
        Assert.Equal(Today.AddDays(10), updated.AccessEndsOn);

        var other = await Run(sp => sp.GetRequiredService<UserService>().CreateAsync("other", "a long password", UserRole.Standard));
        Assert.Equal("user.email_taken", (await Assert.ThrowsAsync<DomainException>(() =>
            Run(sp => sp.GetRequiredService<UserService>().SetDetailsAsync(other.Id, "clerk@co.test", null)))).Code);
        Assert.Equal("user.self", (await Assert.ThrowsAsync<DomainException>(() =>
            Run(sp => sp.GetRequiredService<UserService>().SetDetailsAsync(_db.Owner!.Id, null, Today.AddDays(3))))).Code);

        var cleared = await Run(sp => sp.GetRequiredService<UserService>().SetDetailsAsync(clerk.Id, "", null));
        Assert.Null(cleared.Email);
        Assert.Null(cleared.AccessEndsOn);
    }
}
