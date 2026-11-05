using Microsoft.AspNetCore.Http.Features;
using OpenBook.Api;
using OpenBook.Application;
using OpenBook.Infrastructure;
using OpenBook.Web.Cli;
using OpenBook.Web.Components;
using OpenBook.Web.Endpoints;
using OpenBook.Web.Security;

// Container health checks: answer before building anything.
if (args is [HealthCheckCommand.Name, ..])
    return await HealthCheckCommand.RunAsync(args);

var isResetPassword = args is [ResetPasswordCommand.Name, ..];
var isMakeDemo = args is [DemoCompanyCommand.Name, ..];
var encryptionCommand = args is [EncryptionCommands.Encrypt or EncryptionCommands.Decrypt, ..] ? args[0] : null;
var isMoveToPostgres = args is [MoveToPostgresCommand.Name, ..];

// "--safe-mode" starts without add-ins (for one that stops OpenBook starting); it isn't a configuration key.
var safeModeArgument = args.Contains("--safe-mode", StringComparer.OrdinalIgnoreCase);
var builder = WebApplication.CreateBuilder(args.Where(a => !string.Equals(a, "--safe-mode", StringComparison.OrdinalIgnoreCase)
    && !string.Equals(a, ResetPasswordCommand.ClearTwoFactor, StringComparison.OrdinalIgnoreCase)).ToArray());
if (isResetPassword || isMakeDemo || encryptionCommand is not null || isMoveToPostgres)
    builder.Logging.ClearProviders();

// Desktop or Server (L13). Desktop decides the company file and folders itself and ignores those settings.
var installation = OpenBook.Web.Hosting.Installation.Read(builder.Configuration);
var connectionString = installation.CompanyFile is { } companyFile
    ? $"Data Source={companyFile}"
    : builder.Configuration.GetConnectionString("OpenBook") ?? "Data Source=openbook.db";
var backupDirectory = installation.BackupFolder ?? builder.Configuration["OpenBook:BackupDirectory"];
if (installation.DataFolder is not null)
    Directory.CreateDirectory(installation.DataFolder);

// Before anything opens the file: the conversion needs it to be closed.
if (encryptionCommand is not null)
    return await EncryptionCommands.RunAsync(builder.Configuration, connectionString, encryptionCommand);
if (isMoveToPostgres)
    return await MoveToPostgresCommand.RunAsync(builder.Configuration, installation, connectionString, args);

// A desktop copy already running for this company: open its page instead of starting another.
if (!isResetPassword && !isMakeDemo && installation.IsDesktop)
{
    if (await OpenBook.Web.Hosting.DesktopHost.HandOverAsync(installation))
        return 0;
    OpenBook.Web.Hosting.DesktopHost.Listen(builder, installation);
}

// Add services to the container.
builder.Services.AddLocalization(o => o.ResourcesPath = "Resources");
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

// The log goes to files beside the company file (or the backups folder), and the System page shows recent problems.
if (!isResetPassword && !isMakeDemo)
{
    var logFolder = OpenBook.Web.Diagnostics.LogFolder.For(
        installation.LogFolder ?? builder.Configuration["OpenBook:LogDirectory"], backupDirectory, connectionString, builder.Environment.ContentRootPath);
    // Made by the container, so it's disposed (and the file closed) when the app stops.
    builder.Services.AddSingleton(_ => new OpenBook.Web.Diagnostics.FileLoggerProvider(logFolder));
    builder.Services.AddSingleton<ILoggerProvider>(sp => sp.GetRequiredService<OpenBook.Web.Diagnostics.FileLoggerProvider>());
    builder.Logging.AddFilter<OpenBook.Web.Diagnostics.FileLoggerProvider>("Microsoft", LogLevel.Warning);
    builder.Logging.AddFilter<OpenBook.Web.Diagnostics.FileLoggerProvider>("System", LogLevel.Warning);
}

builder.Services.AddSingleton(installation);
builder.Services.AddSingleton<OpenBook.Web.Hosting.DesktopQuit>();
if (installation.IsDesktop)
    builder.Services.AddHostedService<OpenBook.Web.Hosting.DesktopControlService>();
builder.Services.AddSingleton<OpenBook.Web.Hosting.IInstallerLauncher, OpenBook.Web.Hosting.ProcessInstallerLauncher>();
builder.Services.AddSingleton(OpenBook.Web.Hosting.UpdateTrust.BuiltIn);
builder.Services.AddSingleton<OpenBook.Web.Hosting.UpdateService>();
builder.Services.AddHostedService<OpenBook.Web.Hosting.UpdateCheckService>();
builder.Services.AddHttpClient(OpenBook.Web.Hosting.UpdateService.HttpClientName, c =>
{
    // An installer is large; the feed is small and answers quickly.
    c.Timeout = TimeSpan.FromMinutes(15);
    c.DefaultRequestHeaders.UserAgent.ParseAdd($"OpenBook/{OpenBook.Web.Hosting.UpdateService.CurrentVersion}");
});
builder.Services.AddSingleton<OpenBook.Web.Diagnostics.JobStatus>();
builder.Services.AddSingleton<OpenBook.Web.Diagnostics.BooksCheckStatus>();
builder.Services.AddSingleton(OpenBook.Web.Help.HelpGuide.ForApp());
// Images are read only when Tesseract is installed and its path given; PDFs and text are read either way.
builder.Services.AddSingleton<OpenBook.Application.Purchases.IReceiptTextExtractor>(
    new OpenBook.Infrastructure.Payments.ReceiptTextExtractor(builder.Configuration["OpenBook:Ocr:TesseractPath"]));
// Webhooks and bank connections go to addresses people type in: private networks only when allowed.
builder.Services.AddSingleton(new OpenBook.Infrastructure.Net.OutboundPolicy(builder.Configuration.GetValue<bool>("OpenBook:Outbound:AllowPrivateNetworks")));
// Add-ins load before the services that use them (the report catalog, the importer).
builder.Services.AddSingleton(OpenBook.Web.Plugins.PluginLoader.Load(
    Path.GetFullPath(installation.PluginsFolder ?? builder.Configuration["OpenBook:Plugins:Path"] ?? "plugins"),
    safeModeArgument || builder.Configuration.GetValue<bool>("OpenBook:Plugins:SafeMode")));
builder.Services
    .AddOpenBookApplication()
    .AddOpenBookInfrastructure(
        connectionString,
        backupDirectory,
        builder.Configuration["OpenBook:EncryptionKey"],
        installation.IsDesktop ? DatabaseProvider.Sqlite : DatabaseProviders.Parse(builder.Configuration["OpenBook:Database"], connectionString))
    .AddOpenBookApi()
    .AddOpenBookWebSecurity(installation.KeysFolder ?? builder.Configuration["OpenBook:DataProtectionKeys"]);
builder.Services.AddOpenBookRateLimits(builder.Configuration);
builder.Services.Configure<FormOptions>(o =>
{
    o.MultipartBodyLengthLimit = AccountEndpoints.MaxBackupUploadBytes;
    // Grids post a field per cell: budgets, counts, reorder lists. The default of 1,024 stops them at a few hundred rows.
    o.ValueCountLimit = 100_000;
});
builder.Services.AddScoped<OpenBook.Web.Components.LineFieldCache>();
builder.Services.AddScoped<OpenBook.Web.Components.RenderTurn>();

var app = builder.Build();

if (isResetPassword)
    return await ResetPasswordCommand.RunAsync(app.Services, args);
if (isMakeDemo)
    return await DemoCompanyCommand.RunAsync(app.Services, args);

await app.Services.MigrateOpenBookDatabaseAsync();
await using (var scope = app.Services.CreateAsyncScope())
    OpenBook.Application.CompanyTime.Use(await scope.ServiceProvider.GetRequiredService<OpenBook.Application.Companies.ICompanyStore>().GetCompanyAsync(default));

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}
app.UseMiddleware<SecurityHeadersMiddleware>();
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseMiddleware<OpenBook.Web.Diagnostics.NotFoundMiddleware>();
app.UseHttpsRedirection();
// Before anything reads the request: sign-in and anonymous links are limited per address (RateLimits).
app.UseRateLimiter();
app.UseRequestLocalization(OpenBook.Web.Localization.AppCulture.Options());

app.UseMiddleware<SetupGateMiddleware>();
app.UseAuthentication();
app.UseAuthorization();
app.UseMiddleware<AccessMiddleware>();
// API writes with an Idempotency-Key answer a repeat with the first response (after access is decided).
app.UseMiddleware<IdempotencyMiddleware>();
app.UseAntiforgery();

app.MapStaticAssets();

// For container and load balancer probes: the app is up and its database answers.
app.MapGet("/healthz", async (OpenBook.Infrastructure.Persistence.OpenBookDbContext db, CancellationToken ct) =>
    await db.Database.CanConnectAsync(ct) ? Results.Ok("ok") : Results.StatusCode(StatusCodes.Status503ServiceUnavailable)).AllowAnonymous();
app.MapOpenBookApi();
app.MapAccountEndpoints();
app.MapSingleSignOn();
app.MapAttachmentEndpoints();
app.MapCheckEndpoints();
app.MapBankingEndpoints();
app.MapInventoryEndpoints();
app.MapReportEndpoints();
app.MapPricingEndpoints();
app.MapSegmentEndpoints();
app.MapCollectionEndpoints();
app.MapSalesFlowEndpoints();
app.MapProjectEndpoints();
app.MapAssetEndpoints();
app.MapNonprofitEndpoints();
app.MapChannelEndpoints();
app.MapDashboardEndpoints();
app.MapPluginEndpoints();
app.MapPost("/capture/drafts/{id:guid}/{action:regex(^(retry|dismiss)$)}", async (Guid id, string action, HttpContext http,
    OpenBook.Application.Capture.CaptureService capture, CancellationToken ct) =>
{
    try
    {
        if (action == "retry")
            await capture.RetryAsync(id, http.User.GetUserId(), ct);
        else
            await capture.DismissAsync(id, http.User.GetUserId(), ct);
        return Results.Redirect("/capture");
    }
    catch (OpenBook.Domain.Common.DomainException ex)
    {
        return Results.Redirect("/capture?error=" + Uri.EscapeDataString(ex.Message));
    }
}).RequireAuthorization();
app.MapPost("/capture/devices/{id:guid}/revoke", async (Guid id, IFormCollection form, HttpContext http, OpenBook.Application.Capture.CaptureService capture,
    OpenBook.Application.Companies.AuthService auth, CancellationToken ct) =>
{
    await capture.RevokeDeviceAsync(id, http.User.GetUserId(), http.User.GetSessionId(), ct);
    if (form["endSessions"] == "true")
        await WebSecurity.SignInAsync(http, await auth.EndOtherSessionsAsync(http.User.GetUserId(), ct));
    return Results.Redirect("/capture");
}).RequireAuthorization();

// Without signing in, on purpose: a signed-out lost device asks this to know it must wipe itself.
app.MapGet("/capture/device-status/{id:guid}", async (Guid id, OpenBook.Application.Capture.CaptureService capture, CancellationToken ct) =>
    Results.Json(new { revoked = await capture.IsRevokedAsync(id, ct) }));

app.MapPost("/account/sessions/end-others", async (IFormCollection form, HttpContext http, OpenBook.Application.Companies.AuthService auth, CancellationToken ct) =>
{
    // The stamp changes, so this browser's cookie is issued again to keep it signed in.
    await WebSecurity.SignInAsync(http, await auth.EndOtherSessionsAsync(http.User.GetUserId(), ct));
    var back = form["back"].ToString();
    return Results.Redirect((back.StartsWith('/') && !back.StartsWith("//", StringComparison.Ordinal) ? back : "/account/password") + "?signedOut=true");
}).RequireAuthorization();
app.MapPost("/account/region", (IFormCollection form, HttpContext http) =>
{
    if (OpenBook.Web.Localization.AppCulture.CookieValue(form["region"], form["language"]) is { } value)
        http.Response.Cookies.Append(Microsoft.AspNetCore.Localization.CookieRequestCultureProvider.DefaultCookieName, value,
            new CookieOptions { Expires = DateTimeOffset.UtcNow.AddYears(1), HttpOnly = true, SameSite = SameSiteMode.Lax, IsEssential = true, Secure = http.Request.IsHttps });
    var back = form["back"].ToString();
    return Results.Redirect(back.StartsWith('/') && !back.StartsWith("//", StringComparison.Ordinal) ? back : "/account/region");
}).RequireAuthorization();
// OpenBook Desktop runs in the background; this backs it up and stops it (Settings > System; the tray icon does the
// same through its pipe). A server is stopped by whoever runs it.
app.MapPost("/settings/system/quit", async (OpenBook.Web.Hosting.Installation install, OpenBook.Web.Hosting.DesktopQuit quit, CancellationToken ct) =>
{
    if (!install.IsDesktop)
        return Results.NotFound();
    var backup = await quit.BackUpAsync(ct);
    quit.Stop();
    var said = System.Net.WebUtility.HtmlEncode(OpenBook.Web.Hosting.DesktopQuit.Describe(backup));
    return Results.Content($"""
        <!DOCTYPE html><html lang="en"><head><meta charset="utf-8"><title>OpenBook has stopped</title></head>
        <body style="font-family: system-ui, sans-serif; margin: 3rem auto; max-width: 32rem">
        <h1>OpenBook has stopped</h1><p>{said}</p><p>You can close this tab. Start OpenBook again from the Start menu.</p></body></html>
        """, "text/html");
}).RequireAuthorization();
// Updates (Settings > System, admins): check now, switch automatic checks, and on the desktop install what's downloaded.
app.MapPost("/settings/system/updates/check", async (IFormCollection form, OpenBook.Web.Hosting.UpdateService updates, CancellationToken ct) =>
{
    var status = await updates.CheckAsync(ct);
    return Results.Redirect(status.Error is null ? "/settings/system?updates=checked#updates" : "/settings/system?updates=failed#updates");
}).RequireAuthorization();
app.MapPost("/settings/system/updates/auto", (IFormCollection form, OpenBook.Web.Hosting.Installation install, OpenBook.Web.Hosting.UpdateService updates) =>
{
    if (!install.IsDesktop)
        return Results.NotFound();
    updates.SetEnabled(form["check"] == "true");
    return Results.Redirect("/settings/system#updates");
}).RequireAuthorization();
app.MapPost("/settings/system/updates/install", async (IFormCollection form, OpenBook.Web.Hosting.Installation install, OpenBook.Web.Hosting.UpdateService updates, CancellationToken ct) =>
{
    if (!install.IsDesktop)
        return Results.NotFound();
    var version = updates.Status.Available?.Version;
    if (await updates.InstallAsync(form["anyway"] == "true", ct) is { } refusal)
        return Results.Redirect($"/settings/system?installError={Uri.EscapeDataString(refusal)}#updates");
    return Results.Content($"""
        <!DOCTYPE html><html lang="en"><head><meta charset="utf-8"><title>Updating OpenBook</title></head>
        <body style="font-family: system-ui, sans-serif; margin: 3rem auto; max-width: 32rem">
        <h1>Updating OpenBook</h1><p>Your books were backed up, and OpenBook {System.Net.WebUtility.HtmlEncode(version ?? "")} is being installed. It starts
        again by itself in a minute or two and opens a new tab; you can close this one.</p></body></html>
        """, "text/html");
}).RequireAuthorization();
app.MapPurchasingEndpoints();
app.MapMergeEndpoints();
app.MapOnlineEndpoints();
app.MapDocumentEndpoints();
app.MapCustomFieldEndpoints();
app.MapFormDesignEndpoints();
app.MapPrintEndpoints();
app.MapEmailEndpoints();
app.MapPortabilityEndpoints();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

if (installation.IsDesktop)
    OpenBook.Web.Hosting.DesktopHost.OnStarted(app, installation);
app.Run();
return 0;

/// <summary>Visible to the API tests (WebApplicationFactory).</summary>
public partial class Program;
