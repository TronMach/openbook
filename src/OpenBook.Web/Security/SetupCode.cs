using System.Security.Cryptography;
using System.Text;
using OpenBook.Application.Companies;
using OpenBook.Web.Hosting;

namespace OpenBook.Web.Security;

/// <summary>
/// The code that proves whoever sets up a new OpenBook Server runs it: until a company exists, <c>/setup</c> is open to
/// anyone who can reach the server, so it asks for this code, which OpenBook writes to its log when it starts without a
/// company. <c>OpenBook:SetupCode</c> chooses the code instead (for automated installs). OpenBook Desktop needs none: it
/// only answers this computer, at localhost (<see cref="DesktopHost.RestrictHosts"/>), and opens setup itself.
/// </summary>
public sealed partial class SetupCode
{
    private const string Alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
    private readonly string _value;

    public SetupCode(Installation installation, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(installation);
        ArgumentNullException.ThrowIfNull(configuration);
        Required = !installation.IsDesktop;
        var configured = Normalize(configuration["OpenBook:SetupCode"]);
        Chosen = configured.Length > 0;
        _value = Chosen ? configured : RandomNumberGenerator.GetString(Alphabet, 8);
    }

    public bool Required { get; }

    /// <summary>The code came from <c>OpenBook:SetupCode</c>, so it isn't written to the log.</summary>
    public bool Chosen { get; }

    /// <summary>As it's shown: two groups of four.</summary>
    public string Display => _value.Length == 8 ? $"{_value[..4]}-{_value[4..]}" : _value;

    /// <summary>Whether the typed code is right, ignoring case, spaces and dashes; always when none is needed.</summary>
    public bool Matches(string? typed) =>
        !Required || CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(Normalize(typed)), Encoding.UTF8.GetBytes(_value));

    private static string Normalize(string? text) =>
        new([.. (text ?? string.Empty).Where(char.IsAsciiLetterOrDigit).Select(char.ToUpperInvariant)]);

    /// <summary>Once started: when there's no company yet, says in the log how to set one up.</summary>
    public static void AnnounceWhenStarted(WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        app.Lifetime.ApplicationStarted.Register(() => _ = Task.Run(async () =>
        {
            var code = app.Services.GetRequiredService<SetupCode>();
            if (!code.Required)
                return;
            var logger = app.Services.GetRequiredService<ILogger<SetupCode>>();
            try
            {
                using var scope = app.Services.CreateScope();
                var state = scope.ServiceProvider.GetRequiredService<SetupState>();
                if (await state.CheckAsync(scope.ServiceProvider.GetRequiredService<CompanySetupService>(), app.Lifetime.ApplicationStopping))
                    return;
                if (code.Chosen)
                    LogNoCompanyChosen(logger);
                else
                    LogNoCompany(logger, code.Display);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                LogFailed(logger, ex);
            }
        }));
    }

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "OpenBook has no company yet. To set one up, open /setup in a browser and enter the setup code {Code}. It's needed only once.")]
    private static partial void LogNoCompany(ILogger logger, string code);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "OpenBook has no company yet. To set one up, open /setup in a browser and enter the setup code set in OpenBook:SetupCode.")]
    private static partial void LogNoCompanyChosen(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Couldn't tell whether a company is set up, so no setup code was shown.")]
    private static partial void LogFailed(ILogger logger, Exception error);
}
