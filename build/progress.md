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
| C018 | kmp-local-db | 1 | DONE | 2026-07-27 | 767 tests green (113 new); two SQLDelight databases, schema v2 with a tested migration; 4 micro-change-sets raised |
| C019 | kmp-test-kit | 1 | DONE | 2026-07-27 | 817 tests green (50 new); MockEngine fake covering all 176 operations + descriptor-driven fixtures; contract checks over 176 responses and 85 request bodies |
| C020 | ws-iam-minimal ⭑ | 2 | DONE | 2026-07-28 | 91 tests green; 3 iam migrations added (0104–0106) — 3 micro-change-sets raised |
| C021 | ws-registry-minimal ⭑ | 2 | DONE | 2026-07-28 | 92 tests green; 2 registry migrations added (0307–0308) — 3 micro-change-sets raised |
| C022 | ws-ride-svc-happy-path ⭑ | 2 | DONE | 2026-07-28 | 109 tests green; 1 rides migration added (0608) + 2 internal contract routes — 5 micro-change-sets raised |
| C023 | ws-dispatch-stub ⭑ | 2 | DONE | 2026-07-28 | 78 tests green; 1 dispatch migration added (0710) + 1 internal contract route — 11 micro-change-sets raised |
| C024 | ws-realtime-pipeline ⭑ | 2 | DONE | 2026-07-28 | 35 tests green (3 new services + EMQX fixture); p95 EMQX→SignalR 2.1 s; H3 grid + Kafka consumer promoted to the kernel; 4 micro-change-sets raised |
| C025 | ws-e2e-android-slice ⭑ | 3 | DONE | 2026-07-28 | **WALKING SKELETON REACHED** — one booked ride end to end on the real stack; 2 Android shells assemble; `:shared` gained a jvm() target; wave-1 gate repaired |
| C026 | iam-svc-auth | 2 | DONE | 2026-07-28 | 209 tests green (118 new); 1 iam migration added (0107) — 4 micro-change-sets raised; `POST /v1/auth/mqtt-token` closes C025 gap (c) |
| C027 | iam-svc-profile-rbac | 2 | DONE | 2026-07-28 | 330 tests green (121 new); 1 iam migration added (0108); 8 routes D3' does not carry raised as micro-change-sets; URD §2.3 matrix parsed from `specs/` by the test |
| C028 | registry-svc-vehicles | 2 | DONE | 2026-07-28 | 145 tests green (53 new); 3 registry migrations (0309–0311) + a 7th Redpanda topic; 0308's composite FK relaxed for US-13.9; dispatch-svc now reads the eligibility projection |
| C029 | registry-svc-onboarding | 2 | DONE | 2026-07-28 | 175 tests green (30 new); 1 registry migration (0312) + the `IDocumentExtractionClient` port C054 implements; `vehicle.registered` finally emitted; 6 new `registry.events` types, none with a spec'd envelope |
| C030 | provisioning-svc | 2 | DONE | 2026-07-28 | 99 tests green; 4 prov migrations (0402–0405); device mTLS enabled on EMQX 8883 (`peer_cert_as_username = cn`) so a tracker's certificate *is* its topic grant; 7 micro-change-sets |
| C031 | trip-state-svc | 2 | DONE | 2026-07-28 | 46 tests green; 2 trips migrations (0504–0505); the D-03 mutex is the partial unique index and the Redis half needed a new key — `lock:driver:{driverId}` was already registry's; 6 micro-change-sets |
| C032 | ride-svc-core | 2 | DONE | 2026-07-28 | 252 tests green; the §11.12 matrix is data, not a switch; no new migration — 0605's timer kinds and `terminal_at` were already right; 4 micro-change-sets |
| C033 | reputation-svc | 2 | DONE | 2026-07-28 | 84 tests green; 3 reputation migrations (0803–0805) + an 8th Redpanda topic; the platform's first gRPC service, which needs a port of its own (cleartext has no ALPN); 8 micro-change-sets |
| C034 | dispatch-svc-core | 2 | DONE | 2026-07-29 | 108 tests green (30 new); 2 dispatch migrations (0711–0712) + a `reason` on ride-svc's `/offer/expire`; **0712 fixes a live bug — an ACCEPTED offer stayed live for ever, so a driver could take exactly one ride**; 9 micro-change-sets |
| C035 | dispatch-svc-scheduling-levels | 2 | DONE | 2026-07-29 | 143 tests green (35 new); 1 dispatch migration (0713, +`dispatch.level_config`) + a fifth internal command on ride-svc (`/v1/internal/rides/scheduled`); the C033/C034 "sole writer of `dispatch.driver_levels`" fence narrowed to counter-driven rules; 11 micro-change-sets |
| C036 | dispatch-svc-directional | 2 | DONE | 2026-07-29 | 180 tests green (37 new); **no migration** — 0707/0708 already carried every table and the open `kind` column; the DT-02 predicate reads the durable row rather than ADD §7.4's Redis hint, argued below; `GeoMath` promoted to the kernel; 8 micro-change-sets |
| C037 | ride-svc-proxy-package | 2 | DONE | 2026-07-29 | 314 tests green (62 new); 1 rides migration (0609 — the package recipient + the location-request sweep index) and a `ServiceUnavailable` response in `_shared.yaml`; **no new ride state** — ADD Appendix B.2 invariant 6 held literally, the OTP gates take the edges `start`/`complete` already take; 11 micro-change-sets and one genuine P-03-versus-AL-48 conflict |
| C038 | mqtt-bridge-svc | 2 | DONE | 2026-07-29 | 43 HotPath tests green (13 tagged `Category=MqttBridge`, 8 new); **one broker-config change** — `mqtt.shared_subscription_strategy = sticky`, because EMQX 5.8's `round_robin` default makes two replicas race one vehicle's samples and the end-to-end ordering DoD unreachable; live and replay now hold **separate broker sessions**, not just separate share groups; the bridge became the D-17 *detector* (EMQX stays the enforcer); `IEventPublisher` now returns a `PublishReceipt`; 4 micro-change-sets |
| C039 | position-processor-svc | 2 | DONE | 2026-07-29 | 88 HotPath tests green (53 tagged `Category=PositionProcessor`, 45 new); **no migration** — this service has no database; D-18/T-07 landed as a pure filter with ADD §12.6's table in configuration; the R-08 ownership conflict between ADD §9.4 and C034 resolved by splitting on *what decides the fact* (phase = dispatch-svc, position = here) with a new `veh:driver:{vehicleId}` binding; one fixture bug fixed — `Samples.Dehiwala` was documented as a different res-5 cell and is not; 7 micro-change-sets |
| C040 | persistence-writer-svc | 2 | DONE | 2026-07-30 | 111 HotPath tests green (23 tagged `Category=PersistenceWriter`, all new); **1 trips migration (0506)** — `ux_possample_session_minute` and `trips.session_summaries`, the ADD §9.2 trip summary that no DDL source printed; `COPY` measured at **14,811 rows/s** against the DoD's 3,000; Postgres joins `HotPathCollection` (4 containers now) and `migrate-verify.sh` expects 7 trips tables, not 6; 6 micro-change-sets |
| C041 | fanout-svc | 2 | DONE | 2026-07-30 | 48 tests green in a **new suite** (`Fanout.Api.Tests`; the 9 hub tests moved out of HotPath.Tests, which is 102); **no migration** — this service owns no table; the D6' §5.1/§5.2 contradiction resolved with a `vehicle:{vehicleId}` group, argued below; a **custom Redis control channel** rather than SignalR's backplane, because the latter would multiply every cell batch by the replica count; 9 micro-change-sets |
| C042 | query-svc | 2 | DONE | 2026-07-30 | 65 tests green in a **new suite** (`Query.Api.Tests`); **no migration** — this service writes nothing anywhere; the D-22/D-23/US-7.16/US-7.17 filter **promoted into the kernel** (`MageRide.Shared.Realtime.VehicleVisibilityRules`) so the socket and the snapshot cannot disagree, which is what C041's handoff asked for; `Fanout:JoinSeedFrames` retired with it (Fanout.Api.Tests 48 → 47, HotPath.Tests 102 → 101); the platform's second gRPC service; **no Mode C track exists anywhere to serve as a polyline** and it is not invented; 9 micro-change-sets |
| C043 | tcp-adapter | 2 | DONE | 2026-07-30 | 89 tests green in a **new suite** (`TcpAdapter.Tests`); **no migration** — this service writes nothing anywhere, and reads `registry.vehicles` read-only; four golden frames, one per protocol family, byte-exact with real checksums; the platform's first `Microsoft.NET.Sdk.Worker` service (no HTTP surface at all, so no `AddMageRideDefaults`); a read-only Postgres dependency D7' §2.1's Container 9 row does not list, argued below; `POST /v1/internal/sessions/ignition` finally has a caller; 9 micro-change-sets |
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
**REACHED 2026-07-28 (C025).** `bash e2e/walking-skeleton/run.sh` brings up
`infra/docker-compose.skeleton.yml` from nothing and drives one Mode C ride to `PaymentPending`
through real EMQX, Redpanda, Redis and SignalR, using the KMP module the two Android apps use.

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

- **Component:** C018 kmp-local-db — 2026-07-27
- **Status:** DONE — `./gradlew :shared:testDebugUnitTest detekt ktlintCheck` → **767 tests passed, 0
  failed, 0 skipped** (113 new), detekt and ktlint clean, `compileKotlinIosArm64` green. All four DoD
  items pass: every §1–§3 table exists with its documented columns and indexes (`MobileSchemaTest`), a
  queued command survives a real close-and-reopen and is replayed exactly once
  (`CommandOutboxDurabilityTest`, file-backed SQLite), `gps_buffer` evicts by age and size without
  losing ordering (`GpsBufferSqlTest`), and the v1→v2 migration is tested against a real engine
  (`SchemaMigrationTest`).
- **Notes:**
  **Spec gaps — four micro-change-sets, none actioned in `specs/`:**
  (a) ***`sync_state` does not exist.*** §1.12's heading reads "`sync_state` / `meta`" and §1.5 says
  the GPS sequence is kept "in `meta`/`sync_state`", but the section prints DDL for **`meta` only**
  and nothing else in the document references a `sync_state` table. **Only `meta` created** — the
  same phantom-table class as C003's `iam.user_prefs` and planner finding 2. §1.12 should drop the
  `sync_state` half of its heading, or print the table.
  (b) ***`meta('gps.seq')` cannot be a single key.*** §1.12 gives `'gps.seq'` as the illustrative
  spelling, but §1.5 requires the sequence to be "monotonic **per vehicle_id**" and `gps_buffer`'s
  primary key is `(vehicle_id, seq)`. One counter across two vehicles is fatal for the first one a
  driver comes back to: its counter has moved on without the server ever seeing the gap, so
  `position-processor-svc` discards everything it publishes. **Landed `gps.seq.{vehicleId}`**
  (`MetaKeys.gpsSeq`). §1.12's example key should be corrected.
  (c) ***No retry budget is fixed for `ABANDONED`.*** §1.4 declares the state and §4.1 says "backoff
  is jittered exponential (§7.5.3)", but nothing says when a command stops being retried, and §4.3
  gives retention rules for `ACKED` and `FAILED` and none for `ABANDONED`. **C018 chose 24 h of age
  (primary) plus 50 attempts (a backstop for a command failing fast in a loop)**, both on
  `OutboxRetryPolicy` and both overridable. §4.1/§4.3 should state a figure.
  (d) ***§4.3 has no rule for three tables that grow without bound.*** `trip_shares` (a token per
  share, D-34), `place_recents` (a row per search, local-only) and `job_board` (a row per scheduled
  ride whose pickup time passes). Added as **explicitly labelled C018 additions** on
  `RetentionPolicy` — `tripShareGrace` 1 d, `placeRecentsMax` 50, `jobBoardGrace` 6 h. Everything
  else in `RetentionPolicy` is a figure §4.3 prints.
  **Other spec observations (no change needed):**
  (e) §0.1 offers Room (Android) / GRDB (iOS) / **SQLDelight** ("recommended, optional") and this
  prompt's scope makes SQLDelight the choice, so the `.sq` files are the canonical DDL and there is
  no Room `@Entity` anywhere. §0.1's first two rows are now historical.
  (f) `mobile_db_schema.md` is landed in its **post-Δ shape** — §6's `driver_phone` and `ui_prefs`,
  §7's widened `documents.kind`, §8's `qr_claimed_at` and AL-48 renames — with **one exception**: §8
  is *also* replayed as migration `1.sqm` (see decision 3). C005 note (e) established that these Δs
  are history rather than a sequence the repo replays; that holds for a server DDL created fresh by
  DbUp, and does not hold for a device that is already carrying the old schema.
  (g) `rides.state` and `payment_state` are deliberately left **without a CHECK**, as the spec prints
  them. ride-svc and fare-svc are the sole writers, and a client that rejected a state its build had
  not heard of would strand a passenger on a ride that had already moved on (the C015/C016 rule).
  **Decisions —**
  (1) **Two SQLDelight databases, and the §1 tables are authored once.** §0.2's "one database file
  per app" is encoded as `MageRidePassengerDatabase` + `MageRideDriverDatabase`, so a passenger file
  physically cannot contain `dispatch_offers`. The thirteen shared tables live in
  `src/commonMain/sqldelight/shared/` and a Gradle `Sync` materialises them into each database's own
  package. **That indirection is required, not stylistic:** SQLDelight derives a generated type's
  package from its path under the source root, so pointing both databases at one directory emits
  `…db.core.Command_outbox` twice into one commonMain compilation and the module does not compile
  (verified — "Redeclaration"). Also verified: SQLDelight reads `srcDirs` at configuration time and
  drops any task dependency a provider carries, so the `Sync` needs an explicit `dependsOn` or it
  silently never runs and both databases generate with the §1 tables missing.
  (2) **The SQLite dialect is the 3.18 default, on purpose.** URD NFR-22 pins minSdk 26 and Android
  8.0 ships SQLite **3.19**, so `ALTER TABLE … RENAME COLUMN` (3.25), UPSERT (3.24) and row-value
  `IN` (3.15) are all unavailable. Everything is written to that floor. SQLCipher links its own
  ~3.4x SQLite, but an unencrypted build falls through to the platform engine, so the floor stands.
  (3) **Schema version is 2, and `1.sqm` is §8 (AL-47 + AL-48).** A fresh install creates the final
  shape directly; a handset carrying the Δ 2026-06-28 schema migrates to the same place. The renames
  are done by **rebuild** (`RENAME TO` aside, `CREATE`, copy, `DROP`) rather than `RENAME COLUMN` per
  decision (2), and `proof_upload_queue`'s widened `kind` CHECK needs a rebuild in any SQLite version.
  `SchemaMigrationTest` asserts the migrated database is **structurally identical** to a fresh one —
  table set, columns, indexes and normalised DDL — which is the guard that stops `.sqm` and `.sq`
  drifting. The v1 fixture writes out only the **six tables migration 1 touches**; every other table
  comes from the shipped `.sq`, so a future migration that touches a seventh fails loudly rather than
  quietly skipping it.
  (4) **`INSERT OR IGNORE` suppresses every constraint, not just the primary key**, so
  `command_outbox` and `proof_upload_queue` use a plain `INSERT` — a swallowed command is a lost user
  action and a swallowed proof is lost delivery evidence (P-10). R-18's "one key, one command" moved
  into `CommandOutbox.enqueue`, inside the same transaction. `gps_buffer.append` keeps `OR IGNORE`:
  a repeated `(vehicle_id, seq)` is the designed path (R-17 local dedupe) and the only CHECK on the
  table is written from our own enum. Both choices are commented at the query.
  (5) **`seq` is reserved in blocks of 100.** Persisting per fix would be a database write per second
  inside AL-12's 1 s near-geofence burst. A crash skips the unused tail, which is correct: `seq` must
  be strictly increasing, not gapless — the server's watermark is a floor and
  `ux_positions_vehicle_seq` only rejects exact duplicates. The start is
  `max(persisted watermark, highest seq still on disk)`, so a restored backup cannot rewind either.
  (6) **Encryption differs per platform and iOS is not a gap.** Android is SQLCipher
  (`net.zetetic:sqlcipher-android` 4.17.0) keyed from 32 CSPRNG bytes in C014's Keystore-backed
  `SecureStore` — exactly §0.4's "key is wrapped by the hardware keystore". **iOS uses
  `NSFileProtectionCompleteUntilFirstUserAuthentication`**, which §0.4 explicitly permits ("SQLCipher
  **or GRDB encryption**"): iOS encrypts the file with a class key held in the Secure Enclave, so
  there is no application-held key to leak at all. `AfterFirstUserAuthentication`, not `Complete`,
  matching C014's Keychain accessibility — the driver app writes `gps_buffer` from a background
  location session with the handset locked, and `Complete` would fail every one of those writes. The
  `DatabasePassphrase` seam is carried and unapplied on iOS; a SQLCipher cinterop for `ios-arm64`
  cannot be built on this Linux host (same shape as C017's H3 seam), and that one function is all
  C085/C094 would change.
  (7) **Only the machinery has a store interface.** `CommandOutbox`, `GpsBuffer`, `MetaStore` and
  `Retention` are implemented twice (once per generated package) because `:shared` implements logic
  over them; the projections are not, because `rides` has exactly one consumer and so does
  `dispatch_offers`. Apps reach those through `PassengerDb.sql` / `DriverDb.sql`.
  (8) **The wipe and the row counts are driven off `sqlite_master`**, not a per-app table list, so a
  table added to one schema and not the other cannot be missed. §0.4's real erase is still
  `DatabaseDriverFactory.delete(app)` + `DatabaseKeyManager.forget(app)` — the whole file plus the
  key, because an emptied SQLite file keeps its old pages until something overwrites them.
  `MageRideDb.wipe()` is the in-place fallback for a caller that cannot close the connection.
  (9) **`offline_map_bundles` rows are reported, not deleted.** §4.3 says to evict stale bundles, but
  the row is the only pointer to a PMTiles file the app must delete first — dropping it would orphan
  tens of megabytes on disk. `RetentionReport.mapBundlesToRelease` hands the caller the paths.
  (10) **`localDbModule` deliberately does not bind an open database.** Opening one is `suspend` (the
  key is unwrapped from the Keystore) and a `runBlocking` single would put that on
  `Application.onCreate`. The app binds a `DatabaseDriverFactory` — the fifth app-supplied binding
  across C013/C014/C017/C018 — and opens the database during start-up.
  (11) **Everything is blocking.** `Dispatchers.IO` is not resolvable from `commonMain` for this
  module's target set, and an `expect val` for it would be a platform seam that buys nothing an app
  cannot express better. Documented at `MageRideDb` and in `shared/kmp/CLAUDE.md`.
  (12) **§0.5's schema-revision row is written on open**, not left to the app:
  `MageRideDatabaseFactory` stamps `meta('schema.rev')` when it differs from the schema version, so
  a routine open costs a read rather than a write. `PRAGMA user_version` stays authoritative — it is
  what SQLDelight reads to decide whether to migrate; the `meta` row is the one a support bundle or
  a crash report can see.
  (13) `detekt.yml` gains a **`TooManyFunctions`** exclusion for `**/db/**` and the test source sets:
  a repository interface's function count is its table's operation set (`OutboxStore` has thirteen
  because §1.4's write path needs thirteen statements), and a test class's is its assertion count.
  Same argument the existing `LongParameterList` exclusions make for `data/models` and `data/api`.
  **Two SQLDelight gotchas worth knowing before the next `.sq` edit:** an aggregate with a column
  alias (`SELECT MAX(seq) AS seq`) generates a one-field wrapper data class instead of returning the
  scalar, and **`SUM()` is typed REAL**, so a money or byte total comes back as a `Double` unless it
  is `CAST(… AS INTEGER)`. Both were caught by the compiler here; the second would otherwise have
  reached an eviction threshold as a floating-point comparison.
  **Build host —** a cold gate (`:shared:clean` + `--no-build-cache`) runs 767 tests in ~75 s.
  `org.gradle.caching=true` turns a repeat run into a 3 s cache restore, test results included —
  **clean before quoting a gate time.** New coordinates fetched: SQLDelight 2.3.2
  (runtime, primitive-adapters, coroutines-extensions, android-driver, native-driver,
  **sqlite-driver** — androidHostTest only, a real xerial SQLite, which is what makes the schema and
  migration testable on Linux) and `net.zetetic:sqlcipher-android` 4.17.0 (androidMain only; the AAR
  is not loadable in a JVM host test, so the SQLCipher path is compiled here and exercised on a
  device). `compileKotlinIosArm64` type-checks the iOS actuals in ~30 s; **iOS is not marked DONE
  from this host** — `NativeSqliteDriver` and the `NSFileProtection` call need a Mac to run.

- **Component:** C019 kmp-test-kit — 2026-07-27
- **Status:** DONE — `./gradlew :shared:testDebugUnitTest detekt ktlintCheck` green on a cold run:
  **817 tests passed, 0 failed, 0 skipped** (50 new; C011–C018's 767 still green), detekt and
  ktlint clean. `compileTestKotlinIosArm64` also passes, so the kit type-checks for Kotlin/Native;
  **iOS is not marked DONE from this host** (klib cross-compilation only). C019 added **no line to
  any main source set** — `git diff shared/kmp/src/{commonMain,androidMain,iosMain}` is empty for
  this component, which is the fence discharged rather than argued.
- **Notes:**
  (1) **The fake is the BACKEND, not the clients.** The DoD asks that "every typed client has a
  fake with the same surface"; the obvious reading — sixteen hand-written `RideApi`-shaped stubs,
  ~176 methods — produces a second implementation that must be kept in step with the first and
  that silently skips everything interesting about a call. `FakeApiBackend` is a `MockEngine`
  instead, and the clients above it are the **production** ones. The surface is identical because
  it is the same interfaces, and a test still exercises the minted-once `Idempotency-Key`, the
  one-refresh-on-401 replay, the `426` update wall and the real serializers.
  (2) **Routing is by `operationId`, not by path.** C013's `ApiTransport` already puts the
  contract's own id in every request's attributes (`OperationIdAttribute`), so the engine reads it
  straight off the request. A stub therefore cannot be attached to the wrong route by a mistyped
  URL, and an unknown id throws with a message rather than 404-ing into a client that would read it
  as "not found". This was the single largest simplification in the component.
  (3) **`ApiOperations` is the contract's route table as Kotlin** — 176 rows of
  (id, service, verb, path, success status, response serializer, request serializer). The response
  and request columns are the **typed client's own declared types**, so a body the fake synthesises
  always decodes into exactly what the caller is handed; that is what makes "same surface" a
  property of the code rather than of a review. It has to be Kotlin because the fake needs a
  compile-time `KSerializer` and `commonTest` cannot read a file — so `ApiOperationTableTest`
  (androidHostTest) asserts every row's id, verb, path, status and body-or-not against the YAML.
  An operation added to a contract fails the build there. It was **generated** by joining the
  sixteen client interfaces (return type, body parameter) to the contracts (verb, path, status);
  regenerate the same way rather than hand-extending.
  (4) **Fixtures are derived from `SerialDescriptor` rather than typed out.** The deliverable says
  "fixture builders for every DTO"; there are 289 DTOs, and 289 hand-written builders would be 289
  things to forget when a contract changes. `DtoFixtures.of<T>()` walks the descriptor
  kotlinx.serialization already generates and populates **every** field — required, optional and
  nullable — choosing values from the field's *name*, so `driverPhone` is `+947…`, `otp` is four
  digits and a `…Minor` field is cents. A DTO that gains a field has a fixture with that field on
  the next build, with no edit anywhere. This is also what makes (7) exhaustive.
  (5) **A fixture is a shape; a scenario is a story.** Populating every field produces a
  `RideDetail` that is `Requested` with a driver attached, which no real ride ever is. The four
  canonical journeys are therefore hand-written: `ModeCRide` (→ `Paid`), `ProxyRide` (→
  `CashSettled`, booker ≠ rider, P-05's counterparty phone), `PackageDelivery` (→
  `CashOnDeliveryCollected`, two OTP handoffs) and `ModeBSubscription` (two billing cycles, no ride
  aggregate anywhere in it). Their edges are checked against `RideTransitions` rather than against
  themselves, and the three ride journeys are asserted to traverse the *same* states up to
  settlement — ADD Appendix B.2 invariant 6 as a test rather than a comment.
  (6) **Two clocks, and choosing the wrong one is the bug they exist to prevent.** `TestClock` is
  wound by a statement. `TestTime` (`TestScope.testTime()`) reads the **scheduler's** virtual time,
  so a `delay(25.minutes)` inside the code under test *is* twenty-five minutes on the clock that
  code compares against; a test that drives `runTest` and a hand-wound clock has two notions of
  "now", and the flake that produces looks exactly like a real one. `TestTime.advanceBy` follows
  `advanceTimeBy` with `runCurrent`, because `advanceTimeBy` alone stops just short of its target
  and leaves a task scheduled at exactly that instant un-run — which reads as "the renewal did not
  fire" when the point of the assertion is that it did.
  (7) **The contract checks run the whole chain, both directions.**
  `client return type → SerialDescriptor → fully-populated document → the operation's own schema in
  backend/contracts`. All 176 responses and all 85 request bodies. A field the DTO stopped sending
  is `required, but absent`; a field it gained or renamed is `not declared by the schema`; a
  changed type is a type mismatch; a misspelt enum is `not one of`. Two tests deliberately corrupt
  a good fixture and assert the checker notices, so "the sweep passes" cannot mean "the sweep
  checks nothing".
  (8) **The validator is stricter than OpenAPI, on purpose.** An undeclared property is an error
  even though a schema without `additionalProperties: false` technically permits anything — because
  the document under test was generated *from the DTO*, so an undeclared property means the DTO has
  a field the contract does not, which is precisely the drift being hunted. A schema that is
  genuinely open says so, and that is honoured. Not checked: `pattern`, `format`,
  `min/maxLength`, `minimum`/`maximum` — those constrain values a **server** must reject, not
  shapes a client must match. The fixture values satisfy them anyway.
  (9) **`snakeyaml`, androidHostTest-only.** C013's `ContractScanner` line-scans because it only has
  to find operation ids; comparing a *tree* with cross-file `$ref`s and `allOf`s needs a real
  parser. It never reaches a main source set, so no YAML library ships anywhere.
  (10) **A synthesised page is closed.** `DtoFixtures` populates every field, which for a `Page`
  means `hasMore = true` and a cursor — a page claiming another one exists, which `CursorPagedSource`
  would follow forever. The fake overrides both to "one complete page"; a test that is *about*
  paging queues the two calls it wants.
  (11) **C013's `FakeTokenProvider` and `SequentialIdempotencyKeys` are now typealiases** into the
  kit. A fake that every module reuses belongs in the kit, and keeping C013's tests compiling
  unchanged against the moved versions is the cheapest possible proof that they are drop-in.
  (12) **The kit ships as its own jar.** `:shared:testKitJar` packages `lk.mageride.shared.testing`
  onto the `testKitElements` configuration, excluding its own `*Test` classes and the
  androidHostTest-only `contract` reader; C025/C067/C076 consume it with
  `testImplementation(project(path = ":shared", configuration = "testKitElements"))`. **Scope
  stated plainly:** that covers every consumer that exists before wave 4b. Packaging the same
  `commonTest` sources as an iOS klib for an external consumer needs a Mac and a real iOS consumer
  to verify against, and neither exists yet — deferred rather than faked.
  (13) `detekt.yml` gains a **`LargeClass`** exclusion for the test source sets: `ApiOperations` is
  176 one-line rows because the contracts declare 176 operations, and splitting it by service would
  produce sixteen files of identical combined size joined by a `+`. Same argument the existing
  `LongParameterList` and `TooManyFunctions` exclusions make.
  (14) **Fixed a data race in C013's request recorder, found by this component's gate.**
  `ApiTestKit.testApi` appended to a plain `mutableListOf` from the MockEngine handler, and
  MockEngine serves concurrent requests on several threads — so `five_concurrent_401s_produce_one`
  `_rotation` (C014, D-29) intermittently recorded ten requests instead of eleven and failed. It is
  a test *about* concurrency, so it was the one place a lost append was certain to matter. Both
  recorders — C013's and `FakeApiBackend`'s — are now behind a `Mutex`; the handler is `suspend`,
  so it costs nothing and needs no platform primitive. Five consecutive `--rerun-tasks` runs of the
  affected class pass. Worth knowing because the flake predates C019 and would have been blamed on
  whatever landed next.
  **Two things worth knowing before the next fixture edit:** a **nullable** field's descriptor is
  the same descriptor with a `?` appended to its `serialName`, so a rule keyed on
  `"kotlin.time.Instant"` silently misses `scheduledAt` and fills it with the string
  `"scheduledAt"` — strip the marker first. And `kotlinx.serialization.json.JsonObject` fields (the
  provider-callback `raw`, the push `data`) walk into a **SEALED** `JsonElement` descriptor; they
  are free-form by contract and are synthesised empty.
  **Build host —** no Docker and no compose stack; the replica stayed down. A cold gate
  (`:shared:clean` + `--no-build-cache`) runs 817 tests in ~86 s; `compileTestKotlinIosArm64` adds
  ~2.5 min the first time it links the test klibs. One new coordinate fetched:
  `org.yaml:snakeyaml` 2.6 (androidHostTest only).

- **Component:** C020 ws-iam-minimal — 2026-07-28
- **Status:** DONE — `dotnet test backend/src/Iam.Api.Tests -c Release` → **91 passed, 0 failed,
  0 skipped**. All four DoD items pass against a real Postgres and Redis (Testcontainers). Wave-0
  gates re-run green after the three new migrations: `bash infra/scripts/migrate-verify.sh` →
  **187/187**, `MageRide.Shared.Tests` → 161, `ApiGateway.Tests` → 524.
- **Notes:**
  **Spec gaps — three micro-change-sets, none actioned in `specs/` (D4' §1 owns the iam DDL).**
  All three are landed as migrations because the endpoints cannot meet their own contract without
  them; each file's header carries the argument.
  (a) ***No per-service command log exists except `rides.command_log`*** (`0104__iam_command_log.sql`).
  D3' §0 requires `Idempotency-Key` on every POST and says duplicates "replay the original response
  from a **per-service** command log"; the iam contract makes `POST /v1/auth/otp/verify` idempotent
  ("Idempotent: yes (replay token)"). D4' §5 / server_db_schema §5 print DDL for `rides` only.
  Pointing iam-svc at `rides.command_log` would give two bounded contexts one shared primary key.
  **D4' should print one command-log table per service that has idempotent POSTs.** Shape copies
  0603 minus `ride_id`; `CommandLog:AggregateIdColumn` is null, which the C002 kernel supports.
  **This changes a C003 assertion:** `migrate-verify.sh` now expects **10** iam tables, not 9.
  (b) ***Nothing records which handset a row or an attempt belongs to*** (`0105__iam_device_binding.sql`).
  `iam.devices` keys only on a generated UUID, so the required `deviceId` from otp/request — the
  `device_id` claim (D3' §0) that AL-08 binds a session to — has nowhere to live, and a second
  sign-in from one install would create a second device row. `iam.otp_attempts` likewise records
  neither the `deviceId` (needed for `409 device-mismatch`) nor the app the OTP was for (needed to
  open a passenger vs a driver session). Added `iam.devices.device_key` + a partial unique index on
  `(user_id, device_key)`, and `iam.otp_attempts.device_id` / `.app`. Redis was rejected for the
  latter two: a flush would strand every in-flight login.
  (c) ***"Revoke the session family" is not implementable as `iam.sessions` stands***
  (`0106__iam_session_families.sql`). D3' `/v1/auth/refresh` says replaying a spent refresh token
  revokes the whole family, but no column links a rotated session to the one it replaced, so
  "family" could only mean "everything active for this `(user, app)`" — which **livelocks**: device
  A signs in, device B signs in and revokes A (AL-08), A's background refresh presents its
  now-revoked token and takes B's brand-new session with it, forever. Added
  `iam.sessions.family_id`; a sign-in starts a family, a rotation keeps it, and replay revokes only
  its own lineage. `SessionLifecycleTests.Replaying_a_token_from_an_older_sign_in_does_not_end_the`
  `_newer_session` is the regression test.
  **Contract gaps (no change made, C026 should decide):**
  (d) *`iam.devices.platform` is `NOT NULL CHECK (android|ios)` but no auth request carries a
  platform.* The value is read from the gateway's `X-Platform` (D-31), which D3' does not mark
  required on these operations and the gateway does not enforce (`RequirePlatformHeader: false`).
  Defaults to `android` — the only platform the skeleton ships. Either add `platform` to the
  otp/request body or make the header required on this route.
  (e) *`fcmToken` on otp/request is accepted and dropped.* Its home is
  `iam.devices.fcm_apns_token`, which cannot exist before verify identifies the user, and nothing
  in the skeleton sends an FCM message. C026/C051 should either carry it through the attempt or
  move push registration onto its own endpoint.
  (f) *`attemptsRemaining` is undefined by D3'.* Read as the **send** budget (D-32's 5/h), which is
  what the endpoint's own `429 otp-rate-limited` mapping describes; the C013 KDoc reads it as
  entries-before-lock-out instead. One of the two should be corrected.
  (g) *No spec fixes the OTP TTL or the wrong-entry budget behind the 423.* Chose 5 minutes and 5
  entries (`Otp:Ttl`, `Otp:MaxVerifyAttempts`). D7' §4.2 should carry both.
  (h) *D7' §4.2 has no `Jwt__RefreshTokenKey` row.* Optional; unset, the refresh HMAC is derived
  from the signing key, so a 90-day signing rotation (D7' §13) logs everybody out. Deployments
  should set it.
  **Decisions —**
  (1) **The opaque refresh token is `mr1.{jti}.{hmac}`.** `iam.sessions` has no token column and
  ADD §12.1 calls the row the canonical record, so the token carries its own session id under an
  HMAC instead of a stored secret. Opaque to the client (no claims, nothing decodable), unforgeable
  without the key, and worthless unless its row is unrevoked — which keeps Postgres authoritative
  and Redis `refresh:{jti}` a pure O(1) cache, exactly as the ADD describes.
  (2) **iam-svc resolves its own signing keys locally** rather than fetching its own JWKS over
  HTTP. `AddMageRideAuth` is still what wires the bearer handler — a `PostConfigure` swaps the
  `ConfigurationManager` for an `IssuerSigningKeyResolver` over `SigningKeyRing`. `Jwt:JwksUrl`
  stays configured because it is what *other* services read and the kernel binds it.
  (3) **D-32 fails closed.** An unreachable Redis bucket answers `503 dependency-unavailable`
  rather than letting the send through; the gateway's coarse limiter fails *open* on purpose, but
  this one is the only thing between an attacker and an SMS bill.
  (4) **Roles are not granted by opening an app.** A first sign-in creates the account with the
  role of the app it came from; an existing account keeps its roles and only the `app` claim
  follows the surface. So a passenger who opens the Driver App gets `app=driver, role=passenger`
  and is denied by deny-by-default — holding `driver` is C029's grant to make. **C021's seed
  therefore has to sign its skeleton driver in for the first time with `role=driver`, or grant the
  role directly.**
  (5) **Both start-up secrets are resolved during `IamApplication.Build`** (`SigningKeyRing`,
  `OtpCodes`), so a missing `Jwt:SigningKeyPem` or `Otp:PepperKey` is a deploy that refuses to come
  up rather than a 500 on the first user. Development mints ephemeral ones and warns.
  (6) **`Sms:Provider=notifylk` fails at start-up**, as an options validation, instead of being
  accepted and silently dropping every OTP. Same mechanism refuses `dev` outside Development unless
  `Sms:AllowDevSenderOutsideDevelopment=true` (the replica will want that; it runs synthetic
  numbers).
  (7) **Phone input is normalised, not just validated.** `0771234567` and `+94 77 123 4567` both
  become `+94771234567`; a leading `0` after an explicit `+94` is treated as a typo and rejected
  rather than "fixed" into somebody else's number.
  **For C021–C025 —**
  `infra/docker-compose.dev.yml` expects one `app-services` container built from
  `backend/src/AppServices/Dockerfile` (C026–C066), but the walking skeleton's manifest gives each
  slice its own `*.Api` project. **No Dockerfile was added here** — whoever wires the compose stack
  for C025 has to decide between a per-slice container and an early `AppServices` host, and doing
  it now would have guessed at that. `dev-up.sh full` still names the missing pieces correctly.
  **Build host —** Docker is used by the test suite (Testcontainers `timescale/timescaledb-ha:pg16`
  and `redis:7-alpine`, both already pulled by C002/C003); the replica stack stayed down
  throughout. The 91 tests take ~49 s, of which ~35 s is 25 harness start-ups — each integration
  test builds a fresh `WebApplication` so its ephemeral signing key and its Redis buckets cannot
  leak into another test.

- **Component:** C021 ws-registry-minimal — 2026-07-28
- **Status:** DONE — `dotnet test backend/src/Registry.Api.Tests -c Release` → **92 passed, 0 failed,
  0 skipped**. All three DoD items pass against a real Postgres (Testcontainers). Wave-0 and wave-2
  gates re-run green after the two new migrations: `bash infra/scripts/migrate-verify.sh` →
  **190/190** (was 187), `MageRide.Shared.Tests` → 161, `ApiGateway.Tests` → 524, `Iam.Api.Tests`
  → 91. `db/seed/skeleton.sql` was applied twice through `infra/scripts/seed-skeleton.sh` against a
  freshly migrated database.
- **Notes:**
  **Spec gaps — three micro-change-sets, none actioned in `specs/` (D3' §registry-svc and D4' §2
  own these).** Two are landed as migrations because the endpoints cannot meet their own contract
  without them; each file's header carries the argument.
  (a) ***US-9.6 has no storage and no endpoint*** (`0308__registry_active_vehicle.sql`). US-9.6 is
  P0 — "if a driver has registered multiple vehicles, **only one vehicle can go live at a time**" —
  and US-9.7 puts "the registration number of the vehicle currently live/online (**the single
  active vehicle selected in vehicle management**)" on the driver dashboard. Nothing in D4' §2 or
  `server_db_schema.md` §2 stores that selection and no D3' route sets it. D-03's two enforcement
  points are both *downstream* of the choice: `ux_sessions_active_driver` (0501) is the Mode A/B
  tracking plane and `dispatch.driver_presence` (0701) only exists once a driver is already online
  **with a `vehicle_id` in hand**. Something has to answer "which vehicle?" first. Added
  `registry.driver_profiles.active_vehicle_id` + `active_vehicle_selected_at` and
  `POST /v1/vehicles/{id}/select-live`. **D4' §2 should carry the columns and D3' the route.**
  (b) ***No per-service command log exists except `rides.command_log`***
  (`0307__registry_command_log.sql`). Identical to C020's finding (a), now a pattern rather than a
  one-off: D3' §0 requires `Idempotency-Key` on every POST and replays from a **per-service** log,
  the registry contract marks `POST /v1/vehicles` "Idempotent: yes", and D4' §5 prints DDL for
  `rides` only. Sharing that table would let a registration and a ride collide on one
  client-generated key. **This changes a C003 assertion:** `migrate-verify.sh` now expects **13**
  registry tables, not 12. The convention is now recorded in `db/CLAUDE.md` so C022–C044 stop
  rediscovering it.
  (c) ***`vehicle.registered` has no outbox to be written to.*** D3' says `POST /v1/vehicles` emits
  it and D6' §2.4 makes the transactional outbox mandatory for cross-service events, but neither
  DDL source declares `registry.outbox` — the same gap C004 found for `dispatch` and closed there.
  **Not created here:** nothing in the walking skeleton consumes the event (dispatch reads the
  vehicle row directly), and publishing outside a transaction to satisfy the letter of the contract
  would break the exact guarantee R-13 exists for. `UseKafka`/`UseOutbox` are off. **C028 must land
  the table and the publish together, and D4' §2 needs the DDL.**
  **Contract gaps (no change made to `backend/contracts/registry.yaml`; C028 should decide):**
  (d) *`ocrJobId` is required by the 201 of `POST /v1/vehicles` and is not returned.* No OCR is
  queued in this slice, so any value would be an identifier no service recognises and a client
  polling it would wait forever. It belongs on the responses that actually queued a job.
  (e) *The four document file ids are required by `VehicleRegistration` and are accepted-and-ignored.*
  There is no upload surface in the skeleton to obtain a ULID from (C029/C054 own it). They are
  declared on the request record so a client written against the contract still compiles.
  (f) *`VehicleSummary` has nowhere to say which vehicle is selected*, which is exactly what the
  US-9.7 dashboard renders. Added `isSelected`; `dispatchState` is also surfaced, which the
  contract has on `VehicleDetail` but not on the summary.
  (g) *Profile Setup precedes vehicle onboarding in D3' but is C029's endpoint.*
  `registry.vehicles.driver_name` is NOT NULL and is what a passenger sees (US-2.12), so
  `POST /v1/vehicles` takes `driverName` from the body and creates the minimal
  `registry.driver_profiles` row when the driver has none; a second vehicle then needs no name.
  A first vehicle with no name anywhere is refused rather than written blank.
  **Decisions —**
  (1) **The selection lives on `registry.driver_profiles`, not in a table of its own.** That row is
  already 1:1 with the driver, so its primary key *is* the "only one at a time" half of US-9.6 —
  free and unbypassable. The ownership half is a **composite FK to `registry.vehicles(id, owner_id)`**,
  which needed a (redundant-for-lookups) `UNIQUE (id, owner_id)` on `registry.vehicles`. That turns
  "a driver may only select a vehicle they own" from a repository `WHERE` clause into an invariant
  Postgres keeps; `SelectLiveTests` asserts it holds against a direct `UPDATE`.
  `ON DELETE SET NULL (active_vehicle_id)` names its column (PostgreSQL 15+) — without the list
  Postgres would try to null `driver_id`, which is the primary key. **APPROVED-ness is not
  expressible as a constraint** and is enforced in the service; **C029 must clear the selection when
  a selected vehicle is DEACTIVATED or REJECTED.**
  (2) **`car` is rejected, not rewritten (DoD).** AL-09 maps `car → sedan` as a one-time data
  migration, not an input alias — silently rewriting would hide an un-updated client until a fare
  tariff or a map marker disagreed. `bus`/`train` are *canonical* types but Mode A, so they are
  `403 mode-not-allowed` rather than `400 invalid-vehicle-type`; the 400's detail names the
  replacement so a client learns what to send.
  (3) **Registration numbers are canonicalised, not just validated** (`wp qa-1234` → `WP-QA-1234`),
  following C020's phone-normalisation precedent. `ux_vehicles_regno_active` is a unique index over
  the *stored text*, so without this D-37 is bypassed by retyping the plate — proved by
  `The_same_plate_typed_differently_is_still_a_duplicate`. A character a plate cannot contain is
  **refused rather than stripped**: deleting it would let two genuinely different plates collide.
  Whitespace of any kind is a separator, so a stray tab or newline is copy-paste noise, not a
  rejection.
  (4) **The dev approve endpoint is not mapped when it is off**, rather than answering 403 — an
  unmapped route is undiscoverable. `Registry:DevApprovalEnabled` unset means Development only;
  the replica sets it `true` explicitly because it runs synthetic data under the Production
  environment name (added to `infra/env/.env.app.example`, which D7' §4.2 does not list). It still
  requires a driver bearer that owns the vehicle — a seed path that skipped authentication would
  be the one thing here reachable without a session. **It bypasses AL-10's mandatory insurance
  document and the AL-30 step machine**, says so in its own logging, and warns at start-up whenever
  it is on outside Development.
  (5) **The seed is `db/seed/skeleton.sql`, deliberately outside `db/migrations/`.** DbUp applies
  that directory to every database including production, and this file invents an account and
  approves a vehicle with no insurance document. The distinction is now recorded in `db/CLAUDE.md`
  alongside why the §20 reference seeds stay in `19xx`. It ends with a `DO` block that raises unless
  exactly one selected, approved Mode C vehicle exists, so a half-seeded database fails the script
  instead of reporting success.
  (6) **`infra/scripts/seed-skeleton.sh` falls back to running `psql` inside
  `timescale/timescaledb-ha:pg16`** when the host has no `postgresql-client` — which this build host
  does not. Installing a client package to run a seed would be odd in a repo that already pulls that
  image for every migration, and the fallback is what let the wrapper be proved here rather than
  assumed.
  (7) **`UseRedis = false`.** This slice has no candidate index, no presence and no cache, so a Redis
  dependency would only add a readiness probe that can fail while everything here still serves.
  (8) **Tokens are minted in the test suite, not fetched from iam-svc.** registry-svc holds no
  signing key; standing a real iam-svc up would re-test C020 and make this suite fail for reasons
  that are not registry's. The claim shape matches `Iam.Api/Auth/AccessTokenIssuer` exactly, and
  **C025 is where a real iam token crosses into a real registry-svc**.
  **For C022–C025 —**
  The skeleton driver is `00000000-0000-4000-8000-00000000d001` on `+94770000001`; the vehicle is
  `00000000-0000-4000-8000-00000000c001`, plate `WP-QA-0001`, `three_wheeler`, Mode C, APPROVED and
  already selected. The seed **grants the `driver` role explicitly** — C020 decision (4) means
  opening the Driver App does not confer it, so a driver seeded without the grant signs in fine and
  is then refused by every route here.
  Two things C025 has to decide, neither guessed at here: **there is still no Dockerfile** (C020 left
  the same choice open — per-slice container vs. an early `AppServices` host), and **the gateway has
  no route for `/v1/dev/**`**, so the dev approve endpoint is reachable only by talking to
  registry-svc directly. The SQL seed needs no HTTP at all, which is why it is the path the skeleton
  should use.
  **Build host —** Docker is used by the test suite (Testcontainers `timescale/timescaledb-ha:pg16`,
  already pulled by C002/C003) and by the seed-script fallback; the replica stack stayed down
  throughout. The 92 tests take ~39 s, of which most is 30 harness start-ups — each integration test
  builds a fresh `WebApplication` so its test signing key cannot leak into another test. Redis is not
  needed by this suite at all.

- **Component:** C022 ws-ride-svc-happy-path — 2026-07-28
- **Status:** DONE — `dotnet test backend/src/Ride.Api.Tests -c Release` → **109 passed, 0 failed,
  0 skipped**. All four DoD items pass against a real Postgres *and* a real Redpanda
  (Testcontainers). Wave-0/1/2 gates re-run green after the new migration and the contract change:
  `bash infra/scripts/migrate-verify.sh` → **190/190**, spectral → **0 errors**,
  `MageRide.Shared.Tests` → 161, `ApiGateway.Tests` → 524, `Iam.Api.Tests` → 91,
  `Registry.Api.Tests` → 92.
- **Notes:**
  **Spec gaps — five micro-change-sets. Two are actioned in this repo (one migration, one
  contract); three are contract gaps left for later components to decide.**
  (a) ***The offer has no owner in the ride aggregate, and the ride cannot remember its own
  quote*** (`0608__rides_offer_and_fare.sql`). ADD §11.11 gives `rides.rides` only
  `current_offer_id` and `offer_expires_at`, so ride-svc knows an offer is live but not whose it
  is — yet `GET /v1/rides/{id}` and `/state` are exactly what the driver app reads to render the
  offer card and the 15-second countdown, and both answer `403 not-ride-participant` to a
  non-party. Without `offered_driver_id` the only choices are "every driver may read every offered
  ride" or "the offered driver may read nothing". `dispatch.offers` (0702) holds the same fact but
  belongs to dispatch-svc. Separately, `POST /v1/rides/request` **requires** `estimatedFare` in its
  202, `RideDetail.fare` and the `complete` response carry it, and R-18 makes a retry replay the
  *existing* ride — impossible if the amount lived only in the caller's `fareEstimateToken`.
  Added `offered_driver_id`, `offered_vehicle_id`, `fare_estimate_minor`, `fare_surcharge_minor`
  and `currency`. **D4' §5 and `server_db_schema.md` §5 need all five.** No table count changed, so
  `migrate-verify.sh` is still 190/190.
  (b) ***`ride-svc` is the sole writer of `rides.state`, but nothing gives dispatch-svc a way to
  move it*** (`backend/contracts/ride.yaml`: `POST /v1/internal/rides/{id}/matching` and
  `/offer`). ADD §11.11's sequence diagram draws dispatch-svc running
  `UPDATE rides SET state='Offered' …` itself; §11.12 says in the same document that "`ride-svc` is
  the **sole writer** of `rides.state`", and the C023 prompt's fence repeats it. Sole-writer wins —
  two services issuing conditional updates against one aggregate is the race R-02 exists to
  remove — so the two moves dispatch drives are commands on ride-svc. **D3' ride-svc needs both
  routes**, and ADD §11.11's diagram should show the call rather than the UPDATE.
  **Contract gaps (no change made; the named component should decide):**
  (c) *`RideDetail.counterpartyPhone` (AL-48) is never populated.* It needs an `iam.users` read
  ride-svc does not make, and the field is optional. **C032/C048** should decide whether ride-svc
  crosses that boundary or query-svc owns the projection — the same question as
  `DriverSummaryRepository`, which does cross it for `RideDetail.driver` because the passenger's
  live-ride screen has nowhere else to learn who is coming.
  (d) *`Place.address` is accepted and dropped.* `rides.rides` stores geography only and the field
  is optional in the contract, so a booking's typed address is not persisted. Deliberately **not**
  added to the migration: no rule depends on it and D4' §5 keeps presentation text off the
  aggregate. C032 should add the columns if the live-ride screen needs the string back.
  (e) *`POST /v1/rides/{id}/start` accepts an `otp` and ignores it.* The contract says a passenger
  or proxy ride "requires the rider's start OTP", but **nothing anywhere issues one** — no
  endpoint, no column (`rides.rides`'s two OTP hashes are the package pickup/delivery pair, P-07),
  and no notification. C032 must either add the issuing half or drop the field.
  Also noted, not raised: *the contract's `start` allows `Accepted | DriverArrived → InProgress`
  while ADD Appendix B.2 draws only the second edge.* The contract wins
  (`backend/contracts/CLAUDE.md`) — a driver who reached the rider without the geofence firing must
  still be able to start — and `RideTransitions` carries both.
  **Decisions —**
  (1) **The accept is ADD §11.11's conditional UPDATE and nothing else** — no advisory lock, no
  pre-flight `SELECT`, no application ordering. In particular there is **no
  `offered_driver_id = :driverId` predicate on it**: adding one would turn the concurrent
  double-accept the DoD requires ("exactly one 200 and one 409") into two 403s and move the
  guarantee out of the database. The compensating change is
  `accepted_vehicle_id = CASE WHEN offered_driver_id = :driverId THEN offered_vehicle_id END`, so a
  winner who was never offered the ride records **no** vehicle rather than somebody else's; that
  path is only reachable if an `offerId` leaks, and C023's `ux_offers_driver_live` closes it
  properly. `ConcurrentAcceptTests` races two drivers, then ten.
  (2) **`Completed` is not terminal in the domain**, even though `ux_rides_open_passenger` exempts
  it (C004 note (b)). The ride still owes a payment, so `complete` moves
  `InProgress → Completed → PaymentPending` inside **one** transaction — the ride never rests in
  Completed, which also means the window C004 warned about (the open-ride guard lifting and
  re-applying across the pair) is never observable from outside a transaction.
  (3) **One `ride.completed` event, not two.** D6' §2.2 registers no name for the fare hand-off;
  the envelope's `state` is `PaymentPending`, which is where the ride actually is by the time a
  consumer reads it. Both moves are still audited in `rides.transitions`.
  (4) **`offer.created` and `offer.declined` are emitted on `ride.events`.** D6' §2.2 lists
  `offer.created` in the `ride.events` type set and ADD §11.11 writes it to `rides.outbox`;
  `offer.declined` is named by §11.12 without a topic and rides alongside it, because ride-svc is
  what performed the `Offered → Matching` move dispatch needs to hear about. `Requested → Matching`
  emits **nothing** — dispatch drove that move itself and the registry has no name for one.
  (5) **The offer deadline is stamped from ride-svc's clock**, not taken from dispatch-svc's
  request body. It is ride-svc's `offer_expires_at > now()` that decides an accept; two clocks
  would make the boundary unfalsifiable. dispatch sends `ttlSeconds` and gets the instant back to
  mirror onto `dispatch.offers.expires_at`.
  (6) **`fareEstimateToken` is a real HMAC token, and its codec is in the kernel**
  (`MageRide.Shared.Fares`) because fare-svc signs and ride-svc verifies — that is the definition
  of cross-cutting in `backend/CLAUDE.md`. Format `mrf1.<b64url(claims)>.<b64url(hmac)>`, keyed by
  `Fare:EstimateTokenKey`, **no default** (a well-known key is one a client can mint for itself).
  The claims bind tier and trip; ride-svc rejects a token issued for another `vehicleType` or
  `kind`. C049/C050 replace what fills the token, not the format.
  (7) **`clientRequestId` accepts a ULID as well as a UUID.** ADD §11.13 has the apps generate
  ULIDs and the contract types the field `Ulid`, but `rides.rides.client_request_id` is a Postgres
  `UUID`. `Ulids.TryParse` decodes Crockford base32 to the same 128 bits, so a correct client is
  not a 400 on every booking. Nothing converts back — the value is only ever compared.
  (8) **`/v1/internal/rides/**` is guarded by a shared secret and is not mapped without one.**
  D3' §0 puts the internal family on mTLS and the gateway refuses the prefix at the edge (C008),
  but no mesh exists until C042. Unset `Ride:InternalApiKey` means the routes do not exist at all —
  404s, not an open door. `ride.yaml` declares an interim `internalKey` scheme that C042 deletes.
  (9) **Only `kind: passenger` is bookable.** Proxy needs a rider identity
  (`ck_rides_proxy_identity`) and package a size plus both OTP hashes
  (`ck_rides_package_complete`); a booking that skipped either would be refused by the database as
  a 500 rather than answered. `scheduledAt` is refused for the same reason — accepting it and
  dispatching immediately would send a driver to a passenger who asked for tomorrow (C035 owns
  scheduling).
  (10) **`UseRedis = false`.** The `lock:driver-offer:{driverId}` fast path in §11.11 is
  dispatch-svc's reservation (D5' §3.6); the authoritative accept here is pure Postgres, so a Redis
  dependency would be a readiness probe that can fail while every route still serves.
  (11) **The fare stub's `truck` / `mini_truck` rates are invented.** D5' §1.1 prints no delivery
  rates ("admin-configured, Epic 20") and `RideVehicleType` still lets a caller ask for them, so
  refusing would break the contract. Marked `PLACEHOLDER` in `FareTariff`; **no spec has been read
  as authorising those two numbers.**
  **For C023 (dispatch-svc) —**
  Consume `ride.requested` off `ride.events`, then call
  `POST /v1/internal/rides/{rideId}/matching` and `POST /v1/internal/rides/{rideId}/offer`
  (`X-MageRide-Internal-Key`, `Idempotency-Key`). `ride.requested`'s payload already carries
  `vehicleType`, `paymentMethod`, `fareEstimateMinor` and `currency` so the candidate build and the
  D6' §2.2 `offer.created` push need no second read. The offer route returns `version` and
  `offerExpiresAt` — **put the `version` on your own `dispatch.events` `offer.created`**: C013's
  note (6) records that the envelope lacks one today and that `OfferSession.accept()` pays a
  `GET /v1/rides/{id}/state` for it. `offer.declined` on `ride.events` is your cue to release the
  driver and offer the next candidate; ride-svc has already cleared `current_offer_id`,
  `offered_driver_id`, `offered_vehicle_id` and `offer_expires_at`.
  **For C037 —**
  The R-04 backstop must decide what `Offered → Matching` does with `current_offer_id`. Leaving it
  set makes ADD §11.11's second origin (`state IN ('Matching','Offered')`) reachable, and the
  accept's audit row — which records `from_state='Offered'` because nothing in this slice can
  produce a Matching ride with a live offer — starts lying. `RideTransitions` already draws the
  edge; `RideService.AcceptOfferAsync` carries the note.
  **Build host —** Docker is used by the test suite only (Testcontainers
  `timescale/timescaledb-ha:pg16` and `redpandadata/redpanda:v24.2.26`, both already pulled by
  C002/C009); the replica stack stayed down throughout. The 109 tests take ~55 s, of which most is
  ~35 harness start-ups — each integration test builds a fresh `WebApplication` so its test signing
  key cannot leak into another test.

- **Component:** C023 ws-dispatch-stub — 2026-07-28
- **Status:** DONE — `dotnet test backend/src/Dispatch.Api.Tests -c Release` → **78 passed, 0 failed,
  0 skipped**. All four DoD items pass against a real Postgres, a real Redis *and* a real Redpanda
  (Testcontainers), with a real ride-svc standing beside dispatch-svc rather than a fake. Wave-0/2
  gates re-run green after the new migration, the contract route and the TestKit change:
  `bash infra/scripts/migrate-verify.sh` → **190/190**, spectral → **0 errors**,
  `MageRide.Shared.Tests` → 161, `ApiGateway.Tests` → 524, `Iam.Api.Tests` → 91,
  `Registry.Api.Tests` → 92, `Ride.Api.Tests` → **117** (was 109; `InternalOfferExpiryTests` covers
  the route this component added to ride-svc).
- **Notes:**
  **Spec gaps — eleven micro-change-sets. Two are actioned in this repo (one migration, one
  contract route); the rest are gaps recorded for the component that owns the affected spec.**
  (a) ***The R-04 backstop has nothing to call*** (`backend/contracts/ride.yaml`:
  `POST /v1/internal/rides/{rideId}/offer/expire`). ADD §11.11's durable-backstop paragraph has the
  Quartz job "transition the ride back to `Matching`" itself, but §11.12 in the same document makes
  ride-svc the **sole writer** of `rides.state` and this prompt's fence repeats it. C022 opened the
  internal family for exactly this reason and left two routes; expiry is the third. The `UPDATE` is
  bound to `offer_expires_at <= now()` **evaluated by Postgres** — the negation of the predicate
  that decides an accept — so a sweeping node whose clock ran ahead is answered `409` and cannot
  take a window away from a driver who is still inside it. It also clears `current_offer_id`, which
  answers the question the C022 handoff left open for whoever landed R-04: leaving it set makes
  §11.11's second accept origin (`state IN ('Matching','Offered')`) reachable and the accept's
  `from_state='Offered'` audit row start lying. **D3' ride-svc needs the route.**
  (b) ***`dispatch.command_log` does not exist*** (`0710__dispatch_command_log.sql`). The third
  instance of the same gap — iam (0104, C020), registry (0307, C021). D3' §0 requires an
  `Idempotency-Key` on every POST and replays from a **per-service** log; `dispatch.yaml` declares
  the header on four POSTs. D4' §5 still prints DDL for `rides` only, and sharing that table would
  let a driver going online and a passenger booking collide on one client-generated key. **This
  changes a C004 assertion:** `migrate-verify.sh` now expects **13** dispatch tables, not 12.
  **D4' §5 should print one command-log table per service with idempotent POSTs.**
  (c) ***`offer.declined` and `offer.expired` identify neither the driver nor the offer.***
  D6' §2.2's `ride.events` payload has `driverId`, and §11.12 makes `offer.declined` dispatch's cue
  to release the driver — but ride-svc builds the envelope from the row **after** the update, and
  both moves clear `offered_driver_id` and `current_offer_id`, so both fields are absent. Nothing is
  lost here (`dispatch.offers` already knows, and `ReleaseLiveOfferAsync` is keyed by ride), but a
  consumer that is not dispatch-svc cannot act on either event. **Either D6' §2.2 should say the
  fields are absent on these two types, or ride-svc should build the payload from the pre-update
  row.** `RideEventEnvelopeTests` pins the current behaviour so the choice is deliberate.
  (d) ***D5' §3.1's `searchRadius` has no value anywhere.*** The line reads
  `near = [d in raw where ST_DWithin(d.geo, pickup, searchRadius)]` and no spec — D5', ADD §7.4,
  the URD — ever gives the number. **`Dispatch:SearchRadiusM` defaults to 5000**, chosen to sit
  inside the res-5 ring(2) reach (~40 km) so the pre-filter stays genuinely coarse, and above the
  ~3 km passenger live-map view (R-06). **No spec has been read as authorising 5 km.**
  (e) ***D6' §2.2's `dispatch.events` offer envelope carries no `version`,*** which forces the KMP
  `OfferSession.accept()` to spend a `GET /v1/rides/{id}/state` inside a 15-second window just to
  learn the number the ADD §11.11 accept demands (C013 note 6; the C022 handoff asks C023 to fix
  it). **Added**, as the ride version at the moment the offer was armed — which is exactly the
  `expectedVersion` the accept wants. **D6' §2.2 should print it.**
  (f) ***D3' §route table's `{state:online}` is not a value the contract allows.***
  `dispatch.yaml`'s `PresenceState` enum is `OFFLINE|AVAILABLE|OFFERED|ON_RIDE`, mirroring the
  `dispatch.driver_presence` CHECK (C004). The contract wins (`backend/contracts/CLAUDE.md`) — a
  driver who is `OFFERED` or `ON_RIDE` has no way to say so in a two-value vocabulary. **D3' should
  print the enum.**
  (g) ***`409 driver-already-live` on `POST /v1/standby/online` has no stated trigger.*** It is in
  the operation's `x-error-codes` and the description says going online on a second vehicle
  *overwrites* `vehicleId`, which is not a 409. Read as: switching vehicles while `OFFERED` or
  `ON_RIDE` is refused — that would silently rewrite which vehicle is serving a live ride,
  including the plate the passenger is watching for. Going online again on the **same** vehicle, or
  on a different one while merely `AVAILABLE`, is the overwrite the description describes.
  (h) ***`rides.timers` rows of kind `offer_expiry` are written by dispatch-svc.*** The table is
  ride-svc's by name; the job is dispatch's by every spec that names an owner — ADD §6 gives
  dispatch-svc "Quartz.NET (scheduled rides **+ offer backstop**)", D5' §3.5 files the durable
  backstop under *Offer TTL & cascade*, and this component's deliverable list says so outright.
  Nothing here touches `rides.rides`. **If the schemas are ever split across databases the timer
  has to move to `dispatch.timers`** (0708 already exists for the DT-04 case) and `ck_timers_kind`
  in 0605 becomes the only place `offer_expiry` is spelled.
  (i) ***ADD §6/§11.11 specify Quartz.NET clustered; this is a leased poll.*** What R-04 actually
  requires is "fires ≤1 s after expiry independent of Redis", which one indexed
  `UPDATE … WHERE id IN (SELECT … FOR UPDATE SKIP LOCKED)` per half-second gives with the same
  multi-replica safety and none of Quartz's schema, clustering protocol or second scheduler.
  **C034/C037 should decide** when the scheduled-ride and no-show timers arrive and there is a
  second and third caller to justify the dependency.
  (j) ***D-07 needs a Redis setting no spec mentions.*** Redis publishes no keyspace events by
  default, and `CONFIG SET notify-keyspace-events` needs an admin connection the kernel's
  multiplexer deliberately does not open (nor does a managed Redis allow it). Added
  `--notify-keyspace-events Ex` to `infra/docker-compose.dev.slim.yml` **and** to
  `MageRide.TestKit`'s `RedisFixture`, so the fixture and the compose file do not disagree about
  whether the path is reachable. **D7' §3's Redis command line needs the same flag**, and the
  replica's Container 4 with it.
  (k) ***`.env.app.example`'s `Dispatch__Outbox__*` keys bind nothing.*** The file is one flat
  namespace shared by every co-located service (its own header says so), so the `Dispatch__` prefix
  C009 used to avoid clobbering ride-svc's `Outbox__Channel=ride_outbox` also stops the kernel from
  ever reading them. `DispatchApplication` sets the three values in code instead, so the service is
  correct either way; the keys are kept and the reason is now written beside them.
  **Decisions —**
  (1) **The H3 pre-filter reads whole cell sets and applies no radius at all.** D5' §3.1 offers
  "PostGIS / GEOSEARCH BYRADIUS" for the exact post-filter; a `GEOSEARCH BYRADIUS` on the Redis side
  as well would make the mandatory post-filter look optional to the next person to read the code.
  So `PreFilterAsync` is `SortedSetRangeByRank` over the 19 keys and nothing else, and
  `ST_DWithin` on `dispatch.driver_presence` (ADD §6, D-06) is the only thing that decides. The
  DoD's fourth item is proved by a driver **in the pickup's own res-5 cell, 15 km away**: same Redis
  key as the driver 70 m away, `PreFilterCount = 1`, `CandidateCount = 0`. A second test widens
  `SearchRadiusM` to 20 km and the same driver comes back, which is what pins the decision to the
  post-filter rather than to H3.
  (2) **The cascade is entirely event-driven and needs no cross-service read.** Every `ride.events`
  envelope carries the whole payload, so `offer.declined` / `offer.expired` already contain the
  pickup, the tier, the payment method and the fare — enough to run the next round without
  dispatch-svc ever reading `rides.rides`. That is what keeps R-01's boundary honest: this service
  reads and writes nothing in the `rides` schema except its own `offer_expiry` timers.
  (3) **Ordering: Lua lock → (`dispatch.offers` + `rides.timers` + presence, one transaction) →
  ride-svc → (realign + `dispatch.outbox`, second transaction).** The backstop is armed *before*
  ride-svc is called, deliberately: a process that dies in between leaves a timer for an offer the
  ride never got, which the sweep expires, is answered 410, settles and frees the driver. The other
  order leaves a ride `Offered` with nothing watching the deadline. A ride-svc refusal unwinds
  everything (`UnwindAsync`) rather than holding the driver.
  (4) **Timers are leased, not marked fired on claim.** Holding the claim's row lock across the
  expiry would deadlock against the expiry's own `rides.timers` writes on another connection;
  marking `fired_at` on claim would let a worker killed mid-expiry take the ride's only backstop
  with it. One statement pushes `fire_at` out by `Dispatch:TimerLease` (30 s) and returns the rows.
  (5) **The `ride.events` consumer lives in `Dispatch.Api`, not in the kernel.** C002 shipped a
  producer and no consumer, and `backend/CLAUDE.md` says cross-cutting code belongs in
  `MageRide.Shared` — but a consumer promoted there today would be untested code in the kernel with
  one caller. It reads the kernel's `KafkaOptions` so a deployment still configures one broker in
  one place. **C024/C034/C039 should promote `RideEventConsumer` once there is a second caller.**
  (6) **No dedupe table.** D6' §2.3 makes delivery at-least-once and says consumers key on
  `eventId`; every action here is idempotent by construction instead — deterministic
  `Idempotency-Key`s on the three ride-svc commands (`dispatch-{operation}-{subject}`, so a retry
  after a timeout replays rather than issuing a second command) and conditional `UPDATE`s guarded
  on the status they expect. **C034 must revisit if it adds an action that is not naturally
  idempotent.** There is also **no DLQ** (D6' §2.3): an unparseable envelope is logged and
  committed, a failing handler is retried by not committing — which stalls its partition, loudly,
  rather than losing a booking.
  (7) **`dispatch.candidate_scores` is written at `dispatch_algorithm_version = 0`,** for every
  candidate considered rather than only the winner (R-11). The fence forbids weighted scoring, and
  version 0 is how a later audit of a version-0 decision is told apart from a scoring bug; the
  breakdown names the algorithm (`nearest-only`) in as many words. **C034 lands version 1.**
  (8) **`ExpiredNoDriver` is not implemented.** D5' §3.5's 120-second global timeout (US-6A.11) is a
  ride *state*, and no route exists to write it — C032 owns `system-cancel` and C034 the timeout.
  The cascade stops at `Dispatch:MaxOfferRounds` (8) and the ride stays in `Matching`, which is the
  honest answer: the passenger sees "searching", not a driver who is not coming.
  (9) **`pocketken.H3` 4.5.0.1**, a managed port of the same H3 v4 algorithms `com.uber:h3` gives
  the KMP module (C017) — that library is JNI with no .NET binding. It ships a `net10.0` lib and
  depends only on `NetTopologySuite`, already pinned. Cell ids are asserted against **known-good v4
  values** rather than against whatever the library returns, because a port that disagreed by one
  bit would not fail loudly: it would produce an empty candidate set and a map with no vehicles on
  it. `H3GridTests` also pins that res-5 is the res-7 passenger cell's ancestor, which is what lets
  one position feed both indexes.
  (10) **The GPS-freshness gate reads the durable row, not the Redis TTL.**
  `driver:availability:{driverId}` has R-08's 60 s TTL but **nothing refreshes it in this slice** —
  position-processor-svc (C039) owns the heartbeat — while `dispatch.driver_presence` has no TTL at
  all. So D5' §3.2's "GPS sample older than 2×expectedInterval" is enforced as
  `last_seen_at >= now() - Dispatch:PresenceTtl` inside the post-filter, where it cannot be lost.
  (11) **The hard gates this slice does *not* have are absent rather than stubbed.** No wallet gate
  (D-08), no Driver Level, no `reputation.block_state` (D-04), no `safety.blocked_drivers`, no
  package-size compatibility (P-11), no `DISPATCH_SUSPENDED` (E-03), no Directional predicate
  (DT-02). A gate that always passes reads like a gate that works; `CandidateRepository` names all
  seven and says whose they are.
  (12) **The test suite runs a real ride-svc beside dispatch-svc.** Everything this component has to
  prove lives in the seam — who may write `rides.state`, whose clock stamps `offer_expires_at`,
  whether `offer.created` can precede a commit — and a stubbed ride-svc would agree with dispatch
  about all three by construction. The harness resets `dispatch.*`, the `offer_expiry` timers and
  the Redis keyspace per test, and **drains `rides.outbox`**: most tests run with ride-svc's
  dispatcher off, so their `ride.requested` rows sit undispatched and the first test to turn the
  dispatcher on would otherwise replay a backlog of finished rides and reserve its own driver.
  **For C024 (ws-realtime-pipeline) —**
  `offer.created` on `dispatch.events` is the driver's push, and it is committed before it exists
  (R-13). The envelope is D6' §2.2's plus `version` (gap (e)); `expiresAt` on it is **ride-svc's**
  instant, so a countdown rendered from it agrees with the accept. `RideEventConsumer` is the
  pattern to copy and the thing to promote into `MageRide.Shared.Messaging` (decision 5).
  **For C025 (ws-e2e-android-slice) —**
  The skeleton driver from C021's seed needs `POST /v1/standby/online` with a position before any
  ride can be offered — presence is not implied by the vehicle being selected. The gateway already
  routes `/v1/standby/**` to `dispatch-svc` (C008), and both new services still have **no
  Dockerfile** (C020/C021 left the same choice open). `Dispatch:RideServiceInternalKey` must equal
  ride-svc's `Ride:InternalApiKey` or the skeleton silently books rides no driver is ever offered.
  **For C034 (dispatch-svc-core) —**
  Everything in fence-list order is listed in `backend/src/Dispatch.Api/CLAUDE.md`. Three that are
  easy to miss: `RideEventConsumer` has no DLQ, the `ExpiredNoDriver` timeout has no route to write
  it, and `dispatch.driver_presence.state` is currently moved by dispatch alone — once
  position-processor-svc (C039) starts refreshing `driver:availability:{driverId}`, the two writers
  have to agree on who owns the transition.
  **Build host —** Docker is used by the test suite only (Testcontainers
  `timescale/timescaledb-ha:pg16`, `redis:7-alpine` and `redpandadata/redpanda:v24.2.26`, all
  already pulled by C002/C009); the replica stack stayed down throughout. The 78 tests take ~80 s,
  of which most is ~40 harness start-ups — each integration test builds a fresh ride-svc *and*
  dispatch-svc so a test signing key or a background worker cannot leak into another test.

- **Component:** C024 ws-realtime-pipeline — 2026-07-28
- **Status:** DONE — `dotnet test backend/src/HotPath.Tests -c Release` → **35 passed, 0 failed**
  (~110 s). All four DoD items assert directly. **p95 EMQX → passenger's SignalR group = 2102 ms**
  over 20 positions published a second apart (median 1352 ms, max 2184 ms), against the 5 s SLO and
  at the **shipped** 2 s batch interval, which dominates the number — the DoD says p95, so the test
  measures one rather than timing a single observation. Two mqtt-bridge replicas share
  `$share/posGroup/…` with **exactly one** copy on `telemetry.raw` and both take a share; a device
  publishing to another vehicle's topic is **disconnected by the deployed ACL**; the passenger joins
  **exactly 19** res-7 + ring(2) cells. Full solution: **1158 passed, 0 failed** — Dispatch's 64
  confirm the two kernel promotions below changed no behaviour.
- **Notes:**
  **Micro-change-sets raised —**
  (a) *`infra/env/.env.app.example`'s `Emqx__SharedSub` is replaced by a group NAME.* C009 wrote the
  whole filter (`$$share/posGroup/veh/+/pos/live`, needing `$$` to survive compose interpolation).
  The service now takes `MqttBridge__LiveShareGroup=posGroup` and builds the filter itself. This
  removes the two ways E-08 breaks silently: a filter that lost its `$share/` prefix makes every
  replica receive every message — one copy per replica on `telemetry.raw`, no error anywhere — and
  a filter pointed at the wrong topic subscribes successfully to nothing. **D7' §4.2's
  `Emqx__SharedSub` row should follow.** Also renamed: `Mqtt__BrokerUrl` → `Mqtt__Host`/`__Port`,
  `Mqtt__DevJwtSecret` → `Mqtt__SessionTokenSecret`, and the replay group is `posReplayGroup`, not
  C009's `replayGroup`. C009 flagged that whole block as invented-before-use.
  (b) *`Mqtt__ServiceUsername` was defined twice in `.env.app.example`* — once for mqtt-bridge and
  once for tcp-adapter. `env_file` is one flat map, so the tcp-adapter value won for every container
  reading the file, and the compose `hot-path` service sets it again in `environment:` purely to
  undo that. The bridge now reads `MqttBridge__ServiceName`; **C043 should give tcp-adapter its own
  prefixed key** rather than rely on ordering.
  (c) *`SignalR__BackplaneRedis` and `Geocell__Res` are gone, for opposite reasons* — see decision 3
  and R-06. Neither is a setting this platform should have.
  (d) *ADD §7.5.1 / D5' §5.2 vs `mqtt-topics.md` §2.2 still print the `setPosRate` hint two ways*
  (top-level `intervalMs` vs `args.seconds`). Not touched here — C017 already recorded it and the
  downlink is C038's — but it is still open.
  **Decisions —**
  (1) **Two things were promoted into `MageRide.Shared`, both because C024 was the second caller.**
  `H3Grid` moved out of `Dispatch.Api/Domain` into `MageRide.Shared.Geo` beside a new `GeoCells`
  holding the resolutions: dispatch keys its candidate index at res 5 and the fan-out plane keys
  `cell:{h3index}` at res 7, and two copies of a grid whose ids must be *bit-identical* to the KMP
  module's is exactly the drift that shows up as an empty map. `RideEventConsumer`'s consume loop
  became `MageRide.Shared.Messaging.KafkaTopicConsumer` — the promotion C023's own handoff asked
  C024 to make. `Dispatch.Api.Tests/Domain/H3GridTests.cs` moved to `MageRide.Shared.Tests/Geo/`
  with it (dispatch 78 → 64 tests, the kernel 221 → 235).
  (2) **`AutoOffsetReset.Latest` on `telemetry.raw`, alone on the platform.** dispatch-svc reads
  `ride.events` from the earliest offset because a booking committed while it was down still has to
  be dispatched. A position is not like that: `geo:live` and `cell:{h3index}` are a *current-state*
  index, and a processor that woke after ten minutes down would replay ten minutes of stale samples
  and push every one to passengers as current, oldest last. History is Timescale's (T-06).
  `PositionProcessor:StartFromEarliest` reverses it; only the test harness sets it, and it is there
  so a test never races its consumer's group assignment.
  (3) **fanout-svc has NO SignalR backplane, and that is a correctness decision, not an omission.**
  Every replica reads the cell streams it has members in and pushes to its own local group, so
  coverage is already complete; a Redis backplane on top would re-broadcast each replica's send to
  every other replica and a passenger would get one copy of every frame *per replica*. D6' §5's
  backplane earns its place for the **directed** sends C041 owes — `ShareRevoked`'s targeted
  removal under 200 ms (D-22), `RideStateChanged`, `DriverPosition` — where the replica holding a
  connection is unknown. **C041 must add it for those and keep the per-cell batches off it.**
  (4) **A cell's stream read position is fixed at JOIN, not on the pump's first tick**, and this was
  a real bug found by the end-to-end test rather than a precaution. Resolving it on the first tick
  advances past everything written between the join and that tick and sends nothing, because a batch
  with no frames is not a batch — a 2-second hole at exactly the moment a passenger opens the map.
  Related: **`$` is never used as a stream position.** A non-blocking `XREAD` from `$` resolves to
  the stream's last id and so always returns nothing — a pump that appears to run and never delivers.
  (5) **`Fanout:JoinSeedFrames` (32) is scope I added deliberately and C041/C042 should remove.**
  `signalr-hub.md` §1.1 makes `GET /v1/nearby` (query-svc, C042) the snapshot path and says the
  socket carries only deltas. Until C042 exists, a passenger who opens the map sees *nothing* until
  each nearby vehicle's next sample, which is indistinguishable from a broken map — and C025's
  Android slice opens exactly that map. The seed replays the tail of each joined cell to the
  **joining connection only**, so it is bounded and costs the group nothing. Two snapshot paths is
  one more than the contract has; delete it when `/v1/nearby` lands.
  (6) **The MQTT session JWT is minted in the kernel** (`MageRide.Shared.Mqtt`) as HS256 against
  EMQX's shared secret, with the D6' §3.2 claim set (`vehicleId`, `deviceId`, `rideId?`, TTL
  `max(ride + 2 h, 4 h)`). C030 replaces the *signature* with RS256 over provisioning-svc's JWKS and
  nothing else — the claims are already right, and `emqx.conf` already carries the commented JWKS
  block. Anything holding the dev secret can mint a token for any vehicle, which is why it does not
  survive into the replica.
  (7) **The bridge decodes nothing and keys on the topic, never the payload.** EMQX authenticated
  the topic; the payload is self-asserted. position-processor rebinds a sample whose `vehicleId`
  disagrees with its topic and logs it — trusting the payload would undo the ACL, and a bridge that
  parsed payloads would drop a sample it merely failed to understand before anyone could see it on
  `telemetry.raw` and find out why.
  (8) **`telemetry.raw` acknowledgement is manual and follows the produce.** MQTTnet acks on handler
  return, which would make EMQX → Redpanda at-most-once. An unproducible payload is left
  unacknowledged and EMQX redispatches it to another group member when the session ends.
  (9) **`EmqxFixture` bind-mounts the deployed `infra/deploy/emqx/*.conf`**, copied into the test
  output by the TestKit csproj, and throws if they are absent — EMQX's shipped defaults allow every
  topic, so a fixture that silently found no configuration would turn every ACL assertion green for
  the worst possible reason.
  **Spec gaps —** (i) **No spec pins `veh:seq:{vehicleId}`'s TTL.** `mqtt-topics.md` §5 gives the
  tracker a 50,000-sample flash ring and no expiry for the watermark; 24 h is chosen and marked.
  (ii) **R-09's priority half is not implemented** — live preempting replay 4:1 needs broker-side
  priority the C009 EMQX configuration does not set, and faking it client-side would throttle replay
  without protecting live. The *separation* (two share groups) is in place. (iii) `telemetry.normalized`
  is written and **nothing reads it** — D6' §2.1 registers persistence-writer, trip-state and
  fleet-health as its consumers and none exists. That is the right way round: C040 finds the data
  already there. (iv) No `<topic>.dlq` anywhere (D6' §2.3); `KafkaTopicConsumer` commits past a
  poison message and stalls a partition on a retryable failure, which is loud rather than lossy.
  **For C025 (ws-e2e-android-slice) —**
  The passenger half of the live map is ready: connect to `/hubs/live` with the **API access token
  in the `access_token` query parameter** (never the MQTT token, E-02), call
  `JoinGeocells(GeoCells.ViewCells(here))` — 19 res-7 ids — and handle `VehiclePositions`. The
  driver half publishes **CBOR** `PositionSample` to `veh/{vehicleId}/pos/live` with an MQTT session
  JWT whose username **is** the vehicleId. `POST /v1/auth/mqtt-token` (iam.yaml) is **not
  implemented** — C026 owns it — so C025 must either mint the token itself against
  `Mqtt:SessionTokenSecret` or add that route; `MqttSessionTokenIssuer` is the piece to call either
  way. **Still no Dockerfiles**, and the compose paths do not match the landed projects:
  `docker-compose.dev.yml` expects `backend/src/HotPath/Dockerfile` (a combined
  bridge + processor + persistence-writer + fleet-health container, C038–C044) and
  `backend/src/Fanout/Dockerfile`, against `HotPath.MqttBridge` / `HotPath.PositionProcessor` /
  `Fanout.Api` on disk. Whoever writes them reconciles the two.
  **For C038/C039/C040/C041 —** each service's `CLAUDE.md` lists what was deliberately left out in
  fence order. Three that are easy to miss: fanout fans out **every** vehicle (no D-22/D-23
  visibility filter at all, so nothing here claims to implement D-22); position-processor does
  **not** refresh `driver:availability:{driverId}`, so C023 decision 10 still holds; and
  `KafkaTopicConsumer`/`MqttBridgeWorker` have no DLQ.
  **Build host —** Docker is used by the test suite only. `emqx/emqx:5.8` was already pulled by
  C009; no new images. The replica stack stayed down throughout. The 34 tests take ~70 s, most of it
  harness start-ups — the end-to-end tests build five processes (two brokers' clients plus three
  services) per test so a background pump or consumer cannot leak into another test. MQTTnet
  5.2.0.1603 and the `Testcontainers` base package are the only new NuGet dependencies.

- **Component:** C025 ws-e2e-android-slice — 2026-07-28
- **Status:** DONE — **the walking-skeleton milestone is reached.**
  `bash e2e/walking-skeleton/run.sh` brings the stack up from nothing and all four DoD items assert:
  a booked ride reaches **PaymentPending**; the driver's live position reaches the **booking
  passenger's** SignalR group through real EMQX → mqtt-bridge → Redpanda → position-processor →
  `cell:{h3index}` → fanout; an ignored offer **expires at 15 s and the ride re-enters Matching**;
  and the passenger joins **exactly 19** res-7 + ring(2) cells. Both apps assemble
  (`:apps:driver-android:assembleDebug`, `:apps:passenger-android:assembleDebug`). The wave-1 KMP
  gate is green again — see decision 6.
- **Notes:**
  **Two things this component had to build that its prompt does not list, because nothing else had —**
  (i) *the stack itself.* Only `api-gateway` had a Dockerfile; `infra/docker-compose.dev.yml`'s
  `app-services` / `hot-path` / `fanout` containers have never been buildable. C010's parameterised
  `infra/docker/Dockerfile.service` builds all nine services unchanged, so the new file is
  `infra/docker-compose.skeleton.yml` — slim infrastructure plus eight services behind the gateway.
  (ii) *a way to drive it.* See decision 1.
  **Decisions —**
  (1) **`:shared` gained a `jvm()` target and the e2e is a Kotlin/JVM program on top of it.** The
  alternative — an Android instrumentation test — cannot run on this headless box and would never
  run in CI either, and a curl script would prove the backend while leaving the *app contracts*
  untested. The harness now drives the same api-client, the same `LiveHub` names and the same
  `MqttTopics`/`PositionCodec` the two shells do. Four JVM `actual`s were needed and each is honest
  about what a server is not: an in-memory `SecureStore` (no keystore), an attestation provider that
  always returns `null` (D-30's "fail soft, never fake"), and a database driver that **rejects** a
  passphrase rather than silently opening an unencrypted file. `platformH3Grid` and
  `secureRandomBytes` moved into a new `jvmShared` intermediate source set — they were identical on
  both targets, and H3 cell ids that drifted would show up as an empty map, not an error.
  (2) **Per-service containers, not the `app-services` single process** D7' §2.1 and the replica draw.
  This is a Wave-2 arrangement and not a decision about production: folding five services into one
  process is blocked on the kernel, not on effort — ride-svc needs `Outbox:Schema=rides` while
  dispatch needs `dispatch`, and iam/registry/dispatch each need a different `CommandLog:Schema`.
  Both are singleton `IOptions<T>`, so one process can hold exactly one of each. Keying them per
  service is a C002 change and outside this component's fences. `docker-compose.dev.yml` is left
  untouched as the contract C125 builds `app-services` against.
  (3) **The e2e drives TWO rides, and both the split and the order are forced by the platform.** A
  ride whose offer was ignored can never be offered to that driver again (the `NOT EXISTS` against
  `dispatch.offers` in C023's `CandidateRepository`, D5' §3.5), so the expiry cannot be shown on the
  ride that gets completed; and the expiry ride must come **first**, because `PaymentPending` is not
  terminal and a driver who has just completed a ride still holds an active one (R-02) and correctly
  gets no further offers. Two passengers, for the same reason.
  (4) **`run.sh` tears down `--volumes`.** Not tidiness: `POST /v1/rides/{rideId}/cancel` does not
  exist yet (C022 shipped the happy path, cancellation is C035), so a run that dies mid-ride leaves a
  ride the platform offers **no way to clear** and every later run for that driver fails at the
  offer. The passenger sidesteps it by being a new account each run; the driver cannot, being seeded.
  (5) **The apps claim no wireframe id and say so in their own `CLAUDE.md`.** No MapLibre (the
  "live map" is a list), no trilingual resources, no Koin, no navigation, no foreground service.
  What *is* real is the wiring: 19 cells computed by `:shared`, `LiveHub` names taken from `:shared`,
  CBOR payloads through `:shared`'s `PositionCodec`, and the offer countdown rendered from ride-svc's
  `offerExpiresAt` rather than a local timer.
  (6) **The wave-1 KMP gate had been red since C022/C023 and is now green.** They added
  `markRideMatching`, `placeRideOffer` and `expireRideOffer` to `ride.yaml` without the matching
  typed client, so `ContractCoverageTest` counted 179 operations against a hard-coded 176 and
  `ApiOperationTableTest` was three rows short. Fixed additively: three DTOs, three `RideApi`
  functions, three `ApiOperations` rows, and the two counts. 817 tests pass, detekt and ktlint clean.
  **Gaps found, all recorded at the point of the workaround —**
  (a) **No REST response returns an `offerId`.** A driver accepts with a body that requires it, and
  neither `RideDetail` nor `RideStateSnapshot` carries one — only `offerExpiresAt`. It reaches a
  handset on the `offer.created` push (dispatch outbox → `dispatch.events` → notification-svc C051 as
  FCM, → fanout-svc C041 as a socket event) and **neither exists yet**. The harness reads the Kafka
  topic those two will read; the driver shell, which cannot, asks the user to paste it.
  **Recommend adding `offerId` to `RideStateSnapshot`** — `signalr-hub.md` §1.1 already makes REST
  the snapshot path, and without it a driver who misses the push can never accept.
  (b) **Nothing refreshes driver presence.** D5' §3.2 drops a candidate whose `last_seen_at` is older
  than `Dispatch:PresenceTtl` (60 s) and R-08 gives that heartbeat to position-processor-svc, which
  C024 deliberately left to C039. So a driver ages out of the pool a minute after going online and
  the cascade finds nobody — a ride sitting in `Matching` with a driver parked fifty metres away. The
  harness re-asserts presence every 15 s; delete that the day C039 lands.
  (c) **`POST /v1/auth/mqtt-token` is not implemented** (C020 left it to C026), so nothing can hand a
  device its MQTT credential. The harness mints one against EMQX's shared secret; the driver shell
  asks for it. `:shared`'s `MqttSessionTokenManager` is already written against that endpoint.
  (d) **`:shared:assemble` is broken and predates this component.**
  `compileCommonMainKotlinMetadata` rejects the three `expect class`es that inherit an interface
  (C014/C018). It reproduces on a pristine checkout; the wave-1 gate never runs `assemble`, which is
  why it went unseen. Nothing in the skeleton needs it — apps resolve the Android variant, the
  harness the JVM one — but **`assembleXCFramework` does**, so C085/C094 will hit it. Not fixed here:
  the repair touches iOS actuals this host cannot link.
  **Micro-change-sets raised —**
  `.env.common.example`'s `ConnectionStrings__Postgres` authenticates as `mageride`, but the slim
  stack's Postgres and PgBouncer both run as `postgres` — every service is refused by SCRAM;
  `Jwt__JwksUrl` points at `app-services` on `/v1/internal/iam/.well-known/jwks.json`, while the
  landed endpoint is `/.well-known/jwks.json` on iam-svc. Both are overridden in the skeleton compose
  with the reason inline. `androidCompileSdk` stays **36**: `androidx.core:1.19.0` demands 37, which
  is not a published platform, so the catalogue pins **1.18.0** — the newest whose AAR metadata still
  says `minCompileSdk=36`. This is the confirmation C001 asked C067 to make.
  **For C026 (iam-svc) —** implement `POST /v1/auth/mqtt-token` and gap (c) closes for both the app
  shell and the harness. **For C039 —** the presence heartbeat closes gap (b). **For C034/C041/C051 —**
  gap (a) is the one that blocks a real Driver App.
  **Build host —** the Android SDK is now installed (`platforms;android-36`, `build-tools;36.0.0`);
  `local.properties` points at `/opt/android-sdk`. **`platforms;android-37` does not exist** in the
  Google repository yet. Nine service images (~340 MB each) plus the slim stack fit alongside a build;
  the replica stayed down. A full `run.sh` takes ~6 minutes, most of it the image build and the
  15-second offer window it deliberately waits out.

- **Component:** C026 iam-svc-auth — 2026-07-28
- **Status:** DONE — `dotnet test backend/src/Iam.Api.Tests -c Release` → **209 passed, 0 failed,
  0 skipped** (91 → 209; 118 new). All four DoD items pass against a real Postgres and Redis
  (Testcontainers). Gates re-run green after the new migration: `bash infra/scripts/migrate-verify.sh`
  → **195/195** (was 190), `MageRide.Shared.Tests` → 235, `ApiGateway.Tests` → 524,
  `Registry.Api.Tests` → 92, `Ride.Api.Tests` → 117, `Dispatch.Api.Tests` → 64, `HotPath.Tests` → 35.
- **Notes:**
  **Spec gaps — four micro-change-sets, all landed as `0107__iam_portal_credentials.sql`, none
  actioned in `specs/` (D4' §1 owns the iam DDL).** AL-07 gives the Admin Portal password-or-Google
  and the Fleet Portal email/Google/Apple, and AL-37 replaces the removed MFA step with a
  failed-attempt lock-out — and **the schema has nowhere to put any of it.** Each is argued in the
  file header.
  (a) ***No password is storable.*** `iam.users` has `email` and no verifier, so two of the four
  sign-in surfaces the ADD lists are unimplementable as printed. Added `iam.user_credentials`, a
  1:1 side table so reading a profile never reads a verifier and an app account simply has no row.
  (b) ***The AL-37 lock-out has no counter.*** It is named as one of the three controls
  compensating for the removed second factor and D3' maps it to `423 otp-locked`, but nothing
  counts. `failed_attempts`/`locked_until` live on the credential. **Deliberately not Redis:** a
  flush would hand an attacker a clean slate on every internal account at once, which is the
  guarantee the control exists to give.
  (c) ***A Google or Apple identity has nowhere to bind.*** Matching on `iam.users.email` alone
  would mean anybody who can get a provider to assert an address owns the account. Added
  `iam.federated_identities`, unique on `(provider, subject)`.
  (d) ***A portal session cannot be stored.*** `iam.sessions.app` was `CHECK (passenger|driver)`
  and `iam.devices.platform` `CHECK (android|ios)`, so a browser sign-in had **no legal row** —
  yet ADD §12.1 issues portals the same RS256 + opaque refresh pair. Widened to add `admin`/`fleet`
  and `web`. The AL-08 partial unique index is untouched, and that is the point: it now also means
  "one live Admin Portal session per person", which is exactly the **session binding** AL-37 keeps.
  Also closes C020 gap (e): `iam.otp_attempts.fcm_token` parks the push token until verify creates
  the device row it belongs on.
  **Decisions —**
  (1) **One session shape for four surfaces.** `iam.sessions.app` became the *surface*, not the
  app, so portal sign-in reuses issue/rotate/revoke wholesale and every consumer downstream sees
  one kind of token. The claim set is fixed: `sub`, repeated `role`, `fleet_role`+`fleet_id` when
  there is a fleet membership, `device_id`, `app`, `jti`. `MageRideApps` in the kernel grew
  `Admin`/`Fleet` to match — the only shared-kernel change in this component.
  (2) **A browser's `device_id` is derived from its user agent**, not its address. The portal
  sign-in bodies carry no `deviceId` (the contract's schemas are `{email,password}` and
  `{idToken}`) but every session needs one. Hashing the address instead would sign an admin out
  when their laptop hands off from Wi-Fi to a hotspot. That makes the binding coarse — it separates
  browsers, not people — which is the honest description of a cookie-less server-side binding.
  (3) **No portal sign-in creates an account.** Internal roles are provisioned by a Super Admin
  (AL-06) and fleet users by their owner (AL-03), so an unknown email or an unlinked Google subject
  is a `403`, never a first sign-in. This is the one place the portal flow diverges from the app
  flow, where a first OTP verify does create the account.
  (4) **The lock-out gates the password path only.** Locking Google sign-in from failed *password*
  guesses would hand an attacker a denial of service against an admin who never uses one. Covered
  by `LockoutTests.A_locked_password_does_not_lock_google_sign_in`.
  (5) **The IP allow-list is config, not a table** (`Auth:InternalRoleIpAllowList`, CIDRs), and is
  **off while empty** — the ADD calls it optional, and a platform whose only Super Admin is on a
  dynamic address would otherwise be one DHCP lease from having nobody who can sign in. A malformed
  entry is dropped and logged loudly rather than disabling the list: a security control that
  quietly stops working is worse than one that refuses a legitimate admin.
  (6) **PBKDF2-HMAC-SHA256 at 600 000 iterations, PHC-encoded.** Argon2id is the stronger
  primitive but is a third-party package for one consumer; PBKDF2 is in the BCL and FIPS-approved.
  The parameters live in the stored string, so raising the work factor needs no migration. Sign-in
  runs a real derivation even when the address does not exist, so response time does not leak which
  addresses are registered. The 12-character floor is enforced where a password is *set*, not where
  one is presented.
  (7) **Key rotation is an overlap, not a switch.** `Jwt:SigningKeyPem` signs;
  `Jwt:RetiredSigningKeyPems` stays published and accepted for one deploy. Promoting a new key with
  no overlap 401s every session issued in the previous 30 minutes across every service at once —
  `KeyRotationTests` is the regression test. Set `Jwt:RefreshTokenKey` or the 90-day rotation logs
  everybody out (still the C020 gap (h) recommendation; now also in `.env.app.example`).
  (8) **The OIDC verifier is real in the tests; only the provider's key source is faked.**
  `TestOidcProvider` mints tokens with a local key and serves it as the JWKS, so issuer, audience,
  expiry and signature are all checked by production code. Faking `IOidcTokenVerifier` instead
  would have left every one of those untested — including the audience check, without which an ID
  token minted for anybody else's OAuth client would be a MageRide admin session.
  (9) **The Google authorization-code exchange gets no retry pipeline.** A code is single-use: a
  retry after a response we did not see spends it and returns `invalid_grant`, turning a blip into
  a definite failure. Every other outbound client keeps D6' §8.3's retry.
  (10) **`POST /v1/auth/mqtt-token` honours `rideId` rather than inferring it.** A caller that
  sends one gets the extended TTL and the claim; a caller that omits one gets the four-hour floor —
  which is what C014's `MqttSessionTokenManager` documents and re-issues against. Binding to a ride
  the client did not name would make its renewal logic wrong. The endpoint also requires `deviceId`
  to equal the session's `device_id` claim, or a stolen access token could mint a publishing
  credential for a different handset.
  **Contract/spec gaps found (no change made) —**
  (e) *Nothing in `rides.rides` records a ride's expected end* — no ETA, no estimated duration,
  only `created_at`. E-02's `max(active-ride + 2 h, 4 h)` is therefore not computable as printed,
  so `Mqtt:MaxRideDuration` (default 4 h) stands in for how long a Mode C ride is assumed to run
  and the token covers that plus the 2 h grace, floored at 4 h. **D4' §5 should carry an
  `expected_end_at` or an ETA**; the day it lands, `MqttTokenService` reads it and nothing else
  changes.
  (f) *No spec prints the secondary SMS gateway's request shape.* D6' §7.3 names "Dialog/Mobitel"
  and D7' §4.2 gives one URL key; the two candidates do not share an API. `SecondaryGatewayOtpSender`
  posts `{to, from, message}` JSON under a bearer, which every candidate accepts in some form — a
  deployment whose gateway wants otherwise replaces the class. **D6' §7.3 should name the provider
  and its contract.**
  (g) *`.env.app.example` carried `Google__ClientId` / `Apple__ClientId`, which bind to nothing.*
  Replaced with the four `Oidc__*` keys the service reads, plus the `Auth__*` and
  `Sms__NotifyLkUserId` rows D7' §4.2 does not list. `Sms__SecondaryGateway` was filed under
  notification-svc; iam-svc reads the same flat map, and the comment now says so.
  (h) *`iam.fleet_members` allows several memberships; the token carries one pair.* The most
  privileged wins (owner > manager > viewer) and the rest are reachable per-fleet through fleet-svc
  (C058). No spec says which one a token should name.
  **A pre-existing failure found and fixed —**
  `MageRide.Shared.Tests`'s `The_reported_lifetime_never_drops_below_iam_yaml_s_documented_14400_`
  `seconds` (C024) read `ExpiresInSeconds` — which measures against the **system** clock — off a
  token minted at a fixed *fake* instant of 09:00 UTC on 2026-07-28. It passed when C024 ran and
  has drifted red every hour since. Now asserted twice: the minted lifetime on the fake clock, the
  reported one on an issuer using the real clock. `MqttTokenService` never trusted the property —
  it computes `expiresIn` from its own `TimeProvider`, and with `Math.Ceiling`, because truncation
  reports 14399 against a contract that says "never less than 14400".
  **For C025's open gaps —** gap (c) is **closed**: `POST /v1/auth/mqtt-token` exists, and the
  skeleton stack needs no new configuration for it (`Mqtt__SessionTokenSecret` already arrives from
  `.env.common.example` and is the same secret EMQX validates with). `apps/driver-android` and
  `e2e/walking-skeleton` still type the token in by hand; **C067/C068 and C125 can delete that.**
  Gaps (a) `offerId` and (b) presence heartbeat are untouched — they belong to C034/C041/C051 and
  C039.
  **For C027 —** `UserProfileResponse` already carries `roles` and `fleetRole`; it is missing only
  `notifPrefs`. `IUserRepository.PrincipalAsync` is the one call that yields roles + fleet scope,
  and `iam.users` is otherwise untouched, so the profile surface is additive.
  **For C062 (admin-bff) —** provisioning an internal account means an `iam.users` row, a
  `iam.user_roles` grant and a `iam.user_credentials` verifier written through
  `PasswordHasher.Hash`, which is where the 12-character floor is enforced. There is no MFA
  enrolment to build and `Mfa__RequiredForInternal` in D7' §4.2 stays unimplemented (planner
  finding 3).
  **Build host —** Docker for Testcontainers only; the replica stayed down throughout. The 209
  tests take ~65 s, of which most is 40-odd harness start-ups — each integration test builds a
  fresh `WebApplication` so its ephemeral signing key, its OIDC provider and its Redis buckets
  cannot leak into another test.

- **Component:** C027 iam-svc-profile-rbac — 2026-07-28
- **Status:** DONE — `dotnet test backend/src/Iam.Api.Tests -c Release` → **330 passed, 0 failed,
  0 skipped** (209 → 330; 121 new). All four DoD items pass against a real Postgres and Redis
  (Testcontainers). Gates re-run green after the new migration and the contract additions:
  `bash infra/scripts/migrate-verify.sh` → **199/199** (was 195), `ApiGateway.Tests` → 530
  (was 524; `RouteConfigurationTests` runs six theories per gateway route and C027 added one —
  `iam-admin-rbac`, tier 20, without which `RouteTableTests` would send `/v1/admin/rbac/**` to
  admin-bff and fail), Spectral lint on `backend/contracts/*.yaml` → 0 errors, so the 13 new
  contract operations all route to `iam-svc`. `MageRide.Shared.Tests` → 235, `Registry.Api.Tests` → 92,
  `Ride.Api.Tests` → 117, `Dispatch.Api.Tests` → 64, `HotPath.Tests` → 35,
  `dotnet build backend/MageRide.sln -c Release` → 0 warnings.
- **Notes:**
  **Spec gaps — eight endpoints D3' does not carry, all landed in `backend/contracts/iam.yaml`
  and none actioned in `specs/` (D3' owns the route tables).** Each is argued at its path in the
  contract. The pattern is the same in every case: the ADD states the *fact*, D4'/`server_db_
  schema.md` gives it a *column*, D2 draws the *screen* — and no route writes it.
  (a) ***AL-14's default payment method has no setter.*** `iam.users.default_payment_method`
  exists (D4' §1), D2 SCR-PA-027 draws the Cash/LankaQR/OnePay picker and US-22.4 requires it,
  but `PUT /v1/users/me` carries only name, photo, language and notification switches. Added
  `PUT /v1/me/prefs/payment-method`.
  (b) ***AL-27's launch city has no setter.*** The read side was given a route
  (`GET /v1/config/cities`, content-svc) and `iam.users.operating_city_code` was given a column;
  nothing writes it. Added `PUT /v1/me/prefs/operating-city`, which checks `is_active` rather than
  leaving it to the foreign key — a withdrawn city still satisfies the constraint.
  (c) ***AL-13's emergency contacts have no routes at all.*** `iam.emergency_contacts` exists
  (C003), D2 SCR-PA/PI-027b draws add/edit/delete, and `POST /v1/sos` answers
  `400 no-emergency-contact` when there is none — but nothing can put one on file, so that 400 was
  unavoidable for every driver on the platform. Added the four `/v1/me/emergency-contacts` routes.
  (d) ***AL-14's eager-fetch payload has no endpoint.*** The ADD specifies it and NFR-51 bounds it;
  US-1.14 makes it the thing a driver's replacement handset restores a live trip from. Assembling
  it client-side is four round trips (`/v1/users/me`, `/v1/me/saved-addresses`,
  `/v1/rides/*/active`, `/v1/config/cities`) and cannot satisfy "restores trip state instantly" on
  a Sri Lankan mobile network. Added `GET /v1/me/bootstrap`.
  (e) ***URD §2.2 requires the portals to render menus from the model the API enforces, and there
  is no way to read it.*** Added `GET /v1/me/permissions` (self, ungated) and
  `GET /v1/admin/rbac/matrix` · `/roles` · `/users/{id}` plus role grant/revoke — the last five
  gated on §2.3's RBAC row. **C062 fronts these; it does not need to re-derive the matrix.**
  (f) ***`GET /v1/users/lookup` is a registration oracle sitting on a public gateway route.***
  D3' §0 puts it on mTLS and the gateway refuses `/v1/internal/**` at the edge — but this path is
  not under that prefix and the `iam-users` route forwards `/v1/users/{**remainder}` from the
  internet. Guarded with the same shared-secret filter ride-svc uses (`Auth:InternalApiKey`,
  unset ⇒ route not mapped). **The cleanest fix is to move it to `/v1/internal/users/lookup` in
  D3' and the contract**; that is a client-visible rename and belongs to whoever owns D3'.
  (g) ***`iam.phone_lookups` did not exist*** — landed as `db/migrations/0108`. P-03 hashes the
  unregistered *rider's* number into `rides.rides.rider_phone_hash`, but says nothing about the
  lookup that decides whether there is an account at all. D-35 wants that answerable after the
  fact and E-06 wants the answer to hold no PII; one row does both. HMAC-keyed under
  `Auth:PhoneHashKey`, because an unkeyed digest of `+947XXXXXXXX` is a 10^8 offline search.
  (h) ***No feature-flag store exists anywhere.*** ADD §1.12 gives a Super Admin "feature flags"
  and no spec models a table. `config.featureFlags` in the bootstrap payload is an empty object so
  a client can rely on the field; it starts answering the day the store lands. **Needs a
  `config.feature_flags` table in D4' §17b.**
  **Decisions —**
  (1) **URD §2.3 is compiled in and read-only.** `Rbac/PermissionMatrix.cs` is the 21×9 table
  transcribed cell for cell, and `PermissionMatrixTests` **parses §2.3 out of
  `specs/user-requirements-document.md`** and compares all 189 cells rather than restating them —
  hand-copying the table into the test would only prove two copies of my typing agree. It is not
  runtime-editable on purpose: the principal who would edit it is the principal it constrains, so
  a writable matrix is one `UPDATE` away from a Super Admin granting themselves what §2.3 forbids.
  "Assign roles" is the writable half and is `iam.user_roles`.
  (2) **The legend is parsed, not hard-coded.** A cell keeps its URD symbol verbatim (`◐ own org`,
  `⚙ rates`, `✅ read`) and derives its capability flags from the glyph, with three qualifier
  narrowings for the three cells that write a verb into the qualifier and mean it: `✅ read` drops
  write (URD §2.4 — the Auditor has "no write access anywhere"), `◐ raise/recommend` trades write
  for raise (the same row gives Finance `✅ approve/execute`, and the pair only makes sense if the
  CSR cannot), `◐ subset` trades it for configure.
  (3) **`ownScope` is a restriction and is tracked per capability, not per row.** Folding it into
  the union with the other flags is wrong: a caller who holds a capability unscoped from one role
  and own-scoped from another holds it unscoped. Getting this wrong made an Admin who happens to
  own a fleet see *less* than an Admin — caught by `Scope_is_tracked_per_capability_not_per_area`.
  `EffectivePermission.ScopedGrants` is the subset that must be bounded and `qualifier` names how;
  iam-svc cannot know whether ride 7 belongs to the caller, so this is a fence for the owning
  service, not an answer. `PermissionEntry.scopedGrants` carries it on the wire.
  (4) **The fleet sub-role narrows the `fleet_owner` column and nothing else** (URD §2.1 makes
  Owner/Manager/Viewer "an org-scoped sub-model of the Fleet Owner role"). Narrowing the union
  instead would let a Viewer sub-role silently demote a Support CSR role it has no business
  touching — `The_fleet_sub_role_never_narrows_another_role`.
  (5) **`iam.saved_addresses` keeps both spellings of Home/Work — C003 note (c) asked C027 to
  collapse them and the answer is that they cannot be.** `iam.yaml`'s `SavedAddressInput` requires
  `label` *and* carries `isHome`/`isWork`, and the two are not redundant: only the booleans can
  express "at most one Home" as an index, only the label gives D2 SCR-PA-026's "Save Address As"
  somewhere to go. The service reconciles them and refuses the one combination that cannot be
  honoured (`{label:"work", isHome:true}`) rather than silently discarding half the request.
  (6) **The primary emergency contact is the oldest row, and it is denormalised inside the same
  transaction.** D-33 budgets five seconds for the whole SOS fan-out, so safety-svc reads two flat
  columns and never joins; two copies of one fact is only safe if they cannot be observed
  disagreeing. Deleting the primary promotes the next, deleting the last clears both columns.
  "Oldest" makes promotion deterministic without a column the schema does not have.
  (7) **`GET /v1/me/bootstrap` reads four other services' tables directly**, on one connection,
  read-only — the same argument as C026's `PublisherRepository`, only stronger. Four synchronous
  HTTP calls would make a *login* fail whenever any of four services is redeploying. The universal
  rule it does not break is the one about state changes.
  (8) **`DELETE /v1/users/me` records and does nothing else.** No block, no revoked session, no
  anonymised column: erasure may be rejected or held (`FulfilledHold`), and a user whose request is
  refused must find their account as they left it. A second request while one is open is a `409` —
  two 30-day clocks against one obligation leave whichever C065 does not fulfil permanently overdue
  in `ix_pdpa_requests_due`.
  (9) **Notification-type keys are data, not property names.** `MageRideJson` sets
  `DictionaryKeyPolicy = CamelCase`, which would rewrite `SCHEDULED_REMINDER` as
  `sCHEDULED_REMINDER` on the way out and read it back verbatim — corrupting a mute exactly once,
  silently. `LiteralKeyDictionaryConverter` is applied to the column and to the wire.
  **C061's `PUT /v1/notify/preferences` writes the same column and needs the same treatment**; it
  is one file (`Domain/LiteralKeyDictionaryConverter.cs`) and probably belongs in the kernel the
  second service that needs it.
  (10) **`Auth:PhoneHashKey` is deliberately not `Otp:PepperKey`.** The OTP pepper guards a code
  that lives five minutes and can be rotated the moment it leaks; this one keys rows that outlive
  the accounts they name, so a rotation partitions the table rather than re-keying it. Two
  lifetimes, two keys — documented as not-rotatable-in-place in `.env.app.example`.
  (11) **A blocked account still reads as `registered:true`** from the lookup. Answering false
  would push a proxy rider down the unregistered SMS path — where nothing checks the block either —
  and would disclose the account's standing to a caller with no business knowing it.
  (12) **Revoking a role has two refusals, both `409`.** A primary role cannot be revoked as a
  grant (`RolesAsync` unions `iam.users.role`, so the delete would change nothing an evaluator can
  see while the console showed it gone), and a Super Admin cannot revoke their own `super_admin`
  (AL-06 makes them the only principal who can grant it back). Another Super Admin can.
  **Spec conflicts found (no change made) —**
  (i) *D2 SCR-PA/PI-027b still draws a language picker on **Edit profile**, which AL-26 removed.*
  The Δ 2026-06-21 change set is later and wins, and the C027 fence repeats it — but it is a rule
  about **screens**, and the server cannot tell which screen a `PUT` came from. `iam.yaml` lists
  `language` on `PUT /v1/users/me`, so it is honoured there (the contract wins over the code) and
  `PUT /v1/me/prefs/language` is the route onboarding and Settings use. The fence is enforced in
  the apps — **C068/C069 must not bind the Edit-profile segmented control**, and **D2 §SCR-PA-027b
  needs a micro-change-set** removing the Language row from its component table.
  (j) *Two routes now write `iam.users.notif_prefs`* — `PUT /v1/users/me` (iam, D3' route table)
  and `PUT /v1/notify/preferences` (notification-svc, D3' comms). Both are in D3'; neither
  document mentions the other. They must agree on the unmutable set (`SOS_*`, `RIDE_CANCELLED`)
  or the last writer wins with different rules. **D3' should name one owner**; iam enforces the
  `notification.yaml` rule in the meantime.
  (k) *`GET /v1/users/lookup` returns `userId` but P-03 never uses it.* ride-svc needs the boolean
  to choose a flow and the id to set `rides.rider_id`; the contract already carries both, so this
  is only worth noting because widening this response further is the easy mistake — a booker who
  mistypes a digit would learn the name of whoever owns the number they reached.
  **A trap for the next service that reads a money rollup —**
  Dapper matches a record's primary constructor against the **column** types exactly. Declaring
  `DriverEarnings.GrossMinor` as `long` against an `INTEGER` column fails materialisation with
  "a constructor matching signature (…) is required" and surfaces as a 500, not a mapping warning.
  `fares.driver_earnings` money columns are `INTEGER`; they widen into `Money.AmountMinor` at the
  call site instead. **C047 and C063 will hit this on the same table.**
  **For C062 (admin-bff) —** the RBAC model is done and consumable: `GET /v1/me/permissions`
  returns the caller's effective set and `GET /v1/admin/rbac/matrix` the whole table, both in the
  shape the role-scoped menu manifest needs. Use `RequireFeature(area, capability)` rather than
  naming roles at call sites. The audit interceptor (D-35) is still C062's — iam records a role
  grant's provenance in `iam.user_roles.granted_by` and writes no `audit.events` row.
  **For C029 / C053 —** `iam.emergency_contacts` now has an editable list and
  `iam.users.emergency_contact_name`/`_phone` is guaranteed to be the oldest row or NULL, so
  safety-svc's SOS fan-out can read the two columns without a join and trust them.
  **Build host —** Docker for Testcontainers only; the replica stayed down throughout. The 330
  tests take ~3 min, of which most is ~90 harness start-ups.

- **Component:** C028 registry-svc-vehicles — 2026-07-28
- **Status:** DONE — `dotnet test backend/src/Registry.Api.Tests -c Release` → **145 passed, 0
  failed, 0 skipped** (92 → 145; 53 new). All four DoD items pass against a real Postgres, Redis
  and Redpanda (Testcontainers). Gates re-run green after three migrations, a seventh Kafka topic
  and a one-statement change to dispatch-svc: `bash infra/scripts/migrate-verify.sh` →
  **203/203** (was 199), `Dispatch.Api.Tests` → 64, `Ride.Api.Tests` → 117, `Iam.Api.Tests` → 330,
  `ApiGateway.Tests` → 530, `MageRide.Shared.Tests` → 235, `HotPath.Tests` → 35, Spectral on
  `backend/contracts/*.yaml` → 0 errors, `dotnet build backend/MageRide.sln -c Release` →
  0 warnings.
- **Notes:**
  **Spec gaps — three micro-change-sets landed as `db/migrations/0309`–`0311`, plus one topic.**
  (a) ***`share.revoked` had a producer, a consumer, and neither a topic nor a table.*** D3' has
  `DELETE /v1/vehicles/{id}/share/{grantId}` "revoke → `share.revoked` (D-22)" and D6' §5.2 has
  fanout-svc turn it into a directed `RemoveFromGroupAsync` inside 200 ms — but D6' §2.1's
  registry lists six topics and none is registry-svc's, and no DDL source has `registry.outbox`.
  Added both: `0309` and **`registry.events`** (key vehicleId), wired into
  `EventTopics`, `bootstrap-topics.sh` and `slim-verify.sh`. **D6' §2.1 should carry the topic and
  D4' §2 the table.** This is the second half of the gap the C021 handoff left open.
  (b) ***`share.revoked`'s payload cannot do the job D6' §5.2 gives it.*** §5.1's hub-event table
  gives it `{vehicleId}`; §5.2 in the same document requires a **directed** removal "to affected
  passenger". A vehicle id alone leaves fanout two options, both wrong — remove everybody watching
  the vehicle, or query registry-svc on the hot path. The envelope carries `passengerId`,
  `grantId` and a `reason` (`revoked` | `unsubscribed` | `vehicle-deactivated`).
  **D6' §5.1's payload column should say `{vehicleId, passengerId}`.**
  (c) ***"Which vehicles may this driver operate" is spread over three tables and nothing answers
  it.*** US-13.9 gives an assigned driver the right to "select one and go online" with a fleet
  vehicle in a separate "Temporarily assigned to me" group, and every consumer that needs the fact
  — registry's `select-live`, dispatch's standby gate, trip-state's session start — would
  re-derive the join. dispatch-svc **was** the drift already: it read
  `registry.vehicles WHERE owner_id = :driver`, which cannot see an assigned vehicle at all, so
  US-13.9 was unimplementable against it. `0310` adds the view
  `registry.driver_eligible_vehicles` (driver, vehicle, `source`, the raw status columns, and one
  computed `is_go_live_eligible`), and dispatch's `PresenceRepository.FindVehicleAsync` now reads
  it. **D4' §2 should carry the projection.**
  (d) ***`registry.fleet_assignments` has no expiry column,*** although US-13.9 says the
  assignment "auto-expires". The view honours revocation (US-13.8) and cannot honour expiry.
  **C059 owns assignment writes and should add `expires_at`**; the view then gains one predicate.
  **A landed invariant had to be relaxed, and that is the biggest decision here —**
  `0308` (C021) made "a driver may only select a vehicle they own" a **composite foreign key** to
  `registry.vehicles(id, owner_id)`. US-13.9 admits a non-owner, so the key rejected exactly the
  case the requirement is about. `0311` drops it for a plain FK to `registry.vehicles(id)`. The
  invariant is **restated, not dropped**: what must hold is "a driver may only select a vehicle
  they are *entitled* to", entitlement spans two tables and is not expressible as one foreign key,
  and it is enforced against the same projection every other consumer reads. The database still
  refuses a selection naming no real vehicle and still nulls one whose vehicle is deleted.
  `ux_vehicles_id_owner` is deliberately **left in place** — nothing references it, and a UNIQUE
  is not free to re-add on a live table. Three C021 assertions were restated rather than deleted:
  two in `SelectLiveTests` (one now proves the relaxation is real and that registry-svc is what
  refuses the non-owner) and one in `migrate-verify.sh`.
  **A behaviour change downstream components should know about —** a driver with no entitlement to
  a vehicle now gets **`404 vehicle-not-found`** from `select-live`, where C021 answered
  `403 not-owner`. The projection is scoped by driver, so "not yours" and "does not exist" are the
  same query result; answering 403 again would need a second read whose only purpose is to tell a
  stranger that somebody else's plate is registered. `deactivate` and the share routes still
  answer `not-owner`, because those genuinely read the vehicle unscoped first.
  **Decisions —**
  (1) **`lock:driver:{driverId}` is a published fact, not a lock**, despite the ADD's prefix.
  US-9.6 is one column on a row whose primary key is the driver, so selecting a second vehicle
  releases the first in a single `UPDATE` and there is no window in which two are set — D-03's two
  enforcement points (`ux_sessions_active_driver`, `dispatch.driver_presence`) are both downstream
  of the choice and need to know what it was. Written **after COMMIT** and **best effort**: an
  unreachable Redis costs a cache, not a driver's shift, and a test proves a selection still
  succeeds against a dead address.
  (2) **Deactivation is one transaction with four writes** — status, the revocation of every live
  grant, one outbox row per grantee plus `vehicle.deactivated`, and clearing the driver's
  selection. A vehicle off the map while a grant still says otherwise is the leak D-22 exists to
  close. Clearing the selection is the item the C021 handoff left to C028: the FK fires on DELETE
  and a status change is not one, so the vehicle would have stayed selected and failed every
  go-online with nothing on screen to explain why.
  (3) **A grant publishes nothing until it is accepted** (US-4.3b). Publishing at creation would
  have fanout add a passenger to a group they may never accept into.
  (4) **`registry.shares` is visibility; `registry.fleet_assignments` is operation.** US-4.1
  shares "tracking access" with any driver-app user; US-13.9 lends a vehicle to drive. The DoD's
  phrase "assigned/**shared** Mode A/B" is read as the temp-hired case (US-4.10's "temporarily
  hired with a Mode A/Mode B vehicle assigned"), because a share confers no right to drive —
  conflating them would let a passenger take a bus live.
  (5) **`DELETE /subscribers/{userId}` is the passenger's own unsubscribe and only theirs.** The
  owner's removal keeps the row MUTED until they delete it (US-4.12) and is a different verb on
  subscription-svc; an owner reaching this route is 403 rather than silently performing the wrong
  one. The roster read crosses into `subscription.grants` — the contract says outright that the
  roster "is held in subscription.grants", and the alternative is a synchronous hop to a service
  that does not exist yet.
  (6) **Three routes deliberately do not require the `driver` role** — accept, unsubscribe and
  share-request are the *counterparty's* actions and the counterparty is usually a passenger. They
  require authentication and check ownership or grantee identity, which is stronger than a role.
  (7) **`merchantRef` is accepted and logged, not stored.** `registry.driver_payouts` (0304) has
  `onepay_merchant_id` and nothing else; inventing a column for a field no reader has would be
  worse. **D4' §2 should either add it or D3' should drop it.** The binding is keyed on the
  **driver**, not the vehicle, because settlement pays a person — so a driver's second vehicle
  reaching APPROVED rebinds rather than failing.
  **A live configuration trap, found and fixed —**
  `infra/env/.env.app.example` is one flat namespace shared by every co-located service, and it
  carried **unprefixed** `Outbox__Channel=ride_outbox` / `__Schema` / `__Topic` under the ride-svc
  heading. The kernel binds the `Outbox` section *after* each service's own code defaults, so
  those three silently pointed **dispatch-svc's and registry-svc's** dispatchers at ride-svc's
  channel, table and topic — `DispatchApplication` has set its own since C023 and was overridden
  anyway. Renamed to `Ride__Outbox__*` (binding nothing, like the `Dispatch__Outbox__*` block
  C023 added for the same reason); ride-svc needs none of them because the kernel's defaults
  already describe `rides` / `ride_outbox` / `ride.events`. Latent rather than observed — the full
  compose stack is not buildable until Wave 2 — but it would have surfaced as ride-svc's
  dispatcher waking on other services' events. **C009 should keep this file prefix-only.**
  **A trap for the next Minimal API service —**
  A service method named `BindAsync` makes Minimal APIs treat that service as a custom-bound
  *parameter*, and every route taking it as a dependency fails at start-up with "BindAsync method
  found on IMerchantService with incorrect format" — a routing error that says nothing about DI.
  `IMerchantService.BindMerchantAsync` is named for that reason.
  **Contract additions (all additive, Spectral-clean) —** `VehicleSummary` gained `source`,
  `fleetId`, `isSelected` and `isGoLiveEligible`, and `GET /v1/vehicles/mine` gained an `assigned`
  array. US-13.9's group and US-9.6's marker are not expressible in the contract's shape, so a
  client could not render My Vehicles from it. **D3' registry-svc should carry both.**
  **For C029 —** `VehicleRepository.ApproveAsync` and `DeactivateAsync` are the two transitions
  the onboarding machine needs; deactivation's cascade (revoke grants, clear selection, emit) is
  in `VehicleService` and rejection will want the same. `registry.outbox` is live, so the E-03
  `document.expiring` events have somewhere to go. `GET /v1/vehicles/{id}/status` returns a null
  `rejectionReason` because no path writes the column yet.
  **For C030 —** `POST /v1/vehicles/{id}/device` is left unmapped. It is a thin wrapper over
  `POST /v1/trackers/bind`, and a wrapper over nothing would answer 201 to a driver whose tracker
  was never bound.
  **For C034/C038 —** read `registry.driver_eligible_vehicles`, not `registry.vehicles`. It
  carries `is_go_live_eligible` for the simple gate and the raw columns when you need your own
  error mapping, which is why dispatch still answers `vehicle-not-approved` rather than
  `vehicle-not-found`.
  **Build host —** Docker for Testcontainers (Postgres, Redis and Redpanda); the replica stayed
  down throughout. The 145 tests take ~2.5 min.

- **Component:** C029 registry-svc-onboarding — 2026-07-28
- **Status:** DONE — `dotnet test backend/src/Registry.Api.Tests -c Release` → **175 passed, 0
  failed, 0 skipped** (145 → 175; 30 new). All five DoD items pass against a real Postgres, Redis
  and Redpanda (Testcontainers). Gates re-run green after one migration and an additive contract
  pass: `bash infra/scripts/migrate-verify.sh` → **205/205** (was 203), `Dispatch.Api.Tests` → 64,
  `Ride.Api.Tests` → 117, `Iam.Api.Tests` → 330, `ApiGateway.Tests` → 530,
  `MageRide.Shared.Tests` → 235, `HotPath.Tests` → 35, Spectral on `backend/contracts/*.yaml` →
  0 errors, `dotnet build backend/MageRide.sln -c Release` → 0 warnings.
- **Notes:**
  **Spec gaps — one micro-change-set landed as `db/migrations/0312`, plus six unspecified events.**
  (a) ***E-03 asks for four notices per document and the schema can remember one.*** ADD §1 E-03
  and ADD §6 both say the nightly job "emits `document.expiring` (T−30d/T−07d/T−1d) and
  `document.expired`", and the only state it has to decide from is
  `registry.documents(expires_at, status)` — three values for four distinct notices. From the
  second night on, every document inside 30 days is either notified *again* (a driver pushed
  nightly for a month) or *never* again (the T−7 and T−1 reminders never arrive). `0312` adds
  `registry.document_notices(document_id, threshold_days ∈ {30,7,1,0})`, written in the same
  transaction as the outbox row, so the primary key **is** the idempotency. **D4' §2 should carry
  the table**, or E-03 should say which single notice per document it wants.
  (b) ***Six event types with named producers and no envelope.*** `document.expiring` and
  `document.expired` are named by E-03 and given no shape; `vehicle.registered` is named by D3'
  `POST /v1/vehicles` side effects and **was never emitted** — C021 and C028 had no outbox, C028
  landed one and did not go back for it, so every downstream projection of the vehicle set has
  been reading a topic that never carried a registration. C029 emits it. The other three are
  inferred from requirements that need a trigger and have none: `vehicle.approved` (US-2.14's
  REGISTRATION_RESULT push — AL-27 approves with no officer, so nothing else would ever say so),
  `document.review_required` (US-2.10/SCR-AP-003's queue), and `vehicle.dispatch_resumed` (E-03
  suspends "until re-uploaded and re-approved" and never says how the planes that cached the
  suspension learn it ended). **D6' §2.1/§2.2 should carry all six**; shapes are in
  `Onboarding/OnboardingEvents.cs`.
  (c) ***D3' requires the four document ids on `POST /v1/vehicles` and AL-30 forbids it.*** The
  same specification has the wizard entry point "create a NEW vehicle at Step 1/4" (US-2.27) — a
  vehicle that must arrive with four documents has no Step 2/4 to walk to. Resolved by making them
  optional and **honouring them when sent**: supplied, the registration onboards in one shot and
  can come back APPROVED from a single call (the behaviour D3' describes); absent, the wizard saves
  each step (the behaviour AL-30 describes). `VehicleRegistration.required` narrowed accordingly.
  (d) ***Vehicle photos have no document kind.*** `registry.documents.kind` is
  `driving_license | registration | permit | insurance | revenue_license` and D6' §7.5 lists
  "vehicle photos" as a fourth onboarding document. They are stored as **`registration`**: AL-10's
  own list of statuses on the registration hub says "vehicle reg", the Mode-C driver never uploads
  a CR book, and the plated front and back photos are what evidence this vehicle's registration —
  the same kind AL-50 gives the Fleet Portal's CR-book slot. Inventing a `vehicle_photo` value no
  spec names would have been the larger change.
  (e) ***No spec pins the confidence threshold.*** AL-29, BR-25.2 and D6' §7.5 all say "below
  threshold". `Registry:OcrConfidenceThreshold` defaults to **0.80**, bounded at 0.5 so it cannot
  be turned into "trust everything" — the same situation as `Dispatch:SearchRadiusM` (C023).
  **Decisions —**
  (1) **Registration saves Step 1/4.** `POST /v1/vehicles` carries the type and registration
  number, which *is* the `details` step, and D5' §14.1a verifies that step on entry ("entered").
  So a fresh vehicle has one saved step, reads Incomplete and resumes at `insurance` — BR-25.4
  working from the first screen rather than the second. The alternative, asking for the plate again
  on 004, is a screen the driver has already filled in. **This changes a C028 assertion**:
  `verification.vehicleDetails` is `VERIFIED` on the 201, not `PENDING_INPUT`.
  (2) **The driver's own typing on Step 1 is not a manual field.** AL-29 makes any `manual` value
  pending, and details is entirely driver-typed — reading the two rules together makes AL-30's
  auto-approve unreachable for every vehicle ever onboarded. D5' §14.1a's "(entered)" is the
  tiebreak: on Step 1 the entering *is* the verification. Steps 2–4 are unaffected.
  (3) **One verdict rule, applied to fields.** A step is `pending_review` iff any of *its* fields
  is pending. The plate mismatch, the low-confidence read, the driver-typed correction and the
  field that failed to extract all arrive as a pending field — including the last, which is
  **written anyway** with a null value and `source='ai'`, so the officer queue shows "insurance
  expiry could not be read" as a row to fill rather than an absence to notice. Four rules that
  could drift apart became one.
  (4) **A step's verdict is derived from the documents that step saved**, recorded as `documentIds`
  in `registry.onboarding_steps.fields`. That is what makes a re-upload supersede cleanly: the
  failed attempt stays in the audit trail without holding the step down forever.
  (5) **`onboarding_status` comes back down; `status` does not.** Both rise together at
  auto-approval. When a verified step stops being verified — a renewal whose scan was blurry, an
  edited plate — the vehicle reads **Incomplete** on My Vehicles and stays **APPROVED**, because
  the certificate the driver is carrying has not lapsed and E-03 is what takes them off the road
  when one actually does. Demoting `status` would also overturn a Verification Officer's decision
  on an OCR miss.
  (6) **Editing the registration number re-judges the photos.** `reg_no_match` is recomputed
  against the stored `plate_text`. Without it a vehicle could reach APPROVED with front and back
  photos of a *different* plate, which is the one thing Step 4 exists to rule out.
  (7) **AL-10 is checked at the approval gate, not inferred from the steps.** Approval re-reads
  the current insurance and revenue-licence documents and refuses on a missing, expired or
  expiry-less one, so a step that verified weeks ago cannot approve a vehicle whose cover lapsed
  since.
  (8) **"Current document" is the newest saved *batch* per (owner, kind), not the newest row.** A
  step saves two documents of one kind — the licence's two sides, the vehicle's two photos — and
  they are equally current; `DISTINCT ON (owner, kind)` silently made the back supersede the front
  and lost half of every pair. Both rows are inserted in one transaction, so `DEFAULT now()` (the
  transaction timestamp) is identical and `created_at = max(created_at) OVER (PARTITION BY kind)`
  expresses the batch exactly. Found by a test that expected two licence notices and got one.
  (9) **E-03 suspends vehicles, because that is where the column is.** ADD §6 says expiry "flips
  driver to `DISPATCH_SUSPENDED`" and `dispatch_state` lives on `registry.vehicles`: a per-vehicle
  document suspends its own vehicle, a vehicle-less driving licence suspends every vehicle that
  driver owns. The release is strict — both mandatory documents current and unexpired **with a real
  expiry**, and no lapsed identity document. A renewal whose expiry nobody could read is not a
  renewal anybody can rely on.
  (10) **A sweep that crosses several thresholds at once emits only the tightest** and records the
  looser ones as moot. A job down for a fortnight otherwise sends three pushes about one
  certificate, and "30 days left" delivered to somebody with one day left is worse than silence.
  (11) **The extraction call is outside the transaction, and failure is not an error.** ocr-svc is
  a network hop; holding a Postgres transaction across it would put another service's latency on
  this one's pool. `ExtractAsync` throwing is caught and treated as `Unavailable` — the step still
  saves and lands `pending_review`, which is what D5' §14.1a does with a document that failed to
  extract, and is C054's fence stated from this side.
  (12) **Ownership, not entitlement, on every onboarding route.** An assigned driver operates a
  fleet vehicle (US-13.9); its documents are the fleet's to upload in the Fleet Portal (AL-50).
  (13) **The `multipart/form-data` variant of the step route is not mapped.** The bytes belong in
  object storage under SSE-KMS with a 90-day delete (D-36, NFR-28); streaming them through
  registry-svc would put an unredacted image on this service's disk, which is what the redaction
  pre-pass exists to prevent. JSON with upload ids only.
  (14) **`POST /v1/vehicles` validates its upload ids before it creates anything.** The document
  steps run in their own transactions after the registration is durable — deliberately, so a
  one-shot registration that dies on the third upload leaves the same state the wizard would — but
  that makes a bad file id discovered *there* leave a vehicle holding the plate, and the driver's
  own retry is then refused `registration-exists` for their own vehicle (D-37). Both photos are
  required together for the same reason.
  (15) **The internal recompute is Mode C only.** A Fleet Portal vehicle (AL-50) has no
  `registry.onboarding_steps` rows, so running the AL-30 rule over it says "not all four steps
  verified" about a vehicle that never had four steps and marks an approved bus Incomplete. The
  demotion in (5) is likewise gated on the vehicle having at least one saved step.
  **A Dapper trap that cost two rounds of red —**
  **Dapper cannot materialise a record whose constructor takes an array.** It matches constructor
  parameters against the reader's field types, and a Postgres array column presents as
  `System.Array`, which matches no `string[]`/`Guid[]` parameter; the failure is an
  `InvalidOperationException` naming the *constructor*, so it reads as a mapping bug anywhere but
  the column that caused it. It took down every registry route the moment
  `registry.driver_profiles.allowed_vehicle_types` entered the projection. Both reads now select
  `array_to_json(...)::text` and parse — which also escapes properly, where a delimiter would not.
  **Any component adding an array column to a record should expect this.**
  **Contract additions (all additive, Spectral-clean) —** `PUT /v1/drivers/profile` gained
  `displayName`/`photoUrl`/`nicNo`/`allowedVehicleTypes` (the screen after Profile Setup shows the
  driver their own details back, and the stored values differ from the sent ones whenever OCR
  supplied one); the step-save 200 gained `status` (the fourth verified step auto-approves, so the
  response that caused it says so rather than making the app poll) and `ocrJobId`; the 201 of
  `POST /v1/vehicles` gained `nextStep` and dropped `ocrJobId` from `required` (a registration that
  carried no documents queues nothing — the gap C021 raised); `VehicleRegistration.required`
  narrowed to the three fields AL-30 allows. **`nextStep` and `ExtractedField.value` are emitted as
  explicit nulls** — the shared serialiser drops nulls and both are `required`/`… | null` in the
  schema, so a `[JsonIgnore(Never)]` is load-bearing on three response records.
  **For C054 (ocr-svc) —** implement `IDocumentExtractionClient` and register it **before**
  `AddRegistryServices` (the default is `TryAddSingleton`). Return fields with confidences and
  nothing else: whether a field is pending, a step `pending_review` or a vehicle APPROVED is
  decided here, because AL-30 makes those properties of tables ocr-svc does not own. **Do not
  throw** for a document you could not read — return `DocumentExtraction.Unavailable`. The kinds
  and sides you will be asked for are in `DocumentFieldKeys.RequiredFor`/`AcceptedFor`; the photos
  request carries the expected `RegistrationNumber` so the `reg_no_match` comparison and its
  normalisation live in one place. `docs.extractions` is yours to write; put its id in
  `DocumentExtraction.JobId` and it surfaces as `ocrJobId`.
  **For C062 (admin-bff) —** the Verification-Officer queue reads
  `registry.document_fields.verify_status='pending'` (index `ix_document_fields_pending`) and is
  pushed `document.review_required`. After writing `verify_status='confirmed'` you **must** call
  `POST /v1/internal/vehicles/{id}/onboarding/recompute` — a confirmed field counts as verified for
  AL-30, and without the call the vehicle sits at `pending_review` until the driver happens to
  re-save a step. It is idempotent and takes no input. `registry.vehicles.rejection_reason`
  (US-2.15) is still unwritten by any path and is yours.
  **For C030 —** `POST /v1/vehicles/{id}/device` is still unmapped, for the reason the C028 handoff
  gives.
  **For whoever owns the upload surface —** `docs.uploads` has no writer. registry-svc resolves a
  file id to `storage_url`, checks `owner_id` against the caller and never touches the bytes; the
  tests seed the rows directly. A vehicle cannot be onboarded in a deployment where nothing fills
  that table.
  **Build host —** Docker for Testcontainers (Postgres, Redis and Redpanda); the replica stayed
  down throughout. The 175 tests take ~3 min.

- **Component:** C030 provisioning-svc — 2026-07-28
- **Status:** DONE — `dotnet test backend/src/Provisioning.Api.Tests -c Release` is 99/99 green.
  All four DoD items pass, each with a named test: a bound tracker authenticates to EMQX with its
  minted certificate and is ACL-scoped to its own vehicle topics
  (`EmqxDeviceCertificateTests`, against the **deployed** `emqx.conf`/`acl.conf`); a duplicate-IMEI
  presentation quarantines both bindings and emits the admin alert (`AntiCloneTests`); a revoked
  credential stops authenticating within 60 s on both paths (`RevocationTests`); a 1,000-row CSV
  validates atomically and queues its mint jobs with no partial commits (`BulkOnboardingTests`).
  `bash infra/scripts/migrate-verify.sh` 205/205, Spectral clean, and HotPath (35), Shared (235)
  and ApiGateway (530) re-run green against the changed broker config and topic registry.
- **Notes:**
  **Spec gaps — micro-change-sets (7).**
  (a) *No outbox table or topic for the tracker plane.* D3' lists "emit `tracker.bound`" as a bind
  side effect and D6' §4.3 makes `tracker.bound`/`tracker.unbound` the IMEI cache's invalidation
  pair — a producer and a consumer, and no topic and no table. **D6' §2.1 should carry
  `provisioning.events`** (key vehicleId) **and D4' §3 a `prov.outbox`** (migration 0403). Same
  shape C028 raised for `registry.events`; that makes two, so §2.1's "six topics" is really eight.
  (b) *No `prov.command_log`.* Third instance of the one C020 (iam) and C021 (registry) raised, so
  it is settled as a pattern rather than a one-off: **D4' §5 should print one command-log table per
  service with idempotent POSTs**, not for rides alone (migration 0402).
  (c) *T-08 is a time window with nothing to measure it against.* `prov.tracker_bindings` (0401)
  carries `state` but not when or why it changed, and D6' §4.3 quarantines on "within 24 h".
  0404 adds `state_changed_at` / `state_reason`, `prov.imei_sightings`, a `certificate_hold`
  revocation reason and a CHECK on `source`. **D4' §3 should carry them.**
  (d) *`prov.tracker_bindings.fleet_id` referenced `registry.operators`*, a stub 0306 creates only
  to satisfy that one key — the open question finding (g) left "for C030/C043". Resolved:
  **0404 repoints it at `registry.fleets`**, because `{fleetId}` in D3''s bulk route is a
  `registry.fleets` id and T-11 scopes tracker positions by RLS on this column; two id spaces meant
  bulk onboarding wrote a fleet id the predicate could not match and the scoping silently returned
  nothing. `registry.operators` is left in place (released table) and is now unreferenced.
  (e) *T-09 has a fully specified endpoint and no tables.* 0405 adds `prov.bulk_jobs` +
  `prov.bulk_job_rows`; the "one job per fleet" 429 is a partial unique index, not a SELECT-then-
  INSERT, because two Admin Portal tabs is exactly the race that check loses.
  (f) *`tracker.unbound` had no producer.* The only route that could emit it is
  `DELETE /v1/trackers/{imei}`, which D3' marks **admin** decommission — so an owner moving a
  tracker between their own vehicles could not release it, and the anti-clone rule would then
  quarantine the vehicle they moved it to. Added `POST /v1/trackers/unbind`.
  (g) *T-08 is unimplementable on the path where clones actually appear.* See decision (3).
  Added `POST /v1/internal/trackers/{imei}/quarantine`, an optional `credentialSerial` on
  `validate`, `GET /v1/internal/trackers/crl.der|.pem`, a `credentialType` form field on the bulk
  upload and the `errors.csv` route D3''s `errorReportUrl` promises. All in `provisioning.yaml`.
  **A finding that changed the design — D6' §4.2's Redis-backed dynamic ACL cannot deny.**
  §4.2 specifies revocation as "EMQX dynamic ACL backed by provisioning-svc Redis lookup + pub/sub
  invalidation". Tried against a real `emqx/emqx:5.8`: the Redis authz source is **allow-only** —
  neither `HSET mqtt_acl:{user} {topic} deny` nor a rich-JSON `{"permission":"deny"}` value denies
  anything, both fall through to the file source, and the device keeps publishing. Making Redis the
  *only* grant would fix that and break every mobile client, which is not in this service's schema.
  So T-12 is implemented as the two mechanisms the two transports actually admit: TCP — the adapter
  re-validates through `validate` and force-closes on the `prov:tracker` pub/sub message (this is
  §4.2's Redis half, and it works); MQTT — the serial goes on a CRL EMQX fetches from the
  distribution point in the certificate. **§4.2 should say CRL for the broker.**
  **Decisions —**
  (1) **The certificate's CN is the authorisation boundary.** A leaf is `CN={vehicleId}`;
  `emqx.conf`'s 8883 listener now runs `verify_peer` + `fail_if_no_peer_cert` against the device CA
  with `peer_cert_as_username = cn`, so `acl.conf`'s existing `veh/${username}/*` rules confine a
  tracker exactly as they confine a phone — **C030 added no ACL rule**. `peer_cert_as_username` is
  an `mqtt` setting and EMQX 5.8 rejects it on a listener outright, so it goes in a `zone` the
  listener references; `enable_authn = false` on that listener because the mTLS handshake *is* the
  authentication and a tracker has no session token to present as a password. Verified against the
  real image before it was written, and asserted by four tests. The C009 handoff left this to C030
  ("T-02 device mTLS … is provisioning-svc's to enable once it mints client certs").
  (2) **Rotation is not revocation.** The replacement is minted 14 days before expiry and the
  outgoing credential stays valid until its own `expires_at`, so `prov.device_certs` holds several
  live rows per binding and `validate` accepts any of them. A sweep that revoked as it rotated
  would take every tracker out of GSM coverage off the air — the population least able to come back
  for a new credential. **This is why the anti-clone rule cannot key on serial diversity.**
  (3) **Anti-clone is decidable at `bind`, and at the adapter it is not.** Two claims on one live
  IMEI arrive at `bind` with two identities, so both are held there. At the adapter a clone
  presents a *copy* of the genuine credential — same serial — and what distinguishes it is two live
  sockets holding one identity, which is the adapter's state and not this service's. So the adapter
  reports and this service adjudicates (gap (g)). An earlier draft quarantined on two serials at
  `validate`; **it would have quarantined every device the 90-day cron renews**, and the test that
  now guards it is `Both_serials_validate_across_a_rotation_overlap`.
  (4) **Outside the 24 h window the incumbent is superseded, not quarantined.** An operator moving
  a tracker to another vehicle a week later has cloned nothing. Inside it, both are held — an IMEI
  is globally unique by construction, so a second claim on a live one needs a human either way.
  (5) **The 409 is reported after the quarantine commits.** A 409 that rolled it back would leave
  the incumbent publishing and the operator with nothing to escalate.
  (6) **A bind race is re-run rather than reported.** `ux_tracker_imei_active` rejecting the insert
  means somebody bound the IMEI in between, which *is* the T-08 signal; re-running makes the rule
  fire deliberately instead of by accident.
  (7) **A bulk row already bound to the vehicle it names fails at validation, not at the minter.**
  Re-uploading last week's CSV is the likeliest thing an operator will do here, and the bind path
  would hand every row to the anti-clone rule and quarantine a working fleet. A row naming a
  *different* vehicle is left to the minter on purpose — that is a genuine second claim.
  (8) **No event payload carries credential material.** A rotation names serials only; the secret
  half goes to the caller that minted it, once, over TLS. D6' §4.2's downlink `revokeCredential` is
  the instruction to re-enrol, not the delivery — 100k device secrets on a topic with a week's
  retention would undo the point of minting them per device.
  (9) **`battery_mv` → `battery` percent.** D3' types the field 0–100 and the column stores
  millivolts; they are different quantities and something had to convert. A linear map over a
  single-cell Li-ion's 3.3–4.2 V range, documented at the constant. **No spec pins the curve.**
  (10) **The Luhn check digit is not enforced** — the contract's `^\d{15}$` is the contract, and
  D6' §4.1's grey-import GT06/JT808 units report IMEIs that fail Luhn.
  **Infrastructure that changed —** `infra/deploy/emqx/emqx.conf` (the zone + mTLS listener above;
  `enable_crl_check` and `crl_cache.refresh_interval` written out and **commented**, for the same
  reason C009 commented the JWKS block — EMQX refuses a certificate whose CRL it cannot fetch and
  the broker starts before this service, so the CDP and the check are turned on together or not at
  all). `dev-up.sh` now generates the device CA into `infra/deploy/device-ca/` (gitignored) before
  the stack comes up, and both the `emqx` container and `app-services` mount it — **the ordering is
  forced**: EMQX reads its `cacertfile` at listener start and a missing one does not degrade the
  listener, it stops the broker booting. That replaced the `provisioning-ca-data` named volume,
  which the host script could not write. `bootstrap-topics.sh` gained `provisioning.events`;
  `slim-verify.sh`'s topic loop and count follow it (**the count was already stale at 12 — C028 added
  `registry.events` without bumping it — and is now 16**), and its prov-table count is 7.
  `MageRide.TestKit` gained `DeviceCa` and an 8883 port on `EmqxFixture` for the same reason.
  **A cross-test trap worth knowing —** the harness deleted its CA directory on dispose, including
  one handed to it by `EmqxFixture`; the symptom was a bare `unexpected EOF` on the *second* test's
  TLS handshake, with nothing in the broker log, because every credential minted after the first
  disposal chained to a root the broker had never seen. **A fixture-supplied resource is not the
  harness's to clean up.** Separately, .NET caches TLS sessions per target host: a process that
  makes both certificate-ful and certificate-less connections to one `fail_if_no_peer_cert`
  listener poisons itself, so each connection uses a fresh SNI name.
  **For C043 (tcp-adapter) —** call `GET /v1/internal/trackers/{imei}/validate` on every connect and
  every 5 minutes on a long socket, passing `credentialSerial` when the device presented one — that
  is where a revoked credential stops authenticating on your path. Subscribe to the Redis channel
  `prov:tracker` (`RedisKeys.TrackerCredentialChannel`); a `tracker.revoked` message names the IMEI
  and the serials, and force-closing the matching socket inside 1 s is yours. `imei:{imei}` present
  means ACTIVE and absent means "ask Postgres" — there is no cached "revoked". When you see one
  credential on two live sockets, `POST /v1/internal/trackers/{imei}/quarantine` with what you saw;
  it is idempotent, so report it on every reconnect. PSK tokens verify **offline** against
  `secrets/psk_signing_key` — the signature covers the IMEI, so a token lifted off one device does
  not verify for another; spend the round trip on revocation, not on authentication.
  **For C044 (fleet-health-svc) —** `prov.tracker_bindings.last_seen_at` / `signal_strength` /
  `battery_mv` / `sat_count` are read by `GET /v1/trackers/{imei}` and written by nobody. They are
  yours, and `battery_mv` is millivolts (decision 9).
  **For C062 (admin-bff) —** the US-3.4 queue is `prov.tracker_bindings.state = 'QUARANTINED'`
  (index `ix_tracker_quarantined`), pushed `tracker.quarantined` with **both** holders and the
  competing serials. Resolution is: pick a holder, `TransitionAsync` it back to ACTIVE, rotate it
  (the old credential is on `certificate_hold`, the one RFC 5280 reason a CA may lift) and revoke
  the other. **No route does that yet** — it is yours to define.
  **For C125 —** turn on `StepCa:CrlDistributionPoint` and `enable_crl_check` together, and
  replace the embedded issuer with a real step-ca by pointing `StepCa:RootKeyPath` at its
  `$STEPPATH` — the layout already matches. `StepCa:Url` is refused at start-up until a client
  exists. The root key is on disk unencrypted; Vault (D7' §13) is yours.
  **Build host —** Docker for Testcontainers (Postgres, Redis, Redpanda and EMQX); the replica
  stayed down throughout. The 99 tests take ~2 min.
  **One more bug the suite caught late —** the leaf's `notBefore` is backdated five minutes for a
  tracker with a drifted RTC, and a CA written by `openssl req -x509` (which is what `dev-up.sh`
  and a real step-ca both produce) is valid from *now* with no backdating of its own. **Every mint
  in the first five minutes of a fresh stack was refused outright**, and nothing would have caught
  it, because the suite's own CA comes from `MageRide.TestKit.DeviceCa`. `GeneratedCaLoadTests`
  now runs the real `dev-up.sh` block and loads what it wrote, and the leaf's `notBefore` is
  clamped to the issuer's.

- **Component:** C031 trip-state-svc — 2026-07-28
- **Status:** DONE — `dotnet test backend/src/TripState.Api.Tests -c Release` is 46/46 green.
  All four DoD items pass, each with a named test: ten concurrent starts leave exactly one live
  session and the other nine get `409 driver-already-live` (`SessionMutexTests`); an idle session
  auto-ends at 30 minutes and restarts inside the 5-minute grace (`AutoEndTests`); ignition-on
  auto-starts a session for a paired tracker and `GET /v1/sessions/{vehicleId}/active` then reads
  ACTIVE, which is US-5.12's "journey started" (`IgnitionTests`); and a dashboard End closes a
  device-started session while the device is still publishing (`IgnitionTests`). `migrate-verify`
  205/205, Spectral clean, and ApiGateway (530), Shared (235) and Registry (175) re-run green
  against the shared-kernel changes.
- **Notes:**
  **Spec gaps — micro-change-sets (6).**
  (a) *`lock:driver:{driverId}` was already taken, and D-03's SETNX against it could never have
  worked.* ADD §6 and D-03 both specify the active-session mutex as "Redis
  `lock:driver:{driverId}` SETNX + Postgres UNIQUE partial index". C028 uses that exact key for
  registry-svc's published go-live selection, written with an **unconditional `SET`** at the moment
  the driver picks a vehicle — necessarily *before* they start a session. A `SETNX` against it
  would therefore fail every single time, and the mutex would refuse **every** start rather than
  every second one. trip-state-svc uses `lock:session:{driverId}` (added to `RedisKeys`);
  **ADD §6 and D-03 should name a different key from registry's.** The invariant is the index
  either way — see decision (1).
  (b) *`end_reason`'s two vocabularies.* server_db_schema.md §4 / D4' §4 print
  `('driver_ended','idle_timeout','geofence','admin')`; `trip-state.yaml` (D3''s machine-checkable
  form) prints `[driver_ended, idle_timeout, destination_geofence, mqtt_offline]`. Two of four
  agree, `geofence`/`destination_geofence` are one reason under two names, and each document has a
  value the other lacks — the DDL cannot record R-15/T-04's last-will end at all, and the contract
  has no `admin`. Migration 0504 resolves toward the **contract** (it is what a client branches on)
  plus `admin` and plus `ignition_off`, which AL-32/US-3.23 require and *neither* document has.
  **server_db_schema.md §4 and D4' §4 should rename and extend.**
  (c) *US-5.3's idle timer had no input.* "Auto-ends after 30 minutes of idle (no movement
  detected)" needs a last-moved instant per session, and no column in either spec holds one;
  deriving it from `telemetry.positions` would put a hypertable scan on a sweep that runs every
  minute. 0504 adds `last_movement_at` (plus `last_position_geo`/`last_position_at`, which US-5.4
  needs anyway), written by a `telemetry.normalized` consumer. **D4' §4 should carry them.**
  (d) *US-5.4's fence had no centre.* `destination_geo` (0501) is the fence a session ends at and
  US-5.4 defines it as "a 100 m radius of **the previous journey's end position**" — and nothing
  recorded where a journey ended. 0504 adds `end_geo`, copied from the session's last position when
  it closes. Also `offline_since` (R-15/T-04 give the broker a last will and no grace) and
  `started_by`/`ended_by` (AL-32 has both the device and the dashboard writing the same transition,
  and the row could not say which). **D4' §4 should carry all four.**
  (e) *`trips.ratings` had no uniqueness* and the contract answers 409 to a second rating. Without
  the index that rule is a race. 0504 adds `ux_ratings_once`.
  (f) *`trips.command_log` and `trips.outbox`.* The command log is the **fourth** instance of the
  one C020, C021 and C030 raised — settled: **D4' §5 should print one per service with idempotent
  POSTs.** The outbox is narrower and worth separating from C028's and C030's: **the topic already
  exists** — D6' §2.1 has `trip.events`, "Mode A/B session transitions from trip-state-svc" — so
  nothing new is claimed; what is missing is the table on this side of it, so the one producer §2.1
  names has no transactional way to write to the topic it names. **D4' §4 should carry it.**
  (g) *No endpoint carried ignition.* D6' §I-25.3 routes "ACC-on/off ingest events (Epic 3 ingest →
  trip-state-svc)" and AL-32 makes them auto-start/end the session, and D3' has no route. Added
  `POST /v1/internal/sessions/ignition` to `trip-state.yaml`; C043 calls it.
  **Decisions —**
  (1) **The mutex is `ux_sessions_active_driver` and Redis is a published fact.** ADD §6 says
  "SETNX **+** index" and only one can be the invariant: the index settles ten concurrent starts
  with no cooperation, survives a cache flush and cannot be bypassed. So the start does not
  pre-check — a SELECT-then-INSERT loses exactly that race — it inserts and maps `23505` to
  `409 driver-already-live`. Same reasoning C028 records for its own key.
  (2) **Two stored states, three on the wire, and no migration for it.** `trips.sessions.state` is
  `ACTIVE | COMPLETED` in both DDL specs; `SessionState` in the contract is
  `ACTIVE | ENDED | AUTO_ENDED`. The third value is *derived* from `end_reason` in one place
  (`SessionViews.From`). A stored `AUTO_ENDED` would duplicate the reason and the two could then
  contradict each other, which is worse than the mapping. **Both specs are honoured as written.**
  (3) **Reporting is not moving.** A parked bus keeps publishing at its standby cadence, and
  counting those fixes as activity would make US-5.3's timer unreachable — the exact failure it
  exists to prevent. A fix always advances the position and advances the idle clock only on
  movement, judged by two independent signals (speed ≥ 1.4 m/s **or** displacement ≥ 50 m) because
  a cheap tracker reports no speed and consumer GNSS wanders while stationary. **No spec pins
  either number.**
  (4) **A last will starts a clock, it does not end a session.** R-15/T-04 say nothing about how
  long a tunnel may last, and ending on the first `offline` would close a journey every time a bus
  passes under a bridge. `offline_since` is recorded, the sweep decides after two minutes, and a
  redelivered will keeps the *earliest* instant or a retrying broker would push the deadline
  forward forever. **No spec pins the grace.**
  (5) **AL-32 is symmetric.** A dashboard End closes a device-started session and logs
  `device.overridden`; an ACC-off leaves a *dashboard*-started session alone, because a driver
  waiting at a depot with the engine off has said what they want. The device is never authoritative
  in either direction.
  (6) **Ignition declines rather than guesses a driver.** A tracker knows its vehicle and nothing
  else (US-3.22: "the mobile app is not needed"), so the driver is the vehicle's owner — and when
  the vehicle is not Mode A/B, not eligible, or its owner is already live elsewhere, the report is
  declined. Attributing a session to the wrong driver takes their D-03 mutex and blocks the journey
  they are trying to start themselves.
  (7) **Only an auto-ended session is restartable, and the restart is in place.** A driver who
  pressed End meant it; offering to undo it makes the button ambiguous. The restart keeps the id
  and `started_at` because passengers hold that id, and every US-5.10 condition is in the `WHERE`
  clause — the index still decides whether the driver may take the mutex back.
  (8) **Every sweep close goes through the service, not a bulk UPDATE.** Closing also writes the
  domain log, the outbox row, the Redis key and the standby cadence hint; a shortcut would leave
  half-closed sessions no consumer heard about. The claim transaction is released before the closes
  — holding it would nest two transactions on one pooled connection.
  (9) **`session-restart-expired` (410) is a new error code**, registered in the three places the
  contracts CLAUDE.md requires. `trip-state.yaml` declared a 410 response for the restart with no
  410-status code to carry it; `illegal-transition` is a 400 and the operation declares no 400, so
  the "state has moved on" cases became `409 conflict` and the expired grace its own 410.
  (10) **A passenger's rating has no participation check.** Mode A is a public bus and this service
  holds no manifest, so "was this person aboard" is a question it cannot answer and must not
  pretend to. The driver's side is checked, because the session names the driver.
  **A bug the DoD test caught —** `StartAsync` was the one path that did **not** catch the unique
  violation, so nine of ten concurrent starts returned 500 instead of 409. The three other paths
  that take the mutex (restart, ignition, and the bulk equivalent in C030) had it; the main one did
  not. `Ten_concurrent_starts_leave_exactly_one_live_session` is what found it, and it is the exact
  case a SELECT-then-INSERT would also have got wrong.
  **For C043 (tcp-adapter) —** `POST /v1/internal/sessions/ignition` with
  `{vehicleId, state: on|off, at?}` and the `X-MageRide-Internal-Key` header. It is 202 and the
  `outcome` is informational: `declined` is a decision, not a failure, and must not be retried.
  Send `at` from the device's own clock when it has one. You do **not** need to publish presence —
  this service holds its own `veh/+/status` subscription (R-15, T-04).
  **For C051 (notification-svc) —** US-5.9's push is yours. `session.ended` on `trip.events`
  carries `driverId`, `endReason` and `restartableUntil`; the last is null exactly when the driver
  ended it themselves, which is when there is nothing to offer.
  **For C041 (fanout-svc) —** `session.started` / `session.ended` are keyed by vehicleId, so an end
  and the start that follows it cannot be reordered. That ordering is the reason the aggregate id
  is the vehicle rather than the session.
  **For C040 (persistence-writer) —** `trips.position_samples` (0503) still has no writer. It is
  the 1/min Mode A/B history sample, and it is the same decision you own for
  `telemetry.positions`.
  **Build host —** Docker for Testcontainers (Postgres, Redis, Redpanda and EMQX); the replica
  stayed down throughout. The 46 tests take ~45 s.

- **Component:** C032 ride-svc-core — 2026-07-28
- **Status:** DONE — `dotnet test backend/src/Ride.Api.Tests -c Release` is 252/252 green (was 57).
  All four DoD items pass, each with named tests. (1) Every cell of the D5' §7 / ADD §11.12 matrix
  is covered twice: `RideCancellationMatrixTests` restates each printed row against the table the
  service consults, and `CancellationMatrixTests` drives the same rows through HTTP against a real
  Postgres, asserting the target state, the Penalty column and the Events column. (2) Three
  consecutive post-acceptance cancellations return `403 booking-disabled` and a completed ride
  resets the counter (`BookingDisableTests`, five facts including "pre-acceptance never counts" and
  "a driver-side cancel is not the passenger's fault"). (3) The durable backstop outlives the
  process that armed it — `A_backstop_outlives_the_process_that_armed_it` stops the whole service
  and starts a new one against the same database — and there is no cache to lose a timer to, which
  is asserted rather than described. (4) `PaymentSettlementTests` walks every `PaymentState`: the
  three D5' §8.1 terminals settle with `earningPayable: true`, `Disputed` settles with `false`, and
  the seven in-flight states move nothing. Dispatch (64), Shared (235) and the solution build were
  re-run green; Spectral clean.
- **Notes:**
  **Spec gaps — micro-change-sets (4).**
  (a) *§11.12 has no row for a rider cancelling at `DriverArrived`.* The table jumps from the
  `Accepted` rider-cancel straight to the no-show, which would leave a rider unable to cancel
  precisely while a driver is waiting at their door. Appendix B.2's catch-all is stated by
  *acceptance*, not by state — "Any pre-Accepted state + rider cancel → CancelledByRiderBeforeAccept"
  — so a cancel after acceptance is the after-accept terminal wherever it lands. Landed that way,
  with the Rs 50. **§11.12 needs the row.**
  (b) *R-16 gives an "at-payment 10 min" grace with no landing.* D5' §6.3 ends every expired grace
  in "`CancelledByDriver`/`Disputed`" and §11.12 prints neither for `PaymentPending`. A cancel is
  not available once the trip has happened and a fare is owed, so it is the dispute — the same
  landing the in-progress row uses. **§11.12 needs the row.**
  (c) *`ride.yaml`'s `system-cancel` declares `fraud_lock` and `admin_intervention`, and §11.12
  covers neither.* Mapped onto the three landings the matrix already uses, chosen by how far the
  ride has got: nobody assigned ⇒ the no-penalty terminal, a driver assigned ⇒ the driver terminal,
  money owed ⇒ the dispute. There is no "cancelled by admin" state among the eighteen and inventing
  one would break `ck_rides_state`. **ADD §11.12 should name the mapping.**
  (d) *`ride.settled` is a name no spec prints.* D6' §2.2's `eventType` list is partial and §11.12's
  Events column names six more, all used verbatim; but R-05's "driver earning posts only on payment
  terminal" needs an event and nothing names one. Coined here, carrying `earningPayable` so a
  consumer never has to re-derive D5' §8.1's three-terminal rule. **D6' §2.2 should register it.**
  Two `x-error-codes` widenings went into `ride.yaml` in the same change (`cancelRide` and
  `systemCancelRide` gain `ride-terminal` and `conflict`, `cancelRide` also `validation-failed`) —
  all three codes were already in the registry, so nothing was coined.
  **Spec conflict resolved.** *How long a ride waits for a driver.* ADD §11.12 says "timeout (60 s)";
  D5' §7 and URD US-6A.11 say two minutes. Resolved toward the URD, and the two are coherent read
  that way: R-20's `Matching > 60 s` alert would be unreachable if the ride expired at 60 s. Either
  way the number is dispatch-svc's — ride-svc exposes `ExpiredNoDriver` through `system-cancel`
  (`no_driver_found`), which C034 drives.
  **Decisions —**
  (1) **The matrix is data, and it is the authority.** `POST /cancel` takes a `reason`, and the
  reason decides nothing: the outcome is (state × trigger), the trigger comes from which party is
  authenticated, and the guarded `UPDATE` is bound to the same state the matrix was resolved from.
  A client cannot ask for a cheaper terminal, and a ride that moved between the read and the write
  is answered 409 rather than terminated under another row's rules. The client's reason is recorded
  and published because reputation-svc and support want it.
  (2) **A lease-poll, not Quartz — the second time this decision has been made.** ADD §6 names
  "Quartz.NET clustered scheduler" and this was the component that was to bring it. What R-04
  actually requires is that the durable row decides and that a fire lands within about a second on
  any replica; Quartz's contribution would be a job store holding one recurring trigger whose job
  is to scan `rides.timers`, and that scan is already multi-replica safe because it claims
  `FOR UPDATE SKIP LOCKED`. Clustering the trigger would *remove* parallelism, in exchange for
  eleven `qrtz_*` tables no DDL spec declares. C023 reached the same conclusion for `offer_expiry`;
  running two different timer mechanisms over one table would have been worse than either.
  **ADD §6 / §13.4 should stop naming Quartz for ride-svc and dispatch-svc.**
  (3) **`offer_expiry` stays dispatch-svc's; ride-svc owns the other four kinds.** ADD §6 gives
  dispatch "Quartz.NET (scheduled rides **+ offer backstop**)" and C023 built it — arming the row
  and calling `POST /internal/rides/{id}/offer/expire` when it fires. Arming a second row for the
  same offer would have put two timers on one deadline and made "the ride's timer" ambiguous to
  everything that reads the table (Dispatch's own `OfferExpiryTests` reads it that way). Every query
  in `RideTimerRepository` is scoped by kind, and `RequireOwned` makes arming somebody else's kind a
  programming error rather than a silent theft of their backstop.
  (4) **A last will starts a clock; it does not cancel a ride.** R-16's four windows exist because a
  driver in an underpass has not abandoned anybody. An `offline` arms `offline_grace`, an `online`
  retires it, and only the timer expiring reaches the matrix. Redelivery cannot restart the clock —
  `ArmIfAbsentAsync` settles that in the `INSERT` rather than with a prior read, because EMQX
  redelivers a retained will to every replica and again on reconnect, and a broker retrying would
  otherwise push the deadline forward forever.
  (5) **A grace re-plans itself when the ride moves beneath it.** A driver who goes dark while
  `Accepted` and then taps Arrive (a phone with mobile data but a dead broker session does exactly
  this) gets the 120-second window — computed from the *same* `offlineSince`, so moving the ride
  along is not a way to earn a fresh grace and the re-plan terminates. `offline_grace` is therefore
  the one kind a non-terminal transition does **not** retire.
  (6) **"GPS not advancing" is the same fact, not a second one.** §11.12's in-progress row reads
  "LWT → offline > 5 min, GPS not advancing". A vehicle whose broker session has been dead for five
  minutes publishes no positions by construction — the will fires when the session dies and
  positions travel on that session. ride-svc holds no telemetry and deliberately consumes none
  (R-01's boundary is what keeps this aggregate small).
  (7) **`payment_pending` moves nothing.** No row of §11.12 takes a ride out of `PaymentPending` on
  a timeout and R-05 reserves that door for fare-svc, so the timer produces ADD §13.3.1's alert with
  a ride id on it and the `runbooks/ride-stuck.md` pointer, and nothing else. A timer that
  auto-cancelled here would erase a fare for a trip that happened.
  (8) **AL-16 is derived, not stored.** C033's fence says counters live in reputation-svc "and
  nowhere else", and C033 does not exist; a component whose DoD is "three consecutive cancellations
  disable booking" cannot ship without an answer. So `IBookingEligibility` counts the run of
  `CancelledByRiderAfterAccept` at the head of the passenger's own ride history — not a second copy
  of the counter, because the rides *are* the facts it would be computed from. **C033 replaces the
  implementation and deletes the query; nothing else in this service changes.** AL-16's re-enable
  path (clear the outstanding Rs 50, cooldown or CSR reinstatement) needs billing's balance and an
  admin surface and is not implemented — a passenger disabled here is re-enabled by completing a
  ride, which is also what US-6A.10b says resets the counter.
  (9) **A no-show is not a cancellation.** US-6A.10b counts "cancellations made after a driver has
  accepted" and reputation-svc keeps a separate no-show counter (D5' §4.2), so `NoShowRider` does
  not increment the AL-16 run. Charging one event to both tallies would disable booking in two
  rides, not three.
  (10) **Money travels as a rule where ride-svc does not hold the number.** A mid-trip cancel
  accrues the *quoted* fare with `basis: full_fare`, and the rider no-show carries
  `driverCompensationBasis: base_fare_half` — §11.12's "driver compensation = base fare/2", where
  the base fare is per tier and lives in `fares.tariffs`. fare-svc resolves both. ride-svc writes no
  ledger entry and no `dispatch.cancellation_penalties` row: those are other bounded contexts, and
  the fence for this component is that cross-service state changes go through the outbox.
  (11) **A redelivered settlement is a 200, not a 409.** fare-svc's delivery is at least once and
  R-14's replay only covers an identical `Idempotency-Key`, so the same terminal arriving under a
  fresh key is answered with the settled ride and writes no second transition and no second earning
  authorisation. The alternative would have been `payment-already-settled`, which
  `notifyPaymentSettled` does not declare — and a duplicate callback is not an error anyway.
  **Two bugs the DoD tests caught —**
  (a) `ux_rides_open_passenger` exempts `Completed` but **not** `PaymentPending`, so the AL-16 reset
  test could not book its next ride: a passenger whose last trip is awaiting payment is wedged until
  fare-svc settles. Known and landed verbatim (C004 note (b)) but never *exercised* before, because
  C022 had no way to leave `PaymentPending`. The test settles the ride the way fare-svc does; the
  wedge is real for any deployment where fare-svc is down, and is worth an index change if the
  exempt list is ever revisited.
  (b) `updated_at` is maintained by the `trg_rides_updated` BEFORE UPDATE trigger (migration 0002),
  so the R-20 gauge's "age in state" cannot be forged — which is the property that makes the column
  usable for the SLO, and which meant the metrics test had to reach for
  `session_replication_role = replica` to age a ride at all.
  **Three R-20 rows are approximated, and the approximation is stated in code.** ADD §13.3.1's
  "Accepted AND no live pos > 60 s" and "InProgress AND no GPS sample > 5 min" both ask about
  telemetry ride-svc does not hold; what is published is time in state, which is a superset (every
  ride whose driver has gone dark is in it, along with rides that are merely slow). **The precise
  version belongs with whoever owns the position index (C039/C040).**
  **For C037 (proxy + package) —** the aggregate is kind-agnostic and the machine already is:
  nothing below needs a new state. `RideTimerKinds` has the three kinds left for you
  (`location_request_expiry`, `otp_attempt_window`, `cod_uncollected`) and `RequireOwned` is where
  you widen the claim. The P-14 "uncollected COD > 24 h → Disputed" rule is a matrix row you add to
  `RideCancellationMatrix`, not a new code path. `RideStateWriter.RecordAsync` is the only way a
  state change may be written — audit row, timer plan and outbox in one call.
  **For C033 (reputation-svc) —** `reputation.driver_cancelled` is on `ride.events`, keyed by
  rideId, carrying `driverId`, `fromState`, `reasonCode` and `systemInitiated` (a driver who tapped
  Cancel versus one whose phone died — §11.12 gives both the same effect and you may want to tell
  them apart). `ride.no_show_driver` and `ride.no_show_rider` are the no-show counters' input. When
  you land, replace `IBookingEligibility` with a gRPC read of `block_status` — see decision (8).
  **For C034 (dispatch-svc-core) —** `POST /v1/internal/rides/{rideId}/system-cancel` with
  `{reason: "no_driver_found"}` is how a ride reaches `ExpiredNoDriver`, and it is legal **only**
  from `Matching`. The two-minute cascade budget is yours (US-6A.11); see the spec conflict above.
  `driver_offline_grace_expired` is also available to you if you would rather drive the R-15 path
  from dispatch than rely on ride-svc's own `veh/+/status` subscription — whichever reaches the
  matrix first settles it and the other finds no row, which is the normal race.
  **For C049/C050 (fare-svc) —** `POST /v1/internal/rides/{rideId}/payment-settled` with
  `{paymentId, paymentState, settledMinor}`; only `Succeeded`, `FellBackToCash`,
  `CashOnDeliveryCollected` and `Disputed` are accepted. `cancellation.penalty.accrued` carries
  `basis` (`cancellation_fee` | `no_show_fee` | `full_fare`) and `affectedDriverId` — D5' §7.1's
  settlement is yours, keyed `penaltyId:rideId`. A mid-trip cancel's `amountMinor` is the *quote*;
  replace it with the metered fare.
  **Build host —** Docker for Testcontainers (Postgres, Redpanda); the replica stayed down
  throughout. The 252 tests take ~2 min 20 s.

- **Component:** C033 reputation-svc — 2026-07-28
- **Status:** DONE — `dotnet test backend/src/Reputation.Api.Tests -c Release` → **84 passed, 0
  failed** (~1 min). All four DoD items are covered by named tests: the D5' transitions
  (`BlockStatusTests`), the 20 ms p95 over a real socket against a warm Redis
  (`GrpcSurfaceTests.The_block_status_call_answers_under_20ms_at_p95`), one `fraud.suspected` per
  detection window (`CollusionDetectionTests`), and the completed-ride reset — proven twice, once
  in-process and once through a real `ride.events` topic (`RideEventPipelineTests`).
  `MageRide.Shared.Tests` (235) and `Ride.Api.Tests` (252) re-run green after the kernel changes
  below.
- **Notes:**
  **Micro-change-sets — (1) `reputation.events` is not in D6' §2.1.** `fraud.suspected` has a
  producer (ADD §6 "emits `fraud.suspected` for admin review") and a consumer (ADD §12.6's admin
  fraud queue) and no topic, the third time this shape has come up (C028 `registry.events`, C030
  `provisioning.events`). Added to `EventTopics`, `bootstrap-topics.sh`, `RedpandaFixture.Topics`
  and `slim-verify.sh` (now **9** topics / 18 with DLQs). Keyed by **userId**, not rideId — a
  block state is a fact about a person. `reputation.block_state_changed` is this service's own
  name; no spec prints one.
  **(2) `reputation.intake_log` (0803).** D6' §2.3 is at-least-once and a gRPC retry has the same
  shape; counting a redelivered driver-cancel twice would booking-disable a passenger in two rides
  instead of three. The ledger is also the answer to "why is this counter 2?", which is the first
  question an appeal asks. **(3) `reputation.outbox` (0803)** — R-13, same argument as
  `dispatch.outbox` (0709). **(4) `reputation.command_log` (0803)** — the fourth per-service
  command log (iam 0104, registry 0307, dispatch 0710); `db/CLAUDE.md` updated.
  **(5) Block-state provenance and the fraud-flag lifecycle (0804).** `reputation.block_states`
  records the state and not who decided it, so an admin lifting a block would be undone by the
  next report already in the queue — `source`/`reason`/`set_by` are what make an override stick.
  `reputation.fraud_flags` had no `status` even though `reputation.yaml`'s `FraudFlag` types one
  and the list route filters on it, so nothing could ever leave the review queue. `window_key` +
  `ux_fraud_flags_window … NULLS NOT DISTINCT` is what makes the DoD's "exactly once per detection
  window" a database property rather than a scheduler one.
  **(6) `reputation.network_observations` (0805).** E-07 names three detectors and only two had an
  input: pair frequency (this service's ledger) and device binding (`iam.devices.device_key`,
  0105) exist, and **nothing in the whole schema records a client IP** — the one `INET` column is
  `prov.tracker_bindings.remote_addr`, which is a tracker's. A detector with no input is a gate
  that always passes, so the table and `POST /v1/internal/reputation/observations` landed. **No
  producer exists yet**: the gateway (C008) and iam-svc (C020) are the two that see the address.
  The clustering detector is proven against seeded rows.
  **(7) Three admin routes D3' does not carry.** `PUT /v1/admin/reputation/users/{userId}/
  block-state` is the "manual state override with audit" deliverable and D3' names no route for
  it; `POST /v1/admin/reputation/flags/{flagId}/resolve` exists because `FraudFlag.status` has a
  `dismissed | actioned` enum and no way to reach it; `GET /v1/admin/reputation/users/{userId}` is
  what an admin reads before overriding. All three are under `/v1/admin/reputation/**`, which
  `gateway-routes.json` already routes here, so C008 needed no change. `backend/contracts/
  reputation.yaml` updated; spectral 0 errors.
  **(8) `dispatch.driver_levels` has the wrong owner in D4' §6.** Every rule that *changes* a
  level is D5' §4.2's — three reports take one, a no-show takes one (US-6A.7) — and D3' puts both
  `GetDriverLevel` and the appeal restore on reputation-svc. **reputation-svc is now the sole
  writer of that table** and dispatch-svc reads it; a second `reputation.driver_levels` would be
  two tables for one fact, which is what this component's fence exists to prevent. D4' §6 /
  `server_db_schema.md` §6 should move it to the `reputation` schema.
  **Decisions —**
  (1) **gRPC has to have a port of its own, and that is not a preference.** Cleartext HTTP has no
  ALPN, so Kestrel cannot negotiate HTTP/1.1 and HTTP/2 on one socket — the admin endpoint answers
  a gRPC client's preface with `GOAWAY HTTP_1_1_REQUIRED`. D7' §4.2's `Grpc__ListenPort`=5005 was
  right and the "share one endpoint" shortcut is not viable. reputation-svc is therefore the one
  service that binds its own Kestrel listeners; `ASPNETCORE_URLS` still decides the HTTP side.
  **C034/C039 will hit this the moment they add a gRPC client** — the proto is at
  `backend/contracts/proto/reputation.v1.proto`, compiled by the server with
  `GrpcServices="Both"`; add the same `<Protobuf>` item with `GrpcServices="Client"`.
  (2) **The gRPC service is `AllowAnonymous` and idempotency-exempt, and both were bugs first.**
  The kernel's deny-by-default fallback answers `Unauthenticated` to a caller with no bearer, and
  `IdempotencyMiddleware` answers `400 idempotency-key-required` to a gRPC POST — which reaches
  the client as an unreadable "Bad gRPC response. HTTP status code: 400". The RPCs carry their own
  dedupe key, which is stronger than the header's anyway because it also survives a retry that
  regenerated it.
  (3) **The live intake is `ride.events`, not the five D3' RPCs.** D3' declares
  `ReportCancellation`/`ReportNoShow` with ride-svc as the caller; ride-svc (C032) publishes and
  calls nothing, which is also what CLAUDE.md's universal outbox rule requires. Both paths are
  implemented and both go through `reputation.intake_log`, so wiring the gRPC side later counts
  each fact once rather than twice.
  (4) **A completed ride produces two facts, one per side.** D5' §7.2 names no role and both runs
  have to reset. The pair is also the E-07 pair-frequency detector's input, which is why the
  detector reads this service's own ledger rather than `rides.rides` — it stays inside the bounded
  context and survives a ride row being erased under PDPA (E-06).
  (5) **A derived state follows its counters; only an event-imposed one is sticky.** §11.12's
  brief delist has no threshold to fall back below, so it survives its time box;
  `cancellations_disabled` and `reports_delist` are derived, and protecting them the same way
  would mean a completed ride could not re-enable a booking-disabled passenger — which is
  precisely what §7.2 says it must.
  (6) **Reinstatement forgives the counters.** `PUT …/block-state` with `OK` clears all three and
  returns the user to automatic control. Leaving them at three would restore access for exactly as
  long as it took to recompute, and the admin's decision would look like it had silently failed.
  AL-16's other half — "clear outstanding Rs 50 balance" — is billing's and is **not** implemented
  here; what is implemented is the cooldown, the admin reinstatement and §7.2's completed-ride
  reset.
  (7) **A served time box forgives what caused it.** When a 3-report delisting lapses, the sweep
  resets `reports_total` with it; otherwise the recompute would re-delist the driver the instant
  the box ended and "temporary" would mean permanent.
  (8) **Four numbers no spec pins**, all configurable and argued at their declaration: the
  7-day report delisting ("temporary", D5' §4.2), the 30-minute driver-cancel delisting ("brief",
  §11.12), the 24-hour booking-disable cooldown ("a configurable cooldown", AL-16) and E-07's
  pair threshold N ("> N rides / 30 d"). **And WARN: no rule in D5' produces it at all**, so it is
  "one short of a hard threshold" and every bound is configuration.
  **Two bugs the DoD tests caught —**
  (a) **A lock-order deadlock, and the fix is not a retry.** `SELECT … FOR UPDATE` that matches
  nothing takes no lock, so two concurrent first facts for one user could each hold one of
  `block_states`/`counters` and wait for the other. `IBlockStateRepository.LockAsync` now
  materialises an `OK` row before locking, which removes the cycle; reads still use `FindAsync`,
  because a dispatch round asking about a thousand drivers must not create a thousand rows.
  (b) **Dapper matches a record constructor on exact field types**, so a `SMALLINT` column and an
  `int` parameter do not bind — `reputation.counters.cancellations_continuous` and
  `dispatch.driver_levels.level` are both cast `::int` in the SELECT, and the reason is stated at
  each one. Separately, an optional filter needs `@p::text IS NULL` rather than `@p IS NULL` or
  Postgres answers 42P08 for the whole query.
  **Kernel changes —** `Ulids` was promoted from `Ride.Api/Domain` to
  `MageRide.Shared.Primitives` (second caller; the same move C024 made with
  `KafkaTopicConsumer`), and `RedisKeys.BlockStatus(userId)` was added — **ADD §9.4 has no key for
  it**, although D5' §3.2 makes the block state a per-candidate gate and the DoD requires a warm
  cache. Both are micro-change-sets in spirit; ride-svc's four call sites were updated and its 252
  tests re-run green.
  **For C034 (dispatch-svc-core) —** the D5' §3.2 gate is
  `GetBlockStatus(DriverRef{user_id}).dispatch_eligible`, precomputed server-side so every caller
  applies the same rule, and `GetDriverLevel(...).job_board_eligible` is US-6A.8's L1 exclusion.
  Both need `x-mageride-internal-key` metadata equal to `Reputation:InternalApiKey`. Subscribe to
  `reputation.events` if you want to invalidate a local cache on the fact rather than on a TTL.
  `dispatch.driver_levels` is read-only for you now — see micro-change-set (8).
  **For C044 (safety-svc) —** `ReportVehicle` with `status = REPORT_STATUS_CONFIRMED` is the only
  thing that moves `reports_total`; send `PENDING` too, under the same `report_id`, and the
  confirmation is then correctly ignored as a duplicate rather than counted a second time. Three
  confirmed reports delist and cost a level, and the `Ack` carries the resulting state.
  **For C052 (admin-bff) —** the fraud-review queue is
  `GET /v1/admin/reputation/flags?status=open`, or the `fraud.suspected` stream on
  `reputation.events`. Actioning a flag and blocking the subject are two calls on purpose: a flag
  is a review item and never an action (ADD §12.6 reserves the auto-suspend for a Tier-2 admin
  decision). Both write `audit.events` inside the same transaction as the decision.
  **Build host —** Docker for Testcontainers (Postgres, Redis, Redpanda); the replica stayed down
  throughout. The 84 tests take ~1 min.

- **Component:** C034 dispatch-svc-core — 2026-07-29
- **Status:** DONE — `dotnet test backend/src/Dispatch.Api.Tests -c Release` is **108/108 green**
  (30 new). All four DoD items have a test that fails without the code: a score recomputed from its
  own persisted breakdown, a driver refused from the 2nd trip of the Colombo day and allowed the
  1st, a cascade walked in score order that ends `ExpiredNoDriver` on the 120-second deadline, and
  a real EMQX last will releasing a live offer inside the configured grace. `Ride.Api.Tests` (252),
  `Reputation.Api.Tests` (84), `MageRide.Shared.Tests` (235) and `ApiGateway.Tests` (530) re-run
  green; `infra/scripts/migrate-verify.sh` is 205/205 twice; spectral is 0 errors.
- **Notes:**
  **A live bug, found by the wallet DoD and fixed by migration 0712 —**
  `ux_offers_driver_live` is printed in server_db_schema.md §6 as
  `ON dispatch.offers(driver_id) WHERE status IN ('OFFERED','ACCEPTED')`, and nothing in the
  `status` CHECK is terminal for an accepted offer. So a completed ride left its row at ACCEPTED
  for ever and **the second ride a driver was ever offered was refused by a unique violation
  against the first one they finished — and every ride after it.** C023 could not see it (no test
  accepted and then dispatched again) and C025's walking skeleton books exactly one ride. Both
  ways of fixing it inside the printed DDL lose something real: settling the row to DECLINED or
  EXPIRED makes the audit lie about what the driver did, and dropping ACCEPTED from the predicate
  would let a driver hold an offer for a second ride while carrying a passenger on the first —
  the exact race R-10 exists to prevent. 0712 therefore gives *liveness* the dimension it was
  missing (`released_at`) and changes neither printed list. **§6 / D4' §6 should carry it.**
  Two more halves of the same fact were also missing and are now done in `ReturnToPoolAsync`:
  ADD §11.12 makes dispatch-svc responsible for three things on a terminal event and C023 did one
  of them — `lock:driver-offer:{driverId}` was left to its 15-second TTL, which self-heals just
  slowly enough to lose the driver the next ride and just fast enough that nobody sees why.
  **Micro-change-sets raised —**
  (1) **`dispatch.timers` had no ride subject** (migration 0711). 0708 printed it for DT-04 alone,
  so `driver_id` is NOT NULL — and US-6A.11's 120-second deadline has to fire in exactly the case
  where no driver was ever found. `rides.timers` cannot hold it either: its `ck_timers_kind` is a
  closed list of eight ride-svc kinds. 0711 adds `ride_id`, `payload`, a subject CHECK and the two
  live-timer partial unique indexes that make arming idempotent under at-least-once delivery.
  (2) **`dispatch.offers.released_at`** (migration 0712) — above.
  (3) **`POST /v1/internal/rides/{id}/offer/expire` gained a `reason`.** R-04's guard is
  `offer_expires_at <= now()` evaluated by Postgres, and it must stay that way — a backstop
  trusting its own clock could cancel an offer a driver is still inside the window to accept. R-15
  is the one caller that has to revoke *inside* the window: the grace has already established the
  driver's broker session is dead. `reason: driver_unreachable` drops the predicate and nothing
  else; a closed set rather than a boolean so `rides.transitions.reason_code` records which of the
  two happened. `backend/contracts/ride.yaml` updated; **D3' ride-svc should carry it** alongside
  C022/C023's three routes.
  (4) **D5' §5 gives two different position intervals for the same driver.** §3.2 excludes a
  candidate whose GPS sample is older than `2×expectedInterval`; §5.1 says "Idle standby (Mode C) =
  1 / 60 s" and §5.2's table repeats it, while the row below says "Candidate in pool |
  availability=AVAILABLE | 2–5 s" — and a Mode C driver on standby is *both* at once. Took §5.1's
  60 s (⇒ a 120 s bound), because it is the number R-08's `driver:availability` TTL already agrees
  with and because 5 s would put the bound at 10 s and exclude every driver whose app is on the
  standby cadence the same document asks it to use. Both halves are configuration.
  (5) **The 120 s global timeout contradicts ADD §11.12's 60 s.** D5' §3.5, US-6A.11 and this
  component's DoD all say two minutes; §11.12's matrix row says "(60 s)" in a parenthesis. Took
  120 s.
  (6) **No spec gives the §3.3 weights, the distance normaliser or N in "N rounds".** `w_dist`
  0.60 / `w_level` 0.25 / `w_cat` 0.15 sum to 1 so a score reads as a fraction of the best possible
  candidate; `normalize(1/d)` is `1/(1 + d/halfLife)` with a 1 km half-life, which is finite for a
  driver standing on the pickup — the literal `1/d` is not. All are admin-config and travel *on the
  audit row*, so a decision stays reproducible after a retune.
  (7) **P-11's `vehicle_type × package_size` table is not printed anywhere.** ADD §11 and D5' §11
  give exactly one cell of it (`Motorbike × L = false`). The other 23 are in
  `Domain/PackageCompatibility.cs`, derived from AL-09's own split of the eight tiers. **It belongs
  in D5' §11 or as §20 seed data** — it is commercial policy and will be edited far more often than
  code.
  (8) **`ride.requested` carries no `packageSize`.** `rides.rides.package_size` exists (0601,
  CHECK `S|M|L`) but C022's event payload carries `kind` and not the size, and only
  `kind: passenger` is bookable until C037. The P-11 gate is implemented and tested against the
  envelope directly; until C037 adds the field it simply has nothing to reject, which is a missing
  input and not a gate that passes — `candidate_scores.package_size_compatible` stays NULL and says
  so.
  (9) **`RedisKeys.WalletBalance` was added to the kernel.** ADD §6 and D5' §9.2 both name
  `wallet:bal:{driverId}` and `RedisKeys` had no entry; same shape as the `BlockStatus` key C033
  added, on the same hot path.
  **Decisions —**
  (1) **An excluded candidate is written to the audit, not dropped.** R-11's table is asked "why did
  this driver *not* get the ride" more often than the converse, and a row set containing only
  survivors cannot answer it. Every evaluated driver gets a row; `breakdown.rejectedBy` names the
  gate and `rank` is −1, which is a different fact from ranking last.
  (2) **The weights travel with the decision.** `dispatch_algorithm_version` says which formula ran
  and the breakdown says what it ran *with*, so an audit six months later does not need this
  service's configuration — that is what makes DoD 1 a property of the row rather than of the
  process.
  (3) **The reputation gate fails open and says so.** An outage that excluded every driver would
  take the platform down for a signal that removes a handful of them; ADD §12.6 already reserves
  punitive action for a decision somebody made. An unanswered candidate carries
  `blockState: UNKNOWN`, which is a different fact from `OK`.
  (4) **`tripsToday` is counted from `dispatch.offers`, not `rides.rides`.** D5' §2.2 writes it as
  "count(completed+accepted today for driver)", and an ACCEPTED offer is this service's own record
  of exactly that — reading ride-svc's aggregate for a number this bounded context already holds
  would cross the R-01 fence. It also makes D-08's degraded rule work as written: the first-trip
  half survives a billing outage and only the *balance* is ever unconfirmable.
  (5) **A driver with no `billing.accounts` row has a balance of zero, not an unknown one.** D-08's
  "until balance confirmable" is about a read that *failed*; a returned row is a read that
  succeeded. A tier with no `billing.plans` row is refused from the second trip instead, because
  1901 leaves `truck` / `mini_truck` unseeded on purpose.
  (6) **An empty round leaves the ride in Matching.** Only the 120-second deadline or
  `MaxOfferRounds` ends it, because a driver who comes online inside the remaining window is still
  a candidate. And a deadline that arrives while an offer is live reschedules itself past that
  offer's own — §11.12's `ExpiredNoDriver` cell resolves from `Matching` alone, and the one
  candidate the cascade found should get their fifteen seconds.
  (7) **dispatch-svc consumes `telemetry.normalized` itself.** C024's position-processor stopped at
  the three live-map indexes and its CLAUDE.md says why: "a sample carries no driverId, so writing
  `driver:availability:{driverId}` would need a registry lookup this component has no business
  doing on the hot path". dispatch needs no lookup — `dispatch.driver_presence` already holds the
  (driver, vehicle) pair, because the driver told this service which vehicle they went online with.
  It is the platform's second `AutoOffsetReset.Latest` consumer, for position-processor's reason:
  a presence index is current state, and replaying an hour of positions would describe where
  everybody *was*.
  (8) **`last_seen_at` advances on every sample; `geo` only past 25 m.** The freshness gate is about
  liveness, and a driver waiting at a rank is the candidate this service most wants to keep — not
  the first one to drop.
  (9) **A last will starts a clock and an ON_RIDE driver is skipped entirely.** §11.12 gives
  ride-svc four graces on an accepted ride; two services acting on one fact would race, so
  `VehicleStatusService` checks the state before arming anything. A repeated `offline` does not
  extend the grace, or an unstable connection would hold an offer for as long as the instability
  lasted.
  (10) **Two timer tables, two workers, two flags.** `rides.timers.offer_expiry` strands one driver
  when it sticks; the `dispatch.timers` deadline strands a passenger watching a spinner for ever.
  Different failure modes, so an operator can stop one without stopping the other.
  **Test harness —** `DispatchHarness` now starts a **real reputation-svc** beside ride-svc and
  dispatch-svc, so D5' §3.2's gate is a genuine gRPC call on every dispatch test in the assembly.
  That is not thoroughness for its own sake: the gate fails *open*, so an unreachable service, a
  wrong port and a rejected internal key all look like "OK" to a stub. `EmqxFixture` joined
  `DispatchCollection` for the same reason — a refused subscription leaves the client connected and
  simply never delivers a driver going offline. Both `Dispatch.Api` and `Reputation.Api` compile
  `backend/contracts/proto/reputation.v1.proto` (Client / Both); that is only ambiguous if the test
  project *names* one of the generated types, and it does not — if a later test needs to, give the
  `Reputation.Api` reference an `Aliases` rather than copying the proto.
  **For C035 (dispatch-svc-scheduling-levels) —** `dispatch.driver_levels` is read-only for you as
  well (C033 is its sole writer); the level reaches you through
  `IReputationGate` → `GetDriverLevel(...).job_board_eligible`, which is US-6A.8's L1 exclusion
  already precomputed. `dispatch.timers` has `ride_id` and `payload` since 0711, so a T-30 scheduled
  dispatch needs no migration — add a kind and a branch in `DispatchService.RunTimerAsync`, and add
  the kind to `DispatchTimerRepository.ClaimDueAsync`'s `Kinds` array or your rows will never be
  claimed.
  **For C036 (dispatch-svc-directional) —** the DT-02 predicate goes between the hard gates and the
  score (`CandidateScorer.Evaluate`), and `ScoreBreakdown.Directional` is the audit slot DT-02 asks
  for, deliberately left null rather than fabricated. `PresenceService.GoOfflineAsync` and
  `VehicleStatusService` are the two DT-04 clearing points that currently do nothing.
  **For C037 (ride-svc-proxy-package) —** put `packageSize` on the `ride.requested` payload; the
  P-11 gate on this side is finished and waiting for it. `isProxy` / `isPackage` / `packageSize` are
  already carried on `offer.created`.
  **For C046/C047 (wallet, daily fee) —** `wallet:bal:{driverId}` is read-through-populated here
  with a 5 s TTL; you own the `wallet.debited` invalidation. This service only *gates*; the charge
  itself (`billing.daily_fee_charges` + the ledger entry, D5' §2.2) is yours, and a `PAID` row is
  what makes trips 3..N of a day free.
  **Build host —** Docker for Testcontainers (Postgres, Redis, Redpanda, EMQX); the replica stayed
  down throughout. The 108 tests take ~3 min, most of it EMQX start-up.

- **Component:** C035 dispatch-svc-scheduling-levels — 2026-07-29
- **Status:** DONE — `dotnet test backend/src/Dispatch.Api.Tests -c Release` exits 0 (143 tests,
  35 new); `Ride.Api.Tests` (252) and `Reputation.Api.Tests` (84) still green after the Δ on each.
  All four DoD items pass: a Level-1 driver is answered 403 on both the Job Board and
  the intent route with a reason that says it is not a ban; a scheduled ride with no destination is
  400 at the service boundary and nothing is written; the T-30 job goes to the closest
  intent-poster with the higher level winning an exact distance tie; and a penalty settles against
  exactly one ride, with a retry and a later trip both collecting nothing.
- **Notes:**
  **Scope split with C033, and the fence it narrows —** the C034 handoff told this component that
  `dispatch.driver_levels` was read-only for it. That could not be squared with the deliverables:
  D5' §4.2 has four rules, D3' files two of the surfaces that change a level under **dispatch-svc**
  (`POST /v1/internal/drivers/{id}/no-show`, `PUT /v1/admin/drivers/level-config`), and C033's own
  CLAUDE.md hands the level-*up* half over in as many words ("rating collection and the level-*up*
  points … belong to whoever writes ratings"). **Resolution taken:** reputation-svc keeps every rule
  driven by its counters — three confirmed reports → −1 plus the temporary delisting, and the
  US-6A.8 appeal restore, all already built and untouched here — and dispatch-svc owns the level-up
  from `trips.ratings` and the US-6A.7 no-show decrement. Two writers on one row are safe because
  both take `SELECT … FOR UPDATE` first and this side takes *only* that row, which is a suffix of
  C033's documented block state → counters → level order, so no cycle exists.
  **Micro-change-set:** D3' should say which service owns which level rule; today it is derivable
  from the route table and nowhere stated. `Reputation.Api/CLAUDE.md`'s "sole writer" bullet has
  been narrowed in place.

  **Spec gaps — micro-change-sets.**
  (a) **A scheduled ride has no payment method.** `POST /v1/rides/schedule` (Δ 2026-06-28, AL-36)
  takes destination, pickup, time and tier; at T-30 the row becomes a `rides.rides`, whose
  `payment_method` is NOT NULL over a closed set. Landed as an optional `paymentMethod` on the
  contract defaulting to `cash` — **D3' should print it**. Hard-coding cash in the service would
  have taken the passenger's choice away silently.
  (b) **`rides.rides` has no `scheduled_at`, but `RideDetail.scheduledAt` is in the contract.**
  The materialised ride therefore does not carry its own booked pickup time;
  `dispatch.scheduled_rides.pickup_time` is where it lives and `GET /v1/rides/scheduled/{driverId}`
  is what shows it to the driver. **Either D4' §5 should add the column or D3' should drop the
  member.**
  (c) **A materialised ride has no quote.** `POST /v1/internal/rides/scheduled` takes no
  `fareEstimateToken` — a quote taken when the passenger booked is not the price of a ride 30
  minutes from now (D5' §1.4) — so `fare_estimate_minor` is NULL and `offer.created` carries no
  `fareEstimateMinor`. **fare-svc meters it; D5' §1.4 should say so for scheduled rides.**
  (d) **D5' §7.1's `UNIQUE(penalty_id, applied_ride_id)` guards nothing**, which 0706's own header
  already said: `id` is the primary key, so the pair is unique by construction. The real guard is
  the conditional `UPDATE … WHERE status = 'OUTSTANDING'` over a `FOR UPDATE SKIP LOCKED` claim.
  **§7.1 should name that instead of the index.**
  (e) **The Rs 50 is not the only debt settled on the next trip.** §11.12 marks all three penalty
  bases `settledOn: next_trip` — the Rs 50 `cancellation_fee`, the Rs 100 `no_show_fee` and the
  mid-trip `full_fare` — but §7.1 writes the ledger row only for the Rs 50. All three are recorded,
  with a `basis` column, because US-6A.10b's "clear outstanding balance" cannot be evaluated against
  a table holding one of the three. **§7.1's pseudocode should generalise.**
  (f) **`LevelConfig.cancellationPenaltyPoints` has no rule.** The contract names the knob; §11.12
  gives a driver cancellation a reputation hit and a brief delist, both reputation-svc's, and no
  spec gives it a level or a point cost. It is stored and round-tripped by the admin route and
  **nothing reads it** — said plainly in `Dispatch.Api/CLAUDE.md` rather than wired to a
  non-idempotent deduction off `reputation.driver_cancelled`, which a redelivery would double.
  (g) **No spec says what happens to a scheduled ride nobody posted intent on.** D5' §3.7 names
  intent-submitting drivers and nobody else, so that is what was built: the cascade stays inside the
  intent list on every round and the ride ends `ExpiredNoDriver` at the ordinary 120 s deadline
  rather than falling back to the open pool. **This is a product decision, not a technical one** —
  falling back would offer an advance booking to a driver who never opted in.
  (h) **No spec pins** the booking floor and ceiling (30 min / 30 d), the T-30 materialisation
  retry grace (30 min) or the two sweep intervals. Each is argued at its declaration in
  `DispatchOptions`.

  **Δ on other services.**
  (1) **ride-svc gained `POST /v1/internal/rides/scheduled`** (contract + service). It is the fourth
  command of the family C022/C023/C034 built, and for the same reason: `dispatch.offers.ride_id` has
  a foreign key onto `rides.rides`, so the T-30 offer cannot exist before the ride, and R-01 forbids
  dispatch creating it. `POST /v1/rides/request` cannot serve — it is Bearer-authenticated as the
  passenger and demands a fare token. **Idempotent because the scheduled-ride id is the
  `clientRequestId`**, so R-18's `ux_rides_idem` turns a retried sweep into a replay.
  `NewRide.FareEstimateMinor` became `long?` for the same call; `RequireImmediate`'s message now
  points at the right endpoint instead of saying the feature does not exist.
  (2) **dispatch.yaml gained** the optional `paymentMethod` and the two internal penalty routes
  `GET`/`POST /v1/internal/passengers/{id}/penalties[/settle]` — D5' §7.1 has fare-svc read the debt
  before pricing the next trip and mark it settled after posting the ledger entries, and D3' names
  no route to reach a ledger this service owns.
  (3) **`migrate-verify.sh`** moved to 14 dispatch tables and gained six checks over 0713.

  **Migration 0713** — one file, five changes, each argued in the header:
  `scheduled_rides.payment_method` + `ux_sched_ride` + `ix_sched_passenger`;
  `driver_levels.points_awarded_total` (the level engine's idempotency watermark);
  `ux_no_show_driver_ride` (one decrement per missed ride, whatever the delivery count);
  `cancellation_penalties.basis` + `ux_penalty_accrual` (the at-least-once guard §7.1's index is
  not); and the singleton `dispatch.level_config`, the only new table.

  **Decisions —**
  (1) **The booking table is its own timer, and no `dispatch.timers` row is armed.** The C034
  handoff suggested a new timer kind, but `dispatch.timers.ride_id` has a foreign key onto
  `rides.rides` and at T-30 the ride is precisely what does not exist yet. `ix_sched_due` (0704) is
  already a partial index on `pickup_time WHERE status='SCHEDULED'` — "the next thing to fire" — and
  the status column is the claim.
  (2) **The sweep materialises and stops; `ride.requested` dispatches.** One dispatch path, driven
  by one event, rather than a second entry point racing the sweep for the same ride. It also means
  a scheduled ride goes through exactly the cascade every other ride does.
  (3) **A scheduled round is intent-only, on every round, at the 30 km board radius.**
  `DispatchService` looks the booking up by ride id (`ux_sched_ride`) so no caller has to carry the
  fact — a decline, an expiry and a redelivered `ride.requested` all take the same branch. Keeping
  the 5 km on-demand radius would have dropped the very drivers who chose the ride.
  (4) **§3.7 is a different rule from §3.3, not a re-weighting of it.** The Job Board dispatch
  orders by distance and breaks ties on level; the weighted score is still computed and stored and
  `breakdown.ordering` says which rule produced the `rank`, so an audit row whose rank disagrees
  with its score reads as a different rule rather than as a bug. Folding it into the weights would
  put a distant Level-3 driver ahead of a near Level-2 one, which is what "closest … by Level" does
  not say.
  (5) **The level engine is a recompute against a watermark, not a consumed-events queue.** Points
  are summed from `trips.ratings` and only the delta is applied, so a read, the sweep, two replicas
  and a crash mid-update all award the same points once. A rating is not on any topic D6' §2.1
  declares, so there was nothing to consume in any case.
  (6) **A level once earned is not un-earned by the evidence disappearing.** A rating total that
  falls (a PDPA erasure) moves the watermark and leaves the level alone — §4.2's level-down list is
  three reports and a no-show.
  (7) **Ratings are counted only for `subject_kind='ride'`.** This level gates Mode C dispatch and a
  Mode A/B session rating is trip-state-svc's plane; CLAUDE.md's boundary rule is explicit.
  (8) **The no-show insert is the claim, and the level row is locked before it.** The two are one
  atomic act, so two deliveries cannot both find the row unclaimed. A report with no `rideId` cannot
  be deduplicated and is counted as given — which is why the index is partial.
  (9) **An empty settlement and "nothing owed" are the same answer.** fare-svc adds what the settle
  call returns to the fare, so a retry that re-reported the debt would charge it twice.
  (10) **`acceptanceRate` is 1.0 for a driver who has never been offered anything.** The number is
  shown to the driver and read by support; 0 would describe a refusal that never happened.

  **Two bugs caught by the tests, both the same shape —** a bare column list reused in a joined
  query. `dispatch.offers` also has `id`, `ride_id` and `status`, so
  `ScheduledRideRepository.AssignedToDriverAsync` answered an ambiguous-column 500; the penalty
  settlement's `RETURNING` had the same problem against its claim CTE. Both now use an explicitly
  qualified list.

  **For C036 (dispatch-svc-directional) —** `PUT /v1/admin/dispatch/directional-config` is the last
  unmapped route in `dispatch.yaml` and `dispatch.directional_config` (0707) is already seeded and
  verified. `InternalDispatchEndpoints` is where an internal route goes and
  `Dispatch:InternalApiKey` already gates the group. `CandidateOrdering` is the enum to extend if
  DT-02 ever needs its own ordering; `ScoreBreakdown.Directional` is still the null audit slot.
  **For fare-svc (C045) —** `GET /v1/internal/passengers/{id}/penalties` before you price a
  completed trip and `POST …/settle` after you post the ledger entries; the `basis` member tells you
  when `amountMinor` is the quoted fare to be re-metered rather than the amount. Nothing here writes
  `billing.journal_entries` (D-09).
  **For whoever collects Mode C ride ratings —** write `trips.ratings` with
  `subject_kind='ride'`, `direction='passenger_to_driver'`; the level engine needs no event and no
  call, it recomputes.
  **Build host —** Docker for Testcontainers (Postgres, Redis, Redpanda, EMQX); the replica stayed
  down throughout. The suite takes ~4 min, most of it EMQX start-up.

- **Component:** C036 dispatch-svc-directional — 2026-07-29
- **Status:** DONE — `dotnet test backend/src/Dispatch.Api.Tests -c Release` exits 0 (180 tests, 37
  new); `MageRide.Shared.Tests` (235) still green after the `GeoMath` addition, and the whole
  solution builds. All four DoD items pass: a ride heading away from the driver's destination is
  filtered out and the decision is recomputable from `candidate_scores.breakdown.directional` (both
  bearings, the three measurements, the three thresholds that were live); the third activation in
  one Asia/Colombo day is `409 directional-limit-reached` with two rows written and not three;
  going offline clears the filter with reason `offline` and emits `directional.cleared`; and a
  driver who sits out three rides on a filter loses no acceptance rate, gains no no-show, and
  collects no block state.
- **Notes:**
  **No migration.** 0707 already had `dispatch.directional_filters` (one row per activation, the
  `used_date` + `used_date_tz_at` D-38 pair, `ux_directional_active`) and the seeded singleton
  `dispatch.directional_config`; 0708 left `dispatch.timers.kind` deliberately without a CHECK, so
  `directional_reminder` joined `directional_expiry` without one either. `migrate-verify.sh`'s
  14-table count is unchanged.

  **Decisions —**
  (1) **The predicate reads Postgres, not `driver:directional:{driverId}`.** ADD §7.4 and §11.11
  both say dispatch reads the Redis hash "for each surviving candidate". A Redis miss and "this
  driver has no filter" are the same answer to a reader, so a flushed keyspace would switch the
  feature off in silence — the predicate would stop excluding anything while the durable rows still
  said otherwise, and the only visible symptom would be drivers getting rides the wrong way with
  nothing anywhere saying why. One indexed batched query per round, on a path that already takes
  several, buys a failure mode that is loud instead. The hash and the day counter are still written
  with ADD §9.4's shape and its PEXPIRE, and are read by nothing here — the same position C034
  recorded for `driver:availability`'s `level`/`walletOk`. **Micro-change-set:** ADD §7.4/§11.11
  should say the durable row is the read and the key is a hint, which is what §9.4's own
  "authoritative expiry in `dispatch.timers`" already implies for the other half of the same key.
  (2) **DT-03 is `COUNT(*)` inside the INSERT that consumes it.** The count is a subquery in the
  activation's `WHERE`, so two taps arriving together cannot both spend the last use and zero rows
  back *is* the 409. This is also what makes US-6A.19 fall out of the schema rather than out of a
  decrement somebody has to remember: a turn-off marks the row cleared and leaves it counted.
  **Micro-change-set:** ADD §1.15's DT-03 cell still mentions a `use_count` column that neither
  DDL source has; §9.1 and 0707 use the row-per-activation form and are right.
  (3) **A second filter while one is live is a 409, not a replacement.** No spec says. Replacing
  would make re-pointing a destination free, which is exactly the gaming US-6A.19 exists to stop,
  and `ux_directional_active` refuses it in any case. The message says to `DELETE` first and that
  doing so still costs the use.
  (4) **Directional is last in the `rejectedBy` chain, and that ordering is DT-05 itself.** A driver
  the block-state or wallet gate already refused reads as refused by that; the clause has no branch
  that re-admits anybody. Asserted directly — a delisted driver whose filter matches perfectly is
  audited as `block_state`, with `directional.matched: true` on the same row.
  (5) **Two timer kinds, because `ux_dispatch_timers_driver_live` is per (driver, kind).** One row
  cannot carry two fire times. Both are retired when the filter clears, so a driver who turns theirs
  off after two minutes is not warned eight minutes later. `ClaimDueAsync` now names four kinds:
  a claimed row's `fire_at` is pushed out by the lease, so an unhandled kind would be deferred for
  ever rather than left visibly due.
  (6) **Every one of DT-04's four paths is the same conditional `UPDATE … WHERE cleared_at IS
  NULL`.** They race by construction — a driver going offline as their filter expires triggers two —
  so first writer wins the reason and the others emit nothing. Tested.
  (7) **`directional.cleared` / `directional.expiring` are keyed by the driver.** `dispatch.events`
  is keyed by `rideId` (D6' §2.1) and neither event has a ride. **Micro-change-set:** D6' §2.2
  prints no schema for either event and §2.1's partition-key column has no cell for a dispatch event
  that is not about a ride; 0709's `aggregate_id` comment still reads "rideId".
  (8) **A ride with no drop-off keeps the candidate.** `rides.rides.dropoff_geo` is NOT NULL and
  every `ride.events` payload carries it, so this is unreachable today — but DT-05 bounds this
  predicate to *removing* candidates, and a removal justified by a measurement never taken is a
  guess. The audit row says `failedOn: "unevaluable"`.
  (9) **`clear_on_first_trip` fires on the accept, not the offer**, and stays off by default. D5'
  §12 asks for neither behaviour; the column exists, so the flag is honoured and nothing more. A
  filter ended by a ride the driver went on to decline would be spent on nothing.
  (10) **`GeoMath` went into the kernel, not into dispatch-svc.** A bearing is not a dispatch
  concept and `Fare.Api`'s private `HaversineKm` is already a second copy of half of it.
  **Micro-change-set:** collapse `FareEstimator.HaversineKm` onto `GeoMath.DistanceM` — not done
  from under fare-svc, whose own component owns that file.

  **Δ on the shared kernel and the test harness —** `MageRide.Shared/Geo/GeoMath.cs` is new
  (distance, initial bearing, compass angular difference). `RideDispatchRequest` gained a nullable
  `Dropoff`, which is the first thing in this service to need one — the candidate set is still built
  entirely from the pickup. `OfferLoopTests.BuildRequestAsync` now reads `dropoff_geo` alongside
  `pickup_geo` so every suite drives the envelope ride-svc would actually produce.

  **A note on the geometry the tests use —** a "wrong way" destination is not enough to isolate the
  bearing clause: Negombo (north) also fails the progress test, because Dehiwala is further from it
  than Colombo Fort is. Widening θ_max therefore changes nothing for that pair, and the admin-config
  test needs a destination — ~84 km due east — for which the bearing is genuinely the only clause
  that refuses. Both the unit and the integration test assert `failedOn` before asserting the flip,
  so neither can silently start proving something else.

  **For C037 (ride-svc-proxy-package) —** `RideEventPayload.PackageSize` is still the member
  ride-svc does not produce (C034's gap, unchanged); the P-11 gate and the DT-07 predicate both
  compose with it already and neither needs a change when you add it. `DirectionalBreakdown` and
  `PackageCompatibility` are independent — a package ride is filtered directionally on the same
  pickup→dropoff vector as any other kind.
  **For C051 (driver app) —** `GET /v1/standby/directional` is the banner (US-6A.21):
  `timeRemainingSec` and `usesRemaining` are both there, and `usesRemaining` also rides on
  `directional.cleared` so the banner can repaint from the push alone. The DT-08 badge on the
  incoming-request overlay is `directionalMatched` on `offer.created`.
  **For notification-svc (C049) —** `directional.expiring` on `dispatch.events`, partition-keyed by
  driver, carries `notificationType: DIRECTIONAL_EXPIRING`, `minutesRemaining` and `expiresAt` and
  no rendered text. The trilingual template, the channel and the driver's preferences are yours;
  dispatch-svc owns only the clock. `directional.cleared` is the other one, carrying its `reason`.
  **Build host —** Docker for Testcontainers (Postgres, Redis, Redpanda, EMQX); the replica stayed
  down throughout. The suite takes ~4 min, most of it EMQX start-up.

- **Component:** C037 ride-svc-proxy-package — 2026-07-29
- **Status:** DONE — `dotnet test backend/src/Ride.Api.Tests -c Release` is **314/314 green** (was
  252; 62 new). All four DoD items are covered by named tests. (1) *Both channels, and the driver
  sees the rider* — `ProxyBookingTests` walks a proxy ride to `PaymentPending` and asserts
  `bookerId` **and** `riderId` on every one of its six events, then asserts the driver's
  `counterpartyPhone` is the rider's number and the booker's appears nowhere in the payload.
  (2) *A decline stores the decision and no coordinates* —
  `A_declined_request_stores_the_decision_and_transmits_no_coordinates` checks the row, the event
  and the response, and that a later confirm is 410. (3) *A sixth wrong pickup OTP* —
  `A_sixth_wrong_pickup_code_is_locked_and_the_admin_queue_has_been_raised`: five `invalid-otp`,
  one `package.otp_locked`, the sixth `423`, and the *correct* code refused after it. (4) *Photo
  proof* — the ride reaches `PaymentPending`, the artifact row exists, and its `sha256` matches the
  bytes actually on disk. `bash infra/scripts/migrate-verify.sh` is **213/213** (two new checks);
  Spectral is clean; `Reputation.Api.Tests` (84), `Dispatch.Api.Tests` (180) and the solution build
  were re-run green.
- **Notes:**
  **Spec gaps and conflicts — micro-change-sets (11).**
  (a) *A package has a recipient and `rides.rides` has nowhere to put one.* D3' `RideRequest`
  carries `recipientName`/`recipientPhone`, AL-21 makes the recipient the subject of a notification
  at pickup-confirm, AL-33 puts a call button in front of the driver for them — and neither DDL
  source has a column. `rider_*` was not overloaded: `ride.yaml` says a package has "no rider at
  all", so filling `riderId` would make `RideDetail` claim somebody is the rider who is not.
  **Migration 0609 adds `recipient_name` + `recipient_phone` + `ck_rides_package_recipient`; D4' §5
  and server_db_schema.md §5 need all three.**
  (b) *The recipient's number is stored in the clear and the proxy rider's is not.* P-03 hashes the
  unregistered rider because nothing ever has to dial them; AL-21 must SMS the recipient and AL-33
  must let the driver ring them, so a digest would leave both unimplementable. No spec asks for the
  recipient to be hashed — it has no column at all — so the number is kept as `iam.users.phone`
  keeps one.
  (c) *ADD §11.15 asks for a `rides.timers` row that cannot exist.* `kind='location_request_expiry'
  fire_at=now()+5min`, but `rides.timers.ride_id` is `NOT NULL REFERENCES rides.rides(id)` (0605)
  and the request is issued **before** the ride — which is exactly why
  `rides.location_requests.ride_id` is nullable (0606). The durable deadline is the request row
  itself (`issued_at + ttl_seconds`), swept in the same worker pass over the new
  `ix_location_requests_due`. R-04's property is unaffected: the durable row decides, not a process.
  **ADD §11.15 should stop naming a timer row, or 0605 should make `ride_id` nullable.**
  (d) *`otp_attempt_window` has a CHECK value and no duration anywhere.* P-07 says "max 5 attempts
  each → admin queue" and names no window. Left unarmed and unclaimed: a timer that reset the
  counter would hand an attacker unlimited tries at a 10⁴ code by waiting, and a locked handoff is
  unlocked by support, not by the clock. **P-07 should either give the window a number or the kind
  should leave `ck_timers_kind`.**
  (e) *The delivery OTP cannot be both "generated at ride creation" and sent at pickup.* D5' §11
  has both codes minted at booking and their plaintext leaving "exactly once"; ADD §11.16 hands the
  delivery code to the recipient at **pickup**, an hour later, by which time the server holds only a
  digest. Resolved by minting a code at booking (so `ck_rides_package_complete` holds at `INSERT`)
  and re-minting the one actually sent inside the same statement that takes the pickup gate. The
  code exists in the clear for one hop instead of for the whole booking, which is the better half of
  the trade. **D5' §11 and ADD §11.16 should agree on which moment issues it.**
  (f) *`RiderNotRegistered` is terminal in ADD §11.15 and answerable in AL-45.* AL-45 is later and
  wins — notification-svc SMSes a `pickup_confirm` link and SCR-WT-003 feeds the same machine — so
  the state is **live**, runs down the same 300 s clock, and US-8.19's booker fallback is what
  happens if nobody answers rather than the only path. **ADD §11.15's not-registered branch needs
  rewriting to match AL-45.**
  (g) *AL-45 gives public-bff no route to call.* SCR-WT-003 must move a row in
  `rides.location_requests`, which ride-svc owns. Added
  `POST /v1/internal/location-requests/{id}/confirm|decline` — the same shape and the same argument
  as the five internal commands dispatch-svc drives. **D3' should carry the pair.**
  (h) *AL-33 needs two numbers and `RideDetail` carries one.* The delivery sheets put a call button
  beside the sender *and* the recipient. Added `senderPhone` / `recipientPhone` to `RideDetail`,
  package-only and on exactly AL-48's terms (from `Accepted`, to a participant, never otherwise).
  (i) *`RideDetail.packageStatus` has four values and the aggregate has three positions.* The
  machine is kind-agnostic, so `PickedUp` and `InTransit` are the same state; `InProgress` renders
  as `InTransit` and the instant of pickup is the `package.picked_up` event. **`ride.yaml`'s enum
  should drop one or D4' should give the aggregate a column.**
  (j) *No spec names an event for the P-07 admin queue, and D6' §2.4 makes the outbox the only way
  one context asks another for something.* `package.otp_locked` is coined here, carrying the gate
  and the attempt count. **D6' §2.2 should register it**, alongside `location.request.*`, which the
  ADD describes as outbox rows and never names.
  (k) *The proof-photo route's contract cannot express what P-10 stores.* Its multipart schema is
  `file` + `note`; `rides.proof_artifacts.captured_geo` exists and D5' §11 names it as part of the
  proof. Added optional `lat`/`lng`; `note` stays accepted-and-dropped because no column holds it.
  **A genuine conflict, and it is not resolved — it is decided.** *AL-48 asks the API to hand the
  driver the rider's real MSISDN; P-03 stores an unregistered proxy rider as a keyed digest.* Both
  cannot hold, and nothing downstream can invert an HMAC. P-03 is the narrower, privacy-bearing rule
  and it wins: `counterpartyPhone` is **absent** on that one combination, never filled with the
  booker's number (P-05 forbids it outright), and the driver falls back to the in-app channel while
  the booker relays. The same gap makes AL-33's call button unreachable for an unregistered
  *recipient* — except that C037 stores the recipient's number in the clear (note (b)), so that half
  works. **The specs need to say which of P-03 and AL-48 governs an unregistered proxy rider.**
  **Decisions —**
  (1) **No new states, and the gates prove it.** ADD Appendix B.2 invariant 6 says the machine is
  kind-agnostic; `RideTransitions` and `RideCancellationMatrix` gained exactly one row between them
  (P-14's `PaymentPending × CodUncollected → Disputed`). The pickup OTP takes the same
  `Accepted|DriverArrived → InProgress` edge `start` takes and the delivery OTP the same
  `InProgress → Completed → PaymentPending` pair `complete` takes. A gate decides *whether*, never
  *where to*.
  (2) **Both events at every gate.** `package.picked_up` co-fires with `ride.started` and
  `package.delivered` with `ride.completed`. Spelling a package's completion only the new way would
  leave dispatch-svc — which releases the driver on `ride.completed` — holding a ghost-busy driver,
  which is precisely the R-20 alert C034 built.
  (3) **A correct OTP never spends an attempt, and neither does a malformed one.** The gate is two
  conditional `UPDATE`s: one guarded on the digest matching (and on the budget, the driver, the kind
  and the state), one guarded on it *not* matching. No attempt can be counted twice, none can be
  counted for four characters that are not four digits, and a wrong code is **committed** —
  rolling it back would make the budget unenforceable and hand an attacker all ten thousand codes.
  The attempt that exhausts the budget raises the queue item, because the delivery is stuck the
  moment the last try is used and waiting for a sixth would leave a driver standing at a door with
  nobody notified.
  (4) **`passenger_id` is the booking account on all three kinds.** D4' annotates `booker_id`
  "= passenger unless proxy", which reads as though a proxy ride's `passenger_id` should be the
  rider — but that column is `NOT NULL REFERENCES iam.users(id)` and P-03's whole point is that a
  proxy rider may have no account, so the reading is unsatisfiable for the case it was written for.
  R-18's key, AL-16, `ux_rides_open_passenger` and the money all belong to the account that booked.
  **Consequence worth knowing:** a booker can hold one open ride, so they cannot book proxy rides
  for two people at once. No spec grants that; if the product wants it, `ux_rides_open_passenger`
  is what has to change.
  (5) **`cod-collected` settles the ride, and that is not an R-05 exception.** The three gateway
  terminals are states fare-svc *observes*; cash in a driver's hand is observable by nobody, and
  D5' §6 draws `PaymentPending --> CashOnDeliveryCollected: COD confirmed (package, P-08)` as an
  edge of the **ride** machine. It emits P-08's named `payment.cod_collected` *and* the ordinary
  `ride.settled`, so fare-svc and billing read one authorisation shape for all four terminals.
  (6) **P-14 is a matrix row, not a code path** — as the C032 handoff predicted.
  `cod_uncollected` is armed at the **pickup** (ADD §11.16: the clock is about money in transit, and
  the delivery may itself be what never happens), survives every lifecycle move exactly as
  `offline_grace` does, and is retired by the terminal the driver's tap produces. The tap and the
  clock race; whichever lands first leaves the other nothing to do.
  (7) **The P-12 rate limit is Postgres, not Redis.** D5' §10 names a token bucket; ride-svc holds
  no Redis connection at all, which is what makes R-04's "independently of any Redis TTL"
  structural. `ix_location_requests_booker` exists in 0606 for exactly this count, and counting
  inside the transaction that inserts the next row means a request that rolled back never spent a
  token — which a bucket decremented before the write would have. It is checked **before** the
  iam-svc lookup, so a booker who has run out cannot keep using the registration oracle for free.
  (8) **The registration check is a call; the phone read is a join.** "Is this number registered" is
  an oracle, and iam-svc answers it behind `iam.phone_lookups`, which records who asked (C027) —
  bypassing that would remove the control. `counterpartyPhone` is the opposite: iam-svc publishes no
  "number for this user id" route, so it is a read-only join into `iam.users` on exactly the footing
  `DriverSummaryRepository`'s join into `registry.vehicles` already has, and C048 replaces both.
  **iam-svc could use a `GET /v1/internal/users/{id}/phone` — raised for C027's owner.**
  (9) **An iam-svc outage is a 503, not an assumption.** Guessing "unregistered" SMSes a stranger
  because a service was restarting; guessing "registered" pushes an FCM message into the void and
  leaves the booker watching a request that can only expire. `Ride:IamBaseUrl` unset gets the same
  answer from a null object rather than unmapping the routes — a missing setting should be visible
  on the route that needs it, not look like a deployment that never had the feature.
  (10) **A decline cannot leak a position, in three independent ways.** The route takes no body; the
  `UPDATE` has no `resolved_geo` in its `SET` list; the event's `geo` is filled from a column that is
  NULL by construction. P-02's fence is a property of the code rather than of a reviewer's care.
  (11) **The proof photo is written before the transaction.** A ride that moved in between leaves an
  orphan file rather than a completion with no proof behind it — the file is recoverable evidence
  and a missing artifact row would not be. The `sha256` is computed over the bytes as written, in
  one pass, so it describes the file that actually exists.
  (12) **A leak the review caught, not the tests.** `counterpartyPhone` was first resolved for
  anyone `RideRow.IsParticipant` admits — which includes the driver an offer was merely *reserved*
  for. Because ADD §11.11's accept is deliberately not bound to `offered_driver_id`, a driver who
  held the offer and lost the race would have been handed the winner's MSISDN. The rider side is now
  named explicitly (passenger, booker, rider) and
  `A_driver_who_only_held_an_offer_gets_no_number` pins it.
  **One test in another component had to change —** `WalletGateTests.A_tier_with_no_billing_plan_is_
  refused_from_the_second_trip` booked `mini_truck` as a *passenger* ride to reach a tier migration
  1901 leaves unseeded. Δ C037 enforces AL-09's "+truck|mini_truck for **package delivery**" on
  `POST /v1/rides/request`, so that booking is now a 400. `DispatchHarness.RequestRideAsync` gained
  an optional `packageSize` and the test books two deliveries instead — which changes nothing it
  asserts, because P-06 counts deliveries and passenger rides together for the daily fee. The fence
  was previously unenforced anywhere: a passenger could book a truck and dispatch would look for a
  driver to carry them in it.
  **Two findings worth acting on elsewhere —**
  (a) **The kernel's R-14 command log stores response bodies, so the pickup OTP plaintext is at rest
  in `rides.command_log.response_body`.** D3' requires `pickupOtp` on the 202 and R-14 replays the
  stored body verbatim (which is what makes a retry under the same header key work at all), so the
  two requirements meet in a JSONB column. P-07's "hashed at rest" is about the aggregate and holds
  there. **A redaction hook on `IdempotencyMiddleware` belongs in C002's kernel**, not in a service.
  (b) **`FileSystemProofPhotoStore` is not object storage.** D-36 puts every upload on SSE-KMS
  buckets, the dev compose runs MinIO, and no service in this build has an S3 client. `IProofPhotoStore`
  is one method wide and the digest, the artifact row and the state change — everything the AL-44
  receipt and a §11.14 dispute are built from — are in Postgres either way. **Whoever lands the
  object-store client should implement it here first.**
  **For C051 (notification-svc) —** four hand-offs, all on `ride.events`. `location.request.issued`
  carries `state` (`Pending` ⇒ FCM data message to `riderId`; `RiderNotRegistered` ⇒ mint AL-45's
  `pickup_confirm` token and SMS `riderPhone`, which is on the payload and is the one place an
  unhashed number appears) plus `expiresAt` for the 300 s countdown. `package.picked_up` carries
  `deliveryOtp`, `recipientPhone` and `recipientName` — AL-21's branch is yours: registered
  recipient ⇒ FCM deep link, unregistered ⇒ SMS a `safety.trip_share_tokens` link. Every proxy
  event names `bookerId` **and** `riderId`, and both are told about every state change (P-05
  constrains who the *driver* reaches, not who is notified). `package.otp_locked` is the admin-queue
  item; it carries `gate` and `attempts`.
  **For C053 (fanout-svc) —** `location.request.confirmed|declined|expired` are keyed by
  **`requestId`**, not by a ride, and the envelope carries `requestId` at its top level beside
  `eventId`/`eventType`. The group is `booker:{bookerId}:loc-req:{requestId}` and both halves are on
  the payload. `geo` is present on `confirmed` alone.
  **For public-bff (AL-44/AL-45) —** `POST /v1/internal/location-requests/{id}/confirm|decline`
  behind `X-MageRide-Internal-Key`, with the same 410 `token-expired-or-revoked` the Bearer pair
  answers. Burn the `pickup_confirm` token on your side; ride-svc asserts no rider identity on that
  path because an unregistered rider has none. The delivered-page outcome D4' calls derived reads
  off `rides.proof_artifacts` (⇒ `photo_proof`), the `CashOnDeliveryCollected` terminal
  (⇒ `cod_collected`) and `Disputed` (⇒ `disputed`); everything else is `otp_verified`.
  **For C034/C036 (dispatch-svc) —** `RideEventPayload.PackageSize` is **now produced**, which
  closes the gap C034 recorded and C036 repeated: the P-11 compatibility gate has its input and
  `dispatch.candidate_scores.package_size_compatible` stops being permanently NULL for real
  bookings. `packageDescription` rides with it, for the driver's own rejection decision.
  **For C049/C050 (fare-svc) —** `payment.cod_collected` is the COD sibling of `ride.settled` and
  carries the identical `RideSettlementPayload` with `earningPayable: true`; its `paymentId` is the
  **ride id**, because `fares.ride_payments` does not exist yet and the ride is the only aggregate
  with an identifier for that settlement. Replace it when you own the payment row. A package's fare
  is the ordinary tariff and its delivery counts toward the daily fee exactly as a passenger ride
  does (P-06 — "deliveries and passenger rides are interchangeable for fee purposes").
  **For C065 / PDPA —** an erasure has three new places to reach in `rides`: `recipient_phone`
  (clear text), the two `rider_phone_hash` columns (digests, not reversible but still linkable) and
  `rides.proof_artifacts.storage_url`, whose bytes live outside Postgres.
  **Build host —** Docker for Testcontainers (Postgres, Redpanda) and one throwaway
  `timescaledb-ha:pg16` for `migrate-verify.sh`; the replica stayed down throughout. The ride suite
  takes ~2 min 55 s, the migration verify ~4 min.

- **Component:** C038 mqtt-bridge-svc — 2026-07-29
- **Status:** DONE — `dotnet test backend/src/HotPath.Tests -c Release --filter Category=MqttBridge`
  is **13/13 green**; the whole suite is **43/43** (was 35). All four DoD items have a named test.
  (1) *N replicas ingest each message exactly once under load* —
  `Three_replicas_under_load_ingest_each_message_exactly_once` publishes 240 samples from 30
  devices across 3 replicas and asserts the delivered `(vehicleId, seq)` pairs are 240 **distinct**
  values, not merely 240 records: a bridge that dropped one and duplicated another passes a count
  and fails this. (2) *A replay flood is rate-limited without measurable added latency on live
  samples* — `The_backlog_stream_is_held_to_its_per_device_rate` (nothing lost, paced to the
  configured rate, and `ReplayThrottled > 0` so it was **this** bridge that paced it) plus
  `A_backlog_flood_does_not_delay_live_samples` (every live sample under 1 s bridge-side while the
  backlog is still queued — the read of `ForwardedReplay` happens *before* the flood is awaited, so
  a drained backlog cannot make it pass vacuously). (3) *Per-vehicle order preserved end to end* —
  `Per_vehicle_ordering_holds_across_replicas`, 30 samples through **two** replicas, which is the
  case that can actually break. (4) *A rate_violation for a client publishing above 5 msg/s* —
  `A_vehicle_over_the_ceiling_raises_a_rate_violation_on_audit_events`, with
  `A_vehicle_within_the_ceiling_raises_nothing` as its negative. `MageRide.Shared.Tests` (235) and
  `Dispatch.Api.Tests` (180) were re-run green after the `IEventPublisher` change; the solution
  builds clean.
- **Notes:**
  **The one thing to read first: this component changed a broker setting.**
  `infra/deploy/emqx/emqx.conf` now sets **`mqtt.shared_subscription_strategy = sticky`**. EMQX 5.8
  defaults to `round_robin` — verified against the image, not assumed — which picks the next member
  of the group for **every message**. Two bridge replicas then take one vehicle's samples
  alternately and race each other to `telemetry.raw`, so the per-vehicle ordering ADD §7.3 and
  D6' §2.1 promise *end to end* is decided by which process wins. A Redpanda partition key keeps a
  partition ordered; it cannot reorder what arrived scrambled, and C038's DoD asks for the
  end-to-end property explicitly. `sticky` binds a publishing session to one group member, so load
  balancing becomes per **device** rather than per **message** — which is how a fleet actually
  distributes — and E-08's substance (exactly-once dispatch, no duplicate ingest, redistribution on
  replica loss) is untouched. **C024's E-08 test had to change shape with it**: "both replicas took
  a share of one handset's 40 messages" is no longer true by construction, so it now publishes from
  16 devices and asserts both replicas took a share of *those* (the sticky pick is random per
  publishing session, so the chance of either taking none is 2 × 2⁻¹⁶). The exactly-once half of
  the assertion is unchanged and is still the important half. **C125 owns the deployed broker
  policy — carry this setting forward.**

  **Spec gaps and conflicts — micro-change-sets (4).**
  (a) *"Replicas commit Redpanda offsets per partition" (ADD §7.3) describes something a producer
  cannot do.* Committing offsets is a consumer-group operation against offsets you have **read**;
  the bridge only writes. The guarantee the sentence is reaching for is real and is now
  implemented: the bridge learns *where the broker put a record* — partition and offset, off the
  delivery report — **before** it acknowledges the MQTT message, so an acknowledged payload is
  never one Redpanda did not take. `PartitionOffsetLog` records the per-partition high-water mark
  and publishes it as `mageride.mqtt.bridge.partition_offset`. **ADD §7.3 and
  `mqtt-topics.md` §4 should say "confirm the per-partition write before acknowledging", not
  "commit offsets".**
  (b) *D-17's detection cannot live where D6' §3.3 puts it, and the ceiling it names cannot be
  enforced where the ceiling actually is.* Three separate problems with one fix. The spec asks the
  EMQX rule engine for a `TUMBLINGWINDOW` aggregate — which the spec itself prints as
  *illustrative*, and which open-source EMQX 5.8 has no windowed aggregation for. The listener
  limiter that **does** enforce (`messages_rate = "5/s"`, measured at 4.9/s on a single socket with
  no burst) emits no event at all. And that limiter is **per connection** while D-17 is written
  **per `vehicleId`**: `acl.conf` confines every session presenting one vehicle credential to that
  vehicle's topics but does not stop a device opening four of them, at which point the broker sees
  four compliant clients and the platform sees one vehicle publishing at 20 msg/s. mqtt-bridge-svc
  sees every live sample exactly once (E-08) across *all* of a vehicle's connections, which makes
  it the only component that can measure the rate D-17 names — so **the bridge counts and reports,
  EMQX enforces**, and the bridge never drops a sample (a position dropped here is one anti-spoof
  never gets to look at). The test raises the violation exactly that way, over four connections.
  **D6' §3.3 should name mqtt-bridge-svc as the detector and drop the rule-engine SQL.**
  (c) *`audit.events` had a schema and no producer, so the envelope is now in the kernel.*
  D6' §2.2 gives it `{eventId, actorId, action, entityType, entityId, before, after, ts}` and §2.1
  names the producer as "all (admin-bff interceptor)" — which does not exist yet, and `mqtt.rate_
  violation` has no admin request behind it in any case: the actor is a **device**. `AuditEvent`
  lives in `MageRide.Shared.Messaging` beside `EventTopics` so the interceptor and everything after
  it inherit the shape rather than inventing a second one; it is keyed by `entityId` per the §2.1
  registry. `actorId` is the vehicleId, because a `rate_violation` with no actor is a fact nobody
  can be asked about.
  (d) *R-09's "live preempts replay 4:1" is still not implemented as a ratio*, and C024 recorded
  that. It is now moot for the DoD rather than outstanding: the two streams hold **independent
  broker sessions** and the backlog is hard-capped at 20/s per device, which is what actually keeps
  live from being starved (`A_backlog_flood_does_not_delay_live_samples` measures it). A literal
  4:1 preemption still needs broker-side priority the C009 configuration does not set. **D6' §3.5
  should either specify the mechanism or restate the requirement as isolation plus a cap.**

  **Design decisions worth knowing about.**
  *Separate sessions, not just separate share groups.* This is the correction that made R-09 real.
  MQTT's **inflight window is per session** (EMQX's default `max_inflight = 32`), so one session
  holding both filters would let 32 unacknowledged backlog samples — each parked on a T-05 token —
  stop EMQX delivering live positions on the same socket for the length of the wait. The share
  groups alone never addressed that. Two sessions have two windows and the backlog can only starve
  itself.
  *The throttle waits; it does not drop.* A backlog is a vehicle's history and the flash ring
  buffered it for a reason. Over-rate samples are left unacknowledged, which fills the replay
  session's inflight window and stops the broker dispatching — ADD §7.5.2's "server-issued
  back-pressure token", arrived at through the protocol instead of through this process's heap.
  One **lane per device** (`ReplayPacer`), because a single ordered queue would let one vehicle's
  backlog block every other vehicle's behind it and turn a per-device limit into a global one.
  A device that floods far past the limit long enough will have its oldest backlog shed by EMQX's
  own `max_mqueue_len` (1000) — which is what a hard limit means, and is now said out loud in
  `mqtt-topics.md` §4.
  *Both counters are in Redis, so the bridge now needs Redis* (`UseRedis = true`, and the skeleton
  compose gained the dependency). A shared subscription hands each replica a random slice of one
  device's stream: an in-process bucket lets N replicas pass N × the limit, and for D-17 no replica
  would ever observe the rate the vehicle is really publishing at. Both **fail open** — Redis
  unreachable means the sample is forwarded and readiness goes red, because losing telemetry to a
  cache outage is worse than losing a limit the broker still half-enforces. The D-17 counter costs
  **one Redis round trip per vehicle per second**, not one per message: counts accumulate in a
  dictionary on the hot path and a background loop folds each closed second into `INCRBY`.
  *Produces are pipelined.* `TelemetryForwarder.Forward` hands the record to librdkafka
  synchronously and waits for the delivery report on a continuation, so the receive loop takes the
  next message immediately. Awaiting each produce in turn caps a replica at one broker round trip
  per sample — a few hundred a second against ADD §7.6's 1 200/s sustained and 6 000/s burst
  budget. Ordering survives because the enqueue is synchronous *and in call order* and
  `EnableIdempotence` will not reorder a retry; the cross-replica ordering test is what proves the
  whole chain.
  *Stopping is unsubscribe → drain → disconnect.* That sequence **is** "graceful rebalance with no
  duplicate ingest": EMQX stops routing the group's messages here, the forwards already started
  finish and acknowledge, and only then does the socket close. Skip the drain and every payload
  produced but not yet acknowledged comes back to a surviving replica and `telemetry.raw` carries
  it twice. Produces are deliberately **not** cancelled on shutdown for the same reason — the
  producer's own `MessageTimeoutMs` is the bound. An unplanned kill still falls back to
  at-least-once, which is the guarantee MQTT QoS 1 actually offers.

  **Kernel change other components will see.** `IEventPublisher.PublishAsync` now returns
  `Task<PublishReceipt>` / `Task<IReadOnlyList<PublishReceipt>>` instead of `Task`. Source-compatible
  for every existing caller (`await publisher.PublishAsync(...)` is unchanged); the three test fakes
  that implement the interface were updated, and `PublishReceipt.None(topic)` is there for a
  publisher with no broker behind it. `Dispatch.Api.Tests` and `MageRide.Shared.Tests` were re-run
  green to confirm.

  **A pre-existing bug this uncovered.** `DeviceClient`'s client id was
  `$"device-{vehicleId:N}-{Guid.NewGuid():N}"[..40]` — and `device-{vehicleId:N}-` is *exactly* 40
  characters, so the random suffix was truncated away entirely and every connection for one vehicle
  presented the same client id. EMQX disconnects the earlier one, so a second connection silently
  killed the first. Harmless while no test opened two sessions for one vehicle; fatal to the D-17
  test, which must. Fixed to a genuinely unique 28-character id.

  **For C039 (position-processor-svc) —** the record you consume now carries four headers:
  `mqttTopic`, `stream` (`live` | `replay`), `receivedTs` (the bridge's receive clock, ISO-8601
  round-trip) and `bridge` (the replica's id). `stream` is the one that matters — the R-17/T-05
  `seq` watermark is *layer 1* of the dedupe and the bridge deliberately implements none of it, so
  a replayed sample reaching you is expected, not a bug. Two things the bridge does **not** do and
  you own: `telemetry.raw.dlq` (D6' §2.3 — the bridge leaves a failed produce unacknowledged and
  EMQX redispatches it, which is loud rather than lossy but is not a DLQ), and the second-line
  10 msg/s-per-10 s check (`mqtt-topics.md` §4). For that second one, `AuditEvent.Observed(...)` in
  the kernel is the shape to reuse — do not invent a second `audit.events` envelope. Per-vehicle
  ordering now holds through the bridge under multiple replicas, so your `veh:seq:{vehicleId}`
  watermark will see monotone live sequences in the ordinary case and out-of-order ones only from
  `stream=replay`.

  **For C043 (tcp-adapter) —** the adapter publishes as a `svc-` principal on behalf of trackers,
  so it is **one connection carrying many vehicles** and the broker's per-connection
  `messages_rate = "5/s"` will throttle the whole adapter at five messages a second. That ceiling
  was written for a handset publishing one vehicle's positions; it is wrong for a multiplexing
  adapter and `emqx.conf` has no per-principal exemption today. Either give the 8883/5023-family
  listeners their own zone with a higher `messages_rate`, or accept that D-17's bridge-side counter
  is the only limit on the adapter path. Note also that the counter keys on the vehicleId in the
  **topic**, so an adapter publishing correctly to `veh/{vehicleId}/pos/live` is measured per
  vehicle exactly as a handset is — no adapter-side work needed for that half.

  **For C044 (fleet-health-svc) and the R-15/T-04 LWT consumers —** still nobody's. The bridge
  subscribes to two position filters and nothing else; `veh/+/status` has three named consumers in
  `mqtt-topics.md` §6 and no implementation in any component so far.

  **For C125 (infra hardening) —** three things. The `shared_subscription_strategy = sticky` above.
  The RS256/JWKS authentication block that is still commented out in `emqx.conf` (D6' §3.2, D-21's
  15-minute cache) — the dev HMAC secret is what every test and the slim stack use today. And
  `mqtt-topics.md` §4's per-ASN connection guardrail, which `max_conn_rate = "500/s"` only half
  covers.

  **Build host —** Docker for the three Testcontainers fixtures (EMQX, Redpanda, Redis); the
  replica stayed down throughout. The `Category=MqttBridge` filter takes ~2 min 36 s and the whole
  HotPath suite ~3 min 53 s. Note that the suite's wall-clock is dominated by the broker's own
  5 msg/s publish ceiling rather than by anything under test: the load test spends most of its time
  waiting for 30 devices to be allowed to publish 8 samples each.

- **Component:** C039 position-processor-svc — 2026-07-29
- **Status:** DONE — `dotnet test backend/src/HotPath.Tests -c Release --filter Category=PositionProcessor`
  is **53 passed, 0 failed**; the whole HotPath suite is **88 passed**, `MageRide.Shared.Tests` 235 and
  `Dispatch.Api.Tests` 181 (one added here). All four DoD items assert: a teleport is refused and
  counted (`PositionGateTests.A_teleporting_sample_is_rejected_and_leaves_the_live_state_untouched`),
  the availability index adds and removes on a phase transition and on offline within one sample
  (`DriverAvailabilityTests`, five cases), a replayed `seq` is dropped
  (`PositionProcessorTests.A_replayed_seq_is_discarded_on_the_watermark`), and `geo:live` reflects the
  last accepted position inside the 5 s end-to-end SLO the pipeline test already measures.
- **Notes:**
  (1) **R-08 had two owners and the spec's attribution was not implementable — this is the
  resolution.** ADD §9.4 makes position-processor-svc the writer of `driver:availability:{driverId}`
  and `geo:drivers:available:{type}:{res5cell}`; **a position sample carries no driver.** The whole
  telemetry contract is keyed by `vehicleId` because EMQX authenticates a *vehicle*
  (`mqtt-topics.md` §1), and the dispatch plane is keyed by `driverId` because a ride is offered to a
  person. That is why C024 left the heartbeat unwritten and C034 landed a working version in
  dispatch-svc, and why C034's handoff asked C039 to decide who owns the transition. **Split on what
  decides the fact, not on which key it lives in:** dispatch-svc owns the *phase* (creating the hash
  with the tier on go-online, moving `state` on offer/accept, deleting on offline) and the recovery
  path when the 60 s TTL lapsed but `dispatch.driver_presence` survived — it is the only party that
  can read the durable row. position-processor-svc owns everything a *position* decides: the res-5
  cell, `lastSeen`, and the TTL. It **never creates the hash and never adds a driver the hash does
  not already say is `AVAILABLE`**, so the two writers cannot contradict each other — one says who is
  in the pool, the other says where. The whole reconciliation is one Lua script, because an `HSET` on
  a key whose TTL lapsed a millisecond ago resurrects it with one field and no expiry, which then
  reads to dispatch as "online, position unknown" for ever.
  (2) **`dispatch-svc`'s `PresenceService.RecordPositionAsync` still refreshes the same two keys off
  `telemetry.normalized`, and that redundancy was left deliberately.** The writes are identical and
  idempotent (same driver, same res-5 cell, same TTL) and they converge, so running both is safe; the
  alternative was deleting the Redis half of that method, which would have meant rewriting three
  passing C034 tests in a component this session does not own. **Whoever removes it must keep the
  recovery branch** — "the hash is gone but the durable row says AVAILABLE, so re-index" is the one
  thing this service structurally cannot do.
  (3) **The refused sample must not advance the watermark, and that ordering is the subtle part.**
  The plausibility gate runs *before* `LivePositionIndex.RecordAsync`. If a rejected sample moved
  `veh:seq`, every genuine sample behind it would look like a replay and one spoofed frame would take
  a vehicle off the map until its `seq` caught up; if it became the `veh:meta` position the next
  sample is measured against, a spoofer could walk a vehicle across the island one refused jump at a
  time. Both are asserted.
  (4) **`MinStepInterval` is not in any spec and the teleport gate is unusable without it.** An
  implied speed is a distance over a time, and over a short time the numerator is the fix's own error
  circle: 10 m of ordinary GNSS jitter across 100 ms implies 360 km/h. Worse, most trackers stamp
  `sampleTs` to the whole second, so two fixes 200 ms apart arrive bearing the *same* instant and
  there is nothing to divide by. It is a **clamp, not a skip** (1 s, D5' §5.2/AL-12's fastest
  cadence): a teleport published as two same-instant samples is still judged at that interval and
  still fails. Skipping would hand a spoofer the gate, which is what the first draft of this filter
  did.
  (5) **A backlog skips the step gates and nothing else.** `stream=replay` samples carry a stale
  capture time by definition, so implied speed, the monotonic clock and the R-08 heartbeat are
  meaningless for them — judging a reconnecting fleet's history as teleports would drop all of it.
  Accuracy and satellite count still apply: a 500 m fix was useless when it was captured. A record
  with **no** `stream` header is treated as **live**, because reading it as a backlog would silently
  switch the gates off for any producer that forgot to stamp it.
  (6) **Both D-17 lines write the same `audit.events` action.** `mqtt.rate_violation` is the only one
  any spec spells for the MQTT plane, so the bridge's per-vehicle 5 msg/s observation and this
  service's 10 msg/s-over-10 s drop are told apart by `detectedBy` and a new `line` field rather than
  by a second action — as the C038 handoff asked. The **debounce keys differ**
  (`rate:mqtt-violation:` vs `rate:pos-violation:`) so the first line firing cannot silence the
  second, which is the failure mode that would matter most.
  (7) **One fixture bug found and fixed.** `Samples.Dehiwala` was documented since C024 as being in a
  different res-5 cell from Colombo Fort. It is not — both are `85611cb3fffffff`; a res-5 hexagon
  averages 252 km². `Samples.Moratuwa` (18.5 km, and reachable at 55 km/h) was added for the tests
  that need to cross a res-5 boundary. Also, `PipelineTests`' six-sample burst was moving 22 m
  between fixes stamped milliseconds apart — 79 km/h against a three-wheeler's 80 km/h ceiling once
  the gate landed, i.e. a coin toss. The step is now ~11 m; what that test asserts is batching.
  **Micro-change-sets raised —**
  (a) **`veh:driver:{vehicleId}` is not in ADD §9.4's key space** and R-08 is not implementable
  without it or something like it. Either §9.4 should carry the binding, or the §9.4 rows for
  `driver:availability` / `geo:drivers:available` should name dispatch-svc as a co-writer.
  (b) **`veh:meta`'s `poolCell` field is not in ADD §9.4's shape either.** It exists because a GEO set
  has no TTL and the availability hash does: when the hash expires nothing anywhere names the cell key
  that still holds the driver, so the membership leaks for ever. Some memory of it has to live
  somewhere with a longer TTL than 60 s.
  (c) **ADD §12.6's anti-spoof table prices seven of registry's ten vehicle types.** `truck`,
  `mini_truck` and `train` (`0303__registry_vehicles.sql`) have no ceiling. They fall to
  `DefaultMaxSpeedKph`, set to 200 — the most permissive value *in* the spec's own table — so a tier
  nobody priced is never refused by an invented number. The three need real values.
  (d) **D5' §13.1's "minimum satellite count" gives no number.** Four is used (what a 3-D fix needs).
  (e) **D5' §13.1 counts failed samples "toward a per-device fraud score" and no component owns that
  score.** Not provisioning-svc (C030 implements T-08 anti-cloning and nothing else), not
  reputation-svc (D-04 is about users, not devices). Today the trail is
  `mageride.positions.implausible{check,vehicle_type}` and a warning-level log line. **C044
  (fleet-health-svc) is the natural home** — it already owns the per-device rollup.
  (f) **Nothing in D5' or the ADD bounds a sample's clock skew *ahead* of the platform's.**
  `MaxClockSkewAhead` (5 min) is invented, and T-07's monotonic rule needs it: one frame dated 2099
  becomes the watermark and takes that tracker off the map until `veh:meta` expires.
  (g) **`telemetry.raw.dlq` (D6' §2.3) is still unowned.** C038's CLAUDE.md said "that is C039"; it is
  not in this component's deliverable list and it does not belong here — the retry/poison policy lives
  in the kernel's `KafkaTopicConsumer`, which every consumer on the platform shares, so a DLQ added
  from inside one service would either be that service's alone or a kernel change made from under
  five others. **It should be a kernel change with its own component.** Corrected in the bridge's
  CLAUDE.md rather than left pointing here.
  **For C040 (persistence-writer-svc) —** `telemetry.normalized` now carries only samples that passed
  D-18/T-07, so the `ck_positions_*` CHECK constraints should never fire from this path and a
  violation there is a real regression rather than a cheap tracker. `ReceivedTs` is stamped, `FleetId`
  is still **not** populated (`mqtt-topics.md` §6 says C040 must), and the `ON CONFLICT
  (vehicle_id, seq, sample_ts) DO NOTHING` three-column target from C006 note (a) still stands. The
  `veh:meta` hash gained a `poolCell` field — ignore it, it is this service's bookkeeping.
  **For C041 (fanout-svc) —** the `cell:{h3index}` stream and `veh:meta` field names are unchanged;
  `MetaFields` now spells them in one place if you want to read the hash. US-7.17's stale-vehicle
  removal from `geo:live` is still yours and still unimplemented — note that `geo:live` has no TTL of
  its own, exactly like the GEO sets this component now cleans up.
  **For C043 (tcp-adapter) —** every sample you publish goes through the D-18 gates. Two consequences:
  a GT06 frame carries no `satCount`, which is why `RequireSatelliteCount` defaults **off** (turning it
  on blinds the whole GT06 fleet); and `PositionSource` must be set correctly, because
  `Source != Mobile` is what turns on T-07's monotonic-GNSS-clock and satellite checks. An adapter
  that left `source` at its default would have its trackers judged as handsets.
  **For C044 (fleet-health-svc) —** micro-change-set (e) above: the per-device fraud score D5' §13.1
  asks for has no owner and your rollup is the natural place. The signal is already emitted as a
  metric and a log line; it needs a store and a threshold.
  **For C125 (infra hardening) —** `PositionProcessor__DriverAvailabilityTtl` **must equal**
  `Dispatch__PresenceTtl` in every environment file. They are 60 s in both defaults and in
  `.env.app.example`, and nothing enforces the equality at start-up — a mismatch would make a driver
  fall out of the hot index while dispatch still believed them fresh, which reads as an empty
  candidate set.
  **Build host —** Docker for the Testcontainers fixtures only (EMQX, Redpanda, Redis for HotPath;
  Postgres, Redis, Redpanda for the dispatch re-run); the replica stack stayed down throughout. The
  `Category=PositionProcessor` filter takes ~31 s, the whole HotPath suite ~4 min 52 s and
  `Dispatch.Api.Tests` ~4 min 17 s. **No new NuGet dependencies** — the H3 arithmetic came with
  `MageRide.Shared` and the anti-spoof filter is arithmetic.

- **Component:** C040 persistence-writer-svc — 2026-07-30
- **Status:** DONE — `dotnet test backend/src/HotPath.Tests -c Release --filter Category=PersistenceWriter`
  is **23 passed, 0 failed**; the whole HotPath suite is **111 passed**; `bash
  infra/scripts/migrate-verify.sh` is **222/222**. All four DoD items assert:
  `COPY` sustains **14,811 rows/s** on this box against the 3,000 msg/s target
  (`Sustained_ingest_of_three_thousand_rows_a_second_is_written_without_backlog`), a duplicate
  `(vehicle_id, seq)` batch inserts nothing the second time, a writer killed mid-backlog and restarted
  on the same consumer group delivers every row exactly once
  (`Killing_the_writer_mid_batch_loses_no_rows_and_duplicates_none`), and the live map keeps advancing
  while the writer is dead with the backlog still there afterwards.
- **Notes:**
  (1) **ADD §9.2 promises a trip summary and no DDL source prints a table for it.** The sentence is
  "only 1/min sampled + trip summary (start, end, distance, polyline) are persisted operationally",
  and §9.5 item 2 adds that the *query* path for a trip summary "hits aggregates, not raw rows" —
  which cannot be the whole story, because `telemetry.positions_1m` is bucketed by time and knows
  nothing about sessions. It can say how fast a vehicle was going at 14:03; it cannot say where one
  journey started, ended, or how far it went. So 0506 adds `trips.session_summaries`, **not columns on
  `trips.sessions`**: that table is trip-state-svc's aggregate with its own state machine and
  `updated_at` trigger, and US-5.10 lets an auto-ended session restart *in place keeping its id*, so a
  summary is not a final fact about a row — it has to be replaceable when the journey resumes and ends
  again.
  (2) **The 1/min sample had no idempotency key, and an at-least-once consumer needs one.** 0503 gives
  `trips.position_samples` only `PRIMARY KEY (id, sample_ts)` over a generated identity, so every
  write is unconditionally a new row and each rebalance appended a duplicate minute. The writer now
  stores each row **at its minute boundary** — `sample_ts` is the minute the row represents, which is
  what a 1/min series means — and `ux_possample_session_minute` makes the insert idempotent by
  construction. The alternative, an in-process "last written minute per vehicle", resets exactly when
  the platform is least stable.
  (3) **`COPY` cannot do `ON CONFLICT`, and the spec asks for both.** ADD §9.5 item 5 says `COPY`;
  §9.5 item 1 and T-05/R-17 require replay idempotency on the vehicle's sequence. `COPY` has no
  conflict handling at all — one duplicate raises and takes the batch with it. Resolved as binary
  `COPY` into a temp staging table, then one `INSERT … SELECT DISTINCT ON … ON CONFLICT
  (vehicle_id, seq, sample_ts) DO NOTHING`. **The staging table is created inside the transaction,
  `ON COMMIT DROP`**: a session-scoped one is faster and breaks behind PgBouncer in transaction mode
  (ADD §9.3), where consecutive transactions are not promised the same backend.
  (4) **The batch path is not the kernel's `KafkaTopicConsumer`, and that is the component.** That
  consumer commits per message — right for a ride command, a broker round trip per position here. The
  batching loop accumulates per Kafka partition and commits the high-water mark *after* the database
  transaction, which is the whole durability story: a kill has committed nothing, the batch is
  redelivered, and the two unique indexes absorb it. **Promote it into the kernel when a second
  service needs batching**; there is no second one yet.
  (5) **"per partition" (ADD §9.5 item 5) is read as the Kafka partition, not the Timescale space
  partition.** The Timescale reading is not implementable from the application — the vehicle-hash
  partition is computed inside the database — and the Kafka one is what makes "which offsets are safe
  to commit" answerable. Since `telemetry.normalized` is keyed by `vehicleId`, a Kafka partition is a
  stable subset of vehicles, so batching per Kafka partition also gives each `COPY` the per-vehicle
  locality the Timescale reading was after.
  (6) **A poison batch is isolated row by row; everything else is retried.** SQLSTATE class 22 (data)
  and 23 (integrity) will fail identically on every retry, so those rows go to
  `telemetry.normalized.dlq` with the reason and the decoded sample attached and their neighbours are
  written. A dropped connection, a deadlock, a full disk or a chunk being compressed is transient by
  definition and must never be committed past — the hypertable is the system of record.
  `A_transient_failure_is_never_dead_lettered` asserts the classifier directly, because the branch it
  guards is what decides between "retry" and "drop".
  (7) **The summary is computed from full-resolution rows, not from the 1/min samples.** A minute of
  city driving is not a straight line: chaining sixty-second chords across a route with turns loses a
  third of the distance or more. The raw rows are indexed for exactly this read
  (`ix_positions_vehicle_ts`; ADD §9.5 item 6 names "trip linestring for trip Y" as a raw-chunk
  query) and are always present when `session.ended` arrives. It is bounded by
  `(vehicle_id, sample_ts BETWEEN started_at AND ended_at)` and **not by `trip_id`**, because
  `telemetry.positions.trip_id` is set only if the publishing device chose to (`mqtt-topics.md` §2.1)
  and nothing makes a tracker do it — a summary keyed on it would be empty for most fleets. The
  `geometry_source` column records which relation answered, so a reader comparing two journeys can see
  when a distance is a lower bound.
  (8) **The distance is measured before the polyline is simplified.** Simplifying first would quietly
  shorten every journey by the tolerance's worth of detail, and the distance is the one number in a
  summary somebody might be paid against. `The_distance_is_measured_before_the_line_is_simplified`
  runs a 500 m tolerance over a 3.3 km route and asserts both halves.
  (9) **`session.ended` has three outcomes, not two.** Written; `SessionActive` — a US-5.10 restart,
  **committed**, because the journey may run for another hour and stalling the partition would hold up
  every summary behind it; and `SessionNotFound` — the event outran its own transaction, **retried**,
  because a summary lost to that race is a journey with no record of how far it went. A single "null
  means no" conflated the two, which was a real bug in the first draft.
  (10) **Postgres joined `HotPathCollection`.** The first three hot-path services own no table, which
  is why C024's csproj said "no Postgres anywhere in this suite". Every claim this component makes is
  a claim about what a real hypertable did, so the collection now starts four containers, and
  `HotPath.Tests` references `TripState.Api` for one constant — the `session.ended` event name, spelled
  on both sides and asserted equal, so a rename fails here rather than in production as summaries that
  silently stop being written.
  **Micro-change-sets raised —**
  (a) **`trips.session_summaries` is not in D4' §4 or `server_db_schema.md` §4.** ADD §9.2 names the
  artefact and nothing prints it. **D4' §4 should carry the table**; note (1) above argues why it is
  not columns on `trips.sessions`.
  (b) **`trips.position_samples` has no uniqueness in 0503** and cannot be written idempotently
  without some. D4' §4 should carry `UNIQUE (session_id, sample_ts)` and should say that the 1/min
  row's `sample_ts` is the bucket boundary rather than the fix's instant.
  (c) **D7' §4.2 gives this service its settings under a `Timescale` prefix** (`Timescale__BatchRows`,
  `Timescale__FlushMs`) and gives it no others. Everything C040 needs is bound under
  `PersistenceWriter`, with the two D7' names honoured as aliases so no deployed environment file has
  to change. D7' §4.2 should list the section this service actually reads.
  (d) **ADD §9.2 and §9.5 item 2 disagree about where a trip summary comes from** — "persisted
  operationally" versus "the query path hits aggregates". Resolved toward §9.2: the artefact is
  stored, because the aggregates cannot produce it. §9.5 item 2's sentence should say it is *fleet
  daily distance* that hits aggregates.
  (e) **Nothing in any spec bounds the polyline's size or precision.** `PolylineToleranceM` (25 m) is
  invented and argued from D5' §5.2's own `Δpos < 25 m` coalescing threshold — the distance the
  platform has already decided is not worth transmitting.
  (f) **`telemetry.raw.dlq` (D6' §2.3) is still unowned**, as the C039 handoff also recorded. This
  service owns `telemetry.normalized.dlq` — its own input topic — and deliberately nothing else: the
  raw one is a claim against the kernel's shared `KafkaTopicConsumer`, which every consumer on the
  platform uses, so adding it from inside one service would either be that service's alone or a kernel
  change made from under five others.
  **Known gap —** the DoD's fourth line says "stopped for **60 s**" and the test's window is seconds.
  The property is not time-dependent: the writer is a separate consumer group, the live map is Redis
  written by C039, and this service sets `UseRedis = false` so it registers no Redis client at all —
  there is nothing on the path with a minute-scale timeout for a longer sleep to exercise. What the
  test asserts instead is the whole of the fence: the live map advances while the writer is dead, the
  durable table demonstrably does not, and the backlog is still on the topic to be written when it
  returns.
  **For C041 (fanout-svc) —** `telemetry.positions` is now populated, so US-7.17's stale-vehicle
  removal has a durable history to reason against rather than only Redis. `veh:meta`'s field names are
  unchanged.
  **For C042 (query-svc) —** the read path over all of this is yours, and three things are ready for
  it: `trips.session_summaries` answers "trip detail + polyline" (ADD §2719's
  `GET /trips/{userId}/{tripId}`) in one row with no aggregation, `ix_summaries_driver` and
  `ix_summaries_vehicle` are the two history reads, and `geometry_source` is what a response should
  surface when a distance is a lower bound. `GET /v1/nearby` still reads Redis, not this.
  **For C043 (tcp-adapter) —** set `PositionSource` correctly and set `tripId` when the tracker is on a
  Mode A/B journey if you want a full-resolution `trip_id`-keyed read to work later; the summary does
  not need it (see note 7) but nothing else populates it.
  **For C044 (fleet-health-svc) —** `telemetry.fleet_health_5m` (C006's aggregate) now has rows,
  because `fleet_id` is denormalised on every write. A vehicle in no fleet stays invisible to it, by
  design.
  **For C125 (infra hardening) —** this service must **not** be given `ConnectionStrings__Redis`. The
  fence is held structurally by `UseRedis = false`; handing it a Redis DSN would not break anything
  today and would remove the guarantee.
  **Build host —** Docker for four Testcontainers fixtures (Postgres/timescaledb-ha:pg16, EMQX,
  Redpanda, Redis) plus one more for `migrate-verify.sh`; the replica stack stayed down throughout.
  The `Category=PersistenceWriter` filter takes ~23 s, the whole HotPath suite ~5 min 15 s and
  `migrate-verify.sh` ~2 min. **No new NuGet dependencies** — `Npgsql` and `Dapper` were already
  pinned centrally; `NpgsqlBinaryImporter` is why Npgsql is referenced directly rather than only
  through the kernel.

- **Component:** C041 fanout-svc — 2026-07-30
- **Status:** DONE — `dotnet test backend/src/Fanout.Api.Tests -c Release` is 48/48 green, and all four
  DoD lines are asserted through a real SignalR client over a real WebSocket against a real Redis,
  Redpanda and EMQX. `HotPath.Tests` stays green at 102 (the nine hub tests moved into the new suite);
  `MageRide.Shared.Tests` at 235. **No migration** — this service owns no table and holds no database.
- **Notes:**
  **The one design decision everything else follows from —** D6' §5.1 and §5.2 cannot both be true of
  a geocell group. §5.2 says a public `cell:{h3index}` group fans out "Mode A + entitled Mode B", and
  a group has one membership and one message: a Mode B frame put on it reaches every passenger in the
  cell, entitled or not. ADD §11.10's remedy — "for each geocell … `RemoveFromGroupAsync(connectionId,
  "geocell:" + cell)`" — would also stop the revoked passenger seeing the buses, which is visibility
  §5.2 grants unconditionally. **Resolved with a fourth group, `vehicle:{vehicleId}`**, carrying one
  vehicle to the passengers entitled to it (D-23) and, for a Mode C vehicle, to its own driver
  (AL-31). Both spec lines then hold literally: entitlement is still "checked on group join" and never
  per frame, and D-22's revocation is still one directed `RemoveFromGroupAsync` — one that now removes
  exactly what was granted. Micro-change-set (a) against D6' §5.1's group table and `signalr-hub.md`
  §2.1, both updated.
  **There is deliberately no `SubscribeVehicle` method.** Every membership of that group is derived
  from server-side state (`share:{userId}`, or registry-svc's `lock:driver:{driverId}`), so there is
  no request a client could make that the server would not have to overrule — a method taking a
  vehicle id would be a method whose whole body is "ignore the argument". It is also what makes the
  AL-31 fence structural rather than a promise: a driver is joined to exactly one vehicle group, their
  own, whatever the app asks for.
  **The backplane is a channel of our own, not `AddStackExchangeRedis()`.** D6' §5 asks for "Redis
  (MVP) → Redpanda (scale)" and the C024 handoff left a fence saying C041 must add one for the
  directed sends and keep the per-cell batches off it. SignalR's backplane cannot do that — it is a
  property of the `HubLifetimeManager` and applies to every group send the process makes — and every
  replica already produces every cell batch independently, so turning it on would deliver one copy per
  replica per frame. So the directed sends (`ShareRevoked`, `RideStateChanged`,
  `LocationRequestResolved`, `PackageStatus`) travel a Redis pub/sub channel, `fanout:control`, and
  each replica applies a signal to **its own connections only** — disjoint sets, so exactly-once
  delivery — while the batches stay local. `EntitlementTests` measures the cross-replica hop against
  D-22's 200 ms with the passenger deliberately on the replica that did *not* consume the event.
  **Spec gaps and micro-change-sets —**
  (a) **`vehicle:{vehicleId}` is not in D6' §5.1's group table.** Argued above; `signalr-hub.md` §2.1
  and §4 updated, D6' §5.1 needs the row.
  (b) **`signalr-hub.md` §2.1 omits the proxy *rider* from `ride:{rideId}`.** It names "the ride's
  passenger, its driver, and — for a proxy booking — the booker". P-01 makes booker and rider two
  people and the rider is the one in the car; they are admitted, and the contract file now says so.
  (c) **D3' §3.1 and D6' §5.1 write `SubscribeRide`/`SubscribeLocRequest` arguments as ULIDs.**
  `rides.rides.id` and `rides.location_requests.request_id` are `UUID` columns and every REST response
  renders them as such. The hub takes UUID strings; contract file corrected.
  (d) **No spec pins the US-7.17 freshness window.** D5' §5.4, ADD §6 and US-7.17 all say "older than
  the freshness window"; the only related figure is ADD §7.5.1's dispatch rule (`2 × expectedInterval`),
  whose interval is per phase and runs 1–60 s. `Fanout:FreshnessWindow` is **60 s**, chosen to equal
  `Dispatch:PresenceTtl` and `PositionProcessor:DriverAvailabilityTtl` — two different answers to "is
  this vehicle live" would show up to a passenger as a marker they can see and cannot book.
  (e) **Three Redis keys are not in ADD §9.4's key space** — `veh:engaged:{vehicleId}` (US-7.16's
  active hire), `veh:offline:{vehicleId}` (the last-will instant) and `fanout:ride:{rideId}` (the
  participant projection `SubscribeRide` is checked against). Each is argued at its declaration in
  `RedisKeys`. `share:{userId}` *is* spec'd (D-23) and is the only one that is.
  (f) **ADD §11.10's `SREM` + geocell removal is superseded** by (a). The `SREM` is kept — this
  service is the SET's only writer — and the group removal is against `vehicle:{vehicleId}`.
  (g) **D6' §2.2 prints no envelope for `registry.events`.** The outbox dispatcher publishes the
  payload column verbatim with the type in an `eventType` **Kafka header**; ride-svc's rows only look
  like envelopes because `RideEvents.Build` serialises one into that column. A consumer that looked
  for `eventType` in a registry payload would silently discard every share event — the consumer reads
  the header, and `Events.Share` in the test suite produces the flat shape on purpose.
  (h) **`etaSeconds` on `RideStateChanged` is never sent.** D3' marks it optional; the estimate is
  query-svc's (C042), computed from the route, and inventing one here would put two different numbers
  in front of one passenger.
  (i) **`Fanout:JoinSeedFrames` survives C041.** The C024 note said "C041/C042 should remove it";
  `signalr-hub.md` §1.1 makes `GET /v1/nearby` the real snapshot path and that is C042, so removing it
  now would leave a passenger opening the map with nothing at all. It is kept, and now runs through
  the same visibility filter as a live batch — a replay that showed an engaged taxi would be the D-22
  leak with a two-second delay on it. **C042 removes it.**
  **Two races found and closed, both of which fail silently —**
  (1) **`ride.events` is partitioned by `rideId`, so two rides are two partitions and nothing orders
  them against each other.** An offer that expired *before* an accept can be consumed after it, and an
  unconditional `DEL veh:engaged:{vehicleId}` there would put an occupied taxi back on the public map
  for the rest of the trip. The release is a Lua compare-and-delete on the ride id.
  (2) **A vehicle driving from one of a passenger's nineteen cells into another stops appearing in the
  first cell's stream**, which is indistinguishable from having stopped reporting. A naive stale sweep
  would tell the client to erase a marker the next batch puts straight back — once per window, for
  every moving vehicle on the map. The sweep now checks the candidate's own current position first and
  only announces a vehicle that is stale *everywhere*. A vehicle that leaves the whole view is neither
  stale nor offline nor engaged, so no reason in the contract fits and the client ages it out — which
  it must anyway, since a passenger panning the map changes their cell set with no server event.
  **Known gap — `share:{userId}` has no rebuild path.** This service is the SET's only writer and
  builds it from `registry.events` with a stable consumer group reading from Earliest, so a fresh
  deployment replays the topic and rebuilds every passenger's entitlements. A Redis flush *after* that
  leaves entitled passengers with no Mode B visibility until their next grant event. Failing closed is
  the right direction — a passenger who cannot see their school van complains, where the opposite
  default is a disclosure nobody can see — but the durable fix is a read-through against registry-svc,
  and Mode B subscriptions are **C048's** surface. Not stubbed here: a rebuild path that quietly
  returned an empty set would read exactly like a working one.
  **Where the visibility model is read from —** `ride.events` (engagement, participants,
  `RideStateChanged`, `PackageStatus`, `LocationRequestResolved`), `registry.events` (the entitlement
  SET, D-22), the EMQX last will on `veh/+/status` (US-7.17's `offline` half, as `svc-fanout`, which
  `acl.conf`'s `^svc-` rule already grants), and `lock:driver:{driverId}` (AL-31). Each has an
  `Enabled` switch and **each one off is a filter that cannot close**, so
  `WarnAboutFiltersThatCannotClose` names them at start-up — position-processor-svc's rule, for the
  same reason: an open filter looks exactly like a working one from the outside.
  **For C042 (query-svc) —** `GET /v1/nearby` must apply the *same* four rules this service applies
  (D3' §524 already says so): Mode A always, Mode B only if `share:{userId}` contains the vehicle,
  Mode C only if `veh:engaged:{vehicleId}` is absent, and nothing whose `veh:meta.sampleTs` is older
  than `Fanout:FreshnessWindow` or older than `veh:offline:{vehicleId}`. All four keys are readable
  from Redis and the rule itself is `MageRide.Fanout.Visibility.VehicleVisibilityRules.Classify` —
  worth promoting to the kernel when the second caller exists rather than reimplementing it. Landing
  `/v1/nearby` also retires `Fanout:JoinSeedFrames`.
  **For C048 (subscription-svc-mode-b) —** the unsubscribe path must emit `share.revoked` on
  `registry.events` with `passengerId`, exactly as registry-svc does, or a passenger who unsubscribes
  keeps seeing the vehicle. That is also the natural home for the `share:{userId}` rebuild.
  **For C051 (notification-svc) —** nothing on this hub is a push. `signalr-hub.md` §6 lists what goes
  to FCM/APNs instead; a backgrounded app has no socket.
  **For C067–C102 (the apps) —** three client-side obligations the server cannot enforce: age a marker
  out when it stops arriving (a vehicle leaving the view produces no event, by design); treat
  `VehiclePositions` as a merge by `vehicleId` and not a replacement, because the private and public
  streams arrive as separate batches; and after a reconnect, re-join geocells **and** resync from
  `/v1/nearby` (§1.1) rather than waiting for the next tick.
  **Build host —** Docker for three Testcontainers fixtures (Redis, Redpanda, EMQX); the replica stack
  stayed down throughout. `Fanout.Api.Tests` takes ~1 min 40 s, `HotPath.Tests` ~5 min. **One new
  NuGet reference** — `MQTTnet`, already pinned centrally, for the `veh/+/status` subscription.

- **Component:** C042 query-svc — 2026-07-30
- **Status:** DONE — `dotnet test backend/src/Query.Api.Tests -c Release` is 65/65 green in a new suite,
  and all four DoD lines are asserted against a real Redis holding `geo:live`/`veh:meta` hashes written
  by the **real** position-processor-svc writer and a real Postgres holding a real
  `geography(LINESTRING,4326)` polyline. `Fanout.Api.Tests` is 47 (was 48 — the join-seed test went with
  the option it tested), `HotPath.Tests` 101 (was 102, same reason), `MageRide.Shared.Tests` 235.
  **No migration** — this service owns no table and writes nothing but a geocode cache.
- **Notes:**
  **The decision everything else follows from —** `signalr-hub.md` §1.1 makes `GET /v1/nearby` the
  snapshot and resync path for the *same map* `/hubs/live` streams, so the two paths must agree exactly
  about who may be seen. C041's handoff asked for its `VehicleVisibilityRules.Classify` to be promoted
  to the kernel "when the second caller exists rather than reimplementing it", and that is done: the
  file moved to `MageRide.Shared/Realtime/VehicleVisibility.cs` and both services now call one function.
  A second implementation would surface as a passenger watching an engaged taxi for one poll
  interval — D-22's disclosure with a delay on it — and no test on either side would catch it, because
  each suite would agree with its own copy.
  **`Fanout:JoinSeedFrames` is retired, as C041 said C042 would.** The join used to replay each cell's
  tail to the joining connection; `LiveHub.AnchorAsync` keeps the load-bearing half (fixing a new
  cell's stream position at join time, so nothing written before the first tick is skipped) and sends
  nothing. The seed was the weaker of the two snapshot paths anyway: it read only the cells the client
  had joined and only the **public** audience, so it could never show a passenger their own engaged
  vehicle (US-7.16's second half) or their entitled Mode B van (D-23), both of which `/v1/nearby` does.
  **Spec gaps and conflicts —**
  (a) ***No Mode C track is stored anywhere, so the DoD's "stored polyline" is only literally
  satisfiable for Mode A/B.*** ADD §9.2's stored trip summary is per **session**:
  `trips.session_summaries` (0506) covers Mode A and B and `ck_summaries_mode` admits only those two.
  The Mode C equivalent has no table, no column and no writer — E-04's Kalman-filtered track is
  computed by fare-svc for the distance the fare is charged on and never persisted. A ride's line is
  therefore read from the **`telemetry.positions_1m` continuous aggregate**, which is the read path
  ADD §9.5 item 2 prescribes for a trip summary ("hits aggregates, not raw rows") and which migration
  1802 landed *naming this component* ("so query-svc (C042) can pick a granularity by table name
  alone"). It is one point per minute and labels itself `aggregate_1m`. **No table was added for a
  writer that does not exist**; **C049 must persist the E-04 track and its distance**, and this
  service reads whatever column it lands in.
  (b) ***A Mode C `distanceKm` is omitted rather than derived.*** Chaining sixty-second chords across a
  route with turns loses a third of the distance or more — C040's own note on the same trade-off — and
  that number is the one on the receipt. A figure a third short of the fare is worse than no figure.
  (c) ***"My trips spans both planes" is only half-joinable.*** D3' says the history covers Mode A/B
  sessions and Mode C rides, and the only link from a **user** to a `trips.sessions` row is
  `driver_id`: the platform records no ridership for a bus or a school van, because nobody is
  ticketed. So a passenger's history is their Mode C rides (as passenger, booker or registered rider)
  and a driver's is theirs plus their sessions. **Not a narrowing of the contract — the only join the
  schema has.** A passenger-side Mode A/B history would need a boarding record nothing produces.
  (d) ***`fares.driver_earnings` (1004) has no writer, and is deliberately not read.*** It is
  documented as "the read model behind the driver Earnings screen", and its writer is fare-svc's R-05
  earning post (C049/C050). Reading an unwritten rollup would answer every dashboard with zeros while
  the payment rows behind it hold real money — the failure that looks exactly like a working screen.
  It also has no tip and no penalty column, so it could not answer D3' in full even once written.
  **C050 should either populate it or drop it.**
  (e) ***D3' does not state the direction of `EarningsSummary.penaltyMinor`.*** Its prose says "the fee
  and any penalty netted out", which reads like a deduction — and **nothing in D5' ever debits a
  driver a penalty**. The only Rs 50 on the platform is charged to a *passenger* for cancelling after
  an accept and paid to `dispatch.cancellation_penalties.affected_driver_id`, whose own column comment
  is explicit that the driver who later collects it is "a pass-through, not the beneficiary". Read as
  a deduction the field would be permanently zero and would hide money the driver is owed, so it is
  reported as the compensation **credited** and it **adds** to net. **D3' should state the direction.**
  (f) ***`dispatch.cancellation_penalties` has no settled-at column***, so a penalty accrued in March
  and collected in April currently lands in March's earnings (`created_at` is the only date the row
  carries). Micro-change-set: the table needs `settled_at`.
  (g) ***`GET /v1/transport-options`'s `fromLat`/`fromLng` cannot default.*** D3' documents them as
  defaulting to "the caller's last known position", and the platform holds no such thing for a
  **passenger**: `geo:live` is keyed by vehicle because EMQX authenticates a vehicle, and a
  passenger's handset publishes nothing. Made **required** in `query.yaml` rather than inventing an
  origin — a client with a map open knows where its map is centred.
  (h) ***The endpoint overlaps C061.*** AL-17 assigns the reachable-options computation to "query-svc /
  a new transit-svc" and C061 owns `GET /v1/transit/options` with the same inputs and a superset of
  the outputs. `/v1/transport-options` is **kept** only because `shared/kmp`'s generated client
  already calls it (C012) — it aggregates and computes nothing, delegating GTFS matching to
  transit-svc and pricing to fare-svc. **C061 may reasonably absorb it**; if it does, the KMP client
  and the gateway route go with it.
  (i) ***"C042 lands a mesh" is recorded in the C031 and C037 handoffs and is wrong.*** Both note that
  the interim `X-MageRide-Internal-Key` shared secret stands "until C042 lands a mesh"; C042 is
  query-svc. **No component in the 132 owns the Linkerd/SPIFFE mesh** (C119's security review
  *verifies* mTLS on internal routes without installing it), so the interim scheme stays and this
  service uses it too — `Query:InternalApiKey` unset leaves `query.v1.Query` **unmapped**, not open.
  A mesh component is missing from the manifest.
  **Decisions —**
  (1) **The exact post-filter is not an optimisation, it is the correctness of the endpoint.**
  `geo:live` has no per-member TTL and *nothing ever removes a member*: C039 `GEOADD`s and never
  `GEOREM`s, and C041's stale sweep works on the cell streams instead. A `GEOSEARCH` therefore returns
  every vehicle that has ever driven through the radius, at the place it stopped. Every candidate is
  re-read from `veh:meta` (which *does* expire) and re-measured with a haversine; a candidate with no
  hash is dropped, not drawn approximately. The search radius is inflated 1 % first — twice Redis's
  own documented geohash error — so the exact pass can also *include* a vehicle the index excluded
  from just inside the line. `A_geo_index_member_with_no_position_hash_is_not_drawn` is the test.
  **US-7.17's `geo:live` cleanup is still nobody's** (C039's CLAUDE.md assigned it to C041, which did
  the sweep on the streams); the post-filter makes it a cost rather than a bug, and the two counters
  `unknown` and `out_of_radius` are what would show it.
  (2) **US-7.16's second half is implemented, and without a copy of ride-svc's state machine.** An
  engaged Mode C vehicle is off the public answer and *on* the answer for the passenger whose ride
  engaged it. `veh:engaged:{vehicleId}` already names the ride, so the only question left is "is that
  hire yours", which `rides.rides` answers over passenger/booker/rider (P-01/P-03 make those three
  different accounts). Nothing here re-derives which states count as engaged. This is also the only
  path on which `driverName` and `registrationNumber` are populated at all (US-7.12) — without it two
  contract fields would be permanently dead.
  (3) **The registry is read only for vehicles whose identity may be disclosed.** US-7.4 gives the
  popup to Mode A and B alone ("standby on-demand vehicles do not show info when tapped") and US-7.12
  gives the plate to the accepted ride, so an idle Mode C taxi's registration is **never fetched**.
  The privacy rule is the shape of the data access rather than a field-stripping step that could be
  forgotten later.
  (4) **A vehicle whose publisher denormalised no `mode` or `type` is dropped, though the registry
  could supply it.** fanout-svc holds no database and drops that frame; being more generous here would
  put a marker on the map the socket then never moves — a frozen vehicle a passenger walks towards.
  The two planes fail the same way on purpose, counted under fanout's own `unclassified` reason so
  the rates stay comparable. (`type` is separately required: MAP-03 draws a marker *by* type.)
  (5) **`etaSeconds` is a straight line with a detour factor, and every assumption is a setting.**
  ADD §7.6 puts routing (OSRM/Valhalla) in **Phase 3**, so no road network exists to measure against;
  C041 had already deferred this field to C042 once and deferring again would mean nothing ever
  populates it. The per-type speeds are urban averages **including stops** — deliberately *not*
  ADD §12.6's anti-spoof ceilings, which are three to five times higher and are the speeds above
  which a fix is a lie. US-7.11's two halves point at different targets: the accepted vehicle's ETA is
  to whichever end of the journey is ahead, a Mode A vehicle's is to the caller's own map centre.
  Deleted when the router lands.
  (6) **AL-17 is held by an absence of capability.** The search path has no query that can reach
  `spatial.routes`, `transit.gtfs_routes` or anything else holding a route — `IPlaceRepository` reaches
  two tables and `IGeocoder` reaches an OSM *place* index. A filter would be a line somebody could
  delete. `Search_never_returns_a_route_row_for_a_typed_route_number` seeds a real route numbered 138
  **with an active bus on it**, proves `GET /v1/routes/138/buses` returns that bus, and proves search
  for "138" still cannot produce the route.
  (7) **Read-after-write is exactly one read, decided by the read's shape.** ADD §9.3 asks for replicas
  "with read-after-write consistency only where required". Required once: `GET
  /v1/trips/{userId}/{tripId}`, opened from the receipt screen seconds after ride-svc marked the ride
  terminal, where lag does not *stale* the answer but **inverts** it into a 404 on a trip the
  passenger has just finished. Everything else is a list or an aggregate, where a row missing from the
  top of a page appears on the next pull — sending those to the primary too would give up §9.3
  entirely to protect against something no user can see.
  (8) **The R-05 gate is read off the *ride*, not the payment.** D5' §8.1's terminal set is three
  `rides.rides.state` values (AL-47's driver-QR settles into the same place). Gating on payment rows
  would count a `Succeeded` attempt on a ride later disputed and would have to reason about the D-10
  retry chain to avoid counting one fare three times; a ride has one state.
  `A_retry_chain_contributes_one_fare_and_not_three` pins it. Gross excludes the OnePay surcharge
  (US-8.11 — the passenger's gateway cost), and a cash ride with **no payment row at all** still
  counts as a trip, because a driver whose day was all cash must not read "0 trips".
  (9) **The daily fee and the penalty are on the summary and not on a per-ride row.** A daily fee is a
  fact about a *day* (D-13 charges it once, before the second trip) and the penalty about somebody
  else's cancellation; splitting either across a day's rides makes every row's net wrong in a
  different way. D3' marks both optional on `SessionEarning` and required on `EarningsSummary`.
  (10) **Two bugs the tests found, both of the silent kind.** *Modes and types have opposite case
  conventions* — canonical types are lower-case with underscores (AL-09), modes are upper-case A/B/C
  (D5' §2) — and one shared `ToLowerInvariant` turned `modes=C` into a filter matching nothing: an
  empty map, no error anywhere. `NormaliseType`/`NormaliseMode` are the fix and a test pins both.
  *`@Before IS NULL` with no cast* makes Postgres refuse the statement with `42P08` rather than infer
  a type, which took both keyset cursors down; the casts are load-bearing and say so.
  (11) **`limitedLive` rather than a 500 on a Redis outage**, because ADD §12's resilience table
  specifies it by name ("query-svc returns `limited_live` flag"). Always serialised, including when
  false: a client that could not tell "no vehicles nearby" from "we do not know" would render an
  outage as a quiet afternoon.
  (12) **The polyline is encoded here, not by `ST_AsEncodedPolyline`.** Putting the wire encoding in a
  query means a second endpoint reading the same column either repeats the SQL or disagrees about
  precision. `EncodedPolyline` is asserted against the published algorithm's own worked example as
  well as a round trip — a round trip alone passes for an encoder that is self-consistent and wrong,
  and MapLibre is what would then draw a line off the road.
  (13) **The test project references `HotPath.PositionProcessor` and writes the live index through the
  real `LivePositionIndex`.** `veh:meta`'s field names are the contract between C039 and this service
  and neither may reference the other in production; a hand-written copy of the names would stay green
  through a rename there while every passenger's map went quietly empty. Referencing it from the
  *test* project makes such a rename break this build instead.
  **Contract changes (all in `query.yaml`, lint clean) —** `limitedLive` on both snapshot responses
  (ADD §12 names the flag; D3' prints no field); `TripDetail.geometrySource` (a full-resolution Mode
  A/B track and a 1/min Mode C line are not the same artefact, and a client drawing both must not
  present one as the other); `GeocodedPlace.label` (without it a client cannot render US-7.13's
  Home/Work shortcuts); `503` on `/v1/geo/reverse` and `/v1/transport-options`; `fromLat`/`fromLng`
  required (gap (g)); and **`x-grpc-service` + `proto/query.v1.proto`** — ADD §6 gives query-svc
  ".NET 10 minimal API **+ gRPC**" and D3' §0 names "`query-svc` internal", and neither prints a
  service block. Three RPCs, each with a named caller that would otherwise reimplement a rule:
  `GetNearbyVehicles` (fleet-svc C059 and admin-bff C057 both render live maps and must not be a third
  opinion about D-22/D-23), `GetTripDetail` and `GetDriverEarnings` (admin-bff's US-24.9/24.10 tabs).
  `viewer_user_id` is **required** — two of the four rules are per viewer, and answering a call that
  names nobody with the public map is how a back-office screen shows an engaged taxi.
  **For C013 (kmp-api-client) —** the generated client is now behind `query.yaml` in four places, all
  of them mine: `NearbyVehiclesResponse` needs `limitedLive`, `TripDetail` needs `geometrySource`,
  `GeocodedPlace` needs `label`, and `getTransportOptions`'s `fromLat`/`fromLng` must stop being
  nullable. Nothing is broken today — no app screen exists yet (C067+) — but a client that can
  construct a request the contract forbids is a bug waiting for the first booking screen.
  **For C049/C050 (fare-svc) —** persist the E-04 Kalman track and its distance (gap (a)) and either
  populate or drop `fares.driver_earnings` (gap (d)). Both are read here the moment they exist.
  **For C059 (fleet-svc) / C057 (admin-bff) —** call `query.v1.Query/GetNearbyVehicles` rather than
  reading `geo:live`; ADD §9.5 item 8's `fleet_id` RLS (migration 1804) is fleet-svc's to set a role
  for, because no endpoint here is fleet-scoped.
  **For C061 (transit-svc) —** `Query:TransitBaseUrl` expects `GET
  /v1/transit/options?fromLat&fromLng&toLat&toLng` answering `{options:[{shortName, headsign,
  vehicleType, transfers, fareMinor}]}`; `vehicleType` is passed through and not guessed at, because
  MAP-03's rail icon depends on it. See gap (h) on whether this endpoint should move.
  **For C067–C102 (the apps) —** `/v1/nearby` is the cold-start and post-reconnect snapshot and the
  socket carries deltas only; **read `limitedLive` and say so on the screen** rather than rendering an
  empty map; and US-7.14's "no vehicles of your type are active" message is the client's, from an
  empty `vehicles` array on a snapshot whose `limitedLive` is false.
  (j) ***D7' §4.2 has no row for query-svc, and this service needs two ports.*** Cleartext HTTP has no
  ALPN, so Kestrel cannot negotiate HTTP/1.1 and HTTP/2 on one socket — a gRPC client's preface to the
  REST port is answered `GOAWAY HTTP_1_1_REQUIRED`, which is the failure C033 hit and why §4.2 gives
  reputation-svc a `Grpc__ListenPort`=5005. query-svc therefore binds its own listeners the same way:
  `Query:HttpListenPort` (5000, the address `gateway-routes.json` points the cluster at, or
  `ASPNETCORE_URLS`) and **`Query:GrpcListenPort`=5006**. Not 5005 — both services run in the combined
  `app-services` container in the dev compose and would fight over it. **§4.2 needs the row.** The
  HTTP/2 listener is bound whenever `Query:GrpcEnabled` is on even without a key, so a keyless
  deployment fails at "a port that answers Unauthenticated" rather than "connection refused" — a
  diagnosable misconfiguration instead of an apparent network fault. The harness sets both to 0 and
  drives the production wiring, so this is under test rather than deployment-only.
  **Build host —** Docker for two Testcontainers fixtures (Postgres, Redis); the replica stack stayed
  down throughout. `Query.Api.Tests` takes ~1 min, `Fanout.Api.Tests` ~1 min 25 s, `HotPath.Tests`
  ~4 min 40 s. **No new NuGet reference** — `Grpc.AspNetCore`, `Grpc.Net.Client`, `Dapper`, `Npgsql`
  and `StackExchange.Redis` are all already pinned centrally.

- **Component:** C043 tcp-adapter — 2026-07-30
- **Status:** DONE — `dotnet test backend/src/TcpAdapter.Tests -c Release` is 89/89 green in a new suite,
  and every line of the DoD is asserted against the thing it is a claim about: four golden frames decode
  to the expected `PositionSample` through the production mapping; an unbound, revoked and quarantined
  IMEI each close a real socket; a half-closed socket's retained `status=offline` is read back off a real
  EMQX inside the configured window; and T-11 is asserted against `registry.vehicles.mode` in a real
  Postgres and `veh:driver:{vehicleId}` in a real Redis. `dotnet build backend/MageRide.sln -c Release`
  is clean with 0 warnings. **No migration** — this service owns no table and writes to no database.
- **Notes:**
  **The decision everything else follows from —** a tracker cannot present a JWT, so the enforcement that
  confines an MQTT-native device (EMQX's `verify_claims` binding `${username}` to the token's
  `vehicleId`, plus `acl.conf`'s `veh/${username}/*`) is **structurally unavailable** on 5023-5026. The
  adapter connects as one `svc-tcp-adapter` principal with `veh/#`, so the vehicle binding is this
  service's to get right and nothing else's. The guarantee is therefore made structural rather than
  careful: the only thing in the project that produces a topic is a `TrackerAuthorisation.VehicleId`,
  which came from `prov.tracker_bindings`, and `EmqxLink` takes no topic from a caller that did not build
  one from an authorisation. A per-device broker session was considered and rejected — it multiplies
  EMQX's connection count by the tracker population and buys no authorisation the `svc-` grant does not
  already give.
  **`seq` is the capture instant in milliseconds, and this is the load-bearing design choice.** A tracker
  frame has no sequence number worth using: GT06's and JT/T 808's information serials are sixteen bits,
  wrap in hours, and survive neither a device reboot nor a pod move — all of which `veh:seq:{vehicleId}`
  outlives, because R-17/T-05's watermark is per vehicle and permanent. The GNSS instant does survive all
  three, is monotonic per vehicle (which T-07's monotonic-clock check independently requires of hardware
  anyway), and is *identical* for a sample sent live and the same sample re-sent from the device's flash
  ring — so the backlog dedupe falls out of the comparison position-processor already makes rather than
  needing anything new. The cost is that two fixes stamped to the same millisecond collide, and for four
  protocol families that all stamp to the whole second, two fixes in one second are the same position
  twice. `TrackerSamples.From` is the one place it is computed, shared by the TCP sessions and the UDP
  listener, because two producers filling the canonical payload differently would surface as a vehicle
  whose type depends on which port its tracker speaks.
  **Spec gaps and conflicts —**
  (a) ***D7' §2.1 gives Container 9 no database and this service needs one.*** T-11's gate needs the
  vehicle's **mode** and `registry.vehicles.mode` is the only place it exists: `prov.tracker_bindings`
  does not carry it, `imei:{imei}` holds a vehicle id and nothing else, and `veh:meta:{vehicleId}` is
  written by position-processor-svc *from accepted samples* — so reading the mode from there to decide
  whether to accept a sample is circular and empty for exactly the tracker-only vehicles that need it.
  The canonical sample's denormalised `mode`/`vehicleType` (`mqtt-topics.md` §2.1, "so a consumer needs no
  registry lookup") need the same row. One read-only primary-key lookup per device *connect*, cached for
  `Adapter:VehicleProfileTtl` — the same read-only cross-context window provisioning-svc opens for a
  bind, and never a write. Widening C030's `validate` response with a mode was considered and rejected:
  that endpoint's fence is "this service only mints, binds and revokes", and it does not own the column
  either. **§2.1's Container 9 row needs `postgres` added, and the dev compose now declares it.**
  (b) ***D7' §2.2's `runtime:10.0-alpine` cannot be used for this container, and neither can
  `infra/docker/Dockerfile.worker`.*** §2.2 says "`tcp-adapter` uses
  mcr.microsoft.com/dotnet/runtime:10.0-alpine (no ASP.NET)" and C010 built a worker image on that base
  for it. `MageRide.Shared` carries `FrameworkReference Microsoft.AspNetCore.App` — for the middleware,
  health checks and minimal-API results the other twenty services use — and backend/CLAUDE.md requires
  every service to reference it, so a process built against it does not start on the runtime-only image
  whatever it does at run time. `backend/src/TcpAdapter/Dockerfile` (the path the dev compose already
  named) uses `aspnet:10.0-alpine` and says why in its header. The cost is ~10 MB of layer; the
  alternative is a second copy of the kernel. **§2.2, `infra/CLAUDE.md`'s base-image line and
  `infra/docker/Dockerfile.worker`'s header all need the correction** — the worker image is still right
  for `hot-path` only if that project ever stops referencing the kernel, which it does not.
  (c) ***JT/T 808-2013's terminal phone number cannot express an IMEI.*** Six BCD bytes are twelve
  digits; `provisioning.yaml` constrains a binding's IMEI to `^\d{15}$`. So a 2013-header device presents
  an identity **no binding could ever have carried**, and it is refused at connect with that reason named
  (`AuthOutcome.MalformedIdentity`, distinct from `NotBound`, because the two need different fixes). The
  2019 header's ten-byte field carries a zero-padded IMEI comfortably and is what the golden frame uses.
  Both header shapes decode; only the 2019 one can authenticate. Resolving it needs either 2019-capable
  firmware or an **alias index in provisioning-svc** (`imei:alias:{12-digit}` → IMEI, written at bind);
  inventing a mapping here would authenticate a device against a guess, which is the one thing this path
  must not do. **A decision is needed before any 2013-only fleet is onboarded.**
  (d) ***D6' §4.1 and ADD §7.7.1 call H02 "ASCII pipe-delimited"; every device in the family uses
  commas.*** Both separators are accepted — it costs one entry in a `char[]` — rather than picking one
  and refusing the other, because a bus that stops reporting is a worse outcome than a redundant
  separator. **§4.1's table needs the word changed.**
  (e) ***Generic UDP-NMEA shares `source = 4` with NMEA-over-MQTT.*** `ck_positions_source` admits 0…4
  and D6' §4.1 lists five families for five codes, so the two NMEA transports share the code that says
  "this is NMEA". Coining a sixth needs a migration to widen the CHECK for a distinction no consumer
  reads: what a reader wants from `source` is which decoder produced the numbers, and for these two it is
  the same sentence grammar. **Recorded rather than fixed;** if the distinction is ever wanted, it is a
  C006 migration and a `PositionSource` member.
  (f) ***ADD §7.7.3's "per-device pre-shared bearer + IMEI signature" is not expressible on three of the
  four protocols.*** The GT06 login packet is eight BCD bytes of terminal id and an optional two-byte
  model code — there is nowhere to put a credential. H02 and generic-NMEA are the same. **JT/T 808's
  `0x0102` terminal-authentication body is the only field in the four families a credential fits in.**
  `Adapter:RequireCredential` therefore defaults **off**; a credential that *is* presented is always
  verified against `secrets/psk_signing_key`, whatever the setting. The C030 handoff's expectation that
  "an adapter holding the signing key rejects a forged token without a network call" is honoured for the
  one family that can carry one — and `Identity/CredentialTests.cs` mints a real token with C030's own
  `EmbeddedStepCa` and verifies it here, so the two implementations of the format cannot drift silently.
  **§7.7.3's table needs a note naming which families can carry the bearer.**
  (g) ***No spec gives the generic-NMEA framing.*** A `$GPRMC` sentence says where something is and never
  says what, so the three accepted identity prefixes (`IMEI:…;`, `#…#`, a bare digit string) are stated in
  `NmeaCodec`'s remarks and nowhere else. **D6' §4.1 needs the framing written down** before a device
  population is bought.
  (h) ***H02's ACC bit is bit 10 of the status word, inverted, and that is in no document here.*** It is
  what the field-tested decoders for the family do and the reading that makes `FFFFFBFF` — the value a
  unit with the engine running sends — mean ignition-on. Asserted in `CodecTests`; **flagged because a
  wrong reading here auto-starts a Mode A journey every time a bus is parked.**
  (i) ***Three thresholds this component chose because no spec pins them.*** `Adapter:OfflineWindow`
  (5 s — the deadline the T-04 publish must land inside, not a wait), `Adapter:ReplayAge` (60 s — above
  any live cadence D5' §5.2 allows, below the shortest coverage gap worth calling one; JT/T 808's
  `0x0704` needs no heuristic because it *is* the backlog), and `Adapter:IdleTimeout` (15 min — five
  missed GT06 heartbeats, against HAProxy's `timeout client 4h`). Each is argued at its declaration.
  (j) ***D7' §4.2's `Provisioning__ImeiCacheKey` names nothing and should be dropped from the row.***
  The `imei:` prefix is `MageRide.Shared.Caching.RedisKeys.Imei`, spelled once for every service on the
  tracker plane — provisioning-svc writes the key, this service reads it and fleet-health will too — so a
  configurable prefix is a way for three services to disagree about where the cache lives and no way to
  fix anything. It is **kept in `.env.app.example` regardless**, because `slim-verify.sh` asserts the
  templates cover §4.2 line for line and removing it would fail C009's verify; the line now says it reads
  nothing. `Mqtt__ServiceUsername`, which C009 also put in that block, *is* removed — it is not in §4.2,
  named nothing the service reads, and an `env_file` is one flat map, so a `Mqtt__*` key set there for the
  adapter reached every container loading the file (the compose `hot-path` service sets it again in
  `environment:` purely to undo that). `Adapter__ServiceName` replaces it in both places. **§4.2's
  tcp-adapter row needs rewriting** against the settings in `AdapterOptions`.
  **Decisions —**
  (1) **`Microsoft.NET.Sdk.Worker`, and no `AddMageRideDefaults`.** `mqtt-topics.md` §7 says
  "tcp-adapter … has **no HTTP surface**" and D7' §5.1 gives the container a TCP-socket probe for exactly
  that reason. The kernel call configures the HTTP half — RFC 7807, the `Idempotency-Key` middleware, the
  health endpoints, JWT bearer — and none of it has anything to configure in a process no request can
  reach. Postgres, Redis, telemetry and the MQTT session-token issuer are registered individually, each
  through the same kernel extension every other service uses. This is the platform's first service with
  no Kestrel; **"ready" is therefore not a thing this container can answer**, and that is deliberate: a
  device that connects while EMQX is away authenticates normally and its samples are dropped and counted
  (`mageride.tracker.samples_gated{reason=broker_unavailable}`), because refusing devices over a broker
  restart would turn it into the fleet-wide reconnect storm R-09 exists to prevent.
  (2) **T-11 is gated at ingest, and the "online" fact is `veh:driver:{vehicleId}`.** §7.7.7's "pings
  sent while offline are rejected and never reach the live map or dispatch" is a statement about where
  the sample stops — publishing and filtering later would put it on `telemetry.raw`, through
  position-processor's Redis writes and onto the cell streams fanout reads. C039's handoff agrees from
  the other side ("T-11 mode eligibility — dispatch's gate", i.e. not its own). The binding dispatch-svc
  writes at `POST /v1/standby/online` and deletes when the driver goes off duty *is* §7.7.7's sentence,
  and it is already the key position-processor resolves a driver through, so the two planes read one
  fact. The availability hash's `state` is deliberately **not** consulted: a driver mid-offer or mid-ride
  is not `AVAILABLE` and is emphatically online, and gating on the phase would take a Mode C vehicle off
  the map the moment it was hired.
  (3) **`Adapter:PublishWhenModeUnknown` defaults open, and the asymmetry is the argument.** Closed means
  a Postgres blip takes every Mode A bus on the platform off the live map — and §7.7.7 makes the tracker
  "the authoritative and only source" for those, with no app to fall back to. Open means a Mode C
  vehicle whose driver is offline may appear until the lookup recovers, which position-processor's
  freshness gate and dispatch's own availability check both still see. A **stale cache entry is preferred
  to either** (`VehicleProfileCache` keeps expired entries and serves them while the database is
  unreachable), so this only decides a vehicle the pod has never resolved.
  (4) **T-08 is reported, never adjudicated, and both sockets stay open.** Two live sockets holding one
  identity is a fact only this service can see (C030's fence: at `bind` a clone arrives with two
  identities and is decidable there; at the adapter it presents a *copy* of the genuine credential).
  Closing one would destroy the evidence and might well leave the clone publishing, so the adapter POSTs
  `/quarantine` and provisioning-svc's answer arrives back as a revocation on `prov:tracker`.
  (5) **`tracker.bound` closes a socket and `tracker.credential_rotated` does not.** A bind while a socket
  is open means the IMEI moved to another vehicle and that socket is publishing under the old one. A
  rotation must not close anything — "rotation is not revocation, and conflating them bricks devices"
  (C030): the replacement is minted fourteen days early precisely so a tracker parked out of GSM coverage
  can come back and collect it. `DownlinkTests` asserts both directions.
  (6) **The retained presence pair is guarded by `SessionRegistry.IsCurrent`.** An `offline` from a
  session already displaced by a reconnect would overwrite the replacement's `online`, and the value is
  **retained** — the vehicle would read dark to all three LWT consumers until its next reconnect. Across
  pods this cannot be checked at all, which is the fourth reason stickiness is a deployment property and
  not an optimisation (the others: the downlink's socket, the T-08 detection, the JT/T 808 session state).
  (7) **The five downlink commands are a closed set, and an inexpressible one answers null.** GT06's
  command payload is an opaque ASCII string, so a pass-through would turn any publisher on `veh/+/cmd`
  into a device-configuration channel. Not every command exists on every protocol — H02's vocabulary is
  published per device family and only `S71`'s reporting interval has a consistent meaning across the
  population; generic NMEA has no command grammar and no session to write one back on. Those are counted
  as `unsupported` rather than faked, because a device silently discards a command it does not recognise
  and that is indistinguishable from one that arrived and did nothing. `revokeCredential` is honoured by
  closing the socket: no device frame carries it, and stopping service is the only thing the adapter can
  do about a device holding a revoked credential.
  (8) **`AddHostedService<T>` cannot register the three TCP listeners.** It goes through
  `TryAddEnumerable`, which de-duplicates by *implementation type* — so three `TrackerListener`
  registrations silently keep the first and two protocol ports never open, with nothing to see but a
  quiet log. `AdapterListeners` builds them and `ListenerHost` puts them under the host's lifetime.
  Recorded because the failure mode is invisible.
  (9) **`POST /v1/internal/sessions/ignition` now has a caller,** which is **not in this component's
  deliverable list**. C031 landed the route saying in as many words that "the tracker plane decodes ACC
  out of a GT06/JT808 frame (`tcp-adapter`, C043) and had nowhere to say so"; a landed endpoint with no
  caller means a tracker-equipped fleet never auto-starts a journey (AL-32, US-3.22/3.23). The ACC line
  is a bit in a status byte only this service parses, so the decode was happening here regardless. The
  first frame of a session reporting ACC-off is **not** reported — it is the state the device was already
  in, and reporting it would auto-end a session the dashboard started, which AL-32 forbids the device
  from doing. Gated by `Adapter:TripStateBaseUrl`; unset is a start-up warning.
  (10) **The adapter's counters are declared in the service, not in `MageRideDiagnostics`.** Every other
  component put its instruments in the kernel; none of these is cross-cutting, and a protocol adapter's
  vocabulary (`sockets_refused`, `frames_rejected`, `revocation.latency`) does not belong in the assembly
  all twenty-odd services compile against. They are created on `MageRideDiagnostics.Meter`, so the
  exporter D7' §12 configures picks them up by meter name exactly as if they had been.
  **The four golden frames are constructed, not captured.** There is no tracker on this build host, and a
  "capture" pasted from a vendor forum is a frame nobody can check. Every field in `Captures` is derived
  from the published frame layout and every checksum is computed by the algorithm the format names — and
  the one independently attestable fixed point, the GT06 documentation's login acknowledgement
  `78 78 05 01 00 01 D9 DC 0D 0A`, verifies against the same CRC (`WireTests`), which pins both the
  algorithm (CRC-16/X-25, not CRC-CCITT) and the range it covers. All four frames describe the same
  vehicle at the same instant in the same place, so a hemisphere bit read backwards, a knot read as a
  km/h or a Beijing timestamp left in local time shows up as a disagreement with the other three.
  **provisioning-svc is a stub in this suite, and the two formats where that would hide something are
  not.** What the adapter needs from `validate` is four fields; running the real service to produce them
  would drag in step-ca, its migrations and an authenticated bind flow. What a stub cannot check is
  *format agreement*, and there are exactly two formats written down on both sides of the fence — the
  signed PSK token and the `prov:tracker` signal. Both are asserted against `Provisioning.Api`'s own
  `EmbeddedStepCa` and `TrackerCredentialSignal` in `Identity/CredentialTests.cs`, because a divergence
  in either is silent: a renamed JSON field turns every value null and the socket never closes; a changed
  HMAC payload makes every credential look forged.
  **What the next components need from this one —**
  **For C044 (fleet-health-svc) —** the GT06 status byte's voltage level and GSM signal strength, and
  JT/T 808's additional items, are **decoded by nothing today**. `prov.tracker_bindings.signal_strength`,
  `battery_mv` and `sat_count` have a reader (C030's `GET /v1/trackers/{imei}`) and no writer;
  `sys/diag/{vehicleId}` (D6' §3.1, QoS 0) is the topic they belong on and this service publishes nothing
  there. C044 either consumes a diagnostics topic this component would have to start publishing, or the
  fields stay null — **decide which, and if it is the former, say so and it is a small change here.**
  **For C044 / C062 —** `mageride.tracker.samples_gated{reason=mode_c_offline}` rising for a vehicle is
  the operational signal that a Mode C tracker is reporting while its driver is off duty, which is
  US-3.6's "one publisher at a time" question in a different form. Nothing alerts on it.
  **For C125 (the replica) —** `Adapter:ShardCount`/`Shard` are off in the dev compose because there is
  one pod. A multi-pod deployment must **also** make the L4 balancer sticky by device (HAProxy
  `stick-table` on the source address in front of 5023-5025, `sessionAffinity: ClientIP` on DOKS) or the
  four per-pod facts split silently — the adapter logs the disagreement once per device and serves it
  anyway. The hash is FNV-1a over the IMEI's ASCII digits and is pinned by a test.
  **For provisioning-svc (a future Δ) —** gap (c)'s alias index, if a JT/T 808-2013 fleet is onboarded.
  **Build host —** Docker for three Testcontainers fixtures (EMQX, Redis, Postgres); the replica stack
  stayed down throughout. `TcpAdapter.Tests` takes ~1 min 30 s (the codec half is ~0.5 s of it).
  **No new NuGet reference** — `MQTTnet`, `StackExchange.Redis`, `Dapper`, `Npgsql` and
  `Microsoft.Extensions.TimeProvider.Testing` are all already pinned centrally.
