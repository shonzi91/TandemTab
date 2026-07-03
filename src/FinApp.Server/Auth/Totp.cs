using System.Security.Cryptography;

namespace FinApp.Server.Auth;

/// <summary>
/// Minimal RFC 6238 TOTP (time-based one-time password) — the scheme used by Google Authenticator, Authy,
/// 1Password, etc. Self-contained (HMAC-SHA1, 30-second step, 6 digits) so we take no third-party dependency.
/// Secrets are Base32 (the format authenticator apps expect in an <c>otpauth://</c> URI).
/// </summary>
public static class Totp
{
    private const int Digits = 6;
    private const int StepSeconds = 30;

    /// <summary>Generate a fresh random Base32 secret (20 bytes = 160 bits, the RFC-recommended size).</summary>
    public static string GenerateSecret() => Base32Encode(RandomNumberGenerator.GetBytes(20));

    /// <summary>Compute the current 6-digit code for a Base32 secret (what an authenticator app would show now).</summary>
    public static string Generate(string secret) =>
        Compute(Base32Decode(secret), DateTimeOffset.UtcNow.ToUnixTimeSeconds() / StepSeconds);

    /// <summary>Build the <c>otpauth://</c> URI an authenticator app scans (or the user types the secret manually).</summary>
    public static string BuildOtpauthUri(string secret, string issuer, string account)
    {
        var label = Uri.EscapeDataString($"{issuer}:{account}");
        var iss = Uri.EscapeDataString(issuer);
        return $"otpauth://totp/{label}?secret={secret}&issuer={iss}&algorithm=SHA1&digits={Digits}&period={StepSeconds}";
    }

    /// <summary>
    /// Verify a user-entered code against the secret, tolerating a ±<paramref name="window"/>-step clock drift
    /// (default ±1 step = ±30s). Comparison is constant-time and inputs are sanitised (spaces/dashes stripped).
    /// </summary>
    public static bool Verify(string secret, string? code, int window = 1)
    {
        if (string.IsNullOrWhiteSpace(secret) || string.IsNullOrWhiteSpace(code)) return false;
        code = new string(code.Where(char.IsDigit).ToArray());
        if (code.Length != Digits) return false;

        byte[] key;
        try { key = Base32Decode(secret); } catch { return false; }
        var counter = DateTimeOffset.UtcNow.ToUnixTimeSeconds() / StepSeconds;

        for (var offset = -window; offset <= window; offset++)
        {
            var candidate = Compute(key, counter + offset);
            if (CryptographicOperations.FixedTimeEquals(
                    System.Text.Encoding.ASCII.GetBytes(candidate),
                    System.Text.Encoding.ASCII.GetBytes(code)))
                return true;
        }
        return false;
    }

    private static string Compute(byte[] key, long counter)
    {
        Span<byte> counterBytes = stackalloc byte[8];
        System.Buffers.Binary.BinaryPrimitives.WriteInt64BigEndian(counterBytes, counter);
        var hash = HMACSHA1.HashData(key, counterBytes);

        var offset = hash[^1] & 0x0F;
        var binary = ((hash[offset] & 0x7F) << 24)
                     | ((hash[offset + 1] & 0xFF) << 16)
                     | ((hash[offset + 2] & 0xFF) << 8)
                     | (hash[offset + 3] & 0xFF);
        return (binary % (int)Math.Pow(10, Digits)).ToString().PadLeft(Digits, '0');
    }

    private const string Base32Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    private static string Base32Encode(byte[] data)
    {
        var sb = new System.Text.StringBuilder((data.Length + 4) / 5 * 8);
        int buffer = 0, bitsLeft = 0;
        foreach (var b in data)
        {
            buffer = (buffer << 8) | b;
            bitsLeft += 8;
            while (bitsLeft >= 5)
            {
                bitsLeft -= 5;
                sb.Append(Base32Alphabet[(buffer >> bitsLeft) & 0x1F]);
            }
        }
        if (bitsLeft > 0)
            sb.Append(Base32Alphabet[(buffer << (5 - bitsLeft)) & 0x1F]);
        return sb.ToString();
    }

    private static byte[] Base32Decode(string input)
    {
        input = input.TrimEnd('=').ToUpperInvariant().Replace(" ", "").Replace("-", "");
        var output = new List<byte>(input.Length * 5 / 8);
        int buffer = 0, bitsLeft = 0;
        foreach (var c in input)
        {
            var val = Base32Alphabet.IndexOf(c);
            if (val < 0) throw new FormatException("Invalid Base32 character.");
            buffer = (buffer << 5) | val;
            bitsLeft += 5;
            if (bitsLeft >= 8)
            {
                bitsLeft -= 8;
                output.Add((byte)((buffer >> bitsLeft) & 0xFF));
            }
        }
        return output.ToArray();
    }
}
