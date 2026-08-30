using System.Collections.Concurrent;

namespace FinApp.Server.Health;

/// <summary>The things worth counting. Each one is recorded at the point it happens, never inferred later.</summary>
public enum HealthSignal
{
    /// <summary>A user was redirected out to a provider's consent screen.</summary>
    ExternalSignInStarted,
    /// <summary>A provider's callback came back and the exchange succeeded.</summary>
    ExternalSignInCompleted,
    /// <summary>A crash report arrived from a browser (OPEN-BETA B1).</summary>
    ClientError,
    /// <summary>An assistant model call threw or returned nothing usable.</summary>
    AssistantCallFailed,
    /// <summary>An assistant model call returned something.</summary>
    AssistantCallSucceeded,
}

/// <summary>Counts over a rolling window, for <see cref="HealthRules"/> to read.</summary>
public sealed record HealthCounts(
    int SignInStarted, int SignInCompleted, int ClientErrors, int AssistantFailed, int AssistantSucceeded)
{
    public int AssistantCalls => AssistantFailed + AssistantSucceeded;
}

/// <summary>
/// A rolling in-memory tally of a handful of events, so something can notice when their <b>shape</b> goes wrong.
/// <para>
/// ⚠️⚠️ <b>This exists because of a two-day outage whose only evidence was an absence.</b> When the service worker
/// began swallowing the OAuth callback, nothing errored anywhere: the redirect out to Google still happened, the
/// callback simply never arrived, and every dashboard that counts errors read perfectly healthy. An absence is
/// only visible to somebody who already suspects it — so the thing to count is not failures, it is <b>pairs that
/// stopped pairing</b>.
/// </para>
/// <para>
/// ★ In memory, and deliberately, unlike the assistant's spend counters: a restart losing an hour of health
/// signals costs one delayed alert, whereas a restart losing the spend tally costs money. Different data, different
/// durability. It also means no database write on the hot path of a sign-in.
/// </para>
/// </summary>
public sealed class HealthSignals
{
    /// <summary>Nothing is read beyond this, so nothing needs keeping beyond it.</summary>
    private static readonly TimeSpan Retention = TimeSpan.FromHours(6);

    /// <summary>A ceiling so a crash storm cannot turn a health monitor into a memory leak. Old entries go first,
    /// which is right: the newest events are the ones any rule is about to read.</summary>
    private const int MaxEvents = 20_000;

    private readonly ConcurrentQueue<(DateTimeOffset At, HealthSignal Kind)> _events = new();

    public void Record(HealthSignal kind) => Record(kind, DateTimeOffset.UtcNow);

    public void Record(HealthSignal kind, DateTimeOffset at)
    {
        _events.Enqueue((at, kind));
        Trim(at);
    }

    public HealthCounts Snapshot(TimeSpan window, DateTimeOffset now)
    {
        var from = now - window;
        int started = 0, completed = 0, clientErrors = 0, aiFailed = 0, aiOk = 0;

        foreach (var (at, kind) in _events)
        {
            if (at < from) continue;
            switch (kind)
            {
                case HealthSignal.ExternalSignInStarted: started++; break;
                case HealthSignal.ExternalSignInCompleted: completed++; break;
                case HealthSignal.ClientError: clientErrors++; break;
                case HealthSignal.AssistantCallFailed: aiFailed++; break;
                case HealthSignal.AssistantCallSucceeded: aiOk++; break;
            }
        }

        return new HealthCounts(started, completed, clientErrors, aiFailed, aiOk);
    }

    private void Trim(DateTimeOffset now)
    {
        var cutoff = now - Retention;
        while (_events.TryPeek(out var oldest) && (oldest.At < cutoff || _events.Count > MaxEvents))
            _events.TryDequeue(out _);
    }
}
