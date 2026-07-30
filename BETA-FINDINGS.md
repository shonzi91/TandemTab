# TandemTab — Beta Findings

*A single pack covering the beta test pass: automated test coverage, defects, UX backlog, monetization plan, and feature ideas. Grounded in a full walkthrough of the app as a daily user (log expenses, budgets, goals & debt payoff, see where money goes, reminders, privacy, household sharing).*

| | |
|---|---|
| **Application** | TandemTab (https://tandemtab.com) |
| **Build type** | Blazor WebAssembly single-page app |
| **Date** | 30 July 2026 |
| **Contents** | 1) Executive summary · 2) Test coverage · 3) Bugs · 4) UX backlog · 5) Monetization · 6) Feature backlog |

---

## 1. Executive summary

TandemTab is further along than "beta" usually implies. The **intelligence layer is its strength** — actionable insight cards, a health score, rich Breakdown analytics, and an Avalanche/Snowball debt planner — and the **privacy posture is a genuine differentiator** (on-device import, encrypted, never sold, export any time, 2FA, delete with grace period).

The work ahead is mostly **surfacing and de-duplicating** what's already built, plus two real defects to fix. Headlines:

- **2 confirmed defects**, both unhandled client-side errors — one user-visible (sign-out).
- **UX**: the app's own smarts are often stronger than the surface presenting them (buried Breakdown, overstaying onboarding, duplicated nudges, an ambiguous money tile).
- **Monetization**: a clean freemium split exists — keep solo/current-month free; gate *together + history + intelligence*. Launch price low (~€29.99/yr) for an unproven brand and raise later.
- **Features**: the highest-leverage additions are small refinements on existing assets, not new subsystems.

A 48-case automated suite now guards the core flows and is green.

---

## 2. Test coverage

A Playwright + TypeScript end-to-end suite drives the app through accessible roles/text (the app ships no test IDs). **48 cases, all passing.**

Covered: landing page, authentication (sign in / sign up / sign out / invalid credentials), privacy & terms pages, dashboard navigation, feature discoverability, reminders & achievements, and the write flows — **log income, log expense, set budget, goals & debt payoff, wallets/funds, statement import (CSV → map → preview → file), recurring bills, and partner invites** — plus the profile security & privacy panel.

The sign-out test asserts only that the session UI is torn down, so it does not mask the sign-out defect below.

---

## 3. Bugs

### BUG-1 — Sign out raises an unhandled error and stalls · Critical
**Repro:** Sign in → Profile settings → Sign out.
**Expected:** Session ends and the app returns to the signed-out landing / login.
**Actual:** The authenticated UI tears down, then the Blazor "An unhandled error has occurred. Reload" bar appears and the app stalls on *Loading…* with the profile overlay still on top. Recovery requires a manual browser reload. In one run, a reload immediately after sign-out returned to the dashboard, suggesting the session may not be fully cleared — verify server-side.
**Impact:** Sign-out is trust-critical for a finance app.

### BUG-2 — Persistent "An unhandled error has occurred" bar · High
**Observed:** The Blazor global error UI is present from the initial landing-page load, indicating unhandled exceptions during ordinary flows (not only sign-out).
**Recommendation:** Capture/log the exception(s), guard the failing lifecycle/render paths, and treat any appearance of the global error bar as a release blocker.

### Lower-severity
- **Money overview isn't on Home** (income/spend live on other tabs).
- **Next-period creation is hard to discover** (hidden under Period options → Start next month); recurring reminders depend on periods existing.
- **Accessibility:** control accessible names are icon glyphs / title-only (modal buttons announce "✓Add" / "✕Cancel"; Notifications announces "3").

---

## 4. UX backlog

Priority: P1 = high impact on daily use · P2 = meaningful · P3 = polish. Effort: S/M/L.

| # | Priority | Area | Item | Effort |
|---|----------|------|------|--------|
| 1 | P1 | Home / header | Replace ambiguous "Current" tile; lead with "Safe to spend" | S |
| 2 | P1 | Home | Put a real money summary on Home (in / out / safe to spend) | M |
| 3 | P1 | Home / onboarding | Retire the setup checklist once the user is clearly active | S |
| 4 | P2 | Reminders | Stop duplicating Home insight cards in the notification bell | S |
| 5 | P2 | Reminders | Separate time-based "Due" reminders from "Suggestions" | M |
| 6 | P2 | Spending | Promote "Breakdown" (the real "where money goes" view) | M |
| 7 | P2 | Goals | Show APR / interest rate on each debt row | S |
| 8 | P2 | Wallets | De-emphasize or hide zero-balance funds | S |
| 9 | P2 | Periods | Make "start next month" easier to discover | S |
| 10 | P3 | Goals | Sort / pin a "focus" debt in long goal lists | M |
| 11 | P3 | Accessibility | Fix control accessible names (icon glyphs, title-only labels) | M |
| 12 | P3 | Spending | Simplify the three sub-tabs (list vs chart) | S |

**Money-tile fix (item 1) in one line:** `Current · Free · Saved` → `Safe to spend · Spent · Saved` — Current and Free say the same thing today; swapping Current for Spent adds the "where did it go" signal and removes the overlap.

**Already strong — keep:** the insight cards, Avalanche/Snowball debt planner, Breakdown analytics, the privacy posture, and health score / achievements.

---

## 5. Monetization plan

**Model:** Freemium → Pro. Because the business doesn't monetize data, make that the pitch: *"We sell software, not your data."*

**Keep free:** solo, present-focused budgeting — logging, budgets, basic goals, 1 account, current + ~3 months, export to Excel, 2FA. **Gate behind Pro:** *together + history + intelligence.*

| Capability | Free | Pro |
|---|---|---|
| Log expenses/income, budgets, basic goals, export, 2FA | Yes | Yes |
| **Shared / household accounts** (real-time sync) | — | Yes (hero) |
| **Statement import** | ~1/mo or ~50 rows | Unlimited |
| **Debt payoff planner** (Avalanche/Snowball) | 1 debt / view | Full |
| **Advanced insights & Breakdown** (multi-period) | Current period | Full |
| Full history, unlimited accounts/funds/recurring | Small caps | Yes |

**Packaging:** two tiers. **One Pro subscription covers the whole household** (sharing *is* the premium feature). Built-in upgrade triggers: *invite a partner*, *import a statement*, *see my full payoff plan*.

**Pricing (launch low, raise later — grandfather early users):**

| Plan | Price | Notes |
|---|---|---|
| **Annual (hero)** | **€29.99 / yr** (~€2.50/mo) | Keep under the €30 threshold |
| Monthly | €3.99 / mo | Priced to nudge toward annual |
| Beta-tester offer | €19 first year / lifetime lock | Reward the beta cohort |
| Lifetime (optional) | ~€79 one-time | Converts subscription-averse users |

**Why not €4.99/mo:** budgeting audiences are free-anchored, an unknown brand can't command incumbent prices, and the household model keeps unit economics healthy at a low per-sub price — and it's easier to raise than lower. **14-day card-optional trial; bill annual-first.**

**Do NOT paywall:** export (trust promise), 2FA (security), basic logging (funnel), the *first* statement import (let people feel the magic once).

**Engineering to enforce:** billing + entitlement (L), shared-account = Pro gate (M), import limits (M), history window (M), planner gate (S), Free caps (S), trial + paywall UI (M).

---

## 6. Feature backlog

Simple, high-leverage additions that build on existing assets. Excludes what's already shipped (round-up variants, payoff-projection slider, due-date reminders, most-used categories). Guardrail: every item is **opt-in or invisible-until-useful**.

| # | Theme | Feature | Effort |
|---|-------|---------|--------|
| F1 | Effortless logging | Category-first quick add (amount-only, recent-amount hints) | S |
| F2 | Effortless logging | Auto-categorize by tag (bind a tag → category) | S–M |
| F3 | Clarity | "Left to spend today" daily allowance | S |
| F4 | Saving | Round-ups into a savings bucket | M |
| F5 | Household | Settle-up for shared accounts | M |
| F6 | Household | Shared-goal celebration | S |
| F7 | Re-engagement | Weekly "your week in money" recap | S–M |

- **F1** — a category chip opens the sheet with category/fund/date pre-filled and the keypad up on Amount; recent amounts shown as tappable hints (never pre-filled, since amounts vary). Two taps.
- **F2** — bind a tag to a category so tagging auto-files it; compounds F1 by inferring the category up front.
- **F3** — remaining Free ÷ days left in period → one daily target that answers "can I buy this?"
- **F4** — optional round-ups sweep into a savings goal; powers the "set aside €X" nudge already shown.
- **F5** — native "who paid what / you owe €X" for households; strengthens the paid wedge, replaces a second app.
- **F6** — celebrate shared-goal milestones for both members; reuses achievements.
- **F7** — one weekly recap card over Breakdown data; re-engages without daily nagging.

---

## Suggested sequencing

1. **Fix the two defects** (BUG-1, BUG-2) — release blockers.
2. **P1 UX** — money summary + "Safe to spend" tile, retire onboarding.
3. **F1–F3** (logging + clarity) — cheapest wins for daily habit.
4. **Monetization plumbing** — billing/entitlement + the household gate, priced at €29.99/yr.
5. **F5 settle-up + P2 UX** (promote Breakdown, split Due/Suggestions, hide €0 funds).
