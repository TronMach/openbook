using OpenBook.Api;
using OpenBook.Application;
using OpenBook.Domain.Common;
using OpenBook.Infrastructure;
using OpenBook.Web.Components;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

// Home currency moves into company settings with the setup wizard.
builder.Services
    .AddOpenBookApplication(new LedgerOptions(new Currency(builder.Configuration["OpenBook:HomeCurrency"] ?? "USD")))
    .AddOpenBookInfrastructure(builder.Configuration.GetConnectionString("OpenBook") ?? "Data Source=openbook.db")
    .AddOpenBookApi();

var app = builder.Build();

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

app.UseAntiforgery();

app.MapStaticAssets();
app.MapOpenBookApi();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
