using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using OpenBook.Application.Auditing;
using OpenBook.Domain.Auditing;
using OpenBook.Infrastructure;
using OpenBook.Infrastructure.Persistence;

namespace OpenBook.Web.Cli;

/// <summary>
/// <c>OpenBook.Web verify-audit [--against &lt;backup&gt;] [--fingerprint &lt;position&gt;:&lt;hash&gt;]</c>: checks the company's audit chain from the start and, with a
/// backup, that everything the backup's chain holds is still in the company's unchanged, which shows a rewrite that
/// recomputed every hash after it. Reads only, so OpenBook may be running. Exits 0 when all is well, 3 when the chain
/// is broken or differs from the backup (as <c>openbook check</c> does for problems), 1 when something can't be read, 2
/// for a mistake in the command.
/// </summary>
public static class VerifyAuditCommand
{
    public const string Name = "verify-audit";
    private const string Against = "--against";
    private const string Fingerprint = "--fingerprint";

    public static async Task<int> RunAsync(IServiceProvider services, string[] args)
    {
        string? backup = null;
        var at = Array.FindIndex(args, a => string.Equals(a, Against, StringComparison.OrdinalIgnoreCase));
        if (at >= 0)
        {
            if (at + 1 >= args.Length || args[at + 1].StartsWith('-'))
            {
                Console.Error.WriteLine("Say which backup: verify-audit --against <file>.");
                return 2;
            }

            backup = Path.GetFullPath(args[at + 1]);
            if (!File.Exists(backup))
            {
                Console.Error.WriteLine($"There's no file {backup}.");
                return 1;
            }
        }

        // A fingerprint noted earlier (the daily log line, the accountant's package, the books check): "218:d60eef84e6d0558d".
        (long Position, string Hash)? noted = null;
        var f = Array.FindIndex(args, a => string.Equals(a, Fingerprint, StringComparison.OrdinalIgnoreCase));
        if (f >= 0)
        {
            var parts = f + 1 < args.Length ? args[f + 1].Split(':', 2) : [];
            if (parts.Length != 2 || !long.TryParse(parts[0].Replace(",", "", StringComparison.Ordinal), out var position) || position < 1
                || parts[1].Length < 16 || !parts[1].All(Uri.IsHexDigit))
            {
                Console.Error.WriteLine("Give the position and at least 16 characters of its fingerprint: verify-audit --fingerprint 218:d60eef84e6d0558d.");
                return 2;
            }

            noted = (position, parts[1].ToLowerInvariant());
        }

        var storage = services.GetRequiredService<OpenBookStorage>();
        if (storage.Provider == DatabaseProvider.Sqlite && new SqliteConnectionStringBuilder(storage.ConnectionString).DataSource is { } file
            && !File.Exists(Path.GetFullPath(file)))
        {
            Console.Error.WriteLine($"There's no company file at {Path.GetFullPath(file)}.");
            return 1;
        }

        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OpenBookDbContext>();
        if ((await db.Database.GetPendingMigrationsAsync()).Any())
        {
            Console.Error.WriteLine("The company file is from an older OpenBook: start OpenBook once to bring it up to date, then check again.");
            return 1;
        }

        var chain = scope.ServiceProvider.GetRequiredService<AuditChainService>();
        var problems = false;
        Console.WriteLine("Checking the company's audit trail...");
        var report = await chain.VerifyAsync();
        problems |= Print(report);
        if (report.Waiting.Count > 0)
            Console.WriteLine($"  {(report.Waiting.Count > AuditChainService.MaxFindings ? $"More than {AuditChainService.MaxFindings}" : report.Waiting.Count.ToString("N0", System.Globalization.CultureInfo.CurrentCulture))} older audit record(s) haven't joined the chain yet, so they weren't checked. The chain grows while OpenBook runs.");

        if (noted is { } n)
        {
            Console.WriteLine();
            var read = await scope.ServiceProvider.GetRequiredService<IAuditChainStore>().ReadAsync(n.Position - 1, 1, CancellationToken.None);
            var link = read.Count > 0 ? read[0] : null;
            if (link is null || link.Position != n.Position)
            {
                problems = true;
                Console.WriteLine($"Position {n.Position:N0} isn't in the company's chain: it has been cut short, or the fingerprint is from another company.");
            }
            else if (!link.Hash.StartsWith(n.Hash, StringComparison.Ordinal))
            {
                problems = true;
                Console.WriteLine($"Position {n.Position:N0} has the fingerprint {AuditChain.Fingerprint(link.Hash)}, not {AuditChain.Fingerprint(n.Hash)}: the audit trail up to it has been rewritten since the fingerprint was noted, or the company was restored from an older backup.");
            }
            else
                Console.WriteLine($"Position {n.Position:N0} still has the fingerprint noted: nothing in the audit trail up to it has been rewritten.");
        }

        if (backup is null)
            return problems ? 3 : 0;

        Console.WriteLine();
        Console.WriteLine($"Checking the backup {Path.GetFileName(backup)}...");
        try
        {
            await using var source = await scope.ServiceProvider.GetRequiredService<IAuditChainFiles>().OpenAsync(backup, CancellationToken.None);
            if (!source.HasChain)
            {
                Console.Error.WriteLine("  This backup is from before OpenBook chained its audit trail (November 2026), so there's nothing to compare.");
                return 1;
            }

            problems |= Print(await AuditChainService.VerifyAsync(source));
            var comparison = await chain.CompareAsync(source);
            Console.WriteLine();
            if (comparison.Compared == 0)
                Console.WriteLine("The backup's chain is empty: nothing to compare.");
            else if (comparison.Same)
                Console.WriteLine($"All {comparison.Compared:N0} links in the backup are in the company's chain, unchanged: nothing in the audit trail up to the backup has been rewritten.");
            else
            {
                problems = true;
                Console.WriteLine("The company's audit trail differs from the backup's. Either it was rewritten after the backup was taken, or the company was restored from an older backup since (the audit log shows restores):");
                foreach (var difference in comparison.Differences)
                    Console.WriteLine($"  {difference.Text}");
            }
        }
        catch (SqliteException ex)
        {
            Console.Error.WriteLine($"  The backup can't be read ({ex.Message}). A backup is read with this install's encryption key, if it has one.");
            return 1;
        }

        return problems ? 3 : 0;
    }

    private static bool Print(AuditChainReport report)
    {
        if (report.Head is not { } head)
        {
            Console.WriteLine("  No audit records chained yet.");
            return false;
        }

        Console.WriteLine($"  {report.Checked:N0} links, up to position {head.Position:N0} (audit record #{head.RecordId:N0}, {AuditChain.Time(head.OccurredAt)}).");
        Console.WriteLine($"  Latest fingerprint: {head.Hash}");
        foreach (var finding in report.Findings)
            Console.WriteLine($"  {(finding.Serious ? "Problem" : "Note")}: {finding.Text}");
        if (!report.Broken)
            Console.WriteLine("  Unbroken: every record is as it was written.");
        return report.Broken;
    }
}
