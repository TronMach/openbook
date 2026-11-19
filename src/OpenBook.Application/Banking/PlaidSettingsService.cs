using System.Text.Json;
using OpenBook.Application.Forms;
using OpenBook.Domain.Auditing;
using OpenBook.Domain.Banking;
using OpenBook.Domain.Common;

namespace OpenBook.Application.Banking;

/// <param name="Secret">Null or empty keeps the saved secret (unless <paramref name="ClearSecret"/>).</param>
public sealed record PlaidSettingsCommand(string? ClientId, string? Secret, PlaidEnvironment Environment, bool ClearSecret = false);

/// <summary>The company's Plaid keys: saved here by an admin, read by the Plaid client for each call.</summary>
public sealed class PlaidSettingsService(IBankingStore store, ISecretProtector protector, IClock clock, ICurrentUser user) : IPlaidKeys
{
    public const string EntityType = "plaid_settings";

    public async Task<PlaidSettings> GetSettingsAsync(CancellationToken ct = default) =>
        await store.GetPlaidSettingsAsync(ct) ?? PlaidSettings.Create();

    public async Task<PlaidSettings> SaveSettingsAsync(PlaidSettingsCommand command, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        var settings = await store.GetPlaidSettingsAsync(ct);
        string? before = null;
        if (settings is null)
        {
            settings = PlaidSettings.Create();
            store.Add(settings);
        }
        else
        {
            before = Snapshot(settings);
        }

        var secret = command.ClearSecret ? "" : string.IsNullOrWhiteSpace(command.Secret) ? null : protector.Protect(command.Secret.Trim());
        settings.Update(command.ClientId, secret, command.Environment);
        store.Add(AuditRecord.Create(clock.UtcNow, user.Actor, before is null ? AuditAction.Created : AuditAction.Updated, EntityType, settings.Id, before, Snapshot(settings)));
        await store.SaveChangesAsync(ct);
        return settings;
    }

    public async Task<PlaidKeys?> GetAsync(CancellationToken ct)
    {
        if (await store.GetPlaidSettingsAsync(ct) is not { IsReady: true } settings)
            return null;
        var secret = protector.Unprotect(settings.ProtectedSecret!)
            ?? throw new DomainException("plaid.secret_unreadable", "The saved Plaid secret can't be read on this computer. Enter it again under Settings, Plaid.");
        return new PlaidKeys(settings.ClientId!, secret, settings.Environment);
    }

    /// <summary>No secret in here: audit records feed webhooks.</summary>
    internal static string Snapshot(PlaidSettings s) => JsonSerializer.Serialize(new
    {
        s.ClientId,
        HasSecret = s.ProtectedSecret is not null,
        Environment = s.Environment.ToString(),
    });
}
