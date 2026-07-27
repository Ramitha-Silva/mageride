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
| C011 | kmp-module-scaffold | 1 | PENDING | | |
| C012 | kmp-core-models | 1 | PENDING | | |
| C013 | kmp-api-client | 1 | PENDING | | |
| C014 | kmp-auth-session | 1 | PENDING | | |
| C015 | kmp-domain-ride-dispatch | 1 | PENDING | | |
| C016 | kmp-domain-fare-wallet | 1 | PENDING | | |
| C017 | kmp-geo-realtime | 1 | PENDING | | |
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
