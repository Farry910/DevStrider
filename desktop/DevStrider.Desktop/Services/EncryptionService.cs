using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DevStrider.Desktop.Services;

/// <summary>
/// AES-GCM with a SHA-256-derived key from the user's shared passphrase ("hash key"). The
/// passphrase is the only secret — the group coordinates it out-of-band; this service has no
/// opinion on how it's distributed.
///
/// On-wire envelope is JSON so peers can spot it in the repo:
///   { "v":1, "alg":"AES-256-GCM", "n":"&lt;b64 nonce&gt;", "ct":"&lt;b64 ciphertext+tag&gt;" }
/// Plaintext that wasn't encrypted (no key set when pushed) is preserved as raw JSON so we
/// can still pull it in.
/// </summary>
public static class EncryptionService
{
    public const string CurrentVersion = "1";
    public const string Algorithm = "AES-256-GCM";

    private sealed record Envelope(string V, string Alg, string N, string Ct);

    public static byte[] DeriveKey(string passphrase)
    {
        // SHA-256 of UTF-8 passphrase bytes = 32-byte AES-256 key. PBKDF2 would be better
        // for low-entropy passphrases, but the project memo says "user manages key privacy"
        // so a deterministic, copy-pasteable hash is the friendlier choice.
        using var sha = SHA256.Create();
        return sha.ComputeHash(Encoding.UTF8.GetBytes(passphrase ?? ""));
    }

    /// <summary>Encrypt plaintext → JSON envelope (string). Returns plaintext unchanged when key is empty.</summary>
    public static string EncryptToEnvelope(string plaintext, string passphrase)
    {
        if (string.IsNullOrEmpty(passphrase)) return plaintext;
        var key = DeriveKey(passphrase);
        var nonce = RandomNumberGenerator.GetBytes(12);   // AES-GCM standard 96-bit nonce
        var plainBytes = Encoding.UTF8.GetBytes(plaintext);
        var ct = new byte[plainBytes.Length];
        var tag = new byte[16];                            // AES-GCM 128-bit tag
        using (var aes = new AesGcm(key, tag.Length))
        {
            aes.Encrypt(nonce, plainBytes, ct, tag);
        }
        // Concatenate ciphertext + tag so decryption only needs one b64 round-trip.
        var ctPlusTag = new byte[ct.Length + tag.Length];
        Buffer.BlockCopy(ct,  0, ctPlusTag, 0,         ct.Length);
        Buffer.BlockCopy(tag, 0, ctPlusTag, ct.Length, tag.Length);

        var env = new Envelope(CurrentVersion, Algorithm, Convert.ToBase64String(nonce), Convert.ToBase64String(ctPlusTag));
        return JsonSerializer.Serialize(env, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        });
    }

    /// <summary>
    /// Decrypt an envelope produced by <see cref="EncryptToEnvelope"/>. If the input doesn't
    /// look like an envelope (e.g. plaintext snapshot from an older push), returns it as-is.
    /// Throws when an envelope is present but the key is wrong / data is tampered.
    /// </summary>
    public static string DecryptFromEnvelope(string maybeEnvelope, string passphrase)
    {
        if (string.IsNullOrEmpty(maybeEnvelope)) return maybeEnvelope;
        var trimmed = maybeEnvelope.TrimStart();
        if (!trimmed.StartsWith("{")) return maybeEnvelope;
        Envelope? env;
        try
        {
            env = JsonSerializer.Deserialize<Envelope>(maybeEnvelope, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            });
        }
        catch { return maybeEnvelope; }                    // not an envelope; treat as plaintext

        if (env == null || string.IsNullOrEmpty(env.Alg) || env.Alg != Algorithm)
            return maybeEnvelope;
        if (string.IsNullOrEmpty(passphrase))
            throw new InvalidOperationException("Snapshot is encrypted but no sharing key is set in Settings.");

        var key   = DeriveKey(passphrase);
        var nonce = Convert.FromBase64String(env.N);
        var all   = Convert.FromBase64String(env.Ct);
        if (all.Length < 16) throw new CryptographicException("Ciphertext too short for AES-GCM tag.");
        var ctLen = all.Length - 16;
        var ct    = new byte[ctLen];
        var tag   = new byte[16];
        Buffer.BlockCopy(all, 0,     ct,  0, ctLen);
        Buffer.BlockCopy(all, ctLen, tag, 0, 16);
        var plain = new byte[ctLen];
        using var aes = new AesGcm(key, tag.Length);
        aes.Decrypt(nonce, ct, tag, plain);
        return Encoding.UTF8.GetString(plain);
    }

    /// <summary>True if the candidate string is wrapped in our AES-GCM envelope.</summary>
    public static bool LooksEncrypted(string s)
    {
        if (string.IsNullOrEmpty(s)) return false;
        var t = s.TrimStart();
        if (!t.StartsWith("{")) return false;
        try
        {
            var env = JsonSerializer.Deserialize<Envelope>(s, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            });
            return env != null && env.Alg == Algorithm && !string.IsNullOrEmpty(env.Ct);
        }
        catch { return false; }
    }
}
