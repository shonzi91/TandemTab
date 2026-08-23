# TandemTab — the open-issue queue

*The bugs and issues still standing after the Session 111 sweep, in the order they should be taken. Every row was
checked against the code, not carried forward from an older write-up.*

| | |
|---|---|
| **Opened** | 2026-08-20 (Session 111) |
| **Reviewed** | 2026-08-23 — every row re-checked against the code after both outstanding branches were merged |
| **Sources** | [BETA-FINDINGS.md](BETA-FINDINGS.md), [UX-BACKLOG.md](UX-BACKLOG.md), [BACKLOG.md](BACKLOG.md), [docs/MOBILE.md](docs/MOBILE.md), the carried "still open" lists in [HANDOFF.md](HANDOFF.md) |
| **Not in here** | Anything a roadmap phase already owns — R3's assistant, R4's migration, R5's billing. See [OPEN-BETA.md](OPEN-BETA.md) |
| **Status** | ✅ **The owner's list is closed** — O14 shipped in S114 and this file had not been ticked. **The old #1 (the foreign-cash wallet) is built and live.** What is left is led by a different kind of risk: a large amount of Android code is *deployed and has never been run*. |

> ⚠️ **Two rows on this list were stale when it was reviewed**, both in the same direction — work that had shipped
> and never been struck off. Re-check against the code before starting anything here, the way the S111 sweep did.

---

# ⭐ The owner's list (2026-08-20) — this goes first

*From personal daily use. Verbatim intent kept; the reading and the plan under each row are mine, and where I
found the code disagreeing with the report I have said so rather than smoothing it over.*

| # | Item | Kind | State |
|---|---|---|---|
| O1 | Refund into a **non-synced** fund — deduct from the expense, and credit the fund that actually received the money | Feature | ✅ S111 |
| O2 | Tagging an auto-filed expense **loses its 🏦 badge** (✅ S111); and rules should be able to set a **tag**, not just a category (✅ S112) | Bug + feature | ✅ S112 |
| O3 | Rework the **transaction review** flow (keep-both / replace / pin / ✕ / accept is too complicated) + **duplicate detection is over-eager** (two €10 spends two days apart) | Rework | ✅ S112 |
| O4 | Show **money-out transfers** in the expense list — and consider them in budgets, since they lower the balance | Feature | ✅ S111 |
| O5 | Split the **recurring list** into past (newest→oldest) and future (soonest→latest) | S | ✅ S112 |
| O6 | **"Saved toward goals" should name its bucket** everywhere it appears; savings in the Breakdown pie; move **Breakdown + Trends** off the Spending tab onto the Home chart; mobile Trends hover is awkward | Rework | ✅ S112 |
| O7 | New savings-bucket modal: when **Debt** is picked, two more chips appear — separate them from the bucket-type row | S | ✅ S112 |
| O8 | Move the **"ahead / interest saved"** chip off the debt-free Home section onto the bucket, next to interest left, with the prepaid principal in it | S–M | ✅ S112 |
| O9 | Rename **Home → Dashboard**, and let users choose which tab the app opens on | S | ✅ S112 |
| O10 | **Confirmation prompts on every delete**, web and mobile | S–M | ✅ S112 |
| O11 | Money moved from a bucket **into a budget** still counts as saved — €500 in, €200 budgeted out, card still says €500 | Bug | ✅ S111 |
| O12 | Make **"Total saved X (this period · % of money in)"** prettier | S | ✅ S112 |
| O13 | **Debt owed in Trends** disagrees with the debt bucket | Bug | ✅ S111 |
| O14 | Trends' **Spent** and **Set aside** charts should be switchable to a **category** / a **bucket** and drawn for it | M | ✅ S114 |

✅ **All fourteen are closed.** O14 shipped with the Trends focus picker (`trend-focus-sel`, two `<select>`s — one
for a category, one for a bucket — plus `TrendFocused` / `TrendFocusValue`); the row was simply never ticked here.

---

> **How to read the order.** The top row is the largest *unverified* surface: code that is live and has never been
> run. Below it, the wrong-numbers rows are gone — the last of them shipped — so what remains is blocked work,
> product judgement and polish. A row marked ⛔ is *deliberately* waiting on something; don't "clear" it by
> building it.

---

## 1. ⚠️⚠️ A large amount of Android code is LIVE and has never been run

**What happened.** Session 115 wrote nine Android commits — the foreign-cash wallet fix, statement import, merchant
rules, the wallets ring, debt payoff, gestures, a `GET /breakdown` read — and its own handoff says plainly: *not one
line of this session was seen running*. The emulator booted and installed but stopped at a sign-in the agent could
not complete. Those commits are now merged and deployed (`finapp-00327-t6g`).

**Why it is first.** Every other row here is missing, blocked, or a judgement call. This one is a large surface of
*unknown* code in front of users. And the specific risks are the kind that look perfectly fine when read: a swipe
that fights the scroll view, a ring drawn with a seam, a slider that snaps to the wrong point, a column mapper
against a real bank CSV.

**It also now covers the merge's own repairs.** Merging S115 into main broke three screens in ways only one of
which the compiler could see — `BreakdownSheet`, `PayoffSheet` (calling a deleted formatter), `NotificationsSheet`
and `ImportSheet` (rendering money that escaped privacy mode). Those fixes are reasoned and compiled, **not
observed**, and two of them are about figures being hidden, which is exactly the class of thing you cannot confirm
by reading.

**And it absorbs the older rows:** batch 5's dialogs, the sectioned recurring list, the landing-tab chips (S112)
and the Android refund row (S110) have all been compiled-only for the same reason. ⚠️ **The sign-in blocker is
gone** — `5706699` drove a signed-in emulator on API 35 and verified masking against real balances, after S115's
handoff was written. Recipe and traps are in the Android toolchain notes; verifying against a *local* server needs
two temporary edits that must be reverted before commit.

## 2. ⚠️ A phone-linked bank connection tracks the aggregator's first account, permanently

`CompleteLinkAsync` takes `session.AccountIds[0]`, its own comment reading *"the user can switch in the UI"* — and
on the phone there is none. At a bank with a current *and* a savings account, whichever the aggregator lists first
is what syncs. **Disconnecting and re-linking does not help**: it takes the first again.

⛔ **Deferred with bank's back half**, on the audience (a two-email allowlist who all have the web app), not on
verifiability — see the box in [docs/MOBILE.md](docs/MOBILE.md). It is the sharpest of the five costs written up
there and the first thing to build when the allowlist widens. **Pick it up as `PUT /bank/account` +
`GET /bank/accounts` together** — one screen, and the pair is useless split.

## 3. ⚠️ `POST /bank/ack` shipped to Android without its undo

The phone can acknowledge a pending bank row; `/bank/reset` — the undo for a mis-tap — is not wired. A
semi-destructive action with no way back on the surface that offers it. Same shape as S108's settle/unsettle
finding, one row earlier in the list. ⛔ Also inside the bank deferral.

## 4. There is no background bank check

New transactions are learned **on app open, on account switch, and by manual refresh only**, throttled to 15
minutes server-side (`BankSyncFreshFor`, so the throttle counts across devices). Opening the app twice inside 15
minutes will not re-check.

That is the design, and it is defensible — but it is a *user-visible* design that has never been decided out loud.
**The decision to make:** is "your bank is checked when you open the app" the promise, or does this need a
scheduled server-side pull? The second is a real feature, not a fix.

## 5. UX-BACKLOG #11 — accessibility, and the light-theme palette it owns

Marked *in progress* since S81, and the only item from the original beta report that is neither shipped nor closed
as stale. Control accessible names are still icon glyphs and `title`-only in places (the bell announces "3"; modal
buttons announce "✓Add" / "✕Cancel" because the decorative glyph is read).

⚠️ **This row also owns the 32 sub-4.5:1 light-theme findings** the S89 sweep deliberately left alone — brand green
at 3.34:1, secondary greys at 2.4–3.0:1 on white. That is the product's visual language, so it is a **palette
decision** to be taken here on purpose, not a theme bug to be fixed in passing.

## 6. Verification debt, carried since S88/S89

The **chart animations** and **F6's shared-account "together" line** have never been seen with real data. Both are
cheap to check against a seeded account and have been carried for twenty sessions on the strength of "it compiles".

## 7. Notifications and achievements bake amounts into prose

`NotificationsMap` and `AchievementsMap` build strings like *"Off balance — overspent by €4,760.00."* server-side,
so **no client formatter ever sees those figures**. Privacy mode therefore cannot reach them the way it reaches
everything else, and both clients paper over it: the web and the phone each run a regex (`maskServerText` on
Android) written against `MoneyText.Format`'s actual output.

⚠️ **The regex is correct today and coupled to a format it does not own** — if `MoneyText.Format` ever changes
shape, this is the other end of that change, and the failure is silent (a real figure under a bar promising it is
hidden). Found on the emulator, not by reading the code. **The proper fix is those payloads carrying code + args
like `InsightMessageDto` already does**, so the client formats them and inherits masking for free. That is a
contract change across the server mapper and every string in it — its own session, deliberately not smuggled into
the one that found it.

## 8. The debt figures differ between web and phone, beyond payoff

The owner reported a web-vs-phone difference on debt and said *"maybe other stuff too I don't know"*. S115 built
the payoff read (`743ec4a`) and **deliberately did not guess at the rest** rather than invent a list. Now that the
payoff read exists, the honest next step is to diff the two surfaces field by field and write down what actually
differs.

## 9. Tests whose comments assert what their code does not

Removing `AddCategory`'s ignored parent parameter (`c8c4d16`) exposed four silent callers, **two of them tests
whose comments claimed a nesting that never happened**. A test that documents behaviour it does not exercise is
worse than no test: it is read as evidence. ⚠️ Named as a question rather than a task — *where else?* — and
`AccountRoundTripTests` is the place to start, since it was preserving a structure it had never created.

## 10. ⛔ BACKLOG #16 — audit the fourth savings-bucket kind (`Investment`)

A permanent extra toggle on the add/edit bucket modal plus a Goals filter chip, and it is not obvious they earn
their keep. **Deliberately blocked on real usage data** — removing a `SavingKind` burns the enum value, as the
reverted `PlannedExpense` kind did. Decide it with users, not with a hunch.

## 11. ⛔ UX-BACKLOG #10 — pin or sort a focus debt

Deferred on purpose until somebody actually has a goal list long enough to scroll.

## 12. Housekeeping

- **Dead CSS:** `.debt-progress` and `.debt-prog-cap` in `Dashboard.razor.css` — re-checked 2026-08-23: two rules
  each, zero references in the markup.
- **`ReopenTrip` has zero callers.** Re-checked 2026-08-23: the domain method, the endpoint, `BudgetingState` and
  even the page's own `private Task ReopenTrip(...)` all exist and work — there is no `@onclick` anywhere that
  reaches it, and Android has nothing. ⚠️ **Ask before removing** — deleting a working path is not the same as
  removing dead code.

## 13. ⛔ Production risk (not a bug): Neon's connection ceiling

A traffic spike fans Cloud Run instances out into Neon's connection limit, and **promotion is that spike**. R4
(Railway) retires it. **If R4 slips, the mitigation stops being optional:** a pooled connection string plus a
`max-instances` cap, before R7.

---

## Closed 2026-08-23 — the merge session

- ✅ **The foreign-cash wallet (the old #1).** The write side always carried `ForeignAmount`; the gap was the READ,
  since no thin contract carried a fund's currency or rate. `Currency`/`Rate` on `FundRowDto` **and**
  `FundOptionDto` closed it, and a wallet's currency can now be set from the phone as well. ⚠️ Conversion is on the
  **add** path only — an edit is pre-filled with the stored, already-converted figure, so treating it as foreign
  would divide a real expense. **Live, but see #1: never run.**
- ✅ **O14 (Trends focus picker)** — shipped in S114 and never ticked here.
- ✅ **Both of R2's stated lags** (wallet currency, statement import) are closed; parity 108/120 → **115/122 (94%)**.
- ⚠️ **The merge itself created four defects**, three of which no compiler would catch — see [HANDOFF.md](HANDOFF.md).
  The lesson generalises: a conflict is where two edits touch one line, but the dangerous case is one side
  *deleting* a shared symbol while the other writes new callers of it. **Grep the merged tree for deleted symbols.**
- ⚠️ **Two branches each appended a trailing optional field to `RecurringRowDto`** as the last parameter. "Trailing
  and optional" is safe against an old *client* and says nothing about a second branch. Both kept; the positional
  order is now written down in the contract.

---

## Batch 5 (Session 112) — and what it did NOT verify

- ⚠️ **Six of the eight web confirms are verified by construction, not by hand.** *Remove recurring* and *Remove
  tag* were driven in the browser (ask → Cancel returns you where you were; ask → Remove deletes and returns).
  The other six go through the **same** `AskConfirm`/`ConfirmAndRun` pair, which is why it exists — but the bank
  ones could not be reached: the local fixture's `BankSync:AllowedEmails` had to be reverted (it fails five server
  tests), and the avatar one needs an uploaded picture.
- ⚠️ **`OnBrowserBack` following the landing tab is code-verified only.** Tab changes push no history entry, so a
  scripted `history.back()` leaves the app rather than firing the handler.
- ⚠️ **Android is compiled, not run.** `:app:compileDebugKotlin` is clean and nothing more is claimed — the two new
  dialogs, the sectioned recurring list and the landing-tab chips have not been seen on a device. That is the
  emulator session O10's Android half was always going to need.
- ★ `RecurringRowDto` gained a trailing optional **`Pending`**. It is not derivable from `Due`/`Upcoming` — an item
  due in three weeks is pending but neither — so without it the phone could not tell "not yet" from "already done".

## Closed on the way through the owner's list (Session 112, batch 4)

- **The Home donut's centre said "spent" over a figure that now includes savings.** The ring is the sum of its own
  slices, so the moment set-aside money joined the chart (O6b) the word under the total was calling €300 of savings
  spending. It reads *"used"*. The figure and its label have to move together or the chart argues with its title.
- ⚠️ **`BudgetingState.DebtsAheadOfSchedule()` has no caller** now that O8 moved the badge onto the bucket. Left in
  place with a warning on it rather than deleted: it takes `Math.Max` of the months and **sums** the interest, which
  is honest for a whole account and a lie beside any single loan. If it is ever wanted again, read that first.
- ⚠️ **Scrubbing the Trends readout by dragging is NOT implemented** (the tap bug in O6d is). Touch pointers are
  implicitly captured by the element the press began on, so a `pointermove` over the next column never arrives —
  freeing it needs `releasePointerCapture` via JS interop plus hit-testing by coordinate.
- ✅ **The Cloud Build upload was 394.8 MiB because of `android/app/build` (383 MB), not `bin`/`obj`.**
  `.gcloudignore` had been excluding the .NET outputs all along; the Dockerfile copies only `NuGet.config` and
  `src/`, so it now drops `android/`, `tests/`, `tools/`, `docs/` and `deploy/` too. **Measured on the next
  deploy: 5.6 MiB over 297 files** — 70× less. What is left of the ~5 minutes is the container build.

## Closed on the way through the owner's list (Session 112, batch 3)

Not on this list when it opened — found while doing O2b and O3, and fixed with them:

- **The duplicate hint printed an icon's NAME as text** — `CategoryIcons.Effective` returns `"utensils"` for the
  built-in set, and the hint interpolated it raw, so it has read *"utensils Food"* since the hint shipped.
- **"Same — replace" dropped the label and the trip.** It carried the category and nothing else, so replacing a row
  un-labelled it. Harmless-looking until O2b, where the rule that had just labelled the row would be undone by
  accepting the bank's copy of it. Both now come across; only the row's provenance changes.
- ⚠️ **A `BankSync:AllowedEmails` value in `appsettings.Development.json` fails five server tests** (403 on every
  bank route). The test factory reads Development settings, and an allowlist that names anyone excludes the tests'
  own users. Worth knowing before adding one for a local bank fixture — the failures look nothing like the cause.

## Closed by the sweep that opened this file (Session 111)

- **`SetSettlement` dropped a settled expense's body data** — label, trip, time, synced flag and bank link. Losing
  the bank link meant the next sync would offer to log the same expense again.
- **`EditExpense` dropped the foreign figures and any refund** — correcting an amount erased both, and the refund's
  undo with them.
- **The `ClearTag` contract trap** — an omitted tag cleared the label while an omitted time did not.

Also struck off after checking the code rather than the write-up: BETA-FINDINGS' two "lower-severity" items (money
overview on Home, next-period discoverability) both shipped in S74–S79 and were never ticked; UX #12 was already
closed as stale; and [FEATURE-BACKLOG.md](FEATURE-BACKLOG.md) is entirely cleared.
