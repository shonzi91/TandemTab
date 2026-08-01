# TandemTab — On-Device AI Assistant (Roadmap / Backlog)

*Idea-stage. A small, on-device (local) AI assistant that logs expenses, adjusts budgets, and answers a few canned insight questions — via text and later voice — gated behind explicit, granular privacy consent. Origin: user feedback, Session 80 brainstorm (2026-08-01). **Not started; parked for later.***

| | |
|---|---|
| **Application** | TandemTab (https://tandemtab.com) + native Android + planned MAUI (iOS/Android) |
| **Target surface** | **Mobile first** (iOS + Android), on-device. Web is a fallback, not the priority. |
| **Design stance** | Small & limited, in-app, small context. Constrained typed actions — *not* a free-form chatbot. |
| **Privacy stance** | On-device so financial data never leaves the phone; explicit, granular consent before anything runs. |
| **Effort key** | S = small · M = medium · L = large |
| **Date** | 1 August 2026 |

---

## Why this shape (the two settled decisions)

1. **Local = on-device on mobile.** Most useful on phones; keep the data on the device. The web app can fall back to a "not available here" state or an opt-in path later.
2. **Constrained, not conversational.** Free-text *input* that always resolves to a **typed action + confirm chip** — never a blank chat box that mutates money freely. This is both what small local models are good at and what keeps financial data safe.

**Headline framing:** the model is a *swappable parser at the edge*; the real product is the **action schema + name→entity resolver + confirm/undo chip**, sitting over `BudgetingState` methods that already exist. It's a translation problem, not an AI problem. The LLM is ~20% of the effort; the action/trust layer is ~80%.

---

## The action schema (v1 — 4 write intents + 1 read intent)

The model emits **names**, never Guids. A resolver turns names into entities; nothing mutates until a confirm chip is tapped.

```csharp
// Layer 1: what the on-device model emits (JSON, name-based, un-trusted)
abstract record AssistantIntent;
record LogExpense (decimal Amount, string Category, string? Wallet, string? Note, string? When) : AssistantIntent;
record LogIncome  (decimal Amount, string? Source,  string? Wallet, string? When)                : AssistantIntent;
record AdjustBudget(string Category, decimal Amount, BudgetOp Op)                                : AssistantIntent; // Set|Increase|Decrease
record MoveMoney  (decimal Amount, string FromWallet, string ToWallet, string? Note)             : AssistantIntent;
record AskInsight (InsightTopic Topic)                                                          : AssistantIntent; // read-only, canned
enum BudgetOp { Set, Increase, Decrease }
enum InsightTopic { SpentThisPeriod, BudgetStatus, TopCategory, SavingsRate, SafeToSpend }
```

### Each intent maps to an existing `BudgetingState` method (no new domain code)

| Intent | Resolver does | Calls (`BudgetingState`) |
|---|---|---|
| `LogExpense` | Category→categoryId, Wallet→fundId (default primary), When→date (default today) | `AddExpense(categoryId, amount, fundId, note, date)` (~1007) |
| `LogIncome` | Source→income categoryId, Wallet→fundId | `RecordDeposit(categoryId, fundId, amount, date)` (~1061) |
| `AdjustBudget` | read `BudgetFor(cat)` for current cap + keep its threshold/notify; compute new amount for Increase/Decrease | `SaveBudget(categoryId, newAmount, threshold, notify)` (~2192) |
| `MoveMoney` | two wallet-name→fundId resolves | `TransferFunds(fromFundId, toFundId, amount, note)` (~1712) |
| `AskInsight` | pure read over `TotalSpent`/`TotalBudgeted`/`BudgetFor`/`DisplayFreeToAllocate` | none — returns a sentence |

### The layer that actually needs building (resolver + confirm)

```csharp
interface IAssistant { Task<AssistantIntent?> Parse(string utterance); }   // one impl per platform behind DI
record PendingAction(string Summary, Func<Task> Apply, Func<Task>? Undo, IReadOnlyList<Ambiguity> NeedsPick);
```

1. **Name→entity resolution with disambiguation.** Fuzzy-match against `Account`'s categories/funds; if ambiguous/unknown, the chip shows a **picker** instead of guessing. Never invent an entity.
2. **Confirm-before-apply.** Every write renders as a chip — e.g. *"Log €12 · Food · Checking · today"* — with **Apply / Edit / Cancel**. Model output is a *proposal*, never a commit.
3. **Undo for free.** Each write has a matching remover already (`RemoveExpense`, `RemoveDeposit`, `RemoveBudget`, `RemoveFundTransfer`) → `PendingAction.Undo` is a one-liner.

**Deliberately out of v1** (keeps it simple, narrows the failure surface): bucket allocate/disburse, recurring, account-to-account transfers, category/fund management, bank confirmations.

---

## On-device model options (mobile)

**Tier A — use the OS's built-in model (best UX, newest devices only):**
- **iOS:** Apple **Foundation Models** framework (Apple Intelligence, iOS 26+) — on-device ~3B model with guided/structured output + tool-calling. No download, private, free. Devices: iPhone 15 Pro+ / newer.
- **Android:** **Gemini Nano** via AICore / ML Kit GenAI — on-device, structured output. Devices: Pixel 8+ / Galaxy S24+ / growing.

**Tier B — bundle your own small model (broader reach):**
- **ONNX Runtime GenAI** (Microsoft) running **Phi-3.5-mini** or **Gemma-2B** — binds cleanly in **.NET/MAUI** (pure C#, no Swift/Kotlin bridge). Cost: +1–2 GB app size or first-run download, slower, more battery/thermal.

**MAUI reality / the real tax:** native LLM APIs are per-platform. Either write `IAssistant` with a Swift-side + Kotlin-side impl (Tier A), or go all-.NET with ONNX (Tier B) and eat the size. Plus a graceful "AI not supported on this device" state. **Pragmatic call:** Tier A where available, Tier B or a polite fallback otherwise. The task is small (extract a few fields, pick one of 5 actions), so a 1–3B model — or even a deterministic parser for the common "12 eur lunch" case — is plenty.

---

## Constrained vs free-form — why constrained won (for the record)

| | Constrained (typed, small context) | Free-form chat |
|---|---|---|
| Reliability | ✅ output validated vs schema | ❌ small models hallucinate / mis-call |
| Fits on-device | ✅ short prompts | ❌ wants whole ledger in context |
| Privacy surface | ✅ only needed fields | ⚠️ tends to pull whole ledger |
| Battery/latency | ✅ fast | ❌ heavier |
| Safety on money | ✅ bounded actions + easy confirm/undo | ❌ arbitrary mutations |
| Testable | ✅ finite intents → eval accuracy | ❌ long tail |
| Feels magical | ❌ walls (fix in UI: suggestion chips) | ✅ flexible |
| Insights/"why" | ⚠️ only pre-built queries | ✅ open-ended |

**Sweet spot:** constrained spine, conversational skin — free-text input → typed action → confirm chip; a small fixed set of read-only insight intents; suggestion chips for discovery.

---

## Consent (reuses existing plumbing)

- Gate the feature on `RecordConsent("ai_assistant", accountId)` / `WithdrawConsent(...)` — already implemented in `BudgetingState` (~2004).
- Make it **granular**: a separate `"ai_mutations"` scope so a user can allow **read-only insights** without allowing **writes** (log/adjust/move).
- Consent copy states plainly: what runs where (on-device), what's stored, and that the assistant can propose changes to their money (never auto-apply).

⚠️ **Insights ≠ advice.** Frame insight answers as *observations over the user's own data* ("Food ran over 3 months straight"), never recommendations ("you should…") — stays clear of personalized financial advice.

---

## Phasing

- **Phase 0 — schema + confirm/undo UX.** Build `IAssistant` + `PendingAction` + the confirm chip; prototype the parse against a **cloud** model to validate the interaction (no on-device work yet).
- **Phase 1 — text expense logging.** `LogExpense`, parser-first with LLM fallback; resolver + disambiguation.
- **Phase 2 — read-only insights.** The 5 `InsightTopic` handlers.
- **Phase 3 — the other writes.** `LogIncome`, `AdjustBudget`, `MoveMoney`.
- **Phase 4 — on-device.** Swap parse onto Tier A (Apple FM / Gemini Nano), Tier B (ONNX) fallback.
- **Phase 5 — voice.** STT in (Whisper-class / OS), TTS out (OS voices). A whole second subsystem — weakest exactly on the tokens expense logging needs (numbers, currencies, merchant names), so last.

## MCP protocol — where it fits (and where it doesn't)

**For the on-device mobile assistant: not needed.** MCP (Model Context Protocol) is a standard client/server way to hand an LLM a menu of tools + data sources — great when the model and the tools live in **separate processes/machines** and you want interoperability. In v1 the model runs *inside the app* calling *our own* `BudgetingState` methods *in-process*; wrapping that in a protocol adds a transport + a running server for zero benefit. Use the plain in-process `IAssistant` + resolver instead. MCP here would be architectural cosplay.

**Where MCP *would* earn its place in TandemTab (separate directions, later):**
- **A) TandemTab as an MCP *server*** — expose `log_expense` / `budget_status` / `move_money` behind the user's auth so the user's *own* Claude/ChatGPT can act on their budget. Lower effort than building our own assistant, but it's **cloud, data leaves the phone** → cuts against the on-device privacy story. A different product; worth its own evaluation.
- **B) TandemTab as an MCP *client*** — if we later ingest external sources (bank feed, receipts inbox, prices API), consuming them as MCP servers saves bespoke integrations each time.

## Understanding, correction & "learning"

- **Misunderstanding is a *when*, not an *if* — the design absorbs it.** Parse errors (wrong amount/category) are caught by the **confirm chip** (user fixes €50→€15 before commit); resolution errors (which wallet? unknown category) are caught by **disambiguation** (picker, never guess). A misread becomes a one-tap correction, not a bad record — the core reason constrained+confirm beats free-form.
- **The model does *not* learn (and shouldn't).** On-device models are frozen; no on-phone fine-tuning. The *system* gets smarter cheaply and privately via a **per-user resolver memory**: remember that this user's "lunch"→Food, "the joint account"→a specific fund, "rent"→€1,200. Every confirm-chip correction is a **locally-stored** labeled example → pre-resolve it right next time. It's a growing local dictionary of *this user's* vocabulary (rides on the existing tags feature), not ML. Memory, not intelligence — the robust, private version.

## Multi-language

- **Understand freely, speak from templates.** Extraction is forgiving — the model mostly needs to find the amount + a noun, and the resolver matches against the user's *own* category names (e.g. "20 лв за обяд" → match "обяд" → Food). So **input can be any language** day one.
- **But generate insight/confirmation text from our own localized `Localizer` strings, not the model** — the app already has full Bulgarian. The assistant *understands* many languages but *speaks* in vetted, translated strings → no hallucinated grammar, no advice-drift, consistent with the app.
- **Tier caveat:** OS models (Apple FM / Gemini Nano) and small bundled models are English-strongest; other-language *nuance* is weaker (Bulgarian: usable for extraction, shaky for free-form phrasing) — which is exactly why output stays on our localization rails. Voice (Phase 5) inherits the split: STT multilingual-ish, TTS via OS locale voices.

## Open questions / risks

- Device fragmentation: how much reach do we need before Tier B (bundled model, big app) is worth it vs Tier-A-only + graceful fallback?
- Resolver quality is the make-or-break: fuzzy category/wallet matching against real user vocabularies; how aggressively to auto-resolve vs ask.
- Web story: on-device in the browser (WebLLM/WebGPU) is heavy — accept "local on native, opt-in/cloud on web", or no web at all for v1?
- STT accuracy on money tokens (Phase 5) may force a "confirm the number" step regardless.

## Next concrete step when resumed

Take **`LogExpense` end-to-end as a vertical slice** — resolver + confirm-chip component + one real parse call (ONNX vs Apple-FM/Gemini-Nano) — to measure the true line count before committing.
