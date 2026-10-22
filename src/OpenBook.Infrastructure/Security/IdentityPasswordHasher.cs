using Microsoft.AspNetCore.Identity;
using OpenBook.Application.Companies;

namespace OpenBook.Infrastructure.Security;

/// <summary>ASP.NET Core Identity's PBKDF2 hasher; it flags old hashes for upgrade when its defaults strengthen.</summary>
internal sealed class IdentityPasswordHasher : Application.Companies.IPasswordHasher
{
    // Identity's hasher takes a user object but doesn't use it.
    private static readonly object NoUser = new();
    private readonly PasswordHasher<object> _hasher = new();

    public string Hash(string password) => _hasher.HashPassword(NoUser, password);

    public PasswordCheck Verify(string hash, string password) =>
        _hasher.VerifyHashedPassword(NoUser, hash, password) switch
        {
            PasswordVerificationResult.Success => PasswordCheck.Success,
            PasswordVerificationResult.SuccessRehashNeeded => PasswordCheck.SuccessRehashNeeded,
            _ => PasswordCheck.Failed,
        };
}
