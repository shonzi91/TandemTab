using FinApp.Server.Health;

namespace FinApp.Server.Tests;

/// <summary>
/// The rules and the say-it-once judgement, tested without a clock, a mailbox or a host — which is the whole
/// reason they are separate types from the loop that drives them.
/// </summary>
public class HealthWatchdogTests
{
    private static readonly HealthThresholds Default = new();
    private static readonly TimeSpan Hour = TimeSpan.FromHours(1);

    private static IReadOnlyList<HealthFinding> Evaluate(HealthCounts counts) =>
        HealthRules.Evaluate(counts, Default, Hour);

    private static HealthCounts Counts(int started = 0, int completed = 0, int errors = 0, int aiFail = 0, int aiOk = 0) =>
        new(started, completed, errors, aiFail, aiOk);

    // ── The rule that would have caught the outage ─────────────────────────────────────────────────────────────

    [Fact]
    public void Sign_ins_that_start_and_never_finish_are_reported()
    {
        // ⭐ The August 26–28 signature exactly: redirects out to the provider, zero callbacks back, and not one
        // error logged anywhere. No error-counting alarm can see this state; a ratio can.
        var finding = Assert.Single(Evaluate(Counts(started: 5, completed: 0)));

        Assert.Equal(HealthRules.SignInRoundTrip, finding.Id);
    }

    [Fact]
    public void One_completed_sign_in_is_enough_to_stay_quiet()
    {
        // The fault being watched for is total: the callback route either reaches the server or it does not. A
        // single success proves it does, and whatever else is going on is not this.
        Assert.Empty(Evaluate(Counts(started: 20, completed: 1)));
    }

    [Fact]
    public void Nobody_signing_in_is_not_a_fault()
    {
        // ★ The reason this rule is a ratio and not "sign-ins fell to zero". On a beta with thirty users, a quiet
        // night is normal — an absolute alarm would fire nightly and be muted within a week.
        Assert.Empty(Evaluate(Counts(started: 0, completed: 0)));
    }

    [Fact]
    public void One_or_two_abandoned_sign_ins_do_not_raise_the_alarm()
    {
        // Somebody opening the consent screen and closing it is a start with no completion, and is not news.
        Assert.Empty(Evaluate(Counts(started: 2, completed: 0)));
    }

    // ── The other two rules ───────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_burst_of_client_errors_is_reported()
    {
        var finding = Assert.Single(Evaluate(Counts(errors: 10)));
        Assert.Equal(HealthRules.ClientErrors, finding.Id);
    }

    [Fact]
    public void A_trickle_of_client_errors_is_not()
    {
        Assert.Empty(Evaluate(Counts(errors: 9)));
    }

    [Fact]
    public void An_assistant_that_fails_every_call_is_reported()
    {
        var finding = Assert.Single(Evaluate(Counts(aiFail: 6, aiOk: 0)));
        Assert.Equal(HealthRules.AssistantFailing, finding.Id);
    }

    [Fact]
    public void An_assistant_that_mostly_works_is_not()
    {
        Assert.Empty(Evaluate(Counts(aiFail: 2, aiOk: 8)));
    }

    [Fact]
    public void Too_few_assistant_calls_to_judge_means_no_judgement()
    {
        // Two failures out of two is 100% and means nothing. A rule that fires on a sample of two is a rule that
        // fires on noise.
        Assert.Empty(Evaluate(Counts(aiFail: 2, aiOk: 0)));
    }

    [Fact]
    public void Several_things_can_be_wrong_at_once()
    {
        var findings = Evaluate(Counts(started: 5, completed: 0, errors: 40, aiFail: 10));

        Assert.Equal(3, findings.Count);
        Assert.Equal(
            [HealthRules.SignInRoundTrip, HealthRules.ClientErrors, HealthRules.AssistantFailing],
            findings.Select(f => f.Id).ToArray());
    }

    // ── Saying it once, then again, then saying it stopped ────────────────────────────────────────────────────

    [Fact]
    public void A_fault_is_announced_once_not_every_cycle()
    {
        // An alert every ten minutes trains its reader to filter it, and a filtered alert is no alert.
        var alerts = new HealthAlerts();
        var findings = Evaluate(Counts(started: 5));
        var now = DateTimeOffset.UtcNow;

        Assert.Single(alerts.Reconcile(findings, now, TimeSpan.FromHours(6)));
        Assert.Empty(alerts.Reconcile(findings, now.AddMinutes(10), TimeSpan.FromHours(6)));
        Assert.Empty(alerts.Reconcile(findings, now.AddMinutes(20), TimeSpan.FromHours(6)));
    }

    [Fact]
    public void A_fault_that_is_still_there_hours_later_says_so_again()
    {
        // Silence after the first message is indistinguishable from "somebody fixed it".
        var alerts = new HealthAlerts();
        var findings = Evaluate(Counts(started: 5));
        var now = DateTimeOffset.UtcNow;

        alerts.Reconcile(findings, now, TimeSpan.FromHours(6));
        var repeat = Assert.Single(alerts.Reconcile(findings, now.AddHours(7), TimeSpan.FromHours(6)));

        Assert.False(repeat.IsRecovery);
        Assert.Contains("Still happening", repeat.Detail);
    }

    [Fact]
    public void Recovery_is_announced_too()
    {
        // Without this the reader has to go and check, which is the pull-not-push habit the whole feature ends.
        var alerts = new HealthAlerts();
        var now = DateTimeOffset.UtcNow;
        alerts.Reconcile(Evaluate(Counts(started: 5)), now, TimeSpan.FromHours(6));

        var recovery = Assert.Single(alerts.Reconcile([], now.AddMinutes(10), TimeSpan.FromHours(6)));

        Assert.True(recovery.IsRecovery);
        Assert.Equal(HealthRules.SignInRoundTrip, recovery.Id);
        Assert.Empty(alerts.Firing);
    }

    [Fact]
    public void A_fault_that_comes_back_is_announced_again()
    {
        var alerts = new HealthAlerts();
        var findings = Evaluate(Counts(started: 5));
        var now = DateTimeOffset.UtcNow;

        alerts.Reconcile(findings, now, TimeSpan.FromHours(6));
        alerts.Reconcile([], now.AddMinutes(10), TimeSpan.FromHours(6));          // recovered
        var again = Assert.Single(alerts.Reconcile(findings, now.AddMinutes(20), TimeSpan.FromHours(6)));

        Assert.False(again.IsRecovery);
        Assert.DoesNotContain("Still happening", again.Detail);   // it is new again, not ongoing
    }

    [Fact]
    public void Healthy_is_silent()
    {
        Assert.Empty(new HealthAlerts().Reconcile(Evaluate(Counts()), DateTimeOffset.UtcNow, TimeSpan.FromHours(6)));
    }

    // ── The counters underneath ───────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Only_events_inside_the_window_are_counted()
    {
        var signals = new HealthSignals();
        var now = DateTimeOffset.UtcNow;

        signals.Record(HealthSignal.ExternalSignInStarted, now.AddMinutes(-90));   // before the window
        signals.Record(HealthSignal.ExternalSignInStarted, now.AddMinutes(-30));
        signals.Record(HealthSignal.ExternalSignInCompleted, now.AddMinutes(-5));

        var counts = signals.Snapshot(Hour, now);

        Assert.Equal(1, counts.SignInStarted);
        Assert.Equal(1, counts.SignInCompleted);
    }

    [Fact]
    public void Every_signal_lands_in_its_own_bucket()
    {
        var signals = new HealthSignals();
        var now = DateTimeOffset.UtcNow;

        signals.Record(HealthSignal.ClientError, now);
        signals.Record(HealthSignal.AssistantCallFailed, now);
        signals.Record(HealthSignal.AssistantCallFailed, now);
        signals.Record(HealthSignal.AssistantCallSucceeded, now);

        var counts = signals.Snapshot(Hour, now);

        Assert.Equal(1, counts.ClientErrors);
        Assert.Equal(2, counts.AssistantFailed);
        Assert.Equal(1, counts.AssistantSucceeded);
        Assert.Equal(3, counts.AssistantCalls);
    }

    [Fact]
    public void A_crash_storm_cannot_grow_the_tally_without_limit()
    {
        // A health monitor that becomes the memory leak would be a poor joke.
        var signals = new HealthSignals();
        var now = DateTimeOffset.UtcNow;
        for (var i = 0; i < 25_000; i++) signals.Record(HealthSignal.ClientError, now);

        Assert.True(signals.Snapshot(Hour, now).ClientErrors <= 20_000);
    }
}
