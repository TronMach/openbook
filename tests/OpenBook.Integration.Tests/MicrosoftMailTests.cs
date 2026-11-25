using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OpenBook.Application.Companies;
using OpenBook.Application.Forms;
using OpenBook.Domain.Common;
using OpenBook.Domain.Companies;
using OpenBook.Infrastructure.Forms;
using OpenBook.Infrastructure.Persistence;

namespace OpenBook.Integration.Tests;

/// <summary>Microsoft's token service: hands out tokens for the code and refresh tokens it knows.</summary>
public sealed class FakeMicrosoft : HttpMessageHandler
{
    public const string ClientId = "1b4e28ba-2fa1-11d2-883f-0016d3cca427";
    public const string Tenant = "studio.example";
    public const string Code = "good-code";

    private int _issued;

    public List<Dictionary<string, string>> Asked { get; } = [];

    /// <summary>The refresh token that works now.</summary>
    public string Refresh { get; set; } = "refresh-0";

    /// <summary>Gives a new refresh token with each access token, as Microsoft may.</summary>
    public bool Rotates { get; set; }

    public int ExpiresIn { get; set; } = 3600;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.RequestUri!.AbsoluteUri != $"https://login.microsoftonline.com/{Tenant}/oauth2/v2.0/token")
            return Answer(HttpStatusCode.NotFound, new { error = "invalid_request", error_description = "AADSTS90002: Tenant not found.\r\nTrace ID: 1" });
        var body = await request.Content!.ReadAsStringAsync(cancellationToken);
        var form = body.Split('&').Select(p => p.Split('=', 2)).ToDictionary(p => p[0], p => Uri.UnescapeDataString(p[1].Replace('+', ' ')), StringComparer.Ordinal);
        Asked.Add(form);
        var good = form["client_id"] == ClientId
            && (form["grant_type"] == "authorization_code" ? form["code"] == Code && form.ContainsKey("code_verifier") : form["refresh_token"] == Refresh);
        if (!good)
            return Answer(HttpStatusCode.BadRequest, new { error = "invalid_grant", error_description = "AADSTS70008: The refresh token has expired.\r\nTrace ID: 2" });
        if (Rotates || form["grant_type"] == "authorization_code")
            Refresh = $"refresh-{_issued + 1}";
        return Answer(HttpStatusCode.OK, new
        {
            token_type = "Bearer",
            access_token = $"access-{++_issued}",
            refresh_token = Refresh,
            expires_in = ExpiresIn,
            id_token = "e30." + Convert.ToBase64String(Encoding.UTF8.GetBytes("""{"preferred_username":"books@studio.example"}""")).TrimEnd('=') + ".sig",
        });
    }

    private static HttpResponseMessage Answer(HttpStatusCode status, object value) =>
        new(status) { Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json") };
}

/// <summary>Sending email with Microsoft's sign-in: the app registration, signing the mailbox in from the settings page, and the tokens.</summary>
public sealed partial class MicrosoftMailTests : IAsyncLifetime
{
    private const string Password = "a long password";
    private readonly string _folder = Path.Combine(Path.GetTempPath(), $"openbook-msmail-{Guid.NewGuid():N}");
    private readonly RecordingEmailSender _email = new();
    private readonly FakeMicrosoft _microsoft = new();
    private WebTestDatabase _database = null!;
    private WebApplicationFactory<Program> _app = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_folder);
        _database = new WebTestDatabase(_folder, "msmail.db");
        await _database.CreateAsync();
        _app = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            _database.Apply(b);
            b.UseSetting("OpenBook:DataProtectionKeys", Path.Combine(_folder, "keys"));
            b.ConfigureTestServices(s =>
            {
                s.Replace(ServiceDescriptor.Singleton<IEmailSender>(_email));
                s.Replace(ServiceDescriptor.Singleton<IMicrosoftMailClient>(_ => new MicrosoftMailClient(_microsoft)));
            });
        });
        _ = _app.Server;
        await In<CompanySetupService, object?>(async s =>
        {
            await s.SetUpAsync(new SetUpCompanyCommand("Studio", BusinessType.SoleProprietorship, "USD", 1, "owner", Password));
            return null;
        });
    }

    public async Task DisposeAsync()
    {
        await _app.DisposeAsync();
        SqliteConnection.ClearAllPools();
        await _database.DropAsync();
        TestFolder.Delete(_folder);
    }

    private async Task<T> In<TService, T>(Func<TService, Task<T>> work) where TService : notnull
    {
        await using var scope = _app.Services.CreateAsyncScope();
        return await work(scope.ServiceProvider.GetRequiredService<TService>());
    }

    private Task<EmailSettings> UseMicrosoft() => In<EmailService, EmailSettings>(s => s.SaveSettingsAsync(new EmailSettingsCommand(
        EmailDelivery.Smtp, null, 587, SmtpSecurity.StartTls, null, null, false, "books@studio.example", "Studio", null, SignIn: SmtpSignIn.Microsoft)));

    private Task<MicrosoftMailSignIn> App(string? secret = "the-secret") =>
        In<MicrosoftMailService, MicrosoftMailSignIn>(s => s.SaveAppAsync(MailPurpose.Sending, FakeMicrosoft.ClientId, FakeMicrosoft.Tenant, secret));

    private Task Test() => In<EmailService, bool>(async s => { await s.SendTestAsync("pat@example.test"); return true; });

    private static async Task<string> Refused(Func<Task> work) => (await Assert.ThrowsAsync<DomainException>(work)).Code;

    [Fact]
    public async Task The_mailbox_is_signed_in_once_and_its_tokens_renewed_as_they_lapse()
    {
        Assert.Equal("email.microsoft_secure", await Refused(() => In<EmailService, EmailSettings>(s => s.SaveSettingsAsync(new EmailSettingsCommand(
            EmailDelivery.Smtp, null, 587, SmtpSecurity.None, null, null, false, "books@studio.example", null, null, SignIn: SmtpSignIn.Microsoft)))));
        Assert.Equal("smtp.office365.com", (await UseMicrosoft()).Host);
        Assert.Equal("email.microsoft_not_connected", await Refused(Test));
        Assert.Equal("email.microsoft_client_id", await Refused(() => In<MicrosoftMailService, MicrosoftMailSignIn>(s => s.SaveAppAsync(MailPurpose.Sending, "nonsense", null, null))));
        await App();
        var signedIn = await In<MicrosoftMailService, MicrosoftMailSignIn>(s => s.CompleteAsync(MailPurpose.Sending, FakeMicrosoft.Code, "verifier", "https://books.test/settings/email/microsoft/done"));
        Assert.Equal(("books@studio.example", true), (signedIn.Mailbox, signedIn.IsConnected));
        Assert.DoesNotContain("refresh-", signedIn.ProtectedRefreshToken, StringComparison.Ordinal);
        Assert.Equal("the-secret", _microsoft.Asked[0]["client_secret"]);

        // One access token serves until it's nearly up, and a new refresh token is kept.
        _microsoft.Rotates = true;
        await Test();
        await Test();
        Assert.Equal(["access-2", "access-2"], _email.Sent.Select(s => s.Password));
        _microsoft.ExpiresIn = 60;
        await In<MicrosoftMailService, MicrosoftMailSignIn>(s => s.SaveAppAsync(MailPurpose.Sending, FakeMicrosoft.ClientId, FakeMicrosoft.Tenant, null));
        await Test();
        await Test();
        Assert.Equal(["access-3", "access-4"], _email.Sent.Skip(2).Select(s => s.Password));

        // Microsoft withdraws it: the message says what to do, and nothing secret is in the audit log.
        _microsoft.Refresh = "withdrawn";
        Assert.Equal("email.microsoft_sign_in_again", await Refused(Test));
        var audit = await In<OpenBookDbContext, List<string?>>(db => db.AuditRecords.Where(a => a.EntityType == MicrosoftMailService.EntityType).Select(a => a.After).ToListAsync());
        Assert.All(audit, a => Assert.DoesNotContain("secret\"", a!.Replace("HasSecret\"", "", StringComparison.Ordinal), StringComparison.OrdinalIgnoreCase));
        Assert.All(audit, a => Assert.DoesNotContain("refresh", a, StringComparison.OrdinalIgnoreCase));

        // Another app's registration ends the sign-in: the token was the old one's.
        await In<MicrosoftMailService, MicrosoftMailSignIn>(s => s.SaveAppAsync(MailPurpose.Sending, Guid.NewGuid().ToString(), FakeMicrosoft.Tenant, null));
        var changed = await In<MicrosoftMailService, MicrosoftMailSignIn>(s => s.GetAsync(MailPurpose.Sending));
        Assert.Equal((false, (string?)null), (changed.IsConnected, changed.ProtectedSecret));
    }

    [Fact]
    public async Task Signing_in_goes_through_the_browser_and_only_the_one_who_started_it_can_finish()
    {
        await UseMicrosoft();
        await App(secret: null);
        using var client = await SignedInAsync();
        var page = await client.GetStringAsync("/settings/email");
        Assert.Contains("http://localhost:80/settings/email/microsoft/done", page, StringComparison.Ordinal);

        var out_ = await Post(client, page, "/settings/email/microsoft/sign-in");
        Assert.Equal(HttpStatusCode.Redirect, out_.StatusCode);
        var url = out_.Headers.Location!;
        Assert.StartsWith($"https://login.microsoftonline.com/{FakeMicrosoft.Tenant}/oauth2/v2.0/authorize", url.AbsoluteUri, StringComparison.Ordinal);
        Assert.Contains("SMTP.Send", url.AbsoluteUri, StringComparison.Ordinal);
        var state = Regex.Match(url.Query, "state=([^&]+)").Groups[1].Value;

        // Someone else's state, a refusal at Microsoft, and the same answer twice are all refused.
        Assert.Contains("error=", (await client.GetAsync("/settings/email/microsoft/done?code=good-code&state=forged")).Headers.Location!.ToString(), StringComparison.Ordinal);
        Assert.Empty(_microsoft.Asked);
        out_ = await Post(client, page, "/settings/email/microsoft/sign-in");
        state = Regex.Match(out_.Headers.Location!.Query, "state=([^&]+)").Groups[1].Value;
        var done = await client.GetAsync($"/settings/email/microsoft/done?code={FakeMicrosoft.Code}&state={state}");
        Assert.Equal("/settings/email?done=signed-in#microsoft", done.Headers.Location!.ToString());
        Assert.False(_microsoft.Asked[0].ContainsKey("client_secret"));
        Assert.Equal("http://localhost:80/settings/email/microsoft/done", _microsoft.Asked[0]["redirect_uri"]);
        Assert.Contains("error=", (await client.GetAsync($"/settings/email/microsoft/done?code={FakeMicrosoft.Code}&state={state}")).Headers.Location!.ToString(), StringComparison.Ordinal);
        Assert.Contains("as books@studio.example", await client.GetStringAsync("/settings/email"), StringComparison.Ordinal);
        await Test();
        Assert.Equal("access-2", Assert.Single(_email.Sent).Password);

        Assert.Equal("/settings/email?done=signed-out#microsoft", (await Post(client, page, "/settings/email/microsoft/sign-out")).Headers.Location!.ToString());
        Assert.Equal("email.microsoft_not_connected", await Refused(Test));
    }

    [Fact]
    public void Answers_are_read_or_refused_and_the_address_carries_no_secret()
    {
        Assert.Equal("email.microsoft_unreadable", Assert.Throws<DomainException>(() => MicrosoftMail.Token("nonsense")).Code);
        Assert.Equal("email.microsoft_unreadable", Assert.Throws<DomainException>(() => MicrosoftMail.Token("""{"token_type":"Bearer"}""")).Code);
        var token = MicrosoftMail.Token("""{"access_token":"a","expires_in":99999999,"id_token":"x.!!.y"}""");
        Assert.Equal((TimeSpan.FromSeconds(300), (string?)null, (string?)null), (token.ExpiresIn, token.Mailbox, token.RefreshToken));
        Assert.Equal("email.microsoft_app", MicrosoftMail.Error("""{"error":"invalid_client","error_description":"AADSTS7000215: Invalid client secret.\r\nTrace ID: x"}""", 401).Code);
        Assert.DoesNotContain("Trace", MicrosoftMail.Error("""{"error":"invalid_client","error_description":"Bad.\r\nTrace ID: x"}""", 401).Message, StringComparison.Ordinal);
        Assert.Equal("email.microsoft_refused", MicrosoftMail.Error(null, 503).Code);
        Assert.DoesNotContain("s3cret", MicrosoftMail.AuthorizeUrl(new MicrosoftApp(FakeMicrosoft.ClientId, "common", "s3cret"), MailPurpose.Bills, "http://localhost/x", "s", "v"), StringComparison.Ordinal);
    }

    private async Task<HttpClient> SignedInAsync()
    {
        var client = _app.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var login = await client.GetStringAsync("/login");
        var response = await client.PostAsync("/login", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = Token(login),
            ["_handler"] = "login",
            ["Input.Username"] = "owner",
            ["Input.Password"] = Password,
        }));
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        return client;
    }

    private static Task<HttpResponseMessage> Post(HttpClient client, string page, string address) =>
        client.PostAsync(address, new FormUrlEncodedContent(new Dictionary<string, string> { ["__RequestVerificationToken"] = Token(page) }));

    private static string Token(string html) => WebUtility.HtmlDecode(TokenPattern().Match(html).Groups[1].Value);

    [GeneratedRegex("name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"")]
    private static partial Regex TokenPattern();
}
