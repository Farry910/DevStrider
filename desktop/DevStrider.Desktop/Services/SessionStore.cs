using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DevStrider.Desktop.Services;

/// <summary>
/// Where the week-long bearer token sits between runs:
/// <c>%LOCALAPPDATA%\DevStrider\session.dat</c>, encrypted with DPAPI under the Windows account
/// that wrote it.
///
/// <para>
/// A separate file from <c>settings.json</c> on purpose. Settings describe this machine and are
/// worth reading in a text editor; this is a credential, it is per Windows user rather than per
/// machine, and signing out has to be able to delete it outright without taking a listener port
/// and a Word path with it.
/// </para>
///
/// <para>
/// DPAPI's user scope means the ciphertext is bound to the Windows account: copied to another
/// machine, or read by another user on this one, it does not decrypt. That is the whole of the
/// protection and it is worth being clear about its limit — anything running <i>as this user</i>
/// can decrypt it, because it must be possible for this app to. What it replaces is a shared
/// PostgreSQL password sitting in cleartext in a settings file, which was neither scoped to a
/// user nor to a machine nor to a week.
/// </para>
///
/// <para>
/// P/Invoke rather than the <c>System.Security.Cryptography.ProtectedData</c> package: the app is
/// Windows-only and this is two entry points, which is the same trade the tray icon already makes
/// by using the in-box WinForms one.
/// </para>
/// </summary>
public sealed class SessionStore
{
    public static string FilePath => Path.Combine(SettingsStore.DirectoryPath, "session.dat");

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    /// <summary>
    /// The saved session, or null when there isn't one, it can't be decrypted, or it has already
    /// expired. Every one of those is the same thing to the caller — show the sign-in window — and
    /// an unreadable file is cleaned up rather than left to fail again on the next launch.
    /// </summary>
    public PortalSession? Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return null;
            var plain = Unprotect(File.ReadAllBytes(FilePath));
            if (plain == null) { Clear(); return null; }

            var session = JsonSerializer.Deserialize<PortalSession>(Encoding.UTF8.GetString(plain), Json);
            if (session == null || string.IsNullOrEmpty(session.Token) || session.UserId == 0) { Clear(); return null; }
            if (session.ExpiresAt <= DateTime.UtcNow) { Clear(); return null; }
            return session;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            System.Diagnostics.Debug.WriteLine($"session.dat unreadable: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Write the session out. Temp file then move, like the settings store: a crash mid-write
    /// leaves the previous token intact rather than a truncated file that fails to decrypt and
    /// costs the user a sign-in.
    /// </summary>
    public void Save(PortalSession session)
    {
        var cipher = Protect(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(session, Json)));
        if (cipher == null)
        {
            // DPAPI is not available (it needs a loaded user profile, which a service account may
            // not have). Staying signed in is a convenience; writing the token in the clear to buy
            // it is not a trade this app gets to make on the user's behalf.
            System.Diagnostics.Debug.WriteLine("DPAPI unavailable — the session will not be kept across restarts.");
            Clear();
            return;
        }

        Directory.CreateDirectory(SettingsStore.DirectoryPath);
        var temp = FilePath + ".tmp";
        File.WriteAllBytes(temp, cipher);
        File.Move(temp, FilePath, overwrite: true);
    }

    /// <summary>Delete the saved session. Idempotent — signing out when there is nothing to clear is fine.</summary>
    public void Clear()
    {
        try { if (File.Exists(FilePath)) File.Delete(FilePath); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            System.Diagnostics.Debug.WriteLine($"couldn't delete session.dat: {ex.Message}");
        }
    }

    // ── DPAPI ───────────────────────────────────────────────────────────────

    private const int CryptprotectUiForbidden = 0x1;

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int cbData;
        public IntPtr pbData;
    }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(
        ref DataBlob dataIn, string? description, IntPtr entropy, IntPtr reserved,
        IntPtr prompt, int flags, out DataBlob dataOut);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(
        ref DataBlob dataIn, IntPtr description, IntPtr entropy, IntPtr reserved,
        IntPtr prompt, int flags, out DataBlob dataOut);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr handle);

    private static byte[]? Protect(byte[] plain) =>
        Call(plain, (ref DataBlob input, out DataBlob output) =>
            CryptProtectData(ref input, "DevStrider session", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero,
                CryptprotectUiForbidden, out output));

    private static byte[]? Unprotect(byte[] cipher) =>
        Call(cipher, (ref DataBlob input, out DataBlob output) =>
            CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero,
                CryptprotectUiForbidden, out output));

    private delegate bool CryptCall(ref DataBlob input, out DataBlob output);

    /// <summary>
    /// Marshal in, call, marshal out, and free both sides whatever happened. The unmanaged buffer
    /// crypt32 hands back is ours to LocalFree; the pinned input has to be released even when the
    /// call fails, which is why every path goes through the finally.
    /// </summary>
    private static byte[]? Call(byte[] data, CryptCall crypt)
    {
        var input = new DataBlob();
        var output = new DataBlob();
        try
        {
            input.cbData = data.Length;
            input.pbData = Marshal.AllocHGlobal(data.Length);
            Marshal.Copy(data, 0, input.pbData, data.Length);

            if (!crypt(ref input, out output)) return null;

            var result = new byte[output.cbData];
            Marshal.Copy(output.pbData, result, 0, output.cbData);
            return result;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            System.Diagnostics.Debug.WriteLine($"DPAPI unavailable: {ex.Message}");
            return null;
        }
        finally
        {
            if (input.pbData != IntPtr.Zero)
            {
                // Zero the plaintext copy before releasing it rather than leaving it in whatever
                // the allocator hands out next.
                for (var i = 0; i < input.cbData; i++) Marshal.WriteByte(input.pbData, i, 0);
                Marshal.FreeHGlobal(input.pbData);
            }
            if (output.pbData != IntPtr.Zero) LocalFree(output.pbData);
        }
    }
}
