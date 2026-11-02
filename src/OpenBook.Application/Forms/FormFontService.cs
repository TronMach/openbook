using System.Text.Json;
using OpenBook.Domain.Auditing;
using OpenBook.Domain.Common;
using OpenBook.Domain.Companies;

namespace OpenBook.Application.Forms;

/// <summary>Fonts added for forms. Changes become visible only after <see cref="SaveChangesAsync"/>.</summary>
public interface IFormFontStore
{
    /// <summary>Tracked.</summary>
    Task<IReadOnlyList<FormFont>> ListAsync(CancellationToken ct);

    void Add(FormFont font);
    void Remove(FormFont font);
    void Add(AuditRecord record);

    Task SaveChangesAsync(CancellationToken ct);
}

/// <summary>A font shipped with OpenBook.</summary>
public sealed record BundledFont(string Key, string Family, string Category, string License);

/// <summary>The fonts shipped with OpenBook (files beside the app).</summary>
public interface IBundledFonts
{
    IReadOnlyList<BundledFont> List();

    /// <summary>Null when the files are missing.</summary>
    (byte[] Regular, byte[] Bold)? Load(string key);
}

/// <summary>Downloads a family's regular and bold TrueType faces from Google Fonts.</summary>
public interface IGoogleFonts
{
    /// <exception cref="DomainException">Unknown family, or Google Fonts can't be reached.</exception>
    Task<(byte[] Regular, byte[]? Bold)> FetchAsync(string family, CancellationToken ct);
}

/// <summary>A font ready to print with: its key, family and faces.</summary>
public sealed record FormFontFace(string Key, string Family, byte[] Regular, byte[]? Bold);

/// <summary>A font a design can choose; <see cref="Key"/> null = the built-in sans-serif.</summary>
public sealed record FormFontChoice(string? Key, string Family, string Group, bool CanRemove);

/// <summary>
/// The fonts forms can print with: the built-in sans-serif, those shipped with OpenBook, and fonts added from Google
/// Fonts or uploaded (stored in the company file, audited as <c>form_font</c>). A design whose font has gone prints
/// with the built-in one, so a missing font never stops a document from printing.
/// </summary>
public sealed class FormFontService(IFormFontStore store, IBundledFonts bundled, IGoogleFonts google, IFormDesignStore designs, IClock clock, ICurrentUser user)
{
    public const string EntityType = "form_font";
    public const string BuiltInName = "OpenBook Sans";

    /// <summary>The built-in font first, then the shipped ones by category, then those added, by name.</summary>
    public async Task<IReadOnlyList<FormFontChoice>> ChoicesAsync(CancellationToken ct = default)
    {
        var used = (await designs.ListAsync(ct)).SelectMany(d => new[] { d.FontKey, d.CompanyFontKey }).OfType<string>().ToHashSet(StringComparer.Ordinal);
        return
        [
            new FormFontChoice(null, $"{BuiltInName} (built in)", "Built in", false),
            .. bundled.List().OrderBy(f => f.Category, StringComparer.Ordinal).ThenBy(f => f.Family, StringComparer.Ordinal)
                .Select(f => new FormFontChoice(f.Key, f.Family, f.Category, false)),
            .. (await store.ListAsync(ct)).OrderBy(f => f.Family, StringComparer.OrdinalIgnoreCase)
                .Select(f => new FormFontChoice(f.Key, f.Family, f.Origin == FormFontOrigin.Google ? "Added from Google Fonts" : "Uploaded", !used.Contains(f.Key))),
        ];
    }

    public async Task<bool> ExistsAsync(string? key, CancellationToken ct = default) =>
        key is null || bundled.List().Any(f => f.Key == key) || (await store.ListAsync(ct)).Any(f => f.Key == key);

    /// <summary>The faces to print with; null for the built-in font, or when the font is gone.</summary>
    public async Task<FormFontFace?> FaceAsync(string? key, CancellationToken ct = default)
    {
        if (key is null)
            return null;
        if (bundled.List().FirstOrDefault(f => f.Key == key) is { } shipped)
            return bundled.Load(key) is { } files ? new FormFontFace(key, shipped.Family, files.Regular, files.Bold) : null;
        return (await store.ListAsync(ct)).FirstOrDefault(f => f.Key == key) is { } added ? new FormFontFace(added.Key, added.Family, added.Regular, added.Bold) : null;
    }

    /// <summary>Downloads a family from Google Fonts (regular and bold) and keeps it.</summary>
    public async Task<FormFont> AddFromGoogleAsync(string family, CancellationToken ct = default)
    {
        var name = FormFont.CheckFamily(family);
        await EnsureNewAsync(name, ct);
        var (regular, bold) = await google.FetchAsync(name, ct);
        return await SaveAsync(FormFont.Create(name, FormFontOrigin.Google, regular, bold, clock.UtcNow), ct);
    }

    /// <summary>A family uploaded as TrueType files; the bold face is optional.</summary>
    public async Task<FormFont> UploadAsync(string family, byte[] regular, byte[]? bold, CancellationToken ct = default)
    {
        var name = FormFont.CheckFamily(family);
        await EnsureNewAsync(name, ct);
        return await SaveAsync(FormFont.Create(name, FormFontOrigin.Uploaded, regular, bold, clock.UtcNow), ct);
    }

    /// <summary>Removes an added font no design uses.</summary>
    public async Task RemoveAsync(string key, CancellationToken ct = default)
    {
        var font = (await store.ListAsync(ct)).FirstOrDefault(f => f.Key == key) ?? throw new EntityNotFoundException(EntityType, Guid.Empty);
        if ((await designs.ListAsync(ct)).FirstOrDefault(d => d.FontKey == key || d.CompanyFontKey == key) is { } design)
            throw new DomainException("font.in_use", $"The {design.Name} design uses {font.Family}. Choose another font there first.");
        store.Remove(font);
        store.Add(AuditRecord.Create(clock.UtcNow, user.Actor, AuditAction.Deleted, EntityType, font.Id, Snapshot(font), null));
        await store.SaveChangesAsync(ct);
    }

    private async Task EnsureNewAsync(string family, CancellationToken ct)
    {
        if (bundled.List().Any(f => string.Equals(f.Family, family, StringComparison.OrdinalIgnoreCase)))
            throw new DomainException("font.built_in", $"{family} comes with OpenBook already: choose it in a design.");
        if (string.Equals(family, BuiltInName, StringComparison.OrdinalIgnoreCase)
            || (await store.ListAsync(ct)).Any(f => f.Key == FormFont.KeyFor(family)))
            throw new DomainException("font.exists", $"{family} has been added already.");
    }

    private async Task<FormFont> SaveAsync(FormFont font, CancellationToken ct)
    {
        store.Add(font);
        store.Add(AuditRecord.Create(clock.UtcNow, user.Actor, AuditAction.Created, EntityType, font.Id, null, Snapshot(font)));
        await store.SaveChangesAsync(ct);
        return font;
    }

    private static string Snapshot(FormFont f) => JsonSerializer.Serialize(new
    {
        f.Family,
        f.Key,
        Origin = f.Origin.ToString(),
        Regular = $"{f.Regular.Length:N0} bytes",
        Bold = f.Bold is { } bold ? $"{bold.Length:N0} bytes" : null,
    });
}
