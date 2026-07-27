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
- **`-Xexpect-actual-classes` is on** (C014). `expect class` is still flagged Beta (KT-61573) even
  though `expect fun` is stable, and the class form is what `PlatformSecureStore` and
  `PlatformAttestationProvider` need: their constructors genuinely differ per platform (a `Context`
  versus a Keychain service name), which an expect *function* cannot express.
- **A KDoc must not contain `/*`.** Kotlin block comments nest, so `contracts/*.yaml` inside a
  `/** … */` opens a second comment and the file stops parsing several declarations later. Write
  the path without the glob.

## Source-set layout (ADD §18.2)
```
src/commonMain/kotlin/lk/mageride/shared/
    data/models/      DTOs — Position, Trip, Vehicle, Fare, Wallet     (C012)
    data/api/         Ktor client for the REST surfaces                (C013)
    data/repository/  repository abstractions                          (C012-C018)
    domain/auth/      OTP sign-in, token lifecycle, MQTT token         (C014)
    domain/ride/      Mode C ride machine, cancellation, package/proxy (C015)
    domain/dispatch/  offers, Driver Level, Job Board, directional     (C015)
    domain/fare/      Mode C fare rules, surcharges, payment machine   (C016)
    domain/wallet/    balance, daily fee, vouchers, credit transfer    (C016)
    domain/subscription/  Mode B billing cycle + subscriber payments   (C016)
    domain/geo/       H3 geocells, adaptive rate logic                 (C017)
    mqtt/             MqttConfig, PositionPayload, AdaptiveRateEngine  (C017)
    db/               SQLDelight queries + drivers                     (C018)
    util/             BusinessCalendar (Asia/Colombo dates, C016)
    platform/         PlatformInfo, SecureStore, attestation (expect)  (C011, C014)
    (ADD §18.2 draws the first of those two as `domain/trip/`. That layout predates R-01, which
     split the Mode C ride out of the tracking session; `domain/trip` under the current vocabulary
     would name trip-state-svc's Mode A/B aggregate, which is not in this module at all. The
     manifest's C015 deliverable already says `domain/ride`. See the C015 handoff.)
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
  `401` refreshes once and replays once, and a second `401` is `onAuthenticationLost()`.
  `refresh(staleAccessToken)` is told which token failed, so a provider that has already rotated
  past it can replay without rotating again. Both default to the no-op binding, so the graph
  resolves before C014's module is added. `ktor-client-auth` is still unapplied and is not needed —
  the refresh is an `HttpSend` interceptor, which is what makes "same `Idempotency-Key` on the
  replay" expressible.
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

## Session layer (`domain/auth` + `platform`, C014)
- **[AuthSessionManager] is the only thing that touches a token.** `SessionState.SignedIn` carries
  a user id, a device id and `isNewUser` — never a token — and the tokens leave the class through
  exactly one door, `SessionTokenProvider`, which the HTTP pipeline holds. A view model that wants
  a bearer token is asking the wrong question.
- **Phone OTP only (AL-07).** `IamApi.signInWithGoogle` / `…Apple` / `…Password` and the admin pair
  exist for the portals; nothing under `domain/auth` may call one, and
  `PlatformSecurityHygieneTest` fails the build if something does.
- **Concurrent refreshes collapse on the token that failed, not on a lock.** `refresh` takes the
  access token the failing request sent; a caller whose token has already been replaced replays
  instead of rotating. The refresh token is single-use and racing it revokes the whole session
  family (D-29) — the mutex alone does not prevent that, because a caller can acquire it *after*
  the rotation it was waiting for.
- **Offline is not revoked.** `onAuthenticationLost` ends the session for a refused credential and
  does nothing for a network failure or 5xx. Never widen that: it is what stops a driver being
  signed out mid-ride by a tunnel.
- **The MQTT token is a different token (E-02)** with its own TTL and its own renewal loop; it is
  never the API access token, and a failed renewal never drops the token already in hand.
  `MqttSessionTokenManager.token` is a `StateFlow` because EMQX validates the JWT at CONNECT, so a
  rotation only takes effect on the next connection — C017's client has to reconnect on it.
- **`SecureStore` is the only place a secret is persisted.** Android encrypts with an Android
  Keystore AES-GCM key into a private preferences file; iOS uses Keychain items with
  `kSecAttrAccessibleAfterFirstUnlockThisDeviceOnly` (`AfterFirstUnlock`, not `WhenUnlocked`, so a
  locked handset can still renew mid-ride). C018's SQLite file holds no token —
  `mobile_db_schema.md` §1.1 stores only expiry timestamps and a `jti`.
- **Four app-supplied bindings** across C013 + C014: `HttpClientEngine`, `ApiConfig`, `AuthConfig`
  (the app surface — there is no safe default) and `SecureStore`. Optionally
  `AttestationProvider` → `PlatformAttestationProvider`; without it D-30's twenty operations fail
  at the edge, which is the honest outcome.
- **`X-Attestation` wire format** is the gateway's (C008), not any spec's: Android sends the Play
  Integrity token unwrapped, iOS sends `base64url(keyId) "." base64url(assertion)` signed over
  `SHA-256("<METHOD> <path>")`. That last part is why `AttestationRequest` carries the method and
  path rather than only an `operationId`.

## Mode C domain (`domain/ride` + `domain/dispatch`, C015)
- **Mode C only (R-01).** `domain/ride` is the ride-svc aggregate. Mode A/B tracking sessions are
  trip-state-svc's and are not modelled here — not now, not later.
- **[RideTransitions] is ADD Appendix B.2 as data, and it is the only place a transition exists.**
  `next()` is a map lookup; nothing branches its way to a state the table does not list.
  `RideTransitionTableTest` re-declares the appendix independently and sweeps all 18 × 20
  state/trigger pairs, so an added edge fails the build rather than a screen.
- **The client never advances a ride.** `RideProjection` moves only through `onServerState(…)`, and
  a server-confirmed move the table does not draw is **applied and flagged**, never dropped —
  ride-svc is the sole writer, so refusing its answer would show a passenger a ride that had
  already ended. `verdict(command)` is the other direction: a local guess that saves a round trip,
  never a claim about what the server would allow.
- **Two edges are in the table but not in the Appendix B.2 diagram**, both carried by other parts
  of the same spec and both called out at the declaration: `Matching → Accepted` (D5' §6.1's accept
  guard `state IN ('Matching','Offered')`) and `Accepted|DriverArrived → NoShowDriver` (the D5' §7
  matrix row).
- **An expired offer is never sent.** `OfferSession.accept()` / `decline()` check the 15-second
  deadline against the clock before touching the network. `409 offer-already-accepted` and
  `410 offer-expired` stay distinct all the way to `OfferOutcome.Taken` / `Expired`.
- **Every threshold that the server can tune is read, never baked.** The directional predicate
  takes `DirectionalConfig` and the level rules take `LevelConfig`; `DriverLevelRules.D5_DEFAULTS`
  is a fallback for a client that has not read the admin config yet, not a constant. Fixed numbers
  — the 15 s offer TTL, the 5 OTP attempts, Rs 50 / Rs 100, the R-16 grace windows — are named
  constants citing the spec line that fixes them.
- **Almost none of it is in the Koin graph.** `rideDispatchModule` binds one thing, `OfferSession`
  (the driver's single offer slot, ADD Appendix B.2 invariant 3), and needs nothing from an app
  that C013 does not already ask for. Everything else is a value type built from the config just
  read — binding a `DirectionalPredicate` at start-up would pin whatever the thresholds were when
  the app launched.

## Money domain (`domain/fare` + `domain/wallet` + `domain/subscription`, C016)
- **The client never computes the authoritative fare.** `fare-svc` prices every ride and the
  `fareEstimateToken` binds the quoted price, so no number computed here can become what a
  passenger is charged. `FareCalculator` mirrors D5' §1.1 to *render* and *explain* a price and to
  make the §1.3 rounding testable; `FareCalculator.of(response, …)` takes the server's total as
  given and only decomposes it.
- **One rounding rule, in one place.** `FareRounding` is banker's rounding (half-to-even) to a
  whole minor unit, and every percentage — the peak/night uplift, the OnePay 5%, the voucher
  discount — goes through it as an **exact rational**, never through a `Double`. Nothing is rounded
  at an additive step.
- **`fares.peak_windows.multiplier_pct` is deliberately not modelled.** §1.1 reads the *tariff's*
  `peak_surcharge_pct` / `night_surcharge_pct`; a window decides only whether an uplift applies.
  Two sources of the same 20/15 would eventually disagree.
- **Every business date is Asia/Colombo** and goes through `util/BusinessCalendar` (D-13, D-38).
  A `fee_date`, a `period_month` or a `next_due` answered from the device's zone is wrong for five
  and a half hours a day.
- **`ModeCTier` has no ETA and no distance field** (AL-19). The fence is the shape of the type, not
  a rule in a screen; `ModeCTiers.arrivalVisible(state)` is the one place that says when an arrival
  time becomes legitimate (`RideState.isDriverAssigned`, i.e. `Accepted` onward — not `Offered`).
- **`PaymentTransitions` is D-10 + AL-47 as data**, and `PaymentProjection` moves only through
  `onServerState(…)` — same rule as `RideProjection`: a server-confirmed move the table does not
  draw is **applied and flagged**, never dropped. `PaymentStatus` carries no version, so ordering
  is enforced by "a terminal payment is never walked back" instead.
- **R-05's `Paid` / `CashSettled` are RIDE states, not payment states.**
  `PaymentTransitions.settlementTrigger` / `settledRideState` is the only join between the two
  machines; `DriverConfirmedQR` settles as `CashSettled` (AL-47, "settles like cash").
- **A credit transfer moves the exact value** (AL-01). `CreditTransferRules.entryFor` produces two
  postings that sum to zero and nothing else; `LedgerEntry`'s own `init` refuses anything that does
  not balance (D-09, the client-side mirror of `billing.assert_balanced()`).
- **Top-up is OnePay card / OnePay wallet / LankaQR — there is no fourth** (AL-05). Mode B's
  `online_transfer` is a *different thing*: a passenger paying a fleet owner directly, pass-through
  money that never touches a wallet or the platform ledger. `MoneyDomainHygieneTest`
  (androidHostTest) fails the build if either boundary moves.
- **Nothing in `domain/subscription` builds a `LedgerEntry`** — Mode B money is a pass-through to
  the owner (§18b) and MageRide holds none of it.
- **`fareWalletModule` binds nothing, on purpose.** Every input here is admin-tunable and
  server-supplied — tariffs, windows, fee tiers, voucher ladder, low-balance threshold — so a
  binding would pin whatever the numbers were at launch. Build the value types at the call site
  from the config just read. Read the module's KDoc before adding a binding.

## Dependency rules
- **Every version lives in `gradle/libs.versions.toml`.** Never inline one here.
- Three catalog entries are declared but deliberately **not applied**: `sqldelight` and its drivers
  (C018 — it also applies the Gradle plugin), `h3` (C017), and `multiplatform-settings`, which C011
  reserved for C014 and C014 did not take — both of its app-side backends are plain settings
  (`SharedPreferencesSettings`, `NSUserDefaultsSettings`), which is exactly what C014's DoD forbids.
  `ktor-client-auth` is likewise unused: refresh-on-401 is a send-pipeline interceptor (C013), which
  is what makes "same `Idempotency-Key` on the replay" expressible.
- **`com.google.android.play:integrity` is androidMain-only** and `implementation`, not `api`: no
  Play Integrity type appears in this module's public surface. The iOS half needs no coordinate —
  App Attest comes from Kotlin/Native's `DeviceCheck` platform library, and the Keychain from
  `Security`.
- **`com.uber:h3` is JNI, not Kotlin Multiplatform.** Its jar carries android-arm/arm64, linux
  and darwin natives and no klib, so it is an androidMain/JVM dependency only; the iOS side
  needs cinterop against the H3 C library. C017 owns that expect/actual.
- Add a Koin module per component and append it to `sharedModules` in `di/SharedModule.kt` —
  do not grow `sharedCoreModule`. Apps are told to use `sharedModules`, so a new binding must
  never require an edit in all four of them.
