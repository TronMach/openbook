using System.Collections.Concurrent;
using System.Net.Http.Headers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OpenBook.Application;
using OpenBook.Application.Forms;
using OpenBook.Domain.Common;
using OpenBook.Domain.Companies;
using OpenBook.Infrastructure.Persistence;

namespace OpenBook.Infrastructure.Forms;

/// <summary>Microsoft's token service over HTTPS. The address is Microsoft's own, never typed in.</summary>
public sealed class MicrosoftMailClient : IMicrosoftMailClient, IDisposable
{
    private readonly HttpClient _client;

    public MicrosoftMailClient()
        : this(new SocketsHttpHandler { AllowAutoRedirect = false, PooledConnectionLifetime = TimeSpan.FromMinutes(5) })
    {
    }

    /// <summary>For tests: a handler standing in for Microsoft.</summary>
    public MicrosoftMailClient(HttpMessageHandler handler)
    {
        _client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
        _client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("OpenBook", "1.0"));
    }

    public Task<MicrosoftToken> RedeemAsync(MicrosoftApp app, string code, string verifier, string redirectUri, string scopes, CancellationToken ct) => PostAsync(app, new()
    {
        ["grant_type"] = "authorization_code",
        ["code"] = code,
        ["code_verifier"] = verifier,
        ["redirect_uri"] = redirectUri,
        ["scope"] = scopes,
    }, ct);

    public Task<MicrosoftToken> RefreshAsync(MicrosoftApp app, string refreshToken, string scopes, CancellationToken ct) => PostAsync(app, new()
    {
        ["grant_type"] = "refresh_token",
        ["refresh_token"] = refreshToken,
        ["scope"] = scopes,
    }, ct);

    public void Dispose() => _client.Dispose();

    private async Task<MicrosoftToken> PostAsync(MicrosoftApp app, Dictionary<string, string> form, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(app);
        form["client_id"] = app.ClientId;
        if (app.Secret is { } secret)
            form["client_secret"] = secret;
        try
        {
            using var content = new FormUrlEncodedContent(form);
            using var response = await _client.PostAsync(MicrosoftMail.TokenUrl(app.Tenant), content, ct);
            var body = await response.Content.ReadAsStringAsync(ct);
            return response.IsSuccessStatusCode ? MicrosoftMail.Token(body) : throw MicrosoftMail.Error(body, (int)response.StatusCode);
        }
        catch (Exception ex) when (ex is HttpRequestException || ex is TaskCanceledException && !ct.IsCancellationRequested)
        {
            throw new DomainException("email.microsoft_unreachable", "Microsoft can't be reached just now. Try again in a little while.");
        }
    }
}

/// <summary>
/// Access tokens, held in memory while they last. Read and renewed in a scope of their own: whoever is sending an email
/// may have changes waiting in theirs, which a save of a new refresh token mustn't save along with it.
/// </summary>
internal sealed class MicrosoftMailTokens(IServiceScopeFactory scopes, IMicrosoftMailClient client, IClock clock) : IMicrosoftMailTokens, IDisposable
{
    /// <summary>A token is renewed this long before it lapses, so a send never starts with one about to.</summary>
    private static readonly TimeSpan Margin = TimeSpan.FromMinutes(5);

    private readonly ConcurrentDictionary<MailPurpose, (string Token, DateTimeOffset Until, string? From)> _held = new();
    private readonly SemaphoreSlim _one = new(1, 1);

    public async Task<string> AccessTokenAsync(MailPurpose purpose, CancellationToken ct)
    {
        await _one.WaitAsync(ct);
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<OpenBookDbContext>();
            var secrets = scope.ServiceProvider.GetRequiredService<ISecretProtector>();
            var signIn = await db.MicrosoftMail.SingleOrDefaultAsync(m => m.Purpose == purpose, ct);
            if (signIn?.ProtectedRefreshToken is not { } stored)
                throw new DomainException("email.microsoft_not_connected", "The mailbox isn't signed in at Microsoft yet: an admin does it under Settings, Email.");
            // With several servers, or after a restore, the sign-in may be another than the token held is for.
            if (_held.TryGetValue(purpose, out var held) && held.Until > clock.UtcNow && held.From == stored)
                return held.Token;

            var refresh = secrets.Unprotect(stored)
                ?? throw new DomainException("email.microsoft_sign_in_again", "The mailbox's sign-in can't be read on this computer: an admin signs it in again under Settings, Email.");
            var app = new MicrosoftApp(signIn.ClientId, signIn.Tenant, signIn.ProtectedSecret is { } secret
                ? secrets.Unprotect(secret) ?? throw new DomainException("email.microsoft_secret_unreadable", "The saved client secret can't be read on this computer. Enter it again.")
                : null);
            var answer = await client.RefreshAsync(app, refresh, MicrosoftMail.Scopes(purpose), ct);
            if (answer.RefreshToken is { } next && next != refresh)
            {
                var renewed = secrets.Protect(next);
                signIn.Renew(renewed);
                try
                {
                    await db.SaveChangesAsync(ct);
                    stored = renewed;
                }
                catch (Exception ex) when (ex is DomainException or DbUpdateException)
                {
                    // Busy, or signed in afresh meanwhile: the token in use goes on working, and the next one is kept.
                }
            }

            _held[purpose] = (answer.AccessToken, clock.UtcNow + answer.ExpiresIn - Margin, stored);
            return answer.AccessToken;
        }
        finally
        {
            _one.Release();
        }
    }

    public void Dispose() => _one.Dispose();

    public void Forget(MailPurpose purpose) => _held.TryRemove(purpose, out _);
}
