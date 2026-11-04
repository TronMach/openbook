using System.Security.Claims;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OpenBook.Application.Companies;
using OpenBook.Application.Sales;
using OpenBook.Domain.Common;
using OpenBook.Domain.Companies;
using OpenBook.Domain.Sales;
using OpenBook.Infrastructure.Persistence;
using OpenBook.Web.Security;

namespace OpenBook.Integration.Tests;

/// <summary>
/// An interactive page's connection outlives the request whose cookie was checked, so what it does is checked each
/// time. These run <see cref="Operations"/> as a circuit does, with the claims a sign-in cookie carries.
/// </summary>
public sealed class OpenPageAccessTests : IAsyncLifetime
{
    private const string Password = "a long password";
    private readonly string _folder = Path.Combine(Path.GetTempPath(), $"openbook-open-pages-{Guid.NewGuid():N}");
    private WebTestDatabase _database = null!;
    private WebApplicationFactory<Program> _app = null!;
    private HttpClient _browser = null!;
    private Guid _userId;
    private ClaimsPrincipal _signedIn = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_folder);
        _database = new WebTestDatabase(_folder, "open-pages.db");
        await _database.CreateAsync();
        _app = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            _database.Apply(b);
            b.UseSetting("OpenBook:DataProtectionKeys", Path.Combine(_folder, "keys"));
        });
        _browser = await PageSmokeTests.SetUpAsync(_app);

        await using var scope = _app.Services.CreateAsyncScope();
        var sp = scope.ServiceProvider;
        _userId = (await sp.GetRequiredService<UserService>().CreateAsync("pat", Password, UserRole.Accountant)).Id;
        var auth = sp.GetRequiredService<AuthService>();
        var user = (await auth.SignInAsync("pat", Password)).User!;
        var session = await auth.StartSessionAsync(user, null, null);
        _signedIn = new ClaimsPrincipal(new ClaimsIdentity(
            [.. WebSecurity.Claims(user), new Claim(AuthService.SessionClaim, session.ToString())], "Cookies"));
    }

    public async Task DisposeAsync()
    {
        await _app.DisposeAsync();
        SqliteConnection.ClearAllPools();
        await _database.DropAsync();
        Directory.Delete(_folder, recursive: true);
    }

    private async Task<int> RunOnOpenPageAsync(ClaimsPrincipal? principal, bool inCircuit = true)
    {
        await using var scope = _app.Services.CreateAsyncScope();
        var circuit = scope.ServiceProvider.GetRequiredService<CircuitUser>();
        circuit.Principal = principal;
        circuit.InCircuit = inCircuit;
        var users = await scope.ServiceProvider.GetRequiredService<Operations>().RunAsync<UserService, IReadOnlyList<User>>(s => s.ListAsync());
        return users.Count;
    }

    private async Task AssertSignedOutAsync(ClaimsPrincipal? principal)
    {
        var refused = await Assert.ThrowsAsync<DomainException>(() => RunOnOpenPageAsync(principal));
        Assert.Equal(Operations.SignedOutCode, refused.Code);
    }

    private async Task WithServicesAsync(Func<IServiceProvider, Task> work)
    {
        await using var scope = _app.Services.CreateAsyncScope();
        await work(scope.ServiceProvider);
    }

    [Fact]
    public async Task An_open_page_works_while_the_session_counts() =>
        Assert.True(await RunOnOpenPageAsync(_signedIn) >= 2);

    [Fact]
    public async Task An_open_page_stops_when_the_person_is_made_inactive()
    {
        await WithServicesAsync(sp => sp.GetRequiredService<UserService>().SetActiveAsync(_userId, false));
        await AssertSignedOutAsync(_signedIn);
    }

    [Fact]
    public async Task An_open_page_stops_when_the_role_changes()
    {
        await WithServicesAsync(sp => sp.GetRequiredService<UserService>().ChangeRoleAsync(_userId, UserRole.ViewOnly));
        await AssertSignedOutAsync(_signedIn);
    }

    [Fact]
    public async Task An_open_page_stops_when_the_person_signs_out_there()
    {
        var session = AuthService.SessionIdOf(_signedIn)!.Value;
        await WithServicesAsync(sp => sp.GetRequiredService<AuthService>().EndSessionAsync(_userId, session));
        await AssertSignedOutAsync(_signedIn);
    }

    [Fact]
    public async Task An_open_page_stops_when_other_sessions_are_ended()
    {
        await WithServicesAsync(sp => sp.GetRequiredService<AuthService>().EndOtherSessionsAsync(_userId));
        await AssertSignedOutAsync(_signedIn);
    }

    [Fact]
    public async Task An_open_page_stops_on_the_day_access_ends()
    {
        // Straight in the database: the page only takes dates after today, and this one has come since.
        var ended = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-2));
        await WithServicesAsync(sp => sp.GetRequiredService<OpenBookDbContext>().Users.Where(u => u.Id == _userId)
            .ExecuteUpdateAsync(u => u.SetProperty(x => x.AccessEndsOn, ended)));
        await AssertSignedOutAsync(_signedIn);
    }

    [Fact]
    public async Task A_connection_that_comes_back_without_a_valid_cookie_is_refused()
    {
        await AssertSignedOutAsync(new ClaimsPrincipal(new ClaimsIdentity()));
        await AssertSignedOutAsync(null);
    }

    [Fact]
    public async Task A_page_drawn_for_a_request_is_left_to_the_cookie_check() =>
        Assert.True(await RunOnOpenPageAsync(null, inCircuit: false) >= 2);

    [Fact]
    public async Task A_file_name_is_never_part_of_a_script()
    {
        const string name = "x'+alert(1)+'.pdf";
        Guid customer = default;
        await WithServicesAsync(async sp =>
        {
            customer = (await sp.GetRequiredService<CustomerService>().CreateAsync(new CustomerDetails("Acme"))).Id;
            using var file = new MemoryStream("%PDF-1.4"u8.ToArray());
            await sp.GetRequiredService<Application.Attachments.AttachmentService>().AddAsync("customer", customer, name, file);
        });

        var page = await _browser.GetStringAsync($"/customers/{customer}");

        Assert.Contains("data-confirm=\"Remove x", page, StringComparison.Ordinal);
        Assert.DoesNotContain("onclick=\"return confirm('Remove x", page, StringComparison.Ordinal);
    }
}
