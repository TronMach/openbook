using System.Text;
using Microsoft.Data.Sqlite;
using OpenBook.Application.Companies;
using OpenBook.Domain.Common;
using OpenBook.Infrastructure;
using OpenBook.Infrastructure.Persistence;

namespace OpenBook.Web.Cli;

/// <summary>
/// <c>OpenBook.Web reset-password [username]</c>: recovery for a forgotten password. Runs against the same
/// company file the web app would use (same configuration and arguments), then exits.
/// Reads the new password from the keyboard without echoing it, or one line per prompt when input is piped.
/// </summary>
public static class ResetPasswordCommand
{
    public const string Name = "reset-password";

    public static async Task<int> RunAsync(IServiceProvider services, string[] args)
    {
        // The username is the first bare word after the command; "--key=value" arguments are configuration.
        var username = args.Skip(1).FirstOrDefault(a => !a.StartsWith('-'));

        var dataSource = Path.GetFullPath(new SqliteConnectionStringBuilder(services.GetRequiredService<OpenBookStorage>().ConnectionString).DataSource);
        Console.WriteLine("OpenBook password reset");
        Console.WriteLine($"Company file: {dataSource}");

        // Don't let a typo in the path or working folder silently create a new, empty company file.
        if (!File.Exists(dataSource))
        {
            Console.Error.WriteLine("No company file there. Run this from the folder you start OpenBook from, or pass --ConnectionStrings:OpenBook=\"Data Source=<path>\".");
            return 1;
        }

        try
        {
            await services.MigrateOpenBookDatabaseAsync();

            var password = ReadSecret("New password: ");
            if (ReadSecret("Confirm new password: ") != password)
            {
                Console.Error.WriteLine("The passwords don't match. Nothing was changed.");
                return 1;
            }

            await using var scope = services.CreateAsyncScope();
            var reset = await scope.ServiceProvider.GetRequiredService<AuthService>()
                .ResetPasswordAsync(username, password, $"console:{Environment.UserName}");

            Console.WriteLine($"Password reset for '{reset}'. Any signed-in sessions have been signed out.");
            return 0;
        }
        catch (DomainException ex)
        {
            Console.Error.WriteLine($"{ex.Message} Nothing was changed.");
            return 1;
        }
    }

    private static string ReadSecret(string prompt)
    {
        Console.Write(prompt);
        if (Console.IsInputRedirected)
        {
            var line = Console.ReadLine() ?? string.Empty;
            Console.WriteLine();
            return line;
        }

        var secret = new StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter)
                break;
            if (key.Key == ConsoleKey.Backspace)
            {
                if (secret.Length > 0)
                {
                    secret.Length--;
                    Console.Write("\b \b");
                }
            }
            else if (!char.IsControl(key.KeyChar))
            {
                secret.Append(key.KeyChar);
                Console.Write('*');
            }
        }

        Console.WriteLine();
        return secret.ToString();
    }
}
