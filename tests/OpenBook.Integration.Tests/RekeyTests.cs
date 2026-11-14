using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OpenBook.Application.Companies;
using OpenBook.Application.Forms;
using OpenBook.Domain.Companies;
using OpenBook.Domain.Common;
using OpenBook.Infrastructure.Persistence;
using OpenBook.Web.Cli;

namespace OpenBook.Integration.Tests;

/// <summary>New keys for a key that may have leaked: the company file's encryption key and the keys that protect stored secrets.</summary>
public sealed class RekeyTests : IAsyncLifetime
{
    private const string Password = "a long password";
    private readonly string _folder = Path.Combine(Path.GetTempPath(), $"openbook-rekey-{Guid.NewGuid():N}");
    private WebTestDatabase _database = null!;
    private WebApplicationFactory<Program> _app = null!;

    private string Keys => Path.Combine(_folder, "keys");

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_folder);
        _database = new WebTestDatabase(_folder, "rekey.db");
        await _database.CreateAsync();
        _app = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            _database.Apply(b);
            b.UseSetting("OpenBook:DataProtectionKeys", Keys);
        });
        _ = _app.Server;
    }

    public async Task DisposeAsync()
    {
        await _app.DisposeAsync();
        SqliteConnection.ClearAllPools();
        await _database.DropAsync();
        TestFolder.Delete(_folder);
    }

    [SqliteFact]
    public async Task A_rekeyed_company_file_opens_with_the_new_key_and_not_the_old()
    {
        var path = Path.Combine(_folder, "company.db");
        await using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString()))
        {
            await connection.OpenAsync();
            await using var create = connection.CreateCommand();
            create.CommandText = "PRAGMA journal_mode = WAL; CREATE TABLE T (Id INTEGER); INSERT INTO T VALUES (42);";
            await create.ExecuteNonQueryAsync();
        }

        await SqliteEncryption.EncryptAsync(path, "old key");
        var kept = await SqliteEncryption.RekeyAsync(path, "old key", "new key");

        Assert.Equal(42L, await ReadAsync(path, "new key"));
        await Assert.ThrowsAsync<SqliteException>(() => ReadAsync(path, "old key"));
        // The file under the old key is kept, as backups made before still need it.
        Assert.Equal(42L, await ReadAsync(kept, "old key"));
        Assert.Equal("encryption.same_key", (await Assert.ThrowsAsync<DomainException>(() => SqliteEncryption.RekeyAsync(path, "new key", "new key"))).Code);
        Assert.Equal("encryption.not_encrypted", (await Assert.ThrowsAsync<DomainException>(() => SqliteEncryption.RekeyAsync(PlainCopy(), "x", "y"))).Code);
    }

    [Fact]
    public async Task Rekeying_the_secrets_writes_them_with_a_new_key_and_the_old_keys_no_longer_read_them()
    {
        var owner = await InScope(async sp =>
        {
            await sp.GetRequiredService<CompanySetupService>().SetUpAsync(
                new SetUpCompanyCommand("Rekey Co", BusinessType.SoleProprietorship, "USD", 1, "owner", Password));
            var user = await sp.GetRequiredService<OpenBookDbContext>().Users.SingleAsync();
            var setup = await sp.GetRequiredService<TwoFactorService>().NewSetupAsync(user.Id);
            await sp.GetRequiredService<TwoFactorService>().EnableAsync(user.Id, setup.Secret,
                Totp.Code(Base32.Decode(setup.Secret)!, Totp.Step(DateTimeOffset.UtcNow)));
            return (user.Id, setup.Secret);
        });
        var before = await StoredSecretAsync(owner.Id);
        // Someone took a copy of the keys folder.
        var copy = Path.Combine(_folder, "keys-copy");
        Directory.CreateDirectory(copy);
        foreach (var file in Directory.GetFiles(Keys))
            File.Copy(file, Path.Combine(copy, Path.GetFileName(file)));
        Assert.Equal(owner.Secret, Copied(copy).Unprotect(before));

        Assert.Equal(0, await RekeyCommands.RekeySecretsAsync(_app.Services));

        var after = await StoredSecretAsync(owner.Id);
        Assert.NotEqual(before, after);
        // OpenBook, started again with its keys folder, reads it.
        Assert.Equal(owner.Secret, Copied(Keys).Unprotect(after));
        Assert.Null(Copied(copy).Unprotect(after));
        var keys = _app.Services.GetRequiredService<IKeyManager>().GetAllKeys();
        Assert.Single(keys, k => !k.IsRevoked);
        Assert.Contains(keys, k => k.IsRevoked);
        // Said in the audit log.
        Assert.True(await InScope(sp => sp.GetRequiredService<OpenBookDbContext>().AuditRecords.AnyAsync(a => a.After != null && a.After.Contains("SecretsRekeyed"))));
    }

    private string PlainCopy()
    {
        var path = Path.Combine(_folder, "plain.db");
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        connection.Open();
        using var create = connection.CreateCommand();
        create.CommandText = "CREATE TABLE IF NOT EXISTS T (Id INTEGER);";
        create.ExecuteNonQuery();
        return path;
    }

    private static async Task<long> ReadAsync(string path, string key)
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false, Password = key }.ToString());
        await connection.OpenAsync();
        await using var read = connection.CreateCommand();
        read.CommandText = "SELECT Id FROM T";
        return (long)(await read.ExecuteScalarAsync())!;
    }

    private Task<string> StoredSecretAsync(Guid userId) => InScope(async sp =>
        (await sp.GetRequiredService<OpenBookDbContext>().Users.AsNoTracking().SingleAsync(u => u.Id == userId)).ProtectedTwoFactorSecret!);

    /// <summary>The secrets protector a copy of the keys folder gives, as OpenBook builds its own.</summary>
    private static CopiedKeys Copied(string folder) => new(
        DataProtectionProvider.Create(new DirectoryInfo(folder), b => b.SetApplicationName("OpenBook")).CreateProtector("OpenBook.Secrets.v1"));

    private sealed class CopiedKeys(IDataProtector protector) : ISecretProtector
    {
        public string Protect(string plaintext) => protector.Protect(plaintext);

        public string? Unprotect(string protectedText)
        {
            try
            {
                return protector.Unprotect(protectedText);
            }
            catch (System.Security.Cryptography.CryptographicException)
            {
                return null;
            }
        }
    }

    private async Task<T> InScope<T>(Func<IServiceProvider, Task<T>> work)
    {
        await using var scope = _app.Services.CreateAsyncScope();
        return await work(scope.ServiceProvider);
    }
}
