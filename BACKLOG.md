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

## P4 — Next up (added Session 31, from a competitor-gap review)
15. ~~**Predictive cash-flow runway (3–6 months out)**~~ **✅ DONE.** Built as
    `Domain/Forecasting/CashFlowForecast.Project(...)` — a pure, month-stepped projector that walks up to
    6 months from the current closing balance, applies due recurring income/bills + the sinking-fund
    set-aside, and names the first month the balance goes negative (`CashFlowMonth`/`CashFlowProjection`,
    with a `CashFlowBasis` for demonstrated-vs-typical). Covered by `CashFlowForecastTests`. Surfaced on
    Home as the **"At this rate" runway card** (gated to the latest period via `State.IsLatestPeriod`) with
    a **"Show the math" modal** (`Modal.RunwayMath`, S76) whose what-if sliders recompute client-side
    (the forecast is `decimal`/`Guid`/`string`-pure, no `Money`/`Period` — see `docs/DOMAIN-REMOVAL.md`).
    *Was: the one genuine gap a competitive review turned up. Every input already existed; now they render.*
    Original rationale kept below for the record:
    - `Domain/Recurring/RecurringItem` already models bills + income as **forward-looking expectations**
      (`DayOfMonth`, per-period due tracking, `DueDateWithin`/`IsUpcoming`). `RecurringAmountMode.Typical`
      even self-tunes toward the actual via `LearnFromActual` — recurring amounts are already predictions.
    - `LoanForecast` / `InvestmentForecast` are pure, month-stepped projectors — copy their shape.
    - `Period` materialises opening balances as `CarryoverSource` contributions, so "cash at the start of
      a month" is already a solved problem, not a replay.

    **Shape:** a pure `Domain/Forecasting/CashFlowForecast.Project(...)` that walks N months from the
    current closing balance applying due recurring income/bills, returning a per-month balance plus the
    first month it goes negative. Keep it **projection-only** (moves no money), like every other forecast
    here. Pairs with the existing Home "on track for" card, which already owns forward-looking answers.

    **Fold in, don't duplicate:** "predictive budgeting" (noted below) and the Wave-4 calendar +
    "set aside €X/month to meet due dates" idea (Session 26) are the same territory. One engine.

## Home density — the app's own kitchen-sink risk (added Session 37, from a competitor-honesty review)
16. **Home density — mostly already addressed; the header was the real issue. ✅ (Session 37).**
    ⚠️ **Correction:** the original write-up here ("~8 sections, five redundant 'how am I doing?' panels
    stacked") was drawn from the *old* handoff, not the current code. Reading Home as it actually is: the
    **milestones** strip is already a one-line link → Achievements modal; the **health score** is a one-line
    card → modal; the **deep-insights** panel is gone from Home entirely (it lives in the Health Score modal).
    Prior sessions already did most of the consolidation. The remaining sections — score card, urgent alerts,
    runway line, targets list, milestones line — are each compact and answer *distinct* questions, not five
    framings of one. **Don't cut good, distinct content to hit a reduction target.**
    - **The one genuine issue was the 5-number header** (Current / Free / **after bills** / **planned** /
      Saved) — which Session 37 itself bloated. **Fixed:** dropped the "planned" sub-line (it duplicated the
      urgent "budgets still plan €X but only €Y is free" alert), keeping the unique "after bills". Header is
      now 3 numbers + at most one context line. Verified live.
    - **Still open — audit the 4th savings-bucket kind (`Investment`).** A permanent extra toggle on the
      add/edit bucket modal + a Goals filter chip; unclear it earns its keep. But removing a `SavingKind`
      has migration weight (values get burned, like the reverted PlannedExpense kind) — **decide on real
      usage data, not a hunch.** Defer until there are users.
    - **Standing principle — consolidate, don't add:** every good idea has defaulted to a *new* panel/number
      rather than replacing one; the "avoid overwhelming" ethos is reactive cleanup, not a gate. Any future
      in-app assistant should *replace* stacked panels with an on-demand answer, not become a new section.

17. **AI assistant — "narrate, don't compute" (added Session 37).** An in-app assistant is compatible with the
    privacy wedge (#5) *and* the honesty brand (#4) **only** if it never does the math and never sees raw data.
    - **Always-on, safe kind:** natural-language **navigation** ("take me to my car fund"), **help/explainers**
      ("how does the runway work?") from static docs, and **narrating numbers the engine already computed** in
      plain language. The LLM produces *no* figures, so it can't hallucinate one — the deterministic engine
      (already correct) owns every number.
    - **Opt-in, off by default:** anything that summarises the user's actual money sends only **aggregated /
      anonymised** inputs ("groceries €430, up 12%"), never raw transactions, to a **no-training / no-retention**
      API. Consistent with the already-declined **LLM auto-categorisation** (inspectability).
    - **Do NOT:** pipe raw transactions to a cloud LLM for advice/categorisation — that becomes the thing we
      differentiate against (Beyond Budget) *and* risks stating a wrong number.
    - **Two payoffs:** a marketable position — *"AI that helps you understand your money without ever getting
      your raw data, and can't make up a number"* — and a lever against the Home kitchen-sink (item 16): one
      on-demand "how am I doing?" answer can **replace** stacked status panels, not add a 9th.
    - ⚠️ **Only claim what you can deliver.** On-device / anonymised + no-retention is harder than piping to a
      cloud LLM; a fig-leaf "private AI" badge would wreck the trust brand faster than shipping no AI. The
      privacy-absolutist core of the wedge may reject *any* cloud LLM touching finances — keep it opt-in.

**Considered and declined in the same review** (don't re-litigate without new information):
- **Multiple budgeting methodologies** (zero-based / 50-30-20 / envelope side by side) — **declined.** A
  method isn't a feature, it's the meaning of every number on screen; supporting several gives every
  projection, insight, achievement and test multiple readings. Same lesson as the reverted PlannedExpense
  kind ("kind is the wrong axis"), an order of magnitude larger. The app's current stance is deliberate and
  documented at `Period.FreeToAllocateAfter`: **budgets are advisory plans; only savings reserve cash.** If
  zero-based is ever genuinely wanted, **migrate** to it — don't run both.
- **LLM auto-categorisation** — **declined.** The Session-29 token-subset rules are deterministic, editable
  and inspectable (the user narrows a rule by toggling chips); that design exists specifically to fix the
  Revolut "Transfer to person X/Y" collapse. Swapping it for probabilistic parsing trades a system users can
  correct for one they can only complain about.
- **Fee analysers / tax-loss harvesting** — **declined.** Needs brokerage integration + jurisdiction-specific
  (BG/EU) rules, and edges into regulated personalised financial advice.
- **Performance pricing (a % of money "saved")** — **declined.** Perverse incentives, and taking a cut of
  savings edges toward being a financial product.

Also surfaced as real (not yet backlogged): **push notifications** — in-app reminders already exist
(`HomeReminders`, per-budget `AlertThreshold` at 80%); only *push* is missing, and it still needs a
PWA/service worker or native. And **family permissions** (read-only / allowance views for kids) — the only
missing piece in collaboration, which is the product's actual moat.

**Backlog status:** P0–P3 all cleared. Post-backlog items shipped since: **debt-lifecycle Phase 3**
(found already shipped) and **Plans → "expenses fund"** — a savings bucket carries a short list of
expected future costs (`PlannedCost`: label + amount + cadence `OneOff/Monthly/Quarterly/Yearly` + an
optional due-date for one-offs) and shows the flat **monthly set-aside** to cover them (a sinking fund:
recurring costs annualise, a dated one-off spreads across the months until due). This **replaced** the
short-lived **set-aside schedule + `Group`/🧷 Commitments** design (too complex, didn't fit the car-lease
example — Session 25), which had itself replaced a reverted "PlannedExpense" kind (kind = wrong axis).
Remaining un-backlogged: **multiple synced funds**, **predictive budgeting**, **bind-bucket-to-fund**,
push notifications (now a mobile native win). **Mobile is going full native, per platform — native
Android (Kotlin) first, then native iOS (Swift). No MAUI (decided 2026-07-19).** See
[docs/MOBILE.md](docs/MOBILE.md); deferred behind a verify + pre-mobile-changes pass, and gated on
the server-side-domain decision (dropping MAUI means the C# client domain can't come along).
