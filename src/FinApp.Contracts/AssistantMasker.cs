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

        // Every written form of every name, longest first so "car fund" wins over "car" — the alternative leaves
        // half a name in the sentence.
        // ★ A name is matched under its singular as well as as written. People type "grocery bill", not
        // "Groceries bill", and before this the mismatch was invisible in the worst way: the category simply did
        // not register, the question fell back to an account-wide answer, and that answer was returned as though
        // it were the one asked for.
        var vocab = new List<(string Form, AssistantSlot Slot)>();
        foreach (var v in vocabulary)
        {
            if (string.IsNullOrWhiteSpace(v.Name)) continue;
            var slot = v with { Name = v.Name.Trim() };
            if (slot.Name.Length < MinNameLength) continue;
            vocab.Add((slot.Name, slot));
            if (Singular(slot.Name) is { } singular) vocab.Add((singular, slot));
        }
        vocab = vocab.OrderByDescending(v => v.Form.Length).ToList();

        var slots = new List<AssistantSlot>();
        var sb = new StringBuilder();
        var i = 0;

        while (i < question.Length)
        {
            if (MatchAt(question, i, vocab) is { } hit)
            {
                // ⚠️ Advance by the LENGTH MATCHED, not by the name's length — they differ whenever a singular
                // form matched, and using the name would leave a character behind or eat one too many.
                var index = slots.FindIndex(s => s.Id == hit.Slot.Id && s.Kind == hit.Slot.Kind);
                if (index < 0) { slots.Add(hit.Slot); index = slots.Count - 1; }
                sb.Append('{').Append(index + 1).Append('}');
                i += hit.Length;
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
    /// "Food" does not match inside "Foodie". Returns the entity and how many characters it consumed.</summary>
    private static (AssistantSlot Slot, int Length)? MatchAt(
        string question, int i, List<(string Form, AssistantSlot Slot)> vocab)
    {
        if (i > 0 && char.IsLetterOrDigit(question[i - 1])) return null;

        foreach (var (form, slot) in vocab)
        {
            var n = form.Length;
            if (i + n > question.Length) continue;
            if (string.Compare(question, i, form, 0, n, StringComparison.CurrentCultureIgnoreCase) != 0) continue;
            if (i + n < question.Length && char.IsLetterOrDigit(question[i + n])) continue;
            return (slot, n);
        }
        return null;
    }

    /// <summary>
    /// The singular of a name, or null when there isn't a safe one.
    /// <para>⚠️ Deliberately timid. It only strips endings English is reliable about, and it refuses to produce a
    /// stem shorter than four characters — a category called "Gas" must never start matching "Ga", because a
    /// wrong match here does not fail loudly, it masks the wrong span and changes the question.</para>
    /// </summary>
    private static string? Singular(string name)
    {
        // Three, not four: "Boxes" → "Box" is a real singular and a four-character floor threw it away. Three
        // still rejects the cases that matter — "Gas" and "Bus" stem to two characters and are refused.
        const int minStem = 3;
        string? stem =
            name.EndsWith("ies", StringComparison.CurrentCultureIgnoreCase) ? name[..^3] + "y" :
            name.EndsWith("ches", StringComparison.CurrentCultureIgnoreCase) ||
            name.EndsWith("shes", StringComparison.CurrentCultureIgnoreCase) ||
            name.EndsWith("xes", StringComparison.CurrentCultureIgnoreCase) ||
            name.EndsWith("ses", StringComparison.CurrentCultureIgnoreCase) ? name[..^2] :
            name.EndsWith("ss", StringComparison.CurrentCultureIgnoreCase) ? null :
            name.EndsWith("s", StringComparison.CurrentCultureIgnoreCase) ? name[..^1] :
            null;

        return stem is { Length: >= minStem } && !stem.Equals(name, StringComparison.CurrentCultureIgnoreCase)
            ? stem
            : null;
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
