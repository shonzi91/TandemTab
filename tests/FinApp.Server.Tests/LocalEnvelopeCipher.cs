using System.Security.Cryptography;
using System.Text;
using FinApp.Server.Accounts;

namespace FinApp.Server.Tests;

/// <summary>
/// Envelope cipher that "wraps" the data key with a fixed local AES key instead of KMS — lets us exercise the real
/// envelope format, round-trip and compression deterministically, without cloud credentials. Shared by the cipher
/// unit tests and the end-to-end snapshot-encryption tests.
/// </summary>
public sealed class LocalEnvelopeCipher : EnvelopeSnapshotCipher
{
    private static readonly byte[] Kek = SHA256.HashData("test-kek"u8.ToArray());

    protected override Task<byte[]> WrapAsync(byte[] dek, CancellationToken ct) => Task.FromResult(WrapWithKek(dek));

    protected override Task<byte[]> UnwrapAsync(byte[] wrapped, CancellationToken ct)
    {
        var tag = wrapped[..16];
        var ctBytes = wrapped[16..];
        var dek = new byte[ctBytes.Length];
        using var aes = new AesGcm(Kek, 16);
        aes.Decrypt(new byte[12], ctBytes, tag, dek);
        return Task.FromResult(dek);
    }

    /// <summary>Writes the retired v1 envelope (raw UTF-8 inside the ciphertext) so readers can be tested against a
    /// row shaped the way production wrote them before compression landed. Production has no v1 writer any more —
    /// this is the only one left, and it exists so the compatibility path stays covered.</summary>
    public static string ProtectUncompressed(string plaintext)
    {
        var dek = SHA256.HashData("test-dek"u8.ToArray());
        var nonce = new byte[12];
        var pt = Encoding.UTF8.GetBytes(plaintext);
        var ctBytes = new byte[pt.Length];
        var tag = new byte[16];
        using (var aes = new AesGcm(dek, 16))
            aes.Encrypt(nonce, pt, ctBytes, tag);

        var wrapped = WrapWithKek(dek);
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
        return "ENC1:" + Convert.ToBase64String(ms.ToArray());
    }

    private static byte[] WrapWithKek(byte[] dek)
    {
        var nonce = new byte[12];
        var ctBytes = new byte[dek.Length];
        var tag = new byte[16];
        using var aes = new AesGcm(Kek, 16);
        aes.Encrypt(nonce, dek, ctBytes, tag);
        return tag.Concat(ctBytes).ToArray();
    }
}
