using FinApp.AssistantProbe;
using FinApp.Contracts;

// ─────────────────────────────────────────────────────────────────────────────────────────────────────────────
// The assistant probe — what the log cannot tell you.
//
// ⚠️ Why this exists. Production reported 15 of 21 model answers coming back `unknown`, and then the obvious next
// step turned out to be impossible: NOTHING logs a question. An `unknown` writes only its intent; the one line
// carrying any shape at all (`{Length} chars, {Slots} slots`) is on the exception path, and an `unknown` did not
// throw — it parsed cleanly and returned a key the catalogue does not handle. The privacy design that makes the
// feature defensible is the same property that makes its failure mode unobservable.
//
// So this drives the two pieces that decide everything BEFORE the model is reached — AssistantMasker and
// AssistantLocalMatcher, the real production types, not copies — over a corpus of natural phrasings, and prints
// where each one lands. What falls through here is exactly what would have cost a model call in production.
//
// ★ It needs no API key, no account, no server and no browser. That is the point: the expensive half of the
// question ("which phrasings miss?") is answerable for nothing, and only the residue needs a real model.
//
//   dotnet run --project tools/FinApp.AssistantProbe                 # the built-in corpus
//   dotnet run --project tools/FinApp.AssistantProbe -- questions.txt  # one question per line, # for comments
//   dotnet run --project tools/FinApp.AssistantProbe -- --misses      # only what falls through
// ─────────────────────────────────────────────────────────────────────────────────────────────────────────────

var onlyMisses = args.Contains("--misses");
var file = args.FirstOrDefault(a => !a.StartsWith("--"));

// A vocabulary shaped like a real bilingual account, because masking is what turns a question into the thing the
// matcher actually sees. The Bulgarian names are the ones the cross-language work in 5f0b337 was aimed at.
var vocabulary = new List<AssistantSlot>
{
    new(AssistantSlotKinds.Category, Guid.NewGuid(), "Groceries"),
    new(AssistantSlotKinds.Category, Guid.NewGuid(), "Eating out"),
    new(AssistantSlotKinds.Category, Guid.NewGuid(), "Transport"),
    new(AssistantSlotKinds.Category, Guid.NewGuid(), "Gas"),
    new(AssistantSlotKinds.Category, Guid.NewGuid(), "Храна"),
    new(AssistantSlotKinds.Category, Guid.NewGuid(), "Деца"),
    new(AssistantSlotKinds.Goal,     Guid.NewGuid(), "Car fund"),
    new(AssistantSlotKinds.Goal,     Guid.NewGuid(), "Emergency fund"),
    new(AssistantSlotKinds.Goal,     Guid.NewGuid(), "Mortgage"),
    new(AssistantSlotKinds.Wallet,   Guid.NewGuid(), "Main account"),
    new(AssistantSlotKinds.Wallet,   Guid.NewGuid(), "Cash"),
    new(AssistantSlotKinds.Trip,     Guid.NewGuid(), "Greece"),
};

var corpus = file is not null
    ? File.ReadAllLines(file).Where(l => l.Trim().Length > 0 && !l.TrimStart().StartsWith('#')).ToList()
    : Corpus.Default;

int local = 0, fell = 0, refused = 0;
var missed = new List<string>();

Console.OutputEncoding = System.Text.Encoding.UTF8;
Console.WriteLine($"{corpus.Count} questions · vocabulary of {vocabulary.Count} names\n");

foreach (var question in corpus)
{
    var masked = AssistantMasker.Mask(question, vocabulary);
    var reply = AssistantLocalMatcher.Match(masked.Text, masked.Slots.Select(s => s.Kind).ToList());

    // Strict mode is always on in the client now, so an unmasked suspect word is not a warning — it is a refusal,
    // and the question never reaches the model at all. Counted separately: it is a third outcome, not a miss.
    var strictRefuses = reply is null && !masked.IsClean;

    if (reply is not null) local++;
    else if (strictRefuses) refused++;
    else { fell++; missed.Add(question); }

    if (onlyMisses && reply is not null) continue;

    var verdict = reply is not null
        ? $"LOCAL   {reply.Intent}/{reply.Target}{(reply.Slot is { } s ? $" slot {s}" : "")}"
        : strictRefuses ? "REFUSED strict mode — never sent"
        : "MODEL   → this is the one that costs $0.0016";

    Console.WriteLine($"  {verdict}");
    Console.WriteLine($"          \"{question}\"");
    if (masked.Text != question) Console.WriteLine($"          masked: {masked.Text}");
    if (masked.Slots.Count > 0)
        Console.WriteLine($"          slots:  {string.Join(", ", masked.Slots.Select((sl, i) => $"{{{i + 1}}}={sl.Kind}:{sl.Name}"))}");
    if (!masked.IsClean) Console.WriteLine($"          suspect: {string.Join(", ", masked.Suspect)}");
    Console.WriteLine();
}

Console.WriteLine(new string('─', 100));
Console.WriteLine($"  answered on the device : {local,3}   ({Pct(local)})   free, offline, never sent");
Console.WriteLine($"  fell through to model  : {fell,3}   ({Pct(fell)})   each one a paid call");
Console.WriteLine($"  refused by strict mode : {refused,3}   ({Pct(refused)})   an unmaskable word in the text");

if (missed.Count > 0 && !onlyMisses)
{
    Console.WriteLine($"\n  What the rule tables do not reach — the candidate list, in the order printed above:");
    foreach (var m in missed) Console.WriteLine($"    · {m}");
}

Console.WriteLine($"""

  ⚠️ Read this as "would the matcher have to pay for it", not "is the answer right". A fall-through is not a bug:
     the model is the designed fallback and an unusual phrasing SHOULD reach it. What matters is a fall-through
     whose meaning is plainly one of the {AssistantCatalogue.Targets.Count + AssistantCatalogue.Explainers.Count + AssistantCatalogue.Topics.Count} catalogue keys — that one is a missing rule, and it is free to fix.
  ⚠️ And the trap before you add one: the question CLASS decides which rule table runs, so a phrase added only to
     the rules is unreachable. "when will my loans be paid off" sat in the navigate class and never reached the
     report rules it had just been added to.
""");

string Pct(int n) => corpus.Count == 0 ? "—" : $"{100.0 * n / corpus.Count,4:0.0}%";
