# TandemTab — Steps before open beta

*What stands between the app as it is today and letting strangers use it. Written 4 August 2026 (Session 82),
after Debt R2 shipped and BUG-1 was fixed.*

| | |
|---|---|
| **Application** | TandemTab (https://tandemtab.com) |
| **Live revision** | `finapp-00267-jvn` (2026-08-04) |
| **Scope of this doc** | Open **public** beta — an unrestricted sign-up link, not a handful of invited friends |
| **Effort key** | S = hours · M = a day · L = multi-day |

## The headline

**The product is ready; the operations around it are not.** Nothing in the feature backlog gates a beta —
[FEATURE-BACKLOG.md](FEATURE-BACKLOG.md), [UX-BACKLOG.md](UX-BACKLOG.md) and [PROJECTION-IDEAS.md](PROJECTION-IDEAS.md)
are all enhancements to a product that already does its job. What's missing is the machinery for finding out
when it *doesn't* do its job for someone who isn't you.

**Sequence:** B1 → B2 → B3 → B4 → the verification hour → open the door.

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

**Read them with:**
```
gcloud logging read 'jsonPayload.logger="FinApp.ClientError"' --limit 50 --freshness=1d
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

**Sequence note:** B1 is done, so **B2 is next.**

### B2 — A way to tell you something · **S** · ⬜
A feedback route from **both** the landing page (before sign-up) and the profile modal (after) — a star rating
plus a free-text comment, posted to an endpoint. Store rating + text + account id + a `PublicConsent` flag (see
[P1](#p1--landing-page-feedback--public-reviews--collection-see-b2--display-m)). Plus a support address visible
somewhere that isn't the footer of a legal page.

**Why:** testers who can't report don't report, they churn. This is the cheapest item on the list and it is the
only channel through which the *subjective* problems (confusing, slow, didn't trust it) will ever reach you.
B1 catches crashes; B2 catches everything that isn't a crash.

### B3 — A real read of the legal pages · **S** (+ ideally a lawyer's glance) · ⬜
`privacy.html` / `terms.html` (and the `.bg` variants) exist and the privacy posture is a genuine strength —
but before opening to the public, confirm they actually name:
- the **data controller** (who, and a real contact address),
- a **retention period** and what deletion does (the grace-period delete already exists — say so),
- a **GDPR contact / rights route** (access, export, erasure — export and delete are both built, so this is
  mostly documenting what's true),
- what the **bank-sync** integration shares and with whom, if a beta tester can reach it at all
  (currently gated to 2 allow-listed emails, so probably not — confirm).

**Why it's a blocker:** you will be collecting financial data from EU residents on a public sign-up. This is the
one item on the list that is about other people's rights rather than product quality, and the one worth having
someone qualified glance at rather than shipping on our own judgement.

### B4 — State what "beta" promises about their data, and stamp the cohort · **S** · ⬜
Decide, then put it on the landing page and the sign-up screen in one sentence each:
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

- ⬜ **The S80 "Saved toward goals" Breakdown slice** — shipped, served-bytes verified, **never eyeballed**.
  Needs a period containing a real bucket disbursement. Log one and look at it.
- ⬜ **Period lifecycle end-to-end** — start next period, close one, remove the latest, switch accounts.
  S79–S81 landed seven separate fixes here and each was verified in isolation, not as a sequence.
- ⬜ **A fresh-account walk-through as a stranger would do it** — register → accept terms → create account →
  onboarding checklist → first income → first expense → first budget → first goal. Watch for anything that
  assumes prior state.

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
