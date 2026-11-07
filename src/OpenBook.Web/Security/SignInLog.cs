namespace OpenBook.Web.Security;

/// <summary>What the log may say about a sign-in attempt.</summary>
public static class SignInLog
{
    /// <summary>A typed username as a log may hold it: one line, at most 60 characters (someone may type a password there).</summary>
    public static string Name(string? typed)
    {
        var text = new string((typed ?? "").Where(c => !char.IsControl(c)).ToArray()).Trim();
        return text.Length <= 60 ? text : text[..60] + "…";
    }
}
