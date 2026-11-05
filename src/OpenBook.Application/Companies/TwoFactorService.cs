using System.Text.Json;
using OpenBook.Application.Forms;
using OpenBook.Domain.Auditing;
using OpenBook.Domain.Common;
using OpenBook.Domain.Companies;

namespace OpenBook.Application.Companies;

/// <summary>A secret to set up an authenticator app with: as text to type in, and as the address the QR code holds.</summary>
public sealed record TwoFactorSetup(string Secret, string Uri);

public sealed record TwoFactorEnabled(IReadOnlyList<string> RecoveryCodes, AuthenticatedUser User);

/// <summary>
/// Two-factor sign-in with an authenticator app (<see cref="Totp"/>) and one-time recovery codes: setting it up,
/// the second step of signing in, turning it off, and who must use it (<see cref="SignInSettings"/>).
/// </summary>
public sealed class TwoFactorService(ICompanyStore store, IClock clock, ICurrentUser currentUser, ISecretProtector protector, AuthService auth)
{
    private const string UserEntity = "user";
    private const string SettingsEntity = "sign_in_settings";

    /// <summary>A new secret for <paramref name="userId"/>'s app. Nothing is saved until <see cref="EnableAsync"/> confirms it.</summary>
    /// <param name="keep">A secret already being set up (after a wrong code), to show again.</param>
    public async Task<TwoFactorSetup> NewSetupAsync(Guid userId, string? keep = null, CancellationToken ct = default)
    {
        var user = await GetUserAsync(userId, ct);
        var company = await store.GetCompanyAsync(ct);
        var secret = Base32.Decode(keep) is { Length: Totp.SecretBytes } kept ? kept : Totp.NewSecret();
        var account = company is null ? user.Username : $"{user.Username} ({company.Name})";
        return new TwoFactorSetup(Base32.Encode(secret), Totp.Uri("OpenBook", account, secret));
    }

    /// <summary>Turns two-factor sign-in on once a code from the app shows it was set up right.</summary>
    /// <returns>The recovery codes, shown once, and the user for re-issuing the caller's cookie.</returns>
    public async Task<TwoFactorEnabled> EnableAsync(Guid userId, string secret, string code, CancellationToken ct = default)
    {
        var user = await GetUserAsync(userId, ct);
        if (user.HasTwoFactor)
            throw new DomainException("twofactor.already_on", "Two-factor sign-in is already on. Turn it off first to use another app.");
        var bytes = Base32.Decode(secret);
        if (bytes is not { Length: Totp.SecretBytes })
            throw new DomainException("twofactor.secret_invalid", "Start the setup again: the secret was damaged.");
        if (Totp.Match(bytes, code, clock.UtcNow, lastStep: null) is not { } step)
            throw new DomainException("twofactor.wrong_code", "That code doesn't match. Check the app shows OpenBook and try the newest code.");

        var codes = TwoFactor.NewRecoveryCodes();
        user.EnableTwoFactor(protector.Protect(Base32.Encode(bytes)), step, codes.Select(TwoFactor.HashRecoveryCode), clock.UtcNow);
        store.Add(Audit(user, "two-factor sign-in turned on"));
        await store.SaveChangesAsync(ct);
        return new TwoFactorEnabled(codes, await store.AuthenticatedAsync(user, ct));
    }

    /// <summary>Needs the password and a code (or a recovery code), so a borrowed signed-in browser can't turn it off.</summary>
    public async Task DisableAsync(Guid userId, string password, string code, CancellationToken ct = default)
    {
        var user = await GetUserAsync(userId, ct);
        if (!user.HasTwoFactor)
            return;
        await CheckPasswordAndCodeAsync(user, password, code, ct);
        user.DisableTwoFactor();
        store.Add(Audit(user, "two-factor sign-in turned off"));
        await store.SaveChangesAsync(ct);
    }

    /// <summary>Asks for the password, and the code when two-factor is on, before something a borrowed browser mustn't do.</summary>
    public async Task CheckIdentityAsync(Guid userId, string password, string code, CancellationToken ct = default)
    {
        var user = await GetUserAsync(userId, ct);
        if (user.HasTwoFactor)
        {
            await CheckPasswordAndCodeAsync(user, password, code, ct);
        }
        else if (!await auth.VerifyPasswordAsync(userId, password, ct))
        {
            throw new DomainException("auth.wrong_password", "That password isn't right.");
        }
    }

    /// <summary>Replaces every recovery code; the old ones stop working.</summary>
    public async Task<IReadOnlyList<string>> NewRecoveryCodesAsync(Guid userId, string password, string code, CancellationToken ct = default)
    {
        var user = await GetUserAsync(userId, ct);
        if (!user.HasTwoFactor)
            throw new DomainException("twofactor.off", "Turn two-factor sign-in on first.");
        await CheckPasswordAndCodeAsync(user, password, code, ct);
        var codes = TwoFactor.NewRecoveryCodes();
        user.ReplaceRecoveryCodes(codes.Select(TwoFactor.HashRecoveryCode));
        store.Add(Audit(user, "recovery codes replaced"));
        await store.SaveChangesAsync(ct);
        return codes;
    }

    /// <summary>
    /// The second step of signing in, after <see cref="AuthService.SignInAsync"/> answered
    /// <see cref="SignInStatus.TwoFactorRequired"/>: a code from the app or a recovery code. Wrong codes count toward
    /// the same lockout as wrong passwords.
    /// </summary>
    /// <param name="securityStamp">The stamp the password step saw; a password change since then makes it start again.</param>
    public async Task<SignInResult> SignInAsync(Guid userId, string securityStamp, string code, CancellationToken ct = default)
    {
        var user = await store.GetUserAsync(userId, ct);
        var now = clock.UtcNow;
        if (user is not { IsActive: true, HasTwoFactor: true } || user.SecurityStamp != securityStamp || user.AccessEnded(CompanyTime.TodayAt(now, CompanyTime.Zone)))
            return new SignInResult(SignInStatus.InvalidCredentials);
        if (user.IsLockedOut(now))
            return new SignInResult(SignInStatus.LockedOut, LockedUntil: user.LockedUntil);

        var usedRecovery = false;
        if (!CodeMatches(user, code, now, out usedRecovery) || !await store.ClaimSignInCodeAsync(user, ct))
        {
            await store.CountFailedLoginAsync(user, ct);
            user.LockIfOverLimit(now, User.MaxFailedLogins);
            await store.SaveChangesAsync(ct);
            return user.IsLockedOut(now)
                ? new SignInResult(SignInStatus.LockedOut, LockedUntil: user.LockedUntil)
                : new SignInResult(SignInStatus.InvalidCode);
        }

        if (usedRecovery)
            store.Add(AuditRecord.Create(now, user.Username, AuditAction.Updated, UserEntity, user.Id, null,
                JsonSerializer.Serialize(new { Changed = "signed in with a recovery code", Left = user.RecoveryCodesLeft })));
        user.RecordSuccessfulLogin(now);
        await store.SaveChangesAsync(ct);
        return new SignInResult(SignInStatus.Succeeded, await store.AuthenticatedAsync(user, ct), TwoFactorSince: user.TwoFactorEnabledAt,
            UsedRecoveryCode: usedRecovery, RecoveryCodesLeft: user.RecoveryCodesLeft);
    }

    /// <summary>
    /// The second step skipped on a browser remembered for this user since two-factor sign-in was turned on
    /// (the host checks its cookie); it still has to be the same user, stamp and setup.
    /// </summary>
    public async Task<SignInResult> SignInRememberedAsync(Guid userId, string securityStamp, DateTimeOffset twoFactorSince, CancellationToken ct = default)
    {
        var user = await store.GetUserAsync(userId, ct);
        var now = clock.UtcNow;
        if (user is not { IsActive: true, TwoFactorEnabledAt: { } since } || since != twoFactorSince || user.SecurityStamp != securityStamp
            || user.AccessEnded(CompanyTime.TodayAt(now, CompanyTime.Zone)))
            return new SignInResult(SignInStatus.InvalidCredentials);
        user.RecordSuccessfulLogin(now);
        await store.SaveChangesAsync(ct);
        return new SignInResult(SignInStatus.Succeeded, await store.AuthenticatedAsync(user, ct));
    }

    public async Task<SignInSettings> SettingsAsync(CancellationToken ct = default) =>
        await store.GetSignInSettingsAsync(ct) ?? SignInSettings.Create();

    /// <param name="from">Null: up to each person; otherwise everyone with this role or a higher one.</param>
    public async Task<SignInSettings> RequireAsync(UserRole? from, CancellationToken ct = default)
    {
        var settings = await store.GetSignInSettingsAsync(ct);
        string? before = null;
        if (settings is null)
        {
            settings = SignInSettings.Create();
            store.Add(settings);
        }
        else
        {
            before = Snapshot(settings);
        }

        settings.RequireTwoFactor(from);
        store.Add(AuditRecord.Create(clock.UtcNow, currentUser.Actor, before is null ? AuditAction.Created : AuditAction.Updated, SettingsEntity, settings.Id, before, Snapshot(settings)));
        await store.SaveChangesAsync(ct);
        return settings;
    }

    private bool CodeMatches(User user, string? code, DateTimeOffset now, out bool usedRecovery)
    {
        usedRecovery = false;
        if (TwoFactor.Digits(code) is not null)
        {
            var secret = user.ProtectedTwoFactorSecret is { } stored ? Base32.Decode(protector.Unprotect(stored)) : null;
            if (secret is null)
                throw new DomainException("twofactor.secret_unreadable",
                    "This server can't read your authenticator secret (it was set up on another server). Use a recovery code, or ask an admin to turn two-factor sign-in off for you.");
            return Totp.Match(secret, code, now, user.LastTwoFactorStep) is { } step && user.AcceptTwoFactorStep(step);
        }

        usedRecovery = !string.IsNullOrWhiteSpace(code) && user.UseRecoveryCode(TwoFactor.HashRecoveryCode(code));
        return usedRecovery;
    }

    private async Task CheckPasswordAndCodeAsync(User user, string password, string code, CancellationToken ct)
    {
        if (!await auth.VerifyPasswordAsync(user.Id, password, ct))
            throw new DomainException("auth.wrong_password", "The password is incorrect.");
        if (!CodeMatches(user, code, clock.UtcNow, out _) || !await store.ClaimSignInCodeAsync(user, ct))
        {
            await store.CountFailedLoginAsync(user, ct);
            user.LockIfOverLimit(clock.UtcNow, User.MaxFailedLogins);
            await store.SaveChangesAsync(ct);
            throw new DomainException("twofactor.wrong_code", "That code doesn't match. Use the newest code from your app, or a recovery code.");
        }
    }

    private async Task<User> GetUserAsync(Guid userId, CancellationToken ct) =>
        await store.GetUserAsync(userId, ct) ?? throw new EntityNotFoundException(UserEntity, userId);

    private AuditRecord Audit(User user, string changed) =>
        AuditRecord.Create(clock.UtcNow, currentUser.Actor, AuditAction.Updated, UserEntity, user.Id, null, JsonSerializer.Serialize(new { Changed = changed }));

    private static string Snapshot(SignInSettings s) => SingleSignOnService.Snapshot(s);
}
