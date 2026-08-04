using System.Text.RegularExpressions;

namespace FinApp.Contracts;

/// <summary>
/// One client-side failure, reported so we find out about it. Deliberately tiny: an exception's <b>type, message
/// and stack</b> plus enough context to reproduce it — never the user's money.
/// </summary>
/// <param name="Kind">Where it came from: "render" (an unhandled Blazor render exception), "js" (a window error
/// or unhandled promise rejection), "api" (a 5xx from our own API), or "errorui" (the global error bar appeared).</param>
/// <param name="Message">The exception message, already scrubbed by <see cref="ErrorScrubber"/> client-side.</param>
/// <param name="Stack">Stack trace if there was one. Frames are code identifiers, so they carry no user data.</param>
/// <param name="Where">The app location — route, or the tab/modal being shown. Helps reproduce.</param>
/// <param name="AppVersion">Build identifier, so a report can be tied to a deployed revision.</param>
/// <param name="UserAgent">Browser/OS string, for "only happens on Safari" cases.</param>
public record ClientErrorReport(
    string Kind,
    string Message,
    string? Stack = null,
    string? Where = null,
    string? AppVersion = null,
    string? UserAgent = null);

/// <summary>
/// Strips anything that could be a user's real data out of an error message before it is logged.
/// <para>
/// <b>Why this exists at all.</b> The obvious assumption — "an exception message is developer text, it's safe" —
/// is false in this codebase. Domain guards deliberately quote real values back at the user: <i>"That fund only
/// holds €1,234.56…"</i>, <i>"A tag named “Mortgage” already exists."</i>. Shipping those verbatim to a log would
/// mean this app's error pipeline quietly became a channel for exactly the financial data the product promises
/// never to move. So messages are redacted to their <b>shape</b> — which is all a diagnostic needs.
/// </para>
/// <para>
/// <b>Applied on both sides on purpose.</b> The client scrubs so raw values never leave the device; the server
/// scrubs again so a stale client, a forged POST, or a future code path that forgets can't write raw values into
/// the logs. Defence in depth on a promise worth keeping.
/// </para>
/// </summary>
public static class ErrorScrubber
{
    /// <summary>Longest message we keep. Beyond this it's noise, and a huge body is an abuse vector.</summary>
    public const int MaxMessageLength = 800;

    /// <summary>Longest stack we keep.</summary>
    public const int MaxStackLength = 4000;

    // Order matters: emails before digit-runs (an address can contain digits), amounts before bare numbers.
    private static readonly Regex Email = new(@"[\w.+-]+@[\w-]+\.[\w.-]+", RegexOptions.Compiled);
    // A currency symbol next to a number, or any number written with 2 decimal places — i.e. money.
    // The trailing guard is (?!\d), NOT (?![\w.]): a figure at the end of a sentence is followed by a full stop,
    // and excluding "." there let "you paid 1,234.56." through untouched. The leading (?<![\w.]) stays, so a
    // version string like "v1.2.34" isn't mistaken for money.
    private static readonly Regex Money = new(@"[€$£лв]\s?-?[\d\s,.]*\d|(?<![\w.])-?\d[\d\s,]*[.,]\d{2}(?!\d)",
        RegexOptions.Compiled);
    // Names the domain quotes back at the user. Both curly (“x”) and straight ("x") — guards use both.
    private static readonly Regex Quoted = new("[“‘\"'][^”’\"']{1,80}[”’\"']", RegexOptions.Compiled);
    // Any remaining run of 5+ digits: IBANs, card fragments, account refs, external bank ids. Same trailing-guard
    // reasoning as Money — a card number ending a sentence is precisely the case that must not escape.
    private static readonly Regex LongDigits = new(@"(?<![\w.])\d[\d\s-]{4,}\d(?!\d)", RegexOptions.Compiled);

    /// <summary>Redact a message down to its shape. Null/blank in, blank out; never throws.</summary>
    public static string Message(string? message)
    {
        if (string.IsNullOrWhiteSpace(message)) return "";
        var text = message.Trim();
        text = Email.Replace(text, "«email»");
        text = Money.Replace(text, "«amount»");
        text = Quoted.Replace(text, "«name»");
        text = LongDigits.Replace(text, "«digits»");
        return Truncate(text, MaxMessageLength);
    }

    /// <summary>Trim a stack trace to a sane length. Frames are code identifiers, so nothing is redacted —
    /// but a message can be embedded in the first line of a .NET stack, so that line is scrubbed too.</summary>
    public static string? Stack(string? stack)
    {
        if (string.IsNullOrWhiteSpace(stack)) return null;
        var lines = stack.Split('\n');
        // A .NET ToString() starts "System.Foo: the message" — scrub any line that isn't an "at …" frame.
        for (var i = 0; i < lines.Length; i++)
            if (!lines[i].TrimStart().StartsWith("at ", StringComparison.Ordinal))
                lines[i] = Message(lines[i]);
        return Truncate(string.Join('\n', lines), MaxStackLength);
    }

    /// <summary>Scrub a whole report — the single call both the client and the endpoint use.</summary>
    public static ClientErrorReport Clean(ClientErrorReport report) => report with
    {
        Kind = Truncate((report.Kind ?? "").Trim(), 20),
        Message = Message(report.Message),
        Stack = Stack(report.Stack),
        // "Where" is an app location (route / tab / modal name), but a route can carry an id — scrub it the same way.
        Where = string.IsNullOrWhiteSpace(report.Where) ? null : Truncate(Message(report.Where), 200),
        AppVersion = string.IsNullOrWhiteSpace(report.AppVersion) ? null : Truncate(report.AppVersion.Trim(), 60),
        UserAgent = string.IsNullOrWhiteSpace(report.UserAgent) ? null : Truncate(report.UserAgent.Trim(), 300),
    };

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max] + "…";
}
