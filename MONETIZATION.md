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

- **14-day full Pro trial**, card-optional — the payoff planner and shared budget sell themselves once seen.
- **Bill annual-first**; annual dominates retention in this category, and the monthly is priced to nudge toward it.
- EUR pricing suits the EU/Bulgaria base; mirror to USD (~$3.99 / $29.99) for other markets.

## Guardrails — do NOT paywall

- **Export to Excel** — undercuts the "export any time" trust promise.
- **2FA / security** — never gate security.
- **Basic expense/budget logging** — the retention loop and top of funnel.
- **The first statement import** — let people feel the magic once, then cap.

## Engineering gates (what enforcing this needs)

| Gate | Enforcement work | Rough effort |
|---|---|---|
| Subscription state | Billing integration (Stripe/Paddle), entitlement flag per user/household | L |
| Shared account = Pro | Block "Invite" / accept-invite unless owner is Pro | M |
| Import limits | Count imports/rows per period; cap on Free | M |
| History window | Restrict period navigation + Breakdown ranges on Free | M |
| Debt planner | Gate Avalanche/Snowball + projections; allow 1 debt on Free | S |
| Caps (accounts/funds/recurring) | Enforce counts on create for Free | S |
| Trial + paywall UI | Trial timer, upgrade prompts at trigger points, restore purchases | M |

## Why this monetizes well here

The strongest already-built assets — **shared real-time budgets**, the **Avalanche/Snowball debt planner**, and the **Breakdown analytics** — are exactly what people pay for, while the daily logging that drives habit stays free. The upgrade moments are already in the product; monetization is mostly about metering them, not building new value.
