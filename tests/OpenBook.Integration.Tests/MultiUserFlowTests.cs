using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OpenBook.Application;
using OpenBook.Application.Companies;
using OpenBook.Application.Forms;
using OpenBook.Application.Sales;
using OpenBook.Application.Webhooks;
using OpenBook.Domain.Common;
using OpenBook.Domain.Companies;
using OpenBook.Domain.Sales;
using OpenBook.Domain.Webhooks;
using OpenBook.Infrastructure;
using OpenBook.Infrastructure.Persistence;

namespace OpenBook.Integration.Tests;

public sealed class MultiUserFlowTests : IAsyncLifetime
{
    private readonly LedgerDatabase _db = new();

    public Task InitializeAsync() => _db.InitializeAsync();

    public Task DisposeAsync() => _db.DisposeAsync();

    private Task<T> Run<T>(Func<IServiceProvider, Task<T>> work) => _db.InScope(work);

    private Task<User> AddUser(string name, UserRole role) =>
        Run(sp => sp.GetRequiredService<UserService>().CreateAsync(name, "a long password", role));

    [Fact]
    public async Task The_owner_is_an_admin_and_admins_add_users_with_roles()
    {
        Assert.Equal(UserRole.Admin, _db.Owner!.Role);
        var clerk = await AddUser("Clerk", UserRole.Standard);
        Assert.Equal(("clerk", UserRole.Standard, true), (clerk.Username, clerk.Role, clerk.IsActive));

        var taken = await Assert.ThrowsAsync<DomainException>(() => AddUser("CLERK", UserRole.ViewOnly));
        Assert.Equal("user.username_taken", taken.Code);
        var audit = await Run(sp => sp.GetRequiredService<ILedgerStore>().ListAuditAsync("user", clerk.Id, null, default));
        Assert.Contains("Standard", audit.Single().After!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task There_is_always_an_active_admin_and_nobody_demotes_themselves()
    {
        var users = (Func<IServiceProvider, UserService>)(sp => sp.GetRequiredService<UserService>());
        var self = await Assert.ThrowsAsync<DomainException>(() => Run(sp => users(sp).ChangeRoleAsync(_db.Owner!.Id, UserRole.Standard)));
        Assert.Equal("user.self", self.Code);

        var other = await AddUser("second", UserRole.Admin);
        await Run(sp => users(sp).ChangeRoleAsync(other.Id, UserRole.Accountant));
        var last = await Assert.ThrowsAsync<DomainException>(() => Run(sp => users(sp).SetActiveAsync(_db.Owner!.Id, false)));
        Assert.Equal("user.self", last.Code);

        await Run(sp => users(sp).ChangeRoleAsync(other.Id, UserRole.Admin));
        await Run(sp => users(sp).SetActiveAsync(other.Id, false));
        Assert.False((await Run(sp => users(sp).GetAsync(other.Id)))!.IsActive);
    }

    [Fact]
    public async Task Inactive_users_cannot_sign_in_and_role_changes_end_sessions()
    {
        var clerk = await AddUser("clerk", UserRole.Standard);
        var auth = (Func<IServiceProvider, AuthService>)(sp => sp.GetRequiredService<AuthService>());
        var signedIn = await Run(sp => auth(sp).SignInAsync("clerk", "a long password"));
        Assert.Equal(UserRole.Standard, signedIn.User!.Role);

        await Run(sp => sp.GetRequiredService<UserService>().ChangeRoleAsync(clerk.Id, UserRole.ViewOnly));
        Assert.False(await Run(sp => auth(sp).IsSessionValidAsync(clerk.Id, signedIn.User.SecurityStamp)));

        await Run(sp => sp.GetRequiredService<UserService>().SetActiveAsync(clerk.Id, false));
        Assert.Equal(SignInStatus.InvalidCredentials, (await Run(sp => auth(sp).SignInAsync("clerk", "a long password"))).Status);
    }

    [Fact]
    public async Task Api_keys_authenticate_as_their_user_until_revoked_or_the_user_is_inactive()
    {
        var clerk = await AddUser("clerk", UserRole.Standard);
        var keys = (Func<IServiceProvider, ApiKeyService>)(sp => sp.GetRequiredService<ApiKeyService>());
        var created = await Run(sp => keys(sp).CreateAsync(clerk.Id, "Shop sync"));

        var user = await Run(sp => keys(sp).AuthenticateAsync(created.Token));
        Assert.Equal(("clerk", UserRole.Standard), (user!.Username, user.Role));
        Assert.Null(await Run(sp => keys(sp).AuthenticateAsync(created.Token + "x")));
        Assert.Null(await Run(sp => keys(sp).AuthenticateAsync("Bearer nonsense")));
        Assert.NotNull((await Run(sp => keys(sp).ListAsync(clerk.Id))).Single().LastUsedAt);

        await Run(sp => sp.GetRequiredService<UserService>().SetActiveAsync(clerk.Id, false));
        Assert.Null(await Run(sp => keys(sp).AuthenticateAsync(created.Token)));
        await Run(sp => sp.GetRequiredService<UserService>().SetActiveAsync(clerk.Id, true));
        Assert.NotNull(await Run(sp => keys(sp).AuthenticateAsync(created.Token)));

        var notMine = await Assert.ThrowsAsync<EntityNotFoundException>(() => Run(async sp => { await keys(sp).RevokeAsync(created.Key.Id, _db.Owner!.Id); return 0; }));
        Assert.Equal("api_key.not_found", notMine.Code);
        await Run(async sp => { await keys(sp).RevokeAsync(created.Key.Id, clerk.Id); return 0; });
        Assert.Null(await Run(sp => keys(sp).AuthenticateAsync(created.Token)));
    }

    [Fact]
    public async Task Webhooks_send_signed_events_for_changes_made_after_they_were_added()
    {
        await Run(sp => sp.GetRequiredService<CustomerService>().CreateAsync(new CustomerDetails("Before")));
        var hook = await Run(sp => sp.GetRequiredService<WebhookService>().CreateAsync("https://example.com/hook", "customer.*", "CRM"));
        await Run(sp => sp.GetRequiredService<CustomerService>().CreateAsync(new CustomerDetails("Acme")));
        await _db.CreateAccount("Not a customer", OpenBook.Domain.Accounts.AccountType.Expense);

        Assert.Equal(1, await Run(sp => sp.GetRequiredService<WebhookDispatcher>().RunOnceAsync()));
        var (url, body, headers) = Assert.Single(_db.Webhooks.Sent);
        Assert.Equal("https://example.com/hook", url);
        Assert.Equal("customer.created", headers["OpenBook-Event"]);

        using var json = JsonDocument.Parse(body);
        Assert.Equal("customer.created", json.RootElement.GetProperty("type").GetString());
        Assert.Equal("owner", json.RootElement.GetProperty("actor").GetString());
        Assert.Equal("Acme", json.RootElement.GetProperty("after").GetProperty("DisplayName").GetString());

        // The receiver checks the signature with the secret it was given.
        var signature = headers[WebhookSignature.Header];
        var timestamp = long.Parse(signature.Split(',')[0][2..], System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal(WebhookSignature.Compute(hook.Secret, timestamp, body), signature);

        // Nothing new: nothing sent.
        Assert.Equal(0, await Run(sp => sp.GetRequiredService<WebhookDispatcher>().RunOnceAsync()));
    }

    [Fact]
    public async Task Failed_deliveries_are_retried_later_and_can_be_sent_again()
    {
        var hook = await Run(sp => sp.GetRequiredService<WebhookService>().CreateAsync("https://example.com/hook", null, null));
        _db.Webhooks.Status = 500;
        await Run(sp => sp.GetRequiredService<WebhookService>().PingAsync(hook.Subscription.Id));
        await Run(sp => sp.GetRequiredService<WebhookDispatcher>().RunOnceAsync());

        var delivery = (await Run(sp => sp.GetRequiredService<WebhookService>().DeliveriesAsync(hook.Subscription.Id))).Single(d => d.EventType == "ping");
        Assert.Equal((WebhookDeliveryStatus.Pending, 1, 500), (delivery.Status, delivery.Attempts, delivery.LastStatusCode));

        // Not due again until a minute has passed.
        await Run(sp => sp.GetRequiredService<WebhookDispatcher>().RunOnceAsync());
        Assert.Equal(1, _db.Webhooks.Sent.Count(s => s.Headers["OpenBook-Event"] == "ping"));

        _db.Webhooks.Status = 200;
        _db.Clock.UtcNow = _db.Clock.UtcNow.AddMinutes(2);
        await Run(sp => sp.GetRequiredService<WebhookDispatcher>().RunOnceAsync());
        delivery = (await Run(sp => sp.GetRequiredService<WebhookService>().DeliveriesAsync(hook.Subscription.Id))).Single(d => d.EventType == "ping");
        Assert.Equal((WebhookDeliveryStatus.Succeeded, 2), (delivery.Status, delivery.Attempts));

        await Run(sp => sp.GetRequiredService<WebhookService>().RedeliverAsync(delivery.Id));
        await Run(sp => sp.GetRequiredService<WebhookDispatcher>().RunOnceAsync());
        Assert.Equal(3, _db.Webhooks.Sent.Count(s => s.Headers["OpenBook-Event"] == "ping"));
    }

    [Fact]
    public async Task Rotating_the_secret_signs_with_the_new_one()
    {
        var hook = await Run(sp => sp.GetRequiredService<WebhookService>().CreateAsync("https://example.com/hook", "invoice.*", null));
        var secret = await Run(sp => sp.GetRequiredService<WebhookService>().RotateSecretAsync(hook.Subscription.Id));
        Assert.NotEqual(hook.Secret, secret);

        await Run(sp => sp.GetRequiredService<WebhookService>().PingAsync(hook.Subscription.Id));
        await Run(sp => sp.GetRequiredService<WebhookDispatcher>().RunOnceAsync());
        var (_, body, headers) = Assert.Single(_db.Webhooks.Sent);
        var timestamp = long.Parse(headers[WebhookSignature.Header].Split(',')[0][2..], System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal(WebhookSignature.Compute(secret, timestamp, body), headers[WebhookSignature.Header]);
    }

    [Fact]
    public void Event_types_come_from_the_audit_entity_and_action()
    {
        var record = OpenBook.Domain.Auditing.AuditRecord.Create(DateTimeOffset.UtcNow, "owner", OpenBook.Domain.Auditing.AuditAction.ClosedPeriodOverride, "company", Guid.NewGuid(), null, "{}");
        Assert.Equal("company.closed_period_override", WebhookDispatcher.EventType(record));
    }
}

/// <summary>A real company file on disk, encrypted with SQLCipher.</summary>
public sealed class EncryptionTests : IAsyncLifetime
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), $"openbook-enc-{Guid.NewGuid():N}");
    private string File(string name) => Path.Combine(_folder, name);

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(_folder);
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(_folder, recursive: true);
        return Task.CompletedTask;
    }

    private static ServiceProvider Services(string path, string? key, string backups) => new ServiceCollection()
        .AddOpenBookApplication()
        .AddOpenBookInfrastructure($"Data Source={path}", backups, key)
        .AddSingleton<IClock>(new FixedClock())
        .AddSingleton<ICurrentUser, TestUser>()
        .AddSingleton<ISecretProtector, TestSecrets>()
        .AddSingleton<IEmailSender, RecordingEmailSender>()
        .BuildServiceProvider();

    private static async Task SetUpAsync(ServiceProvider services)
    {
        await services.MigrateOpenBookDatabaseAsync();
        await using var scope = services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<CompanySetupService>().SetUpAsync(
            new SetUpCompanyCommand("Secret Co", BusinessType.SoleProprietorship, "USD", 1, "owner", "correct horse battery"));
    }

    [Fact]
    public async Task An_encrypted_company_file_is_unreadable_without_its_key_and_backs_up_encrypted()
    {
        var path = File("company.db");
        await using (var services = Services(path, "s3cret-key", File("backups")))
        {
            await SetUpAsync(services);
            await using var scope = services.CreateAsyncScope();
            var backup = await scope.ServiceProvider.GetRequiredService<IBackupService>().CreateAsync(default);
            Assert.False(SqliteEncryption.IsPlain(backup.Path));
            await scope.ServiceProvider.GetRequiredService<IBackupService>().RestoreAsync(backup.Path, backup.FileName, default);
            System.IO.File.Delete(backup.Path);
        }

        SqliteConnection.ClearAllPools();
        Assert.False(SqliteEncryption.IsPlain(path));
        Assert.DoesNotContain("Secret Co", System.Text.Encoding.UTF8.GetString(await System.IO.File.ReadAllBytesAsync(path)), StringComparison.Ordinal);

        await using (var noKey = new SqliteConnection($"Data Source={path};Pooling=False"))
        {
            await noKey.OpenAsync();
            await using var command = noKey.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM sqlite_master;";
            await Assert.ThrowsAsync<SqliteException>(() => command.ExecuteScalarAsync());
        }

        await using (var wrongKey = Services(path, "wrong", File("backups")))
        {
            await using var scope = wrongKey.CreateAsyncScope();
            await Assert.ThrowsAnyAsync<Exception>(() => scope.ServiceProvider.GetRequiredService<CompanySetupService>().IsSetUpAsync());
        }
    }

    [Fact]
    public async Task A_plain_company_file_can_be_encrypted_and_decrypted_in_place()
    {
        var path = File("plain.db");
        await using (var services = Services(path, null, File("backups")))
            await SetUpAsync(services);
        SqliteConnection.ClearAllPools();
        Assert.True(SqliteEncryption.IsPlain(path));

        var kept = await SqliteEncryption.EncryptAsync(path, "new-key");
        Assert.True(SqliteEncryption.IsPlain(kept));
        Assert.False(SqliteEncryption.IsPlain(path));
        await using (var services = Services(path, "new-key", File("backups")))
        {
            await services.MigrateOpenBookDatabaseAsync();
            await using var scope = services.CreateAsyncScope();
            Assert.True(await scope.ServiceProvider.GetRequiredService<CompanySetupService>().IsSetUpAsync());
            // The ledger triggers came across with the schema.
            var db = scope.ServiceProvider.GetRequiredService<OpenBookDbContext>();
            var triggers = await db.Database.SqlQueryRaw<string>("SELECT name AS \"Value\" FROM sqlite_master WHERE type = 'trigger'").ToListAsync();
            Assert.Subset(triggers.ToHashSet(), SqliteLedgerTriggers.Names.ToHashSet());
        }

        SqliteConnection.ClearAllPools();
        var again = await Assert.ThrowsAsync<DomainException>(() => SqliteEncryption.EncryptAsync(path, "new-key"));
        Assert.Equal("encryption.already_encrypted", again.Code);
        await SqliteEncryption.DecryptAsync(path, "new-key");
        Assert.True(SqliteEncryption.IsPlain(path));
    }
}
