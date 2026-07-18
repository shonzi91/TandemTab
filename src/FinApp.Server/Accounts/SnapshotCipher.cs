using System.IO.Compression;
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
    public bool IsEncrypted(string stored) => EnvelopeSnapshotCipher.HasEnvelopePrefix(stored);
    public Task<string> ProtectAsync(string plaintext, CancellationToken ct = default) => Task.FromResult(plaintext);
    public Task<string> UnprotectAsync(string stored, CancellationToken ct = default) => Task.FromResult(stored);
}

/// <summary>
/// Envelope encryption: each write gets a fresh random 256-bit data key (DEK) that encrypts the payload with
/// AES-256-GCM; the DEK itself is wrapped by a key-encryption key held elsewhere (see <see cref="WrapAsync"/>).
/// The stored form is <c>ENC2:</c> + base64(wrappedDek ‖ nonce ‖ tag ‖ ciphertext). The KEK never touches the DB;
/// compromising the database alone yields only ciphertext. Legacy plaintext (no prefix) is passed through on read.
/// <para>
/// <b>The payload is gzipped before it is encrypted</b>, which is the only order that helps: ciphertext is
/// incompressible, so compressing after encryption would do nothing, and the stored column then also carries ~33%
/// base64 overhead on top of whatever it holds. Snapshot JSON is highly repetitive and shrinks by roughly an order
/// of magnitude, which is what the database write actually costs.
/// </para>
/// <para>
/// Two envelope versions exist and <b>both are always readable</b>: <c>ENC1:</c> (raw UTF-8 inside the ciphertext),
/// <c>ENC2:</c> (gzip inside the ciphertext). A row upgrades itself the next time it is saved; nothing needs
/// migrating.
/// </para>
/// <para>
/// <b>Which version is written is a deployment choice</b> (<see cref="CompressWrites"/>, config
/// <c>Snapshots:CompressWrites</c>), because it is the one direction that isn't backwards-compatible: a server
/// build that predates <c>ENC2:</c> doesn't recognise the prefix, so it treats such a row as legacy plaintext and
/// hands the client base64 garbage instead of failing. Rolling compression out therefore goes in two phases —
/// deploy a build that can *read* <c>ENC2:</c> while still writing <c>ENC1:</c>, then flip the flag once that build
/// is the one you'd roll back to. The flag is also the undo: turning it off returns writes to <c>ENC1:</c>, and
/// rows already written as <c>ENC2:</c> stay readable.
/// </para>
/// </summary>
public abstract class EnvelopeSnapshotCipher : ISnapshotCipher
{
    /// <summary>Envelope v1 — ciphertext holds raw UTF-8.</summary>
    public const string Prefix = "ENC1:";

    /// <summary>Envelope v2 — ciphertext holds gzipped UTF-8.</summary>
    public const string PrefixGzip = "ENC2:";

    private const int NonceLen = 12;   // AES-GCM standard nonce
    private const int TagLen = 16;     // AES-GCM tag

    /// <summary>Whether writes produce <c>ENC2:</c> (gzipped). Reads handle both regardless.</summary>
    public bool CompressWrites { get; }

    protected EnvelopeSnapshotCipher(bool compressWrites) => CompressWrites = compressWrites;

    /// <summary>True when a stored value carries any envelope prefix (either version).</summary>
    public static bool HasEnvelopePrefix(string stored) =>
        stored.StartsWith(Prefix, StringComparison.Ordinal) || stored.StartsWith(PrefixGzip, StringComparison.Ordinal);

    public bool Encrypts => true;
    public bool IsEncrypted(string stored) => HasEnvelopePrefix(stored);

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
            var raw = Encoding.UTF8.GetBytes(plaintext);
            var pt = CompressWrites ? Gzip(raw) : raw;
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
            return (CompressWrites ? PrefixGzip : Prefix) + Convert.ToBase64String(ms.ToArray());
        }
        finally { CryptographicOperations.ZeroMemory(dek); }
    }

    public async Task<string> UnprotectAsync(string stored, CancellationToken ct = default)
    {
        if (!IsEncrypted(stored)) return stored;   // legacy plaintext row — leave as-is until it's next saved

        var gzipped = stored.StartsWith(PrefixGzip, StringComparison.Ordinal);
        var blob = Convert.FromBase64String(stored[Prefix.Length..]);   // both prefixes are the same length
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
            return Encoding.UTF8.GetString(gzipped ? Gunzip(pt) : pt);
        }
        finally { CryptographicOperations.ZeroMemory(dek); }
    }

    private static byte[] Gzip(byte[] raw)
    {
        using var ms = new MemoryStream();
        // Optimal, not Fastest. Measured on snapshot-shaped JSON (~435KB, unique expense ids over a small set of
        // repeated category/fund ids): Fastest 4.5ms → 5.5x smaller, Optimal 7.2ms → 7.1x. The extra ~3ms of CPU is
        // noise beside the ~70ms KMS wrap on the same request, and it buys ~29% fewer bytes on a row that is both
        // stored indefinitely and shipped across clouds (Cloud Run europe-west1 → Neon eu-central-1).
        using (var gz = new GZipStream(ms, CompressionLevel.Optimal, leaveOpen: true))
            gz.Write(raw, 0, raw.Length);
        return ms.ToArray();
    }

    private static byte[] Gunzip(byte[] compressed)
    {
        using var src = new MemoryStream(compressed);
        using var gz = new GZipStream(src, CompressionMode.Decompress);
        using var outMs = new MemoryStream();
        gz.CopyTo(outMs);
        return outMs.ToArray();
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

    public KmsSnapshotCipher(string keyName, bool compressWrites = false, KeyManagementServiceClient? kms = null)
        : base(compressWrites)
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
