using System.Security.Cryptography;
using System.Text;

namespace FinApp.Server.BankSync;

/// <summary>
/// Encrypts sensitive bank fields at rest — balance snapshots and staged transaction amounts/descriptions — with
/// AES-256-GCM. The key is derived from the app's JWT signing key (SHA-256, domain-separated), so no extra secret
/// needs managing and it's available wherever the app runs (the real key in prod, the dev placeholder locally).
/// <para>Decryption is <b>tolerant</b>: any stored value without the <c>enc1:</c> envelope is treated as legacy
/// plaintext and returned unchanged. Existing rows keep working and are re-encrypted the next time they're written
/// (e.g. the next sync), so no data migration is required.</para>
/// </summary>
public sealed class BankDataProtector
{
    private const string Prefix = "enc1:";
    private const int NonceSize = 12;   // AES-GCM standard nonce
    private const int TagSize = 16;     // AES-GCM auth tag
    private readonly byte[] _key;

    public BankDataProtector(IConfiguration config)
    {
        // Domain-separated from the JWT usage so the two never share an effective key.
        var jwtKey = config["Jwt:Key"] ?? "dev-only-finapp-signing-key-change-me-in-production-please";
        _key = SHA256.HashData(Encoding.UTF8.GetBytes(jwtKey + "|finapp-bank-data-v1"));
    }

    /// <summary>Encrypt a value for storage. Null/empty passes through unchanged.</summary>
    public string? Protect(string? plaintext)
    {
        if (string.IsNullOrEmpty(plaintext)) return plaintext;
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var pt = Encoding.UTF8.GetBytes(plaintext);
        var ct = new byte[pt.Length];
        var tag = new byte[TagSize];
        using var gcm = new AesGcm(_key, TagSize);
        gcm.Encrypt(nonce, pt, ct, tag);
        var blob = new byte[NonceSize + TagSize + ct.Length];
        Buffer.BlockCopy(nonce, 0, blob, 0, NonceSize);
        Buffer.BlockCopy(tag, 0, blob, NonceSize, TagSize);
        Buffer.BlockCopy(ct, 0, blob, NonceSize + TagSize, ct.Length);
        return Prefix + Convert.ToBase64String(blob);
    }

    /// <summary>Decrypt a stored value. Values without the envelope prefix are returned as-is (legacy plaintext);
    /// a decrypt failure (corrupt data / rotated key) also returns the raw value rather than throwing, so reads
    /// never crash.</summary>
    public string? Unprotect(string? stored)
    {
        if (string.IsNullOrEmpty(stored) || !stored.StartsWith(Prefix, StringComparison.Ordinal)) return stored;
        try
        {
            var blob = Convert.FromBase64String(stored[Prefix.Length..]);
            if (blob.Length < NonceSize + TagSize) return stored;
            var pt = new byte[blob.Length - NonceSize - TagSize];
            using var gcm = new AesGcm(_key, TagSize);
            gcm.Decrypt(blob.AsSpan(0, NonceSize), blob.AsSpan(NonceSize + TagSize), blob.AsSpan(NonceSize, TagSize), pt);
            return Encoding.UTF8.GetString(pt);
        }
        catch { return stored; }
    }
}
