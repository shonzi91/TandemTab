using FinApp.Contracts;

namespace FinApp.Shared.UI.Services;

/// <summary>
/// Decides whether the signed-in account may use a gated capability, and raises the upgrade prompt when it may
/// not (OPEN-BETA P4).
/// <para>
/// <b>The prompt is the product decision here.</b> MONETIZATION.md's whole argument is that the upgrade moment is
/// the moment someone reaches for sharing / import / the payoff planner — not a pricing page they were shown on
/// arrival. So nothing in the app advertises Pro up front; a gate only speaks when it is actually reached, and it
/// names the specific feature that was blocked.
/// </para>
/// <para>
/// <b>This is convenience, not security.</b> Entitlement is server-side; a tampered client that skipped the gate
/// would reach an endpoint that refuses it. What this buys is a decent explanation instead of a bare error.
/// </para>
/// <para>
/// <b>Inert during beta.</b> <see cref="Allows"/> returns true for the "unlimited" plan, which is every account
/// while <c>Monetization:Enabled</c> is off — so gates can be added to call sites today and change nothing until
/// the flag lifts. That is the point of adding them now rather than in a rush later.
/// </para>
/// </summary>
public sealed class PlanGate(AuthState auth, FinAppApiClient api)
{
    private PlansDto? _plans;
    private bool _loading;

    /// <summary>Raised with the blocked feature key when a gate refuses. MainLayout subscribes and shows the
    /// upgrade modal; nothing else needs to know a paywall exists.</summary>
    public event Action<string>? Blocked;

    /// <summary>The tier table, once loaded. Null while monetization is off (nothing to load) or before the first
    /// gate is reached — <see cref="Allows"/> treats "don't know yet" as allowed, because a spurious paywall is a
    /// far worse failure than a briefly missing one.</summary>
    public PlansDto? Plans => _plans;

    public bool MonetizationOn => auth.CurrentUser?.MonetizationEnabled == true;

    /// <summary>Whether the current plan includes <paramref name="featureKey"/>. Fails OPEN in every uncertain
    /// case: monetization off, plan unknown, catalogue not loaded, or a key the server never sent.</summary>
    public bool Allows(string featureKey)
    {
        if (!MonetizationOn) return true;
        var plan = auth.CurrentUser?.Plan ?? "unlimited";
        if (plan is "pro" or "unlimited") return true;
        var f = _plans?.Features.FirstOrDefault(x => x.Key == featureKey);
        return f is null || f.InFree;
    }

    /// <summary>Gate a call site: returns true to proceed, or false having raised the upgrade prompt.
    /// <code>if (!Gate.Require(PlanFeatures.Share)) return;</code></summary>
    public bool Require(string featureKey)
    {
        if (Allows(featureKey)) return true;
        Blocked?.Invoke(featureKey);
        return false;
    }

    /// <summary>Load the tier table once, lazily. Only worth doing when monetization is on; while it's off every
    /// gate short-circuits before ever needing the catalogue.</summary>
    public async Task EnsureLoadedAsync(CancellationToken ct = default)
    {
        if (_plans is not null || _loading || !MonetizationOn) return;
        _loading = true;
        try { _plans = await api.GetPlansAsync(ct); }
        catch { /* stays null → Allows fails open */ }
        finally { _loading = false; }
    }

    /// <summary>Drop the cached tiers so the next gate re-reads them — called after an upgrade completes.</summary>
    public void Invalidate() => _plans = null;
}
