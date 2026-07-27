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
| C002 | backend-shared-kernel | 0 | PENDING | | |
| C003 | db-schema-identity-registry | 0 | PENDING | | |
| C004 | db-schema-trips-rides-dispatch | 0 | PENDING | | |
| C005 | db-schema-business-content | 0 | PENDING | | |
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

