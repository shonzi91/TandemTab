# TandemTab — Steps before open beta

*What stands between the app as it is today and letting strangers use it. Written 4 August 2026 (Session 82),
after Debt R2 shipped and BUG-1 was fixed.*

| | |
|---|---|
| **Application** | TandemTab (https://tandemtab.com) |
| **Live revision** | `finapp-00270-z5t` (2026-08-05) — B1–B4 all shipped |
| **Scope of this doc** | Open **public** beta — an unrestricted sign-up link, not a handful of invited friends |
| **Effort key** | S = hours · M = a day · L = multi-day |

## The headline

**The product is ready; the operations around it are not.** Nothing in the feature backlog gates a beta —
[FEATURE-BACKLOG.md](FEATURE-BACKLOG.md), [UX-BACKLOG.md](UX-BACKLOG.md) and [PROJECTION-IDEAS.md](PROJECTION-IDEAS.md)
are all enhancements to a product that already does its job. What's missing is the machinery for finding out
when it *doesn't* do its job for someone who isn't you.

**Sequence:** B1 → B2 → B3 → B4 → the verification hour → open the door.

> **Status (2026-08-05): B1–B4 ✅ done and live** (`finapp-00270-z5t`), and **the verification hour is ✅ done**
> (all three checks below passed — one low-severity mini-donut finding noted, not a gate). What remains before
> the door is really just **the intake decision** (staged invites vs a public link — the Capacity section
> argues for staged) and then opening it. A lawyer's glance at the legal pages stays advisable but is not a
> hard gate.

Ideas that arrive after this was written go to
[After the door opens](#after-the-door-opens--parked-by-default) — see [the scope
freeze](#definition-of-done--and-the-scope-freeze) for the one test that promotes something to a blocker.

---

## Blockers

### B1 — Client-side error reporting · **M** · ✅ **DONE (Session 82, 2026-08-04)**
Built **in-house** rather than wiring Sentry, deliberately: a third-party error collector is a new sub-processor
to declare in the privacy policy (i.e. it lands straight in [B3](#b3--a-real-read-of-the-legal-pages--s--ideally-a-lawyers-glance)),
needs a DSN secret in a **public repo**, and means arbitrary exception context leaving the device — for an app
whose pitch is *"we sell software, not your data"* that's a poor trade for a UI. Cloud Logging already exists and
the deploy recipe already queries it. Sentry stays an upgrade path if volume ever demands grouping/alerting.

**What's there:**
- `POST /client-errors` — **anonymous** (a crash can happen pre-login, during registration, or *because* auth
  broke; a signed-in-only endpoint would drop the reports we most need), on its own rate-limit bucket, logged as
  structured fields to `FinApp.ClientError`. No table, no migration, no third party.
- **`ErrorScrubber`** (`FinApp.Contracts`) runs on **both** sides — client-side so raw values never leave the
  device, server-side so a stale client or forged POST can't write them into the logs. Redacts money amounts,
  domain-quoted user names, emails and long digit runs; keeps stack frames and the shape of the message.
  ⚠️ **This is not paranoia:** domain guards quote real values back at the user (*"That fund only holds
  €1,234.56…"*, *"A tag named “Mortgage” already exists."*), so an unscrubbed error pipeline would quietly
  become a channel for exactly the data the product promises never to move.
- **Four capture hooks:** an `ILoggerProvider` forwarding Error/Critical (this is the one that catches unhandled
  Blazor render exceptions — literally BUG-1's signature), plus JS `window.onerror`, `unhandledrejection`, and a
  MutationObserver on `#blazor-error-ui` (the JS path matters precisely when .NET is the thing that broke).
- Never throws, never blocks a render, de-dupes, capped per session.

**Read them with** (the app logs via the default text console, so entries land in `textPayload`, not
`jsonPayload` — match on the substring):
```
gcloud logging read 'textPayload:"FinApp.ClientError"' --limit 50 --freshness=1d
```

⚠️ **Still open:** nothing *alerts* — you have to go and look. A scheduled query or a log-based alert on
`FinApp.ClientError` is the natural follow-up and is cheap; without it this is a pull, not a push.

<details><summary>Original rationale (kept for the record)</summary>

Wire the Blazor WASM client to an error reporting service (Sentry or equivalent): unhandled exceptions, the
global error UI being shown at all, and failed API calls.

**Why this is number one.** [BUG-1](BETA-FINDINGS.md) — sign-out crashing the app and stalling it on *Loading…* —
sat in a **Critical** row of our own beta report for five days and was only caught because someone went looking.
It was a two-line fix. Today an exception in the client goes to that user's browser console and **nowhere else**.

In a closed circle you are the one hitting the bugs. In an open beta you are not. Without this, "open beta"
means *strangers silently hit crashes and leave, and you learn nothing.* Every other item on this list makes the
beta better; this one is the difference between a beta that produces information and one that doesn't.

**Minimum useful version:** capture unhandled exceptions + any render of `#blazor-error-ui`, with the account id
(never the financial data — see the privacy stance in [BACKLOG.md](BACKLOG.md) #17).
</details>

**Sequence note:** B1 and B2 are done. **B3 (legal read) is next** — and it should be done together with B2's
open sub-item, since both need the same decision: a real support/GDPR contact address.

### B2 — A way to tell you something · **S** · ✅ **DONE (Session 82, 2026-08-04)** — one sub-item open
**Why:** testers who can't report don't report, they churn. This is the only channel through which the
*subjective* problems (confusing, slow, didn't trust it) will ever reach you. B1 catches crashes; B2 catches
everything that isn't a crash.

**What's there:**
- One `<FeedbackForm>` component (stars + comment + per-submission publish consent) in **two homes**: the
  **landing page**, collapsed so it never competes with the CTA, and the **profile modal**.
- `POST /feedback` — **anonymous allowed**, and not merely for convenience: the landing page is where someone
  who looked and decided *not to sign up* can say why, which is feedback obtainable no other way. Shares the
  client-error rate-limit bucket.
- **Stored** in a `Feedback` table (migration-free `CREATE TABLE IF NOT EXISTS`, same pattern as
  `ConsentService`) **and** logged to `FinApp.Feedback`, so it shows up beside the errors where you're already
  looking. Stored rather than only logged because a log has retention limits and no consent flag — and
  [P1](#p1--landing-page-feedback--public-reviews--collection-see-b2--display-m) will need both.
- **`PublicConsent` defaults to false at every layer.** A review is never quotable unless that box was ticked
  for that review.
- **Comments are deliberately never scrubbed** (unlike error messages): this is text someone wrote on purpose
  for us to read. Redacting *"I can't see my €500 budget"* would destroy the report.
- A send failure surfaces an error and **keeps what they typed** — losing something someone took the time to
  write is a bad way to treat them.

**Read them with** (`textPayload`, not `jsonPayload` — see the B1 note; feedback is also stored in the
`Feedback` table, queryable directly):
```
gcloud logging read 'textPayload:"FinApp.Feedback"' --limit 50 --freshness=7d
```

✅ **Done — a support address.** `admin@tandemtab.com` is a real, active mailbox (confirmed 2026-08-05). It was
already the GDPR/data contact in `privacy.html`/`terms.html`; it's now also surfaced in the **profile modal**
("Your data & privacy" → "Questions, or a privacy request? admin@tandemtab.com"). No `support@` was invented.

### B3 — A real read of the legal pages · **S** (+ ideally a lawyer's glance) · ✅ **DONE (2026-08-05)** — lawyer's glance still advisable
`privacy.html` / `terms.html` (and the `.bg` variants) were read in full against the checklist. They already
name, and were confirmed to name:
- the **data controller** — TandemTab Company, Sofia, Bulgaria, contact `admin@tandemtab.com`. ✓
- a **retention period + what deletion does** — 30-day archive then permanent delete, stated plainly. ✓
- a **GDPR contact / rights route** — access, correction, erasure, restriction, portability, withdraw consent,
  plus the CPDP complaint route, all via `admin@tandemtab.com`. Export + delete are both built. ✓
- **bank-sync** — the Open Banking sections are commented out (not offered); import-only is described, and the
  feature is allow-list-gated to 2 emails so a beta tester can't reach it. ✓

**Gap found and fixed:** the policy was last updated 11 July, *before* B1 (error reporting) and B2 (feedback)
added two new processing activities. Both are now disclosed under "What we collect" (error reports —
scrubbed of financial detail; feedback — stored, only shown publicly on opt-in), with a retention line for
diagnostic logs. Date bumped to 5 August 2026, EN + BG.

**Still advisable (not a blocker):** a qualified lawyer's glance. The pages ship on our own judgement for now;
this is the one area about other people's legal rights, so a professional review remains worth doing.

### B4 — State what "beta" promises about their data, and stamp the cohort · **S** · ✅ **DONE (2026-08-05)**
**Shipped this session (defaults chosen when the owner didn't pick — flagged for review):**
- **The promise, on the landing hero + the sign-up screen:** *"In free beta — your account and everything in
  it carries through to launch."* (sign-up: *"Free while we're in beta — and your account and everything in it
  carries through to launch."*). EN + BG. **Two deliberate default choices** the owner can still change: (1)
  committed that **data survives to launch** (the obvious intent for a budgeting app; strongest trust signal —
  soften if a reset is possible); (2) said **"free while in beta" with NO future-pricing promise** — no
  grandfather commitment, sidestepping the unresolved €29.99/$39.99 until [MONETIZATION.md](MONETIZATION.md) is
  settled. Backup wording was left out (no claim we can't back).
- **★ The cohort stamp — built (the non-backfillable part).** `SignupService` writes one row to a `UserSignups`
  table (`UserId`, `JoinedAt`, `Cohort="beta"`) at account creation, on **both** the password and external
  (Google/Facebook) paths. Kept **off** the EF-mapped `User` entity on purpose — a side table needs no EF
  migration (SQLite) or raw ALTER on the live Postgres `Users` table (`EnsureCreated` won't evolve it), matching
  how every other per-user concern is stored. `JoinedAt` also answers P2's "sign-ups over time". Confirmed there
  was **no** existing creation timestamp anywhere (`Entity` has only `Id`), so this is genuinely the first record
  of who joined when. +1 server test.

Original decision list (kept — the owner may want to revisit the wording):
- **Will accounts and data survive to launch?** (If yes, say it. If you might reset, say *that* — loudly.)
- **Is it backed up?** What happens if a migration goes wrong?
- **Will it stay free for people who join now?** (Ties into [MONETIZATION.md](MONETIZATION.md)'s
  "grandfather early users" / beta-tester offer — a promise made here is a promise you keep.)

**Why:** beta users forgive bugs. They do not forgive losing a month of budgeting they entered by hand. Silence
on this reads as "we haven't thought about it," which is worse than an honest "we may reset once."

**⚠️ The one bit that cannot wait — stamp the beta cohort at sign-up.** A `BetaCohort` flag (or just trusting
`CreatedAt` against a cut-off date) on the user row, written when the account is created. This is the only part
of the whole monetization story that is **expensive to retrofit**: if you promise grandfathered pricing to
"people who joined during beta" and never recorded who that was, you are reconstructing it later from whatever
timestamps happen to exist, or breaking the promise. Everything else about billing can be built at leisure
(see [P4 below](#p4--monetization-behind-a-flag--m)); this cannot. It is one column and one write.

---

## After the door opens — parked by default

**Scope rule: anything that arrives after this doc was written goes here, not above, unless it makes the beta
*unsafe or unmeasurable*.** These four were requested on 2026-08-04, all of them reasonable, none of them a
reason to delay. They are specced here so they're not lost — and so they stop competing with the four blockers.

### P1 — Landing-page feedback + public reviews · **collection: see B2 · display: M** · ⬜
Two different features fused into one request; they should ship at different times.

- **Collecting** a star rating + a comment **is B2** — build it there, on the landing page *and* in-app. One
  intake, two entry points. Store rating + text + account id + a `PublicConsent` flag.
- **Displaying** them (the star average + a testimonial carousel on the landing page) — **park until there is
  something real to show.** On day one the carousel is empty, and an empty or seeded testimonial strip on a
  product whose whole pitch is *"we sell software, not your data"* costs more trust than it buys. Ship it when
  you have ~10 genuine reviews you'd be happy to quote.
- **Three constraints for when it does ship:** (1) **explicit opt-in per review** before anything is shown
  publicly, with the name shown as the user chooses it — republishing a beta tester's words without asking is
  exactly the kind of thing this brand can't afford; (2) a **self-hosted star average is self-attested** and
  reads as marketing, not proof — it will never carry the weight an App Store rating does, so don't over-invest;
  (3) **beta-era ratings will be low** and you can't reset them — consider collecting from the start but only
  displaying ratings given after launch.

### P2 — Owner-only admin dashboard (users + activity) · **M–L** · ⬜
Real value: it's how you learn whether anyone is actually using this. Two hard constraints.

- **Metrics, not surveillance.** Sign-ups over time, last-active, counts (accounts / periods / expenses logged),
  retention, error counts from [B1](#b1--client-side-error-reporting--m-). **Never other people's financial
  data.** Being able to read a stranger's budget would contradict the privacy wedge, the marketing copy, and
  most likely `privacy.html` itself. Decide this at design time, because it's the kind of thing that gets added
  "just to debug something" later.
- **Server-side authorization, not a hidden route.** An endpoint that enumerates users is the single
  highest-value target in the app. Role check in the API, not `@if` in the UI.
- **Start with a SQL query, not a UI.** On day one, "how many signed up, how many logged an expense, who's
  come back" is one query against the DB you already have. That gets ~80% of the value for ~2% of the work,
  and it tells you what the dashboard should show before you build it.

### P3 — Money-over-time chart (income / expenses / saved / balance) · **M** · ⬜
One chart, four series, over: current period · 3 · 6 · 12 months · all time · custom range.

**Where it goes — recommendation: a fourth Spending sub-tab, "Trends"**, beside By date / By budgets /
Breakdown.
- Spending already **is** the analysis surface, and the switch is already the fixed header on every view (S74).
- **Breakdown already owns the range control** — presets plus two directly-editable date inputs (S74) — so the
  "3 / 6 / 12 / all / custom" selector is a reuse, not a new concept.
- It answers a genuinely distinct question. [UX-BACKLOG #12](UX-BACKLOG.md) was closed on the premise that each
  Spending view answers something the others don't: By date = *when*, By budgets = *vs plan*, Breakdown =
  *where it went*. Trends = ***how it's moving***. That premise survives a fourth tab; it wouldn't survive a
  fourth pie chart.
- **Rejected:** Home (violates the standing "avoid overwhelming sections" preference — Home is a glance
  surface); inside the Health-score modal (this is a primary analytical view, not a drill-in).
- **Consolidate, don't add** ([BACKLOG.md](BACKLOG.md) #16): this should **absorb** the per-category trend
  sparklines currently in the Health modal (`InsightsService.BuildMiniTrends` / `TrendSeries`), not become a
  third place that draws trends.
- **Build on what exists:** `InsightsService.BuildMiniTrends`, `TrendSeries`, `SavingsReportService`, and the
  per-period figures the hero already computes. Mostly assembly, not new maths.
- ⚠️ **Worthless without multi-period data** — it will look broken on a fresh account. Needs an empty state
  ("come back after a couple of months") and it is the single strongest argument for shipping it *after* beta
  users have accumulated some history rather than before.

### P4 — Monetization behind a flag · **M** · ⬜
Build the **rails** now, flip the **switch** later — which is exactly what [docs/BILLING.md](docs/BILLING.md)
already recommends ("build the rails early; they're cheap and migration-independent").

- A single server-side `Monetization:Enabled` config flag (Cloud Run env var, so flipping it is a revision
  update, no deploy of new code).
- Entitlement checks at the gate points **now**, all returning "entitled" while the flag is off: shared
  accounts, statement-import limits, history window, debt planner, Free caps
  ([MONETIZATION.md](MONETIZATION.md) has the table).
- The **beta-cohort stamp is [B4](#b4--state-what-beta-promises-about-their-data-and-stamp-the-cohort--s-)** —
  that part is a blocker precisely because it can't be backfilled.
- **Not now:** Stripe/Paddle integration, paywall UI, trial timer. Those are the expensive half and the standing
  decision is still *monetize after mobile + push*. A flag with no billing behind it is a day; a flag with
  billing behind it is weeks.
- ⚠️ **Reconcile the price first.** [MONETIZATION.md](MONETIZATION.md) says €29.99/yr, `docs/BILLING.md` says
  $39.99/yr. Harmless while nothing is shown; embarrassing the moment a number reaches a user.

---

## Deliberately NOT blockers

Recorded so they don't get re-litigated at the door.

| Item | Why it can wait |
|---|---|
| **Billing / monetization** | The standing decision ([docs/BILLING.md](docs/BILLING.md)) is *monetize after mobile + push*. A free beta is correct; charging for a beta would be worse than not. ⚠️ [MONETIZATION.md](MONETIZATION.md) (€29.99/yr) and [docs/BILLING.md](docs/BILLING.md) ($39.99/yr) still disagree on price — reconcile before any pricing is ever shown, not before beta. |
| **Android** | It's a real thin client but is **~13 sessions behind web** (none of S70, S74–S82: Home hero/donut, bell grouping, period-lifecycle fixes, Debt R1/R2, F3, a11y). Ship the beta **web-only**; a stale mobile app is a worse first impression than no mobile app. Android parity is a post-beta track and beta feedback should steer which parts of it matter — see [docs/MOBILE.md](docs/MOBILE.md). |
| **iOS** | **On hold (2026-08-04).** The product is web + Android; iOS is revisited only once that pairing is stable and there's real demand. |
| **The feature backlog** | F1/F2/F4–F7, UX #10, projections D1–D8, the on-device AI assistant. Every one is an enhancement, none is a gap. |
| **Web-thinning (Path B Phase 2/3)** | Pure refactor, invisible to users. It also lost its forcing function when native Android started ahead of the gate — worth asking whether it's still worth doing at all, separately from beta. |
| **The `Investment` bucket-kind audit** | [BACKLOG.md](BACKLOG.md) #16 explicitly says decide on real usage data. A beta is how you *get* that data — this is an argument for opening, not for waiting. |

---

## The verification hour (before opening, not after)

Three things that are built and believed-correct but never seen working with real data. All cheap.

- ✅ **The S80 "Saved toward goals" Breakdown slice** (done 2026-08-05). Created a Holiday bucket (€500), applied
  €200 to the goal ("Apply to a goal" = the disbursement), and the Breakdown donut showed **Food €50 (20%) +
  "Saved toward goals" €200 (80%)** with the target icon, a **€250 donut total that exceeds the €50 Spent**
  exactly as designed (slice included in the donut, excluded from Spent). Hero Spent stayed €50. Works.
- ✅ **Period lifecycle end-to-end** (done 2026-08-05, as a sequence): edited the period end into the past →
  **"period ended — start next month" banner** (S79 #1); **started next month** → new period, correct carry
  (€2,450 free carried, €300 stays earmarked), no crash; **removed the latest period** → **no crash** (S79 #3
  fix holds), confirm dialog present, clean drop-back; **switched accounts** (created a 2nd, round-tripped) →
  no crash, state preserved. All console-error-free.
  - ⚠️ **One finding to check:** right after "Start next month", the new period's hero correctly read **Spent
    €0**, but the Home **"Where your money went" mini-donut still showed €250** (the *previous* period's Food +
    Saved). The mini-breakdown looks like it isn't re-scoping to the newly-active period immediately after a
    roll-over. Low severity (display-only, self-corrects), but worth a look — the hero and the donut should
    agree on which period they describe.
- ✅ **A fresh-account walk-through as a stranger would do it** (done 2026-08-05, local throwaway): register →
  accept terms → create account → onboarding → first income (€3,000) → first expense (€50 Food). Clean the
  whole way — consent gate, starter categories/funds pre-seeded, hero + F3 day-left + health score + the
  "where your money went" donut all update, onboarding collapses to the slim "Finish setup" link after
  income+expense. No errors, nothing assumes prior state. **Not yet exercised in this walk:** first *budget*
  and first *goal* (the other two onboarding steps) — low risk but unconfirmed.

---

## Capacity — an open question, not an answer

⚠️ **Unverified.** Everything above concerns behaviour, not load. Nobody has looked at Cloud Run concurrency
limits, the Neon connection ceiling, the per-IP rate limiters (only the "invite" policy is known-tuned), or what
happens when 200 people register in an hour.

This is a strong argument for a **staged invite list over a public link** — open in waves, watch, widen. It gets
you real users without betting the first impression on untested capacity, and it makes B1's error stream
readable instead of a firehose.

---

## Definition of done — and the scope freeze

Open beta is ready when: **B1–B4 are done, the verification hour is clean, and the intake shape (staged vs
public) is chosen.** Nothing else on any backlog is a precondition.

**The freeze.** This list is closed. A new idea gets added to *this* doc only if it makes the beta **unsafe**
(someone's data or rights at risk) or **unmeasurable** (you couldn't tell it went wrong). Everything else —
however good, however small — goes to [After the door opens](#after-the-door-opens--parked-by-default) or the
existing backlogs. The four items in that section were each a five-minute idea and roughly two weeks of work;
that ratio is why a going-live date moves.

**The test to apply to the next one:** *"If we open the beta without this, does someone get hurt, or do we fail
to notice something breaking?"* If neither — it's a P-item, and it will be more informed after real users
anyway.
