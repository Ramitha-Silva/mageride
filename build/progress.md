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
| C006 | db-schema-telemetry-timescale | 0 | PENDING | | |
| C007 | openapi-contracts | 0 | PENDING | | |
| C008 | api-gateway-yarp | 0 | PENDING | | |
| C009 | docker-compose-dev | 0 | PENDING | | |
| C010 | ci-skeleton | 0 | PENDING | | |
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
