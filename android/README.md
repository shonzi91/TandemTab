# TandemTab — Android (native)

Native Android client (Kotlin + Jetpack Compose), talking to the same server API as the web app.
This is the start of the native track described in [../docs/MOBILE.md](../docs/MOBILE.md) — Option A
(server-side domain, thin clients). The app carries **no** domain logic; it renders figures the server
computes and localizes.

## Stack
- Kotlin 2.0 · Jetpack Compose (Material 3) · single-Activity
- Ktor client + kotlinx.serialization for the HTTP/JSON layer
- minSdk 26 (Android 8.0) · compileSdk/targetSdk 35 · JDK 17 language level (built with Studio's JBR 21)

## What works today (first vertical slice)
- **Sign in** against `POST /auth/login` (`https://tandemtab.com` by default; see `API_BASE_URL` in
  `app/build.gradle.kts`).
- **Home overview** — lists the signed-in user's accounts (`GET /accounts`), shows the balance-header
  figures (`GET /accounts/{id}/overview`): current / free / saved / spent / contributed / bills due /
  safe-after-bills. Multi-account users get a chip switcher.
- Token is held **in memory** only (no "stay signed in" yet); 2FA accounts are detected and told the app
  doesn't support them yet.

## Build / run
The Android SDK lives at `%LOCALAPPDATA%\Android\Sdk`; `local.properties` (gitignored) points at it.
Builds use Android Studio's bundled JDK (JBR 21). From this folder:

```
# JDK 17+ required by the Android Gradle Plugin — use Studio's JBR:
$env:JAVA_HOME = "C:\Program Files\Android\Android Studio\jbr"
.\gradlew.bat :app:assembleDebug        # build the APK
.\gradlew.bat :app:installDebug         # install to a running emulator/device
```

Or open this folder in Android Studio and Run.

## Next
See [../docs/MOBILE.md](../docs/MOBILE.md) Phase 2. Near-term: persistent token store (DataStore) +
token refresh, then port the next surfaces (Spending, Goals, Wallets, Insights) against the existing
thin read API.
