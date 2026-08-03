# TandemTab — UX Backlog

*Prioritized UX improvements from a daily-user walkthrough of every tab. Separate from functional defects (see `BUG-REPORT.md`).*

| | |
|---|---|
| **Application** | TandemTab (https://tandemtab.com) |
| **Perspective** | Daily user: log expenses, budgets, goals & debt payoff, see where money goes, reminders, privacy |
| **Date** | 30 July 2026 |
| **Priority key** | P1 = high impact on daily use · P2 = meaningful · P3 = polish |
| **Effort key** | S = small · M = medium · L = large |

## Priority summary

| # | Priority | Area | Item | Effort | Status |
|---|----------|------|------|--------|--------|
| 1 | **P1** | Home / header | Replace ambiguous "Current" tile; lead with "Safe to spend" | S | ✅ S74 |
| 2 | **P1** | Home | Put a real money summary on Home (in / out / safe to spend) | M | ✅ S74 |
| 3 | **P1** | Home / onboarding | Retire the setup checklist once the user is clearly active | S | ✅ S75 / S79 |
| 4 | **P2** | Reminders | Stop duplicating Home insight cards in the notification bell | S | ✅ S75 |
| 5 | **P2** | Reminders | Separate time-based "Due" reminders from "Suggestions" | M | ✅ S75 |
| 6 | **P2** | Spending | Promote "Breakdown" (the real "where money goes" view) | M | ✅ S74 |
| 7 | **P2** | Goals | Show APR / interest rate on each debt row | S | ✅ S74 |
| 8 | **P2** | Wallets | De-emphasize or hide zero-balance funds | S | ✅ S74 |
| 9 | **P2** | Periods | Make "start next month" easier to discover | S | ✅ S79 |
| 10 | **P3** | Goals | Sort / pin a "focus" debt in long goal lists | M | ⬜ open (defer — few users have long lists) |
| 11 | **P3** | Accessibility | Fix control accessible names (icon glyphs, title-only labels) | M | 🔨 in progress |
| 12 | **P3** | Spending | Simplify the three sub-tabs (list vs chart) | S | ✅ closed — stale (superseded by S73–S75 Spending rework) |

---

> **Status (updated Aug 2026):** all of P1 (#1–3) and P2 (#4–9) shipped across Sessions 74–79.
> Of P3, **#12 is closed as stale** (the three-view "list vs chart" premise no longer holds after the
> S73–S75 Spending rework — By date / By budgets / Breakdown now each answer a distinct question).
> **#11 (accessibility) is in progress; #10 (pin focus debt) is deferred** until users actually
> accumulate long goal lists. Detail sections below are kept for the record.

## P1 — high impact on daily use

### 1. Replace the "Current" money tile
The header shows **Current · Free · Saved**, but Current and Free express the same idea and are often identical (both €136.50 in testing), teaching users to ignore one. Lead with **Safe to spend** (today's Free) as the hero number and replace Current with **Spent this period** — the "where did it go" signal. Result: `Safe to spend · Spent · Saved`.

### 2. Give Home a real money summary
Home currently shows nudges and a health score, but the actual in/out numbers live on other tabs (income under Wallets, spend under Spending). A daily user's first screen should answer "how am I doing?" at a glance. Add the summary from #1 to Home.

### 3. Retire onboarding once the user is active
The "Let's get you set up — 4 of 6 done" checklist still headlines Home after the account has income, expenses, budgets, and goals. Collapse it into a small "finish setup" link (or dismiss it) once the user is demonstrably active, so it stops occupying prime real estate.

## P2 — meaningful improvements

### 4. Don't duplicate Home insights in the bell
The notification bell repeats the exact insight cards already shown on Home ("Adjust budgets", "Move to savings", "No savings set aside"). Two copies dilute both. Show each nudge once, in one place.

### 5. Separate "Due" from "Suggestions"
The bell mixes financial *suggestions* (nudges) with what will become time-based *bill reminders*. When recurring bills start surfacing "due soon," must-do reminders risk getting buried under nice-to-do nudges. Split them into two clearly labeled groups.

### 6. Promote the "Breakdown" view
Spending → Breakdown is the genuine "where does my money go" view — income vs spent, **% of income**, by category or fund, across configurable time ranges. It's two taps deep and easy to miss. Surface a mini version on Home and/or promote it above "By date."

### 7. Show interest rate on debt rows
Debt goals list "€500 · owed" but no APR, yet the Avalanche strategy sorts precisely by interest rate. Showing each debt's rate in the row makes the recommended payoff order legible and trustworthy.

### 8. De-emphasize zero-balance funds
Empty funds render as full-size cards with equal visual weight (noise grows as funds accumulate). Add a "hide €0 funds" toggle or collapse zero-balance funds into a small group.

### 9. Make next-period creation discoverable
With one period, the prev/next steppers are disabled with no affordance, and creating the next month is hidden under *Period options → Start next month*. Since recurring-bill reminders depend on periods existing, make this action more visible.

## P3 — polish

### 10. Sort / pin a focus debt
Long goal lists become a scroll. Let users sort or pin a "focus" debt so the one they're attacking stays on top, complementing the payoff plan.

### 11. Fix control accessible names
Header controls label themselves via `title` while their accessible name is an icon/number (e.g. Notifications announces "3"), and modal buttons announce "✓Add" / "✕Cancel" (the decorative glyph is read). Add explicit `aria-label`s and mark decorative glyphs `aria-hidden` — important for a finance app.

### 12. Simplify Spending sub-tabs — ✅ CLOSED (stale, Aug 2026)
Original premise: Categories / By date / Breakdown are really "list vs chart" plus a category grouping. **No longer true** after the S73–S75 rework: **By date** is a chronological ledger, **By budgets** is a budget-adherence view (bars vs caps), and **Breakdown** is a budget-free, multi-period analytical pie over an adjustable window. Each answers a distinct question, and the view switch now leads every view so it no longer jumps on tab change. Nothing left to collapse — closed without further work.

---

## What's already strong (keep)

- The insight cards ("budgets plan €1,176.50 but only €136.50 is free", "set aside €32 to hit 20%") — specific and actionable.
- Goals: Avalanche / Snowball payoff strategies and the "the stack never clears — minimums don't cover interest" warning.
- Breakdown analytics (income vs spent, % of income, by category/fund, time ranges).
- Privacy posture: on-device import, encrypted, never sold, export to Excel, 2FA, delete with grace period.
- Health score and achievements for daily motivation.
