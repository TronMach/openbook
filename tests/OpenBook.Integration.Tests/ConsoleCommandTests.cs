using System.Diagnostics;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using OpenBook.Application.Accounts;
using OpenBook.Application.Auditing;
using OpenBook.Application.Companies;
using OpenBook.Domain.Accounts;
using OpenBook.Domain.Companies;

namespace OpenBook.Integration.Tests;

/// <summary>
/// The commands OpenBook.Web takes, run as their own process on a company file, as an admin would: checking the audit
/// trail (reads only) and resetting a forgotten password. SQLite only: they're about the file.
/// </summary>
public sealed class ConsoleCommandTests : IAsyncLifetime
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), $"openbook-command-{Guid.NewGuid():N}");

    private string CompanyFile => Path.Combine(_folder, "company.db");

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_folder);
        await using var app = App();
        _ = app.Server;
        await using var scope = app.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<CompanySetupService>().SetUpAsync(
            new SetUpCompanyCommand("Studio", BusinessType.SoleProprietorship, "USD", 1, "owner", "a long password"));
        await scope.ServiceProvider.GetRequiredService<AccountService>().CreateAsync(new CreateAccountCommand("Petty cash", AccountType.Bank));
        await scope.ServiceProvider.GetRequiredService<AuditChainService>().ExtendAsync(late: true);
    }

    public Task DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        TestFolder.Delete(_folder);
        return Task.CompletedTask;
    }

    private WebApplicationFactory<Program> App() => new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
    {
        b.UseSetting("ConnectionStrings:OpenBook", $"Data Source={CompanyFile}");
        b.UseSetting("OpenBook:DataProtectionKeys", Path.Combine(_folder, "keys"));
        b.UseSetting("OpenBook:SetupCode", WebTestDatabase.SetupCode);
    });

    private async Task<(int Code, string Output)> RunAsync(string[] arguments, string? input = null, string? file = null)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = AppContext.BaseDirectory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "OpenBook.Web.dll"));
        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);
        start.ArgumentList.Add($"--ConnectionStrings:OpenBook=Data Source={file ?? CompanyFile}");
        start.ArgumentList.Add($"--OpenBook:DataProtectionKeys={Path.Combine(_folder, "keys")}");
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        if (input is not null)
            await process.StandardInput.WriteAsync(input);
        process.StandardInput.Close();
        using var limit = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        await process.WaitForExitAsync(limit.Token);
        return (process.ExitCode, await output + await error);
    }

    [SqliteFact]
    public async Task The_audit_trail_is_checked_and_a_changed_record_found()
    {
        var sound = await RunAsync(["verify-audit"]);
        Assert.True(sound.Code == 0, sound.Output);
        Assert.Contains("Unbroken", sound.Output, StringComparison.Ordinal);

        // A backup's chain is all there, and a fingerprint noted from it still fits.
        var backup = Path.Combine(_folder, "backup.db");
        File.Copy(CompanyFile, backup);
        var compared = await RunAsync(["verify-audit", "--against", backup]);
        Assert.True(compared.Code == 0, compared.Output);
        Assert.Contains("unchanged", compared.Output, StringComparison.Ordinal);
        Assert.Equal(3, (await RunAsync(["verify-audit", "--fingerprint", "1:0000000000000000"])).Code);

        // Mistakes in the command are told apart from problems in the books.
        Assert.Equal(2, (await RunAsync(["verify-audit", "--against"])).Code);
        Assert.Equal(2, (await RunAsync(["verify-audit", "--fingerprint", "nonsense"])).Code);
        Assert.Equal(1, (await RunAsync(["verify-audit", "--against", Path.Combine(_folder, "none.db")])).Code);
        var missing = Path.Combine(_folder, "typo.db");
        Assert.Equal(1, (await RunAsync(["verify-audit"], file: missing)).Code);
        Assert.False(File.Exists(missing));

        // Someone with the file rewrites a record behind OpenBook's back.
        await ExecuteAsync("DROP TRIGGER TR_AuditRecords_NoUpdate; UPDATE AuditRecords SET Actor = 'someone else' WHERE Id = (SELECT min(Id) FROM AuditRecords);");
        var changed = await RunAsync(["verify-audit"]);
        Assert.True(changed.Code == 3, changed.Output);
        Assert.Contains("Problem", changed.Output, StringComparison.Ordinal);
    }

    [SqliteFact]
    public async Task A_forgotten_password_is_reset_from_the_console()
    {
        var different = await RunAsync(["reset-password", "owner"], "a new long password\nanother long password\n");
        Assert.True(different.Code == 1, different.Output);
        Assert.Contains("don't match", different.Output, StringComparison.Ordinal);
        Assert.Equal(1, (await RunAsync(["reset-password", "nobody"], "a new long password\na new long password\n")).Code);
        Assert.Equal(1, (await RunAsync(["reset-password", "owner"], "short\nshort\n")).Code);
        var missing = Path.Combine(_folder, "typo.db");
        Assert.Equal(1, (await RunAsync(["reset-password", "owner"], "x\nx\n", missing)).Code);
        Assert.False(File.Exists(missing));
        Assert.Equal(SignInStatus.Succeeded, await SignInAsync("a long password"));

        var reset = await RunAsync(["reset-password", "owner", "--restore-admin"], "a new long password\na new long password\n");
        Assert.True(reset.Code == 0, reset.Output);
        Assert.DoesNotContain("a new long password", reset.Output, StringComparison.Ordinal);
        Assert.NotEqual(SignInStatus.Succeeded, await SignInAsync("a long password"));
        Assert.Equal(SignInStatus.Succeeded, await SignInAsync("a new long password"));
    }

    private async Task<SignInStatus> SignInAsync(string password)
    {
        await using var app = App();
        _ = app.Server;
        await using var scope = app.Services.CreateAsyncScope();
        return (await scope.ServiceProvider.GetRequiredService<AuthService>().SignInAsync("owner", password)).Status;
    }

    private async Task ExecuteAsync(string sql)
    {
        SqliteConnection.ClearAllPools();
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = CompanyFile, Pooling = false }.ToString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }
}
