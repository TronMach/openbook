using Microsoft.AspNetCore.Http.Features;
using OpenBook.Api;
using OpenBook.Application;
using OpenBook.Infrastructure;
using OpenBook.Web.Cli;
using OpenBook.Web.Components;
using OpenBook.Web.Endpoints;
using OpenBook.Web.Security;

var isResetPassword = args is [ResetPasswordCommand.Name, ..];

var builder = WebApplication.CreateBuilder(args);
if (isResetPassword)
    builder.Logging.ClearProviders();

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

builder.Services
    .AddOpenBookApplication()
    .AddOpenBookInfrastructure(
        builder.Configuration.GetConnectionString("OpenBook") ?? "Data Source=openbook.db",
        builder.Configuration["OpenBook:BackupDirectory"])
    .AddOpenBookApi()
    .AddOpenBookWebSecurity();
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
app.UseAntiforgery();

app.MapStaticAssets();
app.MapOpenBookApi();
app.MapAccountEndpoints();
app.MapAttachmentEndpoints();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
return 0;
