using FinApp.Server.BankSync;
using Microsoft.Extensions.Configuration;

namespace FinApp.Server.Tests;

/// <summary>
/// Bank balances and staged transaction amounts/descriptions are encrypted at rest with <see cref="BankDataProtector"/>.
/// These lock in the guarantees the storage layer relies on: values round-trip, ciphertext is opaque and randomised,
/// and legacy plaintext (rows written before encryption) is still readable so no migration is needed.
/// </summary>
public class BankDataProtectorTests
{
    private static BankDataProtector Make(string key = "test-signing-key-at-least-32-chars-long!!") =>
        new(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Jwt:Key"] = key }).Build());

    [Theory]
    [InlineData("1234.56")]
    [InlineData("TESCO STORES 3421 LONDON")]
    [InlineData("café — münchen 街角")]
    public void Round_trips_values(string plaintext)
    {
        var p = Make();
        var cipher = p.Protect(plaintext);
        Assert.NotEqual(plaintext, cipher);
        Assert.StartsWith("enc1:", cipher);
        Assert.Equal(plaintext, p.Unprotect(cipher));
    }

    [Fact]
    public void Encryption_is_randomised_per_call()
    {
        var p = Make();
        Assert.NotEqual(p.Protect("same"), p.Protect("same"));   // fresh nonce each time
    }

    [Fact]
    public void Legacy_plaintext_reads_through_unchanged()
    {
        // Rows written before encryption have no envelope prefix — they must decrypt to themselves, not throw.
        Assert.Equal("99.99", Make().Unprotect("99.99"));
    }

    [Fact]
    public void Null_and_empty_pass_through()
    {
        var p = Make();
        Assert.Null(p.Protect(null));
        Assert.Equal("", p.Protect(""));
        Assert.Null(p.Unprotect(null));
    }

    [Fact]
    public void A_different_key_cannot_decrypt()
    {
        var cipher = Make("key-number-one-padded-to-32-characters!!").Protect("secret balance");
        // Wrong key → GCM auth fails; we return the raw stored value rather than crashing the read.
        Assert.Equal(cipher, Make("key-number-two-padded-to-32-characters!!").Unprotect(cipher));
    }
}
