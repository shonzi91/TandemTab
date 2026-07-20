# TandemTab — subscriptions & entitlements

**Status:** design. Nothing is implemented; this is the plan to fold into the server-side domain
migration (see [MOBILE.md](MOBILE.md) Option A / Phase 0).

**Sequencing decision (2026-07-20): monetize *after* mobile + push notifications, not now.** A paid
web-only app with no push notifications converts poorly and churns hard — retention is the thing that
makes a subscription stick, and that arrives with native + notifications. Build the *rails* early
(they're cheap and migration-independent); flip on billing once the app is worth paying for. **Design
the tier boundaries now** so the migration builds them in rather than bolting them on.

This doc is the single source of truth for pricing/entitlements. Update it as decisions firm up.

---

## Tiers

Prices are launch guesses, not final. **Free must be generous enough to fall in love** — the
motivating features (the debt-free date, momentum/streaks) are the retention hook and conversion
engine, so they must be *felt* for free. Gate **depth, breadth, collaboration, and cost-incurring
features**, never the core hook or history.

| | **Free** — the hook | **Premium** — $4.99/mo · **$39.99/yr** | **Ultra** — $8.99/mo *(later)* |
|---|---|---|---|
| Manual entry, budgets, funds, reminders | ✅ | ✅ | ✅ |
| Recurring items — *define + reminder* | ✅ | ✅ **+ auto-post** | ✅ + auto-post |
| Debt payoff date + goal projection | **1 debt · 1 goal** | Unlimited | Unlimited |
| Combined "debt-free by X" headline *(the aha)* | ✅ | ✅ | ✅ |
| Full history / streaks / milestones | ✅ | ✅ | ✅ |
| Statement imports | 1 / mo, manual | Unlimited **+ auto-file rules** | Unlimited |
| Multi-debt planner, what-if sims, runway, advanced trends | — | ✅ | ✅ |
| Shared account with a partner *(host pays)* | — | ✅ | ✅ |
| Multiple accounts | 1 | Several | Several |
| Live bank sync (Enable Banking) | — | — | ✅ |
| Multi-currency + live FX (ExchangeRate API) | — | — | ✅ |

**Why this shape**
- **Gate the count, not the aha.** Free tracks **1 debt + 1 goal** with the full payoff date /
  projection on them — accurate, because it's projecting exactly what's tracked. Unlimited debts/goals
  is Premium, and the multi-debt planner is *naturally* Premium since it needs 2+ debts. (Don't ever
  project a subset of several entered debts — that shows a wrong debt-free date. A hard cap avoids it.)
- **Never gate history.** Streaks/milestones/momentum *are* history; locking it kills the feature that
  drives daily return, which is what you need before anyone will pay.
- **Split recurring & imports on *automation*, not the primitive.** Recurring bills must be free to
  *define* — without rent/salary, "free to allocate" is wrong, the runway can't project, and the free
  app feels broken, so people bounce before forming the habit that converts them. Free gets the item +
  a "Rent due today — log it?" reminder; **Premium auto-posts** it. Same axis for imports: 1 manual
  import/month free, unlimited **+ auto-file categorization** Premium.
- **Ultra = the features that cost *us* money.** Enable Banking (per-connection PSD2 cost) and FX rates
  align the top price with COGS. ⚠️ Before Ultra ships, confirm $8.99 − Premium value actually covers a
  heavy bank-sync + FX user; if that user costs $3–4/mo, Ultra margin is thin.

**Pricing notes**
- Annual at **$39.99 (~33% off)**, not $49.99 (~16%) — the thin discount suppresses annual take-up, and
  annual plans are what protect you from churn.
- $4.99/mo is aggressive vs. the field (YNAB $14.99, Rocket Money ~$6–12). A low anchor buys trust for a
  new app; raise later with grandfathering (subs store their own `Plan`, so a price rise won't touch
  existing ones unless you migrate them).
- **Three tiers, launched in two steps.** Ship **Free + Premium**; introduce **Ultra only when bank sync
  + multi-currency actually exist.** Don't show a tier you can't deliver.

---

## The model

### The one insight: entitlement is a property of the *account*, resolved through its *owner*

The caller's identity never enters a feature check:

```
plan_governing(account) = effective_plan( account.OwnerUserId )   // NOT the caller
allowed = PlanFeatures.Includes(plan_governing(account), feature)
```

This is what makes host-and-guest work with **no per-guest state**:

- **Guest** edits the host's shared account → check keys off `OwnerUserId` (the host) → guest gets
  Premium automatically. Only the host needs to pay.
- The same guest's **own personal account** → its owner is the guest → governed by the guest's own
  (Free) sub → upgrade prompts land there. Same rule, no special case.
- **Host cancels** → owner's plan lapses to Free → the shared account degrades the moment the sub
  expires. No cleanup job — the resolver does it.

We already have the hook: **`Account.OwnerUserId` exists today** (the account creator; gates
rename/delete). It *is* the "host".

### Where each piece lives

| Piece | Layer | Why there |
|---|---|---|
| `Plan` enum + `PlanFeatures` map | **Domain (pure)** | Single source of truth for tier boundaries; unit-testable; shared by client UX + server enforcement (like `CashFlowBasis`). **Migration-proof** — the only part safe to stub before the migration. |
| `Subscription` (per **User**) | **Server, relational/EF** | Billing state must be queryable — webhooks write it, an expiry check reads it. Must **not** live in the account snapshot blob (opaque, gzipped, KMS-encrypted, per-account). `User` is already an EF entity (`db.Users`), so this sits beside it. |
| `OwnerUserId` (+ optional cached `Plan`) as a plain column on the account row | **Server, relational/EF** | So the server resolves an account's owner **without decrypting** the snapshot. This is also **brick #1 of the domain migration** — building entitlements pulls it forward rather than competing with it. |

### 1. Domain — the tier vocabulary (pure)

```csharp
namespace FinApp.Domain.Billing;

public enum Plan { Free = 0, Premium = 1, Ultra = 2 }   // order = tier order

public enum Feature {
    SharedAccount, MultiDebtPlanner, WhatIfSimulator, CashFlowRunway,
    AdvancedTrends, AutoPostRecurring, AutoFileRules,
    BankSync, MultiCurrency,          // Ultra-only
}

public static class PlanFeatures {
    // Minimum plan per feature. Anything NOT listed is free-for-all (manual entry, budgets,
    // reminders, defining recurring items, the combined debt-free headline, full history).
    private static readonly Dictionary<Feature, Plan> Min = new() {
        [Feature.SharedAccount]     = Plan.Premium,
        [Feature.MultiDebtPlanner]  = Plan.Premium,
        [Feature.WhatIfSimulator]   = Plan.Premium,
        [Feature.CashFlowRunway]    = Plan.Premium,
        [Feature.AdvancedTrends]    = Plan.Premium,
        [Feature.AutoPostRecurring] = Plan.Premium,   // defining a recurring item is free; auto-posting is not
        [Feature.AutoFileRules]     = Plan.Premium,
        [Feature.BankSync]          = Plan.Ultra,
        [Feature.MultiCurrency]     = Plan.Ultra,
    };

    public static bool Includes(Plan p, Feature f) =>
        Min.TryGetValue(f, out var m) && p >= m;   // Ultra inherits Premium automatically

    // Quotas are numbers, not booleans, so they sit beside the map. Free is capped; paid is unlimited.
    public static int MonthlyImportLimit(Plan p) => p == Plan.Free ? 1 : int.MaxValue;
    public static int MaxDebtBuckets(Plan p)     => p == Plan.Free ? 1 : int.MaxValue;
    public static int MaxGoalBuckets(Plan p)     => p == Plan.Free ? 1 : int.MaxValue;
}
```

Change tiers → edit this one file.

### 2. Server — Subscription (per user)

```csharp
public enum SubStatus { Trialing, Active, Grace, Canceled, Expired }

public sealed class Subscription : Entity {
    public Guid UserId { get; }
    public Plan Plan { get; private set; }
    public SubStatus Status { get; private set; }
    public DateTimeOffset? CurrentPeriodEnd { get; private set; }   // trial or paid-period end
    public string? Provider { get; }        // "stripe" | "apple" | "google" | null (cardless trial)
    public string? ProviderRef { get; }     // customer/sub id or original_transaction_id

    // Entitled if active, OR trialing/grace/canceled but not yet past the end date.
    public bool IsEntitledNow(DateTimeOffset now) =>
        Status == SubStatus.Active ||
        (Status is SubStatus.Trialing or SubStatus.Grace or SubStatus.Canceled &&
         CurrentPeriodEnd is { } end && end > now);
}
```

**No Subscription row = Free.** Never backfill; absence is the default tier.

### 3. Server — EntitlementService (the resolver)

```csharp
public sealed class EntitlementService(AppDb db, IClock clock) {
    public async Task<Plan> EffectivePlanAsync(Guid userId, CancellationToken ct) {
        var sub = await db.Subscriptions.FirstOrDefaultAsync(s => s.UserId == userId, ct);
        return sub?.IsEntitledNow(clock.Now) == true ? sub.Plan : Plan.Free;
    }

    public async Task<Plan> PlanForAccountAsync(Guid accountId, CancellationToken ct) {
        var ownerId = await db.AccountRows.Where(r => r.Id == accountId)
                                          .Select(r => r.OwnerUserId).SingleAsync(ct);
        return await EffectivePlanAsync(ownerId, ct);       // ← resolve through owner
    }

    public async Task RequireAsync(Guid accountId, Feature f, CancellationToken ct) {
        if (!PlanFeatures.Includes(await PlanForAccountAsync(accountId, ct), f))
            throw new PaymentRequiredException(f);          // → HTTP 402
    }
}
```

---

## Enforcement — two kinds of gate

Spend enforcement effort only on the **hard** gates. Precedent already in the tree:
`BankSync/BankAccessPolicy.cs` + `BankSyncService.RequireBankAccess` — generalize that shape.

| Gate | Enforced where | Features | Why |
|---|---|---|---|
| **Hard** (real €/provider cost) | **Server, authoritative** — like `RequireBankAccess` today | Bank sync, statement imports (quota), FX rates, **creating a shared space** | A bypass costs *us* money or hits a provider |
| **Soft** (pure client compute over the user's own data) | **Client UX only**, via `/me` payload | Multi-debt planner, what-if sim, runway, trends | These are `LoanForecast`/`InsightsService` computed on-device; there's no endpoint to protect. Worst case a determined user reads their own numbers — not worth server plumbing |

- **Shared-space gate:** `RequireAsync(accountId, Feature.SharedAccount)` when adding the **2nd member**
  (at the invite/accept path). Keys off the owner → only the host needs Premium.
- **Client needs to know the plan:** extend `/me` (or the account summary) to return
  `{ plan, features: [...], limits: { importsThisMonth } }`. This is for UX (hide buttons, show upgrade
  CTAs) and is **never** authoritative.

---

## Trials (45-day, cardless → downgrade to Free)

A trial is just a subscription in `Trialing` with **no card attached**:

```
Subscription { Plan = Premium, Status = Trialing, CurrentPeriodEnd = now + 45d, Provider = null }
```

When it lapses, `IsEntitledNow` returns false → the resolver returns Free → the app degrades
gracefully. **"Strip them to Free when the trial ends" needs no separate job** — it's the same
resolver path as a host cancelling. No auto-charge, because there's no payment method.

- **45 days is defensible for budgeting** — value compounds across periods, so a trial spanning a full
  period rollover + the momentum features is what delivers the aha. (Shorter 14–30-day trials are the
  norm elsewhere; the longer window is a deliberate bet on this category.)
- ⚠️ **Careful trialing Ultra.** Bank sync and FX cost real money per user, so a free Ultra trial pays
  Enable Banking / FX fees for non-payers. Trial **Premium** freely; for Ultra, exclude bank sync from
  the trial or keep the Ultra trial short.
- This is a *converting-to-Free* trial, not an auto-charging one — friendlier, no surprise charges,
  lower conversion than a card-required trial. If conversion needs a lift later, a card-required
  auto-converting trial (standard IAP) is the alternative; the model supports both.
- Same graceful-degrade UI as a host cancel (read-only + retain data) covers trial expiry too.

## Surfacing locked features (Premium/Ultra badges)

Gated features carry a small **badge/lock tag** (⭐ Premium, 💎 Ultra) — a *tag*, not a new section, so
it fits the app's "avoid overwhelming sections" design ethos.

- **Visible-but-locked, never hidden.** A greyed feature with a badge drives more upgrades than an
  absent one — you can't want what you can't see. Tapping it opens the upgrade sheet.
- **Keep it light** — one subtle badge per locked feature, not littered across the UI; protect the calm
  aesthetic.
- The client already knows the plan + feature list from the `/me` payload, so it renders the badge and
  the upgrade CTA with no extra round-trip.

## Billing providers (later)

Put **RevenueCat** at the edge as the cross-platform entitlement truth: **Stripe** on web, **Apple/Google
IAP** on mobile. ⚠️ **Shared-access-as-purchase must go through IAP on mobile** (15–30% platform cut) —
which is another reason billing belongs on the server, not bolted onto the web app.

- RevenueCat's **webhook is the only writer** of the `Subscription` table; all our code ever does is
  **read** the table. One integration instead of three receipt validators; providers stay at the edge.

---

## Edge cases to decide before shipping billing

- **Host cancels** → owner plan lapses → resolver returns Free → shared account degrades. UI must go
  **read-only + retain data**, never delete. (Automatic via the resolver; we only build the graceful UI.)
- **Ownership transfer** (`Account.TransferOwnership` exists) → plan follows the new owner. Transferring
  to a Free user silently downgrades the household — **warn or block** that transfer.
- **Grandfathering** — `Subscription.Plan` is stored per user, so a price rise never touches existing
  subs unless we migrate them. Keep it that way.

---

## Legal — Privacy Policy & Terms (must update *before* taking money)

Introducing paid tiers changes the legal surface, so both documents need a revision that ships **with**
billing, not after:

- **Terms of Service** — add subscription terms: what each tier grants, price + billing cadence, the
  **45-day trial → auto-downgrade-to-Free** behaviour (explicitly: no auto-charge on this trial model),
  renewal/cancellation, refunds, and that shared-account access ends when the host's plan lapses (data
  retained, read-only).
- **Privacy Policy** — disclose the new processors/data flows: the billing provider (**RevenueCat**),
  the payment platforms (**Stripe / Apple / Google IAP**), and — when Ultra ships — **Enable Banking**
  (Open Banking / PSD2) and the **ExchangeRate API**. Say what's shared with each and why.
- **Store compliance** — Apple/Google require accurate subscription disclosure + working restore-purchase
  and cancellation paths; the Terms/Privacy links are a review checklist item.
- ⚠️ **Get these reviewed by someone qualified** — this is not legal advice, just the checklist of what
  changes. Regulated-finance + payments wording is worth a professional pass.

---

## Build order

Two pieces are **independent of the domain migration** and can ship first to lay the rails, gating
nothing:

1. `Plan` + `PlanFeatures` in the domain (pure, ~an afternoon, fully unit-tested). **Migration-proof** —
   the tier boundaries can't be invalidated by moving the money model.
2. `Subscription` EF table + `EntitlementService`, everyone defaulting to Free, **gating nothing yet** —
   but `/me` starts returning the plan.

The one thing that **is** migration work is the denormalized `OwnerUserId` column on the account row —
which the "move the money model server-side" migration gives us anyway.

**Do not gate any feature or take any payment until mobile + push notifications ship** (the sequencing
decision above). And **do not take a first payment until the Terms + Privacy revision above ships** —
it's a hard pre-launch gate, not a follow-up.
