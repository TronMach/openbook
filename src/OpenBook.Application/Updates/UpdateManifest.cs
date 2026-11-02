using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace OpenBook.Application.Updates;

/// <summary>
/// What a release publishes as <c>latest.json</c> beside its installer: the version, where the installer is, its SHA-256
/// and size, the oldest version that may update straight to it, the release notes, and a signature over all of that
/// (<see cref="SignedText"/>) made with the release key (ECDSA P-256, SHA-256). An app only trusts a manifest signed by the
/// key it was built with (<see cref="UpdateKeys"/>), and only runs an installer whose hash matches, so neither a hacked
/// download host nor a changed file can push an update.
/// </summary>
public sealed record UpdateManifest(
    string Version,
    string Url,
    string Sha256,
    long Size,
    string? MinimumVersion,
    string? NotesUrl,
    DateTimeOffset PublishedAt,
    string Signature = "")
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    /// <summary>The exact text the signature covers: one field per line, in this order, after a format marker.</summary>
    public string SignedText => string.Join('\n',
        "openbook-update-1",
        Version,
        Url,
        Sha256.ToLowerInvariant(),
        Size.ToString(CultureInfo.InvariantCulture),
        MinimumVersion ?? "",
        NotesUrl ?? "",
        PublishedAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));

    /// <summary>Signs the manifest with a private key (PEM); the release tool does this.</summary>
    public UpdateManifest SignWith(string privateKeyPem)
    {
        using var key = ECDsa.Create();
        key.ImportFromPem(privateKeyPem);
        return this with { Signature = Convert.ToBase64String(key.SignData(Encoding.UTF8.GetBytes(SignedText), HashAlgorithmName.SHA256)) };
    }

    /// <summary>Whether the signature is the release key's (a public key, PEM) over these exact fields.</summary>
    public bool IsSignedBy(string publicKeyPem)
    {
        byte[] signature;
        try
        {
            signature = Convert.FromBase64String(Signature);
        }
        catch (FormatException)
        {
            return false;
        }

        using var key = ECDsa.Create();
        key.ImportFromPem(publicKeyPem);
        return key.VerifyData(Encoding.UTF8.GetBytes(SignedText), signature, HashAlgorithmName.SHA256);
    }

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    /// <summary>Null when the text isn't a manifest.</summary>
    public static UpdateManifest? Parse(string json)
    {
        try
        {
            var manifest = JsonSerializer.Deserialize<UpdateManifest>(json, Json);
            return manifest is { Version.Length: > 0, Url.Length: > 0, Sha256.Length: 64, Size: > 0 } ? manifest : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The SHA-256 of a file, as lower-case hex.</summary>
    public static async Task<string> HashAsync(Stream stream, CancellationToken ct = default) =>
        Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, ct));

    /// <summary>A new key pair for signing releases: the private key (keep it secret) and the public key (built into the app).</summary>
    public static (string PrivateKeyPem, string PublicKeyPem) NewKeyPair()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return (key.ExportPkcs8PrivateKeyPem(), key.ExportSubjectPublicKeyInfoPem());
    }
}

/// <summary>Release versions: the leading numbers of a version (a "-beta" or "+commit" suffix is ignored).</summary>
public static class UpdateVersion
{
    public static Version? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        var core = text.Trim().TrimStart('v', 'V').Split('+', '-')[0];
        if (!Version.TryParse(core, out var version))
            return null;
        // 1.2 and 1.2.0 are the same release.
        return new Version(version.Major, version.Minor, Math.Max(0, version.Build), Math.Max(0, version.Revision));
    }

    /// <summary>True when <paramref name="candidate"/> is a newer release than <paramref name="current"/>.</summary>
    public static bool IsNewer(string? candidate, string? current) =>
        Parse(candidate) is { } next && Parse(current) is { } now && next > now;
}
