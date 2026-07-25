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

This doc is the single source of truth for the mobile plan. Update it as decisions firm up.

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
- ◻ **iOS blocked on Mac / cloud-Mac access** — Android is not.

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
