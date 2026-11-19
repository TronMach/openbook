using OpenBook.Domain.Common;

namespace OpenBook.Domain.Banking;

/// <summary>Which of Plaid's servers the keys are for: test banks only, or real ones.</summary>
public enum PlaidEnvironment
{
    /// <summary>sandbox.plaid.com: Plaid's test banks, free.</summary>
    Sandbox,

    /// <summary>production.plaid.com: real banks (Plaid's Trial plan, or a paid plan).</summary>
    Production,
}

/// <summary>
/// The company's own Plaid keys (one row, created on first save): the client ID and a secret for one environment. The
/// secret is stored through the host's secret protector, never shown again, and left out of the data export and audit.
/// Each bank connected through Plaid is a <see cref="BankConnection"/> holding its own access token.
/// </summary>
public sealed class PlaidSettings
{
    public const int MaxClientIdLength = 100;

    private PlaidSettings()
    {
    }

    public Guid Id { get; private set; }
    public string? ClientId { get; private set; }
    public string? ProtectedSecret { get; private set; }
    public PlaidEnvironment Environment { get; private set; }

    public bool IsReady => ClientId is not null && ProtectedSecret is not null;

    public static PlaidSettings Create() => new() { Id = Guid.CreateVersion7(), Environment = PlaidEnvironment.Sandbox };

    /// <param name="protectedSecret">Null keeps the saved secret; empty clears it.</param>
    public void Update(string? clientId, string? protectedSecret, PlaidEnvironment environment)
    {
        if (!Enum.IsDefined(environment))
            throw new DomainException("plaid.environment_invalid", "Choose Sandbox or Production.");
        var id = Text.Optional(clientId, MaxClientIdLength, "plaid.client_id_too_long", "Client ID");
        if (id is not null && !id.All(char.IsAsciiLetterOrDigit))
            throw new DomainException("plaid.client_id_invalid", "The client ID is letters and digits, as Plaid shows it under Developers, Keys.");
        ClientId = id;
        if (protectedSecret is not null)
            ProtectedSecret = protectedSecret.Length == 0 ? null : protectedSecret;
        Environment = environment;
    }
}
