# TandemTab — mobile / native roadmap

**Status:** planning. **Decision (2026-07-12):** go **full native** (native UI, not the Blazor
Hybrid WebView). Mobile work is **deferred** until a round of verification + pre-mobile changes on
the current app lands first (see Phase 0).

This doc is the single source of truth for the mobile plan. Update it as decisions firm up.

---

## What exists today
- **`FinApp.App.Maui`** — a MAUI **Blazor Hybrid** scaffold (native shell, but UI renders `Shared.UI`
  Razor in a WebView). Currently pinned to `net9.0-windows` only; Android/iOS/MacCatalyst commented
  out. Already branded to `com.tandemtab.app` / "TandemTab" and pointed at prod in Release (2026-07-12).
- **`FinApp.Shared.UI`** — the whole Blazor UI (`Dashboard.razor` ~2k lines) + client services
  (`BudgetingState`, `AuthState`, `SyncClient`, `FinAppApiClient`, `Localizer`, `InsightsService`,
  `AchievementsService`).
- **`FinApp.Server`** — sync/auth API on Cloud Run. Stores an **opaque, client-owned snapshot blob**;
  it never deserializes account contents (privacy design).

## The decisive architecture fact
**The client runs the C# domain model directly.** `Shared.UI` references `FinApp.Domain` +
`FinApp.Contracts`; `BudgetingState` deserializes the snapshot **on-device** via
`AccountSnapshotSerializer` and computes the money model, forecasting, insights, achievements,
reallocation and recurring logic **client-side**. The server only ever sees the encrypted blob.

This means "full native" is really a question of **how much of that C# logic survives the port**:

| Path | UI | Client domain/logic (`FinApp.Domain`, `BudgetingState`, serializer, insights…) | Verdict |
|---|---|---|---|
| **MAUI native XAML** | rewrite Razor → XAML | **kept as-is (still C#)** | **Recommended** |
| **.NET native (iOS/Android bindings, no MAUI)** | rewrite native | kept as-is (C#) | Viable, more plumbing |
| **Flutter** | rewrite in Dart | **reimplement all of it in Dart, or move it server-side** | Large; breaks the privacy design |
| **React Native** | rewrite in TS | **reimplement all of it in TS, or move it server-side** | Large; breaks the privacy design |

Flutter/RN don't just rewrite the screens — they force you to either **reimplement the entire
client-side domain** in Dart/TS (the money model, snapshot (de)serialization, forecasting, recurring,
reallocation, insights) **or relocate it to the server**, which would undo the deliberate
client-owned-opaque-snapshot privacy architecture. That's months of work and a security-model change,
independent of any UI benefit.

**Recommendation: full native via MAUI native XAML.** It rewrites only the presentation layer while
keeping `FinApp.Domain`, the serializer, and the client services intact — the app's actual brain. If a
non-.NET stack is chosen later, the prerequisite is a server-side-domain refactor, tracked separately.

## iOS needs a Mac — in every framework
Building and code-signing an iOS app uses Apple's Xcode toolchain, which only runs on macOS. This is
true for MAUI, Flutter, React Native, and native Swift alike — it is Apple's requirement, not the
framework's. You don't have to *own* one:
- **React Native + Expo (EAS Build)** runs the macOS build in Expo's cloud — the smoothest "no Mac" path.
- **MAUI / Flutter** → rent cloud macOS (Codemagic, Bitrise, GitHub `macos` runners).
- **Android needs no Mac** in any framework.

So the Mac requirement is *not* a reason to prefer Flutter/RN over MAUI — only Expo's managed cloud
softens it, and that benefit is dwarfed by the client-domain rewrite cost above.

---

## Phase 0 — verify + pre-mobile changes on the current app  ← we are here
Harden and confirm the existing web/Hybrid app **before** committing to the native port, so the port
starts from a known-good baseline. Concrete items **TBD — to be filled in from the user's list.**
Candidates worth folding in here:
- Full end-to-end verification pass (register → account → budgets → expense → savings → recurring).
- Any UX/domain changes the user wants settled before they're frozen into a native UI rewrite.
- Confirm the client/server contract surface (`FinApp.Contracts`) is stable — it's the one seam the
  native UI will bind to.

## Phase 1 — native shell proof (Android first)
- Choose the concrete stack (default: **MAUI native XAML** per above).
- Install `maui` workload + Android SDK/JDK on the dev box (not present today).
- Stand up one real native screen (e.g. the auth panel) bound to the existing `AuthState`/`BudgetingState`.
- Run on an Android emulator, log in against prod. Go/no-go on the port.

## Phase 2 — port the UI
- Rebuild the `Dashboard` surfaces as native views over the **unchanged** client services: Home,
  Budgets, Funds, Debt/Savings, Insights, Recurring, bank review, modals.
- Reuse `Localizer` (EN/BG) and the existing state/change-notification model.

## Phase 3 — native-only wins
- **Push notifications** (FCM/APNs) — the item deferred from backlog #10; recurring bills-due + savings nudges.
- **Biometric unlock** (Face ID / fingerprint) gating a finance app.
- Deep links for the OAuth code exchange (`finappTakeAuthCode`) and Enable Banking `/bank/callback`.

## Phase 4 — distribution
- Android: signed AAB → Play Console (internal testing first).
- iOS: Apple Developer account ($99/yr), TestFlight → App Store. **Needs macOS** (cloud is fine).
- Mobile CI on workload-equipped runners (GitHub `macos-latest` for iOS; Codemagic/Bitrise alt).

---

## Open decisions
- **Concrete native stack** — MAUI native XAML (recommended) vs .NET native vs Flutter/RN (each
  Flutter/RN requires the server-side-domain refactor first).
- **Fate of the Hybrid scaffold** — repurpose the same `FinApp.App.Maui` project (MAUI supports native
  XAML + Blazor in one project) or retire it.
- **Mac access** — user expects access "soon"; iOS is blocked until then, Android is not.
- **Phase 0 scope** — the specific verify/change list the user wants done first.
