namespace OpenBook.Application.Updates;

/// <summary>
/// The public half of the release signing key (ECDSA P-256). Installed copies only trust update manifests signed with its
/// private half, which is kept out of the repository (a GitHub Actions secret, and a backed-up file). Never change it
/// casually: copies built with the old key would stop trusting new releases.
/// </summary>
public static class UpdateKeys
{
    public const string PublicKey = """
        -----BEGIN PUBLIC KEY-----
        MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEfo9kXih89+QsmDPqJIf7AyXUhyL6
        EUA3I3hcGfqOUA+K3EEwzYei7A14R2k8RxbAYIvieE1qI8dMQkCt43VRUQ==
        -----END PUBLIC KEY-----
        """;
}
