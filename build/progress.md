# MageRide — Build Progress

Single source of truth for build state. One row per component from `build/manifest.yaml`.
After completing a component, set its Status and append the 3-line handoff under
**Session Handoffs** at the bottom of this file.

- Status values: `PENDING` · `IN PROGRESS` · `DONE` · `PARTIAL` · `BLOCKED`
- No wave N+1 work begins until every wave N verify command passes.
- Total components: **132** · estimated sessions: **267**

## Wave gates

| Wave | Components | Gate |
|------|-----------|------|
| 0 | C001–C010 (10) | all DDL applies twice cleanly, shared kernel + gateway tests green, slim compose healthy, CI parses |
| 1 | C011–C019 (9) | `./gradlew :shared:testDebugUnitTest detekt ktlintCheck` green (common + Android targets; iOS verified on macOS) |
| 2 | C020–C044 (25) | walking skeleton books one ride end to end + every core service test suite green |
| 3 | C045–C066 (22) | every business service test suite green; ledger balances in all money tests |
| 4a | C067–C084 (18) | both Android apps build and every owned SCR-* screen matches its wireframe |
| 4b | C085–C102 (18) | both iOS apps build and test **on macOS**; parity with 4a confirmed screen-for-screen |
| 4c | C103–C117 (15) | all three web surfaces lint + test + build; zero runtime CSS-in-JS in any bundle |
| 5 | C118–C126 (9) | contract + E2E suites green against the deployed replica; day-0 GTFS feed active |
| 6 | C127–C132 (6) | no open high/critical security findings; load, chaos and SG acceptance reports signed off |

## Components

| ID | Component | Wave | Status | Session Date | Notes |
|----|-----------|------|--------|--------------|-------|
| C001 | repo-scaffold | 0 | DONE | 2026-07-27 | verify chain green; 3 spec gaps + root-`build/` collision recorded below |
| C002 | backend-shared-kernel | 0 | DONE | 2026-07-27 | 152 tests green; 2 micro-change-sets raised (command-log column, direct DSN) |
| C003 | db-schema-identity-registry | 0 | DONE | 2026-07-27 | 13 scripts, 24 tables, 40/40 verify checks; 4 micro-change-sets raised |
| C004 | db-schema-trips-rides-dispatch | 0 | DONE | 2026-07-27 | 21 scripts, 26 tables, 84/84 verify checks; 2 micro-change-sets raised, 1 actioned |
| C005 | db-schema-business-content | 0 | DONE | 2026-07-27 | 29 scripts, 53 tables, 151/151 verify checks; 3 micro-change-sets raised |
| C006 | db-schema-telemetry-timescale | 0 | DONE | 2026-07-27 | 4 scripts, 67 total, 187/187 verify checks; 2 micro-change-sets raised (both blocking as printed) |
| C007 | openapi-contracts | 0 | DONE | 2026-07-27 | 21 service contracts + shared library, 262 operations, spectral 0 errors; 5 micro-change-sets raised |
| C008 | api-gateway-yarp | 0 | DONE | 2026-07-27 | 59 routes / 21 clusters, 524 tests green; 5 micro-change-sets raised |
| C009 | docker-compose-dev | 0 | DONE | 2026-07-27 | slim stack healthy, 66/66 verify checks; 6 blocking spec fixes + 6 micro-change-sets raised |
| C010 | ci-skeleton | 0 | DONE | 2026-07-27 | 7 CI jobs, 3 Dockerfile templates, MageRide.TestKit; 685 tests green; wave 0 complete |
| C011 | kmp-module-scaffold | 1 | DONE | 2026-07-27 | 10 tests green, detekt + ktlint clean; AGP 9 forced the KMP Android plugin — `testDebugUnitTest` is now an alias (micro-change-set) |
| C012 | kmp-core-models | 1 | DONE | 2026-07-27 | 300 public types over 16 contracts, 132 tests green; 3 micro-change-sets raised (ADD Appendix A, trip-state enums, §19) |
| C013 | kmp-api-client | 1 | DONE | 2026-07-27 | 16 typed clients covering all 176 operations, 91 new tests (223 total) green; 1 micro-change-set raised (`ReportFormat.wire`) |
| C014 | kmp-auth-session | 1 | DONE | 2026-07-27 | 277 tests green (54 new); Keystore/Keychain SecureStore + Play Integrity/App Attest; 3 micro-change-sets raised |
| C015 | kmp-domain-ride-dispatch | 1 | DONE | 2026-07-27 | 384 tests green (107 new); Appendix B.2 as data + exhaustive 18×20 sweep; 4 micro-change-sets raised |
| C016 | kmp-domain-fare-wallet | 1 | DONE | 2026-07-27 | 543 tests green (159 new); §1.3 banker's rounding + 14-state payment machine; 6 micro-change-sets raised |
| C017 | kmp-geo-realtime | 1 | DONE | 2026-07-27 | 654 tests green (111 new); H3 is a platform seam — **iOS has no engine yet** (C085/C094 bind one); 3 micro-change-sets raised |
| C018 | kmp-local-db | 1 | PENDING | | |
| C019 | kmp-test-kit | 1 | PENDING | | |
| C020 | ws-iam-minimal ⭑ | 2 | PENDING | | |
| C021 | ws-registry-minimal ⭑ | 2 | PENDING | | |
| C022 | ws-ride-svc-happy-path ⭑ | 2 | PENDING | | |
| C023 | ws-dispatch-stub ⭑ | 2 | PENDING | | |
| C024 | ws-realtime-pipeline ⭑ | 2 | PENDING | | |
| C025 | ws-e2e-android-slice ⭑ | 2 | PENDING | | |
| C026 | iam-svc-auth | 2 | PENDING | | |
| C027 | iam-svc-profile-rbac | 2 | PENDING | | |
| C028 | registry-svc-vehicles | 2 | PENDING | | |
| C029 | registry-svc-onboarding | 2 | PENDING | | |
| C030 | provisioning-svc | 2 | PENDING | | |
| C031 | trip-state-svc | 2 | PENDING | | |
| C032 | ride-svc-core | 2 | PENDING | | |
| C033 | reputation-svc | 2 | PENDING | | |
| C034 | dispatch-svc-core | 2 | PENDING | | |
| C035 | dispatch-svc-scheduling-levels | 2 | PENDING | | |
| C036 | dispatch-svc-directional | 2 | PENDING | | |
| C037 | ride-svc-proxy-package | 2 | PENDING | | |
| C038 | mqtt-bridge-svc | 2 | PENDING | | |
| C039 | position-processor-svc | 2 | PENDING | | |
| C040 | persistence-writer-svc | 2 | PENDING | | |
| C041 | fanout-svc | 2 | PENDING | | |
| C042 | query-svc | 2 | PENDING | | |
| C043 | tcp-adapter | 2 | PENDING | | |
| C044 | fleet-health-svc | 2 | PENDING | | |
| C045 | content-svc | 3 | PENDING | | |
| C046 | wallet-svc | 3 | PENDING | | |
| C047 | subscription-svc-daily-fee | 3 | PENDING | | |
| C048 | subscription-svc-mode-b | 3 | PENDING | | |
| C049 | fare-svc-core | 3 | PENDING | | |
| C050 | fare-svc-payments | 3 | PENDING | | |
| C051 | notification-svc | 3 | PENDING | | |
| C052 | safety-svc | 3 | PENDING | | |
| C053 | support-svc | 3 | PENDING | | |
| C054 | ocr-svc | 3 | PENDING | | |
| C055 | voip-svc | 3 | PENDING | | |
| C056 | transit-svc-routing | 3 | PENDING | | |
| C057 | transit-svc-gtfs-lifecycle | 3 | PENDING | | |
| C058 | fleet-svc-org | 3 | PENDING | | |
| C059 | fleet-svc-fleet-ops | 3 | PENDING | | |
| C060 | fleet-billing-svc | 3 | PENDING | | |
| C061 | analytics-read-model | 3 | PENDING | | |
| C062 | admin-bff-core | 3 | PENDING | | |
| C063 | admin-bff-verification | 3 | PENDING | | |
| C064 | admin-bff-directories | 3 | PENDING | | |
| C065 | admin-bff-finance-pdpa | 3 | PENDING | | |
| C066 | public-bff | 3 | PENDING | | |
| C067 | driver-android-shell | 4a | PENDING | | |
| C068 | driver-android-auth-onboarding | 4a | PENDING | | |
| C069 | driver-android-vehicle-onboarding | 4a | PENDING | | |
| C070 | driver-android-dashboard-dispatch | 4a | PENDING | | |
| C071 | driver-android-delivery | 4a | PENDING | | |
| C072 | driver-android-jobs-level-earnings | 4a | PENDING | | |
| C073 | driver-android-wallet-credit | 4a | PENDING | | |
| C074 | driver-android-tracker-sharing-profile | 4a | PENDING | | |
| C075 | driver-android-comms-safety-support | 4a | PENDING | | |
| C076 | passenger-android-shell | 4a | PENDING | | |
| C077 | passenger-android-auth-onboarding | 4a | PENDING | | |
| C078 | passenger-android-live-map-search | 4a | PENDING | | |
| C079 | passenger-android-booking | 4a | PENDING | | |
| C080 | passenger-android-ride-payment | 4a | PENDING | | |
| C081 | passenger-android-package-history | 4a | PENDING | | |
| C082 | passenger-android-mode-b-subscriptions | 4a | PENDING | | |
| C083 | passenger-android-settings-addresses | 4a | PENDING | | |
| C084 | passenger-android-comms-safety-support | 4a | PENDING | | |
| C085 | driver-ios-shell | 4b | PENDING | | |
| C086 | driver-ios-auth-onboarding | 4b | PENDING | | |
| C087 | driver-ios-vehicle-onboarding | 4b | PENDING | | |
| C088 | driver-ios-dashboard-dispatch | 4b | PENDING | | |
| C089 | driver-ios-delivery | 4b | PENDING | | |
| C090 | driver-ios-jobs-level-earnings | 4b | PENDING | | |
| C091 | driver-ios-wallet-credit | 4b | PENDING | | |
| C092 | driver-ios-tracker-sharing-profile | 4b | PENDING | | |
| C093 | driver-ios-comms-safety-support | 4b | PENDING | | |
| C094 | passenger-ios-shell | 4b | PENDING | | |
| C095 | passenger-ios-auth-onboarding | 4b | PENDING | | |
| C096 | passenger-ios-live-map-search | 4b | PENDING | | |
| C097 | passenger-ios-booking | 4b | PENDING | | |
| C098 | passenger-ios-ride-payment | 4b | PENDING | | |
| C099 | passenger-ios-package-history | 4b | PENDING | | |
| C100 | passenger-ios-mode-b-subscriptions | 4b | PENDING | | |
| C101 | passenger-ios-settings-addresses | 4b | PENDING | | |
| C102 | passenger-ios-comms-safety-support | 4b | PENDING | | |
| C103 | tailwind-preset | 4c | PENDING | | |
| C104 | admin-portal-shell | 4c | PENDING | | |
| C105 | admin-portal-auth-dashboard | 4c | PENDING | | |
| C106 | admin-portal-verification | 4c | PENDING | | |
| C107 | admin-portal-moderation-support | 4c | PENDING | | |
| C108 | admin-portal-finance-config-rbac-audit | 4c | PENDING | | |
| C109 | admin-portal-directories | 4c | PENDING | | |
| C110 | admin-portal-gtfs-manager | 4c | PENDING | | |
| C111 | fleet-portal-shell | 4c | PENDING | | |
| C112 | fleet-portal-auth-org-payout | 4c | PENDING | | |
| C113 | fleet-portal-vehicles-drivers-trackers | 4c | PENDING | | |
| C114 | fleet-portal-dashboard-map-analytics | 4c | PENDING | | |
| C115 | fleet-portal-scheduling-billing | 4c | PENDING | | |
| C116 | fleet-portal-subscriptions | 4c | PENDING | | |
| C117 | web-passenger-subview | 4c | PENDING | | |
| C118 | contract-test-suite | 5 | PENDING | | |
| C119 | observability-stack | 5 | PENDING | | |
| C120 | e2e-mode-c-ride | 5 | PENDING | | |
| C121 | e2e-mode-ab-fleet | 5 | PENDING | | |
| C122 | e2e-proxy-package | 5 | PENDING | | |
| C123 | e2e-money-flows | 5 | PENDING | | |
| C124 | cicd-full-pipeline | 5 | PENDING | | |
| C125 | replica-deployment | 5 | PENDING | | |
| C126 | gtfs-day0-load | 5 | PENDING | | |
| C127 | security-review-asvs | 6 | PENDING | | |
| C128 | anti-spoof-hardening | 6 | PENDING | | |
| C129 | load-test-suite | 6 | PENDING | | |
| C130 | chaos-drills | 6 | PENDING | | |
| C131 | voip-tracker-acceptance-sg | 6 | PENDING | | |
| C132 | production-readiness-doks | 6 | PENDING | | |

⭑ = walking-skeleton milestone (C020–C025): one booked ride end to end on Docker Compose.

## Planner findings — spec gaps & conflicts (from C000)

Recorded by the build planner. Each is already encoded as a fence in the affected prompts;
the ones marked **micro-change-set** should be fixed in `specs/` rather than worked around.

1. **`server_db_schema.md` is an incomplete mirror of D4' — micro-change-set.** §0.1
   `CREATE SCHEMA` omits `config`, `subscription` and `transit`, and the file carries no DDL
   for `config.operating_cities` (D4 §17b), the whole `subscription.*` schema (D4 Δ
   2026-06-21, Epic 23) or `transit.gtfs_routes/trips/stops/stop_times/shapes` (D4 Δ
   2026-06-21) — only `transit.gtfs_feed_versions` + `transit_staging` (§27) and
   `analytics.daily_metrics` (§23) were back-filled. **Resolution taken:** C003 creates all
   schemas; C005 lands the missing DDL from D4', which is authoritative here.

2. **`scheduling-svc` / `scheduling.scheduled_rides` do not exist — micro-change-set.**
   ADD §1.11 AL-36 and one D3' Δ heading name a service and schema that appear nowhere else;
   ADD §9.1, D4' §6 and `server_db_schema.md` §6 all place scheduled rides in
   `dispatch.scheduled_rides`. **Resolution taken:** owned by dispatch-svc (C035).

3. **MFA contradiction (AL-37).** D3' §0 still reads "Admin Portal = Password or Google +
   MFA" and D7' §4.2 still sets `admin-bff … Mfa__RequiredForInternal=true`, both predating
   AL-37 which removed the MFA/TOTP step. **Resolution taken:** AL-37 wins — no second
   factor anywhere (fenced in C026, C062, C104, C105).

4. **Number-masking leftovers (AL-48).** Earlier-dated addenda still describe masked calling:
   D3' Δ 2026-06-28 `POST /v1/calls/start … normal_masked`; D3' Δ 2026-07-05
   `POST /public/track/{token}/call` (proxy-DID lease); D6' I-28.3 and I-29.3; traceability
   row US-25.4. All are superseded later in the same documents by the 2026-07-05 #2 set.
   **Resolution taken:** AL-48 wins — `free_voip` only, `tel:` links, no DID lease, no
   masked-SMS relay (fenced in C055, C066, C080, C098, C117).

5. **`tracker-adapter-svc` vs `tcp-adapter` (D-DRIFT-2, still open in the ADD).** ADD §6 names
   the component `tracker-adapter-svc`; D3' Part 1, D6', D7' and the replica layout all use
   `tcp-adapter`. **Resolution taken:** `tcp-adapter` is canonical, `tracker-adapter-svc` is
   an alias only (C043).

6. **Stale build-order references.** B0' §5 still lists a "Wallet Portal" in its Wave-4
   table; AL-02 removed it. There is no Wallet Portal component in this manifest, and the
   B0' wave table is superseded by `build/manifest.yaml`.

7. **Spec-anchor style (decision, not a gap).** `spec_anchors` use readable section slugs
   derived from the headings (e.g. `#9-1-postgresql-bounded-context-schemas`). They identify
   the section to read, not a rendered link target.

8. **Walking-skeleton screens (decision).** C025 builds throwaway-fidelity versions of
   screens formally owned by C068–C070 and C077–C080. It claims **no** wireframe screen ID,
   so screen ownership stays 1:1 across the 202 IDs. Wave 4a replaces it at full fidelity.

## Session Handoffs

_Append 3 lines per completed component (Component / Status / Notes)._

- **Component:** C001 repo-scaffold — 2026-07-27
- **Status:** DONE — `dotnet build backend/MageRide.sln -c Release && ./gradlew projects && npm --prefix portals ci`
  exits 0. All four DoD items pass; every source directory now carries a CLAUDE.md naming its stack
  and a verify command (`shared/kmp` and `apps/driver-android` / `portals/admin` gained the missing
  Verify lines).
- **Notes:**
  **Spec gaps —** (a) *Gradle project paths conflict.* D7' §1/§6/§7 build and CI commands use
  `:driver-android` / `:passenger-android`; this component's DoD requires `:apps:driver-android` /
  `:apps:passenger-android`. Followed the DoD (the paths mirror the on-disk layout); **D7' needs a
  micro-change-set** for those three command lines. `:shared` satisfies both.
  (b) *No minimum-iOS requirement exists.* URD §5.5 NFR-22 pins minimum Android (8.0 / API 26) but
  neither URD, ADD nor D2' states an iOS deployment target — C085/C094 will need one.
  (c) *No spec pins the Gradle-side toolchain* (Gradle / Kotlin / AGP / compileSdk / targetSdk);
  latest stable at scaffold time was chosen and recorded in `gradle/libs.versions.toml`.
  (d) Cosmetic: this prompt's scope says "following the two that already exist" — four scoped
  CLAUDE.md files existed (backend, apps/driver-android, portals/admin, shared/kmp). The deliverable
  list of six to create was correct.
  **Decisions —** (1) **The root Gradle build directory is redirected to `.gradle/root-build/`**
  (`build.gradle.kts`): Gradle's default root buildDir is `<root>/build`, which is the MageRide
  *build plan* directory — left at the default, Gradle would write output into manifest/prompts.
  (2) `global.json` sits at the **repo root**, not `backend/` — the SDK resolver walks up from the
  working directory and the documented verify command runs from the root, so `backend/global.json`
  would never be honoured. `Directory.Build.props` stays in `backend/`.
  (3) .NET 10's `dotnet new sln` defaults to `.slnx`; used `-f sln` to produce the `MageRide.sln`
  the deliverable and verify command name.
  (4) No plugins are declared in the root `build.gradle.kts`, not even `apply false` — that would
  pull AGP and the Kotlin compiler onto the buildscript classpath on every invocation. Versions live
  in `gradle/libs.versions.toml`; modules apply them via `alias(libs.plugins.…)`.
  (5) Module build scripts are deliberately absent (`shared/kmp` = C011, `apps/*-android` = C067/C076).
  Consequence: `./gradlew projects` needs no Android SDK today.
  (6) `.gitattributes` exempts `specs/` from EOL normalisation (`-text`) and **that rule must stay
  last in the file** — later patterns win, and an earlier placement was silently overridden by
  `*.md`. Eight tracked spec files are stored CRLF; `* text=auto eol=lf` would have renormalised the
  three largest spec documents and five approved wireframes into a whitespace-only diff.
  (7) npm workspace members are dependency-free placeholders so `npm ci` stays fast and offline-safe;
  C103/C104/C111/C117 add the real dependencies.
  (8) **No `Directory.Packages.props`** — central package management is not in the deliverable list,
  and enabling it with no entries would make C002's first `<PackageReference Version="…">` fail
  NU1008. Recommend C002 adopt it deliberately.
  (9) `EnforceCodeStyleInBuild=false` alongside `TreatWarningsAsErrors=true`, so .editorconfig style
  rules cannot fail a build on formatting nits.
  **Build host —** the **Android SDK is not installed** (`ANDROID_HOME` unset); needed from C011/C067
  onward for anything that applies AGP. JDK is **17 only** — AGP 9.3.1 ships Java-17 bytecode, so 17
  is sufficient today. Gradle 9.6.1 is cached in `~/.gradle` and the wrapper jar + distribution were
  SHA-256 verified against services.gradle.org.


- **Component:** C002 backend-shared-kernel — 2026-07-27
- **Status:** DONE — `dotnet test backend/src/MageRide.Shared.Tests -c Release` → **152 passed, 0 failed,
  0 skipped**. All five DoD items pass, including the E-09 timing test and the byte-for-byte replay
  test against a real Postgres (Testcontainers `postgis/postgis:16-3.4`, `redis:7-alpine`).
- **Notes:**
  **Spec gaps — two micro-change-sets, neither actioned in `specs/` (D4' is C004/C005's to land):**
  (a) ***`rides.command_log.response_body JSONB` cannot satisfy R-14 as written.*** D4' §5 /
  `server_db_schema.md` §5 declare the column `JSONB`, and ADD §11.13 requires the stored response be
  returned "verbatim". `jsonb` is a *parsed* representation: it strips insignificant whitespace, drops
  duplicate keys and reorders object members, so a replay is semantically equal but **not byte-for-byte**.
  `PostgresCommandLogTests.Jsonb_storage_does_not_round_trip_byte_for_byte` demonstrates it.
  Two further columns are also missing: nothing records the original `Content-Type`, so a replay cannot
  tell `application/json` from `application/problem+json` (the DoD requires every error stay
  `application/problem+json`). **Recommended DDL delta:** `response_body JSON` (Postgres `json` keeps the
  exact input text and stays queryable) **+ `response_content_type TEXT`**. The kernel defaults to that
  shape and also supports `Bytea` and the current lossy `Jsonb` via `CommandLog:BodyStorage`.
  (b) ***E-09 needs a direct-to-Postgres DSN that D7' §4.1 does not define.*** D7' §4.1 lists one
  `ConnectionStrings__Postgres` = the PgBouncer DSN, and `server_db_schema.md` §0 / ADD §9.3 put PgBouncer
  in **transaction mode** in front of every service. `LISTEN` is session-scoped: transaction pooling hands
  the server connection back at COMMIT, so a LISTEN registered through PgBouncer is dropped and the outbox
  dispatcher never wakes — E-09's sub-50 ms path silently degrades to the poll it was meant to replace.
  **Recommended:** add `ConnectionStrings__PostgresDirect` to the D7' §4.1 common table (required for
  ride-svc / dispatch-svc, optional elsewhere). Implemented as `PostgresOptions.DirectConnectionString`;
  when unset the factory falls back to the pooled DSN and logs a warning.
  (c) *Cosmetic:* D3' §0 names 423 "locked (OTP attempts)" and the 426 upgrade gate but gives neither a
  kebab code. Registered as `otp-locked` / `upgrade-required`. Nine further cross-cutting codes are
  kernel-defined for the same reason (`validation-failed`, `internal-error`, the four
  `idempotency-*` codes, `dependency-unavailable`, `upstream-timeout`, `method-not-allowed`).
  **Decisions —**
  (1) **Central package management adopted** (`backend/Directory.Packages.props`), as C001's handoff
  recommended. `<PackageReference>` carries no `Version`; a new dependency needs a `<PackageVersion>`
  entry first or the build fails NU1008. Noted in `backend/CLAUDE.md`.
  (2) **Idempotency is opt-out, not opt-in.** Every POST demands the header (D3' §0); a surface with its
  own dedupe key — the OnePay/IPG webhooks, which key on `provider_transaction_id` (R-19/E-05) — calls
  `.AllowMissingIdempotencyKey()`. 5xx responses are never stored, so a retry re-executes rather than
  replaying a failure; a reservation abandoned by a dead process is reclaimed after 60 s.
  (3) **One outbox drainer at a time**, elected by a transaction-scoped advisory lock. `FOR UPDATE SKIP
  LOCKED` alone would let two replicas publish two events for the same ride out of order, which breaks the
  per-aggregate ordering D6' §2.3 promises consumers. Delivery is at-least-once (row marked dispatched only
  after the broker acks); consumers dedupe on `eventId`/`seq` per D6' §2.3.
  (4) **`pg_notify` is issued by the writer inside the caller's transaction**, not by a table trigger.
  Postgres delivers a transactional NOTIFY at COMMIT, which is exactly R-13 ("no phantom offers") and
  leaves the DDL free of a trigger C004 would otherwise have to own.
  (5) **Polly v8's breaker is ratio-based**, so D6' §8.3's "open after 5 failures/30 s" maps to
  `SamplingDuration=30 s`, `MinimumThroughput=5`, `FailureRatio=0.5`, `BreakDuration=15 s`. The ±25% jitter
  is generated by hand — Polly's `UseJitter` applies a decorrelated curve, not a symmetric band.
  (6) **`TimeProvider`, not a bespoke `IClock`.** `BusinessCalendar` takes it directly and the tests use
  `FakeTimeProvider`.
  (7) **Dapper's built-in type map shadows handlers**, so `DateTimeOffset` and `DateOnly` are
  `RemoveTypeMap`'d before their handlers are added — otherwise Npgsql rejects any non-UTC offset outright
  and `DateOnly` (every D-38 business-date column) cannot be used as a parameter at all.
  (8) **`OpenTelemetry.Exporter.Prometheus.AspNetCore` is `1.17.0-beta.1`** — the only build published.
  D7' §12 requires a per-service `/metrics` scrape; the OTel .NET Prometheus exporter has no stable
  release. Gated behind `Otel:PrometheusEnabled` (default on). OTLP traces/metrics/logs use stable packages.
  (9) `CursorPage.cursor` is force-serialised even when null: D3' §0 spells the field `"opaque|null"`, and
  the platform-wide `WhenWritingNull` policy would otherwise make "last page" look like "field missing".
  (10) Cursors are base64url and optionally HMAC-signed; unsigned by default. A decoded cursor stays
  untrusted input — endpoints must still scope their query by the caller's identity.
  **Build host —** Docker is used by the test suite (Testcontainers pulls `postgis/postgis:16-3.4` ≈ 853 MB
  and `redis:7-alpine`); containers are torn down per run and the replica stack stayed down throughout.
  A host without a Docker daemon skips those tests via `Assert.Skip` rather than failing, so the DB/Redis
  DoD items are only *proved* where Docker is available — CI (C010) must provide it. The E-09 test runs one
  unmeasured warm-up round, then asserts the **median** of five commit→publish measurements is under 50 ms;
  the first drain pays one-off connection and JIT cost (~74 ms measured cold) that is not per-offer latency.

- **Component:** C003 db-schema-identity-registry — 2026-07-27
- **Status:** DONE — `bash infra/scripts/migrate-verify.sh` → **40/40 checks passed**. 13 scripts apply
  to an empty `timescale/timescaledb-ha:pg16`, no-op on a journalled re-run, and re-apply cleanly with
  the journal disabled. 24 tables: iam 9, config 1, registry 12, prov 2; the C004/C005/C006 schemas are
  created but left empty. All six DoD items pass.
- **Notes:**
  **Spec gaps — four micro-change-sets, none actioned in `specs/` (the DDL sections are D4'/server_db
  _schema's to own):**
  (a) ***`server_db_schema.md` §2 `registry.documents.kind` is missing `revenue_license`.*** Its CHECK
  lists `('driving_license','registration','permit','insurance')`, but D4' §2 includes `revenue_license`
  and AL-50 names it as one of the four SCR-FP-004 slots ("kind values already cover the slots"). Without
  it the AL-10 approval gate — verified registration + insurance + **revenue_license** for all modes —
  cannot be recorded. **Took D4'.**
  (b) ***`ck_documents_owner` — the constraint contradicts its own comment.*** D4' Δ 2026-07-18 and
  server_db_schema §26 both print `CHECK (driver_id IS NOT NULL OR fleet_id IS NOT NULL)` (at least one)
  while commenting "-- exactly the uploading principal"; this prompt's DoD also says "CHECKs **exactly
  one** owner". Two of three readings say XOR, so the landed constraint is
  `num_nonnulls(driver_id, fleet_id) = 1`. **C029 / C058 must not set both columns on one row.** If a
  fleet-assigned driver ever needs to own a document against a fleet vehicle, loosen this to `>= 1`.
  (c) ***`iam.saved_addresses` has two incompatible shapes.*** server_db_schema §1 and D4' §2 model
  Home/Work through `label` and carry `updated_at`; D4' Δ 2026-06-21 (AL-26) models them as
  `is_home`/`is_work` with partial unique indexes, makes `line1 NOT NULL` and drops `updated_at`. Landed
  the **union** — only the Δ form can enforce "at most one Home, at most one Work", and only the base
  form gives a row an addressable label. C027 should collapse this to one representation.
  (d) ***`iam.user_prefs` does not exist.*** ADD §9.1 and D4' Δ 2026-06-21 both reference it
  (`ALTER TABLE iam.user_prefs ADD COLUMN language …`), but no `CREATE TABLE` appears in any spec and
  both runnable DDL sources put `language` and `default_payment_method` on **`iam.users`**. Not created;
  the columns stay on `iam.users`. The Δ also sets `DEFAULT 'si'` where `iam.users` says `'en'` — kept
  `'en'`, since AL-26 only makes the onboarding *picker* Sinhala-first. Same class of phantom as planner
  findings 2 and 5.
  **Other spec observations (no change needed, worth knowing):**
  (e) `registry.vehicles` is a **union of the two specs**: server_db_schema §2 has `mode_b_billing` /
  `default_monthly_fare_minor` (AL-24) and D4' §2 has `onboarding_status` (AL-30); neither carries both.
  (f) `server_db_schema.md` §1 **is not runnable in its printed order** — `iam.fleet_members` references
  `registry.fleets` from §2. Hence `0302__iam_fleet_members.sql`, numbered after the registry fleet file.
  (g) `prov.tracker_bindings.fleet_id` references **`registry.operators`**, not `registry.fleets`, in both
  specs. Kept as written, along with the legacy `registry.operators` stub. Now that fleet-svc is Phase 1
  (AL-03) this probably wants repointing — a decision for C030/C043, not a silent change here.
  (h) The prompt says "all 22 schemas"; §0.1 lists **21** `CREATE SCHEMA` statements and names
  `analytics` + `transit_staging` in prose. **23 schemas created.**
  (i) **D7' §2.2's Dockerfile template does not build on the .NET 10 alpine images** — `addgroup -S app`
  fails with "group 'app' in use" because the base image already ships `app` (uid 1654). Worked around
  with a `getent`-guarded create. **C009 will hit this on every service image.**
  **Decisions —**
  (1) **DbUp, journalling to `public.schema_versions`.** Scripts are **embedded in the assembly** so the
  migrate image is one self-contained artifact with no volume to get wrong; `MIGRATE_SCRIPTS_DIR` /
  `--scripts` overrides with a directory. Both sources name a script by its **bare filename**, so a
  database migrated from one and then the other agrees about what has run.
  (2) **`--ignore-journal` exists for the verify.** A journalled second run only proves DbUp remembers;
  pass 3 re-executes every script with the journal disabled, which is what actually proves the DDL is
  idempotent. Every script is written for it (`IF NOT EXISTS`, `CREATE OR REPLACE`, `ON CONFLICT`).
  (3) **`--wait` connect polling (default 60 s).** Compose starts the one-shot `migrate` alongside
  Postgres and a fresh container spends seconds in initdb; polling turns a startup race into a wait.
  (4) **`runtime:10.0-alpine`, not `aspnet`** — a batch job with no HTTP surface, so no port, no
  healthcheck. Runs as the base image's non-root `app` (uid 1654).
  (5) **`public.attach_set_updated_at(schema, table)` helper** in `0002`, so each table migration attaches
  the §0.2 trigger in one line and the naming stays uniform. The verify asserts **no `updated_at` column
  anywhere in the four schemas is left without it**.
  (6) **Seeds:** `iam.roles` (9) and `config.operating_cities` (3, Sinhala/Tamil labels intact) land here
  because their tables do; the rest of §20 (billing.plans, fares.tariffs, …) is C005's.
  (7) **`registry.fleet_payout_profiles.proof_upload_id` / `lankaqr_upload_id` are plain UUIDs for now** —
  their FK target `docs.uploads` is C005's (13xx). **C005 must add the two FK constraints**; AL-49 is
  already on its ADD list. This is the only place C003 leaves §0's "real FOREIGN KEY constraints" unmet.
  (8) **The verify does functional tests, not just catalog introspection** — it proves a second live
  driver session is rejected while a passenger session alongside it is not (AL-08), that `'car'` is
  rejected (AL-09), that a plate frees up on REJECTED (D-37), that a document with neither or both owners
  is rejected (AL-50), and that `updated_at` actually moves.
  (9) Added a repo-root **`.dockerignore`** (the context was 134 MB, almost all `bin/`+`obj/`) and a
  **`db/CLAUDE.md`** documenting the file-naming ranges and the re-runnability rule, per the
  every-source-directory-has-one convention from C001.
  **Build host —** the verify needs Docker and pulls `timescale/timescaledb-ha:pg16` (~853 MB, already
  cached on this box). Containers are published on `127.0.0.1:0` and removed by an EXIT trap; the replica
  stack stayed down. CI (C010) must provide a Docker daemon for this verify to run.

- **Component:** C004 db-schema-trips-rides-dispatch — 2026-07-27
- **Status:** DONE — `bash infra/scripts/migrate-verify.sh` → **84/84 checks passed**. 21 new scripts
  (34 total) apply to an empty `timescale/timescaledb-ha:pg16`, no-op on a journalled re-run, and
  re-apply cleanly with the journal disabled. 26 new tables: trips 4, rides 7, dispatch 12,
  reputation 3, plus 14 monthly `trips.position_samples` partitions. All six DoD items pass.
- **Notes:**
  **Spec gaps —**
  (a) ***`dispatch.outbox` does not exist in either DDL source — micro-change-set, created anyway.***
  server_db_schema §6 and D4' §6 declare no outbox for `dispatch`, but D6' §2.4 names dispatch-svc
  alongside ride-svc as an outbox writer ("ride-svc/dispatch-svc write domain change + outbox row in one
  DB transaction; **`offer.created` pushed only after COMMIT** — no phantom offers, R-13") and D6' §2.1
  registers the `dispatch.events` topic with dispatch-svc as its producer. `offer.created` *is* the event
  R-13 exists for and it is written by dispatch-svc, so without this table C034 would have to publish
  outside its transaction — the exact failure R-13 forbids. Landed as `0709__dispatch_outbox.sql`,
  shape-identical to `rides.outbox` so MageRide.Shared's dispatcher works with
  `Outbox__Schema=dispatch` / `Outbox__Channel=dispatch_outbox` / `Outbox__Topic=dispatch.events`.
  **D4' §6 and server_db_schema §6 need this DDL added.**
  (b) ***`ux_rides_open_passenger` exempts `'Completed'`, which can make the Completed→PaymentPending
  transition fail — micro-change-set, landed as printed.*** The one-open-ride-per-passenger partial
  index skips eleven states, and `Completed` is one of them while `PaymentPending` is not. Since D5' §6
  runs `Completed → PaymentPending`, the guard *lifts* at Completed and *re-applies* one transition
  later: if a passenger books a new ride during that window, the old ride can never leave Completed —
  the unique index rejects the UPDATE. Either `Completed` should come out of the exempt list (it is not
  terminal in D5' §6; the terminal payment states are `Paid` / `CashSettled` /
  `CashOnDeliveryCollected` / `Disputed`) or `PaymentPending` should go into it. Both specs print the
  same list, so it is landed verbatim — **C032 (ride-svc-core) should not discover this at runtime.**
  (c) ***`ux_penalty_apply` enforces nothing.*** Both specs print `UNIQUE (id, applied_ride_id)` on
  `dispatch.cancellation_penalties` and the DoD repeats it, but `id` is the primary key, so the pair is
  unique by construction and the index rejects no row. The real D-05 double-apply guard is elsewhere and
  is intact: the settlement UPDATE is conditional on `status='OUTSTANDING'`, and the ledger entry is
  keyed `billing.journal_entries.idempotency_key = penalty_id || ':' || rideId` (D5' §7.1 — **C005 owns
  that table and must keep the key exactly**). Landed as specified rather than reinterpreted.
  (d) ***`trips.sessions.route_id` has no FK.*** server_db_schema §4 writes
  `REFERENCES spatial.routes(id)`; `spatial.*` is C005's, so the column lands bare (D4' §4 prints it
  bare too). **C005 must add the constraint** — same class as C003's `fleet_payout_profiles →
  docs.uploads` deferral.
  (e) ***`rides.proof_artifacts.kind` needed a fourth value.*** The §5 base DDL lists three; the
  Δ 2026-07-05 #2 change set (AL-47) adds `'qr_receipt'` for the passenger's receipt screenshot, which
  `fares.ride_payments.qr_claim_artifact_id` (C005) points at. Landed with all four. Easy to miss — the
  addition is stated in prose beneath the §25 SQL block, not inside it.
  (f) *Cosmetic:* server_db_schema §19's `payment.method` enum row lists `scan_driver_qr` as used by
  **`rides.rides.payment_method`** as well as `fares.ride_payments.method`. D4' Δ 2026-06-21, D3'
  `POST /v1/rides/request` (`cash|lankaqr|onepay|cod`) and D3' `POST /fare/pay`
  (`cash|lankaqr|onepay|scan_driver_qr`) all scope it to the payment table only — the two columns have
  genuinely different domains (`cod` is booking-side, `scan_driver_qr` is settlement-side). Landed the
  four-value CHECK; §19's row should name only `fares.ride_payments.method`.
  (g) *Cosmetic:* the ADD DT-03 critique row describes `dispatch.directional_filters` as "keyed
  `(driver_id, used_date)` with `use_count`". ADD §9.1 and both DDL sources use **one row per
  activation** with `COUNT(*) per (driver_id, used_date) ≤ max_uses_per_day`. Row-per-activation landed
  — it is also the only shape that keeps US-6A.19 true (a manual turn-off still consumes its use).
  (h) *Cosmetic:* ADD §9.1 prose writes `applied_trip_id` and this prompt's DoD repeats it; both DDL
  sources write `applied_ride_id`. Took the DDL spelling — this is a Mode C ride, not a Mode A/B trip.
  **Decisions —**
  (1) **C002's micro-change-set (a) is actioned here**, since C004 owns `rides.command_log`:
  `response_body` is **`json`, not `jsonb`**, and **`response_content_type TEXT` is new**. jsonb is a
  parsed representation, so an R-14 replay through it is semantically equal but not byte-for-byte as
  ADD §11.13 requires; without the content-type column a replayed error cannot stay
  `application/problem+json`. This is exactly MageRide.Shared's default shape, so the kernel works
  unconfigured. **D4' §5 and server_db_schema §5 still need the same edit.**
  (2) **`used_date_tz_at TIMESTAMPTZ` added to `dispatch.directional_filters`** — D-38 requires a
  business `DATE` to carry a `tz_at` companion, `used_date` is the platform's first such column, and the
  C003 verify already fails any DATE without one. `used_date` also defaults to
  `(now() AT TIME ZONE 'Asia/Colombo')::date` rather than being left to the caller.
  (3) **Three integrity constraints beyond the printed DDL**, each encoding a rule the specs state in
  prose: `ck_directional_config_singleton` (both specs say "single row id=1"),
  `ck_directional_cleared_pair` (`cleared_at` and `cleared_reason` are set together or not at all —
  D5' §12.3 always knows the reason, so **C036 must supply one**), and non-negative CHECKs on the three
  `reputation.counters` columns.
  (4) **`trips.position_samples` ships `trips.ensure_position_samples_partition(DATE)` and a rolling
  14-month window (last month + 13), and deliberately NO `DEFAULT` partition.** A default that has
  accumulated out-of-range rows blocks `CREATE ... PARTITION OF` for the month those rows belong to,
  turning a missed maintenance run into an outage during recovery instead of at the write. Partition
  bounds are computed as explicit `TIMESTAMPTZ` values — bare date literals in `FOR VALUES` resolve
  against whatever `TimeZone` the migrating session carries, which would silently shift every
  Asia/Colombo month boundary by 5:30. **A maintenance job must call the helper** before the window runs
  out; the convention is now recorded in `db/CLAUDE.md`.
  (5) **No NOTIFY trigger on either outbox**, despite server_db_schema §5's comment ("A NOTIFY on this
  table's INSERT ... wakes the dispatcher"). C002 decision (4) issues it from the writing transaction
  instead: Postgres delivers a transactional NOTIFY at COMMIT, which is precisely R-13, whereas a
  trigger also fires for rows a later ROLLBACK discards. Recorded in the DDL so nobody adds it back.
  (6) **CHECK constraints are explicitly named** (`ck_rides_state`, `ck_sessions_mode`, …) so the verify
  can assert an *exact* value set rather than a substring match. The 18 ride states are compared as a
  sorted set against D5' §6 / ADD Appendix B.2, so a typo or a stray extra state fails the build.
  (7) **Nine indexes exist that neither spec prints**, each backing a query the specs do name:
  `ix_command_log_inflight` (stale-reservation reclaim), `ix_sched_due` (scheduled-ride dispatch scan),
  `ix_penalty_outstanding` (D5' §7.1 per-passenger settlement), `ix_location_requests_booker` (P-12 rate
  limit) and `ix_location_requests_ride`, `ix_no_show_events_driver`, `ix_job_board_intents_driver`,
  `ix_fraud_flags_subject` and `ix_fraud_flags_kind`. No index either spec prints was omitted.
  (8) **No FKs on `trips.position_samples`, `trips.ratings.subject_id`,
  `dispatch.no_show_events.ride_id`, `dispatch.cancellation_penalties.*_ride_id` or
  `rides.rides.current_offer_id`** — all bare in both specs, and each for a reason worth keeping: the
  sample path is a partitioned bulk write, `subject_id` is polymorphic across `trips.sessions` and
  `rides.rides`, a level decrement and an accrued debt must outlive the ride they came from, and a
  `current_offer_id` FK would make `rides.rides` and `dispatch.offers` mutually dependent so neither
  could be inserted first.
  (9) **The verify does functional tests, not just catalog introspection** — it proves Mode C is
  rejected on a tracking session (R-01), that a driver cannot hold two ACTIVE sessions (D-03), two live
  offers (R-10), two Accepted rides (O2) or two active directional filters (DT-03), that a passenger
  cannot book twice on one `clientRequestId` (R-18) or hold two open rides, that an incomplete package
  or proxy ride is rejected (P-06/P-07/P-01), that a cleared filter frees the driver while still
  counting toward the daily limit (DT-03), and that a sample routes into the correct Asia/Colombo
  monthly partition.
  **Build host —** same footprint as C003: Docker plus the cached `timescale/timescaledb-ha:pg16`; the
  container is published on `127.0.0.1:0` and removed by an EXIT trap. The replica stack stayed down
  throughout. The verify now runs 84 checks in roughly 40 s.

- **Component:** C005 db-schema-business-content — 2026-07-27
- **Status:** DONE — `bash infra/scripts/migrate-verify.sh` → **151/151 checks passed**, run end to end
  twice. 29 new scripts (63 total) apply to an empty `timescale/timescaledb-ha:pg16`, no-op on a
  journalled re-run, and re-apply cleanly with the journal disabled. 53 new tables: safety 5, fares 5,
  billing 12, subscription 4, comms 3, docs 2, support 1, content 3, audit 1, pdpa 2, spatial 3,
  transit 6, transit_staging 5, analytics 1. All six DoD items pass. `telemetry` is left empty for C006.
- **Notes:**
  **Spec gaps — three micro-change-sets, none actioned in `specs/`:**
  (a) ***`transit.gtfs_feed_versions.uploaded_by` references a column that does not exist.*** Both
  `server_db_schema.md` §27 and D4' Δ 2026-07-22 #2 write
  `uploaded_by UUID NOT NULL REFERENCES iam.users(user_id)`, but `iam.users` has no `user_id` — its
  primary key is `id`, in §1 of the same document and in every other FK in both specs. Landed against
  `iam.users(id)`. **Both DDL sources need the one-word edit.**
  (b) ***The AL-47 rewrite of `fares.ride_payments.state` silently drops `PartiallyRefunded`.*** §25 /
  D4' Δ 2026-07-05 #2 replace the CHECK to add the two driver-QR attestation states and, in doing so,
  omit `PartiallyRefunded` — which the base §9 DDL, §19's enumeration reference and ADD §9.1 all still
  list, and which `fares.refunds.kind = 'partial'` (E-05) has no other terminal state to land in. Three
  sources say keep it, one later rewrite drops it while changing something unrelated. **Landed the union
  (14 states).** A state nothing writes costs nothing; a missing one makes a partial refund
  unrepresentable. §25 should re-include it, and §19 should gain `QrClaimedByPassenger` /
  `DriverConfirmedQR`, which it is still missing.
  (c) ***`fares.invoices` and `fares.payouts` do not exist anywhere but one ADD line.*** ADD §9.1's
  schema listing names both as bare table names with no column list; neither `server_db_schema.md` §9
  nor D4' §9 has DDL, no D3' endpoint touches them, and this prompt's deliverable line echoes the ADD
  listing. **Not created** — the same phantom class as `iam.user_prefs` (C003 note (d)) and
  `scheduling.scheduled_rides` (planner finding 2), and inventing columns would be worse than the gap.
  The functions are already covered: fleet invoicing by `billing.fleet_invoices` (AL-03), driver payout
  binding by `registry.driver_payouts` (D-11, C003) and org payout by `registry.fleet_payout_profiles`
  (AL-49, C003). **ADD §9.1 should drop the two lines.**
  **Other spec observations (no change needed):**
  (d) **Planner finding 1 is stale and can be closed.** It records that `server_db_schema.md` carries no
  DDL for `config.operating_cities`, `subscription.*` or the `transit.gtfs_*` core tables. The
  **2026-07-26 back-fill** (recorded in §22) added all of them as §17b / §18b / §18c, and §0.1 now
  creates the three schemas. D4' and `server_db_schema.md` agree on every one of these tables; the fence
  in this prompt ("take the DDL from D4 and record the gap") no longer had a gap to record.
  (e) *`comms.voip_sessions.masked_sms_fallback` is deliberately absent.* §11 prints it; §25 (AL-48)
  drops it with D-25. Landed in the final post-Δ shape, as with every other Δ in this component — these
  change sets are migration history, not a sequence this repo replays.
  (f) *`comms.call_log.caller_id` is now nullable with nothing to fall back on.* AL-44 made it nullable
  and added `share_token` as the alternative identity; AL-48 then dropped `share_token` but left the
  column nullable in both specs. Landed as printed — the log is explicitly best-effort — but it means a
  row can identify no caller at all. Harmless here, worth knowing in C055.
  (g) *§20 seeds no voucher tiers and no FAQ, and only one notification template.* See decisions (9)
  and (10) for what was seeded and on what authority.
  **Decisions —**
  (1) **Six business-date columns gained a D-38 `*_tz_at` companion**, continuing C004's convention and
  ADD §9.1's wording ("persisted as `DATE` *plus* a `tz_at TIMESTAMPTZ` audit field"):
  `fares.driver_earnings.earn_date`, `billing.daily_fee_charges.fee_date`,
  `billing.monthly_subscriptions.period_month`, `billing.fleet_invoices.period_month`,
  `subscription.subscriptions.next_due`, `subscription.payments.period_month` and
  `analytics.daily_metrics.metric_date`. **`transit.gtfs_feed_versions` is the one exemption** —
  `service_start` / `service_end` are read out of an uploaded GTFS feed rather than computed in
  Asia/Colombo, so there is no derivation instant to record. The exemption is named explicitly in the
  verify rather than left to a silently narrowed schema list.
  (2) **The balance trigger fires on DELETE as well as INSERT/UPDATE.** Both specs print
  `AFTER INSERT OR UPDATE`; deleting one leg of a balanced entry would then leave the ledger quietly
  unbalanced. `billing.assert_balanced()` takes `COALESCE(NEW.entry_id, OLD.entry_id)`, so deleting the
  *entry* still passes (`ON DELETE CASCADE` removes every leg and an empty entry sums to zero) while
  removing a single posting raises. Verified both ways. Note the inherent limit of a row trigger: an
  entry with **zero** postings is never checked, because nothing fires.
  (3) **`billing.accounts` gained a singleton guarantee for the platform-side accounts.** §0/§20 seed one
  `platform` and one `suspense` row and neither carries an `owner_id`, but nothing in the printed DDL
  stops a second one being created — after which postings would split across two accounts and every
  reconciliation would be silently wrong. Landed `ck_accounts_owner_id` (driver/fleet must have an owner,
  platform/suspense must not) plus `ux_accounts_platform` / `ux_accounts_owner`. The §20 platform rows
  are seeded in `1101` beside those indexes rather than in `1901`.
  (4) **`billing.daily_fee_charges` deliberately has no `journal_entry_id`.** Every other money row in
  `billing` carries one, but neither spec prints it here and the link is derivable from the PK via the
  ledger idempotency key — `'daily_fee:' || driver_id || ':' || vehicle_id || ':' || fee_date`, spelled
  out in a column comment. **C047 must use exactly that spelling.** Same reasoning as C004's note on the
  penalty key `penalty_id || ':' || rideId` (D5 §7.1), which `billing.journal_entries.idempotency_key`
  now documents in place.
  (5) **`transit_staging.gtfs_*` is declared with `LIKE ... INCLUDING DEFAULTS INCLUDING CONSTRAINTS
  INCLUDING COMMENTS`, not a copied column list.** AL-54 activation renames one schema into the other in
  a single transaction, so column drift between the two sides would corrupt the live feed rather than
  fail loudly. `LIKE` copies neither keys nor FKs, so those are declared explicitly and point **within**
  `transit_staging` — the verify asserts no staging FK reaches into `transit`, which would drag live rows
  through the swap. **C057 should know** the staging indexes are named `ix_staging_*`: after a swap the
  live tables carry those names, so either rename on activate or stop depending on the index names.
  (6) **CHECK constraints are explicitly named `ck_*`**, as in C004, so the verify can assert exact value
  sets. This renames two constraints the specs spell out (`trip_share_tokens_scope_check`,
  `trip_share_tokens_subject_check`) — those are Postgres's own auto-generated names carried into an
  `ALTER`, not names any application references.
  (7) **Constraints beyond the printed DDL, each encoding a rule the specs state in prose:** waived
  daily fees and FREE monthly/fleet invoices must carry no amount (D-13, "first month free");
  `period_month` must be the first of its month (otherwise the UNIQUE admits two rows per month and the
  free period can be re-claimed); a voucher credits its full face value (`credited_minor =
  denomination_minor`, US-9.19); no self credit-transfer and no self block; a `pending` access request
  claims no decision maker; a `join_anniversary` cycle needs a `join_day`; a `FulfilledHold` PDPA request
  needs a `hold_reason`; `content.broadcasts.message_by_lang` must contain all three languages (D-26 —
  the platform's trilingual rule made a schema constraint); lat/lng bounds on `safety.sos_events`.
  (8) **Twenty-two indexes exist that neither spec prints**, each backing a query the specs name — the
  unacked SOS queue, the pending vehicle-report and support queues, the driver-QR attestation timer, the
  refund and PDPA SLA queues, the monthly billing sweeps, the credit-transfer approval inbox, the
  subscription due-date scan and the owner's slip-verification queue, the NFR-28 upload sweeper, the
  share-token expiry sweep, and the `content.notification_templates` current-version lookup. Three are
  uniqueness rather than performance and are worth calling out: `ux_notif_tokens_token` (FCM/APNs reissue
  a token to whichever install owns it — without this a reinstall leaves a dead handle receiving E-01
  offers), `ux_wallet_tx_account_entry` (the ledger event stream is at-least-once per C002 decision 3, so
  a redelivered entry must not append a second history line) and `ux_refunds_provider_ref`. No index
  either spec prints was omitted.
  (9) **Voucher tier seed: denominations are spec, percentages are not.** URD US-9.19 pins the five
  denominations (Rs 1,000 / 2,000 / 3,000 / 5,000 / 10,000) and every spec that mentions the rate says it
  is admin-configurable per denomination with larger values typically earning more — giving exactly one
  worked example, `100000 → 1000 bps = 10%` (ADD §9.1 and URD US-9.19 both). That point is seeded
  literally; the 11/12/13/15% ladder above it is a **defensible default for Finance to edit in
  SCR-AP-007, not a spec value**, and is flagged as such in `1901`.
  (10) **Only the four template keys the specs actually name are seeded** — `ride_offer` (§20) and
  `package_on_the_way` / `proxy_ride_link` / `pickup_confirm_link` (D6' I-29.2) — each in Si, Ta and En,
  and the verify fails if any key is missing a language. Inventing further keys would put strings in the
  database that no service resolves. **C045 (content-svc) and C051 (notification-svc) own the rest, and
  must add all three languages per key.** The four FAQ topics are the ones US-16.1 names (wallet top-up,
  daily fee, vehicle registration, ride booking), 12 rows in total.
  (11) **`fares.tariffs.effective_from` is pinned to the epoch in the seed.** The column defaults to
  `now()` and is half of the UNIQUE key, so an unpinned seed would write a *new tariff version* on every
  re-run instead of conflicting — and `migrate-verify.sh` pass 3 re-executes every script. This is the
  general hazard for any seed whose conflict target includes a defaulted timestamp; recorded in
  `db/CLAUDE.md`.
  (12) **Both deferred FKs from earlier components are now closed:** `trips.sessions.route_id →
  spatial.routes(id)` (C004 note (d), `ON DELETE SET NULL` so retiring a route does not delete its trips)
  and `registry.fleet_payout_profiles.proof_upload_id` / `lankaqr_upload_id → docs.uploads(id)` (C003
  decision 7, AL-49). Both are added by `DO` blocks guarded on `pg_constraint` so pass 3 stays a no-op.
  §0's "real FOREIGN KEY constraints everywhere" now has no outstanding exceptions.
  (13) **The three platform-wide verify rules are now scoped by one `OWNED_SCHEMAS` list**, not a
  hand-maintained list per check — TIMESTAMPTZ-only, a `tz_at` companion per business `DATE`, and
  `set_updated_at` on every mutable table. A later component that adds a schema must add it there, which
  is a visible edit; previously it could opt out by omission. Two money rules were added to the same
  sweep (DoD item 4): every `*_minor` column is an integer type with a `>= 0` CHECK, and every `currency`
  column defaults to `'LKR'` — with the five signed ledger columns §0 exempts listed explicitly.
  (14) **The verify does functional tests, not just catalog introspection** (67 new checks). It proves
  the balance trigger rejects a single-leg entry *at COMMIT* and rejects deleting one leg of a balanced
  one, that a replayed gateway callback id is refused (R-19), that charging the daily fee twice in one
  Colombo day is a no-op and lands on the Colombo date (D-13/D-38), that a voucher cannot credit less
  than its face value, that a `pickup_confirm` token without a location request and a token-less web SOS
  are both refused while a legitimate `proxy_rider` token and token-only web SOS are accepted (AL-44),
  that `normal_masked` is refused (AL-48), that an unsubscribed Mode B grant stays MUTED until the owner
  deletes it (US-4.12), and that exactly one GTFS feed can be active — and that archiving it lets the
  next one activate (BR-32.2/32.3). Each rejection was checked by hand to fire on its *intended*
  constraint, not incidentally.
  **For later components —** truck / mini_truck have **no seeded daily-fee plan and no seeded tariff**
  (§20 leaves package-delivery rates to admin configuration), so Finance must set both before a delivery
  vehicle can go online; C060/C062 should surface that. `subscription.payments` must **never** post to
  `billing.journal_entries` (§18b — the platform takes no commission on the Mode B pass-through), and
  there is deliberately no column tempting C048 to.
  **Build host —** same footprint as C003/C004: Docker plus the cached `timescale/timescaledb-ha:pg16`,
  published on `127.0.0.1:0` and removed by an EXIT trap. The replica stack stayed down throughout. The
  verify now runs 151 checks in roughly 55 s.

- **Component:** C006 db-schema-telemetry-timescale — 2026-07-27
- **Status:** DONE — `bash infra/scripts/migrate-verify.sh` → **187/187 checks passed**. 4 new scripts
  (67 total) apply to an empty `timescale/timescaledb-ha:pg16`, no-op on a journalled re-run, and
  re-apply cleanly with the journal disabled. 1 new table (`telemetry.positions`, a hypertable) + 6
  views: 4 continuous aggregates and 2 fleet-scoped. All four DoD items pass. Wave 0's DDL is complete.
- **Notes:**
  **Spec gaps — two micro-change-sets, neither actioned in `specs/`. Unlike every earlier component's
  findings, these two are not stylistic: the DDL as printed in D4' §17 and `server_db_schema.md` §18
  does not run on TimescaleDB 2.28 at all.**
  (a) ***`CREATE UNIQUE INDEX ON telemetry.positions (vehicle_id, seq)` is rejected by TimescaleDB.***
  Both sources print it (and ADD §9.5 item 1 repeats it). A unique index on a hypertable must contain
  **every** partitioning column, and this hypertable is partitioned on `sample_ts` (time) *and*
  `vehicle_id` (16-way space):
  `ERROR: cannot create a unique index without the column "sample_ts" (used in partitioning)`.
  Landed as `ux_positions_vehicle_seq (vehicle_id, seq, sample_ts)`. It still rejects the case
  T-05/R-17 exists for — a tracker re-sending a buffered sample carries the GNSS timestamp it was
  captured with, so the replayed tuple collides on all three columns. It does **not** reject a same-seq
  sample bearing a *different* timestamp, which a bare `(vehicle_id, seq)` index would have. **C040
  (persistence-writer) must therefore write `ON CONFLICT (vehicle_id, seq, sample_ts) DO NOTHING`, not
  a two-column conflict target**, and C038/C039 keep owning the upstream rate-limited replay dedupe
  (ADD §7.5.3). **Both DDL sources need the third column added.**
  (b) ***Compression and row-level security cannot both be applied to `telemetry.positions`.*** §18
  prints them six lines apart — `ALTER TABLE … SET (timescaledb.compress, …)` (ADD §9.5 item 3) and
  `ALTER TABLE … ENABLE ROW LEVEL SECURITY` (item 8) — and TimescaleDB refuses the pair in **both**
  orders: `ERROR: operation not supported on hypertables that have columnstore enabled` and
  `ERROR: columnstore cannot be used on table with row security`. A compressed chunk holds a batch of
  rows as compressed arrays, so a per-row policy cannot be evaluated without decompressing the batch.
  No GUC relaxes it (checked the full `timescaledb.*` set), and RLS cannot be moved onto a continuous
  aggregate instead — that is a view, and `ENABLE ROW SECURITY` is table-only.
  **Resolution taken — compression stays, fleet scoping becomes a security-barrier view.** T-06 exists
  because "high-frequency telematics [is] not sized for Postgres"; the ~10× on 30 days of raw is the
  mitigation itself and nothing substitutes for it, whereas the *property* ADD §9.5 item 8 asks for —
  fleet operators "query only their own telemetry via query-svc **without application-side filtering
  risk**" — is fully preserved by putting the filter in the database instead of the policy engine.
  `1804` therefore lands: a `mageride_fleet_reader` NOLOGIN group role; `telemetry.current_fleet_id()`;
  `telemetry.positions_fleet` and `telemetry.fleet_health_5m_fleet`, both `security_barrier`, both
  filtered on `app.fleet_id`; and grants such that the fleet role can read **those two objects and
  nothing else** — not the base table, not a chunk, not the vehicle-keyed rollups. The verify proves
  all six of those. It is also strictly **fail-closed where the printed policy is not**: the spec's
  `current_setting('app.fleet_id')` raises 42704 when the GUC is unset, while
  `current_setting('app.fleet_id', true)` returns NULL and the predicate then matches no row — an
  unscoped connection sees zero rows rather than an error that a caller might catch and retry
  unscoped. **§18 / D4' §17 should replace the two RLS lines with the view + grant form**, or state
  explicitly that compression is dropped — but not keep both.
  **Other spec observations (no change needed):**
  (c) *`fleet_health_5m` is given a refresh policy the specs do not print.* §18 attaches
  `add_continuous_aggregate_policy` to `positions_1m` only. Without one, `fleet_health_5m` never
  materialises and every Fleet Portal dashboard read (US-3.13, C044) rescans raw chunks through
  real-time aggregation. Read as an omission, not a decision; landed at 1 day / 5 min / 5 min.
  (d) *`count(DISTINCT vehicle_id)` in a continuous aggregate works.* Worth recording because it is a
  documented TimescaleDB limitation in older majors and the obvious thing to "fix" pre-emptively;
  2.28 accepts it, and `fleet_health_5m` is landed exactly as printed.
  (e) *`distance_m` is not implementable as an aggregate.* ADD §9.5 item 2 lists the rollups as
  "(avg, max_speed, distance_m)" but the §18 DDL has no such column, and distance needs an *ordered
  pairwise* haversine over consecutive fixes, which is a window function — not expressible in a
  continuous aggregate. Not landed. Trip distance is a `trips`/`rides` summary column already.
  **Decisions —**
  (1) **File numbering is `18xx`, not the `15xx` `db/CLAUDE.md` reserved in C003.** The manifest
  deliverables name `18xx__telemetry_*.sql` and it matches spec §18; both sort between `14xx` and the
  `19xx` seeds, so nothing moved. `db/CLAUDE.md`'s range table is corrected, and gained a TimescaleDB
  section recording (a), (b) and the two runner constraints in decision (2).
  (2) **Every continuous aggregate is created `WITH NO DATA`, and `materialized_only = false` is set
  explicitly.** `CREATE MATERIALIZED VIEW … WITH DATA` cannot run inside a transaction block and the
  runner gives each script one (`WithTransactionPerScript`) — so `WITH NO DATA` is not a preference,
  it is the only form that applies. **This is why C006 needed no change to `MageRide.Migrations`**;
  a future component that needs a genuinely non-transactional statement will have to add one.
  `WITH NO DATA` is also the right migration shape: the refresh policies backfill in the background
  instead of materialising all history while the deploy holds locks. `materialized_only = false` is
  pinned rather than inherited from the server default so a read always combines materialised buckets
  with the live tail — the live map and fleet health both read the current, not-yet-materialised bucket.
  (3) **The 5-minute and 1-hour rollups are computed from raw, not stacked on `positions_1m`.**
  Hierarchical aggregates are cheaper, but `avg(speed_mps)` over a coarser `avg` is only exact when
  every sub-bucket holds the same number of **non-NULL** speeds, and `speed_mps` is nullable
  (`count(*)` counts rows, `avg` skips NULLs). Three independent refreshes over raw cost more CPU and
  are correct. All three share the 1-minute view's column shape so C042 can pick a granularity by
  table name alone.
  (4) **`compress_orderby` is `'sample_ts DESC, seq'`, one column more than the specs print.** With
  `sample_ts DESC` alone TimescaleDB warns `column "seq" should be used for segmenting or ordering` —
  a column of a unique index that is neither segmentby nor orderby cannot be checked without
  decompressing the whole batch, which would make the (a) dedupe guarantee unaffordable on the 7–30 day
  compressed range. `compress_segmentby` is exactly `vehicle_id`, as specified.
  (5) **Four CHECK constraints exist that neither spec prints** — `lat`/`lng` in range, `seq >= 0`, and
  `source BETWEEN 0 AND 4` (the five protocol families the column comment already enumerates). Each is
  a plain tuple check with no index probe, so the COPY ingest path is unaffected. A cheap tracker
  reporting 0/999 degrees is a bug C039 must filter before the batch arrives; these make it loud
  instead of silently poisoning a rollup.
  (6) **One index exists that neither spec prints:** `ix_positions_trip_ts (trip_id, sample_ts) WHERE
  trip_id IS NOT NULL`, backing "trip linestring for trip Y" — a read ADD §9.5 item 6 names explicitly.
  Partial, because only Mode A/B samples carry a trip. No index either spec prints was omitted.
  (7) **`telemetry` joins `OWNED_SCHEMAS` in the verify**, so the four platform-wide sweeps
  (TIMESTAMPTZ-only, `tz_at` per business DATE, `set_updated_at`, the money rules) now cover it. It
  passes all four vacuously — no DATE, no `updated_at`, no `*_minor`, no `currency` — which is the
  point: the list is the thing a later component has to edit visibly rather than opt out of by omission.
  (8) **No FK from `telemetry.positions` to `registry.vehicles` or `registry.fleets`**, matching both
  specs. This is a COPY-batched path at 40k rows/s (§21) where an FK is an index probe per row, and the
  tracker→vehicle resolution already happened upstream in `prov.tracker_bindings` (T-02/T-03).
  `fleet_id` is denormalised at write time so fleet scoping needs no join — **C040 must populate it**,
  and a vehicle that changes fleet keeps its old rows under the old fleet, which is correct for an audit
  trail and is what the fleet view returns.
  (9) **The verify does functional tests, not just catalog introspection** (36 new checks). It proves a
  replayed sample is rejected while a second vehicle may reuse the same seq, that an out-of-range
  latitude and an unknown protocol are refused, that writing a sample creates a chunk, that all four
  aggregates **materialise** (asserted on their materialisation hypertables' chunks, not on a real-time
  read that would pass without any refresh) and return the right bucket contents, and — for the DoD's
  cross-fleet item — that the fleet role is denied the base table, denied a chunk by name, denied the
  vehicle-keyed rollups, sees nothing at all with the GUC unset, and sees exactly its own vehicles with
  it set, while a fleet-less vehicle is invisible to everyone.
  **For later components —** `mageride_fleet_reader` is a **cluster-scoped role**, so `1804` needs
  `CREATEROLE` (and `COMMENT ON ROLE` needs superuser). That is free on the dev/replica boxes and on
  DOKS with a self-managed Postgres, but **C125/C132 must confirm it before pointing the migrate job at
  a managed instance**. `positions_1m/_5m/_1h` carry no `fleet_id` and are therefore platform-only; if
  the Fleet Portal (C114) needs a per-vehicle rollup for its own fleet it needs a re-grouped aggregate,
  which is a C042 decision, not a schema change here.
  **Build host —** same footprint as C003/C004/C005: Docker plus the cached
  `timescale/timescaledb-ha:pg16`, published on `127.0.0.1:0` and removed by an EXIT trap. The replica
  stack stayed down throughout. The verify now runs 187 checks in roughly 70 s.

- **Component:** C007 openapi-contracts — 2026-07-27
- **Status:** DONE — `npx --yes @stoplight/spectral-cli lint 'backend/contracts/*.yaml' --ruleset
  backend/contracts/.spectral.yaml` exits 0 with **0 errors** (1 warning, explained below). 22 YAML
  documents (`_shared.yaml` + 21 services), **262 operations**, plus the two realtime contracts and
  the ruleset. All four DoD items pass.
- **Notes:**
  **Spec gaps — five micro-change-sets, none actioned in `specs/`:**
  (a) ***AL-39 respells the verification write endpoints while calling them "unchanged" —
  the two spellings cannot both be canonical.*** D3' Part 2 has `GET /admin/onboarding/queue`,
  `POST /admin/onboarding/{id}/fields/{key}/confirm`, `PATCH /admin/onboarding/{id}/fields/{key}`
  and `POST /admin/vehicles/{id}/approve|reject`. The later Δ 2026-06-28 item 8 (AL-39) lists three
  subject-typed queues and then notes "Confirm / Edit&confirm / Approve / Reject endpoints
  **unchanged** (PUT `/admin/verification/{id}/fields/{key}`, `/approve`, `/reject`)" — a different
  path family. ADD §1.11's AL-39 row says only "Confirm/Edit/Approve/Reject endpoints unchanged"
  and does not respell them, so the two documents disagree about what "unchanged" means.
  **Resolution taken: the `/v1/admin/verification/*` family is canonical** and the
  `/admin/onboarding/*` + `/admin/vehicles/{id}/approve|reject` forms are absent with a comment
  naming AL-39, per the prompt's later-addendum-wins fence and DoD item 4. The deciding reason is
  not recency: AL-49 requires a Verification Officer to Approve/Reject a **fleet org's payout
  profile**, and `/admin/verification/{subjectId}` is the only spelling whose subject covers
  driver, vehicle *and* org — `/admin/vehicles/{id}/approve` structurally cannot. Every semantic
  D3' Part 2 attached to the old routes is carried onto the new ones (D-11 OnePay merchant bind on
  approve; approval blocked while any field is `pending`, US-2.10a). The two field routes collapse
  into one `PUT` whose body makes `value` optional — omitted = confirm as is, supplied = edit and
  confirm. **D3' Part 2's admin-bff rows should be rewritten to the AL-39 family.**
  (b) ***`DELETE /rides/scheduled/{rideId}` and `GET /rides/scheduled/{driverId}` cannot coexist.***
  ADD Appendix C prints the first, D3' Part 2 the second. Two paths differing only in the *name* of
  a template parameter are one ambiguous OpenAPI path, not two endpoints. Landed the cancel as
  **`DELETE /v1/rides/schedule/{scheduledRideId}`**, the RESTful partner of AL-36's
  `POST /v1/rides/schedule`, and kept D3's GET verbatim. It refuses once dispatch has materialised
  the ride (`status='DISPATCHED'`) — from there cancellation is ride-svc's
  `POST /v1/rides/{rideId}/cancel`, which owns the D-05 penalty matrix, and a dispatch-svc DELETE
  against `rides.rides` would cross the R-01 boundary. **ADD Appendix C should adopt this path.**
  (c) ***`voucher-discount-tiers` has two write endpoints in D3' Part 2.*** The subscription-svc
  table has `PUT /v1/admin/voucher-discount-tiers` and the wallet-svc table has
  `PUT /v1/wallet/admin/voucher-discount-tiers` — same operation, same table
  (`billing.voucher_discount_tiers`), two paths. Both are landed so neither route table is silently
  dropped, and each names the other. **One should be retired; the wallet spelling is the better
  survivor** because the read (`GET /v1/wallet/admin/voucher-discount-tiers`, usage stats) only
  exists there. The same duplication exists for the direct credit send
  (`POST /v1/transfers/driver` vs `POST /v1/wallet/credit-transfer/initiate`) — both landed, both
  write `billing.credit_transfers` with `direction='DIRECT'`.
  (d) ***Voucher purchase is placed in two services.*** D3' Part 2 puts it on subscription-svc
  (`POST /v1/vouchers/purchase`); ADD Appendix C puts it on wallet-svc
  (`POST /wallet/voucher/purchase`). **Took D3'** — it is the API source of truth — and did not
  duplicate it. The ADD-only `GET /wallet/voucher/discount-tiers` **was** landed: the Driver App
  needs the tier ladder before a purchase and D3' gives only the admin read. **ADD Appendix C's
  wallet block should drop the purchase line.**
  (e) *Three error codes had to be coined.* AL-54 specifies statuses without kebab keys —
  "409 duplicate sha256" and "409 not-validated / already-active". Registered `feed-duplicate`,
  `feed-not-validated`, `feed-already-active`. **C057 must `MageRideErrors.Register` all three at
  start-up**; the kernel's 63 declared codes are otherwise a byte-for-byte match with the
  `ErrorCode` enum in `_shared.yaml` (verified both directions).
  **Other spec observations (no change needed):**
  (f) *Endpoints deliberately **absent**, each with a comment naming the AL, in the file header
  where a reader would look for them:* `POST /public/track/{token}/call` and the `normal_masked`
  call type (**AL-48**, `public-bff.yaml` / `voip.yaml`); `POST /v1/admin/auth/mfa/verify` and TOTP
  enrolment (**AL-37**, `iam.yaml`); `/v1/wallet/topup/bank-transfer` and the admin bank-transfer
  routes (**AL-05**, `wallet.yaml`); `POST /wallet/topup/card`, consolidated into `/topup/onepay`
  since card payment *is* the OnePay rail; the four AL-39 routes in (a); and two ADD-only routes
  that AL-40/41/42 subsumed — `GET /admin/drivers/level-1` (now `GET /v1/admin/drivers?level=1`,
  and a literal `level-1` segment would be ambiguous against `GET /v1/admin/drivers/{driverId}`)
  and `GET /admin/users` (replaced by the three typed directories).
  (g) *`GET /v1/rides/history` (AL-36) is filed under ride-svc, not query-svc.* The Δ heading
  groups it with dispatch-svc, but it is a `rides` path and ride-svc is the sole writer of the Mode
  C aggregate (R-01); query-svc keeps `/v1/trips/*`, which spans both planes. Trip *detail* with
  the polyline stays on query-svc.
  (h) *`/v1/geo/search` and `/v1/geo/reverse` are contracted in `query.yaml`.* They are
  nominatim-svc's, but D3' prints them inside the query-svc section and there is no nominatim
  deliverable. `/v1/geo/parse-maps-link` is in `transit.yaml`, where AL-20 puts it.
  (i) *`ride-svc` `/decline` vs ADD Appendix C `/reject`.* Took `/decline` — D3' Part 2's ride-svc
  table, and ride-svc is the sole writer. Same for the three ride-lifecycle routes ADD Appendix C
  still lists under dispatch-svc; that appendix supersedes itself in place ("moved to ride-svc").
  (j) *`ocr-svc`, `analytics-read-model`, `mqtt-bridge`, `position-processor`, `persistence-writer`,
  `fanout-svc`, `tcp-adapter` and `tile-cdn` have no `.yaml`* — none has an HTTP contract in D3'.
  Their surfaces are the two `realtime/` documents, the Redpanda topic registry, and the CDN's
  range-request convention.
  **Decisions —**
  (1) **`x-error-codes` on every operation is the machine-checkable half of D3' §0's error
  contract.** The alternative — inferring codes from status codes — loses exactly the information
  a client branches on. The lint enforces kebab shape; **C118 should assert set membership against
  `MageRideErrors.All` at runtime**, which is the only place the check cannot go stale.
  (2) **The `Idempotency-Key` rule has exactly six documented exemptions**, each carrying
  `x-idempotency-exempt` with its reason: the OnePay and LankaQR callbacks on fare-svc, wallet-svc
  and subscription-svc. They are HMAC-signed and dedupe on `provider_transaction_id` (R-19), and no
  external gateway will send our header — this is C002 decision 2 expressed in the contract.
  **120 of 126 POST operations carry the required header**, and the lint rejects a seventh
  exemption that arrives without a reason string. The three public-bff POSTs deliberately **do**
  require it: SCR-WT is our own client (C117), and the Δ 2026-06-21 addendum restates
  "`Idempotency-Key` on writes" for every family.
  (3) **Security is declared explicitly on all 262 operations**, `security: []` included. AL-06 is
  deny-by-default; a contract that omits `security` inherits a document-level default, and an
  omission that silently means "public" is the one failure mode this rule exists to prevent.
  (4) **`X-Attestation` is a per-operation parameter; `X-App-Version` / `X-Platform` are not.**
  Attestation failure is `401 attestation-failed` on a specific operation, so it is part of that
  operation's contract and is declared on exactly the surfaces D3' §0 names sensitive (auth,
  payments, ride accept, wallet, SOS) — 29 operations. The min-version gate is edge behaviour
  applied to every request by C008 and belongs in `_shared.yaml` as a component, not repeated 262
  times.
  (5) **`_shared.yaml` is a valid OpenAPI 3.1 document, not a fragment**, because 3.1 makes `paths`
  optional. That is what lets the one verify command cover it. `oas3-unused-component` is turned
  off for that file alone via `overrides` — a component library referencing nothing internally is
  its purpose, not a defect.
  (6) **Every service file re-declares its security schemes as `$ref`s into `_shared.yaml`.** Both
  Spectral and every codegen validate an operation's `security` against a *local*
  `components.securitySchemes`; a cross-file reference alone leaves them unresolvable.
  (7) **Enums are pinned to the DDL that landed, not to prose.** `RideState` is the 18-value
  `ck_rides_state` set, `PaymentState` the 14-value `ck_ride_payments_state` set (including the
  `PartiallyRefunded` C005 restored), `VehicleType` the 10-value AL-09 set, and the subscription,
  GTFS, support, PDPA, report and document enums likewise. A contract enum that drifts from a CHECK
  produces a client that can construct an unstorable request.
  (8) **`RideVehicleType` is a separate schema from `VehicleType`.** `bus` and `train` are Mode A
  and can never be booked as a Mode C ride or onboarded through the Driver App; giving the booking
  and onboarding surfaces the full enum would make `403 mode-not-allowed` a runtime discovery
  rather than a compile-time impossibility in the generated client.
  (9) **`CursorPage.cursor` is `type: [string, 'null']`, never omitted** — matching C002 decision 9,
  so "last page" cannot be mistaken for "field missing" by a generated deserialiser.
  (10) **The `/public` prefix is admitted by the path rule alongside `/v1`.** AL-44's family is
  versioned by share-token scope, not by a path segment, and D3' prints it unversioned. The rule
  admits those two prefixes and nothing else.
  **For later components —**
  **C008 (gateway) — six literal-vs-template path overlaps must be routed literal-first, and two
  of them cross a service boundary:** `GET /v1/rides/job-board` (dispatch) and
  `GET /v1/rides/scheduled/{driverId}` (dispatch) sit under the same prefix as
  `GET /v1/rides/{rideId}` and `GET /v1/rides/{rideId}/state` (ride-svc). **A prefix-only YARP rule
  on `/v1/rides` will send Job Board traffic to ride-svc.** The other four are intra-service and
  ASP.NET Core's own precedence resolves them: `/v1/rides/history`, `/v1/vehicles/mine`, and
  `/v1/mode-b/subscriptions/{passengerId}` against `/v1/mode-b/{vehicleId}/…`. Verified by an audit
  script; no two operations share a templated shape.
  **C012/C013 (KMP client):** every operation has a unique camelCase `operationId` and exactly one
  tag, so one API class per tag and one method per `operationId` generates without mangling.
  **C118 (contract tests):** assert `x-error-codes` membership against `MageRideErrors.All`; assert
  the `ErrorCode` enum and the kernel registry agree (they do today, 63 + 3 coined); and assert the
  six `x-idempotency-exempt` operations are exactly the ones calling `AllowMissingIdempotencyKey`.
  **Build host —** no Docker, no database, no replica stack. `npx` fetched
  `@stoplight/spectral-cli@6.16.2` from the registry, so **CI (C010) needs network for this verify**
  or must vendor the CLI. The verify runs in roughly 10 s.

- **Component:** C008 api-gateway-yarp — 2026-07-27
- **Status:** DONE — `dotnet test backend/src/ApiGateway.Tests -c Release` → **524 passed, 0 failed,
  0 skipped** (many are per-route theory cases). 59 routes across 21 clusters plus the locally served
  `/v1/version/check`. All four DoD items pass: a below-floor request gets 426 with
  `{updateUrl, latestVersion, isMandatory}`; a sensitive route without a valid `X-Attestation` gets
  401 `attestation-failed` while a non-sensitive one is untouched; **every one of the 262 operations
  in `backend/contracts/*.yaml` is driven through a running gateway and asserted to reach its own
  service**; and a real SignalR `HubConnection` completes a WebSocket handshake and a round trip
  through the proxy. `dotnet test backend/src/MageRide.Shared.Tests -c Release` still 152/152.
- **Notes:**
  **Spec gaps — five micro-change-sets, none actioned in `specs/`:**
  (a) ***Attestation failure is 401 in D3' and 403 in the ADD, and the ADD's route list is a path
  family that does not exist.*** D3' §0 and this prompt's DoD both say `401 attestation-failed`;
  ADD §12.6's threat matrix says "requests without a valid token are rejected **403**" and scopes
  D-30 to `/api/payments/**`, `/api/wallet/**`, `/api/dispatch/**`. No MageRide path begins `/api`
  (C007 pinned `/v1` and `/public`), and that set omits **auth** and **SOS** — which D3' §0 names —
  while adding dispatch, which it does not. **Resolution taken: D3' + the contracts win.** The
  status is 401, and the enforced set is exactly the operations declaring the `X-Attestation`
  parameter in `backend/contracts/*.yaml`. **ADD §12.6 needs the status and the path family
  corrected**; a test asserts the two sets stay equal in both directions, so the drift cannot
  reopen silently.
  (b) ***No spec defines what is inside `X-Attestation`.*** `_shared.yaml` types it
  `string, maxLength 8192` and nothing anywhere says how a platform encodes its verdict. Android
  needs no encoding — a Play Integrity token is self-contained — but an App Attest **assertion** is
  meaningless without the key id that identifies which registered public key verifies it. Defined
  here as **`base64url(keyId) "." base64url(assertion CBOR)`**, with the signed client data being
  the request binding **`"{METHOD} {path}"`**. **D3' §0 needs this written down**, and
  **C013 / C067 / C085 must emit exactly this** or every iOS assertion fails.
  (c) ***ADD §9.4's Redis key space has no entry for a registered App Attest key.*** Apple publishes
  no server API for App Attest: the relying party verifies each assertion itself against the public
  key kept at registration. Added **`attest:appattest:{keyId}`** (HASH: `pk` = base64
  SubjectPublicKeyInfo DER for P-256, `counter` = uint32). **C026 (iam-svc) writes `pk`** when a
  device completes registration — it already owns `iam.devices.attestation_verified_at`
  (`server_db_schema.md` §1); the gateway only reads the key and moves `counter` forward. **ADD
  §9.4 and `MageRide.Shared`'s `RedisKeys` both need the entry.**
  (d) ***D7' §4.2 has no `api-gateway` row*** — every other service has one. The variables this
  service reads are: `ReverseProxy__Clusters__{cluster}__Destinations__primary__Address` (21, req),
  `Gateway__StateStore` (`Redis`|`Memory`), `Gateway__ForwardedHeaders__KnownProxies__0` (HAProxy,
  req), `Gateway__VersionGate__Platforms__{android|ios}__{MinimumVersion,RecommendedVersion,
  LatestVersion,UpdateUrl}`, `Gateway__Attestation__Mode`,
  `Gateway__Attestation__PlayIntegrity__{PackageName,ServiceAccountJson}` (**secret**),
  `Gateway__Attestation__AppAttest__AppId`, plus the common `ConnectionStrings__Redis` and
  `Otel__Endpoint`. **`Jwt__JwksUrl` is deliberately *not* one of them** — see decision (2).
  (e) ***`version-check` is not a deployable service.*** D3' Part 1 lists it in the service map and
  C007 produced `version-check.yaml`, but a client below the floor cannot reach a separate service
  *through the gate that is rejecting it*. `GET /v1/version/check` is served by the gateway itself
  from the same floor table the transparent gate uses, so the two can never disagree. **C009 must
  not create a version-check container**, and the contract's `servers` block already points at the
  gateway.
  **Other spec observations (no change needed):**
  (f) *C007's handoff says attestation is declared on 29 operations; the contracts carry **22**.*
  Counted mechanically both ways (all POST). The 22 are what is enforced.
  (g) *D6' §8.2's rule list is explicitly "illustrative" and is loose in two places:* it maps
  `/v1/wallet/**,/v1/fees/**` onto "wallet/subscription-svc" and `/v1/vehicles/**,/v1/trackers/**`
  onto "registry/provisioning" as if each pair were one target. They are four different services,
  and the landed table splits them. Nothing to change — the heading says illustrative.
  (h) *`/v1/drivers` has no owner.* Three services each own leaves of it — `profile` (registry-svc),
  `{driverId}/level` and `{driverId}/stats` (dispatch-svc), `{driverId}/block` (safety-svc) — and no
  spec assigns the prefix. Landed as three exact routes with **no catch-all**, so an unclaimed
  `/v1/drivers/...` path 404s at the edge rather than being guessed at.
  **Decisions —**
  (1) **The route table is its own artifact, `gateway-routes.json`, in four explicit order tiers.**
  Lower `Order` wins: **10** the cross-service literal overrides (the six C007 flagged, of which
  `/v1/rides/job-board`, `/v1/rides/schedule*` and `/v1/rides/scheduled/{driverId}` cross from
  ride-svc to dispatch-svc, plus `/v1/fleets/{fleetId}/trackers/bulk` crossing from fleet-svc to
  provisioning-svc and the three-way split of `/v1/geo`); **20** the `/v1/admin` sub-trees owned by
  iam / dispatch / reputation / fare / subscription / content / transit; **50** ordinary per-service
  prefixes; **90** the admin-bff catch-all, which must stay last or it swallows tier 20. ASP.NET's
  own literal-beats-parameter precedence would resolve most of these, but relying on it makes a
  cross-service boundary an emergent property; the tiers make it a reviewable one.
  (2) **The gateway does not validate JWTs, despite D6' §8.1 listing "JWT validate" among its
  jobs.** AL-06 is deny-by-default *authorization*, which needs the caller's effective role set and
  the target resource together — only the owning service has both, and it must re-validate the
  token anyway. Validating a second time at the edge buys nothing, puts a JWKS dependency on
  iam-svc in every request path, and opens a window during a 90-day signing-key rotation (D7' §13)
  in which the edge rejects a token the owning service would have accepted. The edge is therefore
  authentication-free and every route is explicitly `"AuthorizationPolicy": "anonymous"`.
  **Consequence:** the kernel's deny-by-default fallback policy is cleared here
  (`AuthorizationOptions.FallbackPolicy = null`). Without that, `UseAuthorization` challenges on any
  request with no matched endpoint, finds no `IAuthenticationService`, and turns **every unrouted
  path into a 500** — which is what the first run of the route-table test caught.
  (3) **`/v1/internal/**` is refused `404 not-found` ahead of routing**, not merely left unrouted.
  Ten contract operations live there and D3' §0 puts service-to-service traffic on mTLS
  (Linkerd/SPIFFE); they are in the contracts so a *calling service* knows their shape. 404 rather
  than 403: confirming that an internal path exists maps the internal surface for free.
  (4) **The D-31 gate runs inside the YARP proxy pipeline**, so the gateway's own endpoints —
  `/v1/version/check`, `/health/live`, `/health/ready`, `/metrics` — are exempt *by construction*
  rather than by an exemption list somebody has to maintain. `/public/**` (AL-44, opened in a
  browser from an SMS link) carries an explicit `VersionGate: exempt` metadata value.
  (5) **A caller that names a platform must name a version; a caller that names neither is not
  gated.** `X-Platform: android` with a missing or unparsable `X-App-Version` is a broken build and
  gets 426. A request with no `X-Platform` at all is a browser (both portals, the public track
  pages) and passes. Stripping the header is not a bypass worth closing by default: the floor exists
  for client/server compatibility (US-17.1/17.2), while the control against a tampered client is
  attestation, which cannot be evaded by omitting a header. `RequirePlatformHeader` makes the strict
  reading available and is tested.
  (6) **`ClientVersion` wraps C002's `AppVersion` rather than changing it.** Two rules the kernel
  type does not have: a semver **pre-release sorts below its release**, so a `1.6.0-rc.1` TestFlight
  build does not satisfy a floor of `1.6.0`; and the version must be **exactly three numeric
  segments**, because `AppVersion.TryParse("1.4")` silently yields `1.4.0` while the contract's
  regex forbids it. Non-numeric build metadata (`+exp.sha.5114f85`, legal semver and legal per the
  contract) is dropped for comparison since `AppVersion` cannot hold it; a numeric build code is
  kept, because that is exactly what distinguishes two shipped builds of one version. **C002's
  `AppVersion` is deliberately left alone** — it is a released record struct with its own tests.
  (7) **Attestation modes are `Disabled | Audit | Enforce`, with a per-platform override.** Android
  ships in Wave 4a and iOS in 4b; one global switch would mean either enforcing against an app that
  does not exist yet or leaving D-30 off for the platform that does. The dev compose profile runs
  `Disabled`; `Audit` logs the verdict and forwards, for staged rollout.
  (8) **Play Integrity is decoded server-side through Google's `decodeIntegrityToken`**, not by
  unwrapping the JWE locally, so the decryption and verification keys never leave Play Console. The
  JWT-bearer grant (RFC 7523) is hand-rolled rather than pulling in `Google.Apis.Auth` for one
  signed assertion and one form post. Verdicts are cached **positive-only** and keyed on a hash of
  the token — caching a rejection would pin a device out after a transient failure, and caching the
  token itself would put an attestation credential in Redis in clear. Any failure to reach or parse
  Google is a **rejection**, not a pass: an open failure mode makes D-30 a control that any outage
  switches off. **Open hardening item for C128:** there is no server-issued nonce today, so the
  binding is package + verdicts + a 5-minute token-age window rather than a challenge; adding a
  challenge endpoint is an iam-svc change, not a gateway one.
  (9) **App Attest is verified locally per Apple's assertion algorithm** — CBOR decode
  (`System.Formats.Cbor`, first-party, because the parse runs on attacker-supplied bytes), `rpIdHash
  == SHA-256(appId)`, ECDSA P-256 over `SHA-256(authenticatorData || clientDataHash)`, and a
  **strictly increasing signature counter**. The counter is the replay defence and it is advanced
  through a Lua CAS so two replicas verifying concurrently cannot let the lower value win. Because
  the signed client data is `"{METHOD} {path}"`, an assertion captured from
  `POST /v1/auth/otp/request` cannot be lifted onto `POST /v1/wallet/topup/onepay` — proved by a
  test that builds real assertions with a generated P-256 key.
  (10) **Edge rate limits are coarse ceilings, keyed `route|clientAddress`, and fail open.** They
  reuse C002's `ITokenBucketRateLimiter` (Redis, so N replicas share one bucket) with an in-process
  implementation for a single-instance gateway. The named business limits stay in the services that
  own them — OTP 5/h + 60 s cooldown (D-32) keys on a phone number, proxy location requests 5/h +
  30/d (P-12) key on a booker id, and the edge can see neither. **Fail-open is deliberate:** a
  limiter that fails closed turns a Redis blip into a total platform outage, and the services behind
  it still enforce their own limits. **The partition key is the address, never the JWT `sub`** —
  the gateway does not validate tokens, so a `sub` read here would be unverified and a caller could
  mint a fresh one per request to reset its own bucket. `/v1/sos` has its own policy so an SOS
  cannot run out of budget behind ordinary reads (D-33's 5 s p99).
  (11) **`X-MageRide-Upstream` is a config-gated diagnostic, off by default.** Turning it on is how
  the DoD's "route table resolves every service" assertion is made against a *running* gateway with
  one stub upstream instead of 21 — the cluster that served each of the 262 contract paths is read
  back off the response. Off in production: the cluster map is internal topology.
  (12) **A failed forward becomes problem+json.** YARP answers an unreachable destination with a
  bare 502 and no body; every other error a client can see is `application/problem+json` with a
  registry code, so a naked 502 would be the one response a client cannot parse — and the one most
  likely to arrive during an incident. Mapped onto the codes D6' §8.3 already implies:
  `upstream-timeout` (504) for a timeout, `dependency-unavailable` (503) otherwise, and nothing at
  all when the caller has hung up.
  (13) **`X-Request-Id` is written from an `OnStarting` callback, not set up front.**
  `UseExceptionHandler` clears the response before re-running the pipeline, which drops a header set
  earlier — precisely on the responses whose id matters most. The gateway also rewrites the outbound
  `traceparent` from its own span, so a backend trace parents to the gateway hop instead of to
  whatever the client sent, and the edge does not vanish from the trace.
  (14) **Route metadata is load-bearing, never decorative.** `RateLimit`, `VersionGate: exempt` and
  `Streaming: true` are all read or asserted: `RouteConfigurationTests` fails the build if a route
  names an undefined policy, uses an unknown metadata key, points at an undeclared cluster, omits
  `AuthorizationPolicy: anonymous`, sits outside `/v1` `/public` `/hubs`, or — for a streaming route
  — has a cluster that would still drop a quiet WebSocket on YARP's 100 s default or negotiate
  HTTP/2 (over which an upgrade cannot happen at all).
  (15) **The Dockerfile carries C003's `getent`-guarded user creation**, since D7' §2.2's
  unconditional `addgroup -S app` fails on the .NET 10 alpine images. Same fix, second image.
  **For later components —**
  **C009 (compose):** build `backend/src/ApiGateway/Dockerfile` from the repo root; set
  `Gateway__ForwardedHeaders__KnownProxies__0` to HAProxy's address or **every caller collapses into
  one rate-limit bucket**; keep `Gateway__StateStore=Redis` for any multi-replica deployment; and
  **HAProxy must not publish `/health/live`, `/health/ready` or `/metrics` from the gateway to the
  internet** — the shared kernel maps all three anonymously on every service, which is right for an
  internal service and wrong for the public edge. No version-check container (gap (e)).
  **C013 / C067 / C085 (clients):** send `X-App-Version` **and** `X-Platform` on every request, and
  the `X-Attestation` wire format from gap (b) on the 22 sensitive operations.
  **C026 (iam-svc):** owns App Attest registration and writes `attest:appattest:{keyId}` (gap (c)).
  **C118 (contract tests):** `ContractCatalog` here already parses every contract and derives the
  owning service from the file name; the same sweep is the cheapest form of "no endpoint is
  unroutable" and can be lifted wholesale.
  **C128 (anti-spoof):** the Play Integrity nonce challenge in decision (8), and a review of whether
  `RequiredLicensingVerdicts` should become non-empty in production.
  **Build host —** no Docker, no database, no replica stack; the suite binds ephemeral loopback
  ports for a stub upstream and the gateway and runs in about 15 s. iOS is not involved — the App
  Attest verification is server-side C# and is tested with locally generated P-256 keys.

- **Component:** C009 docker-compose-dev — 2026-07-27
- **Status:** DONE — the prompt's verify chain (`config && up -d && wait-healthy.sh && down`) runs
  green from an empty Docker state, and `bash infra/scripts/slim-verify.sh` → **66/66 checks
  passed** from a clean slate (volumes removed first). Nine containers in the slim stack: six
  healthy, three one-shots exited 0; `migrate` applied all **67** C003–C006 scripts in 3.9 s. All
  five DoD items pass. The replica stack stayed down throughout.
- **Notes:**
  **Spec gaps — six of these are not stylistic: the compose block printed in D7' §3 does not
  start. None are actioned in `specs/`.**
  (a) ***`volumes: [pgdata:/var/lib/postgresql/data]` is the wrong path for the image on the same
  line.*** D7' §3 pairs `timescale/timescaledb-ha:pg16` (correct per T-06) with the **official
  postgres image's** data directory. This image sets `PGDATA=/home/postgres/pgdata/data` and runs
  as the non-root `postgres` user, so an empty root-owned volume mounted at the §3 path gives
  `mkdir: cannot create directory '/var/lib/postgresql/data/pgdata': Permission denied` and a
  restart loop on first boot. Landed as `pgdata:/home/postgres/pgdata`. C003–C006 never hit this
  because `migrate-verify.sh` runs the image with no volume at all.
  (b) ***`-c shared_preload_libraries=timescaledb,postgis-3` refuses to start the postmaster.***
  PostGIS is a plain extension with no preloadable library; only `timescaledb` belongs there, and
  it is what the image's own entrypoint already sets.
  (c) ***`redpandadata/redpanda:v24.2` is not a pullable tag*** (D7' §2.1, §9, and the replica's
  Container 3). Redpanda publishes only full patch tags. Pinned to **`v24.2.26`**, the last v24.2
  patch. A floating minor tag would not have been reproducible anyway.
  (d) ***Redpanda does not run with fsync unless it is told to.*** The replica states
  "`--mode dev-container` is **not** used; replica runs in production mode with `fsync` enabled per
  partition", which is what makes its "0 data loss on process kill" true. The image's shipped
  `/etc/redpanda/redpanda.yaml` carries `developer_mode: true`, and rpk then silently appends
  `--unsafe-bypass-fsync=true` to the broker. Neither `rpk redpanda start --mode` (it accepts only
  `dev-container`) nor `--set redpanda.developer_mode=false` corrects it — rpk computes the broker
  flags before applying `--set`. Landed as `rpk redpanda mode production` immediately before
  `start`, verified by reading the broker's `/proc/<pid>/cmdline`. **D7' §3's command line needs
  this step.**
  (e) ***`EMQX_AUTHENTICATION__1__TYPE: jwt` (D7' §3) is not a key EMQX 5 has*** — the field is
  `mechanism`. Also, `--set=k=v` on `rpk redpanda start` is passed through to the redpanda binary
  verbatim and dies with "unrecognised option"; `--set` takes its argument as a separate token.
  (f) ***HAProxy cannot carry port 5026.*** D7' §2.1 and the replica's Container 1 both list
  `5026 (NMEA UDP)` among HAProxy's ports and call the tracker ports "L4 passthrough". HAProxy has
  no UDP forwarder — its only UDP support is syslog and DNS resolution. The same document already
  states this constraint for TURN media ("HAProxy is L4/L7 TCP/HTTP and cannot relay UDP media
  efficiently"), so the rule exists; the NMEA row just missed it. **5026/udp is published directly
  off the `tcp-adapter` container**; 5023–5025 stay behind HAProxy as specified.
  (g) ***`ComBankIpg__WebhookSecret` (D7' §4.2, wallet-svc) contradicts AL-05.*** D7' §9 in the
  same document says "no bank-transfer IPG, AL-05", and C007 landed no
  `/v1/wallet/topup/bank-transfer` operation. Listed in the template so the DoD's "every §4
  variable" holds, but left empty with a comment — nothing reads it. **D7' §4.2 should drop the
  row.**
  (h) ***D7' §3 gives `.env.app` to `app-services` only***, yet §4.2 has rows for
  position-processor, persistence-writer, mqtt-bridge and fleet-health (the four services inside
  `hot-path`), for fanout-svc and for tcp-adapter. Those variables have to reach the process that
  reads them, so all four containers load it.
  (i) ***D7' §4 defines no object-store endpoint or credentials, and no MQTT broker address.***
  §4.2 gives support-svc `Storage__ScreenshotBucket` while the replica's Container 10 names four
  distinct uses (ocr-svc documents, proof-of-delivery photos, profile pictures, pg_dump), and
  mqtt-bridge gets `Emqx__SharedSub` with nothing saying which broker. Added `Storage__S3__*`, the
  four bucket names and `Mqtt__BrokerUrl` to `.env.common.example`, each flagged as a §4.1
  micro-change-set candidate in place.
  **Other spec observations (no change needed):**
  (j) *`emqx/emqx:5.8` open-source has no Kafka producer bridge.* The replica's Container 2 says
  the EMQX rule engine "can directly sink to Redpanda via the Kafka-protocol producer, so at light
  scale we skip deploying a separate `mqtt-bridge-svc`". Data integrations are an **EMQX Enterprise**
  feature; the community image cannot do it. This costs nothing — D6' §3.3 specifies
  `mqtt-bridge-svc` with the E-08 shared subscription regardless, and C038 owns it — but the
  replica's "skip mqtt-bridge-svc" note is not available on this image. **C125 must either accept
  mqtt-bridge-svc as mandatory (recommended, it is already in `hot-path`) or switch to
  `emqx/emqx-enterprise`.**
  (k) *MinIO's API port has no HAProxy entry.* Container 10 says port 9000 is "behind HAProxy
  passthrough" but Container 1's port list has no line for it. Landed as an `s3.` **vhost on 443**
  rather than a new port: pre-signed upload/download URLs for driver documents and delivery proofs
  are handed to a phone, so the object store must be reachable from outside the docker network.
  (l) *Cosmetic:* D7' §2.1/§9 say `redis:7-alpine`, the replica's Container 4 says `redis:7.4-alpine`.
  Took D7' §3's spelling (`7-alpine`), which is also what C002's tests already pull.
  **Decisions —**
  (1) **Two files, one compose project.** `docker-compose.dev.yml` **`include`s**
  `docker-compose.dev.slim.yml` rather than copying it, so there is exactly one definition of
  Postgres, Redis, Redpanda, EMQX and MinIO and the stacks cannot drift; both declare
  `name: mageride`, the `mageride_mr` network and the same volumes, so `full` is genuinely `slim`
  plus applications and never a second database. The one thing the full file overrides is EMQX's
  host ports (`!override`) — HAProxy owns 8883/8084 there, and two publishers cannot share a port.
  (2) **The committed `.example` files ARE the default config layer**, loaded first by every
  service, with the gitignored `env/.env.common` / `env/.env.app` loaded second (`required: false`)
  as the local override. That is what lets `docker compose config` and `up` work on a clean
  checkout with no setup step. Nothing copies the example to the real file on purpose: a copy made
  once would silently shadow every later edit to the template.
  (3) **Compose interpolates `env_file` values.** `Emqx__SharedSub=$share/posGroup/…` reached the
  container as `/posGroup/…` with a "variable is not set" warning — the E-08 shared subscription
  silently degraded to an ordinary one. Written `$$share`, verified by reading `printenv` inside a
  container, and `slim-verify.sh` now **fails on any such warning** so the next `$` cannot slip
  through.
  (4) **The slim stack is sized below the canonical replica budgets** (~5.9 GB against ~16.7 GB):
  postgres 2 GB, redpanda 1.5, emqx 1.5, redis 0.5, minio 0.25, pgbouncer 0.125. Its job is to let
  a component's verify run *alongside* a `dotnet build` on the shared 24 GB box, not to carry load.
  `docker-compose.dev.yml` uses the replica's numbers for the app containers.
  (5) **Every published port is bound to `127.0.0.1`.** This box has a public IP and also hosts the
  replica; a bare `5432:5432` publishes a dev database to the internet. Every port is
  `${VAR:-default}` so a second stack can be moved aside without editing the file.
  (6) **EMQX identity: MQTT username = the principal, and the JWT must agree with it.**
  `verify_claims = { vehicleId = "${username}" }` refuses the CONNECT unless the session token's
  `vehicleId` claim equals the connecting username, so the `veh/${username}/…` rules in `acl.conf`
  are written against a claim the **broker** has verified rather than a self-asserted string. That
  is D6' §3.1's "EMQX binds the vehicleId JWT/X.509 claim" made mechanical, and it makes the DoD
  case fail in **both** directions: a token minted for vehicle B cannot connect as vehicle A, and a
  correctly-bound vehicle A cannot publish to B's topic. Platform components use the same shape
  with a `svc-*` username; one ACL rule grants that prefix the E-08 shared subscription and the
  T-04 LWT-emulation publish. Dev signs with a shared HMAC secret because iam-svc (C026) and
  provisioning-svc (C030) do not exist yet; the RS256 **JWKS block with D-21's 15-minute
  `refresh_interval` is written out and commented in `emqx.conf`** for C030/C125 to switch on.
  (7) **`authorization.no_match = deny` and `deny_action = disconnect`.** EMQX's shipped default is
  *allow*, which would make every rule in `acl.conf` advisory. `disconnect` rather than `ignore`
  because an MQTT 3.1.1 device gets no error code for a refused publish: silently dropping it would
  leave a misprovisioned tracker publishing into a void for its whole 90-day credential.
  (8) **D-17's 5 msg/s is a listener `messages_rate`, not a rule-engine counter.** D6' §3.3
  specifies it per `vehicleId`; a listener limit is per connection, and since a connection
  authenticates as exactly one vehicleId the two agree for every case the broker can see, at a
  fraction of the cost. What it cannot do is emit `mqtt.rate_violation` onto `audit.events` —
  **that half stays C038/C125's**, and `position-processor`'s second-line 10 msg/s/10 s limit is
  unaffected.
  (9) **`migrate` connects directly to Postgres, never through PgBouncer.** The scripts create
  roles (C006's `1804` needs CREATEROLE), run one transaction per script and take advisory locks —
  none of which belong on a transaction-pooled connection. Same reasoning as C002's
  `ConnectionStrings__PostgresDirect`, which is now in `.env.common.example` for ride-svc and
  dispatch-svc.
  (10) **Redpanda advertises two listeners, `internal` and `external`.** Without the advertise pair
  the broker advertises its container hostname, and every client outside docker fails on the
  *second* (metadata-driven) connection rather than the first — which looks like a broker fault.
  `redpanda:9092` in-cluster, `127.0.0.1:19092` from the host, so a `dotnet test` on the box and a
  container hit the same broker.
  (11) **The bootstrap creates the D6' §2.3 dead-letter partner of every topic** (`<topic>.dlq`,
  1 partition — a DLQ is drained by a human or a replay tool, and total ordering beats
  parallelism). 12 topics in total. It is idempotent and re-applies configuration on every run, so
  a hand-edited retention is corrected on the next `dev-up`; the verify runs it twice and asserts
  no thirteenth topic appears.
  (12) **Topic retention is a decision, not a spec value** — no spec pins one. telemetry.* 24 h
  (already durable in `trips.position_samples` / `telemetry.positions` once persistence-writer has
  consumed it), `*.events` 7 d (a consumer down over a weekend must still catch up), `*.dlq` 30 d.
  Each is overridable by an env var and the reasoning is in the script.
  (13) **HAProxy uses `init-addr none`, not the usual `last,libc,none`.** A dev box routinely runs
  with some backends absent, and `libc` blocks on a synchronous `getaddrinfo` per unresolvable
  name: measured **~30 s before HAProxy bound a single port** with six missing backends. With
  `none` the server starts DOWN at 0.0.0.0 and the `docker` resolver brings it up within a second —
  healthz answered immediately, and MinIO was marked UP "thanks to valid DNS answer". The resolver
  is also what makes a `docker compose restart app-services` transparent instead of a blackhole.
  (14) **HAProxy 404s `/health/live`, `/health/ready`, `/metrics` and `/v1/internal/**`.** C008's
  handoff asks for the first three: MageRide.Shared maps them anonymously on every service, which
  is right internally and wrong on the public edge, where `/health/ready` names every dependency it
  probes and `/metrics` is the internal topology. `/v1/internal/**` is refused at the edge as well
  as at the gateway so a misconfigured route cannot expose an mTLS-only path. 404 rather than 403 —
  a 403 confirms the path exists. All four verified against a live HAProxy on the dev network.
  (15) **`api-gateway` is its own container in the full stack.** The replica co-locates it inside
  `app-services` ("21 domain services behind a single YARP gateway process") but D7' §5's Ingress
  names `api-gateway` as its own backend service, and C008 shipped it as its own project and image.
  Landed as a separate container with **20 clusters pointing at `app-services:5000` and
  `fanout-svc` at `fanout:5001`** — still exactly one gateway process, which is what the replica
  sentence is actually about. This is also where `Gateway__ForwardedHeaders__KnownProxies__0` is
  set to HAProxy, without which every caller collapses into one rate-limit bucket.
  (16) **Two further one-shots beside `migrate`:** `redpanda-init` (topics) and `minio-init` (the
  four buckets `.env.common.example` names). Without the second, every `Storage__*` variable points
  at a bucket that does not exist.
  (17) **`wait-healthy.sh` reads which services are one-shots out of `docker compose config`**
  (`restart: "no"`) instead of hard-coding names, so adding a one-shot to either file needs no edit
  there. It fails fast on `unhealthy` or a non-zero exit rather than burning the timeout — a
  Postgres that died in initdb is not going to recover by waiting — and dumps the last 40 log lines
  of whatever failed.
  (18) **`dev-up.sh` refuses to start while the replica project is running** (root CLAUDE.md
  fence), generates the self-signed dev certificate HAProxy needs, and — for the full stack —
  checks each build Dockerfile and names the component that lands the missing ones instead of
  letting docker fail with a bare "failed to read dockerfile".
  (19) **One change outside this component's files:** `backend/src/MageRide.Migrations/Dockerfile`
  gained `apk add --no-cache krb5-libs`. Npgsql probes for GSSAPI on connect, and without it the
  alpine runtime printed `Error: Error loading shared library libgssapi_krb5.so.2` on **every**
  migrate run — harmless (the connection proceeded) but indistinguishable from a real migration
  failure in a compose log, in the stack every later component is told to bring up. C003's own
  verify runs `dotnet run` on the host and is unaffected.
  (20) **`slim-verify.sh` is the DoD proof and does functional tests, not container introspection**
  (66 checks). It mints HS256 tokens with `openssl` alone — no python or PyJWT on the build host —
  and drives a real `eclipse-mosquitto` client on the compose network. Beyond the two DoD
  directions it proves the *positive* case first (an ACL that denied everything would pass every
  negative check and look like a working policy), then that no credentials, a forged signature, an
  expired token, an off-tree topic, `$SYS/#` and a `veh/+/pos/live` firehose are all refused, while
  `svc-mqtt-bridge` still gets its E-08 shared subscription. It also asserts the journal holds 67
  scripts, that PgBouncer really is in transaction mode, that each topic is partitions=3 /
  replicas=1, and that every D7' §4.1 and §4.2 variable is present — with no `Mfa__*` (AL-37,
  planner finding 3) and every secret-marked row an empty or `CHANGEME_` placeholder.
  **For later components —**
  **C038 / C043 (mqtt-bridge, tcp-adapter):** connect to EMQX with username **`svc-mqtt-bridge`** /
  **`svc-tcp-adapter`** and a session JWT whose `vehicleId` claim equals that username; the
  `^svc-` ACL rule is what grants `$share/posGroup/...` and cross-vehicle publish. Any other
  username is denied by `no_match = deny`.
  **C030 (provisioning-svc):** owns the switch from the dev HMAC secret to RS256 JWKS — the block
  is written and commented in `infra/deploy/emqx/emqx.conf`, and D-21's 15-minute cache is its
  `refresh_interval`.
  **C040 (persistence-writer):** `Timescale__BatchRows` / `Timescale__FlushMs` are in
  `.env.app.example`; remember C006's three-column conflict target.
  **C104 / C111 (portals):** `haproxy.cfg` already carries the `admin.` and `fleet.` vhost backends
  and starts cleanly while those containers are absent, so adding a portal needs no HAProxy change
  — only a compose service on 3001 / 3002.
  **C119 (observability):** `Otel__Endpoint` is empty in the template; the `monitoring` container is
  not declared in either dev file (D7' §3 has it as a comment, not a service).
  **C125 (replica):** the six blocking fixes above are the difference between D7' §3 as printed and
  a stack that boots — take `docker-compose.dev.yml` as the starting point, not the spec listing.
  Also: bind ports on the public interface with a real certificate rather than `127.0.0.1`, decide
  the enterprise-vs-mqtt-bridge question in (j), replace `Gateway__Attestation__Mode=Disabled` with
  `Audit`, and re-examine running Postgres as the superuser (C006's `1804` needs CREATEROLE).
  **Build host —** Docker only; no .NET build beyond the `migrate` image (which needs
  `mcr.microsoft.com/dotnet/sdk:10.0`, pulled once). New images cached: `redpandadata/redpanda:v24.2.26`,
  `emqx/emqx:5.8`, `edoburu/pgbouncer`, `minio/minio`, `haproxy:2.9-alpine`, `eclipse-mosquitto:2`
  (~700 MB in total). Peak footprint of the slim stack is ~5.9 GB and the replica stack stayed down
  throughout; `slim-verify.sh` removes the stack and its volumes on exit, including on failure, and
  the box was left with nothing running. The full verify takes roughly 3 minutes, most of it EMQX's
  ~40 s start-up and the fifteen throwaway MQTT client containers.

- **Component:** C010 ci-skeleton — 2026-07-27
- **Status:** DONE — the prompt's verify (`yaml.safe_load(ci.yml) && docker build -f
  infra/docker/Dockerfile.service --build-arg SERVICE=MageRide.Shared`) exits 0, and every job the
  workflow declares was run by hand against this tree: `dotnet test backend/MageRide.sln -c Release`
  with `MAGERIDE_REQUIRE_CONTAINERS=1` → **685 passed** (161 kernel + 524 gateway, 0 skipped),
  `bash infra/scripts/migrate-verify.sh` → **187/187**, spectral → 0 errors, the portal and android
  legs → 0, and `actionlint` → 0. All four DoD items pass. **Wave 0 is complete.**
- **Notes:**
  **Spec gaps —**
  (a) ***D7' §7's `runs-on` expression cannot express the iOS fence.*** The spec writes one
  conditional — `runs-on: ${{ matrix.target == 'ios' && 'macos-14' || 'ubuntu-latest' }}` — so the
  runner for every leg is decided by one string, and a typo in it silently reschedules the iOS leg
  onto Ubuntu where Xcode does not exist and every step that matters is skipped into a green build.
  Landed as a `matrix.include` with a literal `runner:` per leg, and the iOS leg's first step fails
  the job unless `RUNNER_OS = macOS`. This also satisfies the DoD's "every job has an explicit
  runs-on" without relying on how GitHub evaluates a conditional. **D7' §7's snippet should adopt
  the include form.**
  (b) ***D7' §2.2's Dockerfile template does not survive contact with central package
  management.*** It copies exactly two `.csproj` files before `dotnet restore`; with
  `backend/Directory.Packages.props` in force (C002 decision 1) a partial project graph fails
  **NU1008** before the build starts. The templates copy `backend/src/` whole. The same template
  also still carries the unconditional `addgroup -S app` that C003 note (i) recorded as broken on
  the .NET 10 alpine images — fixed here for the third time, so **D7' §2.2 needs both edits**.
  (c) ***`ENTRYPOINT ["dotnet", "${SERVICE}.dll"]` is wrong for at least one existing service.***
  D7' §2.2 assumes the entry assembly is named after the project directory; `ApiGateway` publishes
  `MageRide.ApiGateway.dll`. Resolved at build time from the published `*.runtimeconfig.json` —
  exactly one assembly in a published application has one, and that is the one `dotnet` can launch.
  (d) ***The prompt's verify builds a class library.*** `MageRide.Shared` has no `Main`, so the
  image it produces is structurally complete — `aspnet:10.0-alpine`, user `app`, port 5000, the D7'
  §5.1 healthcheck — but cannot be started. The template falls back to `${SERVICE}.dll` when no
  runtimeconfig is published so that build still succeeds. **"Runnable" was proved separately**: the
  same template built `ApiGateway`, and the container came up in 2 s as uid `app` and answered
  `GET /health/live` with `{"status":"Healthy"}`.
  (e) *`Testcontainers.PostgreSql` 4.13 obsoletes the parameterless builder constructor*, so
  `new PostgreSqlBuilder(image)` is the supported form. With `TreatWarningsAsErrors=true` the
  obsolete call is a build error, not a warning — worth knowing before a wave-2 component copies an
  older snippet.
  **Decisions —**
  (1) **`MageRide.Migrations` gained a public `MigrationEngine`, and `Program.cs` now calls it.**
  The DbUp pipeline — journal `public.schema_versions`, one transaction per script, filename
  ordering, the `--ignore-journal` null-journal swap — was 15 lines of top-level statements, so a
  test could only have re-implemented it. A second DbUp configuration would drift on exactly the
  things that matter and the drift would surface as a *deploy* failure. Nothing about the pipeline
  changed; `migrate-verify.sh` still passes 187/187, which is the proof.
  (2) **`MageRide.TestKit` uses `timescale/timescaledb-ha:pg16`, not `postgis/postgis:16-3.4`.**
  C002's fixtures ran PostGIS-only, which was right when no migration touched TimescaleDB; C006's
  DDL creates a hypertable and four continuous aggregates, so the migration set cannot be applied on
  that image at all. Every TestKit image now matches `infra/docker-compose.dev.slim.yml` exactly
  (redis 7-alpine, redpanda v24.2.26), and a test asserts the container really carries postgis +
  timescaledb + pgcrypto + citext — the difference between "migrations are broken" and "the harness
  is testing the wrong server".
  (3) **C002's `DockerFixtures.cs` was deleted and its tests re-pointed at the TestKit.** A harness
  the one existing test project does not use is a fiction, and two `PostgresFixture` types in one
  assembly is worse. The 152 C002 tests are unchanged in substance and still pass.
  (4) **`[Collection<T>]`, not `[Collection("name")]`.** xUnit resolves a string collection name
  against definitions *in the test assembly only*; the definitions now live in the TestKit, so the
  string form fails discovery with "the following constructor parameters did not have matching
  fixture data" — which is how the first run of the moved tests failed. The generic form resolves
  the definition by type and works across assemblies. **Recorded in `MageRide.TestKit/CLAUDE.md`
  because every wave-2 component will hit it.**
  (5) **Skip-on-no-Docker stays, but CI cannot skip.** The fixtures still `Assert.Skip` when the
  daemon is unreachable so a developer without Docker runs the unit tests; `ContainerFixture` reads
  **`MAGERIDE_REQUIRE_CONTAINERS=1`** and turns that into a hard failure, and the backend job sets
  it. Without this a runner with broken Docker reports green having run no integration test at all —
  the failure mode a skip-based harness is *designed* to produce.
  (6) **Three templates, one build context: the repository root.** Restore needs `global.json` and
  `backend/Directory.*.props` from above the project directory, and a portal needs the workspace
  lockfile and the shared Tailwind preset from above its own directory. `Dockerfile.worker` is
  `runtime`, not `aspnet` — pulling the ASP.NET shared framework into a process that never serves a
  request costs size and CVE surface for nothing, which is precisely why D7' §5.1 probes tcp-adapter
  with a TCP socket instead of `/health/live`.
  (7) **`HEALTH_PORT=none` turns the worker healthcheck off** for `hot-path`, which listens on
  nothing. A healthcheck that cannot observe the process is worse than none, because compose and
  Kubernetes both read "healthy" as "ready to serve". Both modes were verified against running
  containers: `none` → healthy, a live listener on 5023 → healthy, and nothing listening →
  unhealthy.
  (8) **`Dockerfile.portal` fails the build when the standalone bundle is missing.** Next.js only
  emits `.next/standalone` with `output: 'standalone'` in `next.config`; without it the build
  succeeds and the runtime stage has no `server.js`, i.e. a crash-looping container. **C104 / C111 /
  C117 must set it.**
  (9) **The android and iOS legs probe for a build *file*, not for the project.** `./gradlew
  projects` already prints `':shared'` and `':apps:driver-android'` — C001 declared the modules in
  `settings.gradle.kts` and deliberately left their build scripts to C011/C067 — so a probe on the
  project list would run wave-1 tasks that do not exist and fail today. Probing
  `shared/kmp/build.gradle.kts` and `apps/driver-android/build.gradle.kts` means the jobs grow with
  the repository instead of needing an edit in three later components. Caught by dry-running both
  legs, not by reading them.
  (10) **The AL-53 guard matches declarations, not words.** The first version grepped for
  `Microsoft.EntityFrameworkCore|DbContext|dotnet ef` and failed on `Directory.Build.props` and
  `Directory.Packages.props` — the two files whose *comments* forbid EF Core. It now matches a
  `using`, a `<PackageReference>`/`<PackageVersion>`, a `: DbContext` base type and an invocation of
  the tool in a script. Proved both ways: clean on this tree, and it rejects a deliberately-bad tree
  carrying all four shapes.
  (11) **A `compose` job exists that the deliverable list does not name.** Wave 0's gate includes
  "slim compose healthy", C009 shipped `slim-verify.sh`, and nothing else would ever run it. It is
  the most expensive job (~5 GB of image pulls, ~3 min) and is flagged as such in the workflow.
  (12) **`migrate-verify.sh` is the migration job, unchanged.** Its pass 2 already fails the build
  with "expected the second run to apply 0 scripts", which is the DoD verbatim; pass 3 re-runs every
  script with the journal disabled. The same property is *also* asserted in-process by
  `MigrationHarnessTests`, so a broken migration fails the fast backend job (~20 s) rather than
  waiting for the slow one.
  (13) **`concurrency` cancels superseded runs on branches but never on `main`** — every commit on
  main is a release candidate and needs its own result.
  (14) **`actionlint` is the second check on the workflow**, beyond the DoD's YAML parse. A workflow
  that parses can still name a context that does not exist or a shell variable that is never set;
  both checks are in `.github/workflows/README.md` so they can be run before pushing.
  **For later components —**
  **C011 / C067 / C076 (Gradle):** the android leg starts running `:shared:testDebugUnitTest detekt
  ktlintCheck` the moment `shared/kmp/build.gradle.kts` exists, and the app assemble when
  `apps/driver-android/build.gradle.kts` does. **A GitHub runner has no Android SDK preinstalled for
  AGP 9** — C011 should add `android-actions/setup-android` to that leg if the build needs it.
  **C085 / C094 (iOS):** the leg archives `apps/{driver,passenger}-ios/<Scheme>.xcodeproj` with
  `CODE_SIGNING_ALLOWED=NO`; signing and TestFlight upload are C124's, along with the
  `APPLE_API_KEY` / `ANDROID_KEYSTORE` secrets D7' §13 lists.
  **C104 / C111 / C117 (portals):** set `output: 'standalone'`, and give each workspace a `lint`,
  `test` and `build` script — the portal leg fans out with `--workspaces --if-present`, so a missing
  script is silently skipped rather than failed.
  **Every wave-2 service:** add the project to `backend/MageRide.sln` (CI runs the solution, not a
  list), reference `MageRide.TestKit` for integration tests, and use `[Collection<T>]`.
  **C124 (full CD):** wave 5/6's verify commands — `infra/replica/deploy.sh`, `chaos/run-drills.sh`,
  `k6 run load/*.js`, `kubectl apply --dry-run -k`, `acceptance/sg/run.sh` — are deliberately absent
  here; they need a deployed replica, a load generator or the Singapore region. The mapping table in
  `.github/workflows/README.md` says so explicitly so they are not mistaken for an omission.
  **Build host —** Docker plus the .NET SDK. New images pulled: `mcr.microsoft.com/dotnet/runtime:10.0-alpine`
  (the worker base). `actionlint` v1.7.7 was downloaded to the scratchpad for linting and is **not**
  vendored into the repo — CI does not run it today; it is documented as a pre-push check.
  The replica stack stayed down throughout and no dev stack was left running. The full local
  reproduction of every job takes about six minutes, most of it `migrate-verify.sh`.

- **Component:** C011 kmp-module-scaffold — 2026-07-27
- **Status:** DONE (common + Android targets; iOS declared and type-checked, **not** verified) —
  `./gradlew :shared:testDebugUnitTest detekt ktlintCheck` → **BUILD SUCCESSFUL, 10 tests passed,
  0 failed, 0 skipped**, run from a clean build directory with `--no-build-cache` (22 tasks
  executed). All three DoD items pass: the test task runs green, `:shared:assembleXCFramework` is
  defined and fails fast off macOS, detekt and ktlint are clean. `./gradlew projects` (C001's
  verify) and `:shared:build` (D7' §6) both still pass.
- **Notes:**
  **Spec gaps / micro-change-sets —**
  (a) ***`:shared:testDebugUnitTest` no longer exists as a real task — micro-change-set for
  `build/manifest.yaml`.*** **AGP 9 refuses to apply `com.android.library` to a Kotlin
  Multiplatform project at all** — it fails with "not compatible with the
  'org.jetbrains.kotlin.multiplatform' plugin since AGP 9.0" and offers exactly two ways out:
  the replacement plugin `com.android.kotlin.multiplatform.library`, or setting
  `android.builtInKotlin=false` + `android.newDsl=false` to "temporarily bypass this issue".
  Took the replacement plugin — the bypass is a deprecation shim that dies at AGP 10 and would
  also have to be carried by C067/C076. The consequence: **the new plugin has no build variants**,
  so there is no `debug`, the local unit-test task is `testAndroidHostTest`, and the source set is
  `androidHostTest` rather than `androidUnitTest`. `:shared:testDebugUnitTest` is registered as a
  **lifecycle alias** for it so the manifest's `verify_cmd`, this wave's gate, the four wave-1
  prompts and `ci.yml` all keep working unchanged. It is not a weaker check — `androidHostTest`
  dependsOn `commonTest`, so the alias runs exactly what the variant-era task ran. **The manifest
  should retarget the wave-1 `verify_cmd` (and the wave gate line) at
  `:shared:testAndroidHostTest`**, after which the alias can go. D7' §7's `android` matrix step is
  unaffected (it names `:shared:build`, which works).
  (b) ***ADD §18.2's "H3 Kotlin" library does not exist.*** The table lists H3 under "KMP Shared
  Libraries", implying a multiplatform artifact. There is none on Maven Central: `com.uber:h3`
  4.4.0 is **JNI** — its jar ships `android-arm/`, `android-arm64/`, `linux-*`, `darwin-*` and
  `windows-*` natives and no klib, and the only other candidate (`io.github.luneo7:h3`) is a
  rebuild of the same Java bindings. So H3 is an **androidMain/JVM-only** dependency and **C017
  needs an expect/actual**: Android on `com.uber:h3`, iOS on cinterop against the H3 C library (or
  a pure-Kotlin port of the handful of functions §7.5 actually needs — `latLngToCell`,
  `gridDisk`). Two further consequences for C017: the jar carries **no android-x86_64 native**, so
  H3 will not work on an x86_64 emulator, and the JVM natives are extracted to a temp file at
  runtime. Declared in the catalog, deliberately **not wired into a source set** here.
  (c) *Cosmetic:* `gradle/libs.versions.toml` calls compile/target SDK 36 "the newest fully-released
  platform at scaffold time (2026-07-27)". As of this session `platforms;android-37.0` and `37.1`
  are both released. Left at 36 — C001's comment reserves that call for C067, and 36 is what the
  SDK on this host now carries.
  **Decisions —**
  (1) **`explicitApi()` is on.** This module is the API surface for four apps and two languages;
  an inferred return type crosses into the XCFramework as whatever the compiler guessed. Every
  wave-1 component now has to write `public` and a return type. Tests are exempt automatically.
  (2) **The XCFramework is static and named `MageRideShared`** (`import MageRideShared` on the
  Swift side). Dynamic would have to be embedded and re-signed by both Xcode projects and breaks
  SwiftUI previews. `XCFramework("MageRideShared")` registers
  `assembleMageRideSharedReleaseXCFramework`; **`assembleXCFramework` is registered by hand** on
  top of it because that bare name is what D7' §6/§7 and `ci.yml`'s iOS leg invoke. Off macOS it
  fails with a message naming the Linux verify command instead of dying inside the linker —
  verified by running it here.
  (3) **iOS klib cross-compilation is ON** (`kotlin.native.enableKlibsCrossCompilation=true` in
  `gradle.properties`). Kotlin/Native can build Apple *klibs* on Linux even though it cannot link
  a framework or run a simulator test, so `./gradlew :shared:compileKotlinIosArm64` **type-checks
  `src/iosMain` on this box** — verified, and `:shared:build` compiled all three iOS klibs here.
  Every wave-1 component that writes an iOS `actual` should run it; a compile error found now is
  a compile error not found in wave 4b. **This does not make iOS verified** and the fence stands:
  linking, `assembleXCFramework` and `iosTest` are macOS-only, and no iOS target is marked DONE
  from this host. Cost is a one-off ~1 GB Kotlin/Native distribution in `~/.konan`.
  (4) **Wired vs. declared.** The scaffold wires what it is: coroutines, serialization, datetime,
  Koin, Ktor (core/content-negotiation/logging/json + OkHttp on Android + Darwin on iOS) and the
  test stack (kotlin-test, coroutines-test, Turbine, koin-test, ktor-client-mock). Four entries are
  **declared in the catalog and left unapplied**, each with its owner named in the file:
  `multiplatform-settings` + `ktor-client-auth` (C014), SQLDelight — plugin, runtime, coroutines
  extensions and all three drivers (C018), `h3` (C017). **The SQLDelight Gradle plugin is not
  applied here** — with no `.sq` file it configures an empty database and generates nothing; C018
  applies it. Every unapplied coordinate was resolved against Maven Central by hand this session.
  (5) **`kotlinx-serialization-json`, `kotlinx-datetime` and `koin-core` are `api`, not
  `implementation`** — DTOs (C012) expose `@Serializable` types and `LocalDate`, and the apps
  start Koin themselves. The rest, including all of Ktor, is `implementation`: nothing outside
  this module should be able to reach for an `HttpClient` directly.
  (6) **One Koin module per component, appended to `sharedModules`.** `sharedCoreModule` holds only
  what the scaffold owns (the shared `Json`, `PlatformInfo`). Apps are told to use `sharedModules`,
  never the individual modules, so a binding added in C015 needs no edit in any of the four apps.
  `initKoin()` exists mainly for iOS — Swift cannot express Koin's trailing-lambda DSL comfortably.
  (7) **`MageRideJson` is `ignoreUnknownKeys` + `explicitNulls=false` + `encodeDefaults=false`, and
  deliberately NOT lenient and NOT `coerceInputValues`.** The first three mirror the backend
  (C002 serialises with `WhenWritingNull`) and keep an additive server change from crashing an
  older build; the last two are the point — a malformed number or an unknown enum is a contract
  violation and must surface, not silently become a default.
  (8) **Only one expect/actual exists: `platformInfo()`.** The fence allows expect/actual for secure
  storage, attestation and crypto; device identity is the same class of thing (the gateway's
  version gate and attestation key off it, C008) and it is the one piece of DI wiring that cannot
  be common. It needs no `Context`, which is why it, and not `Settings`, is what the scaffold
  binds — a `Settings` binding would force every app to put a `Context` in the graph before C014
  has decided how secure storage works.
  (9) **detekt is pointed at `src`, not at enumerated source sets**, so C012–C019 add source sets
  without touching the build script. Config is `config/detekt/detekt.yml` with
  `buildUponDefaultConfig = true` and `config.validation` on — it carries only deltas (five), and
  an unknown key fails the build rather than being ignored. It lives at the repo root because
  C067/C076 will share it.
  (10) **ktlint's rules live in the repo-root `.editorconfig`**, which gained
  `ktlint_code_style = intellij_idea`. `android_studio` (ktlint-gradle's `android = true`) would
  have contradicted `kotlin.code.style=official` in `gradle.properties` and quietly moved the line
  limit from the .editorconfig's 120 to 100. detekt's `MaxLineLength` is set to the same 120: three
  tools, one number. `./gradlew :shared:ktlintFormat` fixes what it complains about.
  (11) **The ten tests are wiring proofs, not placeholders.** They assert that the Koin graph
  resolves, that the four `MageRideJson` settings behave, that Turbine + `runTest`'s virtual clock
  work, that `TimeZone.of("Asia/Colombo")` resolves to UTC+5:30 on the target's tz database (D-38
  depends on it), and that the Android actual survives `android.os.Build` returning null for every
  field — which is exactly what it does in a local unit test.
  (12) **`ci.yml`'s android leg now installs `platforms;android-36` + `build-tools;36.0.0`
  explicitly**, as C010's handoff asked. The runner image ships an SDK but its contents rotate and
  AGP 9 will not download a missing platform, so the leg would otherwise be able to go red because
  GitHub refreshed `ubuntu-latest`. Used the preinstalled `sdkmanager` rather than adding a
  third-party action. `actionlint` v1.7.7 clean; no other workflow change was needed — C010's
  build-file probe already runs the wave-1 command the moment `shared/kmp/build.gradle.kts` exists.
  (13) **`.gitignore` re-ignores `/build/reports/`, `/build/tmp/` and `/build/kotlin/`.** C001
  redirects the root project's buildDir away from the build-plan directory, but that override only
  applies once the root project is *evaluated* — a Kotlin-DSL script compilation error earlier than
  that still dropped Gradle 9's problems report into `build/reports/` during this session. Caught
  and deleted; the ignore makes it uncommittable rather than relying on noticing it.
  **Build host —** **the Android SDK is now installed** at `/opt/android-sdk` (489 MB:
  `cmdline-tools;latest` 22.0 — SHA-1 verified against `dl.google.com` — plus `platform-tools`,
  `platforms;android-36`, `build-tools;36.0.0`, all licences accepted). Gradle finds it through an
  untracked `local.properties` (`sdk.dir=/opt/android-sdk`); `ANDROID_HOME` is **not** exported
  system-wide, and the verify was re-run with it unset to prove `local.properties` alone is enough.
  `~/.konan` now holds the ~1 GB Kotlin/Native 2.4.10 distribution. No Docker, no compose stack; the
  replica stayed down. A cold verify takes ~50 s, a warm one ~30 s. **Versions chosen** (all newest
  stable that work with the pinned Kotlin 2.4.10, all appended to `gradle/libs.versions.toml`):
  Ktor 3.5.1, kotlinx.serialization 1.11.0, coroutines 1.11.0, kotlinx-datetime 0.8.0, Koin 4.2.2,
  multiplatform-settings 1.3.0, SQLDelight 2.3.2, Turbine 1.2.1, H3 4.4.0, detekt 1.23.8,
  ktlint-gradle 14.2.0 driving ktlint 1.8.0.

- **Component:** C012 kmp-core-models — 2026-07-27
- **Status:** DONE — `./gradlew :shared:testDebugUnitTest detekt ktlintCheck` green: **132 tests
  passed, 0 failed, 0 skipped**, detekt and ktlint clean. 300 public types across 26 model files
  (~6,150 lines) plus 9 test files (~3,200 lines), covering the 16 app-facing contracts — every
  named schema and every inline request/response
  body of `_shared`, iam, registry, trip-state, ride, dispatch, fare, subscription, wallet, query,
  transit, safety, support, content, voip, notification and version-check (176 operations).
  `./gradlew :shared:compileKotlinIosArm64` also passes, so the DTOs type-check for Kotlin/Native;
  **iOS is not marked DONE from this host** (klib cross-compilation only — no linking, no iosTest).
- **Notes:**
  **Spec gaps — three micro-change-sets, none actioned in `specs/`:**
  (a) ***ADD Appendix A "Position Event Schema (Canonical)" is superseded and unbuildable as
  printed.*** This component's spec anchor points at it, but it prints `ts` as epoch millis,
  `source` as a free string (`"mobile_gps | hardware_gt06 | hardware_st901"`), adds `altitude`,
  `tripSessionId` and `traceId`, and **has no `seq`**. Three later sources agree against it —
  `backend/contracts/realtime/mqtt-topics.md` §2.1 (the machine-checkable contract, C007), D6' §2.2,
  and the landed `telemetry.positions` DDL (C006), whose columns are
  `sample_ts/received_ts/seq/speed_mps/heading_deg/accuracy_m/hdop/sat_count/source SMALLINT CHECK
  (source BETWEEN 0 AND 4)`. Modelling Appendix A would produce a DTO that cannot be written to its
  own sink and that omits `seq` — the replay dedupe key R-17/T-05 exists for. **Landed the later,
  runnable shape**; `PositionSource` is an enum encoded as the `0…4` integer through a small
  `KSerializer` so it survives CBOR as well as JSON. **ADD Appendix A should be replaced with the
  §2.1 payload.** C017 owns the MQTT client and the CBOR codec; C012 owns only the shape.
  (b) ***`trip-state.yaml` and `trips.sessions` disagree on two enums — the contract wins.***
  The contract's `SessionState` is `ACTIVE | ENDED | AUTO_ENDED` while `ck_sessions_state` (C004,
  from server_db_schema §4 / D4' §4) is `ACTIVE | COMPLETED`; the contract's `endReason` is
  `driver_ended | idle_timeout | destination_geofence | mqtt_offline` while
  `ck_sessions_end_reason` is `driver_ended | idle_timeout | geofence | admin`. Modelled per the
  contract (`backend/contracts/CLAUDE.md`: "if a service and a contract disagree, the contract
  wins"), and because the contract's three states are the ones US-5.10 needs — `restartableUntil`
  is only meaningful on an auto-ended session, which `COMPLETED` cannot express. **C031 must not
  discover this at runtime:** either the CHECK gains the contract's values or the contract is
  narrowed, but the client cannot serialise a state the CHECK rejects.
  (c) ***`server_db_schema.md` §19 is still missing the AL-47 payment states.*** Its `payment.state`
  row lists twelve values; the landed `ck_ride_payments_state` (C005) has fourteen —
  `QrClaimedByPassenger` and `DriverConfirmedQR`. `_shared.yaml` already has all fourteen and
  `PaymentState` asserts the set against the CHECK, so nothing is blocked; §19 is simply stale.
  C005's handoff raised the same row for the opposite reason (`PartiallyRefunded`).
  **Other spec observations (no change needed):**
  (d) *`_shared.yaml#/components/schemas/Money` is defined but never `$ref`'d.* Every payload spells
  money flat (`amountMinor` + a sibling `currency`). `Money` is landed as the deliverable requires
  and the flat fields stay flat so they round-trip byte-for-byte; the bridge is a `MoneyHolder`
  interface each such DTO implements, so C015/C016 never touch a bare `Long`.
  (e) *Two contracts declare an identical `VoucherDiscountTier`* (`subscription.yaml`,
  `wallet.yaml`), as C007's handoff records. One Kotlin type serves both; when one route is retired
  nothing here changes.
  (f) *`registry.yaml` spells the same verdict `revenueLicense` on create and `revenue` on the
  onboarding read.* Landed both as written rather than reconciled client-side.
  **Decisions —**
  (1) ***Scope: the fifteen app-facing contracts, not all twenty-one.*** The deliverable is "every
  DTO the **four apps** share", and the four apps are passenger/driver × Android/iOS. `admin-bff`,
  `fleet`, `provisioning`, `public-bff` and `reputation` are Next.js and web surfaces whose DTOs
  belong to the portals' TypeScript client; C013's deliverable list names exactly the fourteen
  services modelled here, and `version-check` was added because the apps poll it at cold start
  (D-31). A file is modelled **in full or not at all** — the admin operations inside an otherwise
  app-facing file (`/v1/admin/fees/rates`, `/v1/admin/dispatch/directional-config`, the GTFS
  Dataset Manager) are landed, so C013 never meets a half-covered contract. **If a portal component
  later wants shared types, that is a new component, not a silent extension of this one.**
  (2) **One package per contract file** (`data.models.{iam, registry, trip, ride, …}`) with
  `_shared.yaml` in the `data.models` root. Recorded in `shared/kmp/CLAUDE.md` so C013–C019 and the
  four app shells inherit the rule rather than re-deriving it.
  (3) **`allOf` is flattened into one data class.** kotlinx.serialization cannot compose a
  `@Serializable` from parts, and an `allOf` *is* one JSON object — `VerifyOtpResponse`,
  `VehicleDetail`, `TripDetail`, `SavedAddress`, `FaqArticle`, `TicketDetail`, `CancelRideResponse`
  and `CompleteRideResponse` are all flat here for that reason, each with an accessor
  (`tokens`, `toTier()`, …) that recomposes the part a caller actually wanted.
  (4) **`oneOf(A, B)` becomes two types, not one all-nullable class.** `POST /v1/admin/auth/login`
  is `PasswordLogin | GoogleAuthCodeLogin`; C013 gives it two overloads. A single class with four
  nullable fields would happily serialise a body the server rejects.
  (5) ***`@EncodeDefault(ALWAYS)` on every required field that also has a default.*** `MageRideJson`
  sets `encodeDefaults = false` (C011 decision 7), so `currency: LKR` and `VehicleRegistration.mode`
  — both `required` **and** `const` in the contract — would otherwise be dropped from the wire.
  This is the one place the module's Json config and the contract pull in opposite directions, and
  it is worth knowing before adding a defaulted field to a request DTO.
  (6) **`ErrorCode` is deliberately NOT `@Serializable`.** No schema carries an `ErrorCode` field —
  it only ever arrives inside `Problem.type`. `ProblemDetails.code` derives the kebab key from the
  URI and `ErrorCode.fromWire` resolves it to `null` when unknown, so a service that registers a new
  code at start-up (`MageRideErrors.Register`, C002) degrades an older build to "unrecognised code"
  instead of a `SerializationException` **on the body that explains the failure**. All 66 codes are
  mirrored and asserted unique. `ProblemDetails` also carries the three D-31 `426` extensions, which
  is the same trio `GET /v1/version/check` returns — one render path for both.
  (7) **Enum naming is mechanical:** upper-camel wire values keep their exact spelling as the Kotlin
  entry (`RideState.CashOnDeliveryCollected`), everything else is `UPPER_SNAKE` with an explicit
  `@SerialName` **plus** a `wire` property for non-serialisation callers (path segments, query
  strings, C018's SQLDelight columns). `EnumWireFormatTest` asserts the two never drift.
  (8) **Timestamps are `kotlin.time.Instant`, business dates `kotlinx.datetime.LocalDate`** —
  both have built-in serializers producing exactly the `2026-07-27T04:15:00Z` / `2026-07-27` forms
  the contracts print, so no custom serializer is involved. Every D-38 `…TzAt` companion is
  modelled beside its date. `Ulid`/`PhoneE164`/`PhoneMasked` are **type aliases, not value classes**:
  a Kotlin `value class` is boxed or erased at the Objective-C boundary, and this module ships as an
  XCFramework.
  (9) **`Page<T>` is the one pagination envelope** for all 18 in-scope `allOf(CursorPage, {items})`
  responses, so C013 writes one helper. `PageRequest` carries the `?cursor=&limit=` pair and the
  1/20/100 bounds. Note the client cannot reproduce the server's forced `"cursor": null`
  (C002 decision 9) — `explicitNulls = false` drops it — which is harmless because clients decode
  pages and never encode them.
  (10) **`RideState.isTerminal` / `PaymentState.isTerminal` and the four `DRIVER_EXCLUSIVE` states
  live on the enums.** These are properties of the value set (ADD Appendix B.2, R-05), not
  transition rules; **C015 still owns every transition** and must not read a state machine into
  these helpers.
  (11) **The DoD's "no user-facing string literals" is enforced, not just reviewed.**
  `ModelSourceHygieneTest` (androidHostTest — the only source set with a filesystem) scans the model
  tree with a character-level Kotlin scanner (a regex cannot: `"https://mageride.lk/errors/"`
  contains `//`) and fails on any Sinhala or Tamil code point, any formatted-money literal, any
  `@SerialName` that is not a machine key, and any multi-word literal outside three named
  `require` messages. Adding a fourth needs a deliberate edit to that list.
  (12) **Two detekt deltas were added to `config/detekt/detekt.yml`**, each named in place:
  `complexity.LongParameterList` is excluded for `**/data/models/**` (a DTO's constructor *is* the
  contract's field list — `RideDetail` has eighteen properties because the schema has eighteen), and
  `style.MagicNumber.ignoreEnums` is on (`NMEA_MQTT(4)` is the wire code of a `SMALLINT CHECK
  (source BETWEEN 0 AND 4)` column; hoisting it to a constant moves the number away from its name).
  (13) **Test shape: three complementary sweeps.** `ContractPayloadTest` (39) decodes JSON written
  from the contracts and asserts the fields a wrong reading would silently drop;
  `DtoRoundTrip*Test` (40) builds every DTO with **every** property populated and asserts
  encode→decode identity; `EnumWireFormatTest` (19) asserts the wire spellings, and asserts
  `RideState` and `PaymentState` against the `ck_rides_state` / `ck_ride_payments_state` value sets
  as sorted lists, so a typo, an omission or a stray extra state fails the build.
  **Build host —** no Docker and no compose stack; the replica stayed down. Gradle + the cached
  Kotlin/Native 2.4.10 distribution only. A warm gate run takes ~10 s, a cold one ~75 s;
  `compileKotlinIosArm64` adds ~40 s. `ktlint`'s `standard:class-signature` rule collapses any
  class signature that fits in 120 columns onto one line — write it however and run
  `./gradlew :shared:ktlintFormat` **twice** (the first pass can leave a now-unnecessary trailing
  comma for the second to remove).

- **Component:** C013 kmp-api-client — 2026-07-27
- **Status:** DONE — `./gradlew :shared:testDebugUnitTest detekt ktlintCheck` green: **223 tests
  passed, 0 failed, 0 skipped** (91 new, C012's 132 still green), detekt and ktlint clean.
  Sixteen typed client interfaces + Ktor implementations covering **all 176 operations** of the
  sixteen app-facing contracts, over one shared request pipeline (~2,000 lines of `data/api`
  + ~1,600 lines of test). `./gradlew :shared:compileKotlinIosArm64` also passes, so the client
  type-checks for Kotlin/Native; **iOS is not marked DONE from this host** (klib cross-compilation
  only — no linking, no `iosTest`).
- **Notes:**
  **Spec/model gap — one micro-change-set, not actioned:**
  (a) ***`ReportFormat` (C012, `data.models.transit`) has lowercase `@SerialName`s but no `wire`
  property***, which `shared/kmp/CLAUDE.md` requires of every enum whose wire spelling is not upper
  camel case, "plus a `wire` property for non-serialisation callers (path segments, query strings)".
  `?format=` on `GET /v1/admin/transit/gtfs/uploads/{id}/report` is exactly such a caller, so
  `TransitApi` uses two private constants rather than reading a value off the enum. One-line fix in
  C012's file; deliberately not made here so the model layer stays C012's.
  **No contract gaps found.** Every operation this client needs exists in `backend/contracts/`, and
  nothing was improvised. The one shape the contracts leave to the client is `?format=`/`Accept`
  double-listing — see decision 11.
  **Decisions —**
  (1) ***All sixteen contracts, not the fourteen the prompt's deliverable list names.*** The list
  omits `trip-state` and `version-check`, but the DoD says *"every contract file has a matching
  typed client covering all its operations"*, C012 modelled sixteen files, and both omissions are
  load-bearing: trip-state owns Mode A/B (the Driver App's other half) and `version-check` is the
  D-31 cold-start gate this component's own deliverable list asks for. Coverage: 176/176
  operations, asserted by `ContractCoverageTest`, not by inspection.
  (2) **Interface + `internal` Ktor implementation per contract file**, in `data.api.{iam, registry,
  trip, ride, dispatch, fare, subscription, wallet, query, transit, safety, support, content, comms,
  version}` — the same package split as C012's models, so `data.api.ride` and `data.models.ride`
  line up and `comms` again carries voip + notification. `MageRideApi` bundles all sixteen (one
  object for Swift); `apiModule` also binds each interface on its own so a view model can ask for
  just `RideApi` and get a fake in a test.
  (3) ***mTLS and webhook operations are covered too, and say so.*** 24 of the 176 are
  `/v1/internal/*` (mTLS) or one of the six HMAC-signed provider callbacks — unreachable from an
  app. Landing them keeps "no half-covered contract file" true and gives C118 a typed caller; each
  carries a KDoc line saying it is not app-reachable. The six callbacks go through `apiPostExempt`,
  which sends **no** `Idempotency-Key` (they dedupe on `provider_transaction_id`, R-19) and is
  therefore never retried.
  (4) ***The `Idempotency-Key` is minted into the request builder before the first send, never per
  attempt.*** This is the mechanism behind the DoD line "retrying a POST reuses the original key":
  Ktor's `HttpSend` replays the same builder, so a transport retry and the post-refresh replay both
  carry the original key and the service replays its recorded response (R-14/R-18). Every POST
  method also takes a trailing `idempotencyKey: String? = null`, so a *user*-driven retry can pass
  the same key rather than issuing a second command. Asserted three ways
  (`RequestConventionsTest`, `AuthRefreshTest`, `RetryAndBackoffTest`).
  (5) ***One `HttpSend` interceptor owns the whole send pipeline*** — attestation → circuit breaker
  → retry/backoff → auth refresh → RFC 7807 mapping — rather than four plugins whose relative
  ordering is implicit. Ordering between independent `HttpSend` interceptors is the kind of thing
  that works until someone installs a fifth; this way the order is a numbered list in one KDoc.
  Written as tail recursion, because "retry after backoff" and "refresh and replay" are two
  different reasons to send again and a loop with a flag per reason reads worse.
  (6) **`ktor-client-auth` stays unapplied** (C011 reserved it for C014). Refresh-on-401 is done in
  the send interceptor instead, which is what makes "same `Idempotency-Key` on the replay" and
  "exactly one refresh, then `onAuthenticationLost()`" expressible at all — `Auth`'s refresh hook
  re-runs the request pipeline. C014 therefore only has to implement `TokenProvider` and
  `AttestationProvider`; both have no-op defaults in `apiModule` so the graph resolves today.
  (7) ***Errors: status picks the type, the kebab code picks the branch.*** `MageRideError` is a
  sealed hierarchy — one subtype per status class, plus `AttestationFailed` split out of `401`
  because D-30's recovery (re-attest) is not `401`'s (sign in again), plus four transport arms
  (`Network`, `Timeout`, `Serialization`, `CircuitOpen`). The DoD pair lands as `Conflict`
  (`offer-already-accepted`) vs `Gone` (`offer-expired`) — different types, both carrying their
  code. **No per-code subtypes**: 66 codes would be 66 classes, and `error.code` in a `when` is
  what the registry is for. An unknown code resolves to `null` and keeps the status (C002 can
  register a code this build predates).
  (8) **A non-problem error body keeps its status** and synthesises a `ProblemDetails` whose code is
  the *existing* kernel code for that status class. A captive portal answering HTML must not turn a
  `502` into a `SerializationException`, and the fallback never invents a code outside
  `_shared.yaml#/components/schemas/ErrorCode`.
  (9) ***`426` is thrown **and** published.*** The version gate runs at the edge on every route, so
  all 176 operations can answer it; handling that per call site is 176 chances to forget. The typed
  error still reaches the caller (its own flow must not continue) and
  `MageRideApiSignals.upgradeRequired` (replay 1) carries the same payload to the app shell.
  `GET /v1/version/check` publishes on the same flow, so the cold-start check and a mid-session
  `426` feed one update screen.
  (10) ***`followRedirects = false` on the client.*** No contract route redirects except
  `GET /v1/admin/transit/gtfs/versions/{id}/download`, whose entire payload *is* the `Location`
  header (a short-lived signed object-storage URL). Ktor 3 has no per-request redirect switch, and
  following it would stream a GTFS zip through the JSON pipeline. Elsewhere a `/v1/*` route
  answering `3xx` is a misconfigured gateway, and surfacing that beats chasing it.
  (11) **Ktor's content negotiation appends `application/json, application/problem+json` to any
  `Accept` a call sets**, so the two statement downloads and the CSV validation report send the
  requested type *first* but not exclusively. Recorded in `WalletApi`'s KDoc; **C118 should pin the
  service's behaviour** rather than the client's, because the client cannot suppress the appended
  header.
  (12) ***`data/repository` gets the cursor-paging abstraction, not sixteen repository interfaces.***
  The typed client interfaces already are the seam the app layer injects, so a second 1:1 layer
  would be pure indirection; C015–C018 own the repositories that carry domain. What all eighteen
  paged reads *do* share is the walk, so `CursorPagedSource` + `asFlow`/`asPageFlow`/`loadAll` lives
  there with a `maxPages` stop — `hasMore = true` with a null cursor would otherwise loop forever.
  (13) **Three response shapes needed special handling**, and only three: `oneOf(Schema, null)` on
  the three "active session / active ride" reads decodes through `decodeOrNull` (bypassing content
  negotiation, because "no active ride" must not depend on how it treats a null body); the ETag'd
  `GET /v1/config/cities` returns `Conditional<T>` so a `304` is a value rather than an error; and
  the `302` download returns the header. Everything else is `Page<T>`, a DTO, `Unit` or bytes.
  (14) ***Two named admin-login functions, not two overloads.*** `POST /v1/admin/auth/login` is
  `oneOf(PasswordLogin, GoogleAuthCodeLogin)` (C012 decision 4). `adminLoginWithPassword` /
  `adminLoginWithGoogle` read better than overloads and avoid the `…request:idempotencyKey_:`
  name-mangling Kotlin/Native would apply at the Objective-C boundary.
  (15) **`ContractCoverageTest` (androidHostTest) enforces the DoD rather than reviewing it.** It
  scans the sixteen YAML documents and the `data/api` sources and asserts: every operation is
  called; the call uses the verb the contract declares (and `apiPostExempt` exactly for the
  `x-idempotency-exempt` six); every operation declaring `X-Attestation` passes `attested = true`
  **and no other operation does**; and the counts are still 176 operations / 20 attested. YAML is
  scanned, not parsed — `androidHostTest` has no YAML library and `spectral` already validates the
  documents.
  (16) **One detekt delta**, in place: `complexity.LongParameterList` now also excludes
  `**/data/api/**`. Same argument C011 wrote for `data/models` — `estimateFare` takes six parameters
  because `GET /v1/fare/estimate` declares six query parameters, and `apiRequest` takes ten because
  that is every cross-cutting convention D3' §0 applies to a request. Everything else detekt raised
  was fixed by refactoring (the pipeline split into `RetryBudget` + a recursive `attempt`,
  `ApiTransport.kt` split into route helpers and `ApiBodies.kt`, the coverage test split into
  `ContractScanner.kt`), not by suppression.
  **Build host —** no Docker and no compose stack; the replica stayed down. Gradle + the cached
  Kotlin/Native 2.4.10 distribution only. A warm gate run takes ~40 s, `compileKotlinIosArm64`
  another ~25 s. Two things worth knowing next time: ktlint rejects a **dangling top-level KDoc**
  (a `/** … */` not attached to a declaration — use `//` for a file header), and detekt's defaults
  are strict about `ReturnCount` (2), `ThrowsCount` (2) and `LoopWithTooManyJumpStatements` (1), so
  a decision-tree function wants to be recursion or several small functions rather than one loop.

- **Component:** C014 kmp-auth-session — 2026-07-27
- **Status:** DONE (common + Android; iOS declared and type-checked, **not** verified) —
  `./gradlew :shared:testDebugUnitTest detekt ktlintCheck` green from a clean build directory with
  `--no-build-cache`: **277 tests passed, 0 failed, 0 skipped** (54 new; C011–C013's 223 still
  green), detekt and ktlint clean. All four DoD items pass, each with a test that fails if the rule
  is broken. `./gradlew :shared:compileKotlinIosArm64` also passes, so `src/iosMain` type-checks for
  Kotlin/Native; **iOS is not marked DONE from this host** (klib cross-compilation only — no
  linking, no `iosTest`). ~1,100 lines of `domain/auth` + `platform` and ~1,500 lines of test.
- **Notes:**
  **Spec gaps — three micro-change-sets, none actioned in `specs/`:**
  (a) ***No spec defines the `X-Attestation` wire format, and C008 already had to invent one.***
  D3' §0 and `_shared.yaml` declare the header and its `maxLength: 8192` and stop there.
  `backend/src/ApiGateway/Attestation/AppAttestOptions.cs` records the same gap and defines the
  format this client now produces: **Android sends the Play Integrity token unwrapped; iOS sends
  `base64url(keyId) "." base64url(assertion)`**, the assertion signed over
  `SHA-256("<METHOD> <path>")` (`AppAttestVerifier.ClientData`). Two ends of the platform have now
  independently implemented an undocumented contract — **D3' §0 should state it**, and C118 should
  pin it.
  (b) ***There is no App Attest registration endpoint, so the iOS half of D-30 cannot complete.***
  The gateway verifies an assertion against a public key it reads through `IAttestedKeyStore`, fed
  from `iam.devices.attestation_verified_at` — a column **no `iam.yaml` operation writes**. Apple's
  flow is `generateKey` → `attestKey(challenge)` → *send the attestation object to the relying
  party* → assertions thereafter; the middle step has nowhere to go, and there is no
  challenge/nonce route to bind it to either. Landed the client side in full and exposed
  `PlatformAttestationProvider.prepareRegistration(challenge)` returning an `AppAttestRegistration`
  so C026/C095 can post it the day the route exists. **`iam.yaml` needs
  `POST /v1/auth/attestation/challenge` + `POST /v1/auth/attestation/register`.** Until then iOS
  attestation answers `app-attest-unknown-key` at the edge — which is why `Gateway:Attestation:Mode`
  has an `Audit` setting (C008).
  (c) ***`403 device-revoked` is not in the error registry.*** `mobile_db_schema.md` §0.4 names it
  as the AL-08 displacement signal ("On logout / `403 device-revoked` (AL-08) / PDPA erasure: wipe
  the whole DB file and Keystore entries"), but it appears in no other document and is absent from
  `_shared.yaml#/components/schemas/ErrorCode` and from `MageRideErrors` (C002). The client matches
  it on the **wire spelling** so it works whichever way this is resolved, and falls back to plain
  `SESSION_REVOKED`. **Either register the code or drop the §0.4 reference** — C026 owns the
  producing side.
  **Other spec observations (no change needed):**
  (d) *`iam.yaml` gives `POST /v1/auth/otp/verify` a `403` response with no `x-error-code`.* The
  client treats `403` on a refresh as terminal and `403` on verify as an ordinary failure; if
  iam-svc means "blocked number" there, note that `isBlocked` already comes back on the `200` of
  `/v1/auth/otp/request`, which is what the login screen reads.
  (e) *D5' §14.2's "single active device PER APP" is a **server** rule.* The client's half is that
  the store is namespaced by `app` and a verify for a different `userId` wipes what the previous
  one left, including the MQTT token. The client cannot detect displacement on its own — it learns
  about it when a call is refused.
  **Decisions —**
  (1) ***`TokenProvider.refresh()` gained a `staleAccessToken` parameter (a C013 API change).***
  Without it, "collapse concurrent refreshes" is not expressible: a mutex alone lets a caller that
  acquires the lock *after* a rotation rotate the token that rotation just produced, and D-29
  punishes a re-presented refresh token by revoking the whole session family. Keying the collapse
  on the token that actually failed makes it exact — the pipeline now returns the token it attached
  from `attachCredential` and hands it to `refresh`. `five_concurrent_401s_produce_one_rotation`
  fails against the previous signature.
  (2) ***`AttestationProvider.attestationToken` now takes an `AttestationRequest` (method + path),
  not an `operationId`.*** The gateway binds an App Attest assertion to `SHA-256("<METHOD> <path>")`,
  so a provider holding only an `operationId` cannot produce a header that verifies. Same value
  feeds Play Integrity's `requestHash`, which the gateway does not check today but C128 can turn on
  without an app release.
  (3) **Offline is not revoked.** `onAuthenticationLost` ends the session for a *refused* credential
  (`401`, `403`, `400`, `404` on the refresh, or a `401` that survives a successful rotation) and
  does **nothing** for a network failure, a `5xx`, a timeout or an open breaker — the caller still
  gets its own `401`. C013's contract for that callback is "the session is unrecoverable", and the
  literal reading would sign a driver out of a live ride every time they drove through a tunnel.
  (4) **No token is reachable above this layer.** `SessionState.SignedIn` carries a user id, an app
  surface, a device id and `isNewUser` — deliberately not a token — and `AuthSession` is `internal`.
  The one door out is `SessionTokenProvider`, which only the HTTP pipeline holds.
  (5) **Proactive refresh, with a cooldown.** ADD §12.1 asks for it; the cooldown exists because
  `accessToken()` runs on *every attempt of every request*, so a handset with no network would
  otherwise drive one refresh round trip per call forever.
  (6) ***`SecureStore` and `PlatformAttestationProvider` are `expect class`, not `expect fun`.***
  Their constructors genuinely differ — Android needs a `Context`, iOS a Keychain service name —
  which a function cannot express, and common code never constructs one (the app does, as with
  C013's `HttpClientEngine`). Needed `-Xexpect-actual-classes`; the flag and the reason are in
  `build.gradle.kts`.
  (7) **Android: AES-256-GCM under a non-exportable Keystore key, ciphertext in a `MODE_PRIVATE`
  preferences file — and `setUserAuthenticationRequired` is deliberately OFF.** A driver's handset
  is locked in a mount for most of a ride and the E-02 renewal loop must read its credential then;
  requiring an unlock would make the one token designed to survive a long trip the one that cannot
  be renewed during it. `commit()`, not `apply()`: the rotated refresh token is persisted *before*
  the in-memory copy moves, and a queued `apply()` lost to a process death is a forced sign-out.
  (8) **iOS: Keychain items, no app-level crypto.** `kSecAttrAccessibleAfterFirstUnlockThisDeviceOnly`
  — `ThisDeviceOnly` keeps the session out of iCloud Keychain and every backup (a restored backup
  must not resume a session on another handset), `AfterFirstUnlock` for the same locked-in-a-mount
  reason as (7). ADD §12.1's "Keychain + Secure Enclave" is satisfied by the data-protection class
  key being wrapped by the SE; adding an app-level AES layer would introduce a key the app then has
  to protect, which is the problem the Keychain exists to solve.
  (9) ***`multiplatform-settings` stays unapplied***, despite C011 reserving it for this component:
  both of its app-side backends are plain settings (`SharedPreferencesSettings`,
  `NSUserDefaultsSettings`), which is exactly what this DoD forbids. Its Apple `KeychainSettings`
  would have served, but half a store from a library and half hand-rolled is worse than one
  hand-rolled pair. The catalog comment now says so; the entry is free for a later component that
  needs *non-secret* KV storage.
  (10) **The device id survives a logout; only PDPA erasure takes it.** `iam.yaml` calls it a
  *per-install* identifier and AL-08's "new device" test is meant to fire when the handset changes,
  not when a user signs out and back in. Same for the iOS App Attest key id — regenerating it would
  need re-registration and would look exactly like a cloned device. `mobile_db_schema.md` §0.4's
  "wipe … Keystore entries" is read as the tokens.
  (11) **The MQTT renewal loop never drops the token it holds.** Every renewal error — offline,
  `5xx`, even `401` — is retried with backoff while the current token stays publishable; only
  `release()` or the session actually ending clears it. `MqttSessionTokenManager.token` is a
  `StateFlow` because EMQX validates the JWT at CONNECT, so **C017's client has to reconnect when it
  rotates** — a renewed token does not apply to a live connection.
  (12) **A `Koin` cycle is broken by a deferred lookup, not by a second graph.** `IamApi` →
  `HttpClient` → `TokenProvider` → `AuthSessionManager` → `IamApi`. The manager takes `() -> IamApi`
  and resolves it at first use; `authModule` is appended after `apiModule` in `sharedModules` so its
  `TokenProvider` overrides C013's `Anonymous` placeholder with no edit in any app. `AuthGraphTest`
  asserts that ordering, because if it ever flips every request in all four apps goes out
  unauthenticated.
  (13) **An unreadable stored record is dropped, not re-read.** A session written by an older build
  whose shape has changed would otherwise throw on every cold start until the user reinstalls; the
  recovery from "no session" is a login screen they already know how to use.
  (14) **Test shape.** The refresh and revocation suites run the **real** `SessionTokenProvider`
  inside the **real** C013 send pipeline over a `MockEngine` — the DoD is about how those interact,
  and a fake pipeline would assert the fake. The E-02 renewal suite stubs `issueMqttToken` only
  (delegating the rest of `IamApi`): renewal is pure timing, Ktor runs a request on its own
  dispatcher off the virtual scheduler, and a loop making real calls would advance the two clocks
  independently. `AndroidSecureStoreTest` proves no plaintext reaches the preferences sink — and
  the stronger guarantee is structural, since `KeyValueSink` accepts only a `SealedValue`.
  `PlatformSecurityHygieneTest` covers what this host cannot run: that the iOS store uses the
  Keychain with a `ThisDeviceOnly` class and never `NSUserDefaults`, and that nothing in
  `domain/auth` calls a portal sign-in (AL-07).
  **For later components —**
  **C017:** subscribe to `MqttSessionTokenManager.token` and reconnect on change; call
  `bind(vehicleId, rideId)` when a ride starts and `release()` when it ends. Do **not** send the API
  access token to EMQX.
  **C018:** `mobile_db_schema.md` §1.1 `auth_session` is yours, but the tokens are not — store the
  expiry timestamps and the `jti` only, and wipe the DB file on `SessionEvent.RouteToLogin`.
  **C067 / C076:** bind four things — `HttpClientEngine`, `ApiConfig`, `AuthConfig(app = …)` and
  `PlatformSecureStore(context, namespace)` — plus `PlatformAttestationProvider(context,
  cloudProjectNumber)` and call its `warmUp()` at start-up; without the warm-up the first sensitive
  mutation of the session pays the whole Play Integrity preparation cost, and the first sensitive
  mutation is `POST /v1/auth/otp/request`. Subscribe to `AuthSessionManager.events` once in the app
  shell.
  **C085 / C094:** the same, with `PlatformSecureStore(service)` and
  `PlatformAttestationProvider(secureStore)`. **Everything in `src/iosMain` is compile-checked only**
  — the Keychain and DeviceCheck calls have never executed. Budget time to verify them on a device
  (the simulator does not support App Attest at all).
  **C026:** honour the two gaps above, keep `deviceId` and the `app` claim as the AL-08 key, and note
  that the client presents the opaque refresh token **both** as the bearer credential and in the
  body on `POST /v1/auth/refresh`, which is what `iam.yaml`'s `refreshToken` security scheme asks
  for.
  **Build host —** no Docker and no compose stack; the replica stayed down. Gradle, the Android SDK
  and the cached Kotlin/Native 2.4.10 distribution only. One new coordinate,
  `com.google.android.play:integrity:1.6.0` (androidMain, `implementation`); it brings
  `play-services-tasks` at `compile` scope, which is where the `Task` the API answers with comes
  from. A clean gate run takes ~35 s and `compileKotlinIosArm64` another ~40 s cold. Two things
  worth knowing next time: **Kotlin block comments nest**, so a KDoc containing `contracts/*.yaml`
  opens a second comment and the file stops parsing (the error surfaces on a later declaration);
  and `CFBridgingRetain` / `CFBridgingRelease` live in `platform.Foundation`, not
  `platform.CoreFoundation`.

- **Component:** C015 kmp-domain-ride-dispatch — 2026-07-27
- **Status:** DONE (common + Android; iOS declared and type-checked, **not** verified) —
  `./gradlew :shared:testDebugUnitTest detekt ktlintCheck` green from a clean build directory with
  `--no-build-cache`: **384 tests passed, 0 failed, 0 skipped** (107 new; C011–C014's 277 still
  green), detekt and ktlint clean. All four DoD items pass, each with a test that fails if the rule
  is broken. `./gradlew :shared:compileKotlinIosArm64 --rerun-tasks` also passes, so the new
  `commonMain` type-checks for Kotlin/Native; **iOS is not marked DONE from this host**
  (klib cross-compilation only — no linking, no `iosTest`). ~2,400 lines of `domain/ride` +
  `domain/dispatch` and ~1,800 lines of test. Nothing in `androidMain` or `iosMain` changed:
  Mode C's client-side logic is entirely platform-independent.
- **Notes:**
  (1) **Package layout — `domain/ride`, not `domain/trip`.** ADD §18.2's KMP tree says
  `domain/trip/ — Trip state machine logic`; the manifest's C015 deliverable says `domain/ride`.
  ADD §18.2 predates **R-01**, which split the Mode C ride aggregate out of the Mode A/B tracking
  session; under the current vocabulary `domain/trip` names trip-state-svc's aggregate, which this
  module does not contain and never will. Took the manifest. `shared/kmp/CLAUDE.md` §Source-set
  layout updated. **Micro-change-set:** ADD §18.2's tree should read `domain/ride/`.
  (2) **`RideTransitions` is Appendix B.2 as data, and it is the only place a transition exists.**
  `next()` is a map lookup; there is no branch anywhere that can produce a state the table does not
  list. `RideTransitionTableTest` re-declares the appendix independently from the diagram and the
  D5' §6/§7 prose and asserts set equality, then sweeps **all 18 × 20 = 360 state/trigger pairs**
  to prove every pair outside the table moves nothing. That is the DoD's property, and enumerating
  the whole input space is a stronger statement than sampling it — and faster.
  (3) **Two edges are in the table and not in the Appendix B.2 diagram.** Both are carried by other
  parts of the same spec, both are commented at the declaration, and both are listed separately in
  the test so the claim stays auditable. **(a)** `Matching → Accepted`: D5' §6.1's conditional
  UPDATE guards on `state IN ('Matching','Offered')`, because the 15 s TTL can bounce the ride back
  to `Matching` while the winning accept is in flight. Dropping it would make a driver's own
  successful accept look like a state the client does not understand. **(b)**
  `Accepted|DriverArrived → NoShowDriver`: the D5' §7 matrix has an explicit row for it. **This
  contradicts C012's `RideState.NoShowDriver` KDoc**, which says "D5' §7 models driver-side no-show
  as `CancelledByDriver` and no transition currently writes it" — §7 has both rows. C012's file was
  left alone; the note is what is wrong, not the enum. **Micro-change-set:** correct that KDoc, and
  add the two edges to the Appendix B.2 diagram so the picture and the prose agree.
  (4) **Spec gap — the rider cannot cancel from `DriverArrived`.** D5' §7 has rows for a rider
  cancel from `Accepted` (Rs 50) and from `InProgress` (full fare), and Appendix B.2 draws both.
  Neither has a row for the state in between — the driver waiting at the kerb, which is exactly
  where a rider is most likely to change their mind. Modelled conservatively: the edge is **not**
  in the table, so `CancellationMatrix.costOfRiderCancelling(DriverArrived)` is `null` and a real
  server-side cancel from there surfaces as an applied-but-unknown transition rather than being
  dropped. **Micro-change-set:** D5' §7 needs the row, with its penalty.
  (5) **Spec conflict — `LevelConfig` is points-valued, D5' §4.2 is level-valued.** The contract's
  `LevelConfig` offers `noShowPenaltyPoints` and `cancellationPenaltyPoints`; D5' §4.2 describes a
  no-show and three passenger reports as `level -= 1`, not as point deductions, and gives no rule
  the two knobs could implement. Implemented D5' (the business-logic spec wins), and
  `DriverLevelRules.D5_DEFAULTS` leaves both `null` with the reasoning at the declaration.
  **Micro-change-set:** either give the two knobs a rule in D5' §4.2 or drop them from
  `dispatch.yaml#/components/schemas/LevelConfig`.
  (6) **Gap — `offer.created` carries no `version`, and the accept requires one.** D6' §2.2's
  `dispatch.events` envelope has `offerId`, `rideId`, `driverId`, `expiresAt`, the P-05/P-06/DT-08
  fields and the fare — but no ride `version`, which `AcceptRideOfferRequest` needs (R-14).
  `OfferSession.accept()` therefore reads `GET /v1/rides/{rideId}/state` once, and only when the
  envelope did not carry one; `onVersionKnown(…)` lets a caller that already has it skip the read.
  Not raised as a change-set — adding `version` to the envelope would be a genuine improvement but
  the read is cheap and inside the 15 s. Flagged for C022/C023 if they revisit the envelope.
  (7) **The client never advances a ride.** `RideProjection` moves only through `onServerState(…)`;
  there is deliberately no `apply(trigger)`. A server-confirmed move the table does not draw is
  **applied and flagged** (`RideUpdate.Applied.isKnownEdge == false`), never refused — ride-svc is
  the sole writer (R-01), and a client that dropped a transition it had not been taught would show
  a passenger a ride that had already been cancelled. `verdict(command)` is the other direction: a
  local guess that saves a round trip, explicitly not a claim about what the server would allow.
  `RideUpdate.Applied.trigger` is `null` both when the table draws no edge *and* when it draws more
  than one — `Accepted → CancelledByDriver` is both a driver cancel and an expired grace, and a
  bare `RideStateChanged` frame does not say which.
  (8) **R-14 in one place.** Stale and duplicate snapshots are dropped by version, because SignalR,
  FCM and the reconnect poll all describe the same ride and none of them promises ordering. A 2,000
  frame seeded fuzz asserts the version is monotonic and that an applied frame always leaves the
  projection saying exactly what the server said.
  (9) **Everything server-tunable is read, not baked.** `DirectionalPredicate` takes
  `DirectionalConfig` and `DriverLevelRules` takes `LevelConfig`; `D5_DEFAULTS` is a fallback for a
  client that has not read the admin config, and a test proves both the θ_max and the Job Board
  level floor move with it. Genuinely fixed numbers — the 15 s TTL, 5 OTP attempts, Rs 50 / Rs 100,
  the four R-16 grace windows, the 30-minute Job Board lead — are named constants citing the line
  that fixes them.
  (10) **AL-16 is a mirror, not a ledger.** `PassengerStanding` projects the Rs 50 debt and the
  three-consecutive counter so a passenger can be warned *before* the tap; `serverBookingDisabled`
  wins in both directions when reputation-svc has answered, because re-enablement needs the balance
  cleared **and** a cooldown or a CSR reinstatement (§7.2) and no device can work that out. A
  completed ride clears the whole balance, not Rs 50 of it — §7.1 loops over every OUTSTANDING
  penalty.
  (11) **Directional Travel touches nothing in `domain/ride`** (ADD Appendix B.2 invariant 7: it is
  a dispatch-svc candidate filter, and the aggregate is unchanged whether or not a driver had one).
  The client-side predicate is **advisory** — it exists so the driver app can explain a filter and
  so DT-02/DT-05 are testable against the spec; it can never add a candidate or relax a gate. Its
  haversine and bearing are deliberately local rather than in `domain/geo`: that package is C017's
  and its distance work is JNI-backed on Android, and two textbook formulae on a sphere should not
  tie this rule to a platform. Server-side is PostGIS geography (an ellipsoid); the two agree to
  well under a metre at the 2 km / 250 m thresholds in play.
  (12) **`RideOffer` is `@Serializable` and models D6' §2.2's `offer.created`.** C012 modelled the
  16 REST contracts and deliberately not the event envelopes, but an offer is the one event an app
  receives as a *domain object* rather than as a nudge to re-read. Left here rather than pushed
  back into `data/models` so the event-shape/contract-shape boundary C012 drew stays where it is.
  **For C017:** parse the FCM `RIDE_OFFER` / MQTT payload straight into it.
  **For later components —**
  **C017:** `OfferSession.onOfferPushed(…)` is the entry point for both transports; call
  `onExpired()` when the local countdown or an `offer.expired` frame lands, and feed every
  `RideStateChanged` frame to `RideProjection.onServerState(…)`. `RideGrace` is the client's read of
  the LWT windows your MQTT client's disconnect handling drives.
  **C022 / C023 (ride-svc, dispatch-svc):** `RideTransitions.EDGES` is the client's copy of your
  state machine and `CancellationMatrix.ROWS` of your §7 matrix. If a server transition is not in
  the table the client applies it and flags it — so a new edge is a two-file change, not a silent
  divergence. Note (3) and (4) are yours to resolve first.
  **C067 / C076 / C085 / C094:** bind nothing new. `rideDispatchModule` is already in
  `sharedModules` and its one binding, `OfferSession`, resolves out of C013's `HttpClientEngine` +
  `ApiConfig`. Build `RideProjection` per ride (`RideProjection.of(rideDetail)`), and construct
  `DirectionalPredicate` / `DriverLevelRules` from the config you just read rather than caching one.
  **C019 (test kit):** `RideTransitions` and `CancellationMatrix` are pure data and make good
  generators — a fixture that walks the table produces only reachable rides by construction.
  **Build host —** no Docker and no compose stack; the replica stayed down. Gradle, the Android SDK
  and the cached Kotlin/Native 2.4.10 distribution only. No new dependency and no build-script
  change: C015 is pure Kotlin over what C011–C013 already brought in. A clean gate run takes ~41 s
  and `compileKotlinIosArm64` another ~13 s. Two things worth knowing next time: an `object`'s
  property is **not** covered by detekt's `ignoreCompanionObjectPropertyDeclaration`, so a
  `Money.ofMinor(5_000)` in a plain `object` needs a named `const` behind it; and detekt's
  `LongParameterList` fires at **six** parameters, not above six, which bites test builders long
  before it bites production code.

- **Component:** C016 kmp-domain-fare-wallet — 2026-07-27
- **Status:** DONE (common + Android; iOS declared and type-checked, **not** verified) —
  `./gradlew :shared:testDebugUnitTest detekt ktlintCheck` green from a clean build directory with
  `--no-build-cache --rerun-tasks`: **543 tests passed, 0 failed, 0 skipped** (159 new; C011–C015's
  384 still green), detekt and ktlint clean. All four DoD items pass, each with a test that fails if
  the rule is broken. `./gradlew :shared:compileKotlinIosArm64 --rerun-tasks` also passes, so the new
  `commonMain` type-checks for Kotlin/Native; **iOS is not marked DONE from this host** (klib
  cross-compilation only — no linking, no `iosTest`). ~2,100 lines of `domain/fare` +
  `domain/wallet` + `domain/subscription` + `util` and ~1,900 lines of test. Nothing in `androidMain`
  or `iosMain` changed: the money rules are entirely platform-independent.
- **Notes:**
  **Spec gaps — six micro-change-sets, none actioned in `specs/`:**
  (a) ***BR-23.9's join-anniversary formula contradicts its own worked example.*** The sentence reads
  "`next_due = join_date + 1 month` computed in Asia/Colombo (joined **5 Jun → next due 6 Jul**)" —
  the formula gives 5 July, the example 6 July. Every other source repeats the **example**: ADD §9.1,
  D4' §18b, `server_db_schema.md` §18b, URD US-23.8 and the functional walkthrough all print
  "5 Jun → 6 Jul". Five statements to one, so `ModeBBilling.firstDueDate` implements the example (the
  paid month covers the join day through the same day next month inclusive, so the next payment falls
  due the day after). **BR-23.9's shorthand should read `join_date + 1 month + 1 day`, or the example
  should change** — but the example is what six documents agree on.
  (b) ***The peak/night surcharge percentage has two sources that can diverge.*** `fares.tariffs`
  carries `peak_surcharge_pct` / `night_surcharge_pct` **and** `fares.peak_windows` carries
  `multiplier_pct`, both seeded 20/15 (§20). D5' §1.1's formula reads the **tariff**
  (`peakPct = isPeak(rideTime) ? tariff.peak_surcharge_pct : 0`), so the window's own column is dead
  weight that an admin can edit into disagreement with the fare engine. `SurchargeWindow` deliberately
  does not model it. **Either drop `fares.peak_windows.multiplier_pct` or say in §1.1 which wins.**
  (c) ***R-05 and D5' §8.1 name payment terminals that are not `PaymentState` values.*** Both say the
  earning posts on "`Paid` / `CashSettled` / `CashOnDeliveryCollected`". `Paid` and `CashSettled` are
  **`RideState`** values; the payment-side spellings are `Succeeded` and `FellBackToCash`
  (`ck_ride_payments_state`, C005). Only `CashOnDeliveryCollected` exists in both enums, which is
  what makes the sentence read as if it were one vocabulary. `PaymentTransitions.settlementTrigger` /
  `settledRideState` is the mapping. **R-05's row and §8.1's bullet should name the payment states.**
  (d) ***No spec says which ride state a `DriverConfirmedQR` payment settles into.*** AL-47 says the
  earning posts and that it "settles like cash"; the ride machine's only cash terminal is
  `CashSettled` via `CASH_SETTLED`. Implemented on that basis and flagged at the declaration.
  **AL-47 / ADD Appendix B.2 should state it** — C032/C050 will otherwise each guess.
  (e) ***P-04 does not route `scan_driver_qr`.*** "Cash ⇒ rider pays driver; LankaQR/OnePay ⇒ booker
  charged" predates AL-22/AL-47, which added a fifth settlement method. Modelled as
  **`PayerRole.RIDER`**, on P-04's own reasoning for cash: the payer has to be standing in front of
  the driver's QR to scan it. **P-04 needs the row.**
  (f) ***`SubscriptionPayment` has no `paymentLink` field, so a LankaQR deep link has nowhere of its
  own to ride.*** `fare.yaml`'s `LankaqrInitiation` has both `paymentLink` and `qrPayload`;
  `subscription.yaml`'s payment has `redirectUrl` and `qrPayload` only. The deep link is therefore
  read out of `redirectUrl` and `qrPayload` stays the AL-15 fallback. **`subscription.yaml` should
  gain `paymentLink`**, or BR-23.10 should say the deep link is the redirect.
  **Other spec observations (no change needed):**
  (g) *D5' §1.3's rounding sentence is garbled but unambiguous once parsed.* "round() = banker's
  rounding to nearest minor unit **at each additive step is avoided** — compute in minor units,
  single round only where a `*pct/100` product is taken" reads as three rules: half-to-even; never
  round an addition; one round per product. §1.1 also rounds `extraKm * perKmMinor`, which is a
  second product, not a second rule — a distance is the only genuinely fractional input a fare has.
  (h) *Neither spec pins the peak/night window boundary.* `[start, end)` (half-open) is the only
  reading under which two adjacent windows cannot both claim the same instant; 07:00 is peak and
  09:00 is not. `SurchargeWindowsTest` states it.
  (i) *§9.4 says "walletBalance < Rs 200" without saying whether that is the raw or the spendable
  balance.* Used **`availableMinor`** (balance net of D-05 penalty debt) everywhere, since that is
  what C012's `Wallet` KDoc says the daily-fee gate checks and it is the figure a driver can act on.
  **Decisions —**
  (1) **`domain/subscription` is a new package, alongside `domain/fare` and `domain/wallet`.**
  `shared/kmp/CLAUDE.md`'s layout named only two for C016, but Mode B subscription billing is neither
  a Mode C fare nor the driver wallet: it is **pass-through money to the fleet owner** that must never
  reach `billing.journal_entries` (§18b, C005). Putting it in `domain/wallet` would have placed it
  next to the ledger types it is forbidden to use. The layout table is updated.
  (2) **`util/BusinessCalendar` is the first thing in `util/`** and is the client mirror of
  MageRide.Shared's `BusinessCalendar` (C002). Every function takes the zone explicitly and defaults
  it to Asia/Colombo, so a test states the rule rather than depending on the host clock — and a
  second operating timezone would be a parameter, not a rewrite. `plusMonths` clamps at month end
  (31 Jan + 1 month = 28 Feb), which is what a monthly billing anchor needs.
  (3) **Banker's rounding is implemented explicitly rather than delegated to `kotlin.math.round`.**
  It is documented as ties-to-even, but the rule is the *definition* of §1.3 and a stdlib doc change
  would silently move every fare. Percentages never touch a `Double`: `20_000 * 15 / 100` has one
  right answer and `2e4 * 0.15` is not guaranteed to be it, and a one-cent disagreement with the
  ledger is a reconciliation ticket rather than a curiosity.
  (4) **`FareCalculator.of(serverResponse)` rebuilds the base from the breakdown's own
  `firstKmMinor`/`perKmMinor`/`distanceKm` and leaves the remainder as the surcharge** — it never
  recomputes the total. The `fareEstimateToken` binds the server's figure, so a client that rendered
  a different one would be showing a price the passenger is not about to be charged; if a server ever
  rounded differently, the discrepancy lands visibly in the surcharge line instead.
  (5) **AL-19 is enforced by the shape of `ModeCTier`, not by a rule in a screen.** The type has no
  ETA and no distance property, so a pre-match tier board cannot render one; `ModeCTiers.priceOnly`
  is the projection that drops `TransportOption.etaSeconds` on the way in. `arrivalVisible` reads
  `RideState.isDriverAssigned`, which excludes `Offered` — a reserved driver has not accepted and may
  still decline, so "3 minutes away" would promise a vehicle that is free to walk.
  (6) **Four payment edges are in the table and not in §8.1's mermaid**, each carried by other prose,
  each commented at its declaration and each listed separately in `PaymentTransitionTableTest`:
  `Initiated → FellBackToCash` (cash is the *default* method and has no gateway leg to fail first),
  `Initiated → CashOnDelivery` (§8.3, same reason), `Succeeded → Refunded | PartiallyRefunded | Disputed`
  (§8.2's admin reversal — the diagram only draws `Overpaid → Refunded`), and
  `CashOnDelivery → Disputed` (§8.3's 24 h `cod_uncollected` timer, P-14). The sweep covers all
  14 × 14 = 196 state/trigger pairs, so an undeclared edge fails the build.
  (7) **`Retried` has no outgoing edge, deliberately.** US-8.15's retry is a *new row* chained by
  `retry_of_payment_id`; the machine continues on the successor. A test asserts it.
  (8) **`PaymentProjection` cannot drop a stale frame by version, because `PaymentStatus` has none.**
  `fares.ride_payments` has no optimistic-concurrency column — it is driven by gateway callbacks that
  dedupe on `provider_transaction_id` (R-19), not by client mutations needing R-14. The available
  ordering rule is "a terminal payment is never walked back", which is exactly the case that matters:
  an in-flight poll answering after the settling push must not un-settle the ride.
  (9) **The +5-minute AL-47 nudge is measured from the first frame that reported the claim.**
  `PaymentStatus` carries no claim timestamp, so `onServerState(status, observedAt)` stamps one and
  keeps it until the payment leaves `QrClaimedByPassenger` — a poll four minutes later must not
  restart the countdown. notification-svc runs the authoritative timer off its own clock; this drives
  only what the two apps display. **`fare.yaml`'s `PaymentStatus` gaining a `claimedAt` would let
  both sides agree** — not raised as a change-set because the client's copy is display-only.
  (10) **`fareWalletModule` binds nothing, and that is the decision.** C015 bound one object because
  a driver's offer slot is genuinely stateful; C016 has no equivalent. *Every* input here is
  admin-tunable and server-supplied — tariffs (versioned by `effective_from`), peak windows, the
  seven fee tiers, the voucher ladder, the low-balance threshold — so a binding would pin the
  launch-time numbers, which is C015's `DirectionalPredicate` warning applied to money. The two
  stateful projections are per-ride (`PaymentProjection`) and per-screen (`WalletHistory`); a
  singleton of either would be a bug. The module is still registered in `sharedModules` so no app
  needs an edit when a later component gives it something to bind.
  (11) **The AL-05 fence is checked against the source, not just the enum.** `MoneyDomainHygieneTest`
  (androidHostTest — the only source set with a filesystem) fails the build if `bankTransfer`,
  `BANK_TRANSFER`, `topup/bank` or friends appear anywhere in `domain/fare`, `domain/wallet` or
  `domain/subscription`, with comments stripped first so the files can keep *documenting* why bank
  transfer is absent. It carries three more structural fences: `ModeCTier` has no ETA/distance
  property (AL-19), `CreditTransfer.kt` performs no percentage arithmetic at all (AL-01), and nothing
  under `domain/subscription` constructs a `LedgerEntry` (AL-24/§18b). A counter-test asserts Mode B's
  legitimate `ONLINE_TRANSFER` method still exists, so the AL-05 check cannot pass by deletion.
  (12) **Ledger idempotency keys are composed from the business fact, per §0.** `daily_fee:driver:
  vehicle:date` is the spelling C005 pinned in a column comment and **C047 must use it verbatim** —
  `billing.daily_fee_charges` has no `journal_entry_id`, so that key is the only link between the
  charge row and its entry. `driver_transfer:{transferId}`, `topup:{topupId}` and
  `voucher_purchase:{purchaseId}` follow the same pattern.
  **For later components —**
  **C022 / C032 / C049 / C050 (ride-svc, fare-svc):** `PaymentTransitions.EDGES` is the client's copy
  of your payment machine and `settlementTrigger` of your `payment-settled` mapping. A server
  transition outside the table is applied and flagged, so a new edge is a two-file change rather than
  a silent divergence. Notes (c), (d) and (e) are yours to resolve first.
  **C046 / C047 / C048 (wallet-svc, subscription-svc):** `DailyFeeRules.decide` is §2.2 in Kotlin and
  `DailyFeeRules.idempotencyKey` is the key you must write. `CreditTransferRules.entryFor` is the
  two-posting shape AL-01 requires; `ModeBBilling.firstDueDate` implements note (a)'s reading.
  **C073 / C080 / C082 / C091 / C098 / C100 (the money screens):** bind nothing new. Build
  `FareCalculator`, `DailyFeeSchedule` and `VoucherCatalogue` from the config you have just read —
  never cache one. Render `FareQuote.total` and nothing else (US-8.4). `PaymentMethods.actionFor` is
  what the pay sheet switches on, and there is deliberately no action that renders a MageRide QR.
  **C019 (test kit):** `PaymentTransitions`, `TariffTable.D5_DEFAULTS` and
  `DailyFeeSchedule.D5_DEFAULTS` are pure data and make good generators.
  **Build host —** no Docker and no compose stack; the replica stayed down. Gradle, the Android SDK
  and the cached Kotlin/Native 2.4.10 distribution only. No new dependency and no build-script change.
  A clean gate run takes ~47 s and `compileKotlinIosArm64` another ~18 s. Two things worth knowing
  next time: **a KDoc containing `topup/*` breaks the file** — Kotlin block comments nest, so the
  glob opens a second comment and parsing dies several declarations later (the hazard
  `shared/kmp/CLAUDE.md` already warns about, hit here for real); and **Koin's `Module.mappings` is
  `@KoinInternalApi`**, so "this module binds nothing" cannot be asserted directly — the graph test
  asserts membership in `sharedModules` and constructibility without the graph instead.

- **Component:** C017 kmp-geo-realtime — 2026-07-27
- **Status:** DONE — `./gradlew :shared:testDebugUnitTest detekt ktlintCheck` → **654 tests passed,
  0 failed** (111 new), detekt and ktlint clean, and `:shared:compileKotlinIosArm64` type-checks
  `src/iosMain`. All four DoD items pass: the 19-cell res-7 + ring(2) set is asserted against real
  H3 output for Colombo Fort, the cadence engine is swept over all eight D5' §5.2 phases, replay
  carries a strictly monotonic `seq` with local duplicate drop, and group churn is suppressed for
  30 s after a boundary crossing.
- **Notes:**
  **Spec gaps — three micro-change-sets, none actioned in `specs/`:**
  (a) ***The `setPosRate` cadence hint has two incompatible printed shapes — and two different
  units.*** ADD §7.5.1 and D5' §5.2 both write `{"cmd":"setPosRate","intervalMs":2000}` — the
  interval at the **top level, in milliseconds**. `backend/contracts/realtime/mqtt-topics.md` §2.2
  and D6' §3.1 write the general envelope `{"cmd":"setPosRate","args":{"seconds":1},"expiresAt":…}`
  — nested, **in seconds**. A client that understood only one spelling would silently keep
  publishing at the wrong rate, which is precisely the failure R-07 exists to prevent, so
  `MqttCommands` is a **tolerant reader**: `args.intervalMs`, `args.seconds`, `args.intervalSec` and
  a top-level `intervalMs` all decode, into the envelope form. **ADD §7.5.1 and D5' §5.2 should be
  restated in the envelope form with one unit.**
  (b) ***The near-geofence burst radius is 150 m in AL-12 and 300 m in the cadence table.*** ADD
  §7.5.1 and D5' §5.2 print `Near-pickup geofence (<300 m)` in the trigger column; AL-12 (ADD
  §12.4) says "1 call/s within an **admin-configurable radius (default 150 m)** of pickup/drop-off".
  Took **AL-12** — it is the later amendment and it is the line this component's ADD list names —
  as `AdaptiveRateConfig.geofenceRadiusMetres = 150`. The phase itself is server-computed and
  arrives as a hint, so the radius only decides when the client *anticipates* the burst; the two
  documents should still agree. **Both trigger columns should read 150 m, admin-configurable.**
  (c) ***No spec fixes the MQTT keep-alive, the CONNECT credential fields, or the session-expiry
  interval.*** D6' §3.2 and `mqtt-topics.md` §3 define the *credential* (the E-02 session JWT) but
  not where it sits in the CONNECT packet, and ADD §7.1 only says MQTT is chosen partly for its
  "~2-byte keepalives". Landed the EMQX defaults — `password` carries the JWT, `username` is the
  vehicle id, `clientId` is the device id — with 60 s keep-alive, 30 s connect timeout, persistent
  session (`cleanStart = false`) and 1 h session expiry, all on `MqttConfig` so an operator can
  move them. **C038 must confirm these against the EMQX listener config it lands**; if they differ,
  this is the one file to change.
  **Other spec observations (no change needed, worth knowing):**
  (d) *`AdaptiveRateEngine`'s per-phase default is derived, not printed.* D5' §5.2 gives **ranges**;
  three later lines pin points inside them — AL-12's 1 s burst and 60 s Mode C idle standby, and
  D5' §5.1's base cadence (4 s moving, 10 s stationary, 60 s idle). The single rule consistent with
  every one of those is **slow end of the range, except inside the geofence burst**, which is what
  `GpsPhase.defaultInterval` implements and what `AdaptiveRateEngineTest` re-declares independently
  and sweeps. Only `CANDIDATE_IN_POOL` (2–5 s → 5 s) has no external anchor.
  (e) *"Live preempts replay 4:1" (D6' §3.5) is implemented as weighted fair share, not a hard
  gate.* A strict "one replay per four live publishes" would stall a parked vehicle's backlog
  forever, because nothing generates live publishes to earn the credit. `PositionReplayQueue.peek`
  therefore takes `livePending`: the ratio applies while both streams are flowing, and the backlog
  drains at the full 20/s ceiling when live is idle.
  (f) *`signalr-hub.md` §2.1 names the `ride:{rideId}` and `booker:{bookerId}:loc-req:{requestId}`
  groups; no spec names a group for AL-31's driver home map.* None is needed —
  `LiveMapScope.DriverHomeMap` joins **nothing**, because the driver's own marker comes from the
  device's own GNSS, which the position publisher already holds. Encoding it as a type with no
  cells makes the fence structural rather than a rule in a screen.
  (g) *D5' §5.2's coalesce rule ("skip if Δpos < 25 m") means a stationary vehicle publishes
  nothing at all, indefinitely.* Implemented exactly as written; `AdaptiveRateConfig
  .coalesceHeartbeat` is an opt-in escape hatch, **off by default**, for an operator who wants
  proof-of-life on the position plane rather than from the LWT.
  **Decisions —**
  (1) ***H3 is a platform seam, and this is the component's biggest call.*** Cell ids must be
  **bit-identical** to the ones `position-processor-svc` computes, or a passenger joins
  `cell:{h3index}` groups nothing publishes to — a failure that renders as an empty map with no
  error anywhere. H3's grid is defined by constant tables (20 face centres, 122 base cells with
  their neighbours and rotations); re-deriving them in common Kotlin would be a large piece of
  unverifiable arithmetic whose failure mode is silent. So `H3Grid` is a four-method interface,
  Android binds `com.uber:h3` (JNI over the reference C library), and the *rules* — resolutions,
  ring size, hysteresis, exact post-filter, everything in `mqtt` and `realtime` — are common code
  that runs unchanged on both platforms. **`AndroidH3GridTest` asserts the 19-cell golden set for
  Colombo Fort verbatim**, so an H3 upgrade that moved a cell id fails the build instead of moving
  every SignalR group name on the platform.
  (2) ***iOS has no H3 engine yet, and `platformH3Grid()` answers `null` there.*** The Kotlin/Native
  half needs a `cinterop` binding against an H3 compiled for `ios-arm64` / `ios-simulator-arm64`,
  which can only be produced on macOS with Xcode — committing an unbuilt, untested cinterop from
  this Linux host would be worse than the gap. **C085 / C094 must bind an `H3Grid` in their own
  Koin module** (four methods over a Swift H3 package); app modules are appended after
  `sharedModules`, so that binding overrides `geoRealtimeModule`'s default, and
  `GeoRealtimeGraphTest` asserts the override path works on every target. Nothing else in the three
  packages needs an engine.
  (3) **The index *layout* is read in common Kotlin.** `H3Cell.resolution`, `.baseCell`, `.token`
  and `.isWellFormed` come from the documented bit layout rather than from the library, so the hex
  spelling every group name is built from is verified on iOS too; `AndroidH3GridTest` checks that
  reading against `com.uber:h3` at seven resolutions. `isWellFormed` deliberately stops short of
  H3's `isValidCell` — it does not reject a pentagon's deleted subsequence, which needs the base
  cell tables — and says so.
  (4) **The 30 s hysteresis applies the first crossing immediately, then holds; crossing back
  cancels the held one; a reconnect is exempt.** The DoD's wording ("suppressed for 30 s *after* a
  boundary crossing") and the thrash case ADD §7.4 step 6 describes both point the same way. The
  reconnect exemption matters: after a drop the server holds no membership at all, so rate-limiting
  `onReconnected()` would leave a passenger's map blank for up to half a minute.
  (5) **C015's three geometry functions moved into `domain/geo` and its private copies were
  deleted.** `DirectionalTravel.kt` had its own haversine, bearing and angular-difference because
  `domain/geo` was expected to be JNI-backed; it is not — only the *index* arithmetic is
  platform-supplied — and DT-02's thresholds and R-06's exact post-filter are the same two
  formulae. One implementation, one set of tests, and the stale comment in C015 is corrected in
  place.
  (6) **`ReconnectBackoff` lives in `util/`, not in either plane.** R-09 specifies the same
  1–60 s ±25 % curve for MQTT (D6' §3.5) and SignalR (`signalr-hub.md` §1.2) because the two fail
  together. The band is symmetric and the jittered result is **not** clamped back to 60 s — the
  same choice C002 decision 5 made for Polly, and for the same reason.
  (7) **`geoRealtimeModule` binds exactly one thing, `H3Grid`.** Every other type here is either
  stateless (`GeoCells`, `MqttTopics`, `LiveHub`) or built from configuration the client has just
  read (`AdaptiveRateConfig`, `MqttConfig`, `GeoCellSubscription`, `PositionReplayQueue`) — same
  reasoning as C015 and C016, and the KDoc says so in place.
  (8) **The four commands this module does not act on stay untyped.** `mqtt-topics.md` §2.2 fixes
  the *names* of all five downlink commands but the `args` shape of only `setPosRate`; a typed
  `SetGeofence` would be this module inventing a contract. `MqttCommand.Other` carries the envelope
  and a nullable name — an unknown command is delivered and logged, never guessed at.
  **For later components —**
  **C038 / C039 / C040 / C041 (the real-time services):** `MqttTopics`, `MqttTopicKind` (QoS +
  retain) and `PositionCodec` are the client half of your contract; `LiveHub.Method` / `.Event` /
  the three group builders are the client half of the hub. `MqttRateLimits` carries the four
  ceilings. The `seq` watermark (`veh:seq:{vehicleId}`) is what `PositionSequencer` is generating
  against — see decision (1) in note (e) about replay ordering.
  **C018 (kmp-local-db):** **you own the `seq` watermark.** `PositionSequencer(start = …)` must be
  constructed from persisted state; if it rewinds, `position-processor-svc` discards every sample
  published afterwards and the vehicle goes dark while the app believes it is publishing. The GPS
  ring buffer (`sequence_no PK monotonic, vehicleId, lat, lng, ts, accuracy, source`, ADD §11.13) is
  the storage behind `PositionReplayQueue`.
  **C067 / C076 (the Android shells):** you own the HiveMQ and SignalR sockets. Build `MqttConfig`
  once, take the will from `MqttConfig.lastWill(vehicleId)` and the credential from
  `MqttConfig.credentials(token)` — and **reconnect when `MqttSessionTokenManager.token` changes**,
  because EMQX validates the JWT at CONNECT only. Drive `AdaptiveRateEngine.decide` from the
  foreground service and call `onPublished` for retries too.
  **C085 / C094 (the iOS shells):** as above, plus **bind an `H3Grid`** — see decision (2). Until
  you do, anything that opens a map throws `H3GridUnavailableException` naming the binding.
  **C078 / C096 (passenger live map):** `GeoCellSubscription` is the whole subscription lifecycle;
  send `update.join` / `update.leave`, not the full set. On reconnect follow
  `LiveHubRecovery.plan` — groups first, `GET /v1/nearby` second, in that order.
  **C019 (test kit):** `TestH3Grid` in `commonTest` is a deterministic hex grid that satisfies the
  ring arithmetic without an engine; it is a good candidate to promote.
  **Build host —** no Docker and no compose stack; the replica stayed down. Two new dependencies:
  `kotlinx-serialization-cbor` (commonMain) and `com.uber:h3` 4.4.0 (**androidMain only** — its jar
  ships `linux-x64` natives alongside the Android ones, which is why the JVM host tests can exercise
  the real engine on this box). A clean gate run takes ~40 s and `compileKotlinIosArm64` another
  ~18 s. Worth knowing next time: **detekt's `ReturnCount` limit is 2**, which guard-clause parsers
  trip constantly — four functions carry a justified `@Suppress`, following C013's precedent.
