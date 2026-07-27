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

## Model layer (`data/models`, C012)
- **One package per service contract**, named after the contract file: `data.models.{iam, registry,
  trip, ride, dispatch, fare, subscription, wallet, query, transit, safety, support, content,
  comms, version}`. Everything from `backend/contracts/_shared.yaml` — `Money`, `GeoPoint`,
  `Place`, `Page<T>`, `ProblemDetails`, `PositionSample` and the canonical enums — sits in the
  `data.models` root, so nothing service-specific is ever imported to get a primitive.
- **The contract is the shape.** An `allOf` is flattened into one data class (kotlinx.serialization
  cannot compose), a `oneOf(X, null)` is a nullable field, and a `oneOf(A, B)` is two types plus two
  client overloads. Required → non-null, optional → nullable with `= null`. Do not "tidy" a payload
  into a nicer shape: the round-trip tests compare against the contract, not against taste.
- **`encodeDefaults = false`, so a defaulted field is not sent.** A field the contract marks
  `required` *and* gives a `const`/default (`currency: LKR`, `mode: C`) therefore carries
  `@EncodeDefault(ALWAYS)`. Adding a default to a required field without it silently drops it.
- **Money is `Long` minor units, never `Double`.** Flat `…Minor` fields stay flat and the DTO
  implements `MoneyHolder` to expose them as `Money`; the ledger's signed `amountMinor` is
  deliberately not a `Money`.
- **Enums match the DB CHECK domain exactly** and are asserted against it in
  `EnumWireFormatTest`. Wire spellings that are not upper camel case carry an explicit
  `@SerialName` plus a `wire` property for non-serialisation callers; the two must not drift.
- **Portal-only contracts are not modelled here** — `admin-bff`, `fleet`, `provisioning`,
  `public-bff` and `reputation` are Next.js surfaces, and their DTOs belong to the portals'
  TypeScript client. See the C012 handoff.

## API layer (`data/api`, C013)
- **One interface per contract file** — `data.api.{iam, registry, trip, ride, dispatch, fare,
  subscription, wallet, query, transit, safety, support, content, comms, version}`, mirroring
  C012's model packages (`comms` carries `VoipApi` + `NotificationApi`). The interface is the seam
  the app layer injects; the `Ktor…Api` implementation next to it is `internal`. [MageRideApi]
  bundles all sixteen for Swift, and `apiModule` binds each one individually for Kotlin.
- **All 176 operations are covered, including the mTLS and webhook ones.** They are unreachable
  from an app and say so in their KDoc; they exist so no contract is half-covered.
  `ContractCoverageTest` (androidHostTest) fails the build if an operation, its HTTP verb, or its
  `X-Attestation` flag drifts from the YAML.
- **The transport applies every D3' §0 convention, so a client never does.** `ApiTransport` +
  `apiGet/apiPost/apiPostExempt/apiPut/apiDelete` set the absolute URL, `X-App-Version`,
  `X-Platform`, the `Idempotency-Key`, and the attributes the send pipeline reads. Do not call
  `HttpClient` directly from a client.
- **The `Idempotency-Key` is minted before the first send and never re-minted.** Retries and the
  post-refresh replay reuse the same request builder, which is what makes a repeat a *replay*
  (R-14/R-18). Every POST method also takes `idempotencyKey: String? = null` so a user-driven
  retry can pass the original. `apiPostExempt` is for the six `x-idempotency-exempt` provider
  callbacks only, and those are never retried.
- **The whole send pipeline lives in one `HttpSend` interceptor** (`MageRideHttpClient.kt`), in
  this order: attestation → circuit breaker → retry/backoff → auth refresh → RFC 7807 mapping.
  Read that KDoc before adding a plugin; ordering between two `HttpSend` interceptors is exactly
  the kind of thing that works until it does not.
- **C014 supplies `TokenProvider` and `AttestationProvider`; C013 owns when they are called.** A
  `401` refreshes once and replays once, and a second `401` is `onAuthenticationLost()`. Both
  default to the no-op binding, so the graph resolves before C014 lands. `ktor-client-auth` is
  still unapplied and is not needed — the refresh is an `HttpSend` interceptor, which is what
  makes "same `Idempotency-Key` on the replay" expressible.
- **Errors are `MageRideError`, keyed on status for the type and on the kebab code for the
  branch.** `409 offer-already-accepted` is `Conflict`, `410 offer-expired` is `Gone`; never
  collapse the two. Never render `message`/`title`/`detail` to a user — the apps resolve Si/Ta/En
  copy from `code` (D-26).
- **`426` is both thrown and published** on `MageRideApiSignals.upgradeRequired` (replay 1), so an
  app puts up one update wall instead of handling D-31 at 176 call sites.
- **The app must bind an `HttpClientEngine` and an `ApiConfig`.** Nothing else in `apiModule`
  needs the app. `followRedirects = false` is deliberate — see the comment in
  `mageRideHttpClient`.
- **Paging goes through `data/repository/CursorPagedSource`**, not a bespoke loop per screen.

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
