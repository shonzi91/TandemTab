using System.Text;

namespace FinApp.Contracts;

/// <summary>One placeholder: what it stood for, and what to open if the answer points at it.</summary>
public sealed record AssistantSlot(string Kind, Guid Id, string Name);

/// <summary>
/// A question with the user's own vocabulary taken out of it.
/// </summary>
/// <param name="Text">What will actually be sent — <c>"how is my {1} doing"</c>.</param>
/// <param name="Slots">What each placeholder stood for, in order. Never sent; used to resolve the answer.</param>
/// <param name="Suspect">Tokens that look like names the app does not know (a shop, a person). ⚠️ Best-effort —
/// see the note on <see cref="AssistantMasker"/>.</param>
public sealed record MaskedQuestion(string Text, IReadOnlyList<AssistantSlot> Slots, IReadOnlyList<string> Suspect)
{
    public bool IsClean => Suspect.Count == 0;

    /// <summary>The wire request for this question. ★ The only supported way to build one — a request assembled
    /// by hand from raw user text is the single mistake that would undo the whole design.</summary>
    public AssistantAskRequest ToRequest() => new(Text, Slots.Select(s => s.Kind).ToList());
}

/// <summary>
/// Takes a question and a vocabulary, and gives back a question with every known name replaced by a numbered
/// placeholder and every digit replaced by <c>#</c>.
/// <para>
/// ★ <b>It lives in Contracts, with no dependency on a client, for two reasons:</b> it defines how the wire
/// request is built, and it is the piece a second client (the phone, later) must reproduce exactly rather than
/// re-invent. It is a pure function, which is also what makes the guarantee testable rather than assertable.
/// </para>
/// <para>
/// <b>Guaranteed, deterministically:</b> no name in the supplied vocabulary survives, and no digit does.
/// </para>
/// <para>
/// ⚠️ <b>Not guaranteed, said plainly because a fig-leaf here would be worse than no claim:</b> a word the app has
/// never seen cannot be recognised, so it travels. <see cref="MaskedQuestion.Suspect"/> catches the strongest
/// available signal — a quoted token, or a capitalised word that is not opening the sentence — and strict mode
/// refuses to send those, but a lower-case shop name will pass. The honest promise is "everything we can name is
/// removed, every number is removed, and you see the rest before it goes".
/// </para>
/// </summary>
public static class AssistantMasker
{
    /// <summary>A name shorter than this is not matched. ★ Not a tuning knob: a wallet called "AB" would otherwise
    /// mask the middle of every word containing it, and the model would answer the resulting nonsense confidently.</summary>
    public const int MinNameLength = 3;

    public static MaskedQuestion Mask(string question, IReadOnlyList<AssistantSlot> vocabulary)
    {
        if (string.IsNullOrWhiteSpace(question)) return new MaskedQuestion("", [], []);

        // Longest first, so "car fund" wins over "car" — the alternative leaves half a name in the sentence.
        var vocab = vocabulary
            .Where(v => !string.IsNullOrWhiteSpace(v.Name) && v.Name.Trim().Length >= MinNameLength)
            .Select(v => v with { Name = v.Name.Trim() })
            .OrderByDescending(v => v.Name.Length)
            .ToList();

        var slots = new List<AssistantSlot>();
        var sb = new StringBuilder();
        var i = 0;

        while (i < question.Length)
        {
            if (MatchAt(question, i, vocab) is { } hit)
            {
                var index = slots.FindIndex(s => s.Id == hit.Id && s.Kind == hit.Kind);
                if (index < 0) { slots.Add(hit); index = slots.Count - 1; }
                sb.Append('{').Append(index + 1).Append('}');
                i += hit.Name.Length;
                continue;
            }
            // Figures never travel. The model picks a topic and the app owns every number the user reads, so a
            // digit in the question is at best noise and at worst an amount somebody typed.
            sb.Append(char.IsDigit(question[i]) ? '#' : question[i]);
            i++;
        }

        var text = Collapse(sb.ToString());
        return new MaskedQuestion(text, slots, Suspects(text));
    }

    /// <summary>A vocabulary hit starting exactly at <paramref name="i"/>, on word boundaries at both ends so
    /// "Food" does not match inside "Foodie".</summary>
    private static AssistantSlot? MatchAt(string question, int i, List<AssistantSlot> vocab)
    {
        if (i > 0 && char.IsLetterOrDigit(question[i - 1])) return null;

        foreach (var entry in vocab)
        {
            var n = entry.Name.Length;
            if (i + n > question.Length) continue;
            if (string.Compare(question, i, entry.Name, 0, n, StringComparison.CurrentCultureIgnoreCase) != 0) continue;
            if (i + n < question.Length && char.IsLetterOrDigit(question[i + n])) continue;
            return entry;
        }
        return null;
    }

    private static List<string> Suspects(string masked)
    {
        var found = new List<string>();
        var words = masked.Split([' ', '\t', '\n', ',', '.', ';', ':', '!', '?'], StringSplitOptions.RemoveEmptyEntries);
        for (var w = 0; w < words.Length; w++)
        {
            var word = words[w].Trim('"', '\'', '(', ')');
            if (word.Length < 2 || word.StartsWith('{')) continue;
            var quoted = words[w].StartsWith('"') || words[w].StartsWith('\'');
            if ((quoted || (w > 0 && char.IsUpper(word[0]))) && !found.Contains(word))
                found.Add(word);
        }
        return found;
    }

    /// <summary>Masking leaves double spaces where a two-word name stood; a tidy question is a cheaper one.</summary>
    private static string Collapse(string s)
    {
        var sb = new StringBuilder(s.Length);
        var space = false;
        foreach (var c in s)
        {
            if (char.IsWhiteSpace(c)) { space = true; continue; }
            if (space && sb.Length > 0) sb.Append(' ');
            space = false;
            sb.Append(c);
        }
        return sb.ToString();
    }
}
