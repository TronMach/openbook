using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;
using OpenBook.Application.Forms;

namespace OpenBook.Web.Security;

/// <summary>
/// Encrypts the SMTP password with ASP.NET Data Protection. Keys live in the user profile, so a company
/// file restored on another computer needs the password entered again.
/// </summary>
internal sealed class DataProtectionSecretProtector(IDataProtectionProvider provider) : ISecretProtector
{
    private readonly IDataProtector _protector = provider.CreateProtector("OpenBook.Secrets.v1");

    public string Protect(string plaintext) => _protector.Protect(plaintext);

    public string? Unprotect(string protectedText)
    {
        try
        {
            return _protector.Unprotect(protectedText);
        }
        catch (CryptographicException)
        {
            return null;
        }
    }
}
