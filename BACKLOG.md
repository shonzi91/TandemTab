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
2. **Kill silent no-ops** — empty sign-in submit, negative expense amount, and resubmit-after-error all
   fail with *no message* (and a stale "Password must be ≥ 8 characters" lingers after the field is fixed).
   Add inline feedback; clear stale errors on change/resubmit.
3. **Savings-target clamp message** — an out-of-range target (e.g. 5000%) silently snaps to 100%; surface
   what happened instead of silently mutating.
4. **Copy fix** — the deficit signal "…leans on your savings earmark" appears even when there are zero
   savings; reword for the no-savings case.
5. **Avatar URL validation** — the avatar is stored and rendered as `<img src>` from any string; an arbitrary
   external URL becomes a tracking beacon visible to shared-account members. Restrict to `data:image/*`
   (+ trusted host allowlist for adopted Google/Facebook profile pics).
6. **Invite username enumeration** — inviting a non-existent user returns "No user named 'X'.", confirming
   who exists. Low severity (usernames aren't secret, requires auth). *Tradeoff:* a neutral response hurts
   the "did I typo the name?" UX in a collaboration app — consider rate-limiting over silencing.
   *(#5 done — see Shipped.)*

## P1 — Motivation & self-awareness (days each, highest product ROI) — ✅ ALL SHIPPED (Session 20)
7. ~~**Progress-over-time for debts & goals**~~ — **DONE (Session 20, see Shipped).**
8. ~~**Settable planned monthly contribution**~~ — **DONE (Session 20, see Shipped).**
9. ~~**Cross-period trends**~~ — **DONE (Session 20, see Shipped).**

## P2 — Habit formation (larger, converts tool → daily habit)
10. **Reminders / notifications** — local reminders first ("payday? move €X", "you're €40 from your Food
    budget"), push later. Nothing currently pulls the user back into the daily loop.
11. **Faster expense entry** — "repeat last", recent-merchant chips, remember the last fund per category.
    Daily logging is currently a full 5-field modal every time.
12. **Streaks & milestones / achievements** — "3 months hitting your rate", "first extra payment", "25% of
    the loan gone". The bucket's "Notify on milestone" toggle already exists with nowhere motivating to land.

## P3 — Strategic primitives (unlock multiple items above)
13. **Recurring transactions** — fixed bills, salary, standing savings/debt transfers. Biggest missing
    primitive: makes budgeting predictive, makes "free to allocate" honest, and is the real fulfillment of
    the nudge's "every period" framing.
14. **Actionable nudge, done right** — "Move €290 from Food's plan to the loan" (reallocation) **or** a
    standing transfer, behind an explicit confirm. The coherent version of the one-tap button that was
    reverted (setting cash aside didn't consume the budget slack, so the nudge nagged/double-applied).
    Depends on #13 for the recurring variant.

**Suggested sequencing:** P0 (1–4) in one pass → #7 + #8 together (shared debt/goal data model) →
commit to #13 as the strategic bet that unblocks #10, #14, and predictive budgeting.
