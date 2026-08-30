using FinApp.Server.Auth;

namespace FinApp.Server.Health;

/// <summary>
/// The loop: read the counters, run the rules, and <b>send</b> what changed. Thin on purpose — the rules and the
/// say-it-once judgement live in their own testable types, and what is left here is a timer, a mailbox and a log.
/// <para>
/// ⚠️ <b>Why this is in the app rather than a Cloud Monitoring policy.</b> R4 moves hosting off Google Cloud, so
/// alerting built out of Cloud Run's log metrics is work with a known expiry date. This travels: it needs a clock,
/// a counter and an email sender, all of which exist on any host.
/// </para>
/// <para>
/// ⚠️ It is also <b>per instance</b>. Two instances each seeing half the traffic each apply the thresholds to
/// their own half, so a marginal condition can be missed and a real one can be reported twice. For an app at this
/// size that trade is right — the alternative is a shared store on the sign-in hot path — but it is a reason not to
/// tune the thresholds tight.
/// </para>
/// </summary>
public sealed class HealthWatchdog(
    HealthSignals signals,
    IServiceScopeFactory scopes,
    IConfiguration config,
    ILogger<HealthWatchdog> log) : BackgroundService
{
    private readonly HealthAlerts _alerts = new();

    private bool Enabled => config.GetValue("Alerts:Enabled", true);
    private TimeSpan Window => TimeSpan.FromMinutes(config.GetValue("Alerts:WindowMinutes", 60));
    private TimeSpan Every => TimeSpan.FromMinutes(config.GetValue("Alerts:CheckMinutes", 10));
    private TimeSpan RepeatAfter => TimeSpan.FromHours(config.GetValue("Alerts:RepeatHours", 6));

    /// <summary>Where alerts go. <c>Alerts:Email</c>, else the first address on the admin allowlist — the person
    /// who can already see the metrics is the person who should be told they stopped being fine.</summary>
    private string? Recipient =>
        config["Alerts:Email"]
        ?? (config["Admin:Emails"] ?? "")
            .Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault();

    private HealthThresholds Thresholds => new(
        config.GetValue("Alerts:SignInStartsBeforeAlarm", 3),
        config.GetValue("Alerts:ClientErrorsBeforeAlarm", 10),
        config.GetValue("Alerts:AssistantCallsBeforeAlarm", 5),
        config.GetValue("Alerts:AssistantFailureRate", 0.9));

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (!Enabled)
        {
            log.LogInformation("Health watchdog: disabled by configuration.");
            return;
        }

        // ★ One window of warm-up before the first evaluation. A boot mid-flow can leave a start with no matching
        // completion through no fault of anything — and an alert that cries wolf on every deploy is an alert that
        // gets muted, which is worse than not having built it.
        try { await Task.Delay(Window, ct); } catch (OperationCanceledException) { return; }

        while (!ct.IsCancellationRequested)
        {
            try { await CheckAsync(ct); }
            catch (Exception ex) { log.LogError(ex, "Health watchdog: a check threw. The loop continues."); }

            try { await Task.Delay(Every, ct); } catch (OperationCanceledException) { return; }
        }
    }

    private async Task CheckAsync(CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var counts = signals.Snapshot(Window, now);
        var findings = HealthRules.Evaluate(counts, Thresholds, Window);
        var actions = _alerts.Reconcile(findings, now, RepeatAfter);

        // ★ Logged every cycle, not only when something is wrong. A monitor that is silent when healthy is
        // indistinguishable from a monitor that has stopped running — which is the exact failure this feature is
        // about, reproduced one level up.
        log.LogInformation(
            "Health watchdog: sign-ins {Started}→{Completed}, client errors {Errors}, assistant {Failed}/{Calls} failed. Firing: {Firing}.",
            counts.SignInStarted, counts.SignInCompleted, counts.ClientErrors,
            counts.AssistantFailed, counts.AssistantCalls,
            _alerts.Firing.Count == 0 ? "nothing" : string.Join(", ", _alerts.Firing));

        foreach (var action in actions) await SendAsync(action, ct);
    }

    private async Task SendAsync(AlertAction action, CancellationToken ct)
    {
        // Always logged, whatever happens to the email. A delivery failure must not be the reason nobody hears.
        if (action.IsRecovery) log.LogWarning("Health alert RECOVERED [{Id}].", action.Id);
        else log.LogError("Health alert [{Id}] {Title}: {Detail}", action.Id, action.Title, action.Detail);

        var to = Recipient;
        if (string.IsNullOrWhiteSpace(to))
        {
            log.LogWarning("Health watchdog: nowhere to send [{Id}] — set Alerts:Email or Admin:Emails.", action.Id);
            return;
        }

        using var scope = scopes.CreateScope();
        var email = scope.ServiceProvider.GetRequiredService<IEmailSender>();
        if (!email.IsConfigured)
        {
            log.LogWarning("Health watchdog: no mail transport configured; [{Id}] was logged only.", action.Id);
            return;
        }

        var subject = action.IsRecovery
            ? $"TandemTab recovered: {action.Id}"
            : $"TandemTab alert: {action.Title}";
        var text = $"{action.Detail}\n\n— TandemTab health watchdog";
        var html = $"<p>{System.Net.WebUtility.HtmlEncode(action.Detail).Replace("\n", "<br>")}</p>" +
                   "<p style=\"color:#6b7280\">— TandemTab health watchdog</p>";

        try { await email.SendAsync(to, subject, html, text, ct); }
        catch (Exception ex) { log.LogError(ex, "Health watchdog: sending [{Id}] failed.", action.Id); }
    }
}
