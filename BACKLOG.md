# TandemTab — product backlog

Prioritized from the Session 20 black-box UX/security/functionality review + daily-user
product critique. Ordered by impact ÷ effort. Items shipped are struck through with the
commit noted.

## ✅ Shipped
- ~~Home "You're on track for" card — surfaces the all-debts debt-free date + each savings
  goal's date at current pace, up front on Home (previously buried in the drill-in modals).~~ (`2ad5a44`)
- ~~Per-period, per-budget nudge dismissal — dismissing a "spare → debt" suggestion drops just
  that budget for the rest of the period and never re-nags; resets when the period changes.~~ (`2ad5a44`)
- ~~**#7 Progress-over-time for debts & goals** — new `SavingCategory.DebtOriginalBalance` (captured on
  first config, preserved across edits; legacy debts back-fill to current balance). Debt cards show
  "Paid off €X of €Y (Z%)", a shrinking-balance SVG sparkline (`SavingsReportService.DebtBalanceHistory`,
  reconstructed from disbursement history), and "🚀 ~N ahead of the installment plan". Body data, no migration.~~ (Session 20)
- ~~**#8 Settable planned monthly contribution** — new `SavingCategory.PlannedContribution` (per-period,
  both debt & common buckets). Add/edit modal input; projections prefer it via `EffectiveSavingPace`
  (planned ?? demonstrated pace) — payoff modal, goal modal, and Home "on track for" card all use it.~~ (Session 20)
- ~~**P0 #5 Avatar URL validation** — `AvatarService.IsAcceptableAvatar` now restricts avatars to
  `data:image/*` or trusted provider hosts (Google/Facebook), rejecting arbitrary external URLs that would
  beacon shared-account members' IPs. (Finished the half-wired guard that was breaking the server build.)~~ (Session 20)
- ~~**#9 Cross-period trends** — new "Trends over time" strip on the Insights tab: savings rate, total debt
  owed (reconstructed from payment history), and the top spending category, each as a sparkline with a
  vs-average note and sentiment colour. `InsightsService.BuildMiniTrends` + `TrendSeries`; reuses the
  general `Sparkline` helper shared with the #7 debt card.~~ (Session 20)

## P0 — Quick wins (hours each, low risk, high polish)
1. ~~**Stop the raw .NET leak** — registration email error shows `"Email is not valid. (Parameter 'email')"`;
   return a clean user-facing message.~~ **DONE (Session 20)** — `Exception.CleanMessage()` strips the
   `(Parameter 'name')` suffix; wired into AuthService register + AccountService create/update.
2. ~~**Kill silent no-ops**~~ **DONE (Session 20)** — AuthPanel clears stale errors on edit (`@bind:after`)
   and gives a reason on empty Enter-submit; the expense modal shows an inline note on a negative amount.
3. ~~**Savings-target clamp message**~~ **DONE (Session 20)** — shared `SavingsTargetField` shows an inline
   "keep this 0–100%, we'll use N%" note when the typed value is out of range.
4. ~~**Copy fix**~~ **DONE (Session 20)** — the deficit signal now reads "Spending outran your income" with
   no savings-earmark claim when nothing is saved.
5. ~~**Avatar URL validation**~~ **DONE (Session 20, see Shipped).**
6. ~~**Invite username enumeration**~~ **DONE (Session 20)** — kept the helpful "No user named 'X'" message
   (typo UX) but added a dedicated per-IP rate limiter ("invite" policy, 15/min) to blunt scanning.

## ✅ P0 — all shipped (Session 20)

## P1 — Motivation & self-awareness (days each, highest product ROI) — ✅ ALL SHIPPED (Session 20)
7. ~~**Progress-over-time for debts & goals**~~ — **DONE (Session 20, see Shipped).**
8. ~~**Settable planned monthly contribution**~~ — **DONE (Session 20, see Shipped).**
9. ~~**Cross-period trends**~~ — **DONE (Session 20, see Shipped).**

## ✅ P2 — Habit formation — all shipped (Session 21)
10. ~~**Reminders / notifications**~~ **DONE** — in-app contextual reminders on Home (`HomeReminders`): an
    over/near-cap budget ("You're €7.50 from your Food budget", with a Review action) and a savings nudge
    when short of your rate this period. Local only (no push yet).
11. ~~**Faster expense entry**~~ **DONE** — "Repeat last" Home quick action; recent-merchant chips in the
    add-expense modal; the fund defaults to the one last used for the category (`LastFundForCategory`).
12. ~~**Streaks & milestones / achievements**~~ **DONE** — Home "Milestones" strip (`AchievementsService`):
    saving streak, first payment, 25/50/75/100% debt cleared, goal reached, plus a "next target" with progress.

## Home redesign (Session 21)
- Removed "Top spending" from Home; led the page with **quick actions** + **reminders**, kept the "on track
  for" targets, added the **milestones** strip. Deep analytics (score, savings rate, trends, mini-trends)
  stay behind the "Trends, savings rate & score" expander.

## ✅ P3 — Strategic primitives — all shipped
13. ~~**Recurring transactions**~~ **DONE (Session 24-pre, commits `dc1c03d` → `b7e2956` → `8eea5e3`)** —
    fixed bills, salary and standing transfers as `Domain/Recurring/RecurringItem` (body data, no migration):
    three amount modes (**Fixed** / **Typical** self-tuning / **ReminderOnly**), per-period due tracking,
    opt-in **AutoPost** for fixed bills, and a **"· N bills due"** honesty marker on the balance. Full 🔁
    Recurring UI (Home quick action + list/add/edit/confirm modals). Unit-tested by `RecurringItemTests` (18).
14. ~~**Actionable nudge, done right**~~ **DONE (`BudgetReallocationService`)** — moves a budget's unspent
    leftover into savings or another budget, **capped at leftover** so a budget is never cut below what's
    already spent (`ToSavings` opens savings headroom first; `ToBudget` moves between budgets). Wired into
    the Dashboard + `BudgetingState`; covered by `ReallocationAndCapTests` (5).

**Backlog status:** P0–P3 all cleared. Next work is un-backlogged (see HANDOFF.md for candidates —
push notifications / PWA, debt-lifecycle Phase 3, multi-synced-fund).
