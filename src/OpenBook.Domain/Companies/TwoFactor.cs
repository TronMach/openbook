using System.Security.Cryptography;
using System.Text;
using OpenBook.Domain.Common;

namespace OpenBook.Domain.Companies;

/// <summary>
/// Time-based one-time passwords for authenticator apps (RFC 6238 over RFC 4226): HMAC-SHA-1, 6 digits, 30-second
/// steps. A code is accepted one step either side of now, and never for a step at or before the last one used.
/// </summary>
public static class Totp
{
    public const int Digits = 6;
    public const int StepSeconds = 30;
    public const int SecretBytes = 20;

    /// <summary>How many steps either side of now still count, for clocks a little out.</summary>
    public const int Window = 1;

    public static byte[] NewSecret() => RandomNumberGenerator.GetBytes(SecretBytes);

    public static long Step(DateTimeOffset time) => time.ToUnixTimeSeconds() / StepSeconds;

    public static string Code(byte[] secret, long step)
    {
        ArgumentNullException.ThrowIfNull(secret);
        Span<byte> counter = stackalloc byte[8];
        System.Buffers.Binary.BinaryPrimitives.WriteInt64BigEndian(counter, step);
        Span<byte> hash = stackalloc byte[20];
        // SHA-1 is what authenticator apps use for TOTP; as an HMAC it isn't affected by SHA-1's collision weakness.
#pragma warning disable CA5350
        HMACSHA1.HashData(secret, counter, hash);
#pragma warning restore CA5350
        var offset = hash[^1] & 0x0F;
        var value = ((hash[offset] & 0x7F) << 24) | (hash[offset + 1] << 16) | (hash[offset + 2] << 8) | hash[offset + 3];
        return (value % 1_000_000).ToString("D6", System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>The step <paramref name="code"/> belongs to, if it's right for now and later than <paramref name="lastStep"/>.</summary>
    public static long? Match(byte[] secret, string? code, DateTimeOffset now, long? lastStep)
    {
        var digits = TwoFactor.Digits(code);
        if (digits is not { Length: Digits })
            return null;
        var current = Step(now);
        for (var step = current - Window; step <= current + Window; step++)
        {
            if (step > (lastStep ?? long.MinValue) && CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(Code(secret, step)), Encoding.ASCII.GetBytes(digits)))
                return step;
        }

        return null;
    }

    /// <summary>The <c>otpauth://</c> address authenticator apps read from the QR code.</summary>
    public static string Uri(string issuer, string account, byte[] secret) =>
        $"otpauth://totp/{System.Uri.EscapeDataString(issuer)}:{System.Uri.EscapeDataString(account)}"
        + $"?secret={Base32.Encode(secret)}&issuer={System.Uri.EscapeDataString(issuer)}&algorithm=SHA1&digits={Digits}&period={StepSeconds}";
}

/// <summary>RFC 4648 base 32 without padding, the form authenticator apps take secrets in.</summary>
public static class Base32
{
    private const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    public static string Encode(ReadOnlySpan<byte> data)
    {
        var text = new StringBuilder((data.Length * 8 + 4) / 5);
        int buffer = 0, bits = 0;
        foreach (var b in data)
        {
            buffer = (buffer << 8) | b;
            bits += 8;
            while (bits >= 5)
            {
                text.Append(Alphabet[(buffer >> (bits - 5)) & 31]);
                bits -= 5;
            }
        }

        if (bits > 0)
            text.Append(Alphabet[(buffer << (5 - bits)) & 31]);
        return text.ToString();
    }

    /// <summary>Ignores case, spaces, dashes and padding; null when something else is in it.</summary>
    public static byte[]? Decode(string? text)
    {
        if (text is null)
            return null;
        var bytes = new List<byte>(text.Length * 5 / 8);
        int buffer = 0, bits = 0;
        foreach (var c in text)
        {
            if (c is ' ' or '-' or '=')
                continue;
            var value = Alphabet.IndexOf(char.ToUpperInvariant(c), StringComparison.Ordinal);
            if (value < 0)
                return null;
            buffer = (buffer << 5) | value;
            bits += 5;
            if (bits >= 8)
            {
                bytes.Add((byte)(buffer >> (bits - 8)));
                bits -= 8;
            }
        }

        return [.. bytes];
    }
}

/// <summary>Recovery codes and the shapes of what people type at the second step.</summary>
public static class TwoFactor
{
    public const int RecoveryCodeCount = 10;

    /// <summary>Letters and digits that can't be mistaken for each other (no 0/o, 1/l/i).</summary>
    private const string RecoveryAlphabet = "abcdefghjkmnpqrstuvwxyz23456789";

    /// <summary>"xxxxx-xxxxx": ten characters of 31, about 50 bits each.</summary>
    public static IReadOnlyList<string> NewRecoveryCodes() =>
        Enumerable.Range(0, RecoveryCodeCount).Select(_ =>
        {
            var chars = RandomNumberGenerator.GetItems<char>(RecoveryAlphabet, 10);
            return $"{new string(chars, 0, 5)}-{new string(chars, 5, 5)}";
        }).ToList();

    /// <summary>The stored form: SHA-256 of the code without dashes or spaces, in lower case.</summary>
    public static string HashRecoveryCode(string code) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(new string((code ?? string.Empty).Where(char.IsAsciiLetterOrDigit).Select(char.ToLowerInvariant).ToArray()))));

    /// <summary>The code's digits when it's only digits and spaces (a code from the app); null otherwise.</summary>
    public static string? Digits(string? code)
    {
        var trimmed = (code ?? string.Empty).Replace(" ", string.Empty, StringComparison.Ordinal).Trim();
        return trimmed.Length > 0 && trimmed.All(char.IsAsciiDigit) ? trimmed : null;
    }

    /// <summary>
    /// Who has to sign in with a second step: nobody (null), or every user whose role is at least the one given.
    /// </summary>
    public static bool Required(UserRole? from, UserRole role) => from is { } minimum && role >= minimum;
}

/// <summary>
/// How people sign in (one row, created on first save): who must use two-factor sign-in. Single sign-on settings
/// join it later.
/// </summary>
public sealed class SignInSettings
{
    private SignInSettings()
    {
    }

    public Guid Id { get; private set; }

    /// <summary>Null: two-factor is up to each person; otherwise required for this role and every role above it.</summary>
    public UserRole? TwoFactorRequiredFrom { get; private set; }

    public static SignInSettings Create() => new() { Id = Guid.CreateVersion7() };

    public void RequireTwoFactor(UserRole? from) =>
        TwoFactorRequiredFrom = from is null || Enum.IsDefined(from.Value)
            ? from
            : throw new DomainException("user.role_invalid", $"Unknown role {from}.");

    public bool RequiresTwoFactor(UserRole role) => TwoFactor.Required(TwoFactorRequiredFrom, role);
}
