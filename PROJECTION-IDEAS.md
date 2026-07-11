# Projection method ideas — debt payoff & investment growth (and savings)

A menu of **additional** projection/what-if methods for the Debt/Savings tab buckets, to pick from in a
future session. All are **read-only projections** (the money model is never touched — same stance as the
current ones). Each entry notes: what it is · inputs (and whether we already have them) · math sketch ·
where it lands in the UI · rough effort.

## Where things stand today (so you know what NOT to re-invent)
- **Debt buckets** — `FinApp.Domain.Forecasting.LoanForecast`: `PayOff(balance, apr, monthlyPayment)`,
  `SimulateExtra(...)` (installment vs installment+extra), `PlanPayoff(loans, extra, Avalanche|Snowball)`
  (multi-debt planner). UI: "Payoff projection" modal, multi-debt "Payoff plan", "N months ahead", the
  progress-over-time sparkline (`SavingsReportService.DebtBalanceHistory`).
- **Investment buckets** — `FinApp.Domain.Forecasting.InvestmentForecast.Project(present, annualRate%,
  termYears, compoundsPerYear, monthlyContribution)` (monthly-stepped compound FV). UI: "Growth projection"
  modal ("just what's invested now" vs "adding more each month").
- **Common/goal buckets** — goal-date at pace (`EffectiveSavingPace` = planned ?? demonstrated).
- **Shared inputs already available:** `EffectiveSavingPace(bucketId)`, the bucket's accumulated balance
  (`SavingBucketSaved`), debt `Original/Balance/Rate/Installment`, investment `Rate/TermYears/Compounds`.

Legend for effort: **S** ≈ a helper + a few UI lines · **M** ≈ new forecast method + modal section ·
**L** ≈ new inputs/fields or heavier math (Monte Carlo, solvers).

---

## Debt / payoff buckets

### D1. One-off lump-sum / windfall payments (snowflakes) — **M**
Apply a single extra payment at a chosen month (tax refund, bonus) and show the new payoff date + interest saved.
- Inputs: existing + `lumpAmount`, `atMonth`. Math: run `PayOff` month-loop, subtract the lump at `atMonth`.
- UI: a small "+ one-off payment" row in the payoff modal (amount + "in N months"); compare vs baseline.

### D2. Bi-weekly / accelerated schedule — **S**
Pay half the installment every 2 weeks ⇒ 26 half-payments = 13 monthly payments/year. Show the acceleration.
- Math: effective monthly payment × 13/12, feed to `SimulateExtra`. UI: a toggle "Pay bi-weekly".

### D3. Round-up payments — **S**
Round each installment up to the nearest €10/€50/€100; the rounding delta is the "extra".
- Math: `extra = roundUp(installment, step) − installment` → `SimulateExtra`. UI: a "round up to" chip group.

### D4. **Inverse: target-date solver** — "be debt-free by {date}, what payment?" — **M**
Given a target payoff month, binary-search the monthly payment that clears it by then.
- Math: bisect `payment` in `PayOff(...).Months <= targetMonths` (monotonic). UI: date picker → "You'd need
  €X/mo (€Y extra on top of the installment)".

### D5. Affordability / "most I can pay" — **S**
"If I can only put €X/mo total at this, when am I clear — or does it never clear?" (surfaces the
never-clears case honestly). Basically the existing `PayOff` with a user-entered total payment.

### D6. Refinance / rate-change scenario — **M**
Model a new APR (and optionally new term) and show interest saved vs staying. Also a stress test (APR +2%).
- Inputs: `newApr`, optional `newInstallment`. Math: `PayOff` at each APR, diff. UI: "What if I refinanced to
  __%?" line in the payoff modal.

### D7. Credit-card minimum-payment trap — **M** (needs a field)
For revolving debts the "minimum" is a % of the balance (e.g. 2–3%) and *falls* as the balance drops, so
minimum-only takes decades. Model `minPayment = max(floor, pct × balance)` and contrast with a fixed payment.
- New field on debt bucket: `MinPaymentPercent` (+ a `PayOffPercentMin` method). High "aha" value.

### D8. Full amortization curve + interest area — **M**
Beyond the shrinking-balance sparkline: a month-by-month balance curve with the cumulative-interest area, and
a principal-vs-interest split per year. `PayOff` already computes the path — just emit the series.

### D9. Payment holiday / skipped month — **S**
"What if I skip a payment?" interest still accrues; show the payoff extension + extra interest. Inverse of D1.

### D10. Extra-payment as a "guaranteed return" — **S** (framing, cross-bucket)
Reframe an extra debt payment as a guaranteed return equal to the APR ("clearing this 18% card = an 18%
risk-free return"). Pairs with the investment buckets (see X10) to answer "pay debt or invest?".

---

## Investment / growth buckets

### X1. Inflation-adjusted (real) value — **S**
Show FV in today's money alongside nominal. `real = nominal / (1+inflation)^years` (default ~2–3%, editable).
"~€104k in 20y ≈ €63k in today's money." Cheap honesty win; reuse `Project` then discount.

### X2. Bear / base / bull bands — **S**
Run `Project` at `rate−k`, `rate`, `rate+k` (e.g. k = 3pts) and show a low/expected/high range instead of a
single deterministic number. Communicates uncertainty without heavy math.

### X3. **Monte Carlo** percentile fan — **L**
Draw random annual (log-)returns from `mean=rate`, `volatility=σ` (new field, default per asset class), run
~1–5k paths, plot the 10th/50th/90th percentile bands + "probability of reaching €X". The honest way to show
market risk. Pure client compute; cap paths for perf.

### X4. **Inverse: target-value solver** — "reach €X by {date}, how much/month?" — **M**
Bisect `monthlyContribution` so `Project(...).FutureValue >= target`. UI: target amount + date → "invest
€Y/mo". Mirror of D4.

### X5. Time-to-target — **S**
"How long until this hits €X at the current pace?" step the monthly loop until `balance >= target`. Reuses the
`Project` loop with an early exit.

### X6. Withdrawal / drawdown ("what income will it give me?") — **M**
Once it reaches the horizon, show a sustainable withdrawal (4%-rule: `annualIncome = 0.04 × FV`, or months it
lasts at €X/mo). Turns a number into "≈ €350/mo for life" — very motivating for long-horizon buckets.

### X7. Contribution step-up (escalating) — **S**
Increase the monthly contribution by `g%`/year (salary growth). Math: bump the per-month contribution every 12
steps in the loop. UI: "increase what I add by __%/yr".

### X8. Fees / expense-ratio drag — **S**
Subtract an annual fee (%) from the effective rate (`netRate = rate − feePct`) and show gross vs net. New
optional `FeePercent`. Small change, meaningful honesty (a 1% fee is huge over decades).

### X9. Lump-sum vs dollar-cost-averaging — **S**
Compare investing a lump today vs spreading it over N months. Two `Project` runs; show the (usually small)
difference. Good teaching moment.

### X10. Invest vs pay-down-debt comparison — **M** (cross-bucket)
Given a spare €X/mo, compare: invest it at the investment rate vs throw it at the highest-rate debt (D10). Show
which ends richer at a horizon. Bridges the two bucket types; needs both a debt and an investment present.

---

## Savings / common goal buckets (bonus — mostly mirror the above)
- **S1. Target-date & required-monthly solvers** (like D4/X4): "hit €X by {date} → save €Y/mo", and the
  reverse "€Y/mo → reached on {date}". Mostly there via `EffectiveSavingPace`; add the inverse. — **S**
- **S2. Round-up / spare-change savings** (like D3): project "if every expense rounds up to the nearest €1".
  Needs a spend estimate. — **M**
- **S3. Escalating "52-week"-style challenge** projection (like X7). — **S**
- **S4. Emergency-fund coverage**: "this bucket covers N months of your average outgoings" (we already compute
  avg outgoings for the "Nest egg" achievement). — **S**

---

## Cross-cutting patterns worth building once
1. **Inverse solvers** (target date/value → required payment/contribution) — a single bisection helper reused
   by D4, X4, S1. Both `PayOff` and `Project` are monotonic in the payment/contribution, so bisection is safe.
2. **Scenario ranges** (low/expected/high) — a thin wrapper that runs any forecast at 3 rates. Powers D6, X2.
3. **Real (inflation-adjusted) values** — one discount helper reused anywhere a future amount is shown (X1,
   long-horizon debt too).
4. **Fees/taxes toggle** — net-rate adjustment reused by X8 and (if wanted) a tax-drag variant.

## Suggested first picks (impact ÷ effort)
- **X1 inflation-adjusted** + **X2 bear/base/bull** — cheap, and make the investment number honest.
- **D4 / X4 inverse solvers** — "what do I need to do?" is the question users actually have; one shared helper.
- **D7 credit-card minimum trap** — high "aha", needs one new field.
- **X6 withdrawal income** — turns a scary-big number into a relatable monthly income.
- **X3 Monte Carlo** — the standout feature if you want the investment projection to feel serious; save for
  when there's appetite for the heavier build.
