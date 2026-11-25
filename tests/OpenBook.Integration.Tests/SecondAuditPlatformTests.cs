using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OpenBook.Application.Accounts;
using OpenBook.Application.Attachments;
using OpenBook.Application.Banking;
using OpenBook.Application.Companies;
using OpenBook.Application.Forms;
using OpenBook.Application.Nonprofit;
using OpenBook.Application.Purchases;
using OpenBook.Application.Sales;
using OpenBook.Cli;
using OpenBook.Domain.Accounts;
using OpenBook.Domain.Companies;
using OpenBook.Domain.Email;
using OpenBook.Domain.Nonprofit;
using OpenBook.Domain.Purchases;
using OpenBook.Domain.Sales;
using OpenBook.Infrastructure.Persistence;
using OpenBook.Web.Diagnostics;

namespace OpenBook.Integration.Tests;

/// <summary>What the second audit found in sign-in, access, alerts, telemetry and the command line (PLAT).</summary>
public sealed partial class SecondAuditPlatformTests : IAsyncLifetime
{
    private const string Password = "a long password";
    private readonly string _folder = Path.Combine(Path.GetTempPath(), $"openbook-audit2-{Guid.NewGuid():N}");
    private readonly RecordingEmailSender _email = new();
    private WebTestDatabase _database = null!;
    private WebApplicationFactory<Program> _app = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_folder);
        _database = new WebTestDatabase(_folder, "audit2.db");
        await _database.CreateAsync();
        _app = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            _database.Apply(b);
            b.UseSetting("OpenBook:DataProtectionKeys", Path.Combine(_folder, "keys"));
            b.UseEnvironment("Development");
            b.ConfigureTestServices(s => s.Replace(ServiceDescriptor.Singleton<IEmailSender>(_email)));
        });
        _ = _app.Server;
        await Services(sp => sp.GetRequiredService<CompanySetupService>().SetUpAsync(
            new SetUpCompanyCommand("Audit Co", BusinessType.Nonprofit, "USD", 1, "owner", Password)));
    }

    public async Task DisposeAsync()
    {
        await _app.DisposeAsync();
        SqliteConnection.ClearAllPools();
        await _database.DropAsync();
        TestFolder.Delete(_folder);
    }

    private async Task<T> Services<T>(Func<IServiceProvider, Task<T>> work)
    {
        await using var scope = _app.Services.CreateAsyncScope();
        return await work(scope.ServiceProvider);
    }

    private Task<T> In<TService, T>(Func<TService, Task<T>> work) where TService : notnull =>
        Services(sp => work(sp.GetRequiredService<TService>()));

    private HttpClient Browser() => _app.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    private Task<User> AddUser(string name, UserRole role) => In<UserService, User>(s => s.CreateAsync(name, Password, role));

    private async Task<HttpClient> Api(User user, params string[] scopes)
    {
        var key = await In<ApiKeyService, CreatedApiKey>(s => s.CreateAsync(user.Id, $"key {Guid.NewGuid():N}", null, scopes.Length == 0 ? null : scopes));
        var api = Browser();
        api.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key.Token);
        return api;
    }

    [GeneratedRegex("name=\"__RequestVerificationToken\" value=\"([^\"]+)\"")]
    private static partial Regex Token();

    private static async Task<HttpResponseMessage> SignIn(HttpClient browser, string username)
    {
        var page = await browser.GetStringAsync("/login");
        return await browser.PostAsync("/login", new FormUrlEncodedContent(
        [
            new("_handler", "login"), new("__RequestVerificationToken", WebUtility.HtmlDecode(Token().Match(page).Groups[1].Value)),
            new("Input.Username", username), new("Input.Password", Password),
        ]));
    }

    private static string? Location(HttpResponseMessage response) =>
        response.Headers.Location is { } to ? (to.IsAbsoluteUri ? to.PathAndQuery : to.OriginalString) : null;

    [Fact]
    public async Task Repricing_unbilled_time_needs_the_accountant_role_over_the_api_too()
    {
        var clerk = await Api(await AddUser("clerk", UserRole.Standard));
        var books = await Api(await AddUser("books", UserRole.Accountant));

        Assert.Equal(HttpStatusCode.Forbidden, (await clerk.PostAsJsonAsync("/api/projects/time/reprice", new { from = "2026-01-01" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await books.PostAsJsonAsync("/api/projects/time/reprice", new { from = "2026-01-01" })).StatusCode);
    }

    [Fact]
    public async Task The_statement_emails_total_is_the_statements()
    {
        await In<ChartTemplates, ChartImportResult>(t => t.ApplyAsync("nonprofit"));
        var checking = (await In<AccountService, ChartOfAccounts>(s => s.GetChartAsync())).FindByFullName("Checking")!.Id;
        var pat = await In<CustomerService, Customer>(s => s.CreateAsync(new CustomerDetails("Pat Giver", Email: "pat@example.test")));
        var bounced = await In<GiftService, Donation>(s => s.RecordAsync(new RecordGiftCommand(pat.Id, new DateOnly(2026, 3, 3), 500m, checking)));
        await In<GiftService, Donation>(s => s.ReturnAsync(bounced.Id, new DateOnly(2026, 3, 8), 0m, null, "Not enough money"));
        await In<GiftService, Donation>(s => s.RecordAsync(new RecordGiftCommand(pat.Id, new DateOnly(2026, 5, 1), 200m, checking)));
        await In<GiftHistoryService, int>(s => s.ImportCsvAsync("Donor,Date,Amount,Description\r\nPat Giver,2026-02-14,300.00,Spring appeal\r\nPat Giver,2025-02-14,40.00,Before\r\n"));
        await In<EmailService, EmailWording>(s => s.SaveTemplateAsync(EmailKind.GivingStatement, "Your {year} statement", "You gave {total} in {year}."));

        var draft = await In<EmailService, EmailDraft>(s => s.DraftAsync(new EmailSource(EmailKind.GivingStatement, pat.Id, Year: 2026)));
        Assert.Equal("You gave 500.00 in 2026.", draft.Body);

        var household = await In<HouseholdService, Household>(s => s.CreateAsync(pat.Id, "The Givers", null));
        var together = await In<EmailService, EmailDraft>(s => s.DraftAsync(new EmailSource(EmailKind.GivingStatement, household.Id, Year: 2026)));
        Assert.Equal("You gave 500.00 in 2026.", together.Body);
    }

    [Fact]
    public async Task An_unreadable_single_sign_on_secret_shuts_nobody_out_of_the_app()
    {
        await AddUser("clerk", UserRole.Standard);
        await In<SingleSignOnService, SignInSettings>(s => s.SaveAsync(
            new SsoSettingsCommand(true, SsoProvider.Google, null, null, "client-1", "very secret", null, SsoOnly: true)));
        Assert.Equal(HttpStatusCode.OK, (await Browser().GetAsync("/healthz")).StatusCode);

        // As after a restore on a computer with other Data Protection keys.
        await In<OpenBookDbContext, int>(db => db.Set<SignInSettings>().ExecuteUpdateAsync(s => s.SetProperty(x => x.ProtectedSsoClientSecret, "not-from-these-keys")));
        _app.Services.GetRequiredService<ISingleSignOnHost>().SettingsChanged();

        var browser = Browser();
        Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync("/healthz")).StatusCode);
        var login = await browser.GetStringAsync("/login");
        Assert.Contains("Single sign-on isn", login, StringComparison.Ordinal);
        Assert.DoesNotContain("href=\"/login/sso", login, StringComparison.Ordinal);
        Assert.Equal("/login?sso=sso.secret_unreadable", Location(await browser.GetAsync("/login/sso")));

        // Still required for everyone else: an unreadable secret widens nobody's access.
        var refused = await SignIn(Browser(), "clerk");
        Assert.Equal(HttpStatusCode.OK, refused.StatusCode);
        Assert.Contains("Only administrators sign in with a password here.", await refused.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        Assert.Equal("/", Location(await SignIn(browser, "owner")));
        Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync("/settings/sign-in")).StatusCode);
    }

    [Fact]
    public void Telemetry_sends_only_the_server_of_the_addresses_OpenBook_calls()
    {
        using var redaction = new TelemetryRedaction();
        using var call = new Activity("call");
        call.SetTag("url.full", "https://hooks.slack.test/services/Tabc/Bdef/secretpart?x=1");
        call.SetTag("url.path", "/portal/obp_8f3kQz2LmN0pWx7Y?x=1");
        call.SetStatus(ActivityStatusCode.Error, "Couldn't send to pat@example.com");
        redaction.OnEnd(call);

        Assert.Equal("https://hooks.slack.test/", call.GetTagItem("url.full"));
        Assert.Equal("/portal/{id}", call.GetTagItem("url.path"));
        Assert.Equal("Couldn't send to [email]", call.StatusDescription);
        Assert.Equal("http://bridge.test:8080/", Telemetry.ServerOf("http://user:pass@bridge.test:8080/simplefin/claim/token"));
    }

    [Fact]
    public async Task A_session_that_must_set_up_two_factor_sign_in_gets_no_downloads()
    {
        await AddUser("clerk", UserRole.Standard);
        await In<TwoFactorService, SignInSettings>(s => s.RequireAsync(UserRole.Standard));
        var browser = Browser();
        Assert.Equal("/account/two-factor?required=true", Location(await SignIn(browser, "clerk")));

        Assert.Equal("/account/two-factor?required=true", Location(await browser.GetAsync("/inventory/items.csv")));
        Assert.Equal("/account/two-factor?required=true", Location(await browser.GetAsync($"/sales/channels/{Guid.NewGuid()}/skus.csv")));
        Assert.Equal("/account/two-factor?required=true", Location(await browser.GetAsync("/accountant/requests.csv")));
        Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync("/app.css")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync("/account/two-factor?required=true")).StatusCode);
    }

    [Fact]
    public async Task A_limited_key_reads_only_the_files_of_its_own_areas()
    {
        var clerk = await AddUser("clerk", UserRole.Standard);
        var vendor = await In<VendorService, Vendor>(s => s.CreateAsync(new VendorDetails("Landlord")));
        var file = await Services(sp => sp.GetRequiredService<AttachmentService>().AddAsync("vendor", vendor.Id, "w9.txt", new MemoryStream(Encoding.UTF8.GetBytes("W-9"))));
        var list = $"/api/attachments?entityType=vendor&entityId={vendor.Id}";
        var content = $"/api/attachments/{file.Id}/content";

        var data = await Api(clerk, "data:read");
        Assert.Equal(HttpStatusCode.Forbidden, (await data.GetAsync(list)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await data.GetAsync(content)).StatusCode);

        var both = await Api(clerk, "data:read", "purchases:write");
        Assert.Equal(HttpStatusCode.OK, (await both.GetAsync(list)).StatusCode);
        Assert.Equal("W-9", await both.GetStringAsync(content));
        Assert.Equal("W-9", await (await Api(clerk)).GetStringAsync(content));
    }

    [Fact]
    public async Task The_command_line_sends_a_file()
    {
        var owner = await In<OpenBookDbContext, User>(db => db.Users.SingleAsync(u => u.Username == "owner"));
        var key = (await In<ApiKeyService, CreatedApiKey>(s => s.CreateAsync(owner.Id, "cli"))).Token;
        var vendor = await In<VendorService, Vendor>(s => s.CreateAsync(new VendorDetails("Landlord")));
        var path = Path.Combine(_folder, "w9.txt");
        await File.WriteAllTextAsync(path, "W-9");

        var (output, error) = (new StringWriter(), new StringWriter());
        var cli = new CliApp(output, error, Path.Combine(_folder, "cli.json"), _ => _app.CreateClient());
        var code = await cli.RunAsync(["post", $"/attachments?entityType=vendor&entityId={vendor.Id}&fileName=w9.txt", "--file", path, "--key", key]);

        Assert.Equal("", error.ToString());
        Assert.Equal(0, code);
        Assert.Single(await In<AttachmentService, IReadOnlyList<Domain.Attachments.Attachment>>(s => s.ListAsync("vendor", vendor.Id)));
    }

    [Fact]
    public void A_grant_report_due_on_a_day_OpenBook_is_off_still_alerts()
    {
        var saturday = new DateOnly(2026, 11, 28);
        var grant = Grant.Create(Guid.NewGuid(), "Roof repair", Guid.NewGuid(), null, 1000m, saturday.AddMonths(-6), saturday.AddMonths(-6),
            saturday.AddMonths(6), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), false, null, null, DateTimeOffset.UnixEpoch);
        (Grant, GrantReportDue)[] due = [(grant, grant.AddReportDue(saturday, "Final"))];
        var alerts = Alerts();

        // Weekdays only: the Saturday a week before, the due day and the late weeks' Saturdays all pass with OpenBook off.
        for (var day = saturday.AddDays(-12); day <= saturday.AddDays(13); day = day.AddDays(1))
            if (day.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday))
                alerts.GrantReportsDue(due, day, new DateTimeOffset(day, new TimeOnly(9, 0), TimeSpan.Zero));

        // The week before, the week it fell due, and a week late.
        Assert.Equal(3, alerts.Pending);
    }

    private static AdminAlerts Alerts() => new(new ConfigurationBuilder().Build());

    [Fact]
    public async Task An_alert_already_sent_isnt_sent_again_after_a_restart()
    {
        await In<EmailService, EmailSettings>(s => s.SaveSettingsAsync(new EmailSettingsCommand(
            EmailDelivery.Smtp, "smtp.test", 587, SmtpSecurity.Auto, "books", "s3cret-pass", false, "books@studio.test", "Studio", null)));
        var owner = await In<OpenBookDbContext, User>(db => db.Users.SingleAsync(u => u.Username == "owner"));
        await In<UserService, User>(s => s.SetDetailsAsync(owner.Id, "owner@studio.test", null));
        int Sent(string subject) => _email.Sent.Count(m => m.Message.Subject == subject);

        var first = Alerts();
        first.Raise("books-check", "The check found problems", "One", DateTimeOffset.UtcNow);
        await first.SendPendingAsync(_app.Services, default);
        Assert.Equal(1, Sent("The check found problems"));

        // A process that starts afresh remembers nothing itself.
        var restarted = Alerts();
        restarted.Raise("books-check", "The check found problems", "One", DateTimeOffset.UtcNow);
        restarted.Raise("job:Bank feeds", "Bank feeds failed", "Two", DateTimeOffset.UtcNow);
        await restarted.SendPendingAsync(_app.Services, default);
        Assert.Equal(1, Sent("The check found problems"));
        Assert.Equal(1, Sent("Bank feeds failed"));
    }

    [Fact]
    public async Task A_bank_line_pays_an_invoice_only_for_someone_who_may_write_in_sales()
    {
        await In<ChartTemplates, ChartImportResult>(t => t.ApplyAsync("nonprofit"));
        var checking = (await In<AccountService, ChartOfAccounts>(s => s.GetChartAsync())).FindByFullName("Checking")!.Id;
        var income = await In<AccountService, Account>(s => s.CreateAsync(new CreateAccountCommand("Fees for work", AccountType.Income)));
        var item = await In<ItemService, Item>(s => s.CreateQuickAsync("Work", ItemType.Service, 100m, false, income.Id));
        var customer = await In<CustomerService, Customer>(s => s.CreateAsync(new CustomerDetails("Acme")));
        var invoice = await In<InvoiceService, Invoice>(s => s.CreateAsync(
            new CreateInvoiceCommand(customer.Id, new DateOnly(2026, 3, 1), [new SalesLineDraft(item.Id, null, 3m, 100m)])));
        await In<BankFeedService, FeedImportResult>(s => s.QueueAsync(checking, [new FeedLine(new DateOnly(2026, 3, 8), 300m, "ACME CORP", null, null, "P1")]));
        var line = (await In<BankFeedService, IReadOnlyList<FeedReviewItem>>(s => s.ReviewAsync(checking, Domain.Banking.BankFeedStatus.Pending))).Single().Item.Id;
        var clerk = await AddUser("clerk", UserRole.Standard);
        var documents = $"/api/banking/accounts/{checking}/feed/documents";
        var pay = $"/api/banking/feed/{line}/pay-document";

        var banking = await Api(clerk, "banking:write");
        Assert.Empty((await banking.GetFromJsonAsync<JsonElement>(documents)).EnumerateObject());
        Assert.Equal(HttpStatusCode.Forbidden, (await banking.PostAsJsonAsync(pay, new { kind = "Invoice", documentId = invoice.Id })).StatusCode);
        Assert.Equal(300m, (await In<InvoiceService, Invoice?>(s => s.GetAsync(invoice.Id)))!.Balance);

        var sales = await Api(clerk, "banking:write", "sales:write");
        Assert.Single((await sales.GetFromJsonAsync<JsonElement>(documents)).EnumerateObject());
        Assert.Equal(HttpStatusCode.OK, (await sales.PostAsJsonAsync(pay, new { kind = "Invoice", documentId = invoice.Id })).StatusCode);
        Assert.Equal(0m, (await In<InvoiceService, Invoice?>(s => s.GetAsync(invoice.Id)))!.Balance);
    }
}
