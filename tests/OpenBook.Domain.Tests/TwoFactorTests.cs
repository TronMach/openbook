using System.Text;
using OpenBook.Domain.Common;
using OpenBook.Domain.Companies;

namespace OpenBook.Domain.Tests;

public sealed class TwoFactorTests
{
    private static readonly byte[] RfcSecret = Encoding.ASCII.GetBytes("12345678901234567890");

    // RFC 6238 appendix B (SHA-1), the last six of its eight digits.
    [Theory]
    [InlineData(59L, "287082")]
    [InlineData(1111111109L, "081804")]
    [InlineData(1111111111L, "050471")]
    [InlineData(1234567890L, "005924")]
    [InlineData(2000000000L, "279037")]
    [InlineData(20000000000L, "353130")]
    public void Codes_match_the_rfc_test_vectors(long unixSeconds, string expected) =>
        Assert.Equal(expected, Totp.Code(RfcSecret, Totp.Step(DateTimeOffset.FromUnixTimeSeconds(unixSeconds))));

    [Theory]
    [InlineData("", "")]
    [InlineData("f", "MY")]
    [InlineData("fo", "MZXQ")]
    [InlineData("foo", "MZXW6")]
    [InlineData("foob", "MZXW6YQ")]
    [InlineData("fooba", "MZXW6YTB")]
    [InlineData("foobar", "MZXW6YTBOI")]
    public void Base32_follows_rfc_4648_without_padding(string text, string encoded)
    {
        Assert.Equal(encoded, Base32.Encode(Encoding.ASCII.GetBytes(text)));
        Assert.Equal(text, Encoding.ASCII.GetString(Base32.Decode(encoded.ToLowerInvariant() + "==")!));
    }

    [Fact]
    public void Base32_refuses_other_characters() => Assert.Null(Base32.Decode("MZXW1"));

    [Fact]
    public void A_code_counts_one_step_either_side_and_never_twice()
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(1111111111);
        var step = Totp.Step(now);
        Assert.Equal(step, Totp.Match(RfcSecret, Totp.Code(RfcSecret, step), now, null));
        Assert.Equal(step - 1, Totp.Match(RfcSecret, Totp.Code(RfcSecret, step - 1), now, null));
        Assert.Equal(step + 1, Totp.Match(RfcSecret, "  " + Totp.Code(RfcSecret, step + 1).Insert(3, " "), now, null));
        Assert.Null(Totp.Match(RfcSecret, Totp.Code(RfcSecret, step - 2), now, null));
        Assert.Null(Totp.Match(RfcSecret, Totp.Code(RfcSecret, step), now, lastStep: step));
        Assert.Null(Totp.Match(RfcSecret, "12345", now, null));
        Assert.Null(Totp.Match(RfcSecret, "abcdef", now, null));
    }

    [Fact]
    public void The_setup_address_names_the_app_and_carries_the_secret()
    {
        var uri = Totp.Uri("OpenBook", "owner (Acme & Co)", RfcSecret);
        Assert.StartsWith("otpauth://totp/OpenBook:owner%20%28Acme%20%26%20Co%29?secret=GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ&issuer=OpenBook", uri, StringComparison.Ordinal);
        Assert.Contains("&digits=6&period=30", uri, StringComparison.Ordinal);
    }

    [Fact]
    public void Recovery_codes_are_used_once_however_they_are_typed()
    {
        var codes = TwoFactor.NewRecoveryCodes();
        Assert.Equal(TwoFactor.RecoveryCodeCount, codes.Distinct().Count());
        Assert.All(codes, c => Assert.Matches("^[a-z2-9]{5}-[a-z2-9]{5}$", c));

        var user = User.Create("owner", "hash", DateTimeOffset.UnixEpoch);
        user.EnableTwoFactor("protected", 1, codes.Select(TwoFactor.HashRecoveryCode), DateTimeOffset.UnixEpoch);
        Assert.Equal(10, user.RecoveryCodesLeft);

        var typed = codes[3].ToUpperInvariant().Replace("-", " ", StringComparison.Ordinal);
        Assert.True(user.UseRecoveryCode(TwoFactor.HashRecoveryCode(typed)));
        Assert.False(user.UseRecoveryCode(TwoFactor.HashRecoveryCode(codes[3])));
        Assert.Equal(9, user.RecoveryCodesLeft);
    }

    [Fact]
    public void Steps_are_accepted_in_order_only()
    {
        var user = User.Create("owner", "hash", DateTimeOffset.UnixEpoch);
        user.EnableTwoFactor("protected", 100, [], DateTimeOffset.UnixEpoch);
        Assert.False(user.AcceptTwoFactorStep(100));
        Assert.True(user.AcceptTwoFactorStep(101));
        Assert.False(user.AcceptTwoFactorStep(100));

        user.DisableTwoFactor();
        Assert.False(user.HasTwoFactor);
        Assert.Null(user.ProtectedTwoFactorSecret);
        Assert.Equal(0, user.RecoveryCodesLeft);
    }

    [Theory]
    [InlineData(null, UserRole.Admin, false)]
    [InlineData(UserRole.Admin, UserRole.Admin, true)]
    [InlineData(UserRole.Admin, UserRole.Accountant, false)]
    [InlineData(UserRole.Accountant, UserRole.Admin, true)]
    [InlineData(UserRole.ViewOnly, UserRole.ViewOnly, true)]
    public void Requirement_covers_the_role_and_those_above(UserRole? from, UserRole role, bool required)
    {
        var settings = SignInSettings.Create();
        settings.RequireTwoFactor(from);
        Assert.Equal(required, settings.RequiresTwoFactor(role));
        Assert.Throws<DomainException>(() => settings.RequireTwoFactor((UserRole)42));
    }
}
