using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using OpenBook.Web.Hosting;
using OpenBook.Web.Security;

namespace OpenBook.Integration.Tests;

/// <summary>Desktop answers only requests addressed to this computer, and needs no setup code; a server does. No database.</summary>
public sealed class DesktopHostTests
{
    [Theory]
    [InlineData("localhost:5180", HttpStatusCode.OK)]
    [InlineData("127.0.0.1:5180", HttpStatusCode.OK)]
    [InlineData("[::1]:5180", HttpStatusCode.OK)]
    [InlineData("LOCALHOST", HttpStatusCode.OK)]
    [InlineData("rebound.example:5180", HttpStatusCode.BadRequest)]
    [InlineData("127.0.0.1.nip.io:5180", HttpStatusCode.BadRequest)]
    public async Task Desktop_answers_only_requests_for_this_computer(string host, HttpStatusCode expected)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        // The setting a server might have; Desktop ignores it.
        builder.Configuration["AllowedHosts"] = "*";
        DesktopHost.RestrictHosts(builder.Services);
        await using var app = builder.Build();
        app.MapGet("/", () => "OpenBook");
        await app.StartAsync();

        using var request = new HttpRequestMessage(HttpMethod.Get, "/");
        request.Headers.Host = host;
        using var response = await app.GetTestClient().SendAsync(request);
        Assert.Equal(expected, response.StatusCode);
    }

    private static Installation Install(string? kind) =>
        Installation.Read(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["OpenBook:Install"] = kind,
            ["OpenBook:Desktop:DataFolder"] = Path.Combine(Path.GetTempPath(), "openbook-desktop-test"),
            ["OpenBook:Desktop:AppFolder"] = Path.Combine(Path.GetTempPath(), "openbook-desktop-test-app"),
        }).Build());

    private static IConfiguration Settings(string? code = null) =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["OpenBook:SetupCode"] = code }).Build();

    [Fact]
    public void A_server_makes_its_own_setup_code_and_desktop_needs_none()
    {
        var server = new SetupCode(Install(null), Settings());
        Assert.True(server.Required);
        Assert.False(server.Chosen);
        Assert.Matches("^[A-HJ-NP-Z2-9]{4}-[A-HJ-NP-Z2-9]{4}$", server.Display);
        Assert.True(server.Matches(server.Display.ToLowerInvariant().Replace("-", " ", StringComparison.Ordinal)));
        Assert.False(server.Matches(null));
        Assert.False(server.Matches(""));
        Assert.NotEqual(server.Display, new SetupCode(Install(null), Settings()).Display);

        var desktop = new SetupCode(Install("Desktop"), Settings());
        Assert.False(desktop.Required);
        Assert.True(desktop.Matches(null));
    }

    [Fact]
    public void A_chosen_setup_code_is_used_as_given()
    {
        var chosen = new SetupCode(Install(null), Settings("my-own-code-2026"));
        Assert.True(chosen.Chosen);
        Assert.True(chosen.Matches("MYOWNCODE2026"));
        Assert.False(chosen.Matches("MYOWNCODE2025"));
    }
}

/// <summary>The sample password from docker-compose.yml stops a server starting. No database.</summary>
public sealed class SamplePasswordTests
{
    [Theory]
    [InlineData("Host=db;Database=openbook;Username=openbook;Password=change-me", true)]
    [InlineData("Host=db; Database=openbook; Username=openbook; password = CHANGE-ME ;", true)]
    [InlineData("Host=db;Username=openbook;Pwd='change-me'", true)]
    [InlineData("Host=db;Database=openbook;Username=openbook;Password=change-me-please", false)]
    [InlineData("Host=db;Database=change-me;Username=openbook;Password=s3cret", false)]
    [InlineData("Data Source=openbook.db", false)]
    [InlineData(null, false)]
    public void The_sample_password_is_refused(string? connectionString, bool refused) =>
        Assert.Equal(refused, OpenBook.Web.Hosting.SamplePasswords.Refusal(connectionString) is not null);
}
