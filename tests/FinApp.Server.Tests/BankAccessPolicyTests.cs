using FinApp.Server.BankSync;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace FinApp.Server.Tests;

public class BankAccessPolicyTests
{
    private static BankAccessPolicy Policy(string? allowed)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["BankSync:AllowedEmails"] = allowed })
            .Build();
        return new BankAccessPolicy(config);
    }

    [Fact]
    public void Empty_allowlist_allows_everyone()
    {
        var p = Policy(null);
        Assert.False(p.Restricted);
        Assert.True(p.IsAllowed("anyone@example.com"));
        Assert.True(p.IsAllowed(null));
    }

    [Fact]
    public void Configured_allowlist_permits_only_listed_emails_case_insensitively()
    {
        var p = Policy("owner@example.com, Second@Example.com");
        Assert.True(p.Restricted);
        Assert.True(p.IsAllowed("owner@example.com"));
        Assert.True(p.IsAllowed("OWNER@example.com"));   // case-insensitive
        Assert.True(p.IsAllowed("second@example.com"));  // trimmed + lowered
        Assert.False(p.IsAllowed("stranger@example.com"));
        Assert.False(p.IsAllowed(null));
        Assert.False(p.IsAllowed(""));
    }
}
