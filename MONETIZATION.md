# TandemTab — Monetization Plan

*Post-beta recommendation: what to keep free, what to gate behind Pro, and pricing. Grounded in the app's actual features and its "great solo, better together · never sold" positioning.*

| | |
|---|---|
| **Application** | TandemTab (https://tandemtab.com) |
| **Model** | Freemium → Pro subscription (privacy-first: software revenue, not data) |
| **Date** | 30 July 2026 |

## Guiding principle

The product's own wedge is **"Free to start · Great solo, better together · Never sold."** So:

- **Solo, present-focused budgeting stays genuinely usable for free** — this is the acquisition funnel and honours the privacy promise.
- **Pro sells the two things people pay for in this category: *together* and *intelligence*.**
- Because the business doesn't monetize data, make that the pitch: *"We sell software, not your data."* A real differentiator against ad/data-funded free apps.

## Free vs Pro

| Capability | Free | Pro |
|---|---|---|
| Log expenses / income, categories, tags | Yes | Yes |
| Budgets + over-budget warnings | Yes | Yes |
| Accounts / history | 1 account, current + ~3 months | Full multi-year history |
| Basic goals (savings buckets) | Yes | Yes |
| Export to Excel | Yes (keep free — trust promise) | Yes |
| 2FA / security | Yes (never gate security) | Yes |
| **Shared / household accounts** (real-time partner sync) | — | **Yes — hero feature** |
| **Statement import** (CSV/OFX/QIF) | Limited (~1 import or ~50 rows/mo) | Unlimited |
| **Debt payoff strategies** (Avalanche/Snowball, projections) | View only / 1 debt | Full planner |
| **Advanced insights & Breakdown** (trends, % of income, multi-period) | Current period only | Full |
| Health-score history & achievements depth | Basic | Full |
| Unlimited accounts, funds, recurring items | Small caps | Yes |

The natural paywall line is **"together + history + intelligence."** Keep single-user, current-month budgeting fully functional so free users stay and invite a partner — the *invite* becomes the upgrade moment.

## Packaging

Two tiers, not three:

- **Free** — solo, present-focused.
- **Pro — one subscription per household.** Sharing, unlimited import, full analytics, debt planner, full history. Critically, **one Pro sub covers everyone on a shared account**, since sharing *is* the premium feature. Upgrading unlocks the couple/household, not a per-seat charge.

Built-in upgrade triggers: *invite a partner*, *import my bank statement*, *see my full payoff plan*.

## Pricing

Market anchors (annual): YNAB ~$109, Monarch ~$100, Copilot ~$95, PocketGuard ~$75, Goodbudget ~$80. Those are established brands. As an **unproven indie** in a price-sensitive, free-anchored category, launch *below* value and raise later — a low "easy yes" builds the base, reviews, and word-of-mouth that justify higher prices to future cohorts (grandfather early users).

| Plan | Price | Notes |
|---|---|---|
| **Annual (hero)** | **€29.99 / yr** (~€2.50/mo) | The number that matters; keep it under the €30 threshold |
| Monthly | **€3.99 / mo** | No-commitment tier, deliberately less attractive than annual |
| Beta-tester offer | €19 first year, or lifetime price-lock | Reward the beta cohort |
| Lifetime (optional) | **~€79 one-time** | Converts subscription-averse, privacy-minded users |

**On price level:** €29.99/yr (~€2.50/mo) is the recommendation over a higher €4.99/mo headline. Reasoning: (1) budgeting audiences are price-sensitive and anchored to free apps; (2) an unknown brand can't command incumbent prices on day one; (3) the "one sub covers the whole household" model keeps unit economics healthy even at a low per-sub price; (4) it's far easier to *raise* prices for new cohorts than to lower them. Start low, prove retention, then increase.

- **A full Pro trial**, cardless — the payoff planner and shared budget sell themselves once seen. ⚠️ **Not
  built, and the length is an open owner call** — see [Billing go-live](#billing-go-live--the-real-provider-and-the-trial)
  (this line originally said 14 days; the recommendation there is **30**, so the trial always spans one period
  rollover).
- **Bill annual-first**; annual dominates retention in this category, and the monthly is priced to nudge toward it.
- EUR pricing suits the EU/Bulgaria base; mirror to USD (~$3.99 / $29.99) for other markets.

## Billing go-live — the real provider, and the trial

*Added 2026-08-05 (Session 90). Scheduled as **R5** on [the road to promotion](OPEN-BETA.md#the-road-to-promotion--the-ordered-plan-set-2026-08-05-session-89) — after the Railway migration (R4) moves the webhook URL and the secret store, and alongside the Free/Pro re-validation, because pricing a split that is still moving is work that gets redone.*

### What already exists (so this is smaller than it sounds)

| Piece | State |
|---|---|
| `IPaymentProvider` seam (`src/FinApp.Server/Auth/PaymentProvider.cs`) | ✅ built — one method: *send them somewhere to pay, tell me when they did* |
| `SandboxPaymentProvider` | ✅ walks the entire flow; every row it writes is `Sandbox = 1` |
| `SubscriptionService` + `Subscriptions` table | ✅ entitlement is **ours**, never a live call to the provider |
| `EntitlementService` + client gates + server 402 | ✅ shipped Session 87 |
| `Monetization__Enabled` kill switch | ✅ governs the *selling* surfaces only, not gating |
| **A provider that charges a card** | ❌ **this is the whole remaining job** |

The deliberate design already in place: **entitlement is decided by our table, never by calling the provider at request time.** A provider outage must not downgrade paying users mid-session, and expiry is *stored*, not inferred, so lapsing needs no cron job. Adding `StripePaymentProvider : IPaymentProvider` is a self-contained change against a contract both sides already honour.

### ⚠️ Decision 1 — Stripe, or a merchant of record?

**"Stripe" is the reflex answer, not yet the researched one.** The thing that actually differs is **EU VAT**: a digital subscription sold to an EU consumer is taxed at *the buyer's* rate, in *the buyer's* country.

- **Stripe** — you are the merchant of record. Best API, lowest cut, huge ecosystem. **The VAT registration, collection and filing across the EU is yours.** Stripe Tax calculates it for a fee; it does not file it for you.
- **Paddle / Lemon Squeezy** — *they* are the merchant of record: they sell to the customer, they owe the VAT, they file it. Higher cut, less control, and they can decline or offboard a business.

For a one-person business in Bulgaria selling across the EU, that is a genuine trade and worth an hour before any code is written. The `IPaymentProvider` seam exists precisely so this stays a self-contained choice.

**Whichever wins:**
- **⚠️ This repo is public.** The API key and the **webhook signing secret** are host env vars only — same rule as `Admin__Emails`. A leaked signing secret means anyone can forge a *"they paid"* webhook and mint Pro accounts.
- **The webhook is the source of truth, not the browser return.** A user who closes the tab after paying must still get their plan; a user who hand-crafts a return URL must not.
- **Keep the sandbox provider selectable** (`Payments__Provider`) after go-live — it is how the flow is tested without a live charge.
- The provider becomes a **new sub-processor holding billing data**: privacy policy edit, not a footnote.

### ⚠️ Decision 2 — the Pro trial (promised in this doc, absent from the code)

The line further up — *"14-day full Pro trial, card-optional"* — has **never been built**. `Subscriptions` has no trial concept and `IsActiveAsync` matches only `Status = 'active'`.

**Shape of the change (small, if it goes in the right place):** a trial is just a subscription row with `Provider = null`, `Sandbox = 0`, an `ExpiresAt`, and a status the entitlement check accepts. Expiry then needs **no job at all** — the existing date comparison already does it, and the user falls back to Free the moment it passes.

Three rules that are easy to get wrong:

1. **★ Never delete the row on expiry.** Set its status to expired and leave it. Deleting it makes the trial infinitely repeatable, and "start a trial" is the one action a user can replay for free forever.
2. **★ Grant it per *account*, not per user.** One Pro sub covers a household by design, so a per-user trial is re-triggered by inviting yourself. Same reason: the beta cohort's lifetime Pro means **they never see a trial** — the trial exists for post-cap Free users, and offering it to someone who already has Pro for life reads as a downgrade.
3. **Say when it ends, in the app, before it does.** A silent expiry that quietly re-locks the shared budget is how a good product earns a bad review.

**⬜ Owner call — length and card:** the docs disagree ([docs/BILLING.md](docs/BILLING.md) says 45 days cardless → auto-downgrade; the line above says 14 days card-optional).

**Recommendation: 30 days, cardless, auto-downgrade to Free — no auto-charge.** The reasoning is specific to *this* product rather than category habit: TandemTab's aha moment is the **period rollover** — the reconcile step, the carried-over budgets, the goal that visibly moved. **A 14-day trial can end before the user has ever seen a rollover**, which means trialling the app without meeting the feature it is built around. 30 days guarantees exactly one. 45 is defensible for the same reason but gives away a sixth of an annual subscription's value to someone who may never convert. Cardless converts worse than a card-required trial and is the right call anyway for a brand whose whole pitch is *"we sell software, not your data"* — a surprise charge on a budgeting app is a self-inflicted wound. The model supports card-required later if conversion needs a lift.

## Guardrails — do NOT paywall

- **Export to Excel** — undercuts the "export any time" trust promise.
- **2FA / security** — never gate security.
- **Basic expense/budget logging** — the retention loop and top of funnel.
- **The first statement import** — let people feel the magic once, then cap.

## Engineering gates (what enforcing this needs)

| Gate | Enforcement work | Rough effort |
|---|---|---|
| Subscription state | Billing integration (Stripe/Paddle), entitlement flag per user/household | L — **rails ✅ done, provider ❌ open (R5)** |
| Shared account = Pro | Block "Invite" / accept-invite unless owner is Pro | M |
| Import limits | Count imports/rows per period; cap on Free | M |
| History window | Restrict period navigation + Breakdown ranges on Free | M |
| Debt planner | Gate Avalanche/Snowball + projections; allow 1 debt on Free | S |
| Caps (accounts/funds/recurring) | Enforce counts on create for Free | S |
| Trial + paywall UI | Trial timer, upgrade prompts at trigger points, restore purchases | M — **prompts ✅ done, trial ❌ not modelled at all (R5)** |

## Why this monetizes well here

The strongest already-built assets — **shared real-time budgets**, the **Avalanche/Snowball debt planner**, and the **Breakdown analytics** — are exactly what people pay for, while the daily logging that drives habit stays free. The upgrade moments are already in the product; monetization is mostly about metering them, not building new value.
