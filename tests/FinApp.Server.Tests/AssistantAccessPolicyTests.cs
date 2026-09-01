using FinApp.Server.Assistant;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace FinApp.Server.Tests;

/// <summary>
/// The experimental allowlist. Deliberately a copy of <see cref="BankAccessPolicyTests"/>: the two gates have the
/// same shape on purpose, so the test that proves one should read as the test that proves the other.
/// </summary>
public class AssistantAccessPolicyTests
{
    private static AssistantAccessPolicy Policy(string? allowed)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Assistant:AllowedEmails"] = allowed })
            .Build();
        return new AssistantAccessPolicy(config);
    }

    [Fact]
    public void Empty_allowlist_allows_everyone()
    {
        // ⚠️ The direction worth pinning: clearing the value OPENS the feature. Widening the rollout is meant to be
        // an env-var edit, which means an accidental blank one is a rollout too.
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
