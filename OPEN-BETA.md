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

---

## Blockers

### B1 — Client-side error reporting · **M** · ⬜
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

### B2 — An in-app way to tell you something · **S** · ⬜
A "Send feedback" row in the profile modal — a `mailto:` or a POST to an endpoint. Plus a support address
visible somewhere that isn't the footer of a legal page.

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

### B4 — State what "beta" promises about their data · **S** · ⬜
Decide, then put it on the landing page and the sign-up screen in one sentence each:
- **Will accounts and data survive to launch?** (If yes, say it. If you might reset, say *that* — loudly.)
- **Is it backed up?** What happens if a migration goes wrong?
- **Will it stay free for people who join now?** (Ties into [MONETIZATION.md](MONETIZATION.md)'s
  "grandfather early users" / beta-tester offer — a promise made here is a promise you keep.)

**Why:** beta users forgive bugs. They do not forgive losing a month of budgeting they entered by hand. Silence
on this reads as "we haven't thought about it," which is worse than an honest "we may reset once."

---

## Deliberately NOT blockers

Recorded so they don't get re-litigated at the door.

| Item | Why it can wait |
|---|---|
| **Billing / monetization** | The standing decision ([docs/BILLING.md](docs/BILLING.md)) is *monetize after mobile + push*. A free beta is correct; charging for a beta would be worse than not. ⚠️ [MONETIZATION.md](MONETIZATION.md) (€29.99/yr) and [docs/BILLING.md](docs/BILLING.md) ($39.99/yr) still disagree on price — reconcile before any pricing is ever shown, not before beta. |
| **Android** | It's a real thin client but has **none** of S70–S82 (Home hero, Home donut, bell grouping, Debt R1/R2, F3, a11y). Ship the beta **web-only**; a stale mobile app is a worse first impression than no mobile app. |
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

## Definition of done

Open beta is ready when: **B1–B4 are done, the verification hour is clean, and the intake shape (staged vs
public) is chosen.** Nothing else on any backlog is a precondition.
