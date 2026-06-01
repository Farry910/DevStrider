using Microsoft.Win32;

namespace DevStrider.Desktop.Services;

/// <summary>
/// Thin wrapper over <c>HKCU\Software\DevStrider</c>. Used for values that should survive a
/// MongoDB wipe — the registry is the canonical copy, AppSettings is a cache pulled on
/// launch and pushed on save.
///
/// <para>
/// All values stored as <c>REG_SZ</c>. DPAPI-encrypted values are stored as base64 and
/// round-tripped via <see cref="SecretStore"/>.
/// </para>
/// </summary>
public sealed class RegistryStore
{
    private const string KeyPath = @"Software\DevStrider";

    /// <summary>Returns null when the value name doesn't exist.</summary>
    public string? Read(string name)
    {
        using var key = Registry.CurrentUser.OpenSubKey(KeyPath);
        return key?.GetValue(name) as string;
    }

    /// <summary>
    /// Empty string deletes the value (rather than persisting a blank entry); registry
    /// shouldn't accumulate dead values when the user clears a field.
    /// </summary>
    public void Write(string name, string value)
    {
        using var key = Registry.CurrentUser.CreateSubKey(KeyPath);
        if (key == null) return;
        if (string.IsNullOrEmpty(value))
        {
            try { key.DeleteValue(name, throwOnMissingValue: false); }
            catch { /* missing key is fine */ }
        }
        else
        {
            key.SetValue(name, value, RegistryValueKind.String);
        }
    }

    /// <summary>Reads a DPAPI-encrypted base64 blob and returns the plaintext (or null if absent).</summary>
    public string? ReadProtected(string name)
    {
        var b64 = Read(name);
        if (b64 == null) return null;
        if (b64.Length == 0) return "";
        return SecretStore.Unprotect(b64);
    }

    /// <summary>Encrypts the plaintext with DPAPI and stores the base64 ciphertext.</summary>
    public void WriteProtected(string name, string plaintext)
    {
        if (string.IsNullOrEmpty(plaintext)) Write(name, "");
        else Write(name, SecretStore.Protect(plaintext));
    }
}
