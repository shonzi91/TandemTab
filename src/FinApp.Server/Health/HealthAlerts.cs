namespace FinApp.Server.Health;

/// <summary>One message to actually send. <see cref="IsRecovery"/> flips the wording from "this broke" to "this
/// is working again".</summary>
public sealed record AlertAction(string Id, string Title, string Detail, bool IsRecovery);

/// <summary>
/// Decides what to <b>say</b>, given what is currently wrong. Separate from the rules and from the mailbox because
/// this is where the judgement lives, and judgement is the part worth testing.
/// <para>
/// Three behaviours, each answering a way monitoring usually fails:
/// <list type="bullet">
/// <item><b>Say it once.</b> A rule that is still tripped on the next cycle is not news; an alert every ten
/// minutes trains its reader to filter it, and a filtered alert is the same as no alert.</item>
/// <item><b>Say it again eventually.</b> Silence after the first message is indistinguishable from "fixed", so a
/// still-broken rule repeats on a slow cadence.</item>
/// <item><b>Say when it stops.</b> Without a recovery message the reader has to go and check — which is the
/// pull-not-push habit this whole thing exists to end.</item>
/// </list>
/// </para>
/// </summary>
public sealed class HealthAlerts
{
    private sealed record State(DateTimeOffset FirstSeen, DateTimeOffset LastSent);

    private readonly Dictionary<string, State> _firing = new();

    /// <summary>What to send this cycle. Empty is the normal answer and the one to hope for.</summary>
    public IReadOnlyList<AlertAction> Reconcile(
        IReadOnlyList<HealthFinding> findings, DateTimeOffset now, TimeSpan repeatAfter)
    {
        var actions = new List<AlertAction>();
        var live = findings.ToDictionary(f => f.Id);

        foreach (var finding in findings)
        {
            if (!_firing.TryGetValue(finding.Id, out var state))
            {
                _firing[finding.Id] = new State(now, now);
                actions.Add(new AlertAction(finding.Id, finding.Title, finding.Detail, IsRecovery: false));
            }
            else if (now - state.LastSent >= repeatAfter)
            {
                _firing[finding.Id] = state with { LastSent = now };
                var since = now - state.FirstSeen;
                actions.Add(new AlertAction(finding.Id, finding.Title,
                    $"Still happening, {(int)since.TotalHours}h after it started.\n\n{finding.Detail}",
                    IsRecovery: false));
            }
        }

        foreach (var id in _firing.Keys.Where(id => !live.ContainsKey(id)).ToList())
        {
            _firing.Remove(id);
            actions.Add(new AlertAction(id, "Recovered", $"\"{id}\" is back to normal.", IsRecovery: true));
        }

        return actions;
    }

    /// <summary>What is currently tripped — for a status read, and for a log line each cycle so the trail exists
    /// whether or not an email ever went anywhere.</summary>
    public IReadOnlyCollection<string> Firing => _firing.Keys;
}
