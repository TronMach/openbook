using OpenBook.Application.Updates;

// Release chores for OpenBook Desktop's updates (see docs/admin/releasing.md):
//   keygen --out <private key file>       a new signing key pair; prints the public key to build into UpdateKeys
//   manifest --installer <file> --version <x.y.z> --url <download url> [--notes <url>] [--minimum <x.y.z>] [--key <file>] --out <latest.json>
//                                         signs a manifest; the key comes from --key or the OPENBOOK_UPDATE_KEY variable (PEM text)
//   verify --manifest <latest.json> [--installer <file>]
//                                         checks the signature against the built-in key, and the installer's hash and size
static string? Option(string[] args, string name)
{
    var i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}

static int Fail(string message)
{
    Console.Error.WriteLine(message);
    return 2;
}

if (args.Length == 0)
    return Fail("Commands: keygen, manifest, verify. See docs/admin/releasing.md.");

switch (args[0])
{
    case "keygen":
    {
        var path = Option(args, "--out") ?? throw new ArgumentException("Give --out <private key file>.");
        if (File.Exists(path))
            return Fail($"{path} already exists; a new key would stop every installed copy from trusting new releases.");
        var (privateKey, publicKey) = UpdateManifest.NewKeyPair();
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        await File.WriteAllTextAsync(path, privateKey);
        Console.WriteLine($"Private key written to {path}. Keep it secret and backed up.");
        Console.WriteLine("Public key (put it in UpdateKeys.PublicKey):");
        Console.WriteLine(publicKey);
        return 0;
    }

    case "manifest":
    {
        var installer = Option(args, "--installer");
        var version = Option(args, "--version");
        var url = Option(args, "--url");
        var output = Option(args, "--out");
        if (installer is null || version is null || url is null || output is null)
            return Fail("Give --installer, --version, --url and --out.");
        if (UpdateVersion.Parse(version) is null)
            return Fail($"{version} isn't a version number.");
        var keyPem = Option(args, "--key") is { } keyFile ? await File.ReadAllTextAsync(keyFile) : Environment.GetEnvironmentVariable("OPENBOOK_UPDATE_KEY");
        if (string.IsNullOrWhiteSpace(keyPem))
            return Fail("Give --key <private key file> or set OPENBOOK_UPDATE_KEY.");

        await using var file = File.OpenRead(installer);
        var manifest = new UpdateManifest(version, url, await UpdateManifest.HashAsync(file), file.Length, Option(args, "--minimum"), Option(args, "--notes"), DateTimeOffset.UtcNow)
            .SignWith(keyPem);
        if (!manifest.IsSignedBy(UpdateKeys.PublicKey))
            return Fail("That key isn't the one built into OpenBook (UpdateKeys.PublicKey): installed copies wouldn't trust this release.");
        await File.WriteAllTextAsync(output, manifest.ToJson());
        Console.WriteLine($"{output}: version {manifest.Version}, {manifest.Size:N0} bytes, SHA-256 {manifest.Sha256}");
        return 0;
    }

    case "verify":
    {
        var path = Option(args, "--manifest") ?? throw new ArgumentException("Give --manifest <latest.json>.");
        var manifest = UpdateManifest.Parse(await File.ReadAllTextAsync(path));
        if (manifest is null)
            return Fail($"{path} isn't an update manifest.");
        if (!manifest.IsSignedBy(UpdateKeys.PublicKey))
            return Fail("The signature isn't the release key's.");
        if (Option(args, "--installer") is { } installer)
        {
            await using var file = File.OpenRead(installer);
            if (file.Length != manifest.Size || !string.Equals(await UpdateManifest.HashAsync(file), manifest.Sha256, StringComparison.OrdinalIgnoreCase))
                return Fail("The installer doesn't match the manifest.");
        }

        Console.WriteLine($"Good: version {manifest.Version}, signed by the release key.");
        return 0;
    }

    default:
        return Fail($"Unknown command {args[0]}.");
}
