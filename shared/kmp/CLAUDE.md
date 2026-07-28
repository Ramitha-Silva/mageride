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
    domain/geo/       H3 geocells, view + hysteresis, exact distance   (C017)
    mqtt/             MqttConfig, PositionPayload, AdaptiveRateEngine  (C017)
    realtime/         SignalR `/hubs/live` contract + map scope        (C017)
    db/               SQLDelight schema, outbox, GPS ring, retention   (C018)
    util/             BusinessCalendar (C016), ReconnectBackoff (C017)
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

## Geo & real-time plane (`domain/geo` + `mqtt` + `realtime`, C017)
- **The passenger view is H3 res-7 + `ring(2)` = 19 cells (R-06).** res-8 + ring(1) is the
  superseded figure and reaches about 1 km; `GeoRealtimeHygieneTest` fails the build if any file in
  these three packages names resolution 8. Dispatch's coarse pre-filter is res-5 + ring(1–2).
- **A geocell is never a distance bound.** `exactWithin` (haversine) is mandatory after any cell
  lookup — ADD §7.4 step 5 in one function. `GeoDistanceTest` demonstrates that some of the 19
  subscribed cells sit beyond the 3 km the view claims, which is why this is not an optimisation.
  C015's Directional predicate uses the same three formulae; there is one implementation.
- **[H3Grid] is a seam, not a re-implementation.** Cell ids must be bit-identical to the ones
  `position-processor-svc` computes or a client joins `cell:{h3index}` groups nothing publishes to
  — a failure that looks exactly like an empty map. Android binds `com.uber:h3`; **iOS has no
  binding yet** (`platformH3Grid()` answers `null`) and C085/C094 must supply one, which an app
  module can because it is appended after `sharedModules`. Everything else in these packages is
  common code and needs no engine. The index *layout* — resolution, base cell, the `7` markers — is
  read in common Kotlin by `H3Cell` and checked against the library in `AndroidH3GridTest`.
- **Group churn is held for 30 s after a boundary crossing, and a reconnect is not churn.**
  `GeoCellSubscription` applies the first crossing immediately, then holds; crossing back cancels
  the held one. `onReconnected()` re-joins everything regardless — after a drop the server holds no
  membership at all, and rate-limiting recovery would blank the map for half a minute.
- **The cadence table is data, and its defaults are derived from three sources at once.**
  `GpsPhase` carries D5' §5.2's ranges; the default is the slow end of the range except inside
  AL-12's 1 s near-geofence burst, which is the only rule satisfying AL-12 *and* D5' §5.1's base
  cadence. A `setPosRate` hint overrides it, expires with its envelope, and is dropped by a phase
  change. `AdaptiveRateEngine` also refuses a publish that would breach the 5 msg/s broker
  ceiling: being throttled by EMQX emits `mqtt.rate_violation` into `audit.events`, so leaning on
  the broker to rate-limit the client generates a fraud signal.
- **`seq` is strictly monotonic per vehicle and must survive a restart.** If `PositionSequencer`
  rewinds, `position-processor-svc` discards everything published afterwards and the vehicle goes
  dark while the app believes it is publishing — **C018 persists the watermark**. Replay is a
  separate topic, unlocked 2 s after a reconnect, capped at 20/s and yielding to live 4:1.
- **The driver home map joins no geocell group at all (AL-31).** `LiveMapScope.DriverHomeMap` has
  no cells to join by construction; the driver's own marker comes from the device's own GNSS. A
  driver on a ride still joins `ride:{rideId}`, which is a different membership rule.
- **Method and event names on `/hubs/live` are resolved by string.** A typo is a handler that is
  never invoked, not a compile error — so they are spelled once, in `LiveHub`, and asserted against
  `backend/contracts/realtime/signalr-hub.md`. The hub credential is the 30-minute API access
  token, never the MQTT session JWT (E-02).

## On-device database (`db` + `src/commonMain/sqldelight`, C018)
- **Two databases, not one, and they must never merge.** `mobile_db_schema.md` §0.2 gives each app
  its own file — passenger gets §1 + §2, driver gets §1 + §3 — so `:shared` builds
  `MageRidePassengerDatabase` and `MageRideDriverDatabase` separately.
  `MageRidePassengerDatabase.Schema.create()` physically cannot make a `dispatch_offers` table.
- **The §1 shared tables are authored ONCE**, in `src/commonMain/sqldelight/shared/`, and a Gradle
  `Sync` materialises them into each database's own package under `build/generated/`. That step is
  not optional: SQLDelight derives a generated type's package from its path under the source root,
  so pointing both databases at one directory emits `…Command_outbox` twice into one commonMain
  compilation and the module stops compiling. **Edit the file in `sqldelight/shared/`, never a copy
  under `build/`.**
- **The SQLite dialect is pinned at the 3.18 default because minSdk 26 is.** Android 8.0 ships
  SQLite 3.19, so `ALTER TABLE … RENAME COLUMN` (3.25), UPSERT (3.24) and row-value `IN` (3.15) are
  all unavailable. Migrations rebuild tables the portable way. SQLCipher links its own newer SQLite,
  but an unencrypted build falls through to the platform engine, so the floor still applies.
- **Schema version 2. `1.sqm` is `mobile_db_schema.md` §8** (Δ 2026-07-05 #2 — AL-47 `qr_claimed_at`,
  AL-48's unmasked phone columns, `qr_receipt`). `SchemaMigrationTest` builds a v1 database, migrates
  it, and asserts the result is **structurally identical** to a fresh `create()` — table set, columns,
  indexes and normalised DDL. That comparison is what stops `.sq` and `.sqm` drifting; keep it.
- **Only `Instant` and `LocalDate` have column adapters.** Timestamps are epoch **milliseconds**
  (§0.3), business dates are `'YYYY-MM-DD'` **already in Asia/Colombo** (D-38 — derive them through
  `util/BusinessCalendar`, never the handset's zone). `INTEGER AS Boolean` needs no adapter;
  `INTEGER AS Int` needs SQLDelight's `IntColumnAdapter`. Enum-ish columns stay `TEXT` on purpose —
  an adapter that threw on an unknown server state would crash the app on a deploy.
- **`INSERT OR IGNORE` suppresses every constraint, not just the primary key.** `command_outbox` and
  `proof_upload_queue` therefore use a plain `INSERT` (a swallowed command is a lost user action);
  `gps_buffer` keeps `OR IGNORE` because a repeated `(vehicle_id, seq)` is the designed path and the
  only CHECK on it is written from our own enum.
- **`seq` must never rewind.** `PersistentPositionSequencer` reserves blocks of 100 and persists the
  high-water mark to `meta('gps.seq.{vehicleId}')` — **per vehicle**, which §1.12's illustrative
  `'gps.seq'` is not. A restart skips the unused tail, which is fine: `seq` has to be strictly
  increasing, not gapless. A rewind makes `position-processor-svc` discard everything published
  afterwards and the vehicle goes dark while the app believes it is publishing.
- **The projections are not behind an abstraction, and that is deliberate.** Only the machinery
  `:shared` itself implements — `CommandOutbox`, `GpsBuffer`, `MetaStore`, `Retention` — has a store
  interface with two implementations. `rides` has one consumer (the passenger app) and
  `dispatch_offers` has one (the driver app), so they are reached through the generated queries on
  `PassengerDb.sql` / `DriverDb.sql`.
- **Everything is blocking.** SQLDelight's Android and Native drivers are synchronous and this module
  does not pick a dispatcher for four apps. Run the drain worker and the retention sweep off the main
  thread.
- **No secret is ever a column.** §0.4 keeps tokens in C014's `SecureStore`; `auth_session` holds
  expiry hints and a `jti`, and the package OTPs (P-07) are typed in and POSTed. `MobileSchemaTest`
  fails the build if a token- or OTP-shaped column appears — `trip_shares.token` is the one
  exception, and it is a public share handle, not a credential.
- **Encryption is per platform.** Android is SQLCipher (`net.zetetic:sqlcipher-android`) keyed from
  `DatabaseKeyManager` over the Keystore-backed `SecureStore`; **iOS is
  `NSFileProtectionCompleteUntilFirstUserAuthentication`**, which §0.4 permits ("SQLCipher or GRDB
  encryption") and which needs no third party. The passphrase seam is carried but unapplied on iOS —
  same shape as C017's H3 seam. `AfterFirstUserAuthentication`, not `Complete`, because the driver
  app writes `gps_buffer` with the handset locked.
- **`localDbModule` binds two things and deliberately not the database.** Opening it is `suspend`
  (the key comes out of the Keystore), and a `runBlocking` single would put that round trip on
  `Application.onCreate`. The app binds a `DatabaseDriverFactory` and opens the database itself.

## Dependency rules
- **Every version lives in `gradle/libs.versions.toml`.** Never inline one here.
- One catalog entry is declared but deliberately **not applied**: `multiplatform-settings`, which C011
  reserved for C014 and C014 did not take — both of its app-side backends are plain settings
  (`SharedPreferencesSettings`, `NSUserDefaultsSettings`), which is exactly what C014's DoD forbids.
  `ktor-client-auth` is likewise unused: refresh-on-401 is a send-pipeline interceptor (C013), which
  is what makes "same `Idempotency-Key` on the replay" expressible.
- **`com.google.android.play:integrity` is androidMain-only** and `implementation`, not `api`: no
  Play Integrity type appears in this module's public surface. The iOS half needs no coordinate —
  App Attest comes from Kotlin/Native's `DeviceCheck` platform library, and the Keychain from
  `Security`.
- **`com.uber:h3` is JNI, not Kotlin Multiplatform.** Its jar carries android-arm/arm64, linux
  and darwin natives and no klib, so it is an **androidMain-only** dependency (applied by C017,
  `implementation` — `H3JavaGrid` is internal and the public surface is our own `H3Grid`). The iOS
  side needs cinterop against an H3 built for `ios-arm64`, which cannot be produced on this Linux
  host: `platformH3Grid()` therefore answers `null` on iOS and C085/C094 bind their own.
- **SQLDelight is `api` in commonMain** (C018): `SqlDriver`, `ColumnAdapter` and the generated
  `MageRide*Database` types are all in this module's public surface, because an app builds the driver
  and holds the database. The platform drivers are `implementation` — no androidx.sqlite or SQLiter
  type reaches an app. `net.zetetic:sqlcipher-android` is **androidMain-only** (an AAR with per-ABI
  natives, no Kotlin/Native counterpart) and `app.cash.sqldelight:sqlite-driver` is
  **androidHostTest-only** — it is a real SQLite, which is what lets the schema, the migration and
  every query be tested on this Linux host.
- **`kotlinx-serialization-cbor` is applied in commonMain** (C017) and is `implementation`:
  `PositionCodec` is the only door to it, so no CBOR type reaches an app or the XCFramework. It
  serialises the same `PositionSample` the JSON surface does — one DTO, two wires.
- Add a Koin module per component and append it to `sharedModules` in `di/SharedModule.kt` —
  do not grow `sharedCoreModule`. Apps are told to use `sharedModules`, so a new binding must
  never require an edit in all four of them.
