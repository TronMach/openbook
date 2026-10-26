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

var builder = WebApplication.CreateBuilder(args);
if (isResetPassword || encryptionCommand is not null)
    builder.Logging.ClearProviders();

// Before anything opens the file: the conversion needs it to be closed.
if (encryptionCommand is not null)
    return await EncryptionCommands.RunAsync(builder.Configuration, builder.Configuration.GetConnectionString("OpenBook") ?? "Data Source=openbook.db", encryptionCommand);

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

var connectionString = builder.Configuration.GetConnectionString("OpenBook") ?? "Data Source=openbook.db";
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
app.MapPortabilityEndpoints();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
return 0;

/// <summary>Visible to the API tests (WebApplicationFactory).</summary>
public partial class Program;
