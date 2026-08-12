# TandemTab — mobile / native roadmap

**Status:** planning.
**Decision (2026-07-12):** go **full native** — native UI, not the Blazor Hybrid WebView.
**Decision (2026-07-19): no MAUI.** The platforms get their own native stacks: **Android first
(Kotlin / Jetpack Compose), then iOS (Swift / SwiftUI).** MAUI — including the MAUI-native-XAML path
this doc previously recommended — is off the table, and the existing `FinApp.App.Maui` Hybrid
scaffold is to be **retired**, not repurposed.

**Update (2026-07-25, Session 55): native Android has STARTED — ahead of the gate below, by user decision ("lets go android native").**
The web-thinning gate was a *proof mechanism*, not a hard technical dependency; the read+write API is functionally
complete (S37–54) and every Dashboard surface has a verified DTO, so the native client itself now serves as the proof.
A working Kotlin/Compose app lives in [../android/](../android/) — built, booted on an emulator, and verified against
the **live prod API** (login → error path). Web-thinning (Path B Phase 2/3) is now decoupled from native and continues
in parallel. See the Session-55 HANDOFF entry.

**★ Decision (2026-08-04, Session 82): iOS is ON HOLD. The target is web + Android at feature parity.**
Ship those two as one product; revisit iOS only once that pairing is running well. Rationale and the bill this
comes with are below.

This doc is the single source of truth for the mobile plan. Update it as decisions firm up.

---

## The two-platform decision (2026-08-04)

**What changed:** iOS moves from "next after Android" to **on hold, indefinitely**. Web and Android are the
product; iOS is a migration to consider later, on evidence.

**Why it costs nothing today.** iOS was already blocked on Mac / cloud-Mac access (see the tooling gate below),
so nothing was in flight to stop. What the decision actually buys is the removal of a *planning* tax: every
design call no longer has to be triple-checked against a SwiftUI implementation that doesn't exist, and
`IAssistant`-style per-platform abstractions (see [../AI-ASSISTANT-BACKLOG.md](../AI-ASSISTANT-BACKLOG.md))
stop needing a third arm speculatively designed in.

**⚠️ Why "same features basically" is the expensive half of this decision.** Parity is a *goal that has to be
maintained*, and right now it does not hold.

<details><summary>The original session-by-session gap list (written S82, superseded by the audit below)</summary>

| Web session | Not on Android |
|---|---|
| S70 | Home flatten, mobile quick-add FAB |
| S74 | Home money hero, money-in savings rate, Home breakdown donut, empty-fund collapse, Goals APR, Spending/Breakdown reworks |
| S75 | Onboarding collapse, bell Due/Suggestions grouping, one-line last-actions, By-date tag chips, Home reorg, savings-rate scale |
| S76–S77 | Runway "show the math" modal, rotating over-budget alerts |
| S78–S79 | Spent-includes-transfers, bank-adjusted deficit gating, seven period-lifecycle fixes |
| S80 | "Saved toward goals" Breakdown slice |
| S81 | **Debt R1** (informative debt), F3 "left to spend today", a11y #11 |
| S82 | **Debt R2** (installment split + hybrid balance, recurring-bill link) |

</details>

### ★ The parity gap, measured (Session 90, 2026-08-05)

**Counting sessions was the wrong instrument** — it measures how long the drift has run, not how big it is, and
it goes stale the moment web ships again. Since the native app is a thin client, there is an exact one: **which
endpoints the server exposes that `TandemTabApi` never calls.** Every feature Android is missing has to show up
there, because a thin client cannot render what it does not fetch.

Android calls **37** of the account endpoints (**46** after Session 91, **55** after Session 92, **59** after
Session 94, **69** after Session 95). It does not call these:

> ### ⚠️ Re-measured, Session 103 (2026-08-12) — read this before the table below
>
> **The gap did not just grow; its character changed.** Re-running the measurement (every `accounts.Map*` route
> against every path in `TandemTabApi`) puts Android at **61 of 99** account paths, with **38** never called. The
> table below is still right about what it lists, but it predates trips, expense↔tag/trip links, wallet
> currencies, transfers-out and half the bank rows.
>
> **★ The finding that matters is not a count.** The two biggest remaining rows — **trips** and **tags** — were
> **not client work at all**: neither had *any* read model. Trips were **write-only over the API** (five commands,
> no `GET`), because the thick client reads them out of the account snapshot it carries; and `ExpenseDto` carried
> neither `TripId` nor `TagIds`, with no tag list on any view. A thin client cannot render what it cannot fetch,
> so Android had **no trips at all** — not a smaller trips, none — and could not so much as show that an expense
> was labelled. **This is the fourth time R2 has hit the same shape** (the Home hero, the reconcile inputs, the
> bucket upsert): *check what the endpoint returns before sizing a row.* It is no longer a caution — it is the
> single most reliable predictor of what a row costs.
>
> **✅ Both are now unblocked (Session 103):** `GET /accounts/{id}/trips?today=` → `TripsViewDto` (built on
> `TripRecapService`, so the totals are the domain's, not a second summation), `ExpenseDto.TripId` + `TagIds`,
> and `TagOptionDto` on the Spending view. Seven server tests. Android's data layer (DTOs + eight API calls) is
> wired; **its UI is not built yet** — that is the next Android row, and it is the largest one left.
>
> **The remaining server-blocked rows** are unchanged: **F4 round-ups** (no contract field, no command endpoint)
> and the **fund↔bank sync toggle** (`SetFundSynced`, `TODO(cutover)`).

| Missing capability | Endpoints never called | Weight |
|---|---|---|
| **Savings/debt bucket money-movements** (CRUD ✅ **done S91**) | ~~`/savings/buckets…`~~, `/savings/disburse`, `/savings/to-budget`, `/savings/transfer`, `/savings/movements/…` | M |
| ~~**Debt entirely** (R1 informative debt + R2 installments)~~ | ~~`/installments`~~ (S93), ~~`/installments/{groupId}`~~ (S95) | ✅ **done** |
| ~~**Sharing — the hero Pro feature**~~ | ~~`/invitations`, `/members/{id}`, `/transfer-ownership`~~ | ✅ **done S92** |
| ~~**Period lifecycle**~~ | ~~`/periods/start-next`, `/periods/latest`, `/periods/{i}/schedule`~~ | ✅ **done S91** |
| **Statement import** | `/import` | M |
| ~~**Fund management** (add/archive/opening balance)~~ | ~~`/funds…`, `/fund-transfers/{id}`~~ | ✅ **done S95** |
| **Account settings** — the savings target ✅ **done S95**; **F4 round-ups** are ⛔ **not portable** (no contract, no endpoint) | ~~`/settings`, `/savings-target`~~ | M |
| **Tags** — incl. **F2** tag→category | `/tags…` | M |
| Achievements + **F6** goal celebration | `/achievements`, `/milestones` | S–M |
| Onboarding | `/onboarding`, `/onboarding/dismissed` | S |
| Export | `/export` | S |
| Reallocation between budget and savings | `/reallocations/to-budget`, `/reallocations/to-savings` | S |
| Settling an on-behalf expense | `/expenses/{id}/settle` | S |
| ~~Contribution (income) categories~~ | ~~`/contribution-categories…`~~ | ✅ **done S91** (create only) |
| ~~**Trips — the whole feature** (S99–S101)~~ | ~~`/trips`, `/trips/{id}`, `/trips/{id}/started`, `/trips/{id}/finished`, `/expenses/{id}/trip`~~ | ✅ **done S103** — see below. Still open on this row: `/trips/{id}/use-savings` (releasing a savings pot into the trip's budget) and `/trip-tags` (the seeded label set) |
| **Expense labels** — read ✅ **added S103** (`ExpenseDto.TagIds`, `TagOptionDto`); writing one is still unwired | `/expenses/{id}/tag` | S |
| **Money to another account** (transfers out + editing the pair) | `/transfers-out`, `/account-transfers/{id}` | M |
| **A wallet's own currency** (the S~102 multi-currency work) | `/funds/{id}/currency` | S–M |
| **Bank sync's back half** — Android links and syncs but can't map, re-point or reset a connection | `/bank/accounts`, `/bank/account`, `/bank/fund`, `/bank/mappings`, `/bank/reset` | M |
| Archived accounts (list + reactivate) | `/archived`, `/reactivate` | S |
| Editing/removing an income category | `/contribution-categories/{id}` | S |
| The account structure read (a thicker picker source) | `/structure` | S |

*(`/snapshot` is deliberately absent from that list: it is the thick client's whole-aggregate channel, and a thin
client calling it would be carrying the domain it exists not to carry.)*

**Read that table as the R2 backlog.** The four **L** rows are the ones that make Android a *different product*
rather than a smaller one: a user who only has the phone cannot start next month, cannot create a savings goal,
has no debt features at all, and cannot share an account — which is the thing Pro is sold on.
✅ **All four are closed** (S91, S92, S93, S95), and with them the whole **Tier-1 mobile-only** list.

**What's left in this table is the Tier-2 backlog**, and none of it is a phone-only dead-end — every remaining
row is something a phone user can live without or reach another way:
**statement import** (M), **savings/debt money-movements** (M — allocate and spend already work, so these are
refinements), **tags** incl. F2 (M), **achievements + F6** (S–M), **onboarding** (S), **export** (S),
**reallocation** (S), **settling an on-behalf expense** (S).
⛔ Two items are blocked on the **server**, not on Android, and cannot be estimated as client work:
**F4 round-ups** (no field on any contract *and* no command endpoint) and the **fund↔bank sync toggle**
(`SetFundSynced`, `TODO(cutover)`). Both are still whole-snapshot pushes in the thick client. They would batch
naturally into one "account settings commands" server slice.

#### ✅ Trips — closed (Session 103, 2026-08-12)

Spending gains a third segment (**By date · By budgets · Trips**), mirroring where the web keeps it, rather than a
fifth bottom-tab. A trip card carries its state mark (pin / plane / flag), its dates and pill (**Day 3** ·
**Ready to go** · **in 9 days**), and its total; opening it shows the **booked-ahead / while-away / after-getting-
back / a-day** split, the budget line, the two savings sentences, and the actions — attach an already-paid expense,
start, finish, reopen, edit, delete (behind a confirm that says the expenses survive). The FAB's add sheet gained a
**Trip row that defaults to the journey you're on**.

- **★ The bug the emulator found, and a test could not have.** Logging €23.40 onto Rome from the FAB left the trip
  card reading its old total: every trip figure is a *recap of expenses*, and nothing told the recap that an
  expense had moved. Fixed with `refreshTripsIfLoaded()` on add/edit/delete — same shape as S95's "removing an
  installment refetches Savings". **A screen whose numbers are derived server-side has to re-read them whenever
  their inputs change, and the inputs are usually owned by a different screen.**
- **The trip picker defaults to the *live* trip, never to one that has merely arrived by date.** Trip mode is
  opt-in; defaulting on the date would file the morning-of-departure coffee as holiday spending, which is the
  exact thing S101 removed on the web. Finished trips are left out of the picker entirely — that is how a weekly
  shop ends up in last summer's holiday.
- **★ The edit form carries `savingCategoryId` through untouched.** The server's trip edit is a full replace, so a
  form that omitted it would silently unlink the savings pot every time someone corrected a name. Linking a pot is
  still web-only; this makes sure the native editor cannot destroy one. (Fourth full-replace trap in R2.)
- **✅ The recap donut and the trip's own ledger followed** (same session), on a new
  `GET /accounts/{id}/trips/{tripId}?today=` → `TripDetailDto`: slices, which axis they're on, the biggest single
  thing, and every expense linked to the trip. **Its own read, not fields on the list** — the list would otherwise
  carry every expense of every journey the account has ever taken to draw a card nobody may open. Fetched on
  expand, dropped on collapse, and re-read after any expense or trip write.
  - **★ The axis decision travels, like the state does.** Tags lead only when at least half the trip is labelled;
    below that it falls back to categories. Two clients deciding that separately is two chances to lead with a
    different chart for the same trip.
  - ⚠️ **A single slice must be a full circle, not a 360° arc** — an arc that starts and ends at the same point
    draws nothing. That is the *ordinary* case here: a trip files into one category, so unless its labels are
    used there is only ever one wedge. The web hit the identical bug in S100.
  - ⚠️ **When the list's `expenseCount` and the detail's ledger disagree, the detail wins.** They are separate
    reads; deciding the empty state from the older one puts "nothing logged yet" above a list of six expenses.
- **Not built:** the savings release (`/trips/{id}/use-savings`), the seeded trip labels (`/trip-tags`), and the
  Home trip banner / shell shift. "Add something already paid" can still only offer the open period, because
  that is the only ledger a thin client holds.
- Verified on the emulator against a three-trip local seed (running / upcoming / finished) in **both themes**:
  state marks and pills, the split arithmetic (€374.90 booked ahead + €208.10 while away), attaching an expense
  (total moved €484.70 → €549.60, and the 8 Aug shop correctly counted as *booked ahead* against a 10 Aug
  departure), logging one from the FAB onto the live trip, and the delete confirm.

#### ✅ Period lifecycle — closed (Session 91, 2026-08-06)

All three writes now hang off the Home period chip, mirroring the web's period popover: **Start next month**,
**Change these dates**, **Remove this month**. Three things are worth keeping:

- **★ The gating is where the design lives, not the endpoints.** *Start next month* is offered only on the newest
  month and only once it has ended, greyed with *"Available once this month ends"* rather than hidden — the server
  enforces the same rule, so a live-looking item would just be a 400 with extra steps. *Remove* appears only when
  there is an earlier month to fall back to (the server refuses to delete an account's only period).
- **★ The reconcile step is the whole feature, and it must not be squeezed.** The rollover carries hand-entered
  opening balances, so the client compares them to the ledger and names the per-fund drift. That is the choice the
  web shipped as `✕ ✕ ✓` and had to fix in S89 — on a phone it is **three full-width labelled buttons stacked**,
  never a row. Two layout bugs came out of building it, both the same shape: **a floating action bar sized for the
  two-button case hides the tail of the three-button one.** The bar is now a *sibling* of the scrolling body rather
  than overlaid, and the sheet **auto-scrolls to the drift block** when it appears — otherwise pressing the primary
  button silently swaps the buttons under the user's thumb with the explanation still below the fold.
- **Adjustments are written to the CLOSING period before it is sealed**, dated its last day, into a category named
  *"Adjustment"* created on first use — which is why `/contribution-categories` came along (unexplained money-**in**
  needs an income source, not an expense category).

#### ✅ Savings/debt bucket CRUD — closed (Session 91, 2026-08-06)

Create / edit / archive / restore / delete, all four kinds (goal, debt, investment, expenses fund), from a single
sheet on the Goals tab. The money-movements (`/savings/disburse`, `/savings/to-budget`, `/savings/transfer`,
`/savings/movements/…`) stay open as an **M** — allocate and spend already work, so those are refinements, not the
"can't make a goal at all" gap.

- **★ The read model had to grow before the write could be safe.** `SaveSavingBucketRequest` is a full
  **overwrite**, not a patch: `SavingBucketConfig.Apply` calls `SetSavingFund` / `ConfigureSavingGoal` /
  `SetSavingInitialAmount` unconditionally. Four of the fields it overwrites — **`FundId`, `ThresholdPercent`,
  `NotifyOnMilestone`, `InitialAmount`** — were **not in `SavingBucketDto` at all**, so a client that couldn't read
  them back would silently clear the held-in fund, reset the alert threshold to its 80% default, switch milestone
  notifications off, and wipe the starting balance **every time the user renamed a bucket**. They are now on the
  DTO, with two server tests pinning the round-trip. ⚠️ **This is the third time R2 has hit the same shape** (the
  Home hero in S90, the reconcile inputs in S91): *check what the endpoint returns before sizing a row in that
  table — "just UI" is usually wrong here.*
- **Kind is fixed after creation**, as on web: the kinds carry different fields, actions and projections, so
  switching would strand data. The chips only appear when creating, and the Goals filter pre-selects the kind
  (filter to Debts, tap add, and you start on a debt).
- **Delete offers Archive in the same breath.** The domain refuses to delete a bucket with savings history, so the
  confirm dialog says so up front and puts Archive beside Delete — otherwise the advice arrives as a 400. Archived
  buckets get a collapsed "Show archived (N)" section with Restore, so that advice isn't a dead end.

#### ✅ Sharing — closed (Session 92, 2026-08-06)

Invite by username, accept/decline an invitation, see who's on the account, hand it over, remove someone, and
leave — nine calls, all of them already on the server. A phone-only user can now reach the feature Pro is sold on.

- **★ Sharing is two halves that look alike and are not.** The **inviter's** half is account-scoped and
  Pro-gated, and belongs with the account (the People block of the Account sheet, mirroring the web's account
  menu). The **invitee's** half is neither: an invitation arrives *before* there is any membership to hang it
  off, and it may land on a user with **no account at all**. So the invitations card sits on Home **above** the
  money and **outside** the "have we got an overview" branch. Verified exactly there: a freshly-invited user with
  zero accounts sees "shareowner invited you to Household" over an otherwise empty Home. Inside that branch it
  would have been invisible to precisely the people who need it.
- **★ The crown decorates; the server gates.** `/me` already carried `plan`, so the invite row can wear the Pro
  crown — but the client never refuses the call on its own. The gate is the server's 402, whose message is shown
  verbatim ("That's a Pro feature — upgrade to unlock it."). A client that decided for itself would lock out a
  paying user whenever its plan string went stale, and could never be more correct than the server it guesses at.
- **★ The read model was already complete — the first R2 **L** row where that's true.** `AccountSummaryDto`
  carries `ownerUserId`/`isOwner`/`members`, and `/me` carries `id` and `plan`; **no server change was needed**,
  only two fields Android had chosen not to parse (`UserDto.id` was even commented *"we don't need it"* — you do,
  the moment two people share an account and the UI has to say which one is *you*). Checking first still paid:
  it turned a row budgeted as **L** into an afternoon.
- **The owner can leave now, which they could not before.** Android used to offer Leave only to non-owners, so an
  owner's only exit was Delete. The server refuses to orphan an account, so the confirm block carries a member
  picker and the Leave button stays greyed until one is chosen — the picker *is* the request being valid, not a
  courtesy. A sole owner still sees only Delete: with nobody to hand it to, "leave" and "delete" are the same act.
- ⚠️ **The S91 floating-bar bug has a third instance, and this time it was designed out.** The Account sheet's
  Done bar is a sibling of the scrolling body, so a confirm block that grows at the foot lands underneath it —
  the leave picker was born hidden. `SheetShell` now takes a `scrollToEnd` trigger and scrolls the revealed block
  into view. ⚠️ Treat this as a **standing hazard of every sheet in this app**, not three unlucky screens.
- ⚠️ **One state variable doing two jobs shipped a layout bug into the same block.** The member-row expander and
  the hand-over picker both keyed off `handOverTo`, so *choosing* a new owner silently expanded that person's
  action row higher up the sheet and shoved the confirm block back under the bar. Caught on the emulator, not in
  review. They are separate now (`expandedMemberId` vs `handOverTo`).

#### ✅ Recurring bills/income CRUD — closed (Session 94, 2026-08-07)

Android could confirm or skip a bill that fell due, but could not declare one — so a phone-only user's Bills &
income list could only ever be empty, and the surface that reminds you about money leaving was unreachable.
Add / edit / pause / resume / remove now live in the Bills & income sheet (**four calls: 55 → 59**).

- **★ The read model had to grow again — the fourth instance of the same shape.** `RecurringRowDto` carried
  `CategoryName` / `FundName` but **not** `CategoryId` / `FundId` / `AutoPost`. Names are enough to *show* an
  item and useless to *prefill an edit of* one: matching by display name means a rename or a duplicate name
  silently retargets the save. Those three fields are now on the row, pinned by a server test.
- **★ The pickers travel with the view.** `RecurringViewDto` also grew `Categories` (spend), `ContributionCategories`
  (income sources), `Funds` and `Debts` — so the editor opens off the one read it already does, instead of
  borrowing the Spending and Goals caches and hoping both were warm. They are built *before* the no-open-period
  bail-out: an account between periods can still edit what recurs.
- **The kind picker only exists when creating.** The server refuses to change an item's kind, and the category
  list hangs off it (a spend category is not an income source), so on edit the chips are gone rather than shown
  and rejected. Switching kind while creating clears the picked category for the same reason.
- **★ The editor holds an id, not a row.** Pausing from inside the editor refreshes the list, and a captured row
  would keep saying "Pause" after the item was already paused. The live row is looked up each recomposition and
  the form fields are keyed on the **id**, so a refresh doesn't reseed the form and throw away what's being typed.
  Both halves matter: keying on the row would reset the form on every pause.
- **A second sheet is not stacked.** The editor renders *inside* the Bills & income sheet (list ⇄ edit), because
  Compose only reliably drives one `ModalBottomSheet` at a time — and it mirrors what the web does anyway
  (`Modal.Recurring` → `Modal.RecurringEdit`).
- Verified end-to-end on the emulator in **both themes**: a bill created (Rent, €500, Bills, Bank, day 1) → edited
  to €550 → paused (button flipped to Resume live) → removed behind a confirm dialog → then an income item
  (Salary, €2,000, day 25) showing the source picker and no loan-link section.

#### ✅ Fund management — closed (Session 95, 2026-08-07)

Android could *see* its funds and move money between them, but not create one, rename one, set what it opened the
period with, archive or restore one, remove one, or correct a transfer it had already made. **Seven calls: 59 →
66** — `POST/PUT/DELETE /funds`, `PUT /funds/{id}/archived`, `PUT /funds/{id}/opening-balance`, and
`PUT`/`DELETE /fund-transfers/{id}`.

- **★ The read model was already complete — the second R2 row where the check paid by finding nothing.** After
  four consecutive rows that needed the server to grow first (S90 hero, S91 reconcile, S91 bucket overwrite, S94
  recurring), this one needed **zero server change**: `FundRowDto` already carried `Icon`, `Note`, `Balance`,
  `OpeningBalance`, `Synced` and `Archived`, and `WalletsViewDto` already carried `ArchivedFunds`. **The ritual
  is still worth its twenty minutes** — the thing it would have caught here is real (see the next bullet), and
  the two rows where it found nothing were both rows that then took an afternoon instead of a session.
- **★ `EditFundRequest` is a full overwrite, and `FundRowDto.Icon` is what makes that safe.** The server calls
  `RenameFund` + `SetFundNote` + `SetFundIcon` unconditionally, so a client that doesn't send the icon back
  *clears* it. The DTO carries the **raw stored** icon, not the display fallback — so the editor round-trips it.
  Had it carried the effective icon, every first edit would have silently frozen a name-guessed icon into
  storage; had it carried nothing, every rename would have wiped the icon. Same shape as S91's bucket overwrite,
  caught by the same read-the-response habit.
- **★ Archiving a fund is two commands, not one, and the order is the safety property.** A fund that still holds
  money is transferred out **first** (a real fund transfer, so the account total is preserved and the archived
  fund is left at zero), then flagged archived. If the second half fails the transfer stands — visible and
  re-doable — rather than money disappearing into a hidden fund. This mirrors `BudgetingState.ArchiveFund`
  exactly. Verified on device: archiving a €350 jar into Cash left the total at €1,550 and wrote a visible
  "Holiday jar → Cash" transfer row.
- **★ The transfer editor must be able to name an archived fund.** A transfer's sides are ordinary fund ids, and
  archiving is exactly what happens to a fund *after* it has been transferred out of — so the picker is built
  from `funds` **plus any archived fund this transfer references**. Without that the sheet opens with nothing
  selected and re-saving silently retargets the transfer to whichever fund sorts first. Seen working on device:
  the archived "Holiday jar" was still the selected FROM chip.
- **★ The removal blockers are the server's to state, and it states them well.** `Account.FundRemovalBlocker`
  names the reason ("it has sub-funds" / "it's the only fund" / "expenses reference it" / "a transfer references
  it") and `SnapshotService` turns it into a 400 whose body Android already surfaces. So the client computes
  **nothing** and shows the message verbatim, with **Restore** sitting beside Remove as the way out — the same
  "archive is the answer" shape as the savings buckets. Confirmed on device: *"Cannot remove fund: a transfer
  references it."*
- **The destructive halves are dialogs, not in-sheet confirm blocks.** A dialog floats above everything, so
  neither the archive picker nor the remove picker can be born under a floating action bar. That is the
  `SheetShell` hazard designed out rather than worked around — it has now bitten four times in four sessions.
- ⚠️ **The "move opening balance" picker is offered whenever there's somewhere to move it**, not only when this
  period's opening is non-zero. The thin view carries the **open period's** opening balance, while
  `Account.RemoveFund` drops the fund's openings in **every** period — so gating the picker on the one figure
  the client can see would silently lose money a fund was given in an earlier month.
- ⚠️ **Editing an archive-driven transfer down strands money in the archived fund.** Archive moved €350 out;
  editing that transfer to €200 leaves €150 in a fund that is hidden but still counted in the total. The web
  behaves identically (same two commands), so this is inherent to the design, not a port defect — but the
  archived row does show the stranded figure, which is the mitigation.
- ⚠️ **Not verified: the synced-fund branch.** A bank-linked fund is meant to show Edit but no Archive and no
  opening-balance field; the emulator has no bank connection, so that path is code-reviewed only. Also **not
  ported**: the web's `Modal.FundMovements` (a per-fund ledger), which is a *read* the thin Wallets view doesn't
  carry, and the fund↔bank **sync toggle**, which has no command endpoint at all (`BudgetingState.SetFundSynced`
  is still a whole-snapshot push, marked `TODO(cutover)`) — a thin client cannot do it until the server can.
- Verified end-to-end on the emulator (`tandemtab_test`, local server `10.0.2.2:5179`, user `mob95b`, account
  *Phone Budget*) in **both themes**: created *Savings jar* (coins icon, note, €300 opening) → total €1,200 →
  €1,500 → edited to *Holiday jar* @ €350 with the note and icon intact → archived into Cash (total preserved,
  transfer row written) → edited that transfer 350 → 200 → **remove refused with the server's blocker** →
  removed the transfer (balances fully reversed) → removed the fund with its opening balance moved to Bank
  (€1,200 → €1,550, total unchanged throughout).

#### ✅ Savings target — closed (Session 95, 2026-08-07)

Android *displayed* the target on the Health sheet ("target 20%") but had no way to change it, so the one number
the health score measures you against was read-only on a phone. It now lives in the Account sheet beside the
account name. **Two calls: 66 → 68** — `GET /accounts/{id}/settings` (never called before) and
`PUT /accounts/{id}/savings-target`.

- **★ The placement is inherited from the web, and so is the owner gate.** The web keeps the target in its
  **"Edit account"** modal — renamed from *Rename* precisely because the modal had grown past renaming — and gates
  that menu item on `IsOwnerOfCurrent`. Android now matches: the target sits inside the `isOwner` block under the
  rename field. ⚠️ **That gate is a UI convention, not enforcement** — `PUT /savings-target` runs through
  `MutateAsync`, which is membership-scoped, so the server would accept it from any member. Worth knowing before
  someone "fixes" a non-owner's missing field and assumes the server was protecting it.
- **★ `null` is a meaningful loading state here, and defaulting would corrupt data.** `/settings` answers *after*
  the sheet opens. Seeding the field with the DTO's 20% default would let a user open the sheet, tap Save before
  the read lands, and overwrite a real 40% target with 20% — so `SettingsUi.savingsTarget` is `Double?`, the
  field is **disabled** until it arrives, and the caption says so.
- **The actual rate sits under the target.** `AccountOverviewDto.savedRate` was already on the client, so the
  editor can say *"You're saving 0% of money in this period"* right beneath the number being chosen — a target is
  a decision, and it can't be made against a blank. Null (nothing came in yet) is stated as such, never as 0%.
- **Saving invalidates the cached Insights read.** The health score is computed *against* this target, so a
  stale `HealthUi` would show the old goal until the app restarted. Verified: after saving 35%, the Health sheet
  read **"0% target 35%"** and its prose said *"...about €700.00 short of your goal this period"* (35% of €2,000).
- **The client clamps to 0..100 rather than posting what the domain will refuse.** `SetSavingsRateTarget` throws
  outside 0..1, so an out-of-range entry shows an amber "Keep this between 0 and 100%" and keeps Save disabled —
  a 400 is a worse answer than the number the user obviously meant.
- ⛔ **F4 round-ups are NOT portable, and this row is where that got pinned down.** `RoundUpTo` /
  `RoundUpBucketId` live on the domain `Account` but appear **nowhere in `FinApp.Contracts`** — no field on
  `AccountSettingsDto`, and no command endpoint (`BudgetingState.ConfigureRoundUps` is still a whole-snapshot
  push marked `TODO(cutover)`). The web's Edit-account modal carries them; a thin client structurally cannot
  until the server grows **both** halves. Same shape as the fund↔bank sync toggle from the row above.
- ⚠️ `InsightsDto.Empty` hard-codes `SavingsTarget = 0.20`, so a data-less account reports a 20% target
  regardless of what's stored. Harmless today — Android gates the Health card on `hasData` — but it means
  `/insights` is not a trustworthy source for the target. `/settings` is.
- Verified end-to-end on the emulator (user `mob95b`, account *Phone Budget*) in **both themes**: the field
  seeded to 20 from `/settings` with Save disabled → changed to 35 → **"Saved."** and Save disabled again →
  `GET /settings` confirmed `savingsRateTarget=0.35` → `/insights` confirmed `savingsTarget=0.35` → the Health
  sheet read the new goal → 150 raised the range warning and kept Save disabled → survived an app restart.

#### ✅ Removing a logged installment — closed (Session 95, 2026-08-07)

Since S93 Android could log a loan payment but never undo one. **One call: 68 → 69** (`DELETE
/installments/{groupId}`) — but the row was not a missing button, it was a **live data-integrity bug**, and that
is the finding.

- **★ Android could already corrupt an installment, and had been able to since S93.** A payment posts two or more
  linked expense rows. Android's expense list offered its ordinary per-row trash on every one of them, calling
  `DELETE /expenses/{id}` — so deleting the principal row left the interest row behind **and** left a
  payment-driven loan short of its principal. The web has guarded this since the feature shipped
  (`OpenDeleteExpense` redirects to a group confirm); Android never picked the guard up when it picked up the
  logging. **Shipping half of a paired feature ships the trap with it.**
- **★ The three fields that fix it were on the wire all along.** `ExpenseDto` has carried `InstallmentGroupId`,
  `InstallmentPart` and `DebtBucketId` since R2; Android's copy simply didn't declare them. **Third row running
  where the server needed no change, and the second (after S92's `UserDto.id`) where the gap was a field Android
  had declined to parse.** A thin client's blind spots are in its DTOs, not only in the API.
- **The delete redirects rather than being blocked.** The trash still works on an installment row — it just
  raises "Remove this installment? … all N of its rows go together — and a payment-driven loan gets its principal
  back" and calls the group endpoint. Blocking would have been the lazier fix and a worse one: undoing a mistyped
  payment is exactly what the user wants, and the whole payment is the only coherent unit to undo.
- **★ The row count is computed across the period, never the drawer's slice.** A web-logged installment can put
  principal and interest in **different categories**, so counting within one category's expander would report 1
  and the confirm would understate what the delete removes. `groupSize` is threaded from the full expense list.
  (Android's own log sheet happens to send one category for both parts, which is exactly why this would have
  looked correct in testing and been wrong on real web-created data.)
- **The rows now say what they are** — "Car loan · 🧾 Principal" — so two rows on one date read as one payment.
  The loan name is not from the DTO (which carries `debtBucketId` but no debt *name*); it arrives as the row's
  **note**, which the log sheet fills with the bucket name. A payment logged with a custom note shows that note,
  which is correct — the note is the user's words.
- **Removing refetches Savings, not just the expense list**, because a payment-driven debt gets its principal
  back: the Goals balance and Home's overview move with it.
- Verified end-to-end on the emulator (user `mob95b`, *Car loan* €10,000 @ 12% APR, payment-driven) in **both
  themes**: logged €300 → split **€100 interest / €200 principal**, owed 10,000 → 9,800, spent 400 → 700, two
  tagged rows → deleted from the **principal** row → the group confirm named **2 rows** → both rows gone, spent
  back to €400, owed back to **€10,000.00**, and `/spending` confirmed a single remaining expense with no
  orphaned installment rows.

**Closed in Session 90:** the Home money hero (all four tiles, incl. the money-in savings rate, the transfers
sub-line and **F3 "left to spend today"**) and the rotating over-budget alert strip. Both needed server work
first — see the note on `AccountOverview` below.

⚠️ **A thin client cannot close a UI gap the API does not serve.** The web hero showed four figures the native
app could not: three of them lived in `BudgetingState`, i.e. in the domain the thin clients deliberately do not
carry. They are now computed once in `AccountOverview` (`MoneyIn`, `TransfersOut`, `SavedThisPeriod`,
`SavedRate`). **Expect this shape again** for the rows above — check what the endpoint actually returns before
estimating any of them as "just UI".

Plus three standing gaps: **Breakdown** is blocked on the `[BACKEND] GET /breakdown` endpoint; **i18n (en/bg)**
is deferred and is its own session; and the Android **write paths are wired but never click-fired** against a
real account.

That is weeks, not days. Two honest consequences:

1. **The parity gap grows every time web ships.** S70 → S82 is exactly what unattended drift looks like. If
   parity is now a stated goal, it needs a rule — either *freeze web feature work while Android catches up*, or
   *accept a known lag and say so out loud*. Doing neither is how you get here again.
2. **This does not change the open-beta plan.** [../OPEN-BETA.md](../OPEN-BETA.md) already says ship the beta
   **web-only**; a 13-sessions-stale Android app is a worse first impression than no Android app. Android
   parity is a post-beta track, and beta feedback should inform which parts of it matter most.

**Revisit iOS when:** web + Android are at parity and stable, there's evidence of demand from real users, and
Mac access exists. Not before — the Kotlin work is the reusable proof that the thin API can back a native
client, and a second native client is only worth starting once the first one is finished.

---

## Pre-native prerequisites — the checklist (Session 54)
Native starts **only** once the ratified gate below is met. The API itself is now essentially complete; the gate is
**proving** it by thinning the web client. Web-thinning detail lives in [DOMAIN-REMOVAL.md](DOMAIN-REMOVAL.md) (Path B).

**THE GATE (ratified acceptance test):**
- ◻ **The web app runs with `FinApp.Domain` dropped from the WASM bundle** — no client-side domain computation left.
  This is the *proof* the thin API can back a native UI. = DOMAIN-REMOVAL Path B Phases 2–3:
  - ✅ Phase 1 — `Contracts → Domain` severed (Session 54)
  - ◻ Phase 2 — rebind `BudgetingState` off the domain + `Money`→`decimal` (started: the `Fmt` on-ramp, `7002c63`)
  - ◻ Phase 3 — drop the `FinApp.Domain` `ProjectReference`; confirm the bundle no longer ships the domain assembly

**Already done (so the gate is closer than it looks):**
- ✅ **Thin read API complete** — Phase-0 coverage audit (Session 54) verified every Dashboard surface has a DTO,
  incl. the what-if forecast inputs, reallocation caps, expense-entry helpers, and period-selectable insights.
- ✅ **Thin write API functionally complete** — Sessions 44–46 (every `BudgetingState` write has a command endpoint).
- ✅ **Insights narrative is language-independent** (`InsightMessageDto` code+args) — native clients localize locally.
- ✅ **Forecast math extracted** to `FinApp.Forecasting` (pure, portable).

**Secondary prerequisites (can run in parallel with the gate):**
- ◻ **Offline / caching story** — a thin client degrades on a train; design it before native (endpoints exist, the cache doesn't).
- ◻ **Full end-to-end verification pass** (register → account → budgets → expense → savings → recurring) — freeze a known-good baseline.
- ◻ **Settle UX/domain changes** you don't want to rewrite three times once frozen into native.
- ◻ **README refresh** — still describes the retired local-first SQLCipher/MAUI design.
- ◻ **Bank-import provenance** — the one deferred read+write; prod-only (bank sync uncredentialed in dev).

**Not required first:** row-per-entity persistence (a later payoff, not a prerequisite).

**Tooling gates (defer until the gate above is near):**
- ✅ **Android SDK/JDK installed (Session 55).** Android Studio 2026.1.2 + JBR 21 were already present (the "not installed"
  note was stale); SDK (platform/build-tools 35, platform-tools, emulator, android-35 system image) + Gradle 8.10.2 added.
  Build/run recipe in the Session-55 HANDOFF + saved to agent memory.
- ⏸️ **iOS ON HOLD (2026-08-04)** — was blocked on Mac / cloud-Mac access anyway; now a deliberate hold, not
  just a gate. See "The two-platform decision" at the top. Android is not blocked.

---

## What exists today
- ~~**`FinApp.App.Maui`**~~ — **removed (Session 37).** The MAUI Blazor Hybrid scaffold is deleted and
  dropped from the solution; this also unblocked a clean **full-solution build** (it had been failing on
  the missing `maui-tizen` workload).
- **`FinApp.Shared.UI`** — the whole Blazor UI (`Dashboard.razor` ~6k lines) + client services
  (`BudgetingState`, `AuthState`, `SyncClient`, `FinAppApiClient`, `Localizer`, `InsightsService`,
  `AchievementsService`). This stays — it is the **web** app.
- **`FinApp.Server`** — sync/auth API on Cloud Run, plus snapshot storage (gzipped, KMS-encrypted at
  rest). It **does** deserialize account contents today (exports), and holds real bank transactions.

## The decisive architecture fact
**Today the client runs the C# domain model directly.** `Shared.UI` references `FinApp.Domain` +
`FinApp.Contracts`; `BudgetingState` deserializes the snapshot **on-device** and computes the money
model, forecasting, insights, achievements, reallocation and recurring logic **client-side**. The
server stores and syncs, and reads the blob only for exports.

**Dropping MAUI means that C# cannot come with us.** Kotlin and Swift can't run `FinApp.Domain`. So
native-per-platform forces one of two answers, and this is now *the* load-bearing decision on this page:

| Option | What it means | Cost | Verdict |
|---|---|---|---|
| **A — Server-side domain (thin clients)** | Move the money model behind a richer REST API. Android and iOS become UI over endpoints that return computed figures. | One server refactor, then each platform is *only* UI. | **Recommended** |
| **B — Reimplement per platform** | Port the money model, serializer, forecasting, insights, recurring and reallocation into Kotlin *and* Swift. | Two full domain ports, and **three** implementations (C#, Kotlin, Swift) that must agree on money maths forever. | **Rejected** |

**B is not viable for a finance app.** Three independent implementations of the same money maths is
three places for rounding, carryover and projection to disagree — and the disagreement shows up as
wrong numbers in someone's budget, silently. The 178 domain tests would have to be written three times
to catch it, and any future rule change lands three times.

**Why A is now open — this is the change from the previous version of this doc.**
This page used to reject a server-side domain as "breaking the privacy design", on the premise that
the server stored an **opaque blob it never deserialized**. **That premise was retired in Session 31**
(`9b923fb`): `AccountExportService` already fully deserializes the snapshot to render exports, and bank
sync already stores real transactions (date/amount/description) under a server-held key. The ratified
trust model is **"the server may read your data"**; confidentiality comes from encryption at rest plus
access control, not from server blindness. So moving the domain server-side **forfeits nothing that is
still true** — it just relocates code the server is already entitled to run.

### What Option A buys beyond mobile
- **The web client thins out too** — one domain, one place to fix a money bug, one set of tests.
- **It dissolves the whole-snapshot write.** `AccountSnapshotRow`'s own note says every mutation
  rewrites the entire account, so save cost scales with total history rather than edit size, and that
  "is the last thing holding the shape of a design we no longer follow". A server-side domain can
  persist real rows and update what changed. That is the *structural* fix behind Session 31/34's save
  work, which so far has only made the blob smaller (~6.7×), not smaller-per-edit.
- **Push gets easier** — the server already knows what's due, so it can send without a client awake.

### What Option A costs — read before committing
- **It is the largest single change in the project's history.** The money model, forecasting, insights,
  achievements, recurring and reallocation all move behind an API that does not exist yet.
- **The API surface grows a lot.** `FinApp.Contracts` today syncs a blob; it would need endpoints for
  every computed read the UI draws.
- **Offline stops being free.** The client currently holds the whole account and computes locally. A
  thin client needs a caching/offline story or it degrades on a train.
- **It should be done incrementally**, behind the existing web app, *before* any native code is
  written — the web UI is the proof the API is complete.

---

## iOS needs a Mac
Building and code-signing iOS uses Apple's Xcode toolchain, which only runs on macOS — Apple's
requirement, true of Swift, Flutter, RN and MAUI alike. You don't have to own one: rent cloud macOS
(Codemagic, Bitrise, GitHub `macos` runners). **Android needs no Mac**, which is why it goes first.

---

## Phase 0 — verify + pre-mobile changes on the current app  ← we are here
Harden and confirm the existing web app **before** committing to the port, so it starts from a
known-good baseline.
- ✅ **Option A ratified (Session 37)** — server-side domain. See the Open decisions note.
- ✅ **`FinApp.App.Maui` retired (Session 37)** — full-solution build is green again.
- ☐ Full end-to-end verification pass (register → account → budgets → expense → savings → recurring).
- ☐ Any UX/domain changes to settle before they're frozen into a native rewrite.
- ☐ **README refresh** — the "Tech decisions"/persistence copy still describes the retired local-first
  SQLCipher/MAUI design; the Storage/sync rows were corrected in Session 37 but the rest is due a pass.

## Phase 1 — server-side domain (Option A, ratified) — API COMPLETE; web cutover is the remaining work
Grow the computed-read API endpoint-by-endpoint from the snapshot the server already loads, **no
persistence change required** (the snapshot store stays; row-per-entity persistence is a later payoff,
not a prerequisite). Each read = a pure domain service + a `FinApp.Contracts` DTO + an endpoint + tests,
mirroring what `BudgetingState` computes so the numbers can't drift. Reads before mutations; the Blazor
web app stays the acceptance test throughout.

> **Status (Session 54):** the read **and** write API is now complete (Sessions 37–54; Phase-0 coverage audit
> confirmed every Dashboard surface has a DTO). The remaining Phase-1 work is the **web-client cutover** — running
> the web app thin with no client-side domain — which is tracked in its own plan, [DOMAIN-REMOVAL.md](DOMAIN-REMOVAL.md)
> (Path B). That cutover *is* the pre-native gate. The endpoint-by-endpoint history below is kept for reference.

**Shipped so far (Session 37, not yet wired into the web client):**
- ✅ `GET /accounts/{id}/overview` → `AccountOverviewDto` — the balance-header figures
  (`current/free/saved/spent/contributed/billsDue/safeAfterBills`). Domain: `AccountOverview.For`.
- ✅ `GET /accounts/{id}/runway` → `RunwayDto` (204 when no basis) — the cash runway. Domain:
  `AccountForecast.Runway`.

**Next reads (increasing cost):**
- ✅ `GET /accounts/{id}/targets` → `TargetsDto` (empty list when nothing to project). Domain:
  `AccountForecast.Targets` (+ `AccountTarget`/`TargetKind`) — the all-debts debt-free date (each debt at its
  installment + demonstrated pace, latest clears) plus each savings goal at its pace. Mirrors `Dashboard.HomeTargets`
  / `DebtFreeMonthsAtPace`; 4 domain tests pin the math. NOT wired into the web client yet.
- ✅ `GET /accounts/{id}/milestones` → `MilestonesDto(Earned, Total, InProgress)`. **`AchievementsService` moved
  Shared.UI → `FinApp.Domain.Services`** (it only ever depended on domain reads; the client passes its `fmt`/`translate`
  for localized copy, the server ignores copy and counts). New `AchievementsService.Counts` + `MilestoneCounts`; 3
  domain tests. Single source of truth — the count can't drift from the on-screen catalogue. NOT wired into the web client.
- ✅ `GET /accounts/{id}/insights` → `InsightsDto` (empty when the latest period has nothing to score). **The wall
  is cleared: `InsightsService` moved Shared.UI → `FinApp.Domain.Services`.** Its only Shared.UI tie was `CategoryIcons`
  on two lines — decoupled by carrying the category's **raw stored icon** (client resolves the display icon), same as
  Targets. The DTO exposes the **structural** figures — gauge score/band, savings rate/target/shortfall, outgoings
  trend, per-category breakdown; the **localized narrative** (verdict, signal cards, savings critique, quick-wins)
  stays a per-client concern (the domain bakes it in English via a `translate` delegate). 3 domain tests (was
  untestable in Shared.UI). NOT wired into the web client. **Follow-on:** restructure signals/verdict into structured
  data so native clients can localize them — the narrative isn't in the API yet.

**Writes (the mutation API) — STARTED:**
The write half of Phase 1. The client used to mutate the aggregate locally and PUT the whole snapshot; the mutation
API lets a thin client send just a command. The spine is **`SnapshotService.MutateAsync<T>`** — a server-side
read-modify-write: load (contributor auth + decrypt) → deserialize → apply a `Func<Account,T>` → serialize → save
under optimistic concurrency. Domain validation (`InvalidOperationException`/`ArgumentException`) surfaces as **400**.
Concurrency is now backed by a real token: **`AccountSnapshots.Version` is an EF concurrency token** (migration
`AddSnapshotVersionConcurrencyToken` — model-only, empty Up/Down), so a write that lost a race throws
`DbUpdateConcurrencyException` instead of silently clobbering. On that, MutateAsync **reloads the winner's state and
re-applies the mutation** (bounded retry, then a 409) — so the `mutate` delegate must be a pure function of the
account it's handed. The whole-snapshot `SaveAsync` now also translates the token failure to a clean **409** (it
used to be able to clobber a concurrent write). Every future mutation reuses this spine.
- ✅ **Account bootstrap** — `POST /accounts/{id}/bootstrap` (optional `BootstrapAccountRequest(Today?)`, 409 if
  already set up) → `MutationResultDto(v1, id)`. Seeds a freshly-created account's snapshot server-side (the header
  from the relational account via `CreateForHeader`, then the shared **`Account.SeedStarter(today)`** — default
  categories/contribution-categories/funds + the first current-month period + achievements anchor). The starter seed
  **moved into the domain** and the web client's `SeedStarterBody` now delegates to it, so a native and a web account
  start byte-identically. `today` dates the first period to the caller's local month (server UTC when omitted). This
  is the thin-client counterpart of the web app's first-load seed — a native client can now create an account without
  carrying the domain. 5 server tests (`BootstrapApiTests`).
- ✅ **Expenses** — the manual capture loop, mirroring `BudgetingState.AddExpense/EditExpense/RemoveExpense`:
  - `POST /accounts/{id}/expenses` → `MutationResultDto(Version, EntityId)`. Body `AddExpenseRequest`
    (category, amount, fund, date, note, onBehalfOfOtherAccount). Member = caller; `FundSynced` derived from the
    fund — neither is in the request. Validates category/fund exist (else 400); posts to the open period.
  - `PUT /accounts/{id}/expenses/{expenseId}` — `EditExpenseRequest`; append-only edit; preserves bank provenance,
    clears the auto-filed badge.
  - `DELETE /accounts/{id}/expenses/{expenseId}`.
  - 8 server tests (`ExpenseMutationApiTests`), each confirmed **through the /overview read** so the two halves
    prove each other. NOT wired into the web client (same reads-first discipline).
  - **⚠️ Scope gap to close in a later mutation slice:** settlement (on-behalf) mirroring and bank-import
    provenance are cross-account / bank-flow concerns and are **not** handled here — editing/removing a
    settlement-linked expense through this API won't keep its counterpart in step (the web app's whole-snapshot
    path still does).
- ✅ **Deposits (income)** — mirroring `BudgetingState.RecordDeposit/EditDeposit/RemoveDeposit`:
  - `POST /accounts/{id}/deposits` (`AddDepositRequest`: contribution category — empty = general income — fund,
    amount, date). Member = caller; `FundSynced` derived; deposits with the same (member, category, fund) **merge**
    into one row (the response `EntityId` is that row's id). `PUT`/`DELETE .../{depositId}` edit/remove.
  - Deposits are **per-member**: edit/remove of someone else's deposit is a **403** (`ForbiddenException`, thrown
    from inside the mutate delegate so it bypasses the 400 translation) — stricter/cleaner than the web client's
    in-process guard. 8 server tests (`DepositMutationApiTests`), confirmed through /overview's Contributed.
- ✅ **Savings money-movements** — mirroring `BudgetingState.AllocateSaving/EditSavingDeposit/RemoveSavingDeposit/SpendFromSavings`:
  - `POST /accounts/{id}/savings/deposits` (`AddSavingDepositRequest`: bucket, amount, date, note?) — earmarks money
    within the balance (raises "saved", lowers "free"; nothing leaves). `PUT`/`DELETE .../{allocationId}` edit/remove
    a manual deposit (the domain replaces the row on edit — append-only).
  - `POST /accounts/{id}/savings/spend` (`SpendFromSavingsRequest`: bucket, spend category, amount, date, fund?, note?)
    — records a real expense **and** a matching negative drawdown, so the earmark and the balance both fall. Member =
    caller; empty `FundId` derives the web default (first non-synced top-level fund); `FundSynced` set from the chosen
    fund (a correctness nudge over the web, which only ever picks a non-synced fund). Validates bucket/category/fund.
  - The `priorSaved` the web passes to allocate/edit is **unused by the domain**, so it's omitted. 8 server tests
    (`SavingsMutationApiTests`), confirmed through /overview (Saved/Free/Current/Spent).
- ✅ **Savings-bucket CRUD/config** — mirroring `BudgetingState.AddSavingBucket/SaveSavingBucket` + archive/remove:
  - `POST /accounts/{id}/savings/buckets` (create) / `PUT .../{bucketId}` (update) share one `SaveSavingBucketRequest`
    (the 18-field upsert) applied by **`SavingBucketConfig.Apply`** (Server) so the two can't drift. Kind is chosen by
    flags in the web's priority order — debt → investment → ordinary goal — and `IsExpensesFund` (sinking fund for
    `Costs`, a language-independent `PlannedCostDto` with a string cadence) clears any goal. `PlannedContribution`,
    `FundId`, and `InitialAmount` (honoured only while the account has a single period) apply regardless. Debt balance
    anchored to the server UTC date.
  - `PUT .../{bucketId}/archived` (`SetArchivedRequest`) archive/restore; `DELETE .../{bucketId}` remove (domain
    blocker on sub-buckets / savings activity → 400). 9 tests (`SavingBucketApiTests`), verified by deserializing the
    snapshot and inspecting the `SavingCategory`.
- ✅ **Savings bucket money-movements** (completes the savings story) — mirroring `DisburseSaving/ConvertSavingToBudget/MoveSavingToBucket`:
  - `POST /accounts/{id}/savings/disburse` — deploy a bucket to its goal: money out from the chosen fund (external
    transfer, not an expense) + a drawdown; on a debt bucket also an extra principal payment (`RecordSavingDebtPayment`,
    a no-op otherwise). `POST .../savings/to-budget` — mature a save into a category's budget (no money moves, releases
    the earmark). `POST .../savings/transfer` — move between buckets (net-neutral). `DELETE .../savings/movements/{id}`
    — undo any of the three. 7 tests (`SavingBucketMovementApiTests`), verified via /overview + snapshot.
  - **⚠️ Like the web, the domain does NOT enforce "can't deploy more than the bucket holds"** — the caller owns that
    (the web UI does; a native client must too, or a later slice adds server-side enforcement).
- ✅ **Account-structure CRUD** — spend categories, funds, contribution categories (mirroring the client Add/Edit/Archive/Remove):
  - Categories: `POST /accounts/{id}/categories` (name, parent?, icon, essential) · `PUT .../{categoryId}` (rename+icon,
    essential applied only when sent) · `PUT .../{categoryId}/archived` · `DELETE .../{categoryId}`.
  - Funds: `POST /accounts/{id}/funds` (name, parent?, note, icon) · `PUT .../{fundId}` · `PUT .../{fundId}/archived` ·
    `DELETE .../{fundId}?moveOpeningBalancesTo={fundId}` (consolidate opening balances before removal, total-preserving).
  - Contribution categories: `POST /accounts/{id}/contribution-categories` · `PUT .../{catId}` · `DELETE .../{catId}`.
  - All domain guards (unique names, valid parents, removal blockers, last-fund) → 400. 12 tests (`StructureCrudApiTests`),
    verified via snapshot; removal blockers exercised with a real referencing expense/deposit created through the endpoints.
  - **Note:** fund *transfers* + opening-balance edits are period money-movements (a separate later slice); `archived`
    here is a plain hide — the web's move-balance-then-archive convenience comes with that slice.
- ✅ **Recurring items** (bills / income expectations) — mirroring the client's recurring methods:
  - CRUD + pause: `POST /accounts/{id}/recurring` (kind/mode as language-independent strings via `RecurringMap` →
    domain enums; validates category-for-kind + fund; stamped with the server date) · `PUT .../{recurringId}`
    (kind can't change) · `PUT .../{recurringId}/active` · `DELETE .../{recurringId}`.
  - Due handlers: `POST .../{recurringId}/confirm` (posts a real expense/income with the actual amount, tunes a
    "typical" estimate, marks handled) · `POST .../{recurringId}/skip` (marks handled, posts nothing).
  - **Posting single-sourced in the domain:** the confirm/auto-post logic moved to **`Period.PostRecurring`**
    (Domain) and the web client's private `PostRecurring` now delegates to it, so the web and the server confirm
    endpoint can't drift. +3 domain tests (`PeriodPostRecurringTests`), 9 server tests (`RecurringApiTests`).
- ✅ **Remaining writes — DONE (Sessions 44–46):** period lifecycle, budgets, fund transfers + opening balances,
  reallocation, statement import (+ dedupe), settlement (two-account helper). The **only** deferred write is
  **bank-import provenance** (`ConfirmBankMoneyOutAsTransfer`) — prod-only, bank sync uncredentialed in dev.

**Deferred / to settle:**
- ☐ **Wire the web client** to the endpoints — deliberately NOT done per read/write; a piecemeal hybrid (client
  computes some figures, fetches others) adds a network round-trip for data it already holds. Cut over in
  one meaningful chunk once enough reads exist. Mind the **live-bank-balance adjustment**, which the header
  applies client-side and the server figures deliberately omit (identical only when no fund is synced).
- ☐ **Offline/caching story** — a thin client needs one; design endpoints with it in mind.
- **Exit criteria:** the web app runs against the API with **no client-side domain computation left.**

## Phase 2 — native Android (Kotlin / Jetpack Compose)  ← STARTED (Session 55)
- ✅ Install Android SDK/JDK on the dev box (Session 55).
- ✅ Stand up auth against prod (`POST /auth/login`) — done + verified against live; first screen (Home overview) built.
- Port the remaining surfaces: Home (started), Spending, Goals, Wallets, Insights, Recurring, bank review, modals.
- Persistent token store (DataStore) + refresh (`POST /auth/refresh`) — next.
- Re-implement the EN/BG strings against Android resources (`Localizer` does not come along).
- ✅ Go/no-go on the port after the first real screen — **GO** (login+overview slice built, app boots + reaches live API).

## Phase 3 — native iOS (Swift / SwiftUI)
- Same surfaces against the same API. Needs Mac/cloud-Mac access.

## Phase 4 — native-only wins
- **Push notifications** (FCM/APNs) — deferred backlog item #10; recurring bills-due + savings nudges.
- **Biometric unlock** (Face ID / fingerprint) gating a finance app.
- Deep links for OAuth code exchange and the Enable Banking `/bank/callback`.
- **On-device SMS/notification importer** — the lowest-friction capture loop (Beyond Budget's edge: a
  transaction notification is parsed into a pending expense with no export/upload ritual). This is the single
  most on-strategy capture feature we could build **because** it can beat their convenience *without* their
  privacy cost — but only under a hard rule (see the red line below). Android only (iOS forbids reading SMS).

### ⚠️ Privacy red line for any capture feature (SMS parse, receipt OCR, "smart" categorisation)
Our one clean differentiator is **"your raw data is never sold or fed to AI."** The convenience features that
out-capture us (Beyond Budget's SMS auto-import, AI receipt scan, AI suggestions) are exactly the ones that
**breach that promise the instant raw data touches an off-device AI/cloud service.** So the rule is absolute:

- **Any capture/categorisation that uses ML/AI MUST run strictly on-device with ZERO raw-data egress.** No cloud
  OCR call, no categorisation LLM over the wire, no "we only send the merchant string" — that is still egress.
- On-device only: local notification/SMS regex/parsing, on-device OCR (e.g. platform Vision/MLKit **local**
  models), on-device categorisation against the user's own history (we already do merchant→category mapping).
- If a feature can't be done on-device, **don't ship it** rather than quietly relax the claim. One convenient
  cloud API call is enough to make "never fed to AI" a lie, and that claim is the reason to exist vs Beyond
  Budget. Convenience bought by breaking it turns us into "Beyond Budget with worse mobile."
- Statement import (CSV/OFX/QIF/…) stays the privacy-preserving baseline: the user hands over a file they
  control, parsed locally — no aggregator, no AI. On-device SMS import is the same principle, made continuous.

## Phase 5 — distribution
- Android: signed AAB → Play Console (internal testing first).
- iOS: Apple Developer account ($99/yr), TestFlight → App Store. **Needs macOS** (cloud is fine).
- Mobile CI: Gradle for Android; `macos-latest` / Codemagic / Bitrise for iOS.

---

## Open decisions
- ~~**⚠️ Option A vs B**~~ — **RATIFIED: A (server-side domain), Session 37.** B (reimplement in
  Kotlin + Swift) stays rejected — three implementations of the same money maths that must agree forever.
  The web app is the incremental acceptance test; native starts only once it runs with **no** client-side
  domain computation left.
- ~~**Retiring `FinApp.App.Maui`**~~ — **DONE (Session 37).** Removed from the solution and deleted;
  full-solution build green. (No `maui` note in TRANSFER.md; no solution filters referenced it.)
- **Mac access** — user expects access "soon"; iOS is blocked until then, Android is not.
- ~~**Phase 0 scope**~~ — **captured (Session 54)** in the "Pre-native prerequisites — the checklist" section at the
  top of this doc. The gate is the web-client thinning ([DOMAIN-REMOVAL.md](DOMAIN-REMOVAL.md) Path B, Phases 2–3).
