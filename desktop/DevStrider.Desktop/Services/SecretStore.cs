using System.Security.Cryptography;
using System.Text;

namespace DevStrider.Desktop.Services;

/// <summary>
/// DPAPI wrapper: lets us persist the GitHub PAT in the local Mongo doc but keep it readable
/// only by the current Windows user. Round-trips through base64 so the ciphertext survives
/// the JSON / BSON layer cleanly.
/// </summary>
public static class SecretStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("DevStrider/v1");

    public static string Protect(string plaintext)
    {
        if (string.IsNullOrEmpty(plaintext)) return "";
        var bytes = Encoding.UTF8.GetBytes(plaintext);
        var encrypted = ProtectedData.Protect(bytes, Entropy, DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(encrypted);
    }

    public static string Unprotect(string ciphertextBase64)
    {
        if (string.IsNullOrEmpty(ciphertextBase64)) return "";
        try
        {
            var encrypted = Convert.FromBase64String(ciphertextBase64);
            var bytes = ProtectedData.Unprotect(encrypted, Entropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(bytes);
        }
        catch
        {
            return "";
        }
    }
}
