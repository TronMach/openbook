using OpenBook.Application.Companies;
using OpenBook.Domain.Companies;

namespace OpenBook.Application.Import.Migration;

/// <param name="Owner">Null when the file had errors: then nothing was kept, not even the company.</param>
public sealed record MigrationSetupResult(User? Owner, MigrationResult Result);

/// <summary>
/// Setting up a company from another program's export: the company and its owner are created and the file is imported
/// in one database transaction, so a file with errors leaves the database as it was (still waiting for setup).
/// The caller makes the new owner the audit actor, since nobody is signed in yet.
/// </summary>
public sealed class MigrationSetupService(CompanySetupService setup, MigrationImportService import, IUnitOfWork unitOfWork)
{
    /// <summary>The company's details in the file, to fill in the setup form; null for formats without them (IIF, CSV, add-ins).</summary>
    public static SourceCompany? CompanyIn(string source, byte[] content) =>
        Enum.TryParse<MigrationSource>(source, ignoreCase: true, out var builtIn) && Enum.IsDefined(builtIn) ? MigrationReaders.ReadCompany(builtIn, content) : null;

    public async Task<MigrationSetupResult> SetUpAsync(SetUpCompanyCommand command, string source, byte[] content, CancellationToken ct = default)
    {
        await using var transaction = await unitOfWork.BeginAsync(ct);
        var owner = await setup.SetUpAsync(command, ct);
        var preview = await import.PreviewAsync(source, content, ct);
        if (preview.ErrorCount > 0)
            return new MigrationSetupResult(null, preview); // not committed: the company goes too
        var result = preview.NewCount > 0 ? await import.ImportAsync(source, content, ct) : preview;
        await transaction.CommitAsync(ct);
        return new MigrationSetupResult(owner, result);
    }
}
