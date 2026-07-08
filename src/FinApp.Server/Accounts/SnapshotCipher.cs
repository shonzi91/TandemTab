using System.Security.Cryptography;
using System.Text;
using Google.Cloud.Kms.V1;
using Google.Protobuf;

namespace FinApp.Server.Accounts;

/// <summary>
/// Protects the account-snapshot <c>Payload</c> at rest. The snapshot is a client-owned opaque blob, so we can
/// encrypt the whole thing transparently: writers call <see cref="ProtectAsync"/>, readers call
/// <see cref="UnprotectAsync"/>. Implementations must round-trip, and must pass legacy (unencrypted) values through
/// unchanged so existing rows keep working until they're re-saved or migrated.
/// </summary>
public interface ISnapshotCipher
{
    /// <summary>True when this cipher actually encrypts (so a background pass should migrate legacy plaintext rows).</summary>
    bool Encrypts { get; }

    /// <summary>Whether a stored value is already in the encrypted envelope format.</summary>
    bool IsEncrypted(string stored);

    Task<string> ProtectAsync(string plaintext, CancellationToken ct = default);
    Task<string> UnprotectAsync(string stored, CancellationToken ct = default);
}

/// <summary>No-op cipher for local dev / tests (and any environment with no KMS key configured). Stores plaintext.</summary>
public sealed class PassthroughSnapshotCipher : ISnapshotCipher
{
    public bool Encrypts => false;
    public bool IsEncrypted(string stored) => stored.StartsWith(EnvelopeSnapshotCipher.Prefix, StringComparison.Ordinal);
    public Task<string> ProtectAsync(string plaintext, CancellationToken ct = default) => Task.FromResult(plaintext);
    public Task<string> UnprotectAsync(string stored, CancellationToken ct = default) => Task.FromResult(stored);
}

/// <summary>
/// Envelope encryption: each write gets a fresh random 256-bit data key (DEK) that encrypts the payload with
/// AES-256-GCM; the DEK itself is wrapped by a key-encryption key held elsewhere (see <see cref="WrapAsync"/>).
/// The stored form is <c>ENC1:</c> + base64(wrappedDek ‖ nonce ‖ tag ‖ ciphertext). The KEK never touches the DB;
/// compromising the database alone yields only ciphertext. Legacy plaintext (no prefix) is passed through on read.
/// </summary>
public abstract class EnvelopeSnapshotCipher : ISnapshotCipher
{
    public const string Prefix = "ENC1:";
    private const int NonceLen = 12;   // AES-GCM standard nonce
    private const int TagLen = 16;     // AES-GCM tag

    public bool Encrypts => true;
    public bool IsEncrypted(string stored) => stored.StartsWith(Prefix, StringComparison.Ordinal);

    /// <summary>Wrap (encrypt) the data key with the key-encryption key.</summary>
    protected abstract Task<byte[]> WrapAsync(byte[] dek, CancellationToken ct);

    /// <summary>Unwrap (decrypt) the data key.</summary>
    protected abstract Task<byte[]> UnwrapAsync(byte[] wrappedDek, CancellationToken ct);

    public async Task<string> ProtectAsync(string plaintext, CancellationToken ct = default)
    {
        var dek = RandomNumberGenerator.GetBytes(32);
        try
        {
            var nonce = RandomNumberGenerator.GetBytes(NonceLen);
            var pt = Encoding.UTF8.GetBytes(plaintext);
            var ctBytes = new byte[pt.Length];
            var tag = new byte[TagLen];
            using (var aes = new AesGcm(dek, TagLen))
                aes.Encrypt(nonce, pt, ctBytes, tag);

            var wrapped = await WrapAsync(dek, ct);

            using var ms = new MemoryStream();
            using (var w = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true))
            {
                w.Write(wrapped.Length);
                w.Write(wrapped);
                w.Write(nonce);
                w.Write(tag);
                w.Write(ctBytes.Length);
                w.Write(ctBytes);
            }
            return Prefix + Convert.ToBase64String(ms.ToArray());
        }
        finally { CryptographicOperations.ZeroMemory(dek); }
    }

    public async Task<string> UnprotectAsync(string stored, CancellationToken ct = default)
    {
        if (!IsEncrypted(stored)) return stored;   // legacy plaintext row — leave as-is until it's next saved

        var blob = Convert.FromBase64String(stored[Prefix.Length..]);
        using var ms = new MemoryStream(blob);
        using var r = new BinaryReader(ms);
        var wrapped = r.ReadBytes(r.ReadInt32());
        var nonce = r.ReadBytes(NonceLen);
        var tag = r.ReadBytes(TagLen);
        var ctBytes = r.ReadBytes(r.ReadInt32());

        var dek = await UnwrapAsync(wrapped, ct);
        try
        {
            var pt = new byte[ctBytes.Length];
            using (var aes = new AesGcm(dek, TagLen))
                aes.Decrypt(nonce, ctBytes, tag, pt);
            return Encoding.UTF8.GetString(pt);
        }
        finally { CryptographicOperations.ZeroMemory(dek); }
    }
}

/// <summary>Wraps the per-write data key with a Google Cloud KMS key (the KEK). Uses Application Default Credentials
/// — on Cloud Run that's the runtime service account, which needs <c>roles/cloudkms.cryptoKeyEncrypterDecrypter</c>
/// on the key. <paramref name="keyName"/> is the full resource id
/// (<c>projects/…/locations/…/keyRings/…/cryptoKeys/…</c>).</summary>
public sealed class KmsSnapshotCipher : EnvelopeSnapshotCipher
{
    private readonly KeyManagementServiceClient _kms;
    private readonly string _keyName;

    public KmsSnapshotCipher(string keyName, KeyManagementServiceClient? kms = null)
    {
        _keyName = keyName;
        _kms = kms ?? KeyManagementServiceClient.Create();
    }

    protected override async Task<byte[]> WrapAsync(byte[] dek, CancellationToken ct)
    {
        var resp = await _kms.EncryptAsync(new EncryptRequest { Name = _keyName, Plaintext = ByteString.CopyFrom(dek) }, ct);
        return resp.Ciphertext.ToByteArray();
    }

    protected override async Task<byte[]> UnwrapAsync(byte[] wrappedDek, CancellationToken ct)
    {
        var resp = await _kms.DecryptAsync(new DecryptRequest { Name = _keyName, Ciphertext = ByteString.CopyFrom(wrappedDek) }, ct);
        return resp.Plaintext.ToByteArray();
    }
}
