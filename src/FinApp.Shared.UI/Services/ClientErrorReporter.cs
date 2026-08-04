using FinApp.Contracts;
using Microsoft.Extensions.Logging;

namespace FinApp.Shared.UI.Services;

/// <summary>
/// Ships client-side failures to the server so we hear about them (OPEN-BETA B1). Without this, an exception in
/// the WASM client goes to that user's browser console and nowhere else — which is why BUG-1 (sign-out crashing
/// the app) sat in a Critical row of our own beta report for five days.
/// <para>
/// <b>Three rules, all of them "never make things worse":</b> it never throws (a reporter that breaks the app it
/// is reporting on is worse than no reporter), it never blocks a render (fire-and-forget), and it never sends the
/// user's money — every field goes through <see cref="ErrorScrubber"/> before it leaves the device.
/// </para>
/// </summary>
public sealed class ClientErrorReporter(FinAppApiClient api)
{
    /// <summary>Stop after this many reports in one session. A render loop can throw thousands of times a second;
    /// the first few tell us everything, the rest would just be a self-inflicted flood.</summary>
    private const int MaxPerSession = 20;

    private readonly HashSet<string> _seen = [];
    private int _sent;

    /// <summary>Where the user is, set by the shell so a report says which tab/modal was open. Scrubbed like
    /// everything else — a route can carry an id.</summary>
    public string? Where { get; set; }

    /// <summary>Report an exception. Safe to call from anywhere, including an exception handler.</summary>
    public void Report(string kind, Exception ex) =>
        Report(kind, $"{ex.GetType().Name}: {ex.Message}", ex.StackTrace);

    /// <summary>Report a failure we only have text for (a JS error, an API failure).</summary>
    public void Report(string kind, string message, string? stack = null)
    {
        try
        {
            if (_sent >= MaxPerSession) return;
            var clean = ErrorScrubber.Clean(new ClientErrorReport(kind, message, stack, Where, AppVersion, null));
            if (clean.Message.Length == 0) return;

            // De-dupe on kind+message: the same fault re-thrown on every re-render is one bug, not fifty reports.
            if (!_seen.Add($"{clean.Kind}|{clean.Message}")) return;
            _sent++;

            // Deliberately not awaited: reporting must never delay a render or a navigation, and the caller is
            // usually already handling a failure. Faults are swallowed — if the network is down we simply lose
            // the report, which is strictly better than throwing inside an error handler.
            _ = SendAsync(clean);
        }
        catch { /* a reporter that throws is worse than one that stays quiet */ }
    }

    private async Task SendAsync(ClientErrorReport report)
    {
        try { await api.ReportClientErrorAsync(report); }
        catch { /* offline, 429, or the server is the thing that's broken — nothing useful to do here */ }
    }

    /// <summary>Build identifier, so a report can be tied to a deployed revision. Taken from the assembly's
    /// informational version, which the build stamps.</summary>
    public static string AppVersion { get; } =
        typeof(ClientErrorReporter).Assembly.GetName().Version?.ToString() ?? "?";
}

/// <summary>
/// Forwards anything the framework logs at Error/Critical to <see cref="ClientErrorReporter"/>.
/// <para>
/// This is the highest-value hook in the whole feature and it is worth knowing why: an unhandled Blazor render
/// exception surfaces as a <c>Critical</c> log from <c>WebAssemblyRenderer</c> — that is the literal signature
/// BUG-1 produced ("Unhandled exception rendering component: Object reference not set…"). Catching this one
/// channel catches the entire class of bug that took the app down.
/// </para>
/// </summary>
public sealed class ClientErrorLoggerProvider(Func<ClientErrorReporter?> resolve) : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => new Forwarder(categoryName, resolve);

    public void Dispose() { }

    private sealed class Forwarder(string category, Func<ClientErrorReporter?> resolve) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Error;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            try
            {
                var reporter = resolve();
                if (reporter is null) return;   // logged before DI is up; nothing to report to yet
                var message = exception is not null
                    ? $"{exception.GetType().Name}: {exception.Message}"
                    : formatter(state, exception);
                reporter.Report("render", $"[{category}] {message}", exception?.StackTrace);
            }
            catch { /* never let logging break the app */ }
        }
    }
}
