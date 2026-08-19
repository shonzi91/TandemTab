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

## Getting a build onto a phone

`.\gradlew.bat :app:assembleRelease` produces `app/build/outputs/apk/release/tandemtab-release.apk`, and
[`../.github/workflows/android.yml`](../.github/workflows/android.yml) builds the same APK on every push
(downloadable from the run's **Artifacts**) plus attaches one to a **GitHub Release** when an `android-v*`
tag is pushed — a release asset is the only link a tester can open without a GitHub account.

```
# stamp a build the way CI does (defaults are 1 / 0.1.0 when omitted)
.\gradlew.bat :app:assembleRelease -PversionCode=57 -PversionName=0.2.0
```

**Signing.** With no key configured the release APK is signed with the **debug** key — installable for testing,
and refused by both the Play Store and the workflow's release step. The build prints a warning saying so; check
which key actually signed an APK with:

```
& "$env:LOCALAPPDATA\Android\Sdk\build-tools\35.0.0\apksigner.bat" verify --print-certs <apk>
```

To sign for real, generate a key **once** and keep it safe — losing it means never being able to update the
app under the same package name again:

```
keytool -genkeypair -v -keystore tandemtab-release.jks -alias tandemtab -keyalg RSA -keysize 2048 -validity 10000
```

Then, in this folder, create `keystore.properties` (gitignored, alongside `local.properties`):

```
storeFile=tandemtab-release.jks
storePassword=…
keyAlias=tandemtab
keyPassword=…
```

For CI, set four repository secrets — `ANDROID_KEYSTORE_BASE64` (`base64 -w0 tandemtab-release.jks`),
`ANDROID_KEYSTORE_PASSWORD`, `ANDROID_KEY_ALIAS`, `ANDROID_KEY_PASSWORD`. The workflow writes exactly the same
`keystore.properties` from them, so local and CI signing have one shape to get right rather than two.
The `TANDEMTAB_KEYSTORE_FILE` / `_PASSWORD`, `TANDEMTAB_KEY_ALIAS` / `_PASSWORD` environment variables are read
as a fallback if you'd rather not write the file.

**Installing.** `adb install -r <apk>` over USB, or send the APK to the phone and open it (Android asks once for
permission to install from that source). ⚠️ A phone holding a debug-key build cannot be updated in place by a
release-key build or vice versa — the signature differs, so uninstall first.

## Next
See [../docs/MOBILE.md](../docs/MOBILE.md) Phase 2. Near-term: persistent token store (DataStore) +
token refresh, then port the next surfaces (Spending, Goals, Wallets, Insights) against the existing
thin read API.
