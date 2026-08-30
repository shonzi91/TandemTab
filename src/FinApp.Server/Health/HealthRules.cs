namespace FinApp.Server.Health;

/// <summary>Something worth telling the owner about. <see cref="Id"/> is stable so the alert state machine can
/// tell "still broken" from "broken again".</summary>
public sealed record HealthFinding(string Id, string Title, string Detail);

/// <summary>The numbers a rule turns on, all configurable so a quiet beta and a busy launch can want different
/// ones without a deploy.</summary>
public sealed record HealthThresholds(
    int SignInStartsBeforeAlarm = 3,
    int ClientErrorsBeforeAlarm = 10,
    int AssistantCallsBeforeAlarm = 5,
    double AssistantFailureRate = 0.9);

/// <summary>
/// The rules, as a pure function of the counts — which is what makes them testable without a clock, a mailbox or
/// a host.
/// </summary>
public static class HealthRules
{
    public const string SignInRoundTrip = "signin-roundtrip";
    public const string ClientErrors = "client-errors";
    public const string AssistantFailing = "assistant-failing";

    public static IReadOnlyList<HealthFinding> Evaluate(HealthCounts c, HealthThresholds t, TimeSpan window)
    {
        var findings = new List<HealthFinding>();
        var mins = (int)window.TotalMinutes;

        // ⭐ THE ONE THAT WOULD HAVE CAUGHT THE OUTAGE. People are being sent to a provider and none of them are
        // coming back. Nothing errors in this state — the redirect works, the consent screen works, and the return
        // leg vanishes — so no error-counting alarm can see it. A ratio can.
        //
        // ⚠️ It can false-positive on abandonment: somebody who opens the consent screen and closes it is a start
        // with no completion. That is what the threshold is for — three people abandoning and nobody at all
        // succeeding, inside one window, is worth a look even when it turns out to be innocent.
        if (c.SignInStarted >= t.SignInStartsBeforeAlarm && c.SignInCompleted == 0)
            findings.Add(new HealthFinding(SignInRoundTrip,
                "Sign-in is starting but never finishing",
                $"{c.SignInStarted} sign-ins were started with an external provider in the last {mins} minutes and " +
                $"none completed. That is the shape of the August 26–28 outage: the redirect out works, the callback " +
                $"never arrives, and nothing anywhere reports an error. Check that the callback route is reaching " +
                $"the server at all before looking at the provider."));

        // B1 collects crash reports and has always required somebody to go and look. This is the "and looked at"
        // half that was never built.
        if (c.ClientErrors >= t.ClientErrorsBeforeAlarm)
            findings.Add(new HealthFinding(ClientErrors,
                "Browsers are reporting crashes",
                $"{c.ClientErrors} client errors arrived in the last {mins} minutes. " +
                $"gcloud logging read 'textPayload:\"FinApp.ClientError\"' --limit 50 --freshness=1d"));

        // The assistant is the app's only external paid dependency, and its failure mode is quiet by design: a
        // failed parse degrades to "unknown", which is indistinguishable from a question nobody could answer.
        if (c.AssistantCalls >= t.AssistantCallsBeforeAlarm &&
            c.AssistantFailed >= c.AssistantCalls * t.AssistantFailureRate)
            findings.Add(new HealthFinding(AssistantFailing,
                "The assistant's model calls are failing",
                $"{c.AssistantFailed} of {c.AssistantCalls} calls failed in the last {mins} minutes. Users see " +
                $"\"I didn't follow that\" for every question, which looks like a model that understands nothing. " +
                $"Check the API key, the model id, and whether the request settings still match the model."));

        return findings;
    }
}
