# TandemTab — mobile / native roadmap

**Status:** planning.
**Decision (2026-07-12):** go **full native** — native UI, not the Blazor Hybrid WebView.
**Decision (2026-07-19): no MAUI.** The platforms get their own native stacks: **Android first
(Kotlin / Jetpack Compose), then iOS (Swift / SwiftUI).** MAUI — including the MAUI-native-XAML path
this doc previously recommended — is off the table, and the existing `FinApp.App.Maui` Hybrid
scaffold is to be **retired**, not repurposed.

Mobile work stays **deferred** until Phase 0 lands (see below).

This doc is the single source of truth for the mobile plan. Update it as decisions firm up.

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

## Phase 1 — server-side domain (Option A, ratified) — IN PROGRESS
Grow the computed-read API endpoint-by-endpoint from the snapshot the server already loads, **no
persistence change required** (the snapshot store stays; row-per-entity persistence is a later payoff,
not a prerequisite). Each read = a pure domain service + a `FinApp.Contracts` DTO + an endpoint + tests,
mirroring what `BudgetingState` computes so the numbers can't drift. Reads before mutations; the Blazor
web app stays the acceptance test throughout.

**Shipped so far (Session 37, not yet wired into the web client):**
- ✅ `GET /accounts/{id}/overview` → `AccountOverviewDto` — the balance-header figures
  (`current/free/saved/spent/contributed/billsDue/safeAfterBills`). Domain: `AccountOverview.For`.
- ✅ `GET /accounts/{id}/runway` → `RunwayDto` (204 when no basis) — the cash runway. Domain:
  `AccountForecast.Runway`.

**Next reads (increasing cost):**
- ☐ **Targets** — the "on track for" goal/debt payoff dates. Bigger: iterates buckets, composes
  `LoanForecast` + savings pace per row.
- ☐ **Milestones** count (`AchievementsService` — currently Shared.UI).
- ☐ ⚠️ **Health score / insights** — the real wall: `InsightsService` lives in **`Shared.UI`, not the
  domain**, so it must be ported into the domain first before it can move server-side.

**Deferred / to settle:**
- ☐ **Wire the web client** to the endpoints — deliberately NOT done per read; a piecemeal hybrid (client
  computes some figures, fetches others) adds a network round-trip for data it already holds. Cut over in
  one meaningful chunk once enough reads exist. Mind the **live-bank-balance adjustment**, which the header
  applies client-side and the server figures deliberately omit (identical only when no fund is synced).
- ☐ **Offline/caching story** — a thin client needs one; design endpoints with it in mind.
- **Exit criteria:** the web app runs against the API with **no client-side domain computation left.**

## Phase 2 — native Android (Kotlin / Jetpack Compose)
- Install Android SDK/JDK on the dev box (not present today).
- Stand up auth against prod, then port the surfaces: Home, Spending, Goals, Wallets, Insights,
  Recurring, bank review, modals.
- Re-implement the EN/BG strings against Android resources (`Localizer` does not come along).
- Go/no-go on the port after the first real screen.

## Phase 3 — native iOS (Swift / SwiftUI)
- Same surfaces against the same API. Needs Mac/cloud-Mac access.

## Phase 4 — native-only wins
- **Push notifications** (FCM/APNs) — deferred backlog item #10; recurring bills-due + savings nudges.
- **Biometric unlock** (Face ID / fingerprint) gating a finance app.
- Deep links for OAuth code exchange and the Enable Banking `/bank/callback`.

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
- **Phase 0 scope** — the specific verify/change list the user wants done first.
