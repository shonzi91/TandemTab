using FinApp.Domain.Common;

namespace FinApp.Domain.Services;

/// <summary>
/// How an <see cref="InsightArg"/> should be rendered by a client, independent of language:
/// <list type="bullet">
/// <item><see cref="Text"/> — a verbatim string (a category name, a month label); never translated.</item>
/// <item><see cref="Money"/> — a currency amount; the client formats <see cref="InsightArg.Number"/> with its money formatter.</item>
/// <item><see cref="Percent"/> — a ratio in 0..1; the client renders it as a whole-percent (e.g. 0.2 → "20%").</item>
/// <item><see cref="Int"/> — an already-rounded integer count/points value; rendered verbatim as a whole number.</item>
/// </list>
/// </summary>
public enum InsightArgKind { Text, Money, Percent, Int }

/// <summary>
/// One fill value for an <see cref="InsightMessage"/> template placeholder. The domain supplies the raw value and a
/// <see cref="Kind"/>; each client formats it in its own locale. <see cref="Number"/> carries money/percent/int values;
/// <see cref="Text"/> carries verbatim strings (and is null otherwise).
/// </summary>
public sealed record InsightArg(InsightArgKind Kind, decimal Number = 0m, string? Text = null)
{
    /// <summary>A verbatim string (category name, month label) — never translated.</summary>
    public static InsightArg Str(string? text) => new(InsightArgKind.Text, 0m, text ?? "");
    /// <summary>A currency amount (rendered by the client's money formatter).</summary>
    public static InsightArg Cash(Money money) => new(InsightArgKind.Money, money.Amount);
    /// <summary>A ratio in 0..1 rendered as a whole percent (matches the old <c>Pct()</c> helper).</summary>
    public static InsightArg Ratio(decimal ratio) => new(InsightArgKind.Percent, ratio);
    /// <summary>An already-rounded integer (count, days, points) rendered verbatim as a whole number.</summary>
    public static InsightArg Count(decimal value) => new(InsightArgKind.Int, value);
}

/// <summary>
/// A language-independent narrative fragment: a stable <see cref="Code"/> naming which template to use and the
/// <see cref="Args"/> that fill it. Clients own the per-language templates (keyed by <see cref="Code"/>) and format the
/// args locally, so the same report renders correctly in any UI language — no English is baked into the domain.
/// See <see cref="InsightCodes"/> for the code catalogue.
/// </summary>
public sealed record InsightMessage(string Code, IReadOnlyList<InsightArg> Args)
{
    public static InsightMessage Of(string code, params InsightArg[] args) => new(code, args);
    /// <summary>A template with no placeholders (a fixed phrase like a signal title).</summary>
    public static InsightMessage Plain(string code) => new(code, []);
}
