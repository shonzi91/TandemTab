# TandemTab — Feature Backlog

*Simple, high-leverage feature ideas that build on what's already in the app, chosen to add appeal without adding a new mental model. Excludes ideas already shipped (round-ups variants already in place, payoff-projection slider, due-date reminders, most-used categories).*

| | |
|---|---|
| **Application** | TandemTab (https://tandemtab.com) |
| **Theme** | Lock the constants, spotlight what changes · opt-in / invisible-until-useful |
| **Date** | 30 July 2026 |
| **Effort key** | S = small · M = medium · L = large |

## Summary

| # | Theme | Feature | Effort |
|---|-------|---------|--------|
| F1 | Effortless logging | Category-first quick add (amount-only) | S |
| F2 | Effortless logging | Auto-categorize by tag (bind a tag → category) | S–M |
| F3 | Clarity | "Left to spend today" daily allowance | S |
| F4 | Saving | Round-ups into a savings bucket | M |
| F5 | Household | Settle-up for shared accounts | M |
| F6 | Household | Shared-goal celebration | S |
| F7 | Re-engagement | Weekly "your week in money" recap | S–M |

---

## F1 — Category-first quick add (amount-only)
**What:** Tapping a most-used category chip opens the expense sheet with category, fund, and today's date pre-filled and the numeric keypad already up, cursor in Amount. The user types the amount and confirms — two taps.
**Why it works:** Amounts vary every time, but category/fund/date don't — so remove the constants and spotlight the one number that actually changes. Logging effort is the top reason budgeting apps get abandoned.
**Keeps it simple:** No new screen; it's a faster entry into the sheet that already exists.
**Booster:** Show 2–3 tappable *recent amounts* for that category above the keypad (hints, never pre-filled, so they can't be wrong).
**Effort: S.**

## F2 — Auto-categorize by tag
**What:** Let a tag be bound to a category, so applying that tag (or having it remembered for a merchant/note) auto-files the category. e.g. tag `lidl` → Food, tag `metro` → Transport.
**Why it works:** Reuses the tagging users already do; categorization stops being a separate manual step. Compounds F1 — the category can be inferred before the user even picks a chip.
**Keeps it simple:** Builds on the existing tags feature; the binding is a one-time, optional setup per tag.
**Effort: S–M.**

## F3 — "Left to spend today"
**What:** A single daily-allowance number = remaining Free ÷ days left in the period.
**Why it works:** Answers the real daily question — "can I buy this right now?" — better than any balance tile, and turns a period budget into a concrete daily target.
**Keeps it simple:** One line of math over data you already have; one number on Home.
**Effort: S.**

## F4 — Round-ups into a savings bucket
**What:** Optional toggle: round each expense up to the nearest €1/€5 and sweep the difference into a chosen savings goal.
**Why it works:** Painless, automatic saving that directly powers the "set aside €X to hit your 20%" nudge already shown. Small amounts, real momentum.
**Keeps it simple:** One switch; reuses existing funds and goals. Off by default.
**Effort: M.**

## F5 — Settle-up for shared accounts
**What:** For shared/household accounts, a lightweight "who paid what" view and a "you owe €X / they owe €X" settle-up summary.
**Why it works:** Couples and roommates want this and currently reach for a second app (Splitwise). Offering it natively strengthens the paid household wedge and increases invites.
**Keeps it simple:** A read-out over contributions/funds already tracked — no new ledger concept.
**Effort: M.**

## F6 — Shared-goal celebration
**What:** When a shared savings or debt goal hits a milestone, both members get a small celebratory moment.
**Why it works:** Cheap emotion that makes the household feel like a team; drives retention and word-of-mouth among couples.
**Keeps it simple:** Reuses the existing achievements/milestones mechanic.
**Effort: S.**

## F7 — Weekly "your week in money" recap
**What:** Once a week, a single card/notification: spent this week, top category, vs last week, progress toward a goal.
**Why it works:** Gentle re-engagement that brings people back without daily nagging; celebrates progress and surfaces drift early.
**Keeps it simple:** Pure read-out over the existing Breakdown data; strictly weekly so it never becomes noise. Privacy-friendly (on-device).
**Effort: S–M.**

---

## Design guardrail (applies to all)

Keep every one **opt-in or invisible-until-useful** — round-ups off by default, amount hints never pre-filled, recap weekly not daily, tag bindings optional. The apps that lose their simplicity do it by turning helpful nudges into noise.
