using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;
using OpenBook.Application.Companies;
using OpenBook.Domain.Common;
using OpenBook.Infrastructure.Persistence;

namespace OpenBook.Integration.Tests.Browser;

/// <summary>Passkeys through a real browser with a pretend security key: added, signed in with, and refused once removed.</summary>
[Collection(BrowserGroup.Name)]
public sealed class PasskeyTests(BrowserApp app)
{
    private static readonly float Slow = 20_000;

    // Passkeys need a named site: "localhost", not an address.
    private string Address => app.Address.Replace("127.0.0.1", "localhost", StringComparison.Ordinal);

    [BrowserFact]
    public async Task A_passkey_is_added_signs_in_and_stops_once_removed()
    {
        await using var page = await app.OpenAsync();
        var p = page.Page;
        var devTools = await page.Context.NewCDPSessionAsync(p);
        await devTools.SendAsync("WebAuthn.enable");
        await devTools.SendAsync("WebAuthn.addVirtualAuthenticator", new Dictionary<string, object>
        {
            ["options"] = new Dictionary<string, object>
            {
                ["protocol"] = "ctap2", ["transport"] = "internal", ["hasResidentKey"] = true, ["hasUserVerification"] = true, ["isUserVerified"] = true, ["automaticPresenceSimulation"] = true,
            },
        });

        await SignInAsync(p);
        await p.GotoAsync($"{Address}/account/passkeys");
        var add = p.GetByRole(AriaRole.Button, new() { Name = "Add a passkey" });
        await p.GetByLabel("What is it on?").FillAsync("Test key");
        await p.GetByLabel("Your password").FillAsync("not the password");
        await add.ClickAsync();
        await Assertions.Expect(p.Locator(".alert.error").First).ToContainTextAsync("isn't your password", new() { Timeout = Slow });

        await p.GetByLabel("What is it on?").FillAsync("Test key");
        await p.GetByLabel("Your password").FillAsync(BrowserApp.Password);
        await add.ClickAsync();
        await Assertions.Expect(p.Locator(".alert.notice")).ToContainTextAsync("Passkey added", new() { Timeout = Slow });
        var stored = await app.InScope(sp => sp.GetRequiredService<OpenBookDbContext>().UserPasskeys.AsNoTracking().SingleAsync());
        Assert.Equal(("Test key", "localhost"), (stored.Name, stored.Site));

        // Signed out, the passkey alone signs in, and its use is noted.
        await SignOutAsync(page);
        await p.GotoAsync($"{Address}/login?returnUrl=%2Faccount%2Fpasskeys");
        await p.GetByRole(AriaRole.Button, new() { Name = "Sign in with a passkey" }).ClickAsync();
        await p.WaitForURLAsync(new Regex("/account/passkeys$"), new() { Timeout = Slow });
        await Assertions.Expect(p.Locator("table.data tbody tr")).ToContainTextAsync("Test key");
        Assert.NotNull((await app.InScope(sp => sp.GetRequiredService<OpenBookDbContext>().UserPasskeys.AsNoTracking().SingleAsync())).LastUsedAt);

        // Removed here, the key the device still holds is refused.
        p.Dialog += (_, dialog) => dialog.AcceptAsync();
        await p.GetByRole(AriaRole.Button, new() { Name = "Remove Test key" }).ClickAsync();
        await Assertions.Expect(p.Locator(".alert.notice")).ToContainTextAsync("Passkey removed", new() { Timeout = Slow });
        await SignOutAsync(page);
        await p.GotoAsync($"{Address}/login");
        await p.GetByRole(AriaRole.Button, new() { Name = "Sign in with a passkey" }).ClickAsync();
        await Assertions.Expect(p.Locator(".alert.error").First).ToContainTextAsync("can't sign in here", new() { Timeout = Slow });
        await page.AssertCleanAsync();
    }

    [Fact]
    public void An_answer_for_another_site_challenge_or_key_is_refused()
    {
        using var key = System.Security.Cryptography.ECDsa.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP256);
        using var other = System.Security.Cryptography.ECDsa.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP256);
        var challenge = WebAuthn.Challenge();

        PasskeyAnswer Answer(string type = "webauthn.get", string? asked = null, string origin = "https://books.test", string site = "books.test", byte flags = 0x05,
            System.Security.Cryptography.ECDsa? signer = null)
        {
            var client = System.Text.Encoding.UTF8.GetBytes($$"""{"type":"{{type}}","challenge":"{{asked ?? challenge}}","origin":"{{origin}}"}""");
            byte[] data = [.. System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(site)), flags, 0, 0, 0, 7];
            var signature = (signer ?? key).SignData([.. data, .. System.Security.Cryptography.SHA256.HashData(client)], System.Security.Cryptography.HashAlgorithmName.SHA256,
                System.Security.Cryptography.DSASignatureFormat.Rfc3279DerSequence);
            return new PasskeyAnswer("id", Convert.ToBase64String(client), Convert.ToBase64String(data), Convert.ToBase64String(signature));
        }

        long Check(PasskeyAnswer answer) => WebAuthn.Verify(answer, key.ExportSubjectPublicKeyInfo(), WebAuthn.Es256, challenge, "https://books.test", "books.test");
        Assert.Equal(7, Check(Answer()));
        foreach (var bad in new[]
                 {
                     Answer(type: "webauthn.create"), Answer(asked: WebAuthn.Challenge()), Answer(origin: "https://books.test.evil.example"), Answer(site: "evil.example"),
                     Answer(flags: 0x01), Answer(signer: other), new PasskeyAnswer("id", "!", "!", "!"), new PasskeyAnswer(null, null, null, null),
                 })
            Assert.Equal("passkey.refused", Assert.Throws<DomainException>(() => Check(bad)).Code);
        Assert.Throws<DomainException>(() => WebAuthn.Verify(Answer(), key.ExportSubjectPublicKeyInfo(), WebAuthn.Rs256, challenge, "https://books.test", "books.test"));
    }

    private async Task SignInAsync(IPage p)
    {
        await p.GotoAsync($"{Address}/login", new() { Timeout = 120_000 });
        await p.Locator("#username").FillAsync(BrowserApp.Username);
        await p.Locator("#password").FillAsync(BrowserApp.Password);
        await p.GetByRole(AriaRole.Button, new() { Name = "Sign in", Exact = true }).ClickAsync();
        await p.WaitForURLAsync(url => !url.Contains("/login", StringComparison.Ordinal), new() { Timeout = 120_000 });
    }

    private static Task SignOutAsync(BrowserPage page) => page.Context.ClearCookiesAsync();
}
