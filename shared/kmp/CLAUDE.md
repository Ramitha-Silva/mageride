# KMP Shared Module Conventions
- Kotlin Multiplatform, targets: Android + iOS (iosX64 / iosArm64 / iosSimulatorArm64)
- Contains: DTOs, API client, domain logic, auth module, local DB, test kit
- This module is built FIRST — all 4 apps depend on it
- No platform-specific code here — use expect/actual only when unavoidable
- Gradle project path is `:shared` (the directory is shared/kmp); versions come from
  gradle/libs.versions.toml
- Verify: `./gradlew :shared:testDebugUnitTest detekt ktlintCheck` (the wave-1 gate)

## Build host
- Needs an **Android SDK** (`platforms;android-36`, `build-tools;36.0.0`). Point Gradle at it
  with `ANDROID_HOME` or an untracked `local.properties` (`sdk.dir=…`).
- Build host is Linux: verify common + Android targets here. `gradle.properties` enables
  Kotlin/Native klib cross-compilation, so `./gradlew :shared:compileKotlinIosArm64`
  **type-checks** `src/iosMain` on Linux — use it after writing any iOS `actual`. That is a
  compile check only: linking, `assembleXCFramework` and `iosTest` need macOS with Xcode, and
  **iOS targets are never marked DONE from this host**.
- `./gradlew :shared:assembleXCFramework` (D7' §6) is defined here and fails fast with a
  readable message on any non-macOS host. On macOS it builds the release
  `MageRideShared.xcframework` that C085 / C094 import as `import MageRideShared`.

## Build script facts worth knowing before you edit it
- **AGP 9 refuses `com.android.library` in a KMP project.** The Android target comes from
  `com.android.kotlin.multiplatform.library` and is configured inside `kotlin { android { … } }`
  — there is no top-level `android { }` block and there are no build variants.
- **`testDebugUnitTest` is an alias.** With no variants the real task is `testAndroidHostTest`;
  the alias exists because `build/manifest.yaml`, this wave's gate and `ci.yml` all spell the
  old name. `androidHostTest` dependsOn `commonTest`, so the alias runs commonTest too.
- **`explicitApi()` is on.** Every declaration this module publishes needs an explicit
  visibility and return type — it is the API surface for four apps and two languages.
- **detekt reads `config/detekt/detekt.yml`** with `buildUponDefaultConfig = true`; the plain
  `detekt` task is pointed at `src`, so new source sets are covered without a build-script edit.
- **ktlint reads the repo-root `.editorconfig`** (`ktlint_code_style = intellij_idea`). Run
  `./gradlew :shared:ktlintFormat` before arguing with it.

## Source-set layout (ADD §18.2)
```
src/commonMain/kotlin/lk/mageride/shared/
    data/models/      DTOs — Position, Trip, Vehicle, Fare, Wallet     (C012)
    data/api/         Ktor client for the REST surfaces                (C013)
    data/repository/  repository abstractions                          (C012-C018)
    domain/auth/      JWT + refresh, session                           (C014)
    domain/trip/      trip + ride state machines                       (C015)
    domain/dispatch/  offer handling, Driver Level System              (C015)
    domain/fare/      Mode C fare rules, surcharges                    (C016)
    domain/wallet/    balance + transaction history                    (C016)
    domain/geo/       H3 geocells, adaptive rate logic                 (C017)
    mqtt/             MqttConfig, PositionPayload, AdaptiveRateEngine  (C017)
    db/               SQLDelight queries + drivers                     (C018)
    util/             DateTimeUtils, Validators
src/androidMain/  Android actuals + the OkHttp Ktor engine
src/iosMain/      iOS actuals + the Darwin Ktor engine
src/commonTest/   runs on every target
src/androidHostTest/  JVM-only tests of the Android actuals (NOT `androidUnitTest`)
```

## Dependency rules
- **Every version lives in `gradle/libs.versions.toml`.** Never inline one here.
- Four catalog entries are declared but deliberately **not applied yet**, each owned by a later
  component: `multiplatform-settings` + `ktor-client-auth` (C014), `sqldelight` and its drivers
  (C018 — it also applies the Gradle plugin), `h3` (C017).
- **`com.uber:h3` is JNI, not Kotlin Multiplatform.** Its jar carries android-arm/arm64, linux
  and darwin natives and no klib, so it is an androidMain/JVM dependency only; the iOS side
  needs cinterop against the H3 C library. C017 owns that expect/actual.
- Add a Koin module per component and append it to `sharedModules` in `di/SharedModule.kt` —
  do not grow `sharedCoreModule`. Apps are told to use `sharedModules`, so a new binding must
  never require an edit in all four of them.
