using System.Security.Cryptography;
using System.Text;

namespace DevStrider.Desktop.Services;

/// <summary>
/// DPAPI wrapper: encrypts a string with the current Windows user's master key so it can
/// be persisted in plaintext-ish storage (Mongo, registry, JSON file) without being
/// readable from another account. Round-trips through base64 so the ciphertext survives
/// the JSON / BSON layer cleanly. Used today by <see cref="RegistryStore.WriteProtected"/>;
/// available for any future secret a feature wants to stash.
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
