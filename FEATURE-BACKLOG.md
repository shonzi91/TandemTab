# TandemTab — Feature Backlog

*Simple, high-leverage feature ideas that build on what's already in the app, chosen to add appeal without adding a new mental model. Excludes ideas already shipped (round-ups variants already in place, payoff-projection slider, due-date reminders, most-used categories).*

| | |
|---|---|
| **Application** | TandemTab (https://tandemtab.com) |
| **Theme** | Lock the constants, spotlight what changes · opt-in / invisible-until-useful |
| **Date** | 30 July 2026 |
| **Effort key** | S = small · M = medium · L = large |

## Summary

| # | Theme | Feature | Effort | Status |
|---|-------|---------|--------|--------|
| F1 | Effortless logging | Category-first quick add (amount-only) | S | ✅ Session 89 |
| F2 | Effortless logging | Auto-categorize by tag (bind a tag → category) | S–M | ✅ Session 89 |
| F3 | Clarity | "Left to spend today" daily allowance | S | ✅ shipped earlier (`2f35a6a`) |
| F4 | Saving | Round-ups into a savings bucket | M | ✅ Session 89 |
| F5 | Household | Settle-up for shared accounts | M | ❌ **Dropped** — see below |
| F6 | Household | Shared-goal celebration | S | ✅ Session 89 |
| F7 | Re-engagement | Weekly "your week in money" recap | S–M | ✅ Session 89 |

> **Status (2026-08-05, Session 89):** this backlog is **cleared**. F1/F2/F4/F6/F7 were built in one pass; F3 turned
> out to have shipped already (as the `.bal-daily` sub-line under *Safe to spend*) and was simply never marked here;
> **F5 was dropped by the owner** on the grounds that TandemTab's shared accounts are *two contributors paying into
> one pool*, not two people splitting costs — so there is nothing to settle up. (Note the app does already have a
> narrower *on-behalf* settlement between **separate accounts**, `Expense.SettledToAccountId` — a different thing.)

---

## F1 — Category-first quick add (amount-only)
**What:** Tapping a most-used category chip opens the expense sheet with category, fund, and today's date pre-filled and the numeric keypad already up, cursor in Amount. The user types the amount and confirms — two taps.
**Why it works:** Amounts vary every time, but category/fund/date don't — so remove the constants and spotlight the one number that actually changes. Logging effort is the top reason budgeting apps get abandoned.
**Keeps it simple:** No new screen; it's a faster entry into the sheet that already exists.
**Booster:** Show 2–3 tappable *recent amounts* for that category above the keypad (hints, never pre-filled, so they can't be wrong).
**Effort: S.**
**As built (S89):** the category chips already existed; the gap was the *keypad*. Amount now takes focus when the
category was a deliberate choice (a quick-add chip, budget row, category detail) and carries `inputmode="decimal"`
so phones raise the numeric pad. Opening the blank modal deliberately does **not** steal focus — the keypad would
cover the category picker the user still has to use. Hints come from `RecentAmountsForCategory`, which requires an
amount to have been used **twice**: a one-off €13.47 is history, not a habit.

## F2 — Auto-categorize by tag
**What:** Let a tag be bound to a category, so applying that tag (or having it remembered for a merchant/note) auto-files the category. e.g. tag `lidl` → Food, tag `metro` → Transport.
**Why it works:** Reuses the tagging users already do; categorization stops being a separate manual step. Compounds F1 — the category can be inferred before the user even picks a chip.
**Keeps it simple:** Builds on the existing tags feature; the binding is a one-time, optional setup per tag.
**Effort: S–M.**
**As built (S89):** `Tag.CategoryId` (snapshot body data) + a "Files into" picker in the edit-tag modal, shown on the
manage-tags row so a tag that changes the category isn't doing it invisibly. It is a **default at entry time, never a
rule over stored rows**: it fires only while *adding*, so tagging an existing expense can't re-file it (and move spend
between budgets) as a side effect. The swap is announced — "Filed under Transport". Removing a category clears any
binding pointing at it rather than leaving it dangling.

## F3 — "Left to spend today" — ✅ already shipped
**What:** A single daily-allowance number = remaining Free ÷ days left in the period.
**Why it works:** Answers the real daily question — "can I buy this right now?" — better than any balance tile, and turns a period budget into a concrete daily target.
**Keeps it simple:** One line of math over data you already have; one number on Home.
**Effort: S.**
**Already built** in commit `2f35a6a` — the `.bal-daily` sub-line under *Safe to spend*. The numerator is
after-bills headroom, so it can't over-promise before a known bill lands; shown only on the open latest period with
positive headroom. This entry was simply never ticked.

## F4 — Round-ups into a savings bucket
**What:** Optional toggle: round each expense up to the nearest €1/€5 and sweep the difference into a chosen savings goal.
**Why it works:** Painless, automatic saving that directly powers the "set aside €X to hit your 20%" nudge already shown. Small amounts, real momentum.
**Keeps it simple:** One switch; reuses existing funds and goals. Off by default.
**Effort: M.**
**As built (S89):** `Account.RoundUpTo` (0 = off, 1 or 5) + a destination bucket, set in the edit-account modal
beside the savings target — no new section for one switch. The sweep lives in `RoundUpService` because **both the
web client (optimistic) and the server run it**; if they drifted the client would paint a savings row the server
never wrote and the next refetch would silently take the money back. The sweep is an **earmark, not a second
expense**, so the ledger still records exactly what was spent. ⚠️ **A round-up with no cash behind it is skipped**:
allocations may normally exceed available cash (they're advisory), but raising the "overspent into savings" alarm
over 40 cents nobody chose to move would be the feature working against the user.

## F5 — Settle-up for shared accounts — ❌ DROPPED (owner, 2026-08-05)
**Why it was dropped:** the premise doesn't hold for this product. A TandemTab shared account is **two contributors
putting income into one pool** and spending from it — not two people each paying their own costs and reconciling.
There is no per-person balance to settle, so a "you owe €X" summary would have to invent a debt that the ledger
does not model. Don't revive this without first changing what a shared account *means*.

*Original entry:* a lightweight "who paid what" view and a "you owe €X / they owe €X" settle-up summary, pitched at
couples and roommates who currently reach for Splitwise. **Effort was M.**

## F6 — Shared-goal celebration
**What:** When a shared savings or debt goal hits a milestone, both members get a small celebratory moment.
**Why it works:** Cheap emotion that makes the household feel like a team; drives retention and word-of-mouth among couples.
**Keeps it simple:** Reuses the existing achievements/milestones mechanic.
**Effort: S.**
**As built (S89):** a one-shot `GoalCelebration` overlay on per-bucket milestones (`goal_{id}`, `debt_{tier}_{id}`),
with a "You and Maria got here together" line only when the account really is shared.
⚠️ **The key design point: "have I seen this" is tracked per DEVICE, not on the account.** The achievement log lives
in the shared snapshot, so driving the moment off "newly stamped" would hand it to whichever member opened the app
first and silently rob the other — the exact opposite of the feature. First run for an account adopts everything
already earned as seen (an established account must not open into a stack of old celebrations) **and writes that
marker even when the set is empty** — without that, a brand-new account still looks like a first run when its first
milestone lands, and swallows the one celebration that matters most. (That was a real bug, caught in verification.)

## F7 — Weekly "your week in money" recap
**What:** Once a week, a single card/notification: spent this week, top category, vs last week, progress toward a goal.
**Why it works:** Gentle re-engagement that brings people back without daily nagging; celebrates progress and surfaces drift early.
**Keeps it simple:** Pure read-out over the existing Breakdown data; strictly weekly so it never becomes noise. Privacy-friendly (on-device).
**Effort: S–M.**
**As built (S89):** `WeeklyRecapService` + a dismissible Home card. It covers the **last completed** Monday–Sunday
week, not the current one: a recap of a week still running changes every time you open it, and its "vs last week"
compares three days against seven — which reads as a spending collapse every Tuesday. Weeks are walked across the
whole account, not within a period, because a calendar week routinely straddles two monthly periods. The comparison
line is suppressed when there's no prior week (*"100% less than last week"* for someone's first week is noise
dressed as insight), and the card doesn't render at all for an empty week. Dismissal is per device and per week, so
it retires until next Monday — and, like F6, one member dismissing it must not clear it for the other.

---

## Design guardrail (applies to all)

Keep every one **opt-in or invisible-until-useful** — round-ups off by default, amount hints never pre-filled, recap weekly not daily, tag bindings optional. The apps that lose their simplicity do it by turning helpful nudges into noise.
