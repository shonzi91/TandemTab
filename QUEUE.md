# TandemTab — the open-issue queue

*The bugs and issues still standing after the Session 111 sweep, in the order they should be taken. Every row was
checked against the code, not carried forward from an older write-up.*

| | |
|---|---|
| **Opened** | 2026-08-20 (Session 111) |
| **Sources** | [BETA-FINDINGS.md](BETA-FINDINGS.md), [UX-BACKLOG.md](UX-BACKLOG.md), [BACKLOG.md](BACKLOG.md), [docs/MOBILE.md](docs/MOBILE.md), the carried "still open" lists in [HANDOFF.md](HANDOFF.md) |
| **Not in here** | Anything a roadmap phase already owns — R3's assistant, R4's migration, R5's billing. See [OPEN-BETA.md](OPEN-BETA.md) |
| **Status** | ⏸️ **On hold — the owner's list below goes first.** |

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
| O5 | Split the **recurring list** into past (newest→oldest) and future (soonest→latest) | S | ⬜ |
| O6 | **"Saved toward goals" should name its bucket** everywhere it appears; savings in the Breakdown pie; move **Breakdown + Trends** off the Spending tab onto the Home chart; mobile Trends hover is awkward | Rework | ✅ S112 |
| O7 | New savings-bucket modal: when **Debt** is picked, two more chips appear — separate them from the bucket-type row | S | ✅ S112 |
| O8 | Move the **"ahead / interest saved"** chip off the debt-free Home section onto the bucket, next to interest left, with the prepaid principal in it | S–M | ✅ S112 |
| O9 | Rename **Home → Dashboard**, and let users choose which tab the app opens on | S | ⬜ |
| O10 | **Confirmation prompts on every delete**, web and mobile | S–M | ⬜ |
| O11 | Money moved from a bucket **into a budget** still counts as saved — €500 in, €200 budgeted out, card still says €500 | Bug | ✅ S111 |
| O12 | Make **"Total saved X (this period · % of money in)"** prettier | S | ✅ S112 |
| O13 | **Debt owed in Trends** disagrees with the debt bucket | Bug | ✅ S111 |
| O14 | Trends' **Spent** and **Set aside** charts should be switchable to a **category** / a **bucket** and drawn for it | M | ⬜ |

---

> **How to read the order.** The top three are wrong numbers or lost data; below them it is polish and product
> judgement. A row marked ⛔ is *deliberately* waiting on something — don't "clear" it by building it.

---

## 1. ⚠️ Android stores the wrong figure for a foreign-cash wallet

**What happens.** On the web, picking a foreign-cash wallet changes what the Amount field *means* — the modal says
so: *"Amounts spent from this wallet are typed in {CUR} and stored in {account currency}."* The client converts once
at entry and sends both figures; the server stores what it is given and does not re-convert. **Android has neither
the wallet's currency nor its rate**, so the same expense typed on the phone is stored at face value in the account
currency — 100 kr becomes €100, in the one situation the feature exists for: standing in another country with the
phone in your hand.

**Why it is first.** Everything else on this list is missing, slow or ugly. This one is *wrong*, silently, in the
ledger, and no total downstream can tell.

**First step is on the SERVER, not in Kotlin.** No thin contract carries a fund's currency or rate: `FundRowDto` is
id/name/icon/note/balance/openingBalance/synced/archived/availableToTransferOut, and `Currency` appears in
`FinApp.Contracts` only as the *account's*. Add `Currency` + `Rate` to `FundRowDto` and `FundOptionDto`, then the
add-expense sheet can label the field and convert the way the web does.

**Reproducing it needs** a Pro account with a foreign-cash wallet — it is verified by construction (the fields are
absent from the contract, the DTO and the sheet), not on a device.

**Related:** the same missing read is what makes `PUT /funds/{id}/currency` a stated lag rather than an S–M row.

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

## 7. The Android refund row was never exercised

Built in S110, Kotlin compiles clean, and nothing beyond that is claimed — the emulator run stopped at the sign-in
screen. **This is an emulator session, not a build.** ⚠️ Verifying against a local server needs two temporary edits
(debug `API_BASE_URL` → `http://10.0.2.2:5179`, plus `usesCleartextTraffic`) that must be reverted before commit.

## 8. ⛔ BACKLOG #16 — audit the fourth savings-bucket kind (`Investment`)

A permanent extra toggle on the add/edit bucket modal plus a Goals filter chip, and it is not obvious they earn
their keep. **Deliberately blocked on real usage data** — removing a `SavingKind` burns the enum value, as the
reverted `PlannedExpense` kind did. Decide it with users, not with a hunch.

## 9. ⛔ UX-BACKLOG #10 — pin or sort a focus debt

Deferred on purpose until somebody actually has a goal list long enough to scroll.

## 10. Housekeeping

- **Dead CSS:** `.debt-progress` and `.debt-prog-cap` in `Dashboard.razor.css` — no markup references either.
- **`ReopenTrip` has zero callers.** The domain method, the endpoint and the client method all exist and work; the
  button was never placed. ⚠️ **Ask before removing** — deleting a working path is not the same as removing dead
  code.

## 11. ⛔ Production risk (not a bug): Neon's connection ceiling

A traffic spike fans Cloud Run instances out into Neon's connection limit, and **promotion is that spike**. R4
(Railway) retires it. **If R4 slips, the mitigation stops being optional:** a pooled connection string plus a
`max-instances` cap, before R7.

---

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
