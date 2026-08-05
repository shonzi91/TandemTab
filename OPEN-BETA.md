# TandemTab — Steps before open beta

*What stands between the app as it is today and letting strangers use it. Written 4 August 2026 (Session 82),
after Debt R2 shipped and BUG-1 was fixed.*

| | |
|---|---|
| **Application** | TandemTab (https://tandemtab.com) |
| **Live revision** | `finapp-00277-p5t` (2026-08-05) — B1–B4 + P1/P2/P3/P4 shipped; **lifetime-Pro allowance of 100**, real Free/Pro gating (post-cap users gated during beta), Pro crowns + plan comparison; **R1 feature backlog cleared** |
| **Scope of this doc** | Open **public** beta — an unrestricted sign-up link, not a handful of invited friends |
| **Effort key** | S = hours · M = a day · L = multi-day |

## The headline

**The product is ready; the operations around it are not.** Nothing in the feature backlog gates a beta —
[FEATURE-BACKLOG.md](FEATURE-BACKLOG.md), [UX-BACKLOG.md](UX-BACKLOG.md) and [PROJECTION-IDEAS.md](PROJECTION-IDEAS.md)
are all enhancements to a product that already does its job. What's missing is the machinery for finding out
when it *doesn't* do its job for someone who isn't you.

**Sequence:** B1 → B2 → B3 → B4 → the verification hour → open the door.

> **Status (2026-08-05): everything on this list is ✅ done and LIVE on `finapp-00271-4hw`.** B1–B4 shipped, the
> **verification hour is done** (its one low-severity mini-donut finding is fixed and deployed), and — beyond the
> original scope — the owner pulled three of the four parked features into the beta: **P3 (Trends chart), P2
> (admin dashboard), and P4 (monetization rails, flag OFF)**, all built, browser-verified, and now deployed. P1
> (public reviews display) stays parked.
>
> **Update (Session 85):** **P1 (public reviews) is now built and live too** — a landing carousel behind *two*
> gates, author consent **and** moderator approval, because `/feedback`'s write side is anonymous and consent
> alone would let a stranger put text on the marketing page. It renders nothing until a row is deliberately
> approved (`UPDATE "Feedback" SET "Approved"='1' WHERE …`), so it is currently empty by design.
>
> **⚠️ P4 is live but dormant, and that is intended:** `Monetization__Enabled` is **not set**, so there is no
> plan UI, no pricing on the landing page, no gates, and every account reads "unlimited". `Admin__Emails`
> **is** now set (the owner's address, held only as a Cloud Run env var — this repo is public). Anyone hunting
> for the plan/pricing surfaces on tandemtab.com will not find them; that is the flag being off, not a failure.
>
> **Update (Session 89): the owner set a seven-phase plan before promotion** — see
> [The road to promotion](#the-road-to-promotion--the-ordered-plan-set-2026-08-05-session-89). The beta itself
> was never blocked on any of it; the *promotion* of the app now is. The intake decision (staged invites vs a
> public link) remains the last owner call, and a lawyer's glance at the legal pages stays advisable.

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

## The intake decision — now answered: an open link with a lifetime-Pro allowance

**Resolved 2026-08-05 (Session 86), revised Session 87.** The Capacity section below argued for staged invites.
What shipped is an **open public sign-up link that never turns anyone away** — the `Beta__Cap` (**100**) is a
**lifetime-Pro allowance, not a door**. `Beta__Cap` / `Beta__CountFrom` / `Beta__TestEmailPatterns` are Cloud Run
env vars, so changing the allowance is a revision update, not a deploy.

- The **first 100** real sign-ups (from `Beta__CountFrom` = 2026-08-05) are stamped cohort `beta`, are
  **grandfathered to Pro for life**, wear the **Pro tag + crown** during beta, and are **ungated** (unlimited).
- **Everyone after the cap joins on cohort `free`** — still welcome, and during beta they get the **real Free
  experience**: Pro features are gated with an upgrade prompt (billing is off, so the prompt says Pro is coming
  after beta). This is deliberate — it lets post-cap members see what Free looks like, and it's how the paywall is
  exercised before monetization is ever switched on.
- **The gates key on the resolved PLAN, not the global monetization flag.** `unlimited`/`pro` pass everything;
  `free` is gated. The flag now only governs the billing *surfaces* (checkout, public pricing, the plan panel),
  which stay off during beta. So flipping `Monetization__Enabled` later turns on *selling*, not *gating*.
- **Registration never blocks.** The cohort is decided at write time from the seat count, so the boundary can't be
  forgotten by a later read. Existing pre-cap users don't consume the allowance and stay `unlimited`.
- **Accounts predating the cohort stamp count as beta.** Stamping only began in Session 83 (B4), so earlier
  accounts have no row at all. A missing row therefore *means* beta — requiring an explicit `beta` stamp had the
  effect of demoting the earliest members to a gated Free, which is the opposite of the intent (fixed Session 88).
- **Our own test accounts never take a lifetime seat** — an address matching `Beta__TestEmailPatterns` (live:
  `+test;@test.local;@example.com`) is stamped cohort `test` and lands on Free (the natural way to see the gated
  experience). **Register test accounts as `you+test1@gmail.com`** — Gmail delivers aliases to the same inbox, so
  email verification still works.
  - ⚠️ **A Google/Facebook test sign-in cannot use an alias** (the provider supplies the real address), so it will
    land in the lifetime cohort. Fix it afterwards in **Admin console → Admin — cohort**, which re-classifies any
    account by email (`beta` / `free` / `test`). That panel is also the way to reclassify accounts created before
    the patterns existed, and it's worth deciding whether the owner's own personal accounts should be `test` so
    the tester metrics aren't self-inflated.
- The landing page **no longer advertises the remaining count** — a live "N spots left" reads as scarcity pressure
  however it's worded, and it dates the page the moment the allowance fills. The allowance still works; it just
  isn't a marketing device.

**Opening the door is still an explicit owner action** — the lifetime allowance makes it safe, not automatic.

## The road to promotion — the ordered plan (set 2026-08-05, Session 89)

**Owner's sequence.** Seven phases, in this order, ending with actually promoting the app. The ordering is
deliberate and mostly self-justifying: **R1 freezes the feature set**, which is the precondition every later
phase leans on — R5's landing rewrite and paywall pass are explicitly "do this LAST" work, and doing them
against a moving feature set is work that gets redone. R4 lands before R7 for the one reason that matters:
**promotion is the traffic spike, and R4 is what makes the database survive one.**

| # | Phase | Ends when | Size |
|---|-------|-----------|------|
| **R1** | Clear the feature backlog | The feature set is declared **frozen** | L · ✅ **done 2026-08-05** |
| **R2** | Android catch-up + theme verification | Android at web parity; light/dark swept on **both** surfaces | L · web half ✅ |
| **R3** | AI assistant | See the scoping note — the whole of it is not one phase | L+ |
| **R4** | Railway migration (hosting **and** DB) | Serving from Railway, Neon + Cloud Run retired | M–L |
| **R5** | Landing, terms, privacy + Pro-split final verification **+ billing go-live** | The page describes the real product; the paywall is settled **and can actually take money** | M–L |
| **R6** | SEO | Indexed, measurable, bilingual | S–M |
| **R7** | Promote | The door is open | — |

### R1 — Clear the feature backlog
- **[FEATURE-BACKLOG.md](FEATURE-BACKLOG.md) F1–F7**: quick add (S), tag→category (S–M), left-to-spend-today (S),
  round-ups (M), settle-up (M), shared-goal celebration (S), weekly recap (S–M).
- **[UX-BACKLOG.md](UX-BACKLOG.md) #11 accessibility** is 🔨 in progress and the one item here with a compliance
  edge — a finance app whose bell announces "3" is a real defect, not polish. **#10 (pin a focus debt)** is
  deferred by design until users have long lists; clearing it means *writing the decision down*, not building it.
- **[BACKLOG.md](BACKLOG.md) #16's open sub-item** (audit the `Investment` saving kind) says in its own text to
  decide on **real usage data, not a hunch** — so it cannot be cleared before there are users. Stop counting it.
- **Close the verification debt in the same pass:** the S88 chart animations, the S85 Trends axis/hover and the
  Spent transfers sub-line have **never been seen with real data** — all three are build-clean and eyeballed by
  nobody.
- ⚠️ **Guardrail:** FEATURE-BACKLOG's own rule — every item opt-in or invisible-until-useful — plus the standing
  preference for a property/rollup over a new section. **F3 and F7 are the two that will want a new Home panel.**
- **Exit: an explicit "feature set frozen" line.** If this phase trails off instead of ending, R5 has no ground
  to stand on.

> **✅ R1 DONE (Session 89, live in `finapp-00277-p5t`).** F1/F2/F4/F6/F7 built; **F3 was already shipped** and
> merely never ticked; **F5 dropped by the owner** — shared accounts pool income, so there is no per-person balance
> to settle. UX #10 and BACKLOG #16 stay deferred by their own terms (both need real usage data).
> **⚠️ The feature set is NOT frozen yet** — R3's assistant is still to come, and R2 may surface parity gaps. The
> freeze line is declared **when R3 lands**, and only then does R5 have stable ground. See
> [FEATURE-BACKLOG.md](FEATURE-BACKLOG.md) for what each item actually became.
> **Verification debt closed:** none. The S88 chart animations and F6's shared-account "together" line are both
> still **unseen with real data** and carry forward.

### R2 — Android catch-up + theme verification
- Android's last commit is **2026-07-30**; the web has had **S74–S89** since. That's a diff-driven catch-up
  against the web's session log, not a rewrite. Debt R2's grouped *edit* is still unbuilt there.
- Mirror the web's section layouts, cards and colours; differ only in **nav (bottom bar)** and **floating buttons**.
- **★ The gap is now measured, not estimated (Session 90).** Counting sessions behind was the wrong instrument —
  it ages the moment web ships. For a thin client there is an exact one: **the endpoints the server exposes that
  `TandemTabApi` never calls**, since it cannot render what it does not fetch. The table lives in
  [docs/MOBILE.md](docs/MOBILE.md#-the-parity-gap-measured-session-90-2026-08-05) and **is the R2 backlog**.
  - ⚠️ **Four of the gaps make Android a *different* product, not a smaller one:** a phone-only user cannot
    **start a new period**, cannot **create a savings goal or debt**, has **no debt features at all**, and cannot
    **share an account** — the last being the feature Pro is sold on.
  - ⚠️ **"Just UI" is usually wrong here.** Half of the Home hero could not be built until the *server* grew the
    figures: they lived in `BudgetingState`, i.e. in the domain the thin client deliberately does not carry.
    Check what the endpoint actually returns before sizing any row in that table.
- **Session 90 closed:** the Home money hero (all four tiles — safe-to-spend with "after bills" and **F3
  "left to spend today"**, saved with its money-in rate, spent with the transfers sub-line, money in with
  carry-over) and the **rotating over-budget alert strip**. Both browser-verified against a real seeded account
  in **both themes**. Android's own light/dark rendering was checked in the same pass and needed no fixes.
- **Sweep light/dark on the web too, not just Android.** S88 shipped a dark-theme crown colour that silently
  never applied (a leading `::deep` compiles to a selector nothing matches). The web half is the cheaper half
  and has already produced one real bug.
  - **✅ The web half is done (Session 89).** Dark went from 8 sub-4.5:1 findings to 3, and those 3 are
    white-on-brand-green / the avatar palette, identical in light. Three colours had **never been given a dark
    value** — the widest was every chip-picker label in every modal. The recurring shape (a light rule listing
    several selectors whose `html.dark` counterpart covers only some of them) now has a detector:
    **[tools/pairscan.js](tools/pairscan.js)** — run it after adding themed CSS.
  - ⚠️ **Light theme is not "broken" but is faint by design:** 32 sub-4.5:1 findings, all the app's own palette —
    brand green `#13a06e` at 3.34:1, secondary greys at 2.4–3.0:1 on white. Deliberately **not** changed here:
    that is the product's visual language and belongs to **[UX-BACKLOG #11](UX-BACKLOG.md)** (accessibility), not
    to a theme sweep. Decide it there.
- iOS stays **ON HOLD**.

### R3 — AI assistant — ⚠️ scope this before starting
**Two different assistants are specced in this repo, and only one of them is a pre-promotion-sized job.**
- **[AI-ASSISTANT-BACKLOG.md](AI-ASSISTANT-BACKLOG.md)** — on-device, **mobile-first**, constrained typed
  actions. Tier A means a Swift impl *and* a Kotlin impl; iOS is on hold, so it would land **Android-only** —
  i.e. the largest item on this roadmap, delivered to the smaller of the two surfaces, right after R2 spends a
  phase getting that surface back to parity.
- **[BACKLOG.md](BACKLOG.md) #17 "narrate, don't compute"** — navigation, explainers, and narrating numbers the
  deterministic engine already computed. Always-on, safe by construction (the model emits no figures, so it
  cannot invent one), and it **works on the web**, which is the primary product.

**Recommended split:** ship #17's narrate/navigate layer first (cheap, cross-surface, nothing to defend), then
the **action schema + name→entity resolver + confirm/undo chip** — the backlog's own estimate is that this is
**80% of the work and the LLM is 20%**, and a deterministic parser handles "12 eur lunch" with no model at all.
Leave the on-device LLM itself for last, or for after promotion. **Red line, non-negotiable:** any
capture/categorisation using ML runs **strictly on-device with zero raw-data egress** — one convenient cloud
call makes "never fed to AI" a lie, and that claim is the reason to exist.

### R4 — Railway migration (hosting **and** database)
- Cloud Run → Railway **and** Neon → Railway Postgres, per the standing plan.
- **This is the phase that retires the only live production risk:** a traffic spike fans Cloud Run instances out
  into **Neon's connection ceiling**. Promotion *is* that spike. If R4 slips, the mitigation (pooled connection
  string + a `max-instances` cap) becomes mandatory before R7 rather than optional.
- Side benefit: it ends the recurring Windows `gcloud` shim pain (spaced `CLOUDSDK_PYTHON`, `^|^` delimiters).
- **Exit:** served-bytes + endpoint probes green on the new host, DNS cut over, old revisions retired.
- ⚠️ **Railway is a new sub-processor** and the DB may change region — that is a **privacy-policy edit** (feeds
  R5), not a footnote.

### R5 — Landing, terms, privacy + Pro split — final verification
The ⬜ TODOs written below **are** this phase: the landing rewrite, the Free/Pro re-validation, and **billing
go-live**. Added here:
- **★ Real payment integration + the Pro trial** — see the ⬜ TODO below. This is the phase for it: it needs the
  frozen split (you cannot price what is still moving) and it needs R4 done first (webhook URL, secrets and the
  `Subscriptions` table all move hosts in R4; wiring a provider before that is the same work twice).
- **Legal re-read.** B3 was read on 2026-08-05 and was already found stale once — the policy predated the two
  processing activities B1/B2 had just added. R3 (an assistant), R4 (a new sub-processor / possible region
  change) and **the payment provider (a new sub-processor holding billing data)** each oblige another pass. A
  lawyer's glance stays advisable.
- **Fold technical SEO in here** — see R6.

### R6 — SEO
- ⚠️ **The technical half of SEO is landing-page work**: title/meta/OG, structured data, heading order, image
  alt text, LCP/CLS, `sitemap.xml`, `robots.txt`, canonical, and **`hreflang` for EN/BG**. Doing it as a phase
  *after* the rewrite means editing the same page twice. **Do it inside R5's rewrite**, and keep R6 for what
  genuinely can't happen earlier: content pages, off-page, Search Console + analytics.
- The **bilingual EN/BG** surface is a cheap, real asset here — most competitors in this niche ship English only.

### R7 — Promote
- **Preconditions:** R4 done (capacity), R5 done (the page is honest and the paywall is settled), and the
  **intake decision** made — staged invites vs a public link. That decision is still the last open owner call;
  [Capacity](#capacity) argues for staged.
- ⚠️ **Do not promote while checkout is dead.** Today the first 100 sign-ups get lifetime Pro and **everyone
  after them is a genuinely gated Free account** told *"Pro isn't on sale yet."* Promotion is the traffic spike;
  the spike is what fills the 100 and then keeps going. Opening the door without billing means the users past
  the cap meet walls with **no way to pay** — the one arrival state where the product looks worse than it is and
  no revenue is possible. Either billing is live (R5) or the cap is raised so nobody is gated. Not neither.
- ⚠️ **Before promoting, make error reporting a push, not a pull.** B1 notes it plainly: nothing *alerts* on
  `FinApp.ClientError` — you have to go and look. A log-based alert is cheap. Without it, the first you hear of
  a crash on a stranger's device is a review.

## ⬜ TODO before the door opens (R5) — rewrite the landing page

**The landing page currently undersells the product, and deliberately so: it was written before Debt R1/R2, the
Trends chart, the payoff planner, installment splitting, the health score and the achievements existed.** Its six
feature tiles and three "how it works" steps describe an early version of TandemTab.

**Do this LAST, once the beta feature set is frozen** — every rewrite before then is work that gets redone. When
the last beta feature lands, go through the app screen by screen and make the page *maximally informational*:
what it actually does now, with real screenshots rather than prose, and the debt/goal projections given the
prominence they've earned (they are the most distinctive thing in the product and the landing page barely
mentions them).

Check at the same time: the hero ticks, the feature grid, the "how it works" steps, the Pro tier's bullet list
(it is generated from the feature catalogue, so it stays honest by itself), and the beta seat count copy — that
last one comes out entirely when the beta ends.

## ⬜ TODO before the door opens (R5) — re-validate the Free/Pro split + gating

**Same discipline as the landing page: the paywall line predates half the product.** The Free-vs-Pro catalogue
(`MonetizationService.Catalogue`) and MONETIZATION.md's table were written before Trends, the payoff planner,
installment splitting, the health score and achievements existed. Session 87 wired **real gating** — client gates
at every Pro entry point plus a server-side 402 backstop (`EntitlementService`) on the actions that have an
endpoint (invite/share, import, and the 2nd-account cap). But the *split itself* deserves one more deliberate pass
once the feature set is frozen, exactly like the landing rewrite.

**Walk every gate as a Free user** (pin Free in the admin console) and decide, feature by feature, whether the line
is in the right place. Open questions to settle in that pass:

- **Numeric caps still need decided numbers.** "Small caps" on funds and recurring items (MONETIZATION.md) were
  left unenforced on purpose — enforcing them means inventing a number (5 funds? 3 recurring?), and a limit that
  later changes is worse than none. The **account cap is enforced** (Free = 1, server + client); funds/recurring
  are not. Decide the numbers, then enforce them the same way.
- **Achievements depth** has a MONETIZATION line ("Basic vs Full") but **no catalogue key** — either add one and
  gate it, or drop it from the table so the two agree.
- **Debt metering**: the payoff *planner* is gated (`debt`), but "1 debt on Free" (allowing one debt bucket,
  gating the 2nd) isn't enforced server-side yet. Confirm whether view-only-1-debt is the intended Free line.
- **History window**: the Breakdown/Trends range beyond ~3 months is client-gated (`history`); **period
  back-navigation** past that window is not. Confirm the exact Free horizon and gate navigation to match.
- **Price**: ✅ **resolved 2026-08-05 — €29.99/yr (+ €3.99/mo)**, the config default the app already serves.
  `docs/BILLING.md`'s superseded 3-tier `$39.99` table is annotated as such; MONETIZATION.md is authoritative.

## ⬜ TODO before the door opens (R5) — billing go-live: a real payment provider + the Pro trial

**The rails are built and the engine is a stub.** P4 shipped everything except the part that moves money:
`IPaymentProvider` is the seam, `SandboxPaymentProvider` walks the whole flow (checkout → return → subscription
row → plan flips to Pro), `SubscriptionService` owns entitlement, and every gate already honours it. What does
not exist is a provider that charges a card. **Recorded here so it is impossible to forget**; the design detail
lives in [MONETIZATION.md → Billing go-live](MONETIZATION.md#billing-go-live--the-real-provider-and-the-trial).

- **Why R5 and not earlier:** the provider needs a **stable public webhook URL and a secret store**, both of
  which R4 moves to a different host, and it needs a **frozen Free/Pro split**, which is the other half of this
  same phase. Doing it before R4 is the integration twice; doing it before the split is settled is pricing a
  moving target.
- **Why not later (i.e. not after R7):** see the R7 precondition above — post-cap users are already gated with
  no purchase path.
- ⚠️ **Provider is an open decision, and "Stripe" is the default answer, not the researched one.** Selling a
  digital subscription to EU consumers means **VAT at the buyer's rate in their country**. With Stripe *you* are
  the merchant of record and that compliance is yours (Stripe Tax computes it; it doesn't file it). A
  merchant-of-record — **Paddle / Lemon Squeezy** — takes that on for a higher cut. For a one-person business in
  Bulgaria selling across the EU, that trade deserves an hour of real thought before any code. The
  `IPaymentProvider` seam makes it a self-contained choice either way, which is exactly what it was built for.
- **⚠️ The repo is public.** The secret key and the **webhook signing secret** are env vars on the host, never
  files here — same rule as `Admin__Emails`. A leaked signing secret means forged "they paid" webhooks.
- **Sandbox must survive.** Keep `SandboxPaymentProvider` selectable via `Payments__Provider` after the real one
  lands: it is how the flow gets tested without a live charge, and every row it writes is already `Sandbox = 1`.
- **New: the Pro trial.** MONETIZATION.md promises one and **nothing in the code models it** — `Subscriptions`
  has no trial concept and `IsActiveAsync` only matches `Status = 'active'`. It is a small change to the right
  place (a row with `Provider = null` and a status the entitlement check accepts), and it must be **granted once
  per account, never deleted on expiry** — delete the row and the trial is infinitely repeatable.
- ⬜ **Owner call: trial length + card or no card.** The two docs disagree (MONETIZATION.md says 14 days
  card-optional; docs/BILLING.md says 45 days cardless). Recommendation and reasoning in MONETIZATION.md.
- **Exit:** a real card completes a real purchase on the live host, the webhook flips the plan without the
  browser being involved, cancel/expiry degrade as designed, VAT handling is decided and correct, the privacy
  policy names the provider, and the sandbox path still works.

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

### P2 — Owner-only admin dashboard (users + activity) · **M–L** · ✅ **DONE (2026-08-05)** — pulled into beta
Shipped an owner-only "Admin — usage" panel in the profile modal: total users, total accounts, beta-cohort size
(from B4's `UserSignups`), new sign-ups 7d/30d, active users 7d/30d (proxied by recent refresh-token issuance),
and a 30-day sign-ups sparkline. **Metrics only — counts + timestamps, never any account's financial data**
(no snapshot is ever decrypted). Authorization is **server-side and fails closed**: `AdminPolicy` is an email
allowlist (`Admin:Emails` env var); an empty list means nobody is an admin. `GET /admin/metrics` re-checks it
(403 otherwise); `/me` exposes `IsAdmin` only so the client hides the panel. Browser-verified + 2 gate tests.

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

### P3 — Money-over-time chart (income / expenses / saved / balance) · **M** · ✅ **DONE (2026-08-05)** — pulled into beta
Shipped as a 4th **"Trends"** Spending sub-tab (exactly the recommendation below): a multi-series line chart of
income / spent / saved / balance across the periods in the shared Breakdown window (3 / 6 / 12 months / all time),
with a tap-to-hide legend that rescales the axis. It **absorbed** the per-category trend sparklines out of the
Health-score modal (BACKLOG #16 — trends now live in one place). Empty state under two periods. Browser-verified.

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

### P4 — Monetization behind a flag · **M** · ✅ **RAILS DONE (2026-08-05)** — flag OFF for beta
Shipped the **rails, not the billing**, exactly as this item asked. `Monetization:Enabled` (env var, **default
off, and off during beta**) gates everything: while off there is no plan UI and every account is "unlimited".
Flip it on to test — a "Your plan" panel (Free vs Pro cards, price from config) appears; beta-cohort accounts
are **grandfathered to Pro** (keyed off B4's `UserSignups`). `/me` carries `Plan`+`MonetizationEnabled`; new
`GET /plans`. Prices are config-driven — the hero is **€29.99/yr per MONETIZATION.md** (✅ settled 2026-08-05;
`docs/BILLING.md`'s old $39.99 3-tier table is annotated as superseded). Browser-verified with the flag on; the flag-off
default ("unlimited", no UI) is test-pinned. **Deliberately not built:** enforcement at the individual gate
points (shared accounts, import limits, history window, debt planner, Free caps) — the "at leisure" half; the
non-backfillable cohort stamp already shipped in [B4](#b4--state-what-beta-promises-about-their-data-and-stamp-the-cohort--s-).

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
- ✅ **Price settled: €29.99/yr** (+ €3.99/mo), the config default the app serves. `docs/BILLING.md`'s old
  $39.99 3-tier table is annotated as superseded; MONETIZATION.md is authoritative.

---

## Deliberately NOT blockers

Recorded so they don't get re-litigated at the door.

| Item | Why it can wait |
|---|---|
| **Billing / monetization** | The standing decision ([docs/BILLING.md](docs/BILLING.md)) is *monetize after mobile + push*. A free beta is correct; charging for a beta would be worse than not. ✅ **Price settled at €29.99/yr** (2026-08-05) — MONETIZATION.md is authoritative; `docs/BILLING.md`'s old $39.99 3-tier table is annotated as superseded. |
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
