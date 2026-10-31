using Microsoft.AspNetCore.Http.Features;
using OpenBook.Api;
using OpenBook.Application;
using OpenBook.Infrastructure;
using OpenBook.Web.Cli;
using OpenBook.Web.Components;
using OpenBook.Web.Endpoints;
using OpenBook.Web.Security;

var isResetPassword = args is [ResetPasswordCommand.Name, ..];
var encryptionCommand = args is [EncryptionCommands.Encrypt or EncryptionCommands.Decrypt, ..] ? args[0] : null;

// "--safe-mode" starts without add-ins (for one that stops OpenBook starting); it isn't a configuration key.
var safeModeArgument = args.Contains("--safe-mode", StringComparer.OrdinalIgnoreCase);
var builder = WebApplication.CreateBuilder(args.Where(a => !string.Equals(a, "--safe-mode", StringComparison.OrdinalIgnoreCase)).ToArray());
if (isResetPassword || encryptionCommand is not null)
    builder.Logging.ClearProviders();

// Before anything opens the file: the conversion needs it to be closed.
if (encryptionCommand is not null)
    return await EncryptionCommands.RunAsync(builder.Configuration, builder.Configuration.GetConnectionString("OpenBook") ?? "Data Source=openbook.db", encryptionCommand);

// Add services to the container.
builder.Services.AddLocalization(o => o.ResourcesPath = "Resources");
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

var connectionString = builder.Configuration.GetConnectionString("OpenBook") ?? "Data Source=openbook.db";
// Images are read only when Tesseract is installed and its path given; PDFs and text are read either way.
builder.Services.AddSingleton<OpenBook.Application.Purchases.IReceiptTextExtractor>(
    new OpenBook.Infrastructure.Payments.ReceiptTextExtractor(builder.Configuration["OpenBook:Ocr:TesseractPath"]));
// Add-ins load before the services that use them (the report catalog, the importer).
builder.Services.AddSingleton(OpenBook.Web.Plugins.PluginLoader.Load(
    Path.GetFullPath(builder.Configuration["OpenBook:Plugins:Path"] ?? "plugins"),
    safeModeArgument || builder.Configuration.GetValue<bool>("OpenBook:Plugins:SafeMode")));
builder.Services
    .AddOpenBookApplication()
    .AddOpenBookInfrastructure(
        connectionString,
        builder.Configuration["OpenBook:BackupDirectory"],
        builder.Configuration["OpenBook:EncryptionKey"],
        DatabaseProviders.Parse(builder.Configuration["OpenBook:Database"], connectionString))
    .AddOpenBookApi()
    .AddOpenBookWebSecurity(builder.Configuration["OpenBook:DataProtectionKeys"]);
builder.Services.Configure<FormOptions>(o => o.MultipartBodyLengthLimit = AccountEndpoints.MaxBackupUploadBytes);
builder.Services.AddScoped<OpenBook.Web.Components.LineFieldCache>();

var app = builder.Build();

if (isResetPassword)
    return await ResetPasswordCommand.RunAsync(app.Services, args);

await app.Services.MigrateOpenBookDatabaseAsync();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseHttpsRedirection();
app.UseRequestLocalization(OpenBook.Web.Localization.AppCulture.Options());

app.UseMiddleware<SetupGateMiddleware>();
app.UseAuthentication();
app.UseAuthorization();
app.UseMiddleware<AccessMiddleware>();
app.UseAntiforgery();

app.MapStaticAssets();

// For container and load balancer probes: the app is up and its database answers.
app.MapGet("/healthz", async (OpenBook.Infrastructure.Persistence.OpenBookDbContext db, CancellationToken ct) =>
    await db.Database.CanConnectAsync(ct) ? Results.Ok("ok") : Results.StatusCode(StatusCodes.Status503ServiceUnavailable)).AllowAnonymous();
app.MapOpenBookApi();
app.MapAccountEndpoints();
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
app.MapPurchasingEndpoints();
app.MapOnlineEndpoints();
app.MapDocumentEndpoints();
app.MapCustomFieldEndpoints();
app.MapFormDesignEndpoints();
app.MapPrintEndpoints();
app.MapPortabilityEndpoints();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
return 0;

/// <summary>Visible to the API tests (WebApplicationFactory).</summary>
public partial class Program;
