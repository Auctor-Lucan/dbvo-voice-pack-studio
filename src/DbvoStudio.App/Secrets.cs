using System.Security.Cryptography;
using System.Text;

namespace DbvoStudio.App;

/// <summary>Encrypts API keys with Windows DPAPI (current user) before they are written to settings.json.</summary>
public static class Secrets
{
    private const string Prefix = "dpapi:";

    public static string Protect(string plain)
    {
        if (string.IsNullOrEmpty(plain)) return "";
        var b = ProtectedData.Protect(Encoding.UTF8.GetBytes(plain), null, DataProtectionScope.CurrentUser);
        return Prefix + Convert.ToBase64String(b);
    }

    public static string Unprotect(string stored)
    {
        if (string.IsNullOrEmpty(stored)) return "";
        if (!stored.StartsWith(Prefix)) return stored;
        try
        {
            var b = ProtectedData.Unprotect(Convert.FromBase64String(stored[Prefix.Length..]), null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(b);
        }
        catch
        {
            return "";
        }
    }
}
