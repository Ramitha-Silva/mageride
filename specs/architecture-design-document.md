# MageRide — Nationwide Real-Time Vehicle Tracking & Ride Platform
## Architecture Design Document (ADD) v3.5

> **Project:** MageRide — Nationwide Real-Time Vehicle Tracking & Ride Platform
> **Document type:** Architecture Design Document (ADD) v3.5
> **Audience:** Engineering, SRE, Security, Product, Investors / technical reviewers
> **Status:** For Design Review
> **Date:** July 23, 2026 (v3.5 — **Micro-change-set 2026-07-23, 1 change** (see §1.17 AL-56): **the standalone GTFS Acquisition & Authoring Plan is retired** — the GTFS dataset (day-0 national feed **and every refresh**) is an **externally provided file** whose sole ingestion surface is **SCR-AP-016** (AL-54); no in-house authoring workstream exists, and all spec references to the plan document have been removed.) (v3.4 — **Discussion 2026-07-22 change set #2, 2 changes** (see §1.16 AL-54…AL-55 / URD v2.9 Epic 28): **GTFS Dataset Manager — new Admin Portal screen SCR-AP-016** (`admin.mageride.lk` → Configuration group): upload the **full GTFS zip**, server-side **validation with a downloadable row-level error report**, dataset **preview (counts + feed_info version + service window)**, **atomic activation** (staging load → transactional swap → `transit-svc` cache reload), **version history with one-click rollback**; new **`transit.gtfs_feed_versions`** table + versioned `/admin/transit/gtfs/*` endpoint set (raw `POST /admin/transit/gtfs-import` superseded); **full-feed-at-launch premise** — a complete national GTFS file is available at the beginning, so the corridor-first G0–G3 acquisition gating becomes refresh/maintenance methodology only *(that standalone acquisition plan was subsequently retired outright — AL-56)*, and SCR-PA-009's no-coverage degradation becomes a **safety net, not a launch state**.) (v3.3 — **Discussion 2026-07-22, 2 stack decisions** (see §1.15 AL-52…AL-53): **Tailwind CSS mandated as the sole styling system for all MageRide web frontends** — Admin Portal + Fleet Portal (Next.js) and the SCR-WT passenger web-subview pages — one shared **Tailwind preset** carrying the D2 §A brand/typography tokens (light/dark via `dark:` variant), compiled by PostCSS inside `npm run build`; **no CSS-in-JS, no MUI/Bootstrap**. **Backend stack reaffirmed (audit, no drift)**: **.NET 10 Minimal API + Dapper over Npgsql** (in force since v2.5) verified consistent across §2/§6/§18 and D3/D4/D7 — no changes required.) (v3.2 — **Discussion 2026-07-18 change set, 3 changes** (see §1.14 AL-49…AL-51 / URD v2.8 Epic 27): **fleet bank & payout profile** — new Owner-only screen **SCR-FP-002a** (bank/branch/account no/account holder name + statement-or-passbook-first-page + **bank-app LankaQR image** uploads; Verification-Officer-approved via the fleet-org queue; new `registry.fleet_payout_profiles`; passenger Mode B pay sheet consumes the **verified** profile as `payTo`; **Service payment = Paid gated on Verified**); **SCR-FP-004 vehicle onboarding detailed** into named per-vehicle document slots — **registration copy (CR), insurance, revenue license, route permit (Mode A required)** — reusing the Mode-C OCR/per-field-verification pipeline, `registry.documents` opened to fleet uploads (driver_id nullable + fleet_id), approval gate extends AL-10; **"Mode B classification" renamed "Service payment" (Free/Paid)** — UI/docs label only, `/classification` + `mode_b_billing` unchanged.) (v3.1 — **Discussion 2026-07-05 change set #2, 2 decisions** (see §1.13 AL-47…AL-48 / URD v2.7 Epic 26): **driver-QR attestation settlement** — passenger "I've paid" claim (`QrClaimedByPassenger`) + driver confirm → terminal **`DriverConfirmedQR`**; settles like cash, earning posts on confirm (R-05); disputes → Support/Finance (no gateway callback exists for the driver's own bank QR); gateway-verified `Succeeded` stays OnePay-only. **Number-masking requirement withdrawn** — "Normal call" = **direct cellular dial to the counterparty's real number** (revealed post-accept; P-05 proxy routing retained), web subview call = plain **`tel:` link** (drops `POST /public/track/{token}/call` + the proxy-DID lease from AL-44), VoIP-failure fallback = **direct-dial prompt** (D-25 masked-SMS relay removed).) v3.0 — **Discussion 2026-07-05, 8 changes** (see §1.12 AL-44…AL-46): **Passenger Web subview (`passenger.mageride.lk`) formalized into buildable contracts** — screen IDs **SCR-WT-001…006**, a stateless **`public-bff`** serving token-authenticated `/public/track/{token}` snapshot + SSE live feed + receipt; **unregistered proxy rider web pickup-confirm** via SMS-minted `pickup_confirm` token (extends US-8.19's fallback); **web masked call** via ride-scoped proxy DID (`tel:` dial, no web VoIP stack); **web SOS** → dual-gateway SMS to the booker; `trip_share_tokens` gains **proxy_rider / pickup_confirm scopes + access metering**; spec hygiene (SCR-DA/DI-012 re-tagged [MERGED → 010]).) v2.9 — **Discussion 2026-06-28, 11 changes** (see §1.11 AL-36…AL-43): **Admin Portal MFA/OTP step removed** (password/Google only); **admin dashboard statistics filter** (today/week/month/custom range); **verification split** into queues-list + detail + **full-size document viewer** + fleet-org detail; **admin passenger / driver / vehicle directories** with multi-criteria search, detail & transactions; passenger **schedule-ride destination**, **call-type chooser** (free VoIP vs masked cellular), **driver mobile in trip history**, **Get-Started pinned bottom**; driver **camera document-scanner with draggable-corner crop**.) v2.8 — **Discussion 2026-06-25, 13 driver changes** (see §1.10 AL-28…AL-35). v2.7 — **Discussion 2026-06-21, 17 changes** (see §1.9 AL-17…AL-26): geo-only search + **GTFS `transit-svc`** for direct public-bus routes; Mode C price-only tiers; **Google-Maps Paste-link** parsing; package **drop-off** capture + **recipient FCM/SMS web-tracking** on pickup-confirm; **QR-scan pay**; **per-vehicle** Mode B sharing/requests (multi-vehicle + temp-hired); new **Mode B subscription payments** (Paid/Free, per-subscriber fare, LankaQR/OnePay/transfer/cash, owner-verified) + **unsubscribe/muted-until-deleted**; saved-address lines + label; language vertical Sinhala-first & removed from Edit-profile.) v2.6 — **Alignment with URD v2.2** (see §1.8 AL-01…AL-16): reseller folded into the Driver role (no reseller portal/account); back-office consolidated into a single **Admin Portal** at `admin.mageride.lk`; **Fleet Portal** at `fleet.mageride.lk` + `fleet-svc` promoted to **Phase 1**; **Passenger Web subview** at `passenger.mageride.lk`; **Bank-transfer top-ups removed**; nine-role RBAC + auth scoping (apps = Phone OTP; Admin = Password/Google; Fleet = Email/Google/Apple); single-active-device is **per app**; canonical vehicle types (+Truck/Mini Truck); insurance mandatory all modes; VoIP → P0. v2.5 — .NET 10 Minimal API + Dapper. v2.4 — Directional Travel)

---

## Table of Contents

1. [Architecture Critique Summary](#1-architecture-critique-summary)
2. [Executive Summary](#2-executive-summary)
3. [Goals, Non-Goals and Assumptions](#3-goals-non-goals-and-assumptions)
4. [High-Level Architecture Narrative](#4-high-level-architecture-narrative)
5. [Logical Architecture](#5-logical-architecture)
6. [Component Architecture (Microservices)](#6-component-architecture-microservices)
7. [Real-Time Messaging Architecture](#7-real-time-messaging-architecture)
8. [Geospatial Architecture — Redis GEO vs PostGIS](#8-geospatial-architecture--redis-geo-vs-postgis)
9. [Data Architecture](#9-data-architecture)
10. [Physical Deployment Architecture](#10-physical-deployment-architecture)
11. [Sequence Flows](#11-sequence-flows)
12. [Security Architecture](#12-security-architecture)
13. [Observability Architecture](#13-observability-architecture)
14. [High Availability & Failover](#14-high-availability--failover)
15. [Disaster Recovery](#15-disaster-recovery)
16. [Capacity Planning & Sizing](#16-capacity-planning--sizing)
17. [MVP vs Scale Guidance](#17-mvp-vs-scale-guidance)
18. [Technology Stack Recommendations](#18-technology-stack-recommendations)
19. [Multi-Stage Evolution Roadmap](#19-multi-stage-evolution-roadmap)

---

## 1. Architecture Critique Summary

### 1.1 What the Baseline Gets Right

- **MQTT (EMQX) as the device ingest layer.** Correct choice. MQTT is the de-facto telematics protocol — low overhead, QoS levels, last-will, persistent sessions, native to constrained devices and mobile networks with intermittent connectivity. Far better than letting trackers/phones speak directly to SignalR.
- **Separation of ingest plane (MQTT) from fan-out plane (SignalR).** Architecturally sound. These have very different traffic patterns (write-heavy vs read-heavy fan-out) and must scale independently.
- **Redis GEO for live "where are nearby vehicles right now".** Correct. PostGIS is wrong for hot-path 1 Hz lookups at 100k vehicles.
- **PostGIS for persistent spatial data** (routes, stops, geofences, historical trips). Correct.
- **HAProxy + Keepalived for L4 HA in a VPS world.** Pragmatic and cheap. Valid for pre-Kubernetes stages.
- **Ingest adapter pattern for ST-901–class TCP trackers.** Correct — these devices use proprietary binary TCP protocols (JT/T 808, GT06, H02), not MQTT. An adapter normalising them into MQTT is the right pattern.
- **Phone OTP via SMS gateway for user auth.** Pragmatic for MVP — phone-verified identity with low SMS cost via local gateway (e.g., Notify.lk). Firebase Auth retained as optional Google Sign-In bridge.

### 1.2 Critical Flaws and Risks

#### Architectural

1. **SignalR is doing far too much.** In the baseline design SignalR (a) subscribes to MQTT, (b) updates Redis GEO, (c) writes Postgres every 60 s, (d) computes "who is within 3 km", (e) fans out to clients. This is a god-service. It must be split — at minimum into an **Ingest/Processor service** and a **Push/Fanout service**.

2. **No event log / replay capability.** If SignalR crashes mid-broadcast, positions are lost. You need a durable, partitioned event log (Redpanda / Kafka) plus MQTT QoS1 + persistent session + a durable processor.

3. **No concept of "interest registration".** You cannot scan 100k vehicles for every one of 1M passengers every second. The fan-out model must be **subscriber-driven**: passenger device subscribes to a geo-cell (geohash / S2 / H3) channel; the processor publishes per-cell, not per-passenger.

4. **"3 km radius broadcast" doesn't scale.** A naive `GEOSEARCH` per client per second = 1M × 1 QPS = 1M Redis ops/sec just for queries. Use **geohash bucketing** (e.g. geohash-6 ≈ 1.2 km cells) and SignalR groups keyed by cell. Client subscribes to its own cell + 8 neighbours.

5. **Writing every position to Postgres every 60 s is fine — but routing it through SignalR is wrong.** Persistence is a separate consumer of the MQTT/event stream.

6. **No trip/journey state machine service.** Stories 5, 6, 7b, 10 imply a stateful trip lifecycle (started, paused, passenger-onboard, completed, auto-ended after 30 min idle, auto-ended at end-position). This deserves its own bounded context with its own DB tables and a background timer.

7. **No rate-limiting / abuse control on the device side.** A misbehaving phone publishing at 50 Hz can degrade the whole MQTT cluster.

8. **No backpressure model.** What happens when MQTT ingests faster than SignalR can fan out? Today: SignalR OOMs. Solution: a stream (Redpanda / Kafka / Redis Streams) with consumer groups.

9. **"Vehicle can be registered only into one mobile phone"** — the document doesn't define device binding cryptographically. Without device-bound keys, anyone with the credentials can spoof a vehicle's GPS.

10. **No anti-spoofing on positions.** Mobile-published GPS is trivially faked. You need plausibility checks (max speed, jump distance, accuracy radius, dead-reckoning).

#### Capacity / Sizing

11. **A single 16 GB / 6 vCPU Contabo VPS for MVP is fine — but the doc treats it as the production substrate.** It is a single-point-of-failure for everything: EMQX, Redis, Postgres, SignalR, HAProxy all on one box = no HA, no durability, no isolation, noisy neighbour.

12. **Phone OTP via SMS gateway** is cost-effective (Rs 0.50–1.50/SMS vs Firebase's $0.27/SMS) but still a per-auth operational cost. At 1M users with re-auth/OTP resends, plan SMS budget. Firebase Auth is retained only for optional Google Sign-In (free up to 50k MAUs). Plan a Keycloak exit path for full auth independence.

13. **No estimate of message rate.** 100k vehicles × 1 Hz = 100k msg/s ingest, ~20–30 MB/s. EMQX handles this on 1 node but Redis writes at 100k ops/s and Postgres writes at ~1.6k/s (1/min) need a writer pipeline, not synchronous writes from SignalR.

14. **No mobile network reality.** GPS via mobile data on 100k vehicles 24/7 is a non-trivial cost burden on owners. MQTT publish interval should be **adaptive** (1 Hz moving, 0.1 Hz idle, suppress duplicates).

#### Security

15. **MQTT auth is unspecified.** Username/password is insufficient. Use **per-device X.509 client certs** (or JWT with short TTL) issued at provisioning, plus EMQX ACLs scoped to `veh/{vehicleId}/#`.

16. **No TLS termination strategy.** HAProxy SNI passthrough vs termination is not addressed.

17. **No secrets management** (Vault / SOPS / sealed-secrets).

18. **Gemini Flash extracting permit/ID data** — PII leaves your perimeter to a third-party LLM. Needs a privacy assessment and a fallback OCR pipeline (Tesseract / Azure Doc Intelligence).

19. **Driver platform fee** uses the **Namma Yatri (India) methodology** — a flat **daily platform fee** by vehicle type. Mode A (Public Transport) **buses and trains** pay **no fee** (trains are admin-registered Mode A services). Mode C (Standby On-Demand) daily fees: Motorbike Rs 50, Three-wheeler Rs 100, Flex Rs 150, Sedan Rs 200, Mini Van Rs 250, Van Rs 300. The **first trip of the day is always free**; fee is auto-deducted from the driver's wallet before the **2nd trip**. Mode B (Private Transport) vehicles pay a **monthly charge per vehicle ~Rs 300** (consolidated for fleets — AL-03). **No per-trip fees, no commission.** Wallet top-ups are handled **directly in the Driver App** via credit/debit card (OnePay), OnePay wallet, and LankaQR — **bank transfer removed (AL-05)**; drivers never use a web portal. Drivers may purchase **bulk credit vouchers** (Rs 1,000 / 2,000 / 3,000 / 5,000 / 10,000); the per-tier discount is **configured in the database** (variable/admin-configurable) and applied **only on purchase**, crediting the buyer's own wallet immediately (e.g., pay Rs 900 → Rs 1,000). A driver can then **transfer credit to other drivers by Driver ID with no commission** (exact value debited/credited). **"Reseller" is not a separate role, account, or enabled capability** (AL-01) — it is simply any driver who bought bulk credit cheaply and resells it; the margin is the purchase discount, **not a per-transfer commission**. Google Play Subscription is explicitly excluded; in-app top-up uses external payment methods (card/LankaQR), permitted by Google Play for physical/off-platform service fees.

#### Operational

20. **Zero observability mentioned.** No logs/metrics/traces/alerting/SLOs.
21. **No CI/CD, no IaC, no environments (dev/stage/prod).**
22. **No DR/backup strategy.**
23. **Docker Swarm vs Kubernetes is not addressed.** The "production setup" implies plain Docker + HAProxy. That's defensible up to ~5 nodes, then becomes painful.
24. **No multi-AZ / multi-region thinking.** Contabo is single-region; for "nationwide critical transport infra" you need at least warm DR.

#### Domain / Product

25. **"Ad-hoc traveller fare calculation"** (story 7b) is a regulated activity in many jurisdictions — not just a distance × rate calc. Needs surge logic, receipts, dispute resolution, driver wallet, payouts.
26. **"Private transporter share + access request"** is essentially a permissions/ACL subsystem. Not modelled.
27. **"Premium subscription unlocks tracking"** entitlement is not separated from auth.

### 1.3 Remediation Log (ADD v2.0)

This v2.0 revision applies a deficit-log review against URD v1.0. Each item below is a concrete, in-document fix; the corresponding section is referenced.

**Scope confirmation (closed, no change):** the platform targets **four mobile applications** — Passenger Android, Passenger iOS, Driver Android, Driver iOS — via KMP shared logic + Jetpack Compose / SwiftUI native UI. The URD wording "two Android applications" is the *minimum* P0 set; iOS apps ship in Phase 1. The 4-target scope governs §10, §16, §18, §19.

**Driver-vehicle exclusivity (closed, preserved):** the URD constraint "a driver may have only **one** vehicle live at a time" (US-9.6) is the single source of truth. Enforcement is added in §6 (`trip-state-svc` active-session lock) and §9.1 (`trips.sessions` unique partial index). See **D-03**.

| ID | Deficit | Resolution | Section |
|---|---|---|---|
| D-02 | URD-epic → phase mapping not echoed | Phase task lists tagged with URD epic IDs | §19 |
| D-03 | One-vehicle-live-per-driver not enforced | Redis lock `lock:driver:{driverId}` + Postgres unique partial index on `trips.sessions(driver_id) WHERE state='ACTIVE'` | §6 `trip-state-svc`, §9.1 |
| D-04 | "3-strike" counters scattered | New `reputation-svc` aggregates cancellation / no-show / report counters with rolling-window reset | §5.1, §6 |
| D-05 | Cancellation-penalty cross-trip settlement undefined | Outbox-driven settlement on next trip completion, idempotent on `(penaltyId, applicationTripId)` | §6 `fare-svc`, §11.7 (new) |
| D-06 | Job Board "30 km H3 ring" infeasible | Switched to PostGIS `ST_DWithin` on `dispatch.driver_presence`; H3 res-5 only as coarse pre-filter | §6 `dispatch-svc`, §8 |
| D-07 | 15 s offer TTL mechanism not stated | Redis key with `PEXPIRE` + keyspace-notification reassignment | §6 `dispatch-svc` |
| D-08 | Dispatch ↔ wallet hot-path coupling | Redis `wallet:bal:{driverId}` cache (5 s TTL, debit-invalidated); degraded-mode rule documented | §6, §14.1 |
| D-09 | No double-entry ledger | `billing.accounts`, `billing.journal_entries`, `billing.journal_postings` (balanced) | §9.1 |
| D-10 | Payment state machine missing | `fare-svc` state: `Initiated → Pending → Succeeded / Failed / Retried / FellBackToCash` | §6, §11.8 (new) |
| D-11 | OnePay driver-merchant onboarding missing | Added onboarding step in `registry-svc` vehicle-approval flow; merchant binding persisted in `registry.driver_payouts` | §6, §11.9 (new) |
| D-12 | Bank-transfer auto-reconciliation undefined | Named integration: Commercial Bank IPG webhook + manual fallback queue; SLAs documented | §6 `wallet-svc`, §18.1 |
| D-13 | Daily-fee idempotency timezone undefined | All "calendar_date" columns defined in `Asia/Colombo`; PK `(driver_id, vehicle_id, fee_date)` | §9.1 |
| D-14 | Tile / geocoder services not deployed | Added `tile-cdn` (Cloudflare R2 + Worker) and `nominatim-svc` to §6 / §10 | §6, §10 |
| D-15 | OSM update pipeline missing | Weekly `osm-pipeline` CronJob: diff → osm2pgsql → tippecanoe → PMTiles → R2 sync | §19 Phase 1 |
| D-16 | Cloudflare free-tier TOS risk for tiles | Plan upgrades to Cloudflare Pro at >50 GB/mo tile egress; Bunny.net fallback documented | §16.2, §18.1 |
| D-17 | Server-side rate-limit unenforced | EMQX rule-engine: suppress pubs faster than the per-`vehicleId` ceiling (**5 msg/s** under phase-aware cadence, §7.5.2 — originally 2 Hz); `position-processor-svc` second-line check at 10 msg/s | §7.5, §12 |
| D-18 | Plausibility 200 km/h is arbitrary | Per-vehicle-type max-speed table; accuracy > 200 m discarded | §12.6 |
| D-19 | NFR-01 (2–8 s) vs SLO (p95<2 s) contradiction | SLO restated as **p95 < 5 s, p99 < 8 s** (consistent with US-5.5 cadence) | §3.2, §13.3 |
| D-20 | Ingest rate 0.5 msg/s overstated | Blended **0.12 msg/s** derived from US-5.5; sizing recomputed | §16.1 |
| D-21 | EMQX JWKS thundering herd | EMQX local JWKS cache (15 min TTL) + JIT lookup on miss | §7, §12.1 |
| D-22 | Mode B revocation push absent | `share.revoked` event → `fanout-svc` directed `RemoveFromGroupAsync` | §6, §11.10 (new) |
| D-23 | Mode B entitlement uncached | Redis `share:{userId}` SET, pub/sub-invalidated; SignalR join checks cache first | §6 `fanout-svc` |
| D-24 | VoIP not designed | New `voip-svc` (LiveKit SFU + coturn cluster); P1 target 500 concurrent calls | §5.1, §6, §10.3, §16 |
| D-25 | VoIP fallback undefined | If VoIP fails, masked-number SMS relay via `notification-svc`; no direct number exposure | §6 `voip-svc` |
| D-26 | Server-side i18n missing | New `content-svc` for localised notifications, FAQ, broadcasts (Si/Ta/En) | §5.1, §6 |
| D-27 | FCM/APNs rate-limit handling | HTTP v1 batch send + topic strategy + exponential backoff worker | §6 `notification-svc` |
| D-28 | SMS budget unmodelled | Added to §16 capacity table | §16 |
| D-29 | Token store / revocation undefined | 30-min access JWT (RS256, JWKS); opaque refresh in `iam.sessions` + Redis; rotated on use; revokes prior device | §12.1 |
| D-30 | Attestation enforcement point missing | YARP middleware validates Play Integrity / App Attest header on sensitive endpoints | §12.6 |
| D-31 | Minimum-version gate missing | API gateway `X-App-Version` check; rejects with `426 Upgrade Required` | §12 |
| D-32 | OTP rate-limit missing | Redis token bucket: 60 s resend cooldown, 5 attempts/h | §6 `iam-svc`, §12 |
| D-33 | SOS SLO missing | New SLO: SMS dispatched ≤ 5 s p99; secondary SMS gateway fallback; admin live-feed channel | §13.3, §6 `safety-svc` |
| D-34 | Trip-share token scoping weak | Token scoped to trip + 1 h grace, 60 req/min, revocable, no historical replay | §6 `safety-svc`, §12.6 |
| D-35 | Admin audit-log writers unenumerated | `admin-bff` interceptor emits `audit.events` on every mutation (vehicle approve/reject, wallet adjust, ban, config change) | §6, §9.1 |
| D-36 | PII redaction before LLM aspirational | Pre-Gemini redaction pass: OpenCV face-blur + ID-number masking via Tesseract bounding boxes | §12.5 |
| D-37 | Vehicle-registration uniqueness missing | Unique partial index `registry.vehicles(registration_number) WHERE status IN ('PENDING','APPROVED')` | §9.1 |
| D-38 | Timezone unstated | All temporal business columns: `TIMESTAMPTZ`; business-date logic uses `Asia/Colombo` | §9.1 |
| D-39 | Cost tables under-stated | Re-priced for DOKS, VoIP TURN, SMS budget, R2/CDN, OnePay/LankaQR fees | §16.2 |
| D-40 | SignalR throughput claim unsourced | Re-stated as "10–25k sends/pod/s observed on 2 GB pod"; pod count re-derived | §16.3 |

### 1.4 Remediation Log (ADD v2.1 — Ride-Hailing Viability)

v2.1 applies the findings of `ride-hailing-architecture-viability-review.md`. The earlier ADD was strong as a tracking platform but underspecified the Mode C ride control-plane. The fixes below introduce a dedicated **Ride Aggregate** (`ride-svc`), atomic single-winner acceptance, a complete cancellation matrix, idempotent ride commands, mobile offline GPS replay, and operational alerts on stuck workflows. Items E-* are additional gaps neither v2.0 nor the review captured.

| ID | Deficit | Resolution | Section |
|---|---|---|---|
| R-01 | Ride lifecycle conflated with vehicle tracking session | New **`ride-svc`** owns Mode C ride aggregate; `trip-state-svc` keeps only A/B tracking sessions | §6, Appendix B, Appendix C |
| R-02 | Concurrent driver acceptance not atomic | Conditional `UPDATE … WHERE state∈('MATCHING','OFFERED') AND offer_expires_at>now() AND version=:v`; Redis Lua reservation as fast path | §6 `ride-svc`, §9.1, §11.11 (new) |
| R-03 | Cancellation matrix incomplete | Full server-owned transition table for rider/driver/no-show/timeout/payment-fail | §11.12 (new) |
| R-04 | Redis TTL is sole offer timer | Durable backstop: Quartz.NET clustered (Phase 1) / MassTransit saga (Phase 2) / Temporal.io (Phase 3); Redis TTL = fast hint | §6 `ride-svc`, §14 |
| R-05 | Ride↔payment coupling unstated | Driver earning posts only after payment terminal state (`Paid` / `CashSettled` / `Disputed`); ride `Completed` ⇒ `PaymentPending` until settlement | §11.8 (update), Appendix B |
| R-06 | H3 res-8 + ring(1) does **not** cover 3 km | Corrected: H3 **res-7 + ring(2)** for 3 km passenger view; H3 res-5 + ring as dispatch coarse pre-filter; exact-distance post-filter mandatory | §7.4 |
| R-07 | GPS cadence is fleet-blended only | Phase-aware cadence table (standby / candidate / accepted / near-geofence / in-progress) + server-issued `veh/{vehicleId}/cmd` cadence hints | §7.5 |
| R-08 | Redis dispatch availability index missing | New keys `geo:drivers:available:{type}:{cell}` (GEO) + `driver:availability:{driverId}` (string w/ state+TTL) | §9.4 |
| R-09 | Reconnect storms / replay floods | EMQX connection rate-limit (per listener + per ASN); separate `veh/{vehicleId}/pos/live` vs `pos/replay` topics; replay throttled | §7.5 |
| R-10 | Driver reservation not atomic, dispatch sharded by geocell | Reservation via Redis Lua + Postgres `UNIQUE(driver_id) WHERE status IN ('OFFERED','ACCEPTED')`; **ride workflow sharded by `rideId`**, candidate search by geocell | §6 `dispatch-svc`, §9.1 |
| R-11 | Scoring formula not documented / not auditable | Versioned weighted score + `dispatch.candidate_scores` row per evaluation; `dispatch_algorithm_version` persisted on offer | §6 `dispatch-svc`, §9.1 |
| R-12 | Sequential vs batch matching unstated | Phase 1 = **sequential**; batch matching deferred to Phase 2 behind feature flag once atomic-revoke proven | §6 `dispatch-svc` |
| R-13 | Offer emitted inline; no outbox | `offer.created` written through transactional outbox; driver push only after DB commit | §6, §11.11 |
| R-14 | Idempotent ride commands missing | `ride.command_log(idempotency_key UNIQUE)`; every mutating ride API requires `Idempotency-Key` header; duplicates replay original response | §9.1, Appendix C |
| R-15 | EMQX LWT not wired to dispatch | `veh/{vehicleId}/status=offline` event → `dispatch-svc` releases active offer / starts grace timer per ride state | §6, §11.12 |
| R-16 | Driver grace policy undefined | Per-state grace windows (offline-after-accept 60 s, after-arrive 120 s, in-progress 5 min, at-payment 10 min) in config | §11.12, §14.1 |
| R-17 | Mobile local GPS buffer missing | Android Room / iOS SQLite queue in foreground service; replays via `veh/{vehicleId}/pos/replay` with monotonic sequence; server rejects out-of-order/duplicate samples | §7.5, §18.2 |
| R-18 | Passenger request retry duplicates rides | `UNIQUE(passenger_id, client_request_id)` on `rides`; idempotent `POST /rides/request` returns existing ride on retry | §9.1, Appendix C |
| R-19 | Payment-callback-after-cash-fallback unhandled | `payment.overpaid` exception state → admin refund queue; provider callbacks idempotent on `provider_transaction_id` | §11.8 (update), §11.14 (new) |
| R-20 | Stuck-state alerts absent | New SLOs / runbooks: `Matching>60s`, `Offered>20s`, `Accepted no-pos>60s`, `DriverArrived>10min`, `InProgress no-GPS>5min`, `Completed+PaymentPending>10min` | §13.3, §13.4 |
| E-01 | Mobile background-execution kills offers | Driver offer push uses **FCM high-priority** (Android, bypasses Doze) + **APNs `apns-priority: 10` silent push with `content-available:1`** (iOS, wakes app); no-ack within 3 s → SMS fallback to driver | §6 `notification-svc`, §18.2 |
| E-02 | JWT 30 min expires mid-trip in low coverage | **MQTT session JWT decoupled from API JWT**: MQTT token TTL = max(active-ride duration + 2 h, 4 h), bound to `(vehicleId, deviceId, rideId?)`; API access JWT remains 30 min with proactive refresh | §12.1, §6 `iam-svc` |
| E-03 | Driver document expiry untracked | `registry.documents(expires_at, status)`; nightly job emits `document.expiring` (T−30d/T−07d/T−1d) and `document.expired` → auto-suspends dispatch | §6 `registry-svc`, §9.1 |
| E-04 | Fare distance from raw GPS inflates 5–15% | Phase 1: Kalman filter + accuracy-weighted resample in `fare-svc`; Phase 3: OSRM `match` snap-to-road | §6 `fare-svc`, §19 |
| E-05 | Refund / dispute workflow missing | New `fares.refunds` + `billing.journal_entries.kind='payment_refund'`; admin-initiated full/partial refund via OnePay / LankaQR reverse APIs | §6 `fare-svc`, §9.1, §11.14 |
| E-06 | PDPA right-to-erasure / data export unmodelled | New `pdpa.requests` workflow in `admin-bff`: export within 30 d, erasure (soft-anonymise) within 30 d, with statutory hold list (active rides, open disputes, audit·log immutable subset) | §6 `admin-bff`, §9.1, §12 |
| E-07 | Anti-collusion / ride-farming undetected | `reputation-svc` adds pair-frequency detector (same `(passenger, driver)` > N rides / 30 d), device-binding cross-check, IP/ASN clustering | §6 `reputation-svc`, §12.6 |
| E-08 | `mqtt-bridge-svc` shared-subscription topology unspecified | EMQX shared subscription `$share/posGroup/veh/+/pos/live`; bridge replicas load-balance, no duplicate ingest | §7.3 |
| E-09 | Outbox poll latency adds ~250 ms to offer | Postgres `LISTEN/NOTIFY` wakeup (or Debezium) on `outbox.events` table; offer push median < 50 ms | §6 `dispatch-svc`, §6 `ride-svc` |
| E-10 | Tip / gratuity not captured | `fares.ride_payments.tip_amount_minor` + journal kind `tip_payout`; UI opt-in post-trip | §9.1, Appendix C |

### 1.5 Remediation Log (ADD v2.2 — Proxy Booking & Package Delivery)

v2.2 applies `proxy_booking_package_delivery.md` against the URD. Two related capabilities are added: (1) **Proxy Booking** — a booker reserves a Mode C ride for a third-party *rider* whose pickup location may be resolved via an in-app FCM "Request Location" round-trip (P1, Phase 1), and (2) **Package Delivery** — a Mode C extension where the cargo, not a passenger, is moved, verified end-to-end by OTPs at pickup and drop-off (P2, Phase 2). Both reuse the existing Mode C dispatch, fare, daily-fee, and reputation infrastructure; the changes below are *additive* — the v2.1 atomic-accept, cancellation matrix, and idempotency invariants apply unchanged.

| ID | Deficit | Resolution | Section |
|---|---|---|---|
| P-01 | Ride aggregate assumes booker = rider | `rides.rides` adds `booker_id`, `rider_id`, `rider_phone`, `rider_name`, `is_proxy BOOLEAN`; rider may be unregistered (no `iam.users` FK). All notifications fan out to **both** booker and rider channels | §5.1, §6 `ride-svc`, §9.1, Appendix C |
| P-02 | No mechanism for booker to request rider's live GPS | New **location-request** sub-flow in `ride-svc` + `notification-svc`: short-lived FCM data-message to rider's app, rider confirms on map, `pickup_geo` posted back via WebSocket to booker; 5 min TTL | §6, §9.1, §11.15 (new) |
| P-03 | Rider may not be a registered MageRide user | `iam-svc` lookup-by-phone returns `{registered:false}` → booker falls back to map-pin or address search; no FCM dispatched. PII (phone) of unregistered rider is hashed at rest, retained only until ride terminal | §6 `iam-svc`, §12.5 |
| P-04 | Payment routing for proxy bookings | `fares.ride_payments` adds `payer_role ∈ {rider,booker}`, `payer_user_id`; `Cash` always paid by rider, `LankaQR`/`OnePay` always paid by booker; payment state machine (§11.8) unchanged | §6 `fare-svc`, §9.1 |
| P-05 | Driver UI must distinguish proxy bookings | Ride offer event includes `is_proxy`, `rider_name`, `rider_phone_masked`; driver app shows "Third-party booking" badge; VoIP signalling token bound to `(rideId, rider_id ∣ booker_id)` so driver can call **rider** (not booker) | §6 `ride-svc`, §6 `voip-svc` |
| P-06 | Package delivery conflated with passenger ride | `rides.rides` adds `kind ∈ {passenger,package}`, `package_size ∈ {S,M,L}`, `package_description`, `pickup_otp_hash`, `delivery_otp_hash`, `proof_photo_url`; same state machine, same fare tariff, same daily-fee semantics | §5.1, §6 `ride-svc`, §9.1, Appendix B.2 |
| P-07 | OTP verification at pickup/drop-off untracked | Two new ride state events: `package.picked_up` (driver entered correct `pickup_otp` → DriverArrived → InProgress transition) and `package.delivered` (driver entered `delivery_otp` OR uploaded `proof_photo_url` → Completed). OTPs are 4-digit, generated by `ride-svc`, hashed at rest, validated server-side, max 5 attempts; expired/exhausted → admin queue | §6 `ride-svc`, §9.1, §11.16 (new) |
| P-08 | Cash on Delivery (COD) is a new payment terminal | `fares.ride_payments.state` gains `CashOnDelivery` and `CashOnDeliveryCollected`; settlement event `payment.cod_collected` posts driver earning identically to `CashSettled`; admin can flag uncollected COD after 24 h for follow-up | §6 `fare-svc`, §9.1, §11.16 |
| P-09 | Recipient must be reachable without exposing phone | FCM data-message to recipient (if registered) or SMS fallback with web-share token (if unregistered) — same masked-number policy as VoIP; `safety.trip_share_tokens` reused, scoped to package recipient role | §6 `notification-svc`, §6 `safety-svc` |
| P-10 | Proof-photo storage | New `rides.proof_artifacts(ride_id, kind ∈ {delivery_photo,signature}, storage_url, sha256, captured_at, captured_geo POINT)` in object storage (R2 / MinIO), SSE-KMS, 365-day retention, PDPA-erasable via `pdpa-svc` | §6 `ride-svc`, §9.1, §12.5 |
| P-11 | Package-delivery dispatch must respect vehicle capability | `dispatch.candidate_scores` adds `package_size_compatible BOOLEAN` derived from `vehicle_type × package_size` table; *driver may still reject* (autonomy); rejection counts toward reputation only if pattern emerges ("M+ package rejected by Motorbike" is downweighted, not penalised) | §6 `dispatch-svc`, §6 `reputation-svc` |
| P-12 | FCM location-request can be abused for tracking | Per-booker rate limit (5 requests / hour, 30 / day) in `notification-svc`; rider's `Decline` choice persisted to `safety.location_request_audit` (booker_id, rider_phone_hash, decision, ts) — repeated declines from same rider raise booker reputation flag | §6 `notification-svc`, §12.6 |
| P-13 | URD US-8.18 implies real-time round-trip to booker | Booker subscribes to `fanout-svc` group `booker:{bookerId}:loc-req:{requestId}` on issuing the FCM; rider's confirmation publishes through `ride-svc → outbox → fanout-svc`; expired/declined also pushed; no polling | §6 `fanout-svc`, §11.15 |
| P-14 | OnePay refund path for COD shortfall undefined | If COD not collected within 24 h and rider/recipient cannot be reached, ride moves to `Disputed` and falls into existing refund/dispute workflow (§11.14); no new pipeline | §11.16, §11.14 |
| P-15 | Capacity impact of FCM location-request fan-out | Modelled as bursty <0.05 msg/s blended at 100k passengers; reuses `notification-svc` FCM HTTP v1 batch; no new infrastructure | §16.1 |

### 1.6 Remediation Log (ADD v2.3 — Hardware GPS Tracker Promotion to Phase 1)

v2.3 promotes Epic 3 (Hardware GPS Tracker Support) from Phase 2 into Phase 1 and formalises a 100k-device telematics ingest plane. The earlier ADD acknowledged the *adapter pattern* but treated trackers as deferred and never sized for them. The fixes below close that gap and explicitly cover **dual personas**: the **individual ride-hailing driver** (one tracker, one vehicle, intermittent use) and the **fleet operator** (thousands of trackers, 24/7, bulk operations, organisational scoping).

| ID | Deficit | Resolution | Section |
|---|---|---|---|
| T-01 | TCP/UDP adapter mentioned only as Phase 2 | `tracker-adapter-svc` promoted to Phase 1; one StatefulSet *per protocol family* (GT06, JT/T 808, H02, generic-NMEA), normalising into MQTT | §6, §7.7, §10 |
| T-02 | No device provisioning service | `provisioning-svc` promoted to Phase 1; issues per-device X.509 (MQTT-capable) or signed pre-shared secret (legacy TCP) with 90-day rotation via step-ca | §6, §12 |
| T-03 | IMEI → vehicleId resolution unspecified | Redis cache `imei:{imei} → vehicleId` (LFU, 24 h TTL), Postgres `prov.tracker_bindings` as source of truth; cache invalidated by `tracker.bound` / `tracker.unbound` events | §6, §9.1 |
| T-04 | Tracker LWT not wired | EMQX LWT on `veh/{vehicleId}/status=offline` consumed by `trip-state-svc`, `dispatch-svc`, fleet-health aggregator; TCP adapter emulates LWT by detecting socket-half-close | §7.7, §11 |
| T-05 | Replay storm unbounded | Trackers replay backlog on `veh/{vehicleId}/pos/replay` topic only; bridge consumer group is rate-limited (20 samples/s/device); monotonic `seq` deduplication | §7.5.3, §7.7 |
| T-06 | High-frequency telematics not sized for Postgres | **TimescaleDB hypertable** `telemetry.positions` partitioned by `vehicleId` hash + time (1 day chunks); 30 d hot retention; **continuous aggregates** for 1-min / 5-min / 1-hour rollups; **compression policy** after 7 days (~10× ratio) | §9.5 (new) |
| T-07 | Plausibility checks not extended to hardware | `position-processor-svc` applies same per-vehicle-type max-speed and accuracy thresholds; hardware samples additionally checked for monotonic GNSS UTC timestamp and minimum satellite count | §12.6, §7.7 |
| T-08 | No anti-cloning on IMEI | Two devices presenting the same IMEI within a 24 h window quarantines both; manual admin resolution required | §12, §6 `provisioning-svc` |
| T-09 | Fleet bulk-provisioning absent | `registry-svc` exposes `POST /fleets/{id}/trackers/bulk` accepting CSV; validates in a SAGA, materialises bindings + queues credential mint jobs | §6, §11.17 (new) |
| T-10 | Capacity model omits hardware | §16 sizing updated: +100k trackers @ 0.2 Hz blended = +20k msg/s sustained on ingest plane; +1 EMQX node, +2 `position-processor` pods, +1 Timescale chunked tablespace | §16 |
| T-11 | Mode A bus (state-owned) vs Mode C individual cab not differentiated in eligibility rules | Dispatch eligibility uses tracker-online (≤30 s) **and** driver-app-online for Mode C; Mode A position is broadcast irrespective of driver-app state | §6 `trip-state-svc` / `dispatch-svc`, §11 |
| T-12 | Tracker credential revocation latency | EMQX dynamic ACL backed by `provisioning-svc` Redis lookup with sub-second pub/sub invalidation; TCP adapter consults same cache on every authenticate | §12.1 |

### 1.7 Remediation Log (ADD v2.4 — Directional Travel / Destination Filter)

v2.4 adds **Directional Travel** (Destination Filter) for Mode C standby drivers (URD Epic 6A, US-6A.17–US-6A.23), modelled on PickMe's driver "Directional Travel" feature: a driver may declare a destination so that, for a limited period and a limited number of times per day, only hires heading in that direction are offered to them. This is a **candidate-filter** capability layered onto the existing v2.1 dispatch pipeline; it does **not** touch the atomic-accept (R-02), cancellation matrix (R-03), idempotency (R-14), fare, or daily-fee invariants. `ride-svc` (the ride aggregate) is unchanged — directional matching lives entirely in `dispatch-svc` as an additional candidate predicate.

| ID | Deficit | Resolution | Section |
|---|---|---|---|
| DT-01 | No way for a driver to bias dispatch toward a direction | New driver-scoped **Directional Travel filter** owned by `dispatch-svc`: `POST /standby/directional` sets `{destination_geo, expires_at}`; filter stored in Redis `driver:directional:{driverId}` (hash, PX = remaining TTL) + Postgres `dispatch.directional_filters` for audit/limits | §6 `dispatch-svc`, §9.1, Appendix C |
| DT-02 | Direction-match algorithm unspecified / not auditable | Candidate predicate evaluated during candidate generation (§11.11): driver included **only if** `angularDiff(bearing(driver→destination), bearing(pickup→dropoff)) ≤ θ_max` (default 45°) **AND** `dist(pickup, driver) ≤ detour_max` (default 2 km) **AND** `dist(dropoff, destination) < dist(pickup, destination) − progress_min` (default 250 m). Decision + computed metrics persisted in `dispatch.candidate_scores.breakdown.directional` for post-hoc audit (extends R-11) | §7.4, §11.11 |
| DT-03 | Daily-use limit & max duration not enforceable | `dispatch.directional_filters` keyed `(driver_id, used_date)` with `use_count`; admin-configurable `max_uses_per_day` (default 2) and `max_duration` (default 2 h). Activation increments `use_count` atomically (Redis `INCR` + Postgres upsert); manual turn-off **still consumes** a use (US-6A.19) to prevent gaming. Use_date is `Asia/Colombo` per D-38 | §6, §9.1 |
| DT-04 | Filter must auto-clear and not strand the driver | Durable expiry via Quartz.NET (`dispatch.timers.kind='directional_expiry'`) as the source of truth; Redis key TTL is a fast hint only. On fire (or manual `DELETE /standby/directional`, or going offline / EMQX LWT `status=offline`) the filter is removed and `directional.cleared` emitted → driver returns to the full eligible pool. Mirrors the R-04 durable-backstop pattern | §6, §11.12 (LWT path), §14 |
| DT-05 | Interaction with eligibility/safety rules ambiguous | Directional predicate runs **after** all hard gates (wallet/daily-fee, Driver Level, vehicle-category, `reputation-svc.block_status`, package-size compatibility P-11) and **never** relaxes them — it can only *remove* otherwise-eligible candidates, never add ineligible ones. No reputation/acceptance-rate effect when no directional hires arrive (US-6A.23) | §6 `dispatch-svc`, §11.11 |
| DT-06 | Empty-pool risk: directional filters could starve a ride of drivers | Directional filters apply per-driver, so a ride simply skips filtered-out drivers. If a directional driver is the *only* nearby candidate, the ride proceeds to the next ring / `ExpiredNoDriver` exactly as today — directional state never blocks a passenger's ride from matching some *other* available driver | §11.11, §11.12 |
| DT-07 | Applies to packages too (US-6A.22) | Predicate is `kind`-agnostic (`passenger`/`proxy`/`package`); evaluated identically using the ride's pickup→dropoff vector. Composes with P-11 package-size compatibility | §6 `dispatch-svc`, Appendix B.2 |
| DT-08 | Driver app needs live filter state & expiry warning | `GET /standby/directional` returns active filter + remaining uses; `directional.cleared` / pre-expiry reminder (10 min) delivered via `notification-svc` (US-10.14). Cadence unaffected — directional drivers remain normal candidates for *matching* hires (§7.5 phase table unchanged) | §6 `notification-svc`, §6 `dispatch-svc` |

### 1.8 Remediation Log (ADD v2.6 — Alignment with URD v2.2)

v2.6 reconciles the ADD with **URD v2.2** (which resolved conflict register `conflicting-requirements.md`). These are **authoritative** decisions: where a lower-level artifact elsewhere in this document (component rows, schema names, Appendix-C endpoint lists, sequence diagrams) still reflects a pre-v2.6 assumption, it inherits the resolution below and the explicit rename mapping in the relevant AL row. The v2.1–v2.4 ride/dispatch/telematics/directional invariants are unchanged.

| ID | Pre-v2.6 assumption (now corrected) | Resolution (URD v2.2) | Section / mapping |
|---|---|---|---|
| AL-01 | "Reseller" modelled as a separate persona with its own portal, account, and wallet `owner_type='reseller'` | **"Reseller" is not a role, account, or enabled capability.** It is simply any **driver who has purchased bulk credit** (at the bulk-voucher purchase discount) and transfers it to other drivers **in the Driver App** by **Driver ID**; there is **no reseller portal/login/account and no per-transfer commission**. Schema: drop `owner_type='reseller'` (drivers use their normal `driver` wallet); replace `billing.reseller_*` with **`billing.voucher_discount_tiers`** (commission/discount % **per voucher value**, admin-configurable), **`billing.voucher_purchases`**, and **`billing.credit_transfers`** (driver↔driver, **exact value, no commission**). Credit-transfer endpoints (Appendix C `/subscriptions/credit-transfer/*`, `/wallet/credit-transfer/*`) are **Driver-App APIs**, not portal APIs. Admin sets the **bulk-voucher commission % per voucher value** in the Admin Portal Config (the reseller's margin; there is **no per-driver commission rate**) | §5, §6 `wallet-svc`/`subscription-svc`, §9.1 `billing`, §11.6, Appendix C |
| AL-02 | Back-office split across a **"Wallet & Subscription (Admin) Portal"** (`wallet-portal`, Next.js, `wallet.MageRide.lk`) **and** a separate `admin-bff`/Admin Web | **Single consolidated Admin Portal at `admin.mageride.lk`** = the one back-office for all six internal roles, performing **all** back-office functions (wallet/finance reconciliation, support, onboarding/verification, moderation, tariff/config, RBAC, audit, reporting). `wallet-portal` is **removed**; its functions move into the Admin Portal (BFF = `admin-bff`). `wallet.MageRide.lk` → `admin.mageride.lk` everywhere | §5, §6 (`wallet-portal` dropped), §13, §18 |
| AL-03 | Fleet Operator features (Epic 13) + `fleet-svc` **deferred to Phase 2** | **Promoted to Phase 1.** New **Fleet Portal** at **`fleet.mageride.lk`** (responsive web) backed by `fleet-svc` (Phase 1). A fleet operates **Mode A (free)** and/or **Mode B (monthly fee per vehicle)** only — **Mode C is not a fleet option** (Mode C daily fees are always paid from the individual driver's wallet). A **fleet organisation must be verified/approved by a Verification Officer** before it can onboard vehicles/assign drivers (Pending→Approved gate). **Fleet sub-roles** Owner/Manager/Viewer are **org-scoped** (provisioned by the Fleet Owner, not Super Admin) | §5, §6 `fleet-svc`, §10, §19 (Phase plan) |
| AL-04 | n/a (no passenger web surface) | **Passenger Web subview** at **`passenger.mageride.lk`** — a **no-login, single-ride, tokenised web view of the Passenger App** opened via the SMS link sent when a driver accepts a **proxy** ride (URD US-8.22). **Not a separate auth surface**; reuses `safety.trip_share_tokens` (P-09) for ride-scoped, expiring access (NFR-52). Provides live tracking, ETA, driver/vehicle details, masked VoIP, SOS, summary | §6 `safety-svc`/`fanout-svc`, §12 |
| AL-05 | Wallet top-ups include **Bank Transfer** (IPG-webhook auto-reconcile + manual admin queue) | **Bank-transfer top-ups removed.** Wallet top-up methods = **OnePay card, OnePay wallet, LankaQR** only (all instant in-app). `wallet-svc` retains **OnePay/LankaQR gateway settlement reconciliation** (Commercial Bank IPG webhook) but **no** bank-transfer receipt flow / manual reconciliation queue. §11.5 "Bank Transfer path" removed | §6 `wallet-svc`, §11.5, §18 |
| AL-06 | JWT `role ∈ {passenger, driver, reseller, admin}` | **Nine canonical roles**: `driver, passenger, fleet_owner, admin, super_admin, verification_officer, support_csr, finance_officer, auditor` (+ org-scoped fleet sub-roles `owner/manager/viewer` as a `fleet_role` claim). **Deny-by-default RBAC** enforced server-side on every privileged endpoint (URD §2.3 matrix); internal roles (4–9) ~~require **MFA**~~ **no longer require MFA (removed per AL-37, 2026-06-28)** and are provisioned only by Super Admin. `iam` roles/permission tables updated | §6 `iam-svc`/`admin-bff`, §9.1 `iam`, §12.1 |
| AL-07 | "Wallet Portal: Phone OTP + Password + Google Sign-In"; Google on a portal shared with apps | **Auth scoping:** Passenger App + Driver App = **Phone OTP only** (no Google on apps). **Admin Portal** (`admin.mageride.lk`) = **Password or Google Sign-In** (**MFA/TOTP step removed per AL-37, 2026-06-28** — protected by failed-attempt lock-out + optional IP allow-list instead). **Fleet Portal** (`fleet.mageride.lk`) = **Email+Password / Google / Apple**. All resolve to one unified account model | §13, §18 (Auth row) |
| AL-08 | "new-device login revokes prior" implied **per account** | **Single active device is per app.** A new-device login revokes only **that app's** prior device session; the **same person may run the Driver App and Passenger App simultaneously**. Token revocation (Redis `refresh:{jti}` + Postgres `iam.sessions`) is scoped by `(user_id, app)` | §6 `iam-svc`, §12.1 (US-1.12; US-1.11 merged) |
| AL-09 | Vehicle types informal ("car", "Tuk"); fare tariff used "Car" | **Canonical vehicle-type enumeration** (URD §1.B): `Motorbike, Three-wheeler, Flex, Sedan, Mini Van, Van` (passenger ride) + `Truck, Mini Truck` (package delivery) + `Bus, Train` (Mode A). **"Car" → "Sedan".** Applied to `registry.vehicles.vehicle_type` CHECK, fare-tariff, daily-fee, map markers, and dispatch `vehicle_category`; P-11 package-size×vehicle_type table extends to Truck/Mini Truck | §6, §9.1 `registry`, §18 |
| AL-10 | Insurance not modelled as a mandatory onboarding document | **Vehicle insurance certificate mandatory for all modes (A/B/C).** `registry.documents` gains `kind='insurance'` with `expires_at`; AI-extract policy/insurer/expiry (`ocr-svc`); registration cannot be `APPROVED` without it; expiry reuses E-03 `document.expiring/expired` → auto-suspend. Admin-registered trains exempt (line-level cover). **Likewise the vehicle revenue licence is a mandatory onboarding document** — `registry.documents` gains `kind='revenue_license'` with `expires_at`, AI-extracted expiry, required for `APPROVED`, expiry → auto-suspend (E-03); all document statuses (license · vehicle reg · insurance · revenue licence · profile) are listed on the registration hub and the "Under review" screen | §6 `registry-svc`/`ocr-svc`, §9.1, §11 onboarding |
| AL-11 | `voip-svc` / in-app VoIP treated as P1 | **VoIP promoted to P0** (Phase 1) — required by proxy booking (P-05) and the Passenger Web subview (AL-04) masked-contact flows | §6 `voip-svc`, §19 |
| AL-12 | GPS cadence omitted the explicit near-pickup burst | **Near-pickup/near-drop 1 s burst** is part of the Mode C cadence table (R-07): 1 call/s within an admin-configurable radius (default 150 m) of pickup/drop-off; **Mode C idle-standby = 60 s**; bounded by the 5 msg/s/vehicle broker ceiling (§12.4). (URD US-6A.24, NFR-30) | §7.5, §12.4 |
| AL-13 | Driver emergency contact not stored | Driver profile stores an **emergency contact** (name + phone); consumed by `safety-svc` SOS fan-out (URD US-12.9) | §6 `iam-svc`/`safety-svc`, §9.1 `iam` |
| AL-14 | Passenger saved addresses / default payment not modelled; login payload unspecified | Passenger profile stores **Home/Work + labelled saved addresses** (OSM-pin + reverse-geocode) and a **default payment method** (Cash default / LankaQR / OnePay). **Eager-fetch** login payload = profile + saved addresses + payment-method metadata + active trip + (driver) shift/earnings + config; **lazy-fetch** per screen for history/earnings/receipts (URD US-1.15/1.16, Epic 22) | §6 `iam-svc`/`fare-svc`, §18.2 |
| AL-15 | LankaQR shown as a scannable QR | Passenger LankaQR uses a **"Pay" button deep link** that opens the bank app pre-filled; scannable QR is **fallback only** when no compatible app is installed (URD US-8.10a) | §6 `fare-svc`, §11.8 |
| AL-16 | Cancellation disable rule ambiguous | **Three consecutive _post-acceptance_ cancellations disable booking**; counter **resets on any completed ride**; pre-acceptance cancellations never count. The Rs 50 penalty becomes the passenger's outstanding balance, added to the next ride; the next ride's driver is debited Rs 50 → credited to the originally-affected driver (URD US-6A.9/6A.10/6A.10b) | §6 `reputation-svc`/`fare-svc`, §11.12 |

---

### 1.9 Remediation Log (ADD v2.7 — Discussion 2026-06-21, 17 changes)

v2.7 absorbs the 17 change requests from the 2026-06-21 design discussion (URD v2.3, Epic 23 + updated stories). These are **authoritative**; lower-level artefacts inherit the resolution below. The v2.1–v2.6 invariants are unchanged.

| ID | Change request | Architecture resolution | Section / mapping |
|---|---|---|---|
| AL-17 | Passenger location search allowed typing a **route number** | **Destination is a geo-location only.** Search returns **geocoded places + saved/recent** (no route rows). After a place is chosen, **`query-svc` / a new `transit-svc`** computes reachable **public-transport options from the GTFS feed** and private tiers for the booking screen (URD US-8.2a/8.2b). | §6 `query-svc`/`transit-svc`, Appendix C |
| AL-18 | Public-transport routing ad-hoc; no GTFS source | **GTFS feed is the source of truth for Mode A routing.** An admin-managed **GTFS dataset** (routes, trips, stops, stop_times, shapes) is imported into PostGIS; **`transit-svc`** exposes `GET /transit/options?from&to` returning **all direct routes** (route_id, short_name, headsign/description, shape) plus transit (≥1 transfer) options, each tagged Direct/Transit + PUBLIC. Future GTFS-RT for live ETAs (Phase 3). | §6 (new `transit-svc`), §9.1 (`transit` schema), Appendix C |
| AL-19 | Mode C tiers showed ETA/distance pre-match | **Mode C tiers expose price only** in the pre-booking results (no driver matched ⇒ no minutes-away/distance). `fare-svc` returns an upfront price per tier; ETA/distance are populated only **after** dispatch/accept (URD US-8.2c). | §6 `fare-svc`, §11.11 |
| AL-20 | Location pin only via search/map/FCM-request | **Paste-link input** added: client parses **lat/lng from a Google Maps URL** (handles `?q=`, `@lat,lng`, `/place/…@`, and short `maps.app.goo.gl` links via an HTTPS resolve). Pure client-side parse for full URLs; short links resolved through a small **`link-resolver`** edge function (follows redirect, extracts coords, no Google API). Applies to proxy pickup and package pickup **and drop-off** (URD US-8.2d, US-20.2); the paste interaction is surfaced in the **paste sheet SCR-PA/PI-012a** (Empty → Parsing → Resolved pin-preview + reverse-geocoded address → Error/pick-on-map). | §6 (`link-resolver` edge fn), §11.15/11.16 |
| AL-21 | Package booking captured **pickup only**; recipient only notified by FCM | **Package drop-off is captured** (Search/Map/Paste-link/Request). On **driver pickup-confirm**, `notify-svc` branches: **registered recipient → FCM** deep-link to recipient tracking; **unregistered recipient → SMS** with a **`safety.trip_share_tokens`** web link (`passenger.mageride.lk/track?token=…`) to the no-login tracking page (reuses AL-04 / P-09) (URD US-20.5). | §6 `notify-svc`/`safety-svc`, §11.16 |
| AL-22 | Pay-fare rendered a MageRide QR centre-screen | **Passenger scans the driver's QR** (printed/on-screen/sticker) to pay; the app no longer renders a QR in the centre — it offers a **camera scan** + LankaQR deep link (extends AL-15) (URD US-8.10b). | §6 `fare-svc`, §11.8 |
| AL-23 | Mode B marker opened the A-style popup; sharing/requests were account-global | **Mode B marker → access-request screen** (Vehicle ID pre-filled). **Sharing & subscription requests are scoped per vehicle**: `subscription.access_requests` and `subscription.grants` carry `vehicle_id`; the Driver App (SCR-DA-028) and Fleet Portal (SCR-FP-011) filter by vehicle. Supports multi-vehicle drivers and **temporarily-assigned** (fleet-hired) Mode A/B vehicles (URD US-4.9/4.10). | §6 `subscription-svc`/`fleet-svc`, §9.1 `subscription`, §11.10 |
| AL-24 | No Mode B fare/payment model (only Rs 300 platform fee) | New **Mode B subscription payments** (Epic 23). Each Mode B vehicle is **Paid/Free** (`registry.vehicles.mode_b_billing`). Paid vehicles collect a **per-subscriber monthly fare** (`subscription.subscriptions.monthly_fare_minor`, overridable per subscriber) on a **1st-of-month or join-anniversary cycle**. Passengers pay via **LankaQR deep-link / LankaQR scan / OnePay / online transfer (slip upload)**; cash is **owner-marked-received** in the portal. Payments route **to the fleet owner** (pass-through, not platform revenue) via **`subscription-pay-svc`** (or `fleet-svc` payments module) and appear per-subscriber/per-vehicle (`subscription.payments`); full history both sides (URD US-23.x). | §6 (`subscription-svc` payments module), §9.1 `subscription`, Appendix C |
| AL-25 | Unsubscribe semantics undefined | **Unsubscribe** sets `grant.status='unsubscribed'` + revocation push (reuses D-22, §11.10); the passenger **loses visibility** and must **re-request → accept** to rejoin. The fleet portal keeps the row **muted until the owner deletes** it (hard-delete) (URD US-4.11/4.12). | §6 `subscription-svc`, §11.10 |
| AL-26 | Saved addresses stored as a single string; language editable in 3 places | **Saved address = Address Line 1/2/3 + free-text Label** captured in a ModalBottomSheet (`iam.saved_addresses` columns). **Language selection removed from Edit-profile** (kept in onboarding + Settings); onboarding presents language as **vertical boxes, Sinhala-first**. Type-filter chips carry a per-type colour token. (URD US-22.2, US-1.3/1.5) | §6 `iam-svc`, §9.1 `iam`, §18.2 |
| AL-27 | Driver onboarding was a single checklist gated behind Verification-Officer approval; launch cities hard-coded in the app | **Driver onboarding split into two phases (Change 6/22):** (1) **driver-identity Profile Setup** (name + **required** photo + driving license front/back → `registry.driver_profiles` + vehicle-less `registry.documents`) that **precedes Home with no vehicle**; (2) **optional, in-app, Mode-C-only 4-step vehicle onboarding** (type+reg, insurance, revenue licence, front/back photos) **auto-verified by Gemini Flash 3.0** and **auto-approved** when all four extract — **insurance(expiry) · revenue(no+expiry) · photos(plate==reg) · vehicle-details(entered)** — with the **Verification Officer queue used only for Pending** docs. **Mode A/B vehicles + permits move to the Fleet Portal.** Go-Online is **gated until a vehicle is available** (owned Mode C APPROVED or shared/assigned Mode A/B). **Launch cities** move out of the app into **`config.operating_cities`** (admin-managed, `GET /config/cities`, `iam.users.operating_city_code`). (URD US-1.3a, US-2.21–2.25) | §6 `registry-svc`/`ocr-svc`/`content-svc`, §9.1 `config`/`registry`, §12.5 |

---

### 1.10 Remediation Log (ADD v2.8 — Discussion 2026-06-25 driver change pass, 13 changes)

v2.8 absorbs the 13 driver-app change requests from the 2026-06-25 discussion (URD v2.4: US-1.2a, US-2.4a/2.10a/2.26/2.27, US-5.11/5.12, US-7.18, US-9.10, US-18.2, US-20.12/20.13). These are **authoritative**; lower-level artefacts (D2 UI spec, D3 API, D4 data model, D5 business logic, D6 integration) inherit the resolution below. All earlier invariants are unchanged.

| ID | Change request | Architecture resolution | Section / mapping |
|---|---|---|---|
| AL-28 | Driver onboarding lacked a feature intro | **Feature-infographic carousel** added to the driver language/city screen (SCR-DA-002 / SCR-DI-002): 3 client-paged slides (strings/illustrations served by `content-svc`, localised Si/Ta/En), mirroring passenger SCR-PA-002. Pure presentation — no new API. (URD US-1.2a) | §6 client / `content-svc` |
| AL-29 | Licence extraction captured only licence no + expiry; manually-typed fields trusted silently | **`ocr-svc` driving-licence extraction expanded** to also return **`nic_no`** and **`allowed_vehicle_types`** (licence classes), displayed with licence no + expiry. `registry.driver_profiles` / `registry.documents(kind='driving_license')` gain `nic_no`, `allowed_vehicle_types[]`, and **per-field `source ∈ {ai,manual}` + `verify_status ∈ {auto_verified, pending, confirmed}`**. If the scan is unclear the driver types the value (`source='manual'`); **any `manual` or low-confidence field is set `verify_status='pending'`** and routed to the **Verification-Officer queue** (SCR-AP-003) for **Confirm / Edit & confirm**. (URD US-2.4a/2.10a) | §6 `ocr-svc`/`registry-svc`/`admin-bff`, §9.1 `registry`, §12.5 |
| AL-30 | Onboarding was monolithic (submit-all); no per-step persistence, no "doubtful→pending", no incomplete-vehicle state | **Mode-C onboarding becomes a persisted per-step state machine** — `registry.onboarding_steps(vehicle_id, step ∈ {details,insurance,revenue,photos}, status ∈ {pending_input, verified, pending_review}, fields JSONB, saved_at)`. A step is **`pending_review`** when any field is **doubtful (low confidence), driver-entered (manual), or — on the photos step — plate OCR ≠ entered reg_no**; otherwise **`verified`**. **Each step is saved individually**; re-opening the wizard **resumes at the first non-`verified` step** (not Step 1). `registry.vehicles.onboarding_status ∈ {incomplete, approved}` is **derived** (`incomplete` while any step ≠ verified/confirmed; `approved` when all four are). **Only `approved` Mode-C vehicles are go-live eligible** (extends US-9.6). When the active onboarding vehicle is `approved`, the wizard entry point (nav-drawer Vehicle Onboarding / My-Vehicles ＋) **creates a NEW vehicle at Step 1/4**. My Vehicles renders **Incomplete / Approved** per vehicle. (URD US-2.10a/2.26/2.27) | §6 `registry-svc`, §9.1 `registry`, §11 onboarding, Appendix B |
| AL-31 | Driver home map fanned in nearby/other active vehicles | **Driver home map is scoped to the driver's OWN active vehicle only** — `fanout-svc`/client subscribe to the single own-vehicle topic; other drivers' active vehicles are **never rendered on the driver home map** (complements US-7.16 public-map hiding of engaged Mode C). The **driver dashboard top-left hamburger is removed**; navigation is via the bottom **Menu** tab (SCR-DA-036 / SCR-DI-036). (URD US-7.18) | §6 `fanout-svc`, client, §7 |
| AL-32 | Mode A/B drivers shared the Mode-C standby map as Home; tracker vs dashboard journey-control unstated | **Mode A/B Start/End-Journey is the driver home dashboard.** When the active vehicle is Mode A (bus) or Mode B (private), the client routes Home → `trip-state-svc` Start/End-Journey dashboard (SCR-DA-011 / SCR-DI-011) instead of SCR-DA-010 — **only Start Journey / End Journey actions**, vehicle type + reg shown **below the route card**. For tracker-equipped Mode A/B vehicles, `trip-state-svc` **auto-starts/ends the session on vehicle ignition** (ACC on/off, US-3.22/3.23) with the **device as single active publisher** (US-3.6); the **dashboard can manually override** (a driver Start/End writes the session transition regardless of device state). (URD US-5.11/5.12) | §6 `trip-state-svc`, §7.6, §11 |
| AL-33 | Package delivery was a single confirm sheet; COD coupled to completion | **Driver package-delivery flow = three sequential bottom sheets** (SCR-DA/DI-016a/b/c) over the existing `ride-svc` package state machine: **(1) Review** — pickup & drop distances, payment method, **sender & recipient phone numbers each with a mobile-voice-call button**; **Start** (→ proceed) vs **Cancel** (releases the offer back to dispatch → **next eligible driver**, R-02/O2); **(2) Pickup** — Call sender, SOS, **pickup_otp** verify (`package.picked_up` → DriverArrived→InProgress, P-07); **(3) Complete** — **delivery_otp** / proof photo, sender+recipient call buttons, and a **"Delivery completed"** action (→ `Completed`) that **replaces the "Cash received (COD)" button**. COD/cash collection is **decoupled** from completion and reconciled separately (uncollected 24 h → Disputed, P-14). **These call buttons place a direct PSTN dial** (distinct from masked VoIP used for passenger rides, P-05). (URD US-20.12/20.13) | §6 `ride-svc`/`notify-svc`, §11.16, Appendix B.2 |
| AL-34 | Driver credit-request offered a QR-scan path | **QR scanning removed from the driver credit-request flow** — `POST /wallet/credit-transfer/request` is **Driver-ID-only** (SCR-DA-023 / SCR-DI-023 drop the VisionKit/ML-Kit scan tile). Unrelated to AL-22 (the **driver's own pay-QR shown to passengers** is retained). (URD US-9.10) | §6 `wallet-svc`, client, Appendix C |
| AL-35 | Mode B sharing caption + inline rate-passenger were UI clutter | **Client/UX only:** SCR-DA-028 / SCR-DI-028 **remove the "Showing sharing for … temporarily assigned by …" caption box** (the selected chip conveys context) and render the per-vehicle selector **full device width**; SCR-DA-030 / SCR-DI-030 move **rate-passenger from an inline card into a modal bottom sheet** (`reputation-svc` rating endpoint unchanged). (URD items 12, 13 / US-18.2) | client, §6 `subscription-svc`/`reputation-svc` |

---

### 1.11 Remediation Log (ADD v2.9 — Discussion 2026-06-28, 11 changes)

v2.9 absorbs the 11 change requests from the 2026-06-28 review (URD v2.5: Epic 24, US-24.1…US-24.11). These are **authoritative**; lower-level artefacts (D2 UI spec, D3 API, D4 data model, D5 business logic, D6 integration) inherit the resolution below. All earlier invariants are unchanged.

| ID | Change request | Architecture resolution | Section / mapping |
|---|---|---|---|
| AL-36 | Onboarding "Get Started" CTA floated mid-screen; schedule-ride had no destination | **Client/UX:** passenger onboarding (SCR-PA-002 / SCR-PI-002) **pins Get Started to the bottom** below the language list. **Schedule-ride (SCR-PA-013 / SCR-PI-013) now requires a destination** — the same place-search/map-pick picker (`query-svc`/`transit-svc`, AL-17) supplies `dest_lat/lng`; pickup defaults to current GPS but is editable. `scheduling.scheduled_rides` already carries origin/destination — the API now **rejects a schedule create without a destination** (`dest_*` NOT NULL at the service boundary). (URD US-24.1/24.2) | client, §6 `scheduling-svc`/`query-svc`, §9.1 `scheduling` |
| AL-37 | Admin Portal login forced an MFA/TOTP second factor | **MFA/OTP step removed from Admin Portal sign-in.** `iam-svc` internal-role login completes on **password or Google only**; the TOTP challenge + `iam.user_mfa` enrolment are dropped from the login flow. Compensating controls retained: **failed-attempt lock-out**, **session binding**, and an **optional IP allow-list** for internal roles. Supersedes the "MFA for internal roles" clause in AL-06/AL-07. (URD US-24.5) | §6 `iam-svc`, §12.1, §18 (Auth row) |
| AL-38 | Admin dashboard KPIs were a fixed snapshot | **Admin dashboard gains a statistics period filter** — `GET /admin/dashboard/stats?period={today,week,month,custom}&from&to` (Asia/Colombo) served from the **analytics read-model** (materialised rollups in `analytics.daily_metrics` / on-the-fly aggregation). Returns period KPIs (completed trips, gross fare, new riders/drivers, daily-fee revenue) **with vs-previous-period deltas**; live cards (online drivers, pending verifications, open tickets) stay real-time. CSV export of the filtered figures. (URD US-24.7) | §6 `admin-bff`/`analytics-svc`, §9.1 `analytics`, Appendix C |
| AL-39 | Verification was one combined screen | **Verification split into list + detail + viewer.** `admin-bff` exposes **three queue feeds** — `GET /admin/verification/queues/{driving_license,vehicle_registration,fleet_org}` — and a **detail** `GET /admin/verification/{driverId|vehicleId|orgId}` returning the entry's flagged fields **and a list of attached documents** (kind + signed thumbnail/full URL). The client renders SCR-AP-003 (queues), SCR-AP-003a/003c (detail) and **SCR-AP-003b — a full-size document viewer** opened by tapping a thumbnail (zoom/rotate/page). Confirm/Edit/Approve/Reject endpoints unchanged; **document reads emit a `DOC_VIEW` audit event** and use short-lived signed object-storage URLs. (URD US-24.8) | §6 `admin-bff`/`registry-svc`/object storage, §9.1 `registry`/`audit`, §12 |
| AL-40 | No admin way to look up a passenger + their activity | **Passenger directory.** `GET /admin/passengers?q=&name=&mobile=&id=&email=` (multi-criteria) → list; `GET /admin/passengers/{id}` → profile + tabbed **trips / payments / packages / disputes** (joins `ride-svc`, `payment-svc`, `support-svc` read-models). PII fields role-gated; **every lookup logs a `PII_READ` audit event**. Read-only; refunds still route to Finance. (URD US-24.9) | §6 `admin-bff`/`ride-svc`/`payment-svc`, §9.1 `audit`, Appendix C |
| AL-41 | No consolidated admin view of a verified driver + transactions | **Driver directory.** `GET /admin/drivers?name=&mobile=&id=&nic=&reg_no=&level=&status=` (multi-option; defaults to verified) → list; `GET /admin/drivers/{id}` → profile/wallet/level + linked vehicles + tabbed **trips / wallet ledger / daily fee / credit transfers / reports** (joins `wallet-svc`, `ride-svc`, `reputation-svc`). Reversals remain Finance-only (§AP-006). All views audited. (URD US-24.10) | §6 `admin-bff`/`wallet-svc`/`ride-svc`, §9.1 `wallet`/`audit`, Appendix C |
| AL-42 | No admin search of a registered vehicle + its transactions | **Vehicle directory.** `GET /admin/vehicles?reg_no=&id=&type=&mode=&owner_mobile=&fleet_org=&status=` → list; `GET /admin/vehicles/{id}` → registration/insurance/revenue-licence/tracker info + document thumbnails (→ AL-39 viewer) + tabbed **trips / earnings / daily fee / reports** (joins `registry-svc`, `ride-svc`, `wallet-svc`). Mode A/B also show the owning fleet org. (URD US-24.11) | §6 `admin-bff`/`registry-svc`, §9.1 `registry`/`audit`, Appendix C |
| AL-43 | Driver doc capture was a plain photo/upload → weak OCR | **Camera document-scanner with draggable-corner crop (SCR-DA-005 / SCR-DI-005)** is the shared capture surface for every onboarding image (licence front/back, insurance, revenue licence, vehicle front/back). Client uses **CameraX `ImageCapture` (Android)** / **VisionKit `VNDocumentCameraViewController` (iOS)** with a four-corner adjustable quad; **perspective-correct + de-skew on confirm** before upload to object storage and `ocr-svc`/Gemini Flash. Higher capture quality ⇒ higher extraction confidence ⇒ fewer `pending_review` flags (complements AL-29/AL-30). No new server endpoint — same upload contract. (URD US-24.6) | client, §6 `ocr-svc`/`registry-svc`, §12.5 |

### 1.12 Remediation Log (ADD v3.0 — Discussion 2026-07-05, 8 changes)

v3.0 absorbs the 8 change requests from the 2026-07-05 spec-vs-wireframe audit (URD v2.6: Epic 25, US-25.1…US-25.8). The `passenger.mageride.lk` subview (AL-04) was wireframed as six pages but had no screen IDs, no public API contracts, and one flow contradiction — this pass makes it buildable without adding a fifth surface. These are **authoritative**; D1–D6 inherit the resolutions below.

| ID | Change request | Architecture resolution | Section / mapping |
|---|---|---|---|
| AL-44 | `passenger.mageride.lk` existed only as AL-04/P-09 one-liners — six wireframed pages had no screen IDs, no serving API, no live feed, no receipt, no web call/SOS contracts | **Web subview formalized.** Screen IDs **SCR-WT-001…006** (landing/token gate · package track · confirm pickup · ride track · delivered/receipt · expired). A stateless **`public-bff`** serves `GET /public/track/{token}` (scope-shaped snapshot), `/live` (SSE over the same SignalR ride/geocell channels; long-poll fallback), `/call` (leases a **ride-scoped masked proxy DID**, browser `tel:` dial — supersedes US-11.9's "masked-VoIP" wording; no WebRTC on web), `/sos` (dual-gateway SMS to the **booker**, D-33, `safety.sos_events.source='web'`) and `/receipt` (terminal states; outcome = otp_verified / photo_proof / cod_collected / disputed, P-10/P-14). **Token = credential**: per-token + per-IP rate limits, access metering, zero ride data on dead tokens. `trip_share_tokens` gains scopes **`proxy_rider`**, **`pickup_confirm`** (+ metering columns); `comms.call_log` / `safety.sos_events` accept a `share_token` actor. (URD US-25.1/25.2/25.4/25.5/25.6/25.7) | §6 `public-bff` (new, thin)/`notification-svc`/`safety-svc`, §9 `safety.trip_share_tokens`, §12.7 |
| AL-45 | Contradiction: web wireframe showed an **unregistered proxy rider confirming pickup in the browser**, while D1/D5 (US-8.19) said unregistered ⇒ booker map-pin/search only; D3's confirm endpoint was Bearer-only | **Resolved in favour of the web flow** (wireframe + Walkthrough Scenario 54 already assumed it). On `RiderNotRegistered` (P-03), `notification-svc` mints a **`pickup_confirm` token (TTL 300 s, burned on use)** and SMSes the link; **SCR-WT-003** feeds the **same `rides.location_requests` state machine** via `POST /public/track/{token}/pickup/confirm|decline`. **Decline transmits no coordinates (P-02).** US-8.19's booker fallback is retained for decline/expiry — no longer the only path. (URD US-25.3) | §6 `ride-svc`/`notification-svc`, §11.15 |
| AL-46 | Spec hygiene: SCR-DA/DI-012 still tagged **[NEW]** though embedded in the dashboard (risk of a redundant screen build); stale wireframe annotations (US-8.7a vs US-24.4; `GET /rides/active` vs the per-role D3 paths); URD header never bumped for the 2026-06-28 set | **SCR-DA/DI-012 re-tagged [MERGED → SCR-DA/DI-010]** in D2 (state, not screen). Wireframe annotations corrected in `passenger_android.html`/`passenger_ios.html`; `web_passenger.html` captions carry the new SCR-WT IDs. URD header bumped to v2.6 with a v2.5 back-fill note. No runtime impact. (URD US-25.8) | D2 §B, wireframes, URD header |

### 1.13 Remediation Log (ADD v3.1 — Discussion 2026-07-05 change set #2, 2 decisions)

v3.1 records the two product decisions that close feasibility conditions C2/C3 (`technical_feasibility.md`, URD v2.7 Epic 26, US-26.1…US-26.5). These are **authoritative**; D1–D7 and the schema docs inherit the resolutions below.

| ID | Change request | Architecture resolution | Section / mapping |
|---|---|---|---|
| AL-47 | Passenger scans the **driver's own bank-issued LankaQR** (AL-22), so the money moves bank-to-bank and **no webhook ever reaches `fare-svc`** — the D-10 state machine had no oracle for `Paid` vs `FellBackToCash`, and "I paid / driver says no" disputes had no evidence trail | **Attestation-based settlement for driver-QR payments.** Passenger taps **"I've paid"** → `QrClaimedByPassenger` (optional receipt screenshot attached as dispute evidence) → driver push **"QR payment received?"** → **Confirm** → terminal **`DriverConfirmedQR`** (earning posts, R-05 — settles like cash). Driver may confirm without a prior claim. Claim without confirm → nudge push at +5 min; still unresolved → **Support → Finance dispute queue** (no money moves — zero-commission). Gateway-verified `Succeeded` remains **OnePay-only**; `fares.ride_payments.state` gains the two new values. (URD US-26.1) | §6 `fare-svc`, §9 `fares.ride_payments`, D-10 |
| AL-48 | **Number-masking requirement withdrawn** (product decision). Previously: masked-number PSTN bridge for "Normal call" (AL-36/I-28.3), ride-scoped proxy-DID lease for the web subview (AL-44/I-29.3), masked-SMS relay on VoIP failure (D-25) — and no proxy-DID/CPaaS product exists with +94 numbers (feasibility C3) | **Calls use real numbers, revealed only post-accept.** "Normal call" = **direct cellular dial** of the counterparty's MSISDN returned in the ride detail after driver acceptance; withheld for rides cancelled pre-assignment (US-24.4 rule); **P-05 retained** (proxy: driver sees rider, never booker). Call-type chooser stays: **Free (VoIP) / Normal (direct dial)**. Web subview: driver card carries a **`tel:` link** — `POST /public/track/{token}/call`, the DID lease and `comms.call_log.share_token` are **removed**. VoIP failure → "Call normally instead?" direct-dial prompt — **D-25 masked-SMS relay removed**. `comms.call_log.call_type ∈ {free_voip, direct_dial}` (client-logged tap, best-effort). ToS/first-call tooltip disclose number visibility (PDPA transparency, US-26.5). **Supersedes the masking clauses of AL-36, AL-44, D-24 ("no numbers exposed" is now a VoIP property, not a requirement) and D-25.** (URD US-26.2/26.3/26.4/26.5) | §6 `voip-svc`/`public-bff`, §9 `comms.call_log`, §14 failure modes, §18 |

### 1.14 Remediation Log (ADD v3.2 — Discussion 2026-07-18, 3 changes)

v3.2 absorbs the three Fleet Portal corrections from the 2026-07-18 review (URD v2.8: Epic 27, US-27.1…US-27.4): the Mode B money flow finally gets a **verified destination** (bank & payout profile), vehicle onboarding gets the **four real Sri Lankan compliance documents**, and the Paid/Free setting gets an owner-comprehensible name. These are **authoritative**; D1–D6 and the schema docs inherit the resolutions below.

| ID | Change request | Architecture resolution | Section / mapping |
|---|---|---|---|
| AL-49 | Fleet Portal had **no facility to capture where Mode B pass-through money goes** — no bank account / account holder / bank & branch, no proof-of-account upload (statement or passbook first page), and no home for the **bank-app LankaQR code** that the passenger pay sheet (BR-23.10) is supposed to display | **Org bank & payout profile.** New Owner-only screen **SCR-FP-002a** (`/org/payout`): bank, branch, account number, account holder name + uploads `bank_statement`\|`passbook_first_page` + `lankaqr_code` (all via `docs.uploads`). New **`registry.fleet_payout_profiles`** (versioned; partial-unique one `verified` row per org). **Verification Officer** approves in the existing fleet-org queue (SCR-AP-003); holder name must match org/owner KYC. `POST /mode-b/subscriptions/{id}/pay` now returns **`payTo`** (signed LankaQR URL / transfer details) **from the latest verified row only**; `PUT …/classification {paid}` → 409 `payout-profile-not-verified` until Verified. Pass-through economics unchanged — MageRide still holds no subscriber money. (URD US-27.1/27.2) | §6 `fleet-svc`/`subscription-svc`, §9 `registry.fleet_payout_profiles`, D2 SCR-FP-002a |
| AL-50 | **SCR-FP-004 vehicle onboarding was under-specified** — a single generic "insurance + registration docs" dropzone, with no revenue license, no CR copy slot, and no **route permit** although Mode A passenger transport legally requires one (US-2.24 moved permits to the Fleet Portal but the screen never materialized them) | **Named per-vehicle document slots**: **registration copy (CR book)** · **insurance certificate** · **revenue license** (all modes) · **route permit (Mode A required)** — reusing the Mode-C pipeline (`docs.uploads` → ocr-svc Gemini extraction → `registry.document_fields` per-field verification, AL-29). `registry.documents.driver_id` made **nullable** + new `fleet_id` (CHECK one owner present); existing `kind` values cover all four slots. **Approval gate extends AL-10**: verified registration + insurance + revenue_license for all modes, + verified permit for Mode A, before `status='APPROVED'`; expiry auto-suspends dispatch (E-03). Bulk CSV creates vehicles `docs_pending`. (URD US-27.3) | §6 `fleet-svc`/`registry-svc`/`ocr-svc`, §9 `registry.documents`, D2 SCR-FP-004 |
| AL-51 | "**Mode B classification**" (Paid/Free) read as jargon in the Fleet Portal — owners did not connect it to "does this service charge passengers?" | **Renamed "Service payment" — values Free / Paid** across SCR-FP-004 (form field + status-table column) and all fleet-facing copy. **UI/documentation rename only**: `PUT /fleets/{id}/vehicles/{vid}/classification` and `registry.vehicles.mode_b_billing` are intentionally unchanged (API/DB stability; no migration for a label). Supersedes the label of BR-23.8/US-13.1b; semantics stand. (URD US-27.4) | D2 SCR-FP-004, `web_fleet.html`, BR-31.3 |

### 1.15 Remediation Log (ADD v3.3 — Discussion 2026-07-22, 2 stack decisions)

v3.3 records two implementation-stack rulings from the 2026-07-22 architecture review. Pure technology decisions — **no functional scope change**: no new screens, endpoints, schema, or user stories (no URD epic; same pattern as the v2.5 Minimal-API/Dapper ruling). D2/D7 carry the corresponding inline updates + a D2 Δ addendum.

| ID | Change request | Architecture resolution | Section / mapping |
|---|---|---|---|
| AL-52 | The web portals specified only the framework (**Next.js**/React/TypeScript, AL-02/AL-03) — **no styling system was ever mandated**, leaving per-portal divergence open (ad-hoc CSS-in-JS vs component kits) and no single home for the D2 §A brand tokens on the web | **Tailwind CSS is the sole styling system for all MageRide web frontends**: Admin Portal (`admin.mageride.lk`), Fleet Portal (`fleet.mageride.lk`), and the **SCR-WT** passenger web-subview pages (served via `public-bff`, AL-44) reuse the same pipeline for visual consistency. One shared **Tailwind preset** (`@mageride/tailwind-preset`) maps D2 §A colors/vehicle-type tokens/Outfit+Inter type scale/spacing into `tailwind.config` `theme.extend`, with light/dark via the `dark:` variant; D2 breakpoints (375/768/1024) map to Tailwind `screens`. CSS is **compiled at build time by PostCSS inside `npm run build`** — no runtime CSS-in-JS (SSR-safe, smaller bundles, strict-CSP friendly). Headless component primitives (e.g. Radix UI / Headless UI) styled with Tailwind are permitted; **MUI, Bootstrap, styled-components, Emotion and other runtime CSS-in-JS are excluded** | §2 stack list, §6 `admin-bff`, §17 Phase-1 checklist, §18.1 (new Web styling row); D2 §AP/§FP + Δ 2026-07-22, D7 §1 build |
| AL-53 | Review directive: "the .NET 10 API must use **Minimal APIs** and data access must be **Dapper over Npgsql**" — verify the document set actually mandates this end-to-end | **Reaffirmed — already authoritative since ADD v2.5; the 2026-07-22 audit found no drift.** §2 stack list, §6 component table (all `*-svc` rows), and §18.1 (Services / Data access / DB migrations rows) mandate **ASP.NET Core (.NET 10) Minimal API** (no MVC controllers) with **Dapper over Npgsql** — hand-written parameterised SQL, repository-per-bounded-context, `NpgsqlTransaction` for units of work, **no EF Core / `DbContext` / LINQ-to-SQL / ORM change tracking**; migrations = versioned SQL scripts (DbUp/Grate), never `dotnet ef`. Specs agree: D3 §Data-access, D4 header (DDL is source of truth, no model-first generation), D7 §1/§7. **No changes required** | §2, §6, §18.1 (verified); D3 §conventions, D4 header, D7 §1/§7 (verified) |

### 1.16 Remediation Log (ADD v3.4 — Discussion 2026-07-22 change set #2, 2 changes)

v3.4 absorbs the GTFS-input decision from the 2026-07-22 review #2 (URD v2.9: Epic 28, US-28.1…US-28.3): **a full national GTFS file will be available at the beginning**, and the admin needs a real interface to put it in. These are **authoritative**; D1–D7 and `server_db_schema.md` inherit the resolutions below. *(The standalone GTFS acquisition plan, which also inherited them at the time, was retired on 2026-07-23 — AL-56.)*

| ID | Change request | Architecture resolution | Section / mapping |
|---|---|---|---|
| AL-54 | GTFS input existed only as a raw endpoint (`POST /admin/transit/gtfs-import`, AL-18) buried under "Admin Portal Config" — **no screen**, no validation feedback, no visibility of what dataset is live, no way back from a bad import | **New Admin Portal screen SCR-AP-016 `gtfs_manager` — GTFS Dataset Manager** (Configuration nav group; Admin + Super Admin, deny-by-default RBAC, mutations audited per D-35). Upload the **full GTFS zip** (multipart, ≤ 200 MB; required files `agency/routes/trips/stops/stop_times` + `calendar`\|`calendar_dates`; optional `shapes/frequencies/translations/feed_info`) → async **validation pipeline** (referential integrity, duplicate IDs, stops within the Sri Lanka bbox, service-window sanity, stable-ID warnings — route/stop IDs should stay stable across feed versions) → status `Uploaded → Validating → Validated / Failed` with a **downloadable row-level error report**; **preview** (per-file counts, `feed_info` version, service date range, warnings) → **Activate**: importer loads `transit_staging.gtfs_*`, then one transaction swaps the live tables and `NOTIFY`s `transit-svc` to reload caches (≤ 60 s). **Version history + rollback**: exactly one `active` feed (partial-unique index); replaced feeds → `archived`, re-activatable one-click; original zips retained in object storage (sha256 dedupe). New **`transit.gtfs_feed_versions`**; new endpoint set **`/admin/transit/gtfs/uploads*`, `…/activate`, `…/versions*`** — raw `POST /admin/transit/gtfs-import` **superseded** (retained as the internal import step). (URD US-28.1/28.2/28.3) | §6 `transit-svc`/`admin-bff`, §9.1 `transit`, Appendix C; D2 SCR-AP-016, D3, D4, D7 `gtfs-import` job |
| AL-55 | AL-18/the GTFS acquisition plan assumed **corridor-first coverage growth** (G0 ≈ 100 Western-Province routes) with SCR-PA-009 degrading gracefully wherever the feed is thin — but **the full GTFS file will be available at the beginning** | **Full-feed-at-launch premise adopted.** Day-0 operations = admin uploads the complete national feed via SCR-AP-016 before go-live; Mode A route-matching (US-8.2b) is **not gated on corridor acquisition phases**. The acquisition plan's G0–G3 rollout gates no longer gate launch *(the plan was then retired outright on 2026-07-23 — feed refreshes are externally provided files entering via SCR-AP-016; AL-56)*. SCR-PA-009's **no-coverage degradation is retained as a safety net** (genuine feed gaps, expired service windows, pre-first-import state), not as an expected launch state. (URD Epic 28 preamble) | §6 `transit-svc`, D2 SCR-PA-009 note, D6 |

### 1.17 Remediation Log (ADD v3.5 — Micro-change-set 2026-07-23, 1 change)

The GTFS dataset will be **provided to the platform as a ready-made file** — the day-0 national feed **and every subsequent refresh**. There is no in-house sourcing/authoring workstream, so the standalone GTFS Acquisition & Authoring Plan (adopted 2026-07-05, already reduced to refresh methodology by AL-55) is **obsolete and retired**.

| ID | Change request | Architecture resolution | Section / mapping |
|---|---|---|---|
| AL-56 | The retired acquisition plan still appeared as an inherited/companion document across the spec set (validation conventions, refresh-methodology pointers, feasibility C4/P0-4, Phase C spec upload), implying an in-house feed-authoring workstream that no longer exists | **GTFS acquisition plan retired; all spec references removed.** The GTFS feed — launch and every refresh — is an **externally provided file**; the platform's sole ingestion surface is **SCR-AP-016** (upload → validate → atomic activate → rollback, AL-54), and server-side validation (D5 BR-32.1) is the only quality gate MageRide enforces. Feed authorship, sourcing, corridor gating, and survey work are **out of platform scope**. The plan document is archived for historical context only — not part of the build spec set and not uploaded to the Phase C repo | §6 `transit-svc`; D2 SCR-PA-009 note, D4 `feed_info_version` comment, D5 BR-32.1, D6 I-32.2, traceability matrix; `technical_feasibility.md` C4/P0-4 notes; `phase_c_step_by_step_guide.md` Step 4 |

---

## 2. Executive Summary

This document defines the target architecture for a nationwide passenger transport tracking platform. The platform is sized for **incremental growth**, not a day-one 1M-user launch:

| Stage | Concurrent Vehicles | Concurrent Passengers | Substrate |
|---|---|---|---|
| **Development** | ~100 | ~1,000 | Single 24 GB / 6 vCPU VPS (Contabo VPS-30), 2 GB containers — full v2.4 service set (§10.1) |
| **Production (initial launch)** | **10,000** | **100,000** | HAProxy + Keepalived LB cluster fronting a small fleet of VPS/K3s nodes |
| **Production (scale-out)** | 30,000 → 100,000 | 300,000 → 1,000,000 | Add nodes incrementally; graduate to managed K8s when ops load justifies it |

New capacity is added by **scaling node count and pod replicas**, not by re-architecting. The same domain boundaries, topic schema, geocell model, and event contracts hold from a single-VPS development environment to a multi-AZ Kubernetes deployment.

The platform is an **event-driven, geospatially-partitioned, real-time system** built on:

- **EMQX** (MQTT 5) for device ingest
- **Redis (Cluster) + Redis GEO / Streams** for hot-path live state and fan-out coordination
- **Redpanda** (Kafka-API, single-binary, no JVM, no ZooKeeper) for the durable event backbone — single broker in development, 3-node cluster (RF=3) at MVP / pilot, 5-node cluster (RF=3, tiered storage) at national scale
- **ASP.NET Core (.NET 10) Minimal API** microservices with **Dapper** (micro-ORM, hand-written parameterised SQL — no EF Core) on **Linux containers**, orchestrated by **Kubernetes (K3s → managed K8s)**
- **PostgreSQL 16 + PostGIS** for operational and historical spatial data, accessed via **Npgsql + Dapper**
- **SignalR (Redis-backplane → Redpanda-backplane)** for browser/mobile WebSocket fan-out
- **HAProxy + Keepalived** (MVP) → **Kubernetes Ingress (NGINX/Envoy) + cloud LB** (scale)
- **Kotlin Multiplatform (KMP)** shared business logic + **Jetpack Compose** (Android UI) + **SwiftUI** (iOS UI) for 4 mobile app targets (Passenger Android/iOS, Driver Android/iOS)
- **Next.js (React, TypeScript) + Tailwind CSS** for the web frontends — Admin Portal, Fleet Portal, and the SCR-WT passenger web-subview pages (one shared Tailwind preset carrying the D2 design tokens; no runtime CSS-in-JS — AL-52)

The system is **MVP-aware**: it is explicitly designed so that the same domain boundaries hold from a single 24 GB VPS to a multi-AZ Kubernetes deployment, by adding nodes and replacing back-end components without re-architecting.

---

## 3. Goals, Non-Goals and Assumptions

### 3.1 Functional Goals

- Real-time vehicle position visualisation within configurable radius (2–3 km) for passengers
- **Three service modes** — Mode A (Public Transport buses), Mode B (Private Transport — school buses, book hires), Mode C (Standby On-Demand dispatch) — each modelled as a distinct trip lifecycle and visibility scope
- Trip lifecycle management for buses and on-demand vehicles
- **Mode C dispatch & scheduling** — nearest-driver dispatch based on distance, Driver Rating/Level, and vehicle category with 15 s accept/reject window, advance ride scheduling, **Job Board** (visible to drivers within 30 km radius), **Driver Level System** (start L3, level-up via ratings: 100 five-star = 500 points = 1 level up, 3 passenger reports = 1 level drop + temporary delisting, Level 1 = loses scheduled ride / Job Board privileges)
- Vehicle registration with permit/ID OCR extraction, **driver profile** (photo, name), and **registration status tracking** (pending/approved/rejected)
- Sharing model: vehicle-to-user assignment, time-bounded; passenger can unsubscribe from Mode B
- **Namma Yatri-style zero-commission daily fee model** — Mode A (Public Transport) buses pay **no fee**. Mode C (Standby On-Demand) daily fees: Motorbike Rs 50, Three-wheeler Rs 100, Flex Rs 150, Sedan Rs 200, Mini Van Rs 250, Van Rs 300. **First trip of the day is always free**; fee auto-deducted from wallet before 2nd trip. Mode B (Private Transport) pays **monthly ~Rs 300** (first month free). Passengers fully free.
- **Mode C fare engine** — 1st-km charge + per-km rate + peak-hour and night surcharges (admin-configurable windows). Mode B has no per-trip fare.
- Fare calculation with **upfront fare estimate** before boarding/booking
- **In-app ride payment** via LankaQR (no surcharge) or OnePay (+5% surcharge); cash as default
- **Trip rating** (1–5 stars) and **passenger trip history** (Epic 8)
- **Safety primitives** — SOS button for both passengers and drivers (SMS to emergency contact via SMS gateway), live-trip web share link, passenger "Report Vehicle" with 3-strike auto-delisting, **passenger block driver** (Epic 12)
- **Fleet Operator** features — organisation onboarding (Verification-Officer-gated), Mode A/Mode B multi-vehicle management, per-fleet dashboards & live map, scheduling, ST-901 auto-sessions, **monthly per-Mode-B-vehicle billing** via the **Fleet Portal** (`fleet.mageride.lk`) (Epic 13, **Phase 1** — AL-03; route-deviation/geofence alerts Phase 3)
- **Admin moderation** — vehicle/driver suspend, fare tariff and daily fee configuration, broadcast announcements, report review queue, **Driver Level System parameter configuration**, **support ticket queue** (Epic 14)
- **Driver daily platform fee** — drivers top up wallets **in-app only** via credit/debit card (OnePay), OnePay wallet, or LankaQR (**no bank transfer** — AL-05). Daily fee auto-deducted from wallet balance before 2nd trip; **driver-to-driver credit transfer moves the exact value — no per-transfer commission**. **No Google Play Subscription, no per-trip commission**
- **Bulk credit vouchers** — drivers can purchase prepaid credit vouchers (Rs 1,000 / 2,000 / 3,000 / 5,000 / 10,000), each with a **commission/discount % configured per voucher value in the database** (variable — set **per voucher value (denomination)** by Admin in the Admin Portal Config) applied **only at purchase** — the credit lands in the buyer's own wallet immediately (e.g., pay Rs 900 → Rs 1,000 credited at a 10% rate). This % **is the reseller's margin**; the buyer can later transfer credit to other drivers, and **driver-to-driver transfers carry no commission** (exact value debited from sender, credited to recipient)
- **Reseller (informal, not a role/account/capability)** — there is **no separate reseller role or "enable" step**: any driver who has bought bulk credit cheaply can transfer it to other drivers by **Driver ID**. The reselling driver's margin is the **bulk-voucher purchase discount, not a per-transfer commission** (AL-01)
- **Admin Portal** (`admin.mageride.lk`) — single consolidated back-office web application (Sinhala/Tamil/English i18n) for all six internal roles: verification, moderation, support, finance & payment-gateway reconciliation, **bulk-voucher commission-% configuration (per voucher value)**, RBAC, audit, and reporting (AL-02)
- **ETA display** for nearby vehicles (distance-based Phase 1; road-network Phase 3)
- **Driver earnings dashboard** for real-time earnings tracking
- Support for both **mobile-as-tracker** and **dedicated hardware GPS trackers** (ST-901 / ST-902, GT06, TK103, JT/T 808 family, generic-NMEA over MQTT) — **Phase 1**, sized for **100,000 active devices**, covering individual ride-hailing vehicles **and** public-transport / fleet-managed buses with bulk provisioning
- **Cancellation penalty** — Rs 50 debit balance per cancellation after driver acceptance; 3 continuous cancellations disable booking
- **In-app VoIP call** between passenger and driver (no personal phone numbers exposed) — P1
- **In-app support** — FAQ section, support ticket raising and tracking (Epic 16)
- **App version management** — mandatory and soft upgrade prompts for API compatibility (Epic 17)
- **Ratings & reviews** — passenger rates driver (1–5 stars + text), driver rates passenger (1–5 stars), admin review of low-rated drivers (Epic 18)
- **Accessibility** — TalkBack/VoiceOver support, dynamic text sizing, WCAG AA contrast (Epic 19)

### 3.2 Non-Functional Goals

Targets are expressed per stage so that initial production sizing is realistic and growth is planned by adding nodes.

| Attribute | Initial Production (Launch) | Scale-Out Ceiling |
|---|---|---|
| Concurrent vehicle publishers (mobile + hardware combined) | **10,000** (launch — up to 5,000 hardware trackers) | **100,000** (up to 100,000 may be hardware trackers) |
| Concurrent passenger WebSocket sessions | **100,000** | 1,000,000 |
| Position ingest throughput (blended mobile + hardware) | **3,000 msg/s** sustained, **15,000 msg/s** burst | **30,000 msg/s** sustained, **90,000 msg/s** burst |
| End-to-end position latency (device → passenger screen) | **p95 < 5 s, p99 < 8 s** (consistent with US-5.5 4 s moving cadence floor) | Same |
| In-app VoIP concurrent calls (Phase 1) | 500 concurrent | 5,000 concurrent |
| SOS SMS dispatch latency | p99 ≤ 5 s from button-tap | Same |
| Availability (control plane APIs) | 99.9% | 99.9% |
| Availability (live tracking plane) | 99.5% (degrades gracefully) | 99.5% |
| RPO | 5 min (operational data), 0 (live data is ephemeral) | Same |
| RTO | 30 min (full region failover) | Same |

Scale-out is achieved by adding EMQX nodes, fanout pods, position-processor pods, Redis shards, and Postgres read replicas — not by re-architecting.

### 3.3 Non-Goals (Explicit)

- Routing / ETA prediction via road network (Phase 3; simple distance-based ETA is Phase 1)
- Driver ride earnings payout settlement to bank accounts (Phase 2; in-app ride *payment collection* via LankaQR/OnePay and driver *wallet for daily platform fee* is Phase 1)
- Driver-to-driver chat
- Full web passenger app (mobile-first; web surfaces = **Admin Portal** + **Fleet Portal** only). The **Passenger Web subview** (`passenger.mageride.lk`, AL-04) is a no-login, single-ride tokenised view used only for proxy-ride tracking via SMS link — not a full web app
- Ad-hoc route sharing with seat tracking (Phase 2; Epic 6 deferred)
- ~~Fleet operator features (Phase 2)~~ — **now Phase 1** via the Fleet Portal (Epic 13, AL-03)

### 3.4 Key Assumptions

| # | Assumption |
|---|---|
| A1 | Average vehicle publishes 1 call/4s when moving, 1 call/10s when stationary, 1 call/60s when in idle standby. Effective blended steady-state ≈ **0.12 Hz/vehicle** (derived in §16.1, D-20) |
| A2 | Average passenger app subscribes to ~1 geo-cell + 8 neighbours; a moving passenger crosses cells every ~30–60 s |
| A3 | Average position payload ≈ 80–120 bytes on the wire (CBOR/Protobuf); ~250 bytes JSON |
| A4 | Phase 1 supports **both** mobile-app-as-tracker (default for individual drivers) and dedicated hardware trackers (default for fleet-operated buses and high-utilisation Mode C vehicles); mix is configurable per vehicle |
| A5 | Single primary region; warm DR in second region from Phase 3 |
| A6 | Country-scale, not global — sub-region latency assumptions hold |
| A7 | Both Android and iOS platforms targeted via KMP + Native UI (Jetpack Compose / SwiftUI) |

---

## 4. High-Level Architecture Narrative

The platform follows a **strict separation of four planes**:

1. **Device / Ingest plane** — telematics in. MQTT broker cluster + TCP adapter services normalise all device protocols into a single internal event schema.

2. **Stream / Processing plane** — durable, ordered, partitioned event log (Redpanda, Kafka-API compatible). All consumers (live state, persistence, analytics, alerting, trip state machine) read from here. Decouples ingest rate from any downstream consumer.

3. **Live state plane** — Redis Cluster holds last-known position, vehicle metadata cache, and acts as the **geo-partitioning index** (geohash → list of vehicleIds). This is the only system on the hot read path for "what's near me".

4. **Distribution / Edge plane** — SignalR (WebSocket) servers fan out per-geocell updates to subscribed passenger clients. Passengers subscribe to a small set of geocell groups, not to individual vehicles.

Persistence (PostgreSQL/PostGIS), trip lifecycle, billing, identity, OCR, and admin all sit **off the hot path**, consuming from the event stream or serving REST/gRPC requests via an API gateway.

This shape — *ingest → stream → projections* — is the same pattern used by Uber (Kafka + Cherami → uMonitor/uETA), Lyft (Kafka + Flink), and large transit AVL platforms. We use Redpanda (Kafka-API compatible) for lower operational overhead.

---

## 5. Logical Architecture

```
┌─────────────────────────────────────────────────────────────────────────────┐
│                              CLIENTS                                        │
│  Passenger App (Android/iOS)  Driver App (Android/iOS)                       │
│  Admin Portal (admin.mageride.lk)   Fleet Portal (fleet.mageride.lk)         │
│  Passenger Web subview (passenger.mageride.lk — no-login, proxy ride only)   │
│                                                                              │
│  ┌──────────────────────────────────────────────────────────────────────┐  │
│  │  KMP Shared Module (Kotlin)                                          │  │
│  │  Domain logic, DTOs, Ktor HTTP clients, H3 geocell, adaptive rate    │  │
│  │  Shared across all 4 app targets                                     │  │
│  └──────────────────────────────────────────────────────────────────────┘  │
└──────────────┬──────────────────┬──────────────────┬──────────────┬─────────┘
               │ WSS/HTTPS         │ WSS/HTTPS+MQTT        │ HTTPS
               ▼                   ▼                       ▼
┌─────────────────────────────────────────────────────────────────────────────┐
│              EDGE: Cloud LB → Ingress (Envoy/NGINX) → WAF                   │
└──────┬─────────────────────┬──────────────────────┬─────────────────────────┘
       │                     │                      │
       ▼                     ▼                      ▼
┌──────────────┐     ┌────────────────┐     ┌─────────────────────┐
│ API Gateway  │     │ SignalR Fanout │     │ MQTT (EMQX Cluster) │
│ (YARP/Kong)  │     │   (N pods)     │     │   + TCP Adapters    │
└──────┬───────┘     └────────┬───────┘     └──────────┬──────────┘
       │                      │                        │
       │                      │ subscribes to          │ publishes raw
       │                      │ stream topics          │ telemetry
       ▼                      ▼                        ▼
┌─────────────────────────────────────────────────────────────────────────────┐
│        EVENT BACKBONE — Redpanda (Kafka-API; 1 broker dev → 3-node RF=3 MVP → 5-node scale)       │
│  topics: telemetry.raw, telemetry.normalized, trip.events, audit.events     │
└──────┬──────────────┬──────────────┬──────────────┬─────────────────────────┘
       │              │              │              │
       ▼              ▼              ▼              ▼
┌────────────┐ ┌─────────────┐ ┌──────────┐ ┌────────────────────┐
│ Position   │ │ Persistence │ │ Trip     │ │ Anti-Spoof /       │
│ Processor  │ │ Writer      │ │ State    │ │ Plausibility       │
│ (geo idx)  │ │ (Postgres)  │ │ Machine  │ │ Filter             │
└─────┬──────┘ └──────┬──────┘ └────┬─────┘ └─────────┬──────────┘
      │               │             │                 │
      ▼               ▼             ▼                 ▼
┌────────────┐  ┌──────────────────────┐       ┌──────────────┐
│ Redis      │  │  PostgreSQL+PostGIS  │       │ Alerting/    │
│ Cluster    │  │  (HA, streaming repl)│       │ Audit Log    │
│ (GEO,      │  └──────────────────────┘       └──────────────┘
│  Streams,  │
│  Cache)    │
└────────────┘
```

### 5.1 Bounded Contexts (DDD View)

| Context | Responsibility | Storage |
|---|---|---|
| **Identity & Access** | Users, **nine canonical roles + org-scoped fleet sub-roles** with **deny-by-default RBAC** (AL-06), phone OTP for **apps only**, **Password/Google (Admin Portal)** and **Email/Google/Apple (Fleet Portal)** for web, **no MFA second factor for internal roles (removed AL-37)**, device binding (Android Keystore / iOS Keychain + Secure Enclave), **single-active-device per app** (AL-08), **driver emergency contact** (AL-13) | Postgres `iam` schema |
| **Vehicle Registry** | Vehicle, owner, permit, sharing grants, device assignments, **driver profile** (photo, name), **registration status** (pending/approved/rejected), vehicle deactivation, Mode B unsubscribe | Postgres `registry` schema + object storage (profile photos) |
| **Provisioning** | Device cert/JWT issuance, IMEI ↔ vehicleId binding, secret rotation (**Phase 1** — hardware GPS trackers promoted per §1.6, T-02) | Postgres `prov` + Vault |
| **Telemetry Ingest** | Normalise raw GPS into canonical event | Stateless |
| **Live State** | Last-known position, geo index, presence, **vehicle speed for ETA calc** | Redis Cluster |
| **Trip Lifecycle** | Mode-aware sessions (A/B/C), start/end, idle detection (30 min), auto-end at end-position (100m geofence), **5-min grace period restart** after auto-end, trip ratings | Postgres `trips` schema + Redis state |
| **Mode C Ride Aggregate** (v2.2 expanded) | Owns full Mode C *commercial* lifecycle for **passenger rides, proxy bookings, and package deliveries** (P-01, P-06). Distinguishes `booker ≠ rider`, manages **FCM location-request** sub-flow (P-02), generates and validates **4-digit pickup/delivery OTPs** for packages (P-07), persists proof-of-delivery photos (P-10). Same state machine across all three sub-kinds | Postgres `rides` schema + Redis + object storage (proof photos) |
| **Dispatch & Scheduling** (Mode C) | Online standby presence, nearest-driver dispatch by **distance + Driver Rating/Level + vehicle category** with 15 s offer TTL, **Job Board** (scheduled rides within 30 km, drivers post intent, dispatch by proximity + level 30 min prior), **Driver Level System** (start L3, level-up: 100 five-star ratings = 500 points = 1 level up, 3 passenger reports = 1 level drop + temporary delisting, L1 = **loses scheduled ride / Job Board privileges** — not a permanent ban), **cancellation penalty** (Rs 50 debit on next ride after driver acceptance, 3 continuous cancellations = booking disabled), reassignment on reject/timeout | Postgres `dispatch` schema + Redis (driver presence, offer state) |
| **Fan-out / Push** | WebSocket sessions, geocell subscriptions | Redis pub/sub or Redpanda backplane |
| **Platform Fee & Billing** | **7-tier daily fee plans**: Mode A = Free, Motorbike Rs 50, Three-wheeler Rs 100, Flex Rs 150, Sedan Rs 200, Mini Van Rs 250, Van Rs 300 (admin-configurable). **First trip free**; fee auto-deducted before 2nd trip. Mode B **monthly fee per vehicle ~Rs 300** (first month free; consolidated for fleets — AL-03). **Driver wallet balance management** (one wallet per driver; a reselling driver uses the same wallet — AL-01), **in-app wallet top-ups** (OnePay card / OnePay wallet / LankaQR — **no bank transfer**, AL-05), **bulk credit vouchers** (Rs 1,000–10,000, **per-tier purchase discount configured in DB**, applied at purchase, credited to buyer's wallet), **driver-to-driver credit transfers** (by Driver ID, **exact value, no commission** — AL-01), invoices. **No per-trip fee, no Google Play Subscription.** | Postgres `billing` schema + OnePay |
| **Admin Portal** (`admin.mageride.lk`) | Single consolidated **back-office** web app (`admin-bff`, Sinhala/Tamil/English i18n) for **all six internal roles** (AL-02): moderation, onboarding/verification, support, **finance & payment-gateway (OnePay/LankaQR) reconciliation**, **bulk-voucher discount-tier configuration**, tariff/config, RBAC, audit, reporting. **Password or Google Sign-In — no MFA (AL-37).** No driver-facing pages | Serves static + SSR via CDN; API calls to `admin-bff` + `wallet-svc` + `subscription-svc` |
| **Fleet Portal** (`fleet.mageride.lk`) — **Phase 1** | Responsive web app for **Fleet Owners** (AL-03): org onboarding (with Verification-Officer approval gate), Mode A/Mode B vehicle onboarding & lifecycle, driver assignment, tracker binding, scheduling & alarms, live fleet map & per-vehicle analytics (own org, row-level security), **monthly per-Mode-B-vehicle billing** + fleet wallet. **Email+Password / Google / Apple.** Org-scoped Owner/Manager/Viewer sub-roles | Static + SSR via CDN; API calls to `fleet-svc` |
| **Fare & Settlement** | **Mode C fare** (1st-km charge + per-km + peak-hour and night surcharges with admin-configurable windows), **upfront fare estimation**, **in-app ride payment** (Cash / LankaQR / OnePay +5% surcharge), driver earnings aggregation, driver payouts, trip ratings storage. **Mode B has no per-trip fare** (monthly charge only). | Postgres `fares` + `trips` schemas + OnePay |
| **OCR / Document Intake** | ID & vehicle-doc extraction (Gemini Flash 3.0 + fallback); **Mode-C onboarding auto-verify → auto-approve** (AL-27) | Object storage + Postgres `docs` |
| **Notifications** | FCM push, SMS via gateway (Notify.lk), interest alerts, access request alerts, **low-balance / daily fee warnings**, session auto-end alerts, **dispatch offers**, **scheduled-ride reminders**, **payment confirmations**, **SOS** | Stateless + Postgres `notifications` schema (preferences) |
| **Safety & Moderation** | SOS event capture for **both passengers and drivers** (location + trip + emergency contact SMS), live-trip web-share token issuance, passenger "Report Vehicle" intake, 3-strike automatic delisting, **passenger block driver**, admin review queue | Postgres `safety` schema + SMS gateway |
| **Fleet Operator** (Fleet Owner) — **Phase 1** | Organisation onboarding (**Verification-Officer approval gate**, AL-03), multi-vehicle assignment to drivers, per-fleet map and analytics (own-org RLS), scheduling & schedule alarms, ST-901 binding + auto-sessions, **monthly per-Mode-B-vehicle billing** (Mode A free, **no Mode C**); route-deviation/geofence alerts (**Phase 3**) | Postgres `registry.operators` + `fleets` + `spatial.geofences` |
| **Audit & Compliance** | Immutable event store, retention policies, **admin action audit log** (vehicle approvals, wallet adjustments, user bans) | Redpanda + cold storage (S3-compatible) |
| **Observability** | Logs/metrics/traces | Loki/Prometheus/Tempo |
| **In-App Support** | FAQ articles, support ticket creation/tracking/resolution, admin ticket queue | Postgres `support` schema |
| **App Versioning** | Mandatory/soft upgrade prompts, minimum version enforcement at API gateway | Postgres config or remote config |
| **Reputation** | Unifies cancellation, no-show, vehicle-report counters with rolling-window semantics; exposes `block_status` and `level` to dispatch/iam | Postgres `reputation` schema + Redis counters |
| **VoIP / Communications** | LiveKit SFU + coturn TURN cluster, signalling REST ("Free call"); "Normal call" = direct cellular dial to the real number post-accept (AL-48 — masking removed) | Postgres `comms` schema, no media persisted by default |
| **Map Infra** | Self-served vector tiles (PMTiles on Cloudflare R2 + CDN), self-hosted geocoding (Nominatim), weekly OSM update pipeline | R2 bucket + Postgres OSM extract |
| **Localised Content** | Notification templates, FAQ articles, admin broadcasts, fare-tariff display strings in Sinhala/Tamil/English | Postgres `content` schema |

---

## 6. Component Architecture (Microservices)

| Service | Tech | Responsibility | Scaling Unit |
|---|---|---|---|
| `iam-svc` | .NET 10 LTS minimal API | User profile (incl. **driver emergency contact**, AL-13), **nine-role + fleet sub-role RBAC** (deny-by-default, AL-06). **Apps (Passenger + Driver) = Phone OTP only** (SMS gateway, Redis token-bucket 60 s resend / 5 per hr). **Admin Portal = Password or Google Sign-In, no MFA (AL-07/AL-37)**; **Fleet Portal = Email+Password / Google / Apple** (AL-07). Device-binding (Android Keystore / iOS Keychain). **Token model (R-29, E-02)**: 30 min RS256 API access JWT (JWKS-rotated) with `role`/`fleet_role` claims + opaque refresh in `iam.sessions`+Redis (`refresh:{jti}`, rotated on use). **Single active device is per app** — a new-device login revokes the prior `(user_id, app)` session only; a person may run Driver + Passenger apps at once (AL-08, US-1.12). **MQTT session JWT is separate**: TTL = max(active-ride duration + 2 h, 4 h), bound to `(vehicleId, deviceId, [rideId])`; survives mid-trip API-token refresh failures in low coverage | Stateless, HPA on RPS |
| `registry-svc` | .NET 10 LTS | Vehicle CRUD with **uniqueness constraint per registration number (active set)** and **canonical `vehicle_type` CHECK** (Motorbike/Three-wheeler/Flex/Sedan/Mini Van/Van + Truck/Mini Truck + Bus/Train — "Car"→"Sedan", AL-09), sharing grants, permit OCR orchestration, **registration status**, **vehicle deactivation/removal**, **Mode B unsubscribe** emits `share.revoked`, **OnePay merchant onboarding** during approval. **Mandatory vehicle-insurance document for all modes (A/B/C)** — registration cannot be `APPROVED` without a valid insurance certificate (AL-10). **Train registration (Mode A) is admin-only** — trains are created/edited/removed exclusively via `admin-bff`; the Driver App exposes no train-registration path. **Document expiry tracker (E-03)** covers permit/ID/license **and insurance**: nightly job inspects `registry.documents(expires_at)` and emits `document.expiring` at T−30d/T−07d/T−1d; on `expires_at < now()` flips driver to `DISPATCH_SUSPENDED` until re-uploaded and re-approved | Stateless |
| `provisioning-svc` | .NET 10 LTS + step-ca (private CA) + Vault PKI | **Phase 1.** Per-device credential lifecycle for hardware trackers: mints X.509 client certs (MQTT-capable trackers) or signed bearer-token-with-IMEI-binding (legacy TCP-only trackers). Maintains `prov.tracker_bindings` (IMEI ↔ vehicleId ↔ credential serial), Redis cache `imei:{imei}` with pub/sub invalidation, 90-day rotation cron, immediate revocation API (sub-60 s propagation), **anti-clone quarantine** on duplicate-IMEI presentation, bulk-mint worker consuming fleet CSV uploads | Stateless API + worker pool |
| `tracker-adapter-svc` *(per protocol family)* | .NET 10 LTS background worker, raw Kestrel TCP / `SocketAsyncEventArgs` / UDP listener (`System.Net.Sockets`) | **Phase 1.** One StatefulSet **per protocol**: `adapter-gt06`, `adapter-jt808`, `adapter-h02`, `adapter-nmea-mqtt-shim`. Each: terminates TCP/UDP socket, validates IMEI against `provisioning-svc` Redis cache, decodes binary frames into the canonical `PositionSample` record, signs and publishes to `veh/{vehicleId}/pos/live` (or `…/pos/replay` for batched backlog) inside EMQX as a server-side bridge user. Sends protocol-native command frames downstream when `veh/{vehicleId}/cmd` is received. Emulates LWT on socket half-close by publishing a `status=offline` retained message | Horizontally scaled, sticky-by-IMEI-hash via NLB consistent-hash; per-pod 10k sockets |
| `fleet-health-svc` *(new, Phase 1)* | .NET 10 LTS stream consumer | Aggregates `veh/{vehicleId}/status` events and tracker-diagnostics into per-fleet rollups (`Online / Stale / Offline / Decommissioned`); emits alerts on threshold breach (e.g., >10 % of a fleet offline within 5 min); writes to `telemetry.fleet_health_5m` Timescale continuous aggregate | Stateless, partitioned by fleetId |
| `mqtt-broker` | EMQX 5 cluster (3+ nodes) | Device ingest, ACL, TLS termination | Stateful set, DNS-clustered |
| `mqtt-bridge-svc` | EMQX rule-engine OR .NET MQTTnet consumer | Subscribes to MQTT topics, publishes to event stream | Per-broker-node |
| `position-processor-svc` | .NET 10 LTS stream consumer | Validates, anti-spoof, computes geohash, updates Redis GEO + per-cell stream | Partitioned by `vehicleId hash` |
| `persistence-writer-svc` | .NET 10 LTS | Batched writes to Postgres (positions every N sec or distance delta) | Partitioned consumer |
| `trip-state-svc` | .NET 10 LTS + state machine (Stateless lib) | **Mode A / Mode B tracking-session lifecycle only** (US-9.6 active-session mutex via Redis `lock:driver:{driverId}` SETNX + Postgres UNIQUE partial index on `trips.sessions(driver_id) WHERE state='ACTIVE'`); idle timer 30 min; auto-end at end-position geofence 100 m; 5-min grace restart; trip rating capture. **The Mode A/B Start/End-Journey dashboard (SCR-DA-011) drives this service** — for tracker-equipped vehicles the session **auto-starts/ends on ignition** (ACC on/off, US-3.22/3.23) with the device as single active publisher (US-3.6), and the **dashboard Start/End buttons override** the device by writing the session transition directly (AL-32, US-5.11/5.12). **Mode C ride lifecycle moved to `ride-svc`** (R-01) | Sharded by `vehicleId` |
| `ride-svc` *(new, R-01; expanded v2.2)* | .NET 10 LTS + **MassTransit saga** (PostgreSQL repository) + Quartz.NET clustered scheduler | **Sole authoritative writer of the Mode C Ride Aggregate** across three sub-kinds: `passenger`, `proxy` (booker ≠ rider), `package`. States: `Requested → Matching → Offered(driverId, expiresAt) → Accepted → DriverArrived → InProgress → Completed → PaymentPending → Paid \| CashSettled \| CashOnDeliveryCollected \| Disputed` (P-08); terminal cancels unchanged from v2.1. **Atomic single-winner accept** via conditional SQL update + row version. **Workflow sharded by `rideId`**. **Durable timers** for offer / arrival / no-show / payment / **location-request-expiry (5 min, P-02)** / **OTP-attempt-window** via Quartz cluster. **Outbox** publishes `ride.*`, `location.request.*`, `package.picked_up`, `package.delivered` events; Postgres `LISTEN/NOTIFY` sub-50 ms wake-up. **Proxy booking (P-01, P-05)**: every offer payload carries `is_proxy`, `rider_name`, `rider_phone` (**real number, exposed post-accept — AL-48**); driver UI shows "Third-party" badge; VoIP token binds driver to *rider*, not booker (calls — VoIP or direct dial — always reach the **rider**, never the booker). **Package delivery (P-06, P-07)**: generates two 4-digit OTPs at ride creation, hashes them, returns `pickup_otp` to booker/sender via response, `delivery_otp` to recipient via FCM (or SMS+share-token if unregistered); driver POSTs OTPs at pickup/drop-off; max 5 attempts each → admin queue. **Proof-photo upload** (P-10) when recipient unavailable. Consumes `share.revoked`, `wallet.debited`, `payment.settled`, `veh/{vehicleId}/status` (LWT). Owns ride command log + idempotency replay | Stateless, workflow shard by `rideId` hash |
| `dispatch-svc` | .NET 10 LTS + Redis (Lua reservation + presence/offer state, **15 s offer key via `PEXPIRE` + keyspace-notification reassignment**) + Quartz.NET (scheduled rides + offer backstop) | **Mode C candidate generation, scoring, and offer dispatch only** — ride state owned by `ride-svc`. Candidate index built from Redis `geo:drivers:available:{type}:{cell}` (D-06: H3 res-5 coarse pre-filter, then exact `ST_DWithin` on `dispatch.driver_presence`). **Phase 1 = sequential matching** (R-12): top-scored candidate reserved atomically via Redis Lua + Postgres `UNIQUE(driver_id) WHERE status IN ('OFFERED','ACCEPTED')` (R-10). **Versioned weighted scoring** persisted to `dispatch.candidate_scores` with `dispatch_algorithm_version` (R-11). **Pre-dispatch wallet gate**: reads `wallet:bal:{driverId}` Redis cache (5 s TTL); first trip of day always allowed; 2nd+ refused if balance < daily-fee. **Job Board**: PostGIS `ST_DWithin(pickup, driver_home, 30 km)` on `dispatch.driver_presence`; **Driver Level System**: L1 = no Job Board / scheduled rides; **cancellation penalty Rs 50** emitted as outbox event for cross-trip settlement (D-05); **no-show detection** durable timer. **Directional Travel filter (DT-01..DT-08)**: per-driver Destination Filter stored in Redis `driver:directional:{driverId}` + Postgres `dispatch.directional_filters`; applied as an extra candidate predicate *after* all hard eligibility gates (bearing-alignment ≤ θ_max, pickup detour ≤ detour_max, drop-off must make progress toward destination — all admin-configurable); admin-configurable daily-use limit (default 2) and max duration (default 2 h, Quartz durable expiry); emits `directional.cleared`; never relaxes eligibility, only narrows the candidate set. Consumes `reputation-svc.block_status` and EMQX LWT (`veh/{vehicleId}/status=offline`) to release stale offers (R-15) and clear active directional filters (DT-04) | Stateless workers, sharded by `rideId` for workflow; geocell only used as candidate-search key |
| `fanout-svc` (SignalR) | ASP.NET Core SignalR | WebSocket sessions, geocell groups. **Public map visibility filter**: only **Mode A (buses & trains)** and entitled **Mode B** positions are fanned out to passenger geocell groups; **Mode C vehicles engaged on an active hire are excluded from public groups** (their live position is sent only to the assigned ride's passenger group). **Stale-position suppression**: a vehicle whose latest sample is older than the freshness window, or whose EMQX LWT marks it `status=offline` (GPS off / app offline), is dropped from public groups until live ingest resumes. **Mode B entitlement cache** in Redis `share:{userId}` (SET, pub/sub-invalidated) checked on group-join. Listens to `share.revoked` events and pushes a **directed `RemoveFromGroupAsync`** to the affected passenger immediately (no waiting for next cell crossing) | Stateless w/ sticky sessions; backplane via Redis (dev/MVP) or Redpanda (scale) | |
| `query-svc` | .NET 10 LTS minimal API + gRPC | "Nearby vehicles" (incl. driver profile + ETA), **filterable by transport type including trains**, trip history, vehicle details, **driver earnings aggregation**, **destination-based transport options** (returns Mode A buses **and trains** plus on-demand options serving a passenger's requested destination). **Visibility rules enforced**: excludes **Mode C vehicles on an active hire** and any vehicle whose last position is stale (GPS off / app offline) beyond the freshness window | Stateless, reads Redis + Postgres replica |
| `transit-svc` *(new, AL-18; Phase 1)* | .NET 10 LTS + PostGIS | **GTFS public-transport routing.** Imports an admin-managed GTFS dataset (`transit.*`) and serves `GET /transit/options` returning **all DIRECT bus/train routes** (route number, headsign/description, shape polyline) for a passenger's geo-destination + TRANSIT (≥1 transfer) options — feeds SCR-PA-009 (item 3). Also hosts `GET /geo/parse-maps-link` (resolves short Google Maps URLs → lat/lng for the **Paste-link** input, AL-20). No Google API used. **Full national GTFS feed loaded at launch via SCR-AP-016 (AL-54/AL-55)** — versioned uploads, async validation, atomic staging-swap activation + cache reload, one-click rollback (`transit.gtfs_feed_versions`); **feed refreshes are externally provided GTFS files** entering through the same SCR-AP-016 pipeline — no in-house authoring workstream (AL-56). SCR-PA-009's no-coverage degradation (live buses + private tiers shown, route-matching hidden) is retained as a **safety net** for genuine feed gaps | Stateless, reads Postgres replica |
| `subscription-svc` | .NET 10 LTS | **7-tier daily fee rates** (Mode A = Free, Motorbike Rs 50, Three-wheeler Rs 100, Flex Rs 150, Sedan Rs 200, Mini Van Rs 250, Van Rs 300 — admin-configurable). **First trip free**; idempotent fee deduction before 2nd trip per calendar day per vehicle. Mode B **monthly PLATFORM fee per vehicle ~Rs 300** (first month free); fleet vehicles billed via consolidated invoice (AL-03). **Driver-to-driver credit transfer** processing (by **Driver ID**, **exact value, no commission** — AL-01; debit sender, credit recipient the same amount). **Bulk credit voucher purchase** (Rs 1,000–10,000, **per-tier purchase discount configured in DB** — `billing.voucher_discount_tiers` — applied only at purchase, credited directly to the buyer's wallet). **Mode B passenger subscriptions (Epic 23, AL-23/24/25)**: per-vehicle **access-request accept/reject**; per-subscriber **Paid/Free + monthly fare (overridable)** on a **1st-of-month or join-anniversary cycle**; **subscription payments** (LankaQR deep-link/scan, OnePay, online-transfer-slip → owner-verify, cash → owner-mark-received) routed **to the fleet owner** (pass-through); **unsubscribe** → revocation push + muted-until-owner-deletes (`subscription.*`). No per-trip charging. | Stateless |
| `wallet-svc` | .NET 10 LTS + **OnePay** + **LankaQR** | **Driver wallet** balance management (one wallet per driver — a reselling driver uses the same wallet, **no separate reseller account**, AL-01) on a **double-entry ledger** (`billing.accounts`, `billing.journal_entries`, `billing.journal_postings` — balanced postings idempotent on `Idempotency-Key`). **In-app top-ups via OnePay card / OnePay wallet / LankaQR only — bank transfer removed (AL-05)**; OnePay/LankaQR **gateway settlement reconciliation** via Commercial Bank IPG webhook (exceptions → Finance queue in Admin Portal). **Bulk credit voucher** purchase (per-tier DB discount applied at purchase), balance tracking, and **driver-to-driver credit-transfer ledger (exact value, no commission)**. Publishes `wallet.debited` / `wallet.credited` events that invalidate `dispatch-svc` Redis balance cache | Stateless |
| `fleet-billing-svc` *(Fleet Portal, Phase 1)* | .NET 10 LTS + OnePay/LankaQR | **Fleet wallet** + **monthly per-Mode-B-vehicle invoicing** (Mode A free; **no Mode C**, AL-03); consolidated invoice with per-vehicle breakdown; top-up via card/OnePay/LankaQR. Posts to the same `billing` ledger with `owner_type='fleet'` | Stateless |
| `fare-svc` | .NET 10 LTS + **OnePay** | **Mode C fare** (1st-km + per-km + peak/night surcharges) with **Kalman-filter + accuracy-weighted resample** on raw GPS before distance summation (Phase 1) → OSRM `match` snap-to-road (Phase 3) (E-04), **upfront fare estimation**, **in-app ride payment** with persisted payment state machine `Initiated→Pending→Succeeded/Failed/Retried/FellBackToCash/CashOnDelivery/CashOnDeliveryCollected/QrClaimedByPassenger/DriverConfirmedQR/Overpaid/Refunded/Disputed`. **Driver-QR attestation (AL-47)**: scan-driver-QR payments have no gateway callback — passenger claim (`QrClaimedByPassenger`, optional receipt screenshot) + driver confirm → terminal `DriverConfirmedQR` (settles like cash); unresolved claims → Support/Finance dispute; gateway-verified `Succeeded` is OnePay-only. **Late-callback handler**: provider `Succeeded` arriving *after* `FellBackToCash` transitions to `Overpaid` and queues admin refund (R-19). **Tip capture** (E-10): optional post-trip tip credited directly to driver wallet. **Refund/dispute workflow** (E-05): admin-initiated full/partial reversal via OnePay/LankaQR reverse APIs, ledger-balanced via `payment_refund` journal kind. **Cross-trip cancellation settlement** (D-05). Driver earning posts **only on payment terminal state** (R-05). **Proxy-booking payment routing (P-04)**: `payer_role/payer_user_id` resolved at ride creation — `Cash` ⇒ rider pays driver in cash, `LankaQR`/`OnePay` ⇒ booker is charged regardless of who is at pickup. **Package delivery (P-08)**: same tariff as passenger Mode C; `CashOnDelivery` is a payment-initiated state, transitioning to `CashOnDeliveryCollected` on driver confirmation at delivery; uncollected COD > 24 h → ride to `Disputed` (P-14) | Stateless |
| `notification-svc` | .NET 10 LTS + FCM/APNs + SMS gateway | Push notifications. **Dispatch-offer push uses high-priority paths (E-01)**: Android FCM `priority=high` (bypasses Doze); iOS APNs `apns-priority:10` + `content-available:1` (silent push wakes backgrounded app); 3 s ack-wait → SMS fallback to driver phone. Other notifications via **FCM HTTP v1 batch send** + **APNs HTTP/2** with exponential-backoff worker; SMS via primary (Notify.lk) + secondary (Dialog/Mobitel) for SOS; renders templates from `content-svc` in user's language (Si/Ta/En); interest alerts, access requests, low-balance & daily-fee warnings, session auto-end, scheduled-ride reminders, payment confirmations, **SOS p99 ≤ 5 s**, document-expiry warnings. **Proxy-booking location request (P-02, P-12)**: high-priority FCM data-message `{kind: 'location_request', requestId, bookerName, ttl: 300}` to rider's device; per-booker rate-limit 5/h, 30/day via Redis token bucket; rider `Decline` persisted to `safety.location_request_audit`. **Package recipient notify (P-09, AL-21)**: triggered **when the driver confirms pickup** — if the recipient phone **is registered**, high-priority FCM *"📦 Package on the way — [Driver] · ETA NN min"* that deep-links to the recipient tracking screen (SCR-PA-021); if **not registered**, SMS *"Your package is on the way. Track here: passenger.mageride.lk/track?token=…"* containing a `safety.trip_share_tokens`-scoped link to the no-login web tracking page (delivery OTP shown after token validation) | Stateless, queue-driven |
| `safety-svc` | .NET 10 LTS | SOS for **both passengers and drivers** (location + trip context → SMS via primary + secondary gateway in parallel, **fan-out to admin live-feed WebSocket channel**), **live-trip share token** (bound to tripId, valid until trip end + 1 h, rate-limited 60 req/min, revocable, no historical replay), “Report Vehicle” intake feeding `reputation-svc`, **passenger block-driver** list consulted by `dispatch-svc` and `fanout-svc`, admin review queue | Stateless |
| `fleet-svc` *(Fleet Portal `fleet.mageride.lk`)* | .NET 10 LTS | **Phase 1 (AL-03).** Fleet organisation CRUD with **Verification-Officer approval gate** before activation; **org-scoped Owner/Manager/Viewer sub-roles** (provisioned by the Fleet Owner); Mode A/Mode B vehicle onboarding (single + bulk CSV) reusing the Epic-2 approval workflow; vehicle↔driver assignment/revocation; ST-901 binding + auto-session config; scheduling & schedule-not-started alarms (push to assigned-driver apps); per-fleet live map & per-vehicle analytics (own-org **row-level security**). Route-deviation/geofence alerts remain **Phase 3**. Backs the Fleet Portal | Stateless |
| `support-svc` | .NET 10 LTS | **In-app FAQ** article management, **support ticket** creation (with trip ID / screenshot attachment), ticket status tracking, admin ticket queue and resolution | Stateless |
| `admin-bff` *(Admin Portal `admin.mageride.lk`)* | .NET 10 LTS + React/Next.js + Tailwind CSS (AL-52) | **The single back-office BFF for all six internal roles** (Admin, Super Admin, Verification Officer, Support/CSR, Finance, Auditor) with **deny-by-default RBAC + role-scoped menus** (AL-02/AL-06; **no MFA — removed by AL-37**) and an audit interceptor on every mutation (D-35). Functions: **onboarding/verification approval** (incl. **fleet-org approval**, AL-03), vehicle/driver suspend, **train (Mode A) registration (admin-only)**, **GTFS Dataset Manager (SCR-AP-016, AL-54)** — full-feed upload/validate/preview/activate/rollback proxied to `transit-svc`, fare-tariff & daily-fee config, **bulk-voucher discount-tier configuration**, **finance & payment-gateway reconciliation / wallet reversals / refunds**, support tickets, vehicle-report review, broadcast announcements, **RBAC user/role provisioning (Super Admin)**, audit-log views (Auditor read-only). **PDPA workflow (E-06)**: export within 30 d; erasure soft-anonymise within 30 d with statutory hold list. **Refund queue**, **document-expiry queue**, **fraud-review queue** | Stateless |
| `ocr-svc` | .NET 10 LTS + Gemini Flash 3.0 + Tesseract fallback | Document extraction **+ Mode-C onboarding auto-verify** (AL-27): per-doc field extraction drives Verified/Pending → vehicle auto-approve. **Driving-licence extraction also returns `nic_no` + `allowed_vehicle_types`** (AL-29); each extracted field is written to `registry.document_fields` with `confidence` + `source`, and a **doubtful (low-confidence), driver-entered (manual), or plate↔reg-no-mismatch** field sets the owning onboarding step `pending_review` → Verification-Officer queue (AL-29/AL-30). **PII redaction pre-pass** (OpenCV face-blur + Tesseract bounding-box ID-number masking) **before** any data leaves perimeter to Gemini; raw documents in S3 with SSE-KMS, 90-day auto-delete | Stateless, queue-driven |
| `reputation-svc` | .NET 10 LTS | Unifies cancellation, no-show, vehicle-report counters with **rolling-window reset**; exposes `block_status` (`OK / WARN / BOOKING_DISABLED / DELISTED`) and `driver_level` via gRPC. **Anti-collusion detector (E-07)**: flags pair frequency (same `(passenger, driver)` > N rides / 30 d), device-binding cross-check, IP/ASN clustering; emits `fraud.suspected` for admin review | Stateless |
| `voip-svc` | LiveKit SFU + coturn TURN cluster + .NET 10 LTS signalling REST | In-app voice between passenger ↔ driver — the **"Free call"** option (numbers incidentally hidden as a VoIP property; **masking is no longer a requirement — AL-48**). Signalling tokens are tripId-scoped and expire at trip end. **VoIP failure → "Call normally instead?" direct-dial prompt** to the counterparty's real number (D-25 masked-SMS relay removed, AL-48). Recordings off by default (PDPA); on-demand only with admin authorisation | Stateful (SFU pods); HPA on concurrent call count |
| `tile-cdn` | Cloudflare R2 + Cloudflare Worker (or Bunny.net Storage Zone + CDN) | Serves PMTiles for Sri Lanka extract; Worker handles range-byte requests and signed URLs for offline-cache bundles (MAP-09). See §10 for upgrade path beyond free-tier TOS thresholds | Edge-served |
| `nominatim-svc` | Nominatim + osm2pgsql on dedicated Postgres | Self-hosted forward/reverse geocoding for SL extract (~8 GB RAM); refreshed by weekly `osm-pipeline` CronJob | Stateful, 1+1 read replica from Phase 2 |
| `content-svc` | .NET 10 LTS | Localised content store (Si/Ta/En): notification templates, FAQ articles, admin broadcasts, fare-tariff display strings. Versioned with admin approval workflow | Stateless, heavily cached |

**Why this split?**
The hot path (`mqtt-broker → mqtt-bridge → position-processor → fanout-svc`) is isolated and can be scaled independently from the operational/CRUD path. Failure of `subscription-svc` does not affect tracking. Failure of `persistence-writer-svc` does not affect the live map (just delays history writes).

---

## 7. Real-Time Messaging Architecture

### 7.1 Why MQTT for Ingest (Not SignalR)

| Concern | MQTT (EMQX) | SignalR Direct |
|---|---|---|
| Mobile network resilience | Persistent sessions, QoS1, LWT | Reconnect storms |
| Battery & data | ~2-byte keepalives | HTTP/WS overhead |
| Native to GPS hardware | Yes (or via TCP adapter) | No — not feasible |
| Backpressure | Broker buffers + QoS | Lost on disconnect |
| **Verdict** | **Use for ingest** | **Use only for browser/mobile push out** |

### 7.2 Topic Design (EMQX)

```
veh/{vehicleId}/pos/live     # live GPS samples (QoS1, retain=last) — hot path
veh/{vehicleId}/pos/replay   # offline-buffered backlog replay (QoS1, throttled)
veh/{vehicleId}/cmd          # backend → vehicle (e.g. "end session", cadence hint, offer push)
veh/{vehicleId}/status       # online/offline (LWT)
fleet/{operatorId}/+/pos/live  # operator-level wildcard subscriptions
sys/diag/{vehicleId}         # device diagnostics
```

**ACL:** device can only `PUB` to its own `veh/{vehicleId}/*`. **Live vs replay split (R-09)** prevents a reconnect storm — where every vehicle replays its local buffer — from drowning live samples; `mqtt-bridge-svc` consumes both but applies a lower rate-limit and lower priority to `pos/replay`.

### 7.3 Why an Event Stream Between MQTT and Consumers

EMQX → multiple consumers directly is fine for one consumer. But there are **at least 4** independent consumers (live state, persistence, trip SM, anti-spoof, audit, future analytics). You need a **partitioned, durable, replayable log**.

- **Dev / Light replica:** **Redpanda** single broker (RF=1) — single binary, Kafka-API, no JVM, no ZooKeeper. Same `Confluent.Kafka` client code as production.
- **MVP / Pilot:** **Redpanda** 3-node cluster (RF=3). Handles 100k+ msg/s on modest hardware with millisecond p99 produce latency.
- **National scale:** **Redpanda** 5-node cluster (RF=3) with tiered storage to S3/R2 for long-tail retention. Apache Kafka is a drop-in alternative if the operations team prefers it.

Partition key = `vehicleId` → guarantees in-order processing per vehicle.

**EMQX shared subscription for the bridge (E-08).** `mqtt-bridge-svc` is deployed as N replicas all subscribed to `$share/posGroup/veh/+/pos/live` (and a parallel `$share/posGroup/veh/+/pos/replay` consumer group for backlog). EMQX shared subscriptions load-balance message delivery across the group — each message is dispatched to **exactly one** replica — so the bridge horizontally scales without duplicate ingest into the event stream and without coordinator state. Replicas commit Redpanda (Kafka-API) offsets per partition; on replica loss EMQX redistributes ownership within seconds.

### 7.4 Fan-out: Geocell Group Model

**Naive design:** every passenger gets every nearby vehicle update via `GEOSEARCH` per second.
**Cost:** O(passengers × vehicles_in_radius) per second — does not scale.

**Correct design — publish/subscribe by geocell:**

1. World is partitioned into **H3 resolution 7** cells (~5.16 km² hex, edge length ~1.22 km). H3 is preferred over geohash — neighbour math is cleaner and uniform area.
2. `position-processor-svc` publishes each position to a Redpanda topic `cell.{h3index}` (or a Redis stream for short-lived geocell fan-out).
3. `fanout-svc` (SignalR) maintains **SignalR Groups** named `cell:{h3index}`.
4. **Passenger view ~3 km (R-06).** Passenger app on open computes its current H3 res-7 cell + `ring(2)` = **19 cells covering ~2.8–3.3 km radius**, and joins those 19 SignalR groups. (Earlier ADD claim of "res-8 + ring(1) ≈ 3 km" was incorrect — res-8 edge is ~0.46 km, ring(1) covers ~1 km only.) For a wider 5 km view (intercity), use res-7 + ring(3) = 37 cells (~5 km).
5. **Dispatch candidate pre-filter.** `dispatch-svc` indexes drivers in `geo:drivers:available:{type}:{res5Cell}` (H3 res-5, ~252 km² hex) and uses ring(1–2) as a **coarse pre-filter**, then applies exact `ST_DWithin` distance in PostGIS or `GEOSEARCH BYRADIUS` in Redis. The H3 cell alone is **never** treated as a final distance bound.
   - **Directional Travel predicate (DT-02).** For each surviving candidate that has an active Destination Filter (`driver:directional:{driverId}`), `dispatch-svc` additionally requires the ride to head the driver's way: `angularDiff(bearing(driver→destination), bearing(pickup→dropoff)) ≤ θ_max` **and** `dist(pickup, driver) ≤ detour_max` **and** `dist(dropoff, destination) < dist(pickup, destination) − progress_min`. Candidates without an active filter are evaluated normally. This predicate can only *remove* candidates from a round, never widen distance bounds.
6. As the passenger moves and crosses a res-7 cell boundary, the client re-subscribes; SignalR group churn is amortised with a 30 s hysteresis to avoid thrash at boundaries.
7. Updates flow only to interested cells.

**Result:** fan-out cost is O(updates × avg_subscribers_per_cell) — linear in vehicle count, not in user count. This is how Uber and most ride-hailing dispatch systems work.

### 7.5 Adaptive Publish Rate

#### 7.5.1 Phase-Aware GPS Cadence (R-07)

A single fleet-blended cadence over-pays ingest bandwidth for idle vehicles and under-samples vehicles inside an active ride. The driver app receives a server-pushed **cadence hint** on `veh/{vehicleId}/cmd` whenever its workflow phase changes; the device also reverts to a safe default if no hint is received.

| Phase | Trigger | Default cadence | Coalesce rule |
|---|---|---|---|
| Standby — idle | App online, no session | 30–60 s | Skip if Δpos < 25 m |
| Standby — moving | Mode A/B session, speed > 5 km/h | 5–10 s | Skip if Δpos < 25 m |
| Candidate in dispatch pool | `driver:availability` = AVAILABLE, in candidate window | 2–5 s | None (freshness for scoring) |
| Accepted → PickupBound | `ride.state = Accepted` | 2–4 s | None |
| Near-pickup geofence (<300 m) | Computed server-side | 1–2 s | None |
| InProgress | `ride.state = InProgress` | 2–4 s | None |
| InProgress — near-drop geofence | <300 m to drop | 1–2 s | None |
| PaymentPending | `ride.state = PaymentPending` | 30 s | Skip if Δpos < 25 m |

The server publishes the hint as `{"cmd":"setPosRate","intervalMs":2000}` on `veh/{vehicleId}/cmd`. Mobile applies the new cadence within one publish interval. **Server-side freshness rule**: any `dispatch.candidate_scores` evaluation against a driver whose last `pos/live` sample is older than `2 × expectedInterval` excludes that driver from the round.

#### 7.5.2 Server-side Ceilings (D-17)

The client-side cadence is a *cooperative* contract; a misbehaving / compromised app cannot be trusted to honour it. The broker enforces an upper bound:

- **EMQX rule-engine SQL** on the `veh/+/pos/live` topic computes a per-`vehicleId` rolling window. Any client publishing more than **5 messages per second** is rate-limited via the rule's `republish` action being suppressed and a `mqtt.rate_violation` event emitted to `audit.events`. (Ceiling is 5/s because the `Near-geofence` 1 s cadence + retries must not be falsely throttled; previous 2/s ceiling pre-dated phase-aware cadence.)
- **EMQX authorization** further binds each MQTT client to a single `vehicleId` claim in the JWT, so a tampered client cannot impersonate another vehicle.
- `position-processor-svc` acts as the **second-line check**: any tuple breaching `> 10 msg/s` over a 10 s sliding window for the same `vehicleId` is dropped and the vehicle is flagged for `safety-svc` investigation.
- **Replay topic** is rate-limited far more aggressively: max 20 backlog samples/s/vehicle on `veh/{vehicleId}/pos/replay`, with the bridge applying a server-issued back-pressure token.

#### 7.5.3 Reconnect Storm Controls (R-09)

- EMQX **connection rate limit** per listener (e.g. 500 new connections/s/listener) + per ASN guardrail, so a regional 4G outage recovery cannot flood the broker.
- Mobile clients use **jittered exponential reconnect backoff** (1 s–60 s with ±25 % jitter).
- On reconnect, the device opens an idle MQTT session first, drains live samples for 2 s, **then** unlocks replay. Replay messages carry a monotonic `seq` per `vehicleId`; the server discards `seq <= last_seen_seq` to make replay idempotent.

Example EMQX rule (illustrative):

```sql
SELECT
  clientid,
  payload.vehicleId as veh_id,
  count(1) as msg_count
FROM "veh/+/pos/live"
GROUP BY veh_id, TUMBLINGWINDOW(seconds, 1)
HAVING msg_count > 5
```

### 7.6 Event Backbone Sizing (Redpanda)

Redpanda is the event backbone at **every** stage — development (single broker, RF=1), MVP / pilot (3-node, RF=3), national scale (5-node, RF=3 + tiered storage). The choice eliminates the cost of swapping streaming substrates mid-roadmap; the same `Confluent.Kafka` client, the same partition / consumer-group model, and the same `cell.{h3index}` topic naming hold from laptop to multi-region.

| Stage | Brokers | RF | Partitions per topic | Storage | Throughput headroom |
|---|---|---|---|---|---|
| Development / light replica | 1 | 1 | 3 | Local NVMe, 50 GB | ~10k msg/s |
| MVP / Pilot | 3 | 3 | 12 | Local NVMe, 500 GB | ~250k msg/s |
| National scale | 5–7 | 3 | 24–48 | Local NVMe + S3/R2 tiered | 1M+ msg/s |

**Why not Apache Kafka?** Equivalent throughput; higher operational cost (JVM tuning, ZooKeeper or KRaft migration overhead). Redpanda is a drop-in alternative — same client code — if the team standardises on Kafka later.

### 7.7 Hardware GPS Tracker Ingestion Plane (T-01, T-04, T-05)

The hardware ingestion plane is a separate concern from the mobile MQTT ingestion plane because (a) hardware trackers speak proprietary binary protocols over raw TCP/UDP, not MQTT, (b) device populations are heterogeneous and long-lived (firmware seldom updates), and (c) fleets behave differently from individual drivers (bulk operations, 24/7 publish, organisational scoping).

#### 7.7.1 Protocol Coverage

| Family | Transport | Notable Devices | Adapter |
|---|---|---|---|
| **GT06 / GT06N** | TCP, binary framed | Concox GT06, TK103, ST-901 clones | `adapter-gt06` |
| **JT/T 808** (Chinese national standard) | TCP, binary framed | most domestic Chinese trackers, many SL imports | `adapter-jt808` |
| **H02 / H02X** | TCP, ASCII pipe-delimited | older bus trackers | `adapter-h02` |
| **NMEA-over-MQTT** | MQTT (native) | modern industrial trackers (Teltonika, Queclink newer FW) | direct to EMQX (no adapter; ACL-bound) |
| **Generic UDP-NMEA** | UDP | low-cost asset trackers | `adapter-nmea-udp` |

Each adapter exists as an independent StatefulSet (or Deployment for stateless UDP) so that protocol churn does not destabilise the others. A new protocol = a new adapter, not a code change to an existing one.

#### 7.7.2 Ingest Topology

```
                    Internet
                       │
              ┌────────┴────────┐
              │  L4 Anycast LB   │   (HAProxy + Keepalived Phase 1; AWS NLB / DOKS LB Phase 2+)
              └────────┬────────┘
                       │  per-protocol port (5023 GT06, 5024 JT808, 5025 H02, 5026 UDP-NMEA)
       ┌───────────────┼───────────────┐
       ▼               ▼               ▼
  adapter-gt06   adapter-jt808   adapter-h02   …
       │               │               │
       └───────────────┼───────────────┘
                       ▼  authenticated bridge user (mTLS)
              ┌────────────────┐
              │   EMQX cluster │  publishes to veh/{vehicleId}/pos/live  (QoS1)
              └────────┬───────┘                  veh/{vehicleId}/pos/replay (QoS1, low prio)
                       ▼
              ┌────────────────┐
              │ mqtt-bridge    │  shared-subscription consumer group
              └────────┬───────┘
                       ▼
              ┌────────────────┐
              │   Redpanda     │  partitioned by vehicleId
              │  (Kafka-API,   │
              │   single-bin)  │
              │    at scale)   │
              └────────┬───────┘
        ┌──────────────┼──────────────┬───────────────┐
        ▼              ▼              ▼               ▼
  position-       persistence-   trip-state-      fleet-health-
  processor       writer-svc     svc              svc
  (Redis GEO,     (TimescaleDB   (US-9.6,         (rollups,
   anti-spoof)    hypertable)     auto-end)        alerts)
```

**MQTT-capable trackers bypass the adapter entirely** and connect to EMQX directly, authenticating with the same per-device credentials minted by `provisioning-svc`. They are subject to the same ACL (`PUB` only to own `veh/{vehicleId}/*`) as mobile clients.

#### 7.7.3 Device Authentication

| Capability | Credential | Storage on Device |
|---|---|---|
| MQTT + TLS 1.2+ | X.509 client certificate (90-day TTL) | Secure element where available, else flash |
| Raw TCP / UDP (binary) | Per-device pre-shared bearer + IMEI signature (HMAC over IMEI+nonce) | Flash; secret rotated by downlink command on a schedule |

The adapter validates the incoming frame's IMEI against `provisioning-svc`'s Redis cache on every connection (and re-validates every 5 minutes on long-lived sockets). On revocation event, the adapter receives a pub/sub message and force-closes any matching socket within 1 s.

#### 7.7.4 Offline / Batch-Sync Replay (US-3.10, US-3.11)

A tracker losing GSM coverage buffers samples to internal flash (most GT06/JT808 devices have 50k-sample on-board ring buffers). On reconnect, the device emits a burst of samples carrying monotonically increasing `seq` numbers; the adapter routes the burst to `veh/{vehicleId}/pos/replay` (separate topic, separate consumer group, rate-limited 20 msg/s/device).

`position-processor-svc` consumes both `pos/live` and `pos/replay`; for each `vehicleId` it maintains `last_seen_seq` in Redis (`veh:seq:{vehicleId}`) and discards any inbound sample with `seq ≤ last_seen_seq`. This guarantees idempotent replay even when the device, the adapter, or the bridge restarts mid-burst.

Live samples always preempt replay: the bridge consumer applies a 4:1 fair-share scheduler in favour of `pos/live`.

#### 7.7.5 Downlink Commands (US-3.17)

`veh/{vehicleId}/cmd` carries the canonical command envelope `{cmd, args, expiresAt}`. For MQTT-native devices the broker delivers directly. For TCP adapter–served devices, the adapter subscribes to the same topic, translates the envelope into the protocol's native command frame, and writes it back over the open socket. Commands have an `expiresAt`; expired commands are not delivered on reconnect.

Supported commands: `setPosRate`, `pingNow`, `reboot`, `setGeofence`, `revokeCredential`.

#### 7.7.6 Capacity Model (100k Hardware Trackers)

Assumptions: blended publish 0.2 Hz/tracker (mix of moving / idle / standby), 80 B canonical payload, 64 B EMQX overhead.

| Layer | Load @ 100k | Sizing |
|---|---|---|
| TCP adapter sockets | 100k concurrent (15% on legacy proto) | 3 pods × 10k sockets each per protocol family (sticky-hash by IMEI) |
| EMQX MQTT pubs | 20k msg/s sustained, 60k burst | +1 broker node beyond mobile baseline (4-node cluster total) |
| Bridge → Redpanda | 20k msg/s | 2 additional bridge replicas (shared subscription) |
| Position processor | 20k msg/s × 1 ms = 20 CPU-s/s | +2 pods (8 vCPU each) |
| TimescaleDB ingest | Down-sampled to 1 write / 30 s / vehicle = 3.3k inserts/s | Hypertable chunk write absorbs; see §9.5 |

#### 7.7.7 Mode-Specific Routing Rules

- **Mode C (individual ride-hailing).** Dispatch reads position from whichever source is currently authoritative for that vehicleId. The driver-app session is still required to *accept* offers, and **tracker GPS for a Mode C vehicle is ingested only while the vehicle is Online** (the driver has gone online in the app) — pings sent while offline are rejected and never reach the live map or dispatch. If the tracker is offline (>30 s), the dispatcher falls back to phone GPS if the app is reporting, else marks the driver unavailable.
- **Mode A (public bus / fleet).** No driver-app session is required for position to publish; the tracker is the authoritative and only source. For tracker-installed vehicles the **journey starts and ends automatically on ignition** (ACC on → session start; ACC off / idle timeout → session end) — **the mobile app is not needed** (US-3.22).
- **Mode B (private transport / school buses / book-hires).** Like Mode A for tracker-installed vehicles — the **journey auto-starts/ends on ignition**, no app required (US-3.23); sharing-grant entitlement (Epic 4) applies identically to tracker-sourced positions.
- **Fleet scoping.** Every tracker binding carries `fleet_id`; `fleet-health-svc` and the Admin Portal apply row-level security so an operator only sees their own organisation's devices.

---

## 8. Geospatial Architecture — Redis GEO vs PostGIS

| Concern | Redis GEO / H3 | PostGIS |
|---|---|---|
| Last-known position lookups (1 Hz) | ✅ in-memory, sub-ms | ❌ too slow |
| `Nearby vehicles in 3 km` for live map | ✅ via H3 ring + GEOSEARCH | ❌ |
| Routes, stops, geofences, polygons | ❌ no polygon ops | ✅ |
| Historical trip linestrings, distances | ❌ | ✅ ST_MakeLine, ST_Length |
| Map-matching to road network | ❌ | ✅ pgRouting / OSRM |
| Reporting & analytics | ❌ | ✅ |
| Auto-end session geofence (100 m of last end) | ✅ Redis GEORADIUS | ✅ both work |

**Rule:** Redis is the **hot live index**. PostGIS is the **system of record** for everything spatial that survives a process restart.

Use **H3** (Uber's library; .NET binding `H3Lib`) over geohash for cleaner hex neighbours and uniform area cells.

---

## 9. Data Architecture

### 9.1 PostgreSQL Bounded-Context Schemas

> **Conventions (D-38).** All temporal columns are `TIMESTAMPTZ`. Any business-date logic (daily-fee idempotency, peak-hour windows, scheduled-ride cutoffs, monthly subscription) is computed in **`Asia/Colombo`** time and persisted as `DATE` *plus* a `tz_at TIMESTAMPTZ` audit field.

```
iam.users                  -- + emergency_contact_name / emergency_contact_phone (driver SOS, AL-13)
iam.roles                  -- nine canonical roles: driver, passenger, fleet_owner, admin, super_admin, verification_officer, support_csr, finance_officer, auditor (AL-06)
iam.user_roles             -- (user_id, role) — a user may hold several; effective perms = union, deny-by-default
iam.fleet_members          -- (fleet_id, user_id, fleet_role ∈ {owner,manager,viewer}) — org-scoped fleet sub-roles (AL-03)
iam.devices
iam.sessions               -- refresh-token store: (jti, user_id, device_id, app ∈ {passenger,driver}, issued_at, last_used, revoked_at)
                           --   unique partial index (user_id, app) WHERE revoked_at IS NULL
                           --   enforces "single active device PER APP" (AL-08, US-1.12) — Driver + Passenger apps may both be active
iam.otp_attempts           -- token-bucket state for OTP send + verify rate-limit (D-32)
iam.user_prefs             -- language ∈ {si,ta,en} (set in onboarding + Settings only — NOT Edit-profile, AL-26), default_payment ∈ {cash,lankaqr,onepay} (AL-14)
iam.users.operating_city_code  -- launch city chosen at onboarding → config.operating_cities.code; seeds map centroid (AL-27, US-1.3a)
config.operating_cities    -- admin-managed launch cities: (code, name_en/si/ta, centroid_lat/lng, is_active, sort_order); served public via GET /config/cities (AL-27)
iam.saved_addresses        -- (id, user_id, label TEXT, line1 TEXT, line2 TEXT NULL, line3 TEXT NULL, geo POINT, is_home BOOL, is_work BOOL, created_at) — Address Line 1/2/3 + free-text Label captured in the Add-address ModalBottomSheet (AL-26, US-22.2)

registry.vehicles          -- UNIQUE partial index (registration_number) WHERE status IN ('PENDING','APPROVED') (D-37)
                           --   vehicle_type CHECK IN ('motorbike','three_wheeler','flex','sedan','mini_van','van','truck','mini_truck','bus','train') — canonical, "car"→"sedan" (AL-09)
                           --   mode_b_billing ∈ {paid,free} NULL (NULL for Mode A/C); default_monthly_fare_minor INT NULL — "Service payment" Free/Paid + default fare set at onboarding (AL-24, US-13.1b; UI label renamed AL-51 — column name unchanged). 'paid' requires a verified fleet_payout_profiles row (AL-49)
registry.fleet_payout_profiles -- [NEW AL-49] org bank & payout: bank, branch, account_no, account_holder_name, proof_upload_id (statement|passbook 1st page), lankaqr_upload_id (bank-app QR image), status ∈ {pending_verification,verified,rejected}; one verified row per fleet (partial UQ); Verification-Officer approved; passenger pay sheet payTo reads latest verified row
registry.documents         -- [CHANGED AL-50] driver_id now NULLable + fleet_id NULL FK (CHECK one owner) — fleet-uploaded vehicle docs; kinds registration|insurance|revenue_license|permit gate APPROVED (all modes: reg+ins+rev; Mode A: +permit — extends AL-10)
                           --   onboarding_status ∈ {incomplete,approved} — derived: incomplete while any registry.onboarding_steps row ≠ verified/confirmed; approved when all 4 are; only 'approved' Mode-C vehicles are go-live eligible (AL-30, US-2.26/9.6)
registry.onboarding_steps  -- per-step Mode-C onboarding state machine (AL-30, US-2.10a/2.26): (vehicle_id, step ∈ {details,insurance,revenue,photos}, status ∈ {pending_input,verified,pending_review}, fields JSONB, saved_at)
                           --   status='pending_review' when any field is doubtful (low confidence), driver-entered (source='manual'), or — photos step — plate OCR ≠ registration_number; resume opens the first step ≠ 'verified'
registry.permits
registry.shares
registry.operators
registry.fleets            -- fleet organisation: (id, name, business_reg, status ∈ {PENDING,APPROVED,REJECTED}) — Verification-Officer-gated (AL-03)
registry.fleet_vehicles    -- (fleet_id, vehicle_id, mode ∈ {A,B}) — Mode C never fleet-owned (AL-03)
registry.fleet_assignments -- (fleet_id, vehicle_id, driver_id, assigned_at, revoked_at)
registry.driver_profiles   -- name, photo_url, verified_at; nic_no TEXT NULL, allowed_vehicle_types TEXT[] NULL — extracted from the driving-licence scan (AL-29, US-2.4a)
registry.driver_payouts    -- OnePay merchant binding for each approved driver (D-11)
registry.documents         -- driver license / permit / insurance / revenue_license: (id, driver_id, kind, file_url, issued_at, expires_at, status ∈ {VALID,EXPIRING,EXPIRED,REJECTED}) (E-03)
                           --   nightly job: WHERE expires_at < now()+'30d' emits document.expiring; WHERE expires_at < now() flips driver to DISPATCH_SUSPENDED
registry.document_fields   -- per-extracted-field provenance & verification (AL-29, US-2.4a/2.10a): (id, document_id, field_key ∈ {licence_no,licence_expiry,nic_no,allowed_vehicle_types,insurance_expiry,revenue_no,revenue_expiry,reg_no_match,…}, value, confidence NUMERIC NULL, source ∈ {ai,manual}, verify_status ∈ {auto_verified,pending,confirmed}, confirmed_by, confirmed_at)
                           --   source='manual' OR confidence<threshold OR reg_no_match=false ⇒ verify_status='pending' → Verification-Officer queue (SCR-AP-003); officer Confirm/Edit sets 'confirmed' (audited)

prov.device_certs
prov.tracker_bindings       -- IMEI ↔ vehicleId ↔ credential serial ↔ fleet_id; source of truth for IMEI resolution (T-03)
                           --   Redis cache imei:{imei}→vehicleId (24h TTL) invalidated by tracker.bound / tracker.unbound

trips.sessions             -- mode (A/B/C), state (ACTIVE/COMPLETED), destination
                           --   UNIQUE partial index (driver_id) WHERE state='ACTIVE'  (D-03, US-9.6)
                           --   enforces "driver can go live on only one vehicle at a time"
trips.position_samples     -- partitioned monthly
trips.events
trips.ratings              -- 1–5 star rating + optional text per completed trip (US-8.6, US-18.1/18.2)

dispatch.driver_presence   -- online standby drivers (also cached in Redis); has PostGIS POINT for ST_DWithin Job Board query (D-06)
dispatch.scheduled_rides   -- advance bookings (pickup_geo POINT, dropoff_geo POINT, pickup_time, status)
dispatch.offers            -- per-dispatch offer log (driver, ride, sent_at, expires_at, response)
                           --   UNIQUE partial (driver_id) WHERE status IN ('OFFERED','ACCEPTED')  -- R-10 single live offer per driver
dispatch.driver_levels     -- current level per driver (default 3, L1 = loses scheduled ride access), rating_points, level_up_threshold
dispatch.no_show_events    -- audit trail for level decrements
dispatch.job_board_intents -- driver intent submissions for scheduled rides (driverId, rideId, ts)
dispatch.cancellation_penalties -- Rs 50 debit records (passengerId, tripId, driverId_affected, status, applied_trip_id NULL until settled)
                                --   UNIQUE (penalty_id, applied_trip_id) for idempotent cross-trip apply (D-05)
dispatch.directional_filters    -- Directional Travel / Destination Filter (DT-01, DT-03)
                                --   (id, driver_id, destination_geo POINT, set_at, expires_at, cleared_at NULL,
                                --    cleared_reason ∈ {expiry,manual,offline,first_matched_trip}, used_date DATE (Asia/Colombo, D-38))
                                --   UNIQUE partial (driver_id) WHERE cleared_at IS NULL   -- at most one active filter per driver
                                --   daily-use enforcement: COUNT(*) per (driver_id, used_date) ≤ max_uses_per_day (default 2)
                                --   theta_max / detour_max / progress_min / max_duration / clear_on_first_trip resolved from admin config
dispatch.timers                 -- (driver_id, kind='directional_expiry', fire_at, fired_at NULL) — Quartz durable backstop for DT-04
                                --   (shares the durable-timer pattern with rides.timers; Redis key TTL is a fast hint only)

-- Mode C Ride Aggregate (R-01, R-02, R-11, R-14, R-18)
rides.rides                -- (id, passenger_id, client_request_id, vehicle_type, pickup_geo POINT, dropoff_geo POINT,
                           --  state ∈ {Requested,Matching,Offered,Accepted,DriverArrived,InProgress,Completed,
                           --           PaymentPending,Paid,CashSettled,Disputed,
                           --           CancelledByRiderBeforeAccept,CancelledByRiderAfterAccept,CancelledByDriver,
                           --           ExpiredNoDriver,NoShowRider,NoShowDriver},
                           --  accepted_driver_id NULL, accepted_vehicle_id NULL,
                           --  current_offer_id NULL, offer_expires_at TIMESTAMPTZ NULL,
                           --  dispatch_algorithm_version SMALLINT,
                           --  version BIGINT NOT NULL DEFAULT 0,   -- optimistic concurrency
                           --  created_at, updated_at, terminal_at NULL)
                           --  UNIQUE (passenger_id, client_request_id)                                   -- R-18 idempotent request
                           --  UNIQUE partial (passenger_id) WHERE state NOT IN (terminal states)        -- one open ride per rider
                           --  UNIQUE partial (accepted_driver_id) WHERE state IN ('Accepted','DriverArrived','InProgress','PaymentPending')  -- O2 + R-10
rides.transitions          -- (ride_id, from_state, to_state, reason_code, actor_type, actor_id, ts) — immutable audit
rides.command_log          -- (idempotency_key UNIQUE, actor_type, actor_id, ride_id, command, request_hash, response_status, response_body, ts)  -- R-14 replay
rides.outbox               -- transactional outbox; LISTEN/NOTIFY 'ride_outbox' wakes dispatcher (E-09)
rides.timers               -- (ride_id, kind ∈ {offer_expiry,arrival_grace,no_show,payment_pending,offline_grace,
                           --                location_request_expiry,otp_attempt_window,cod_uncollected}, fire_at, fired_at NULL, payload JSONB)
                           --   Quartz.NET clustered job scans WHERE fired_at IS NULL AND fire_at <= now() — durable backstop (R-04)

-- v2.2 additions on rides.rides (proxy booking + package delivery)
--   booker_id            BIGINT NOT NULL REFERENCES iam.users(id)
--   rider_id             BIGINT NULL REFERENCES iam.users(id)        -- NULL if rider is unregistered (proxy/package recipient)
--   rider_phone_hash     BYTEA NULL                                   -- hashed PII for unregistered rider/recipient (P-03)
--   rider_name           TEXT NULL
--   is_proxy             BOOLEAN NOT NULL DEFAULT FALSE               -- (P-01)
--   kind                 SMALLINT NOT NULL DEFAULT 0                  -- 0=passenger, 1=proxy, 2=package (P-06)
--   package_size         CHAR(1) NULL CHECK (package_size IN ('S','M','L'))
--   package_description  TEXT NULL                                    -- short item description shown to driver
--   pickup_otp_hash      BYTEA NULL                                   -- HMAC-SHA256 of 4-digit OTP (P-07)
--   delivery_otp_hash    BYTEA NULL
--   pickup_otp_attempts  SMALLINT NOT NULL DEFAULT 0
--   delivery_otp_attempts SMALLINT NOT NULL DEFAULT 0
--   CHECK (kind <> 2 OR (package_size IS NOT NULL AND pickup_otp_hash IS NOT NULL AND delivery_otp_hash IS NOT NULL))
--   CHECK (is_proxy = FALSE OR (rider_name IS NOT NULL AND (rider_id IS NOT NULL OR rider_phone_hash IS NOT NULL)))

rides.location_requests    -- (id, ride_id NULL, booker_id, rider_phone_hash, rider_id NULL, request_id UUID UNIQUE,
                           --  state ∈ {Pending,Confirmed,Declined,Expired,RiderNotRegistered}, issued_at, ttl_seconds DEFAULT 300,
                           --  resolved_at NULL, resolved_geo POINT NULL, resolved_accuracy_m NUMERIC NULL)
                           --   (P-02, P-03) — short-lived booker→rider GPS round-trip

rides.proof_artifacts      -- (id, ride_id, kind ∈ {delivery_photo,signature,pickup_photo},
                           --  storage_url, sha256 BYTEA, captured_at TIMESTAMPTZ, captured_geo POINT) (P-10)
                           --   PDPA-erasable; 365-day retention by default

dispatch.candidate_scores  -- (id, ride_id, driver_id, score NUMERIC, breakdown JSONB, dispatch_algorithm_version, evaluated_at)
                           --   (R-11) immutable; supports post-hoc dispatch audit and ML training

reputation.counters         -- per-user rolling counters (cancellations_continuous, reports_total, no_shows) (D-04)
reputation.block_states     -- effective state: OK / WARN / BOOKING_DISABLED / DELISTED, expires_at

comms.voip_sessions         -- LiveKit room id, tripId, started_at, ended_at (D-24; masked-SMS-relay flag removed — AL-48)

safety.sos_events          -- {userId, role (passenger|driver), tripId, lat, lng, ts, emergency_contact, sms_status, primary_gateway, secondary_gateway, admin_acked_at}
safety.trip_share_tokens   -- (token, trip_id, scope, expires_at = trip_end + 1h, revoked_at) (D-34)
                           --   scope now includes 'package_recipient' for unregistered recipients (P-09)
safety.vehicle_reports     -- passenger reports; 3+ confirmed reports → auto-delist (fed to reputation-svc)
safety.blocked_drivers     -- passenger ↔ driver block records
safety.location_request_audit -- (id, booker_id, rider_phone_hash, request_id, decision ∈ {Confirmed,Declined,Expired,NotRegistered}, ts) (P-12)

fares.tariffs              -- Mode C (1st-km+per-km+peak/night) per vehicle type. No Mode B fare.
fares.peak_windows         -- admin-configurable peak-hour and night-time multipliers
fares.invoices
fares.payouts
fares.ride_payments        -- payment state machine per trip: state ∈ {Initiated,Pending,Succeeded,Failed,Retried,FellBackToCash,CashOnDelivery,CashOnDeliveryCollected,Overpaid,Refunded,PartiallyRefunded,Disputed}; payer_role ∈ {rider,booker} (P-04); payer_user_id BIGINT (P-04); retry_of_payment_id self-FK (D-10); tip_amount_minor INT DEFAULT 0 (E-10); provider_transaction_id UNIQUE (callback idempotency, R-19)
fares.refunds              -- (id, ride_payment_id, kind ∈ {full,partial,overpaid_reversal}, amount_minor, status ∈ {Requested,Submitted,Succeeded,Failed}, provider_refund_id, reason_code, requested_by, requested_at, settled_at)  -- E-05
fares.driver_earnings      -- aggregated earnings per driver per day

billing.plans                    -- 7-tier daily fee rates (Mode A=Free, Motorbike=50, 3W=100, Flex=150, Sedan=200, MiniVan=250, Van=300; admin-configurable)
billing.daily_fee_charges        -- PK (driver_id, vehicle_id, fee_date) — fee_date is Asia/Colombo DATE (D-13)
billing.monthly_subscriptions    -- Mode B PLATFORM charge to the fleet/owner (~Rs 300, first month free) — distinct from subscriber-facing fare below

-- Mode B passenger subscriptions, access requests & payments (AL-23/24/25; Epic 23). PER VEHICLE.
subscription.access_requests     -- (id, vehicle_id, passenger_id, status ∈ {pending,accepted,rejected}, requested_at, decided_at, decided_by) — per-vehicle request queue (item 8/15)
subscription.grants              -- (id, vehicle_id, passenger_id, status ∈ {active,unsubscribed}, granted_at, expires_at NULL, unsubscribed_at NULL, deleted_at NULL)
                                 --   unsubscribe → status='unsubscribed' + revocation push (D-22); row stays MUTED in fleet portal until owner sets deleted_at (hard-delete) (items 17)
subscription.subscriptions       -- (id, grant_id, vehicle_id, passenger_id, billing ∈ {paid,free}, monthly_fare_minor INT, cycle ∈ {month_first,join_anniversary}, join_day SMALLINT, next_due DATE, status) — per-subscriber fare is OVERRIDABLE (item 16f); cycle = 1st-of-month or join-anniversary (join 5 Jun → due 6 Jul) (item 16g)
subscription.payments            -- (id, subscription_id, vehicle_id, passenger_id, period_month DATE, amount_minor INT, method ∈ {lankaqr_deeplink,lankaqr_scan,onepay,online_transfer,cash}, status ∈ {initiated,pending_verification,paid,failed}, slip_url NULL, gateway_ref NULL, confirmed_by NULL, paid_at NULL, created_at) — routed to FLEET OWNER (pass-through); transfer slip → pending_verification until owner confirm; cash → owner mark-received (items 16d/16e/16f/16h/16i)
billing.wallets                  -- materialised balance view (master = ledger below)
billing.wallet_transactions      -- legacy mirror of journal (read model only)
billing.voucher_discount_tiers   -- (denomination_minor PK, discount_bps, active) — bulk-voucher commission/discount % per VOUCHER VALUE, set per denomination by Admin in the Admin Portal (the reseller's margin; AL-01); e.g. 100000 → 1000 bps = 10% = pay 90,000 get 100,000; applied ONLY at purchase
billing.voucher_purchases        -- (id, driver_id, denomination_minor, discount_bps_applied, paid_minor, credited_minor, gateway_ref) — credits buyer's own wallet immediately; no redeem code
billing.credit_transfers         -- driver↔driver credit transfer records (sender debited 100%, recipient credited 100% — EXACT value, NO commission); by Driver ID; request/approve or direct send

-- Double-entry ledger (D-09)
billing.accounts                 -- (id, owner_type ∈ {driver,fleet,platform,suspense}, owner_id, currency, balance_minor) — no 'reseller' owner_type (AL-01); 'fleet' added for fleet wallet (AL-03)
billing.journal_entries          -- (id, ts, kind ∈ {topup,voucher_purchase,daily_fee,trip_payment,penalty_settle,credit_transfer,adjustment,tip_payout,payment_refund,overpaid_reversal}, idempotency_key UNIQUE, description)
billing.journal_postings         -- (entry_id, account_id, amount_minor signed) — Σ amount_minor per entry MUST = 0 (DB CHECK + trigger)

docs.uploads
docs.extractions

support.tickets              -- support ticket (userId, tripId?, category, description, screenshot_url?, status, admin_response)
support.faq_articles         -- moved to content.faq (see below)

content.notification_templates   -- (template_key, language ∈ {si,ta,en}, subject, body, version, approved_by) (D-26)
content.faq_articles             -- (category, title, body, language, sort_order)
content.broadcasts               -- admin broadcasts (audience filter, message_by_lang JSONB, scheduled_at)

audit.events               -- includes admin mutations from admin-bff interceptor (D-35)

pdpa.requests              -- (id, user_id, kind ∈ {export,erasure}, status ∈ {Received,InProgress,FulfilledHold,Fulfilled,Rejected}, requested_at, due_by (= +30d), fulfilled_at, hold_reason)  -- E-06 PDPA right-to-erasure / data export workflow
pdpa.fulfillment_artifacts -- (request_id, kind ∈ {export_zip,erasure_log}, storage_url, sha256, signed_at)

spatial.routes             -- PostGIS geometry
spatial.stops              -- PostGIS geometry
spatial.geofences          -- PostGIS geometry

-- GTFS public-transport dataset for Mode A direct-route discovery (AL-18; transit-svc; item 3)
transit.gtfs_routes        -- (route_id, agency, route_short_name, route_long_name/headsign description, route_type)
transit.gtfs_trips         -- (trip_id, route_id, service_id, shape_id, direction)
transit.gtfs_stops         -- (stop_id, name, geo POINT) PostGIS
transit.gtfs_stop_times    -- (trip_id, stop_id, stop_sequence, arr, dep)
transit.gtfs_shapes        -- (shape_id, seq, geo POINT) → polyline for map zoom
transit.gtfs_feed_versions -- (feed_version_id, file_name, sha256, feed_info_version, service window, counts JSONB,
                           --  status uploaded/validating/validated/failed/active/archived, validation_report JSONB,
                           --  storage_key, uploaded_by/at, activated_at) — versioned full-feed imports, exactly one active (AL-54)
                           --   GET /transit/options computes DIRECT routes (single route serving both origin & destination corridors) + TRANSIT (≥1 transfer); admin-imported via SCR-AP-016 GTFS Dataset Manager (validated upload → staging load → atomic swap → rollback; AL-54)
```

### 9.2 Partitioning & Retention

- `trips.position_samples` — **monthly partitions**, retained 12 months hot, archived to S3-compatible cold storage (MinIO/Wasabi) after.
- `audit.events` — append-only, 7-year retention if regulated.
- High-frequency raw position data **does not go to Postgres**. Only **1/min sampled** + **trip summary** (start, end, distance, polyline) are persisted operationally. Raw stream goes to **Redpanda with 7-day retention** + optional **ClickHouse / TimescaleDB** for analytics from Phase 3.

### 9.3 Read Scaling

- Postgres primary + 2 read replicas (streaming replication).
- `query-svc` reads from replicas with `read-after-write` consistency only where required.
- **PgBouncer** in transaction mode in front of every service.

### 9.4 Key Redis Data Structures

| Key Pattern | Type | Purpose |
|---|---|---|
| `geo:live` | GEO (sorted set) | Last position of all active vehicles |
| `veh:meta:{vehicleId}` | HASH | Cached vehicle metadata (type, colour, route) |
| `cell:{h3index}` | STREAM | Per-cell position events for fanout consumers |
| `trip:active:{vehicleId}` | HASH | Live trip state (start time, seats, mode) |
| `imei:{imei}` | STRING | IMEI → vehicleId lookup cache |
| `rate:{vehicleId}` | STRING + TTL | Publish rate limiter token bucket |
| `geo:drivers:available:{vehicleType}:{h3Res5Cell}` | GEO | **Dispatch candidate index (R-08)** — only drivers whose `driver:availability.state=AVAILABLE`; written by `position-processor-svc` on phase transition, removed on offline / accept |
| `driver:availability:{driverId}` | HASH | `{state, lastSeen, vehicleType, level, walletOk, currentRideId?}`; TTL 60 s, refreshed on every live GPS sample |
| `driver:directional:{driverId}` | HASH + `PEXPIRE` | **Directional Travel filter (DT-01)** — `{destLat, destLng, expiresAt, usedDate}`; key TTL = remaining duration (fast hint); authoritative expiry in `dispatch.timers` (DT-04). Read during candidate generation to apply the directional predicate (DT-02) |
| `driver:directional:uses:{driverId}:{yyyy-mm-dd}` | STRING + TTL 36 h | Per-day activation counter (Asia/Colombo); `INCR` on each set; enforces `max_uses_per_day` (DT-03) |
| `offer:{rideId}` | HASH + `PEXPIRE` | `{driverId, expiresAt, status}`; key TTL = 15 s — **fast hint**, NOT authoritative; durable backstop in `rides.timers` (R-04) |
| `lock:driver-offer:{driverId}` | STRING via Lua `SET NX PX` | Atomic driver reservation — Lua script combines `SET NX` with insert into `offer:{rideId}`; releases on `OFFERED→DECLINED/EXPIRED` (R-10) |
| `lock:ride:{rideId}` | STRING via `SET NX PX` | Ride workflow single-writer lock for the saga; held for the duration of a state transition |
| `outbox:notify:ride_outbox` | n/a (LISTEN/NOTIFY) | Postgres notification channel; outbox-dispatcher wakes sub-50 ms instead of 250 ms poll (E-09) |
| `pdpa:export:{requestId}` | STRING + TTL 30 d | Pre-signed URL to generated export ZIP (E-06) |

### 9.5 TimescaleDB Hypertable for High-Frequency Telematics (T-06)

Hardware trackers, even with adaptive cadence, produce two orders of magnitude more samples than the daily/operational tables in §9.1 are designed for. Stock PostgreSQL with PostGIS handles this poorly past ~10k inserts/s sustained. The strategy:

1. **Hypertable.** `telemetry.positions` is a TimescaleDB hypertable on a dedicated tablespace, partitioned by `time` (1-day chunks) and space-partitioned by `vehicle_id` hash (16 partitions).

   ```sql
   CREATE TABLE telemetry.positions (
     vehicle_id      UUID         NOT NULL,
     sample_ts       TIMESTAMPTZ  NOT NULL,    -- GNSS UTC
     received_ts     TIMESTAMPTZ  NOT NULL DEFAULT now(),
     seq             BIGINT       NOT NULL,
     lat             DOUBLE PRECISION NOT NULL,
     lng             DOUBLE PRECISION NOT NULL,
     speed_mps       REAL,
     heading_deg     SMALLINT,
     accuracy_m      REAL,
     hdop            REAL,
     sat_count       SMALLINT,
     source          SMALLINT NOT NULL,        -- 0=mobile, 1=gt06, 2=jt808, 3=h02, 4=nmea-mqtt
     fleet_id        UUID,                     -- for RLS scoping
     trip_id         UUID
   );
   SELECT create_hypertable('telemetry.positions', 'sample_ts',
                            partitioning_column => 'vehicle_id',
                            number_partitions   => 16,
                            chunk_time_interval => INTERVAL '1 day');
   CREATE INDEX ON telemetry.positions (vehicle_id, sample_ts DESC);
   CREATE INDEX ON telemetry.positions (fleet_id, sample_ts DESC) WHERE fleet_id IS NOT NULL;
   CREATE UNIQUE INDEX ON telemetry.positions (vehicle_id, seq);  -- replay idempotency (T-05)
   ```

2. **Continuous aggregates** for 1-minute and 5-minute rollups (`avg`, `max_speed`, `distance_m`) materialised every minute; query path for "trip summary" and "fleet daily distance" hits aggregates, not raw rows.

3. **Compression policy** applied to chunks older than 7 days (`segmentby vehicle_id, orderby sample_ts`); typical 10× storage reduction, queryable transparently.

4. **Retention policy.** Hot retention 30 days at full resolution; aggregate retention 12 months; raw chunks dropped after 30 days. Cold export of aggregates to Parquet on R2 monthly.

5. **Write path.** `persistence-writer-svc` batches inserts (1k rows / 500 ms / partition) via `COPY` against the hypertable; throughput tested at >40k rows/s on a 4-vCPU instance.

6. **Read path.** Operational queries ("last position for vehicle X", "trip linestring for trip Y") hit raw chunks within the 30-day window. Reporting queries hit continuous aggregates.

7. **Isolation.** Hypertable lives in `telemetry` schema on a logically separated tablespace (and, from Phase 2, a separate Postgres cluster or Citus distributed node) so a sample-flood does not block transactional writes to `iam`, `registry`, `billing`.

8. **Row-level security** on `fleet_id` enables fleet operators (Epic 13) to query only their own telemetry via `query-svc` without application-side filtering risk.

This complements — does not replace — Redis GEO for the hot 1-Hz "where is the vehicle now" lookups (§8). Redis remains the live index; Timescale is the system of record for historical telemetry.

---

## 10. Physical Deployment Architecture

### 10.1 Development Topology (Single 24 GB / 6 vCPU VPS)

This is the developer/integration environment. All services run as containers on a single Contabo Cloud VPS-30-class box (24 GB RAM, 6 vCPU, 100 GB SSD). Single-point-of-failure is **explicitly accepted** here — this environment is for development and demo, not for paying customers. **Per-container budgets in `lightweight-production-replica.md` (Resource Summary) are canonical for this box** — the uniform "2 GB / 1 vCPU" blocks in the diagram below are historical `stories.txt` defaults (e.g. HAProxy actually runs at 256 MB, Redpanda at 1 GB).

> **Sizing note (v2.3+):** the original `stories.txt` trial assumed a 16 GB VPS-20 for a thin subset of services. The **full v2.4 service set** — `ride-svc`, the hardware-tracker plane (`provisioning-svc`, `tcp-adapter`, `fleet-health-svc`), `voip-svc`, `content-svc`, `reputation-svc`, and the TimescaleDB hypertable — totals ~16.4 GB before OS overhead in `lightweight-production-replica.md`. A **24 GB VPS-30** is therefore the dev/replica baseline; 16 GB remains viable only for the `stories.txt` trial subset shown below.

```
┌──────────────────────── Single VPS (Ubuntu 22.04) ────────────────────────┐
│                                                                            │
│  Docker Compose (or K3s single-node — same manifests)                      │
│                                                                            │
│  ┌──────────────┐  ┌──────────────┐  ┌──────────────┐  ┌─────────────┐   │
│  │  HAProxy     │  │  EMQX (1n)   │  │  Redpanda    │  │  Redis 7    │   │
│  │  2G / 1c     │  │  2G / 1c     │  │  1G / 0.5c   │  │  2G / 1c    │   │
│  └──────┬───────┘  └──────┬───────┘  └──────┬───────┘  └──────┬──────┘   │
│         │                 │                 │                  │          │
│  ┌──────┴────────────┬────┴──────────┬──────┴──────────┐                  │
│  │ signalr fanout    │ position-proc │ trip / persist  │                  │
│  │ 2G / 1c           │ 2G / 1c       │ 2G / 1c (each)  │                  │
│  └───────────────────┴───────────────┴─────────────────┘                  │
│                                                                            │
│  ┌──────────────────────┐                                                  │
│  │ Postgres + PostGIS   │  4 GB / 1 vCPU (per stories.txt)                 │
│  └──────────────────────┘                                                  │
│                                                                            │
│  Backups: nightly pg_dump → S3-compatible (Wasabi/Backblaze)               │
└────────────────────────────────────────────────────────────────────────────┘
```

**Development capacity:** ~100 concurrent vehicles, ~1,000 passenger sockets — sufficient for end-to-end testing and pilot demos.

**Mapping to `stories.txt` trial setup:**

| `stories.txt` item | Component | Container size |
|---|---|---|
| (1) SignalR app, Redis backplane | `fanout-svc` | 2 GB / 1 vCPU |
| (2) EMQX MQTT broker | `mqtt-broker` | 2 GB / 1 vCPU |
| (3) SignalR ↔ EMQX bridge | `mqtt-bridge-svc` (split out from SignalR — see §1.2 critique) | 2 GB / 1 vCPU |
| (4) Redis GEO | `redis` | 2 GB / 1 vCPU |
| (5) PostgreSQL + PostGIS | `postgres` | 4 GB / 1 vCPU |
| (4.a) ST-901 TCP ingest adapter | `tcp-adapter-svc` | 2 GB / 1 vCPU |

### 10.2 Production Topology — Initial Launch (10k vehicles / 100k passengers)

This is the **production setup defined in `stories.txt` §production**: SignalR and EMQX behind HAProxy load balancers, two HAProxy containers running Keepalived for HA, and the TCP ingest adapter sitting behind the LB for ST-901-class trackers.

> **Hosting decision (2026-07-05, normative):** this topology deploys to **DigitalOcean Kubernetes (DOKS), Singapore region** — promoted from §19 roadmap guidance to the decided production substrate. **LiveKit + coturn TURN are pinned to the Singapore region** (SL carriers CGNAT heavily; TURN RTT dominates VoIP quality), with a Colombo TURN node evaluated during the pilot. The Contabo EU box serves **only** the dev/testing replica (§10.1 / `lightweight-production-replica.md`).

It is sized for **10,000 concurrent vehicles and 100,000 concurrent passengers** at launch. Additional nodes are added (not new components) as load grows toward the scale-out ceiling described in §10.3.

**Per-component sizing for the 10k / 100k target:**

| Component | Replicas | Per-pod resources | Notes |
|---|---|---|---|
| HAProxy + Keepalived | 2 (active/standby, VRRP) | 2 GB / 1 vCPU | Per `stories.txt` §production. Fronts SignalR (WSS), EMQX (TCP 8883), and TCP-adapter ports |
| EMQX | 2 nodes (cluster) | 4 GB / 2 vCPU | 10k MQTT clients comfortably; one node failure leaves headroom |
| Redpanda | 3 nodes (RF=3) | 2 GB / 1 vCPU | ~10k msg/s steady state — Kafka-API compatible, single binary, no JVM, no ZooKeeper |
| Redis | 3 nodes (Sentinel) | 2 GB / 1 vCPU | Live geo index for 10k vehicles fits in <100 MB |
| `mqtt-bridge-svc` | 2 | 2 GB / 1 vCPU | Subscribes EMQX → Redpanda |
| `position-processor-svc` | 2–3 | 2 GB / 1 vCPU | Partitioned by `vehicleId` |
| `fanout-svc` (SignalR) | 3 | 2 GB / 1 vCPU | ~33k WS sessions/pod; sticky sessions via HAProxy `source` hash |
| `tcp-adapter-svc` (per protocol) | 2 | 2 GB / 1 vCPU | Behind HAProxy TCP frontend (per `stories.txt` 4.a) |
| `query-svc`, `iam-svc`, `registry-svc`, `trip-state-svc`, `persistence-writer-svc` | 2 each | 2 GB / 1 vCPU | Stateless |
| `reputation-svc`, `content-svc` | 2 each | 1 GB / 0.5 vCPU | Stateless, heavily Redis-cached |
| `voip-svc` signalling | 2 | 1 GB / 0.5 vCPU | Stateless REST in front of LiveKit |
| LiveKit SFU | 2 | 4 GB / 2 vCPU | Phase 1 budget = 500 concurrent calls |
| coturn TURN | 2 | 2 GB / 1 vCPU | UDP relay; bandwidth dominates cost — see §16 |
| `nominatim-svc` | 1 (+1 read replica from Phase 2) | **8 GB / 2 vCPU** | Sri Lanka OSM extract fits in RAM |
| `tile-cdn` | n/a (Cloudflare R2 + Worker) | n/a | PMTiles bucket ~3–5 GB for SL vector tiles |
| PostgreSQL (Patroni) | 1 primary + 2 replicas | 8 GB / 2 vCPU | PgBouncer sidecar |

**Scale-out plan — add nodes when these signals fire:**

| Trigger | Action |
|---|---|
| EMQX CPU > 60% sustained, or > 8k clients/node | Add EMQX node (cluster auto-rebalances) |
| Redpanda consumer lag > 5 s sustained | Add `position-processor-svc` replicas |
| Fanout-svc CPU > 60% or > 30k WS sessions/pod | Add `fanout-svc` replicas (HPA) |
| Redis memory > 70% or ops/s > 50k | Move from Sentinel to Redis Cluster (3M+3R) |
| Postgres replication lag > 10 s, or replica CPU > 70% | Add Postgres read replica |
| Sustained > 50k vehicles or analytics workload | Scale Redpanda from 3 → 5 brokers; introduce ClickHouse |

This is **the same topology** — only node counts change. No re-architecture is required up to ~30k vehicles / 300k passengers.

**Map / geocoder infrastructure (D-14, D-15).** Tile serving and geocoding are first-class infrastructure components, not afterthoughts:

```
┌──────────────────────────────────────────────────────────────────┐
│  Weekly osm-pipeline CronJob (k8s)                               │
│                                                                  │
│  geofabrik SL extract ──► osm2pgsql ──► tippecanoe ──► PMTiles   │
│            │                                              │      │
│            └────────────► nominatim refresh               │      │
│                                                           ▼      │
│                                          Cloudflare R2 (PMTiles) │
│                                                           │      │
│                                                           ▼      │
│                                          Cloudflare Worker / CDN │
└──────────────────────────────────────────────────────────────────┘
              ▲                                          ▲
              │                                          │
   Mobile apps & admin BFF ◄────── HTTPS range bytes ────┘
   call nominatim-svc:8088
```

- **Tile bucket**: ~3–5 GB PMTiles for Sri Lanka at zoom 0–14. Larger detail tiles built on-demand from Postgres extract.
- **Update cadence**: weekly diff-based update keeps OSM data fresh without full rebuild.
- **TOS posture**: see §16.2 / §18.1 for Cloudflare free-tier limit and Bunny.net fallback.

### 10.3 National-Scale Topology (Kubernetes, Multi-AZ)
                │     Cloudflare / Cloud LB (TLS)      │
                └──────────────┬───────────────────────┘
                               │
              ┌────────────────┼────────────────┐
              ▼                ▼                ▼
        ┌──────────┐     ┌──────────┐     ┌──────────┐
        │ HAProxy  │◄────┤ HAProxy  │     │ HAProxy  │  Keepalived VRRP
        └────┬─────┘     └────┬─────┘     └────┬─────┘
             │                │                │
       ┌─────┴──────┬─────────┴───────┬────────┴──────┐
       ▼            ▼                 ▼               ▼
  ┌─────────┐ ┌──────────┐     ┌───────────┐   ┌─────────────┐
  │ EMQX-1  │ │ EMQX-2   │     │ Fanout x3 │   │ API/Query x3│
  └────┬────┘ └────┬─────┘     └─────┬─────┘   └──────┬──────┘
       └──────┬────┘                 │                │
              ▼                      ▼                ▼
        ┌───────────┐         ┌────────────┐   ┌──────────────┐
        │ Redpanda  │◄───────►│ Redis (3M+ │   │ Postgres HA  │
        │  3 nodes  │         │ 3R Cluster)│   │ Patroni 1P+2R│
        └───────────┘         └────────────┘   └──────────────┘
```

### 10.3 National-Scale Topology (Kubernetes, Multi-AZ)

```
  Internet ──► Cloud LB (Anycast, TLS) ──► WAF ──► K8s Ingress (Envoy)
                                                          │
  ┌───────────────────────────────────────────────────────┼─────────┐
  │                    K8s Cluster (multi-AZ)             │         │
  │                                                       │         │
  │  Namespace: edge         Namespace: ingest     Namespace: stream│
  │  ┌──────────────┐        ┌───────────────┐    ┌──────────────┐  │
  │  │ fanout-svc   │        │ EMQX 5 nodes  │    │ Redpanda 5n  │  │
  │  │ HPA 5–50 pods│        │ TCP adapters  │    │ NVMe, RF=3   │  │
  │  └──────────────┘        └───────────────┘    └──────────────┘  │
  │                                                                  │
  │  Namespace: hot-state              Namespace: domain-services    │
  │  ┌────────────────────┐            ┌───────────────────────┐     │
  │  │ Redis Enterprise / │            │ iam, registry, trip,  │     │
  │  │ Dragonfly cluster  │            │ subscription, fare,   │     │
  │  │ (sharded, AZ-aware)│            │ ocr, notification     │     │
  │  └────────────────────┘            └───────────────────────┘     │
  │                                                                  │
  │  Namespace: data                   Namespace: observability      │
  │  ┌────────────────────┐            ┌───────────────────────┐     │
  │  │ Postgres (Patroni  │            │ Prom, Loki, Tempo,    │     │
  │  │ + Citus for shard) │            │ Grafana, Alertmanager │     │
  │  │ ClickHouse (anlyt) │            │ OpenTelemetry Coll.   │     │
  │  └────────────────────┘            └───────────────────────┘     │
  └──────────────────────────────────────────────────────────────────┘
                        │
                        ▼  WAL + WAL-G + object storage
                DR Region (warm replicas, Redpanda MirrorMaker)
```

### 10.4 Container Orchestration: Docker Swarm vs Kubernetes

| Criterion | Docker Swarm | K3s / K8s |
|---|---|---|
| MVP simplicity | ✅ | ❌ (steep learning curve) |
| Stateful workloads (EMQX, Redis, Postgres) | ❌ fragile | ✅ StatefulSets, PVCs |
| Autoscaling (HPA) | ❌ manual | ✅ |
| Secret management integration | ❌ limited | ✅ Vault CSI, sealed-secrets |
| Observability ecosystem | Partial | ✅ full CNCF stack |
| Production at 10+ nodes | ❌ | ✅ |
| **Recommendation** | MVP only (< 5 nodes, < 6 months) | All other stages |

**Use K3s (not full K8s) for MVP** — it installs in 5 minutes on a single VPS, runs Docker Compose workloads with minimal changes, and the manifest set transfers directly to managed K8s (EKS/GKE/Hetzner) when you graduate.

### 10.5 HAProxy vs Kubernetes Ingress Strategy

| Stage | LB Strategy |
|---|---|
| MVP (1 VPS) | HAProxy on host, Keepalived VRRP with spare VPS |
| Pilot (3–10 nodes) | HAProxy containers + Keepalived, Cloudflare proxy for TLS offload |
| Scale (K8s) | Envoy / NGINX Ingress + cloud NLB for MQTT (TCP passthrough, port 8883) |

**Critical:** MQTT (8883) requires **TCP passthrough** — not HTTP ingress. Use a dedicated TCP LoadBalancer service in K8s for the MQTT port. Do not route MQTT through an HTTP ingress controller.

---

## 11. Sequence Flows

### 11.1 Vehicle GPS Update Flow (Mobile-as-Tracker)

```
Driver App      EMQX        mqtt-bridge    Redpanda      Position-Proc    Redis      Fanout-svc    Passenger App
    │             │               │             │               │            │             │              │
    │ MQTT CONNECT│               │             │               │            │             │              │
    │  (X.509)    │               │             │               │            │             │              │
    │────────────►│               │             │               │            │             │              │
    │ PUB         │               │             │               │            │             │              │
    │ veh/V1/pos  │               │             │               │            │             │              │
    │ {lat,lng,t} │               │             │               │            │             │              │
    │────────────►│               │             │               │            │             │              │
    │ PUBACK QoS1 │               │             │               │            │             │              │
    │◄────────────│               │             │               │            │             │              │
    │             │ rule-engine   │             │               │            │             │              │
    │             │──────────────►│ produce     │               │            │             │              │
    │             │               │ (part=hash) │               │            │             │              │
    │             │               │────────────►│               │            │             │              │
    │             │               │             │ consume       │            │             │              │
    │             │               │             │──────────────►│            │             │              │
    │             │               │             │               │ plausibil. │             │              │
    │             │               │             │               │ h3()       │             │              │
    │             │               │             │               │ GEOADD     │             │              │
    │             │               │             │               │───────────►│             │              │
    │             │               │             │               │ XADD       │             │              │
    │             │               │             │               │ cell.H7idx │             │              │
    │             │               │             │               │───────────►│             │              │
    │             │               │             │               │            │ XREAD       │              │
    │             │               │             │               │            │────────────►│              │
    │             │               │             │               │            │             │ SignalR      │
    │             │               │             │               │            │             │ Group(cell)  │
    │             │               │             │               │            │             │ SendAsync    │
    │             │               │             │               │            │             │─────────────►│
```

### 11.2 Passenger Nearby-Bus Query (Initial Map Open)

```
Passenger App     Cloud LB     query-svc          Redis          Postgres (replica)
    │                │               │               │                  │
    │ GET /nearby    │               │               │                  │
    │ ?lat&lng&r     │               │               │                  │
    │───────────────►│               │               │                  │
    │                │ AuthZ (JWT)   │               │                  │
    │                │ h3 ring calc  │               │                  │
    │                │ GEOSEARCH     │               │                  │
    │                │──────────────►│               │                  │
    │                │ vehicleIds    │               │                  │
    │                │◄──────────────│               │                  │
    │                │ MGET meta:*   │               │                  │
    │                │──────────────►│               │                  │
    │                │   (miss)      │               │                  │
    │                │ SELECT FROM   │               │─────────────────►│
    │                │ vehicles      │               │◄─────────────────│
    │                │ MSET cache    │               │                  │
    │                │──────────────►│               │                  │
    │ [{vehId,type,  │               │               │                  │
    │   lat,lng}]    │               │               │                  │
    │◄───────────────│               │               │                  │
    │                │               │               │                  │
    │ WS /hub        │               │               │                  │
    │ JoinGroups     │               │               │                  │
    │ [cell:H1..H7]  │               │               │                  │
    │───────────────►│ fanout-svc    │               │                  │
```

### 11.3 Device Provisioning Flow (Dedicated GPS Tracker)

```
Owner App        registry-svc    provisioning-svc    Vault PKI    EMQX ACL    Redis
    │                  │                 │               │             │          │
    │ Register veh     │                 │               │             │          │
    │ + IMEI           │                 │               │             │          │
    │─────────────────►│                 │               │             │          │
    │                  │ create          │               │             │          │
    │                  │ vehicleId       │               │             │          │
    │                  │ POST /provision │               │             │          │
    │                  │────────────────►│               │             │          │
    │                  │                 │ issue X.509   │             │          │
    │                  │                 │ CN=vehicleId  │             │          │
    │                  │                 │──────────────►│             │          │
    │                  │                 │               │ write ACL   │          │
    │                  │                 │               │ allow       │          │
    │                  │                 │               │ veh/V/*     │          │
    │                  │                 │               │────────────►│          │
    │                  │ {cert,key,      │               │             │          │
    │                  │  brokerURL}     │               │             │          │
    │                  │◄────────────────│               │             │          │
    │                  │ persist binding │               │             │          │
    │                  │ HSET imei→vehId │───────────────────────────────────────►│
    │ display QR /     │                 │               │             │          │
    │ config payload   │                 │               │             │          │
    │◄─────────────────│                 │               │             │          │
```

### 11.4 TCP Tracker (ST-901 / GT06) Adapter Flow

```
ST-901 Tracker    tcp-adapter-svc              Redis            EMQX (internal)
    │                    │                       │                    │
    │ TCP CONNECT 5023   │                       │                    │
    │───────────────────►│                       │                    │
    │ GT06 login pkt     │                       │                    │
    │ {IMEI}             │                       │                    │
    │───────────────────►│                       │                    │
    │                    │ GET imei:{IMEI}        │                    │
    │                    │──────────────────────►│                    │
    │                    │  vehicleId            │                    │
    │                    │◄──────────────────────│                    │
    │                    │ validate HMAC token   │                    │
    │                    │ rate limit check      │                    │
    │ GT06 ACK           │                       │                    │
    │◄───────────────────│                       │                    │
    │ GPS data packet    │                       │                    │
    │───────────────────►│                       │                    │
    │                    │ parse binary          │                    │
    │                    │ MQTT PUB (mTLS)       │                    │
    │                    │ veh/{vehicleId}/pos   │                    │
    │                    │──────────────────────────────────────────►│
```

### 11.5 Wallet Top-Up Flow (in-app card/OnePay/LankaQR only — no bank transfer, AL-05)

```
Driver App (in-app)                         iam-svc          wallet-svc          OnePay/LankaQR     Postgres
    │                                          │                  │                   │                │
    │ Tap "Top Up Wallet" (in-app)             │                  │                   │                │
    │ Select method (Card / OnePay / LankaQR)  │                  │                   │                │
    │ + amount  (bank transfer is NOT a        │                  │                   │                │
    │  top-up method — removed in AL-05)       │                  │                   │                │
    │ Attach API JWT                           │                  │                   │                │
    │─────────────────────────────────────────│─────────────────►│                   │                │
    │                                          │ JWT validated    │                   │                │
    │ POST /wallet/topup                       │                  │                   │                │
    │─────────────────────────────────────────────────────────── ►│                   │                │
    │                                          │                  │ [Card/LankaQR]    │                │
    │                                          │                  │ initiate payment  │                │
    │                                          │                  │─────────────────► │                │
    │ in-app payment sheet / redirect          │                  │                   │                │
    │◄─────────────────────────────────────────────────────────── │                   │                │
    │ ─── driver completes payment ───         │                  │                   │                │
    │                                          │                  │ webhook callback  │                │
    │                                          │                  │◄──────────────────│                │
    │                                          │                  │ credit wallet     │                │
    │                                          │                  │──────────────────────────────────►│
    │ in-app confirmation + receipt            │                  │                   │                │
    │◄─────────────────────────────────────────────────────────── │                   │                │

  [No bank-transfer path — removed in AL-05. All top-ups are instant in-app via OnePay card,
   OnePay wallet, or LankaQR. wallet-svc still reconciles OnePay/LankaQR *gateway settlements*
   via the Commercial Bank IPG webhook; settlement exceptions go to the Finance queue in the
   Admin Portal — but this is not a driver-facing top-up path.]
```

### 11.6 Driver-to-Driver Credit Transfer Flow

```
Requesting Driver  subscription-svc      wallet-svc       notification-svc   Credit-holding Driver   Postgres
(Driver App)                                                                  (Driver App — no portal, AL-01)
    │                    │                   │                   │                   │                  │
    │ Request credit     │                   │                   │                   │                  │
    │ (enter Driver ID   │                   │                   │                   │                  │
    │  or scan QR)       │                   │                   │                   │                  │
    │───────────────────►│                   │                   │                   │                  │
    │                    │ create pending     │                   │                   │                  │
    │                    │ transfer request   │                   │                   │                  │
    │                    │──────────────────────────────────────────────────────────────────────────────►│
    │                    │ notify holder      │                   │                   │                  │
    │                    │──────────────────────────────────────►│                   │                  │
    │                    │                   │                   │ push (name,veh,amt)│                  │
    │                    │                   │                   │──────────────────►│                  │
    │                    │                   │                   │                   │ view request     │
    │                    │                   │                   │                   │ approve          │
    │                    │                   │                   │                   │ POST /transfer   │
    │                    │                   │                   │                   │ /approve         │
    │                    │◄──────────────────────────────────────────────────────────│                  │
    │                    │ check holder      │                   │                   │                  │
    │                    │ balance ≥ amount  │                   │                   │                  │
    │                    │─────────────────►│                   │                   │                  │
    │                    │ balance OK        │                   │                   │                  │
    │                    │◄─────────────────│                   │                   │                  │
    │                    │ debit EXACT amount│                   │                   │                  │
    │                    │ from holder,      │                   │                   │                  │
    │                    │ credit EXACT      │                   │                   │                  │
    │                    │ amount to requester                   │                   │                  │
    │                    │ (NO commission)   │                   │                   │                  │
    │                    │─────────────────►│                   │                   │                  │
    │                    │ record transfer   │                   │                   │                  │
    │                    │ (double-entry D-09)                   │                   │                  │
    │                    │──────────────────────────────────────────────────────────────────────────────►│
    │                    │ notify requester  │                   │                   │                  │
    │                    │──────────────────────────────────────►│                   │                  │
    │ wallet topped up   │                   │                   │                   │                  │
    │ notification       │                   │                   │                   │                  │
    │ (Rs X credited)    │                   │                   │                   │                  │
    │◄───────────────────────────────────────────────────────────│                   │                  │
```

> A driver can also **send credit directly** (without a prior request) by entering the recipient's **Driver ID** and amount. In every driver-to-driver transfer the **exact value** moves — there is **no commission**. The sending driver's margin, if any, came earlier from buying bulk credit at the configured **voucher purchase discount** (`billing.voucher_discount_tiers`).

### 11.7 Cancellation Penalty Cross-Trip Settlement (D-05)

Penalties are not collected immediately (no card-on-file). They settle against the passenger's **next completed trip**, idempotently.

```
On cancellation by passenger after acceptance:
  dispatch-svc inserts dispatch.cancellation_penalties
    (penalty_id, passenger_id, original_trip_id, affected_driver_id, amount=Rs50,
     status='OUTSTANDING', applied_trip_id=NULL)
  → emits cancellation.penalty.recorded event (outbox)

On any future trip completion for that passenger:
  fare-svc.complete_trip(tripId)
    SELECT * FROM dispatch.cancellation_penalties
      WHERE passenger_id=:p AND status='OUTSTANDING' FOR UPDATE SKIP LOCKED
    For each outstanding row:
      INSERT INTO billing.journal_entries (kind='penalty_settle',
         idempotency_key=concat(penalty_id,':',tripId))
      INSERT 2 postings:
         debit  passenger pseudo-account (via fare line item on this trip's invoice)
         credit affected_driver_id wallet account
      UPDATE row SET status='SETTLED', applied_trip_id=:tripId
    UNIQUE(penalty_id, applied_trip_id) prevents double-apply on retry.
  → emits wallet.credited event (invalidates dispatch-svc balance cache).
```

### 11.8 In-App Ride Payment State Machine (D-10)

```
   ┌─────────────┐
   │  Initiated  │  passenger taps "Pay"
   └──────┬──────┘
          ▼
   ┌─────────────┐  fare-svc → OnePay/LankaQR provider
   │   Pending   │  awaits gateway callback (timeout 90 s)
   └──┬───────┬──┘
      │       │
 ok   │       │ provider error / timeout
      ▼       ▼
 ┌────────┐ ┌────────┐
 │Succeeded│ │ Failed │ → passenger may retry → state becomes Retried,
 └────────┘ └───┬────┘   a new payment row is linked via retry_of_payment_id
                │
                │ after N=3 failed retries OR driver overrides
                ▼
         ┌─────────────────┐
         │ FellBackToCash  │  trip remains completed; cash collected offline
         └─────────────────┘
```

State transitions are persisted in `fares.ride_payments`. All states except `Initiated` are durable; replays via the outbox are idempotent on `(trip_id, attempt_no)`.

### 11.9 OnePay Driver-Merchant Onboarding (D-11)

```
Driver        registry-svc       wallet-svc       OnePay merchant
  │  submit         │                  │                  │
  │  vehicle docs   │                  │                  │
  ├────────────────►│                  │                  │
  │                 │  OCR + admin     │                  │
  │                 │  approval        │                  │
  │                 │ (status=APPROVED)│                  │
  │                 ├─────────────────►│ create merchant  │
  │                 │                  │  sub-account     │
  │                 │                  ├─────────────────►│
  │                 │                  │  merchant_id     │
  │                 │                  │◄─────────────────│
  │                 │  persist binding │                  │
  │                 │  in registry.    │                  │
  │                 │  driver_payouts  │                  │
  │  notify driver  │                  │                  │
  │◄────────────────│                  │                  │
```

Without successful merchant binding, `fare-svc` cannot route in-app payments for this driver and falls back to cash by default.

### 11.10 Mode B Share-Revocation Push (D-22)

```
Passenger taps "Unsubscribe" in app
     │
     ▼
registry-svc.revoke_share(user_id, vehicle_id)
     │
     ├─► UPDATE registry.shares SET revoked_at=now() WHERE …
     │
     ├─► PUBLISH share.revoked {user_id, vehicle_id, geocells_to_remove}
     │
     ▼
fanout-svc (any pod with that user's SignalR connection)
     │
     ├─► Redis SREM share:{user_id} {vehicle_id}
     │
     └─► For each geocell currently associated with vehicle_id and that user:
            await groups.RemoveFromGroupAsync(connectionId, "geocell:" + cell)
     │
     ▼
Passenger map immediately stops receiving frames for that vehicle.
```

The push happens **without waiting** for the next geocell crossing — typical removal latency < 200 ms.

### 11.11 Mode C Ride — Atomic Single-Winner Acceptance (R-02, R-13, R-14)

The single-most failure-prone path in a ride-hailing platform is two drivers accepting the same offer. v2.1 enforces atomicity in **both** Redis (fast path) and Postgres (authoritative).

```
Passenger app                                 ride-svc (saga shard by rideId)         dispatch-svc                      Driver app
   │                                                │                                       │                              │
   │  POST /rides/request                           │                                       │                              │
   │  Idempotency-Key: <uuid>                       │                                       │                              │
   │ ─────────────────────────────────────────────► │                                       │                              │
   │                                                │ INSERT rides.command_log              │                              │
   │                                                │   ON CONFLICT (idempotency_key)       │                              │
   │                                                │   DO NOTHING RETURNING id             │                              │
   │                                                │ if conflict → replay stored response  │                              │
   │                                                │ INSERT rides(state='Requested', ...)  │                              │
   │                                                │   ON CONFLICT (passenger_id,          │                              │
   │                                                │     client_request_id) DO NOTHING     │                              │
   │                                                │ INSERT rides.outbox (ride.requested)  │                              │
   │                                                │ NOTIFY ride_outbox                    │                              │
   │ ◄── 202 + rideId ────────────────────────────  │                                       │                              │
   │                                                │  ─── ride.requested ────────────────► │                              │
   │                                                │                                       │ Build candidate set:         │
   │                                                │                                       │   GEO geo:drivers:available  │
   │                                                │                                       │   RING(2) ⇒ ST_DWithin       │
   │                                                │                                       │ Score top-K (versioned)      │
   │                                                │                                       │ INSERT dispatch.candidate_   │
   │                                                │                                       │        scores rows           │
   │                                                │                                       │ Reserve top-1 driver:        │
   │                                                │                                       │   Lua: SET lock:driver-      │
   │                                                │                                       │     offer:{drv} NX PX 15000  │
   │                                                │                                       │ If reservation OK:           │
   │                                                │                                       │   BEGIN                      │
   │                                                │                                       │     INSERT dispatch.offers   │
   │                                                │                                       │       (status='OFFERED')     │
   │                                                │                                       │     UPDATE rides SET         │
   │                                                │                                       │       state='Offered',       │
   │                                                │                                       │       current_offer_id=...,  │
   │                                                │                                       │       offer_expires_at=t+15s,│
   │                                                │                                       │       version=version+1      │
   │                                                │                                       │     WHERE id=rideId          │
   │                                                │                                       │       AND state='Matching'   │
   │                                                │                                       │       AND version=:v         │
   │                                                │                                       │     INSERT rides.outbox      │
   │                                                │                                       │       (offer.created)        │
   │                                                │                                       │   COMMIT                     │
   │                                                │                                       │   NOTIFY ride_outbox         │
   │                                                │                                       │  ──── offer push (FCM hi /   │
   │                                                │                                       │        APNs silent) ───────► │
   │                                                │                                       │                              │
   │                                                │                                       │                              │  Driver taps Accept
   │                                                │                                       │                              │
   │                                                │  POST /rides/{rideId}/offer/{drv}/accept  Idempotency-Key: <uuid>    │
   │                                                │  ◄────────────────────────────────────────────────────────────────── │
   │                                                │                                                                       │
   │                                                │ ATOMIC ACCEPT (authoritative SQL, single statement):                  │
   │                                                │                                                                       │
   │                                                │   UPDATE rides SET                                                    │
   │                                                │       state           = 'Accepted',                                   │
   │                                                │       accepted_driver_id = :driverId,                                 │
   │                                                │       accepted_vehicle_id = :vehicleId,                               │
   │                                                │       version         = version + 1,                                  │
   │                                                │       updated_at      = now()                                         │
   │                                                │   WHERE id = :rideId                                                  │
   │                                                │     AND state IN ('Matching','Offered')                               │
   │                                                │     AND current_offer_id = :offerId                                   │
   │                                                │     AND offer_expires_at > now()                                      │
   │                                                │     AND version = :expectedVersion                                    │
   │                                                │   RETURNING version;                                                  │
   │                                                │                                                                       │
   │                                                │ row_count = 1 → winner; row_count = 0 → 409 Conflict / 410 Expired    │
   │                                                │ Winner: INSERT dispatch.offers UPDATE status='ACCEPTED' (UNIQUE       │
   │                                                │   partial constraint on driver_id rejects any concurrent OFFERED row) │
   │                                                │ Losers: command_log records 409 → driver app shown next offer         │
```

**Directional Travel candidate filter (DT-02, DT-05).** During *candidate set build* (the `RING(2) ⇒ ST_DWithin` step above), after the hard eligibility gates (wallet/daily-fee, Driver Level, vehicle-category, `block_status`, package-size), `dispatch-svc` reads `driver:directional:{driverId}` for each surviving candidate. If a filter is active, the candidate is kept **only if** the ride satisfies the bearing-alignment, pickup-detour, and progress-toward-destination predicate; otherwise it is dropped from this round (no reputation effect). Drivers without an active filter are unaffected. Because the filter is per-driver and only removes candidates, it never blocks the ride from matching another available driver, and the durable backstop (R-04) re-offers to the next candidate exactly as for a decline/expire. The directional decision and computed metrics (bearings, distances) are written to `dispatch.candidate_scores.breakdown.directional` for audit.

**Why both Redis Lua and Postgres atomicity?** Redis Lua is the fast path so a driver app doesn't see a phantom offer that has already been claimed. Postgres is the *authoritative* writer; if Redis is partitioned / flushed, the Postgres `UNIQUE(driver_id) WHERE status IN ('OFFERED','ACCEPTED')` constraint plus the conditional `UPDATE` are the only guarantees that survive. Both are required; neither alone is sufficient.

**Durable backstop (R-04).** The `offer_expires_at` is the source of truth. A Quartz.NET clustered job (`rides.timers.kind='offer_expiry'`) fires within 1 s of expiry independently of any Redis TTL, transitions the ride back to `Matching`, and emits `offer.expired` so `dispatch-svc` re-offers to the next candidate.

### 11.12 Mode C Ride — Cancellation & No-Show Transition Matrix (R-03, R-15, R-16)

`ride-svc` is the **sole writer** of `rides.state`. Every cancellation goes through one of these transitions; nothing else is permitted. All target states are terminal unless marked otherwise. Driver-availability and penalty effects are emitted as outbox events for `dispatch-svc`, `fare-svc`, and `reputation-svc`.

| From state | Actor & trigger | To state | Penalty | Driver availability | Events emitted |
|---|---|---|---|---|---|
| Requested / Matching | Rider taps Cancel | `CancelledByRiderBeforeAccept` | None | n/a | `ride.cancelled` |
| Matching | No candidates after N rounds OR timeout (60 s) | `ExpiredNoDriver` | None | n/a | `ride.expired_no_driver` |
| Offered | Driver declines | back to `Matching` (non-terminal) | None | Released, available again | `offer.declined` |
| Offered | Offer expires (15 s, durable timer) | back to `Matching` (non-terminal) | None | Released | `offer.expired` |
| Accepted | Rider taps Cancel | `CancelledByRiderAfterAccept` | Rs 50 (D-05) | Released, available | `ride.cancelled`, `cancellation.penalty.accrued` |
| Accepted | Driver taps Cancel | `CancelledByDriver` | Reputation hit (counter +1, level-down trigger) | Released, **briefly delisted per reputation rules** | `ride.cancelled`, `reputation.driver_cancelled` |
| Accepted | Driver MQTT LWT → offline > 60 s | `CancelledByDriver` (system) | Same as above | Released | same |
| Accepted | Driver arrives at pickup geofence | `DriverArrived` (non-terminal) | — | — | `ride.driver_arrived` |
| DriverArrived | Rider no-show after 5 min + 2 SMS reminders | `NoShowRider` | Rs 100 (configurable) | Released, available; driver compensation = base fare/2 | `ride.no_show_rider`, `cancellation.penalty.accrued` |
| DriverArrived | Driver MQTT LWT → offline > 120 s | `CancelledByDriver` (system) | Reputation hit | Released | same |
| DriverArrived | Driver taps Start | `InProgress` (non-terminal) | — | — | `ride.started` |
| InProgress | Rider taps Cancel | `CancelledByRiderAfterAccept` | Full fare to driver (D-05 + fare-svc) | Released, available | `ride.cancelled` (in-progress) |
| InProgress | Driver taps Cancel | `CancelledByDriver` | Reputation hit + escalation if frequent | Released | same |
| InProgress | Driver MQTT LWT → offline > 5 min, GPS not advancing | `Disputed` (terminal-with-followup) | Manual review | Released; held for `fraud-review` queue | `ride.disputed` |
| InProgress | Driver taps Complete | `Completed → PaymentPending` (non-terminal) | — | Released *only after* payment terminal | `ride.completed` |

**Driver availability after terminal cancellation** is the responsibility of `dispatch-svc`: on consuming any `ride.cancelled` / `offer.declined` / `offer.expired` event, it (a) decrements `driver:availability:{driverId}.currentRideId`, (b) re-adds to `geo:drivers:available:{type}:{cell}`, (c) releases `lock:driver-offer:{driverId}`. Skipping any of these leaves the driver "ghost-busy" — covered by the `Accepted no-pos > 60s` stuck-state alert (R-20).

### 11.13 Offline Command Replay & Idempotency (R-14, R-17, R-18)

```
Driver app (Accept tap)                                    ride-svc
   │                                                          │
   │  Generate Idempotency-Key = ULID (local, monotonic)      │
   │  Persist to local SQLite outbox (state=PENDING)          │
   │                                                          │
   │  POST /rides/{r}/offer/{d}/accept                        │
   │  Headers: Idempotency-Key: <ulid>                        │
   │ ───────────────────────────────────────────────────────► │
   │                                                          │ INSERT rides.command_log
   │                                                          │   (idempotency_key, request_hash, ride_id, command,
   │                                                          │    actor_id, response_status NULL, response_body NULL)
   │                                                          │   ON CONFLICT (idempotency_key) DO NOTHING RETURNING id
   │                                                          │ If conflict → SELECT response_status, response_body
   │                                                          │   from existing row → return verbatim (replay)
   │                                                          │ Else → execute atomic-accept (§11.11), then UPDATE
   │                                                          │   rides.command_log SET response_status=..., response_body=...
   │   ◄────────────────── 200 OK / 409 Conflict / 410 ────── │
   │  Local SQLite outbox state=ACK; remove                   │
```

**Network-loss case.** If the response is lost, the driver app retries with the same `Idempotency-Key`; the server's `ON CONFLICT` branch replays the exact original response with the same status code. Mobile must persist `Idempotency-Key` before issuing the call.

**GPS local buffer (R-17).** The driver/passenger app's foreground service writes every GPS sample to an Android Room / iOS SQLite ring buffer with `(sequence_no PK monotonic, vehicleId, lat, lng, ts, accuracy, source)`. On reconnect, the app publishes the backlog on `veh/{vehicleId}/pos/replay` ordered by `sequence_no`; `position-processor-svc` discards samples where `sequence_no <= last_seen_seq` per `vehicleId` (Redis `seq:{vehicleId}`, persisted hourly to Postgres). Live samples on `veh/{vehicleId}/pos/live` always take priority.

### 11.14 Late Payment Callback & Refund Workflow (R-19, E-05)

```
Cash fallback already settled                      Provider (OnePay/LankaQR)              Admin
       │                                                    │                                │
       │  ride.payment.state = FellBackToCash               │                                │
       │  (rider paid driver in cash; ride closed)          │                                │
       │                                                    │                                │
       │                          POST /webhooks/onepay/    │                                │
       │                          (late "Succeeded")        │                                │
       │   ◄──────────────────────────────────────────────  │                                │
       │   INSERT INTO fares.ride_payments_callbacks        │                                │
       │     (provider_transaction_id UNIQUE, ...)          │                                │
       │     ON CONFLICT DO NOTHING  ← idempotent           │                                │
       │                                                    │                                │
       │   If current state = FellBackToCash:               │                                │
       │     UPDATE rides SET state='Disputed' is NOT done. │                                │
       │     UPDATE fares.ride_payments SET state='Overpaid'│                                │
       │     INSERT fares.refunds(kind='overpaid_reversal') │                                │
       │     EMIT payment.overpaid → admin queue            │                                │
       │                                                    │                                │
       │                                                    │   Admin reviews → triggers     │
       │                                                    │  ◄───────── POST refund ─────  │
       │   Call OnePay reverse API; update refunds.status   │                                │
       │   Post journal:                                    │                                │
       │     DR platform_account                            │                                │
       │     CR passenger_wallet (or original card path)    │                                │
       │   On success: ride_payments.state='Refunded'       │                                │
       │   Send rider push: "Refund processed: Rs XXX"      │                                │
```

Rider-initiated **disputes** follow the same template but originate from the rider app (`POST /rides/{rideId}/dispute`), produce a `support.tickets` entry, and route to the admin queue; the journal posting is identical.

### 11.15 Proxy Booking — FCM Location Request Round-Trip (P-02, P-03, P-13)

The booker (caller) wants the **rider's** actual GPS as the pickup point, not the booker's best guess. The booker app issues a one-shot location request; the rider's app confirms in-app on a map; the confirmed position arrives at the booker via WebSocket and is auto-populated as `pickup_geo`. The full round-trip is owned by `ride-svc` (writes) and `notification-svc` (FCM out) / `fanout-svc` (WebSocket back). The request is short-lived (5 min, P-02) and rate-limited per booker (P-12).

```
Booker app                       ride-svc                 iam-svc            notification-svc        Rider app          fanout-svc        Booker app (WS)
   │                                │                       │                    │                     │                  │                  │
   │ POST /location-requests        │                       │                    │                     │                  │                  │
   │ Idempotency-Key: <ulid>        │                       │                    │                     │                  │                  │
   │ {bookerId, riderPhone}         │                       │                    │                     │                  │                  │
   ──────────────────────────────►  │                       │                    │                     │                  │                  │
   │                                │ Token-bucket check    │                    │                     │                  │                  │
   │                                │ (5/h, 30/d per       │                    │                     │                  │                  │
   │                                │  booker, P-12)        │                    │                     │                  │                  │
   │                                │ Lookup rider by phone │                    │                     │                  │                  │
   │                                │ ─────────────────────► │                    │                     │                  │                  │
   │                                │  {registered?, userId}│                    │                     │                  │                  │
   │                                │ ◄───────────────────── │                    │                     │                  │                  │
   │                                │                                            │                     │                  │                  │
   │   IF not registered:           │                                            │                     │                  │                  │
   │     INSERT rides.location_requests state='RiderNotRegistered'                │                     │                  │                  │
   │     audit safety.location_request_audit                                       │                     │                  │                  │
   │ ◄── 200 {state:'RiderNotRegistered'} ────────────────────────────────────────────────────────────────────
   │   (booker falls back to type-search or map-pin)                                                       │                  │
   │                                                                                                       │                  │
   │   IF registered:                                                                                      │                  │
   │     BEGIN                                                                                             │                  │
   │       INSERT rides.location_requests (request_id, booker_id, rider_id,                                │                  │
   │         state='Pending', issued_at, ttl_seconds=300)                                                  │                  │
   │       Schedule rides.timers kind='location_request_expiry' fire_at=now()+5min                         │                  │
   │       INSERT rides.outbox (location.request.issued)                                                   │                  │
   │     COMMIT; NOTIFY ride_outbox                                                                        │                  │
   │                                │                                            │                     │                  │                  │
   │ ◄── 202 {requestId, ttl:300} ── │                                            │                     │                  │                  │
   │                                │                                            │                     │                  │                  │
   │  Subscribe WS group:           │                                            │                     │                  │                  │
   │  booker:{bookerId}:loc-req:    │                                            │                     │                  │                  │
   │  {requestId}                   │                                            │                     │                  │                  │
   │ ────────────────────────────────────────────────────────────────────────────► │                  │
   │                                │                                            │                     │                  │                  │
   │  outbox dispatcher                                                                                                       │
   │  ───────── location.request.issued ────────────────────────────►                                                          │
   │                                                                                  │                                     │
   │                                                  notification-svc dispatches FCM data-message HIGH:                       │
   │                                                    {kind:'location_request', requestId, bookerName, ttl:300}                │
   │                                                  ─────────────────────────────────────►                                  │
   │                                                                                  │                                     │
   │                                                                            Rider sees prompt:                              │
   │                                                                            "X wants your pickup location"                  │
   │                                                                            [Share Location] / [Decline]                    │
   │                                                                                  │                                     │
   │                          IF Decline:                                             │                                     │
   │                          POST /location-requests/{requestId}/decline             │                                     │
   │                                                                            ─────► ride-svc:                            │
   │                                                                                     UPDATE rides.location_requests        │
   │                                                                                     SET state='Declined' WHERE             │
   │                                                                                     state='Pending';                       │
   │                                                                                     audit; outbox(location.request.declined)│
   │                                                                                                                              │
   │                          IF Share + Confirm:                                                                                 │
   │                          POST /location-requests/{requestId}/confirm {lat,lng,accuracy}                                     │
   │                                                                                     UPDATE rides.location_requests        │
   │                                                                                     SET state='Confirmed',                 │
   │                                                                                     resolved_geo=POINT(lng,lat),           │
   │                                                                                     resolved_at=now()                       │
   │                                                                                     WHERE state='Pending';                 │
   │                                                                                     outbox(location.request.confirmed)     │
   │                                                                                                                              │
   │  outbox → fanout-svc → booker WS group booker:{bookerId}:loc-req:{requestId}:                                                │
   │                       payload {state,'Confirmed'/'Declined'/'Expired', geo?}                                                 │
   │ ◄────────────────────────────────────────────────────────────────────────────────────────────────
   │  Booker app auto-populates pickup pin (or shows fallback prompt if Declined/Expired)                                        │
```

**Expiry path.** `rides.timers.kind='location_request_expiry'` is the durable backstop. Quartz fires ≤ 1 s after 5-min TTL, transitions `Pending→Expired`, emits the outbox event, and `fanout-svc` notifies the booker WS — the booker sees a "Rider didn't respond — enter pickup another way" prompt without polling. Multiple in-flight requests for the same booker are allowed up to the rate limit, but only the *first* confirmation is honoured per booker session (subsequent confirmations transition to `Expired`).

**Privacy posture.** The rider's GPS, name, and phone are visible to the booker **only** as the pickup point of a ride the rider confirmed; declining a request never leaks the rider's location. Repeated declines from the same rider against the same booker are recorded in `safety.location_request_audit` and feed `reputation-svc` (P-12).

### 11.16 Package Delivery — Pickup/Delivery OTP & Cash-on-Delivery (P-06, P-07, P-08, P-09, P-14)

Package delivery rides traverse the **same** Mode C state machine (§Appendix B.2). The two domain-specific events are `package.picked_up` (replacing/co-firing the geofence-driven `DriverArrived→InProgress`) and `package.delivered` (which fires `Completed`). Both events are gated on OTP verification or proof-photo upload.

```
Sender app                ride-svc            notification-svc       Recipient app/SMS         Driver app
   │                         │                       │                      │                       │
   │ POST /rides/request    │                       │                      │                       │
   │ {kind:'package',       │                       │                      │                       │
   │  pickup, dropoff,       │                       │                      │                       │
   │  recipientPhone, name, │                       │                      │                       │
   │  size, description,    │                       │                      │                       │
   │  paymentMethod}         │                       │                      │                       │
   │ Idempotency-Key:<ulid> │                       │                      │                       │
   ───────────────────────►  │                       │                      │                       │
   │                         │ Generate pickup_otp, │                      │                       │
   │                         │ delivery_otp (4-dig) │                      │                       │
   │                         │ store HMAC hashes    │                      │                       │
   │                         │ in rides.rides       │                      │                       │
   │ ◄ 202 {rideId,         │                       │                      │                       │
   │     pickup_otp:'4829'}─ │                       │                      │                       │
   │  (delivery_otp NOT     │                       │                      │                       │
   │   returned to sender)  │                       │                      │                       │
   │                         │  ── dispatch ─────────────────────────────────────────────► Offer (badged
   │                         │  (§11.11 atomic) with payload {is_package:true, size, description}                      "Package")
   │                         │                                                                          [Accept] [Reject]
   │                         │ ◄ driver accepts; ride.state=Accepted                                  ──────────────────
   │                         │                                                                              │
   │                         │                                                          Driver navigates to pickup
   │                         │                                                                              │
   │                         │ POST /rides/{r}/package/pickup-otp {otp:'4829'}                              │
   │                         │ ◄─────────────────────────────────────────────────────────────
   │                         │ Compare HMAC(otp)==pickup_otp_hash                                            │
   │                         │ IF mismatch: pickup_otp_attempts++; if >=5 → admin queue                     │
   │                         │ IF match (atomic conditional UPDATE on state ∈ Accepted/DriverArrived):       │
   │                         │    state → InProgress; outbox(package.picked_up)                              │
   │                         │    Schedule timer kind='cod_uncollected' fire_at=now()+24h IF paymentMethod=COD│
   │                         │    notification-svc → FCM recipient with {state:'PickedUp', delivery_otp}     │
   │                         │    IF recipient unregistered: SMS via safety.trip_share_tokens link            │
   │ ◄ push: "Package        │                       │  ─── push/SMS ─────►                              │
   │   picked up" ───────── │                       │                      │ (delivery_otp shown   │
   │                         │                       │                      │  to recipient only)   │
   │                         │                                                                              │
   │                         │                                                       Driver navigates to dropoff
   │                         │ POST /rides/{r}/package/delivery-otp {otp:'3157'}                            │
   │                         │   OR POST /rides/{r}/package/proof-photo (multipart, if recipient absent)    │
   │                         │ ◄───────────────────────────────────────────────────────────────────
   │                         │ Verify OTP HMAC OR persist proof_artifact (kind='delivery_photo')           │
   │                         │ state → Completed → PaymentPending                                           │
   │                         │ outbox(package.delivered)                                                    │
   │                         │                                                                              │
   │                         │ fare-svc → final fare; payment routing:                                      │
   │                         │   LankaQR/OnePay → charge booker (payer_role='booker') → §11.8 normal flow    │
   │                         │   COD → ride_payment.state='CashOnDelivery' (waits for driver confirmation)   │
   │                         │                                                                              │
   │                         │ IF COD: driver taps "Cash received" → POST /rides/{r}/cod-collected           │
   │                         │   → ride_payment.state='CashOnDeliveryCollected' → driver earning posted       │
   │                         │   IF timer fires before tap: ride → Disputed (P-14) → admin refund queue       │
   │ ◄ "Package delivered"  │                       │  push: "Delivered"   │                              │
   │  + fare summary       ─ │                       │  ───────────────► │                                     │
```

**OTP security.** OTPs are 4-digit numeric. They are generated server-side, hashed (HMAC-SHA256 with a per-environment pepper) before persistence; the **plaintext leaves the server exactly once** — pickup OTP to the sender via the booking response, delivery OTP via FCM/SMS to the recipient. Driver app submissions are rate-limited (max 5 attempts per OTP, lockout to admin queue thereafter). OTP entropy (10⁴) is acceptable because the verification window is narrow (single ride, server-side rate-limit) and the attacker would need to know the `rideId` (UUID).

**Same fare, same daily fee.** Package deliveries pass through the *exact* fare tariff (`fares.tariffs`) and **count toward the driver's daily-fee trip count** (the first delivery of the day is the first-trip-free; deliveries and passenger rides are interchangeable for fee purposes). No separate billing pipeline.

**Dispatch consideration (P-11).** `dispatch.candidate_scores` includes `package_size_compatible` derived from a static `vehicle_type × package_size` table (e.g., `Motorbike × L = false`). Incompatible candidates are filtered out before offer, which preserves driver autonomy (drivers still see incoming requests with size + description and can reject) while preventing obvious mis-dispatches.

---

## 12. Security Architecture

### 12.1 Identity Model

| Actor | Auth Method | Token Type |
|---|---|---|
| End users (passengers) | Phone OTP via SMS gateway (Notify.lk) — primary; **OTP send rate-limited (Redis token bucket: 60 s resend, 5/h) (D-32)** | JWT RS256, 30 min access / 30d refresh |
| Drivers (mobile — Android) | Phone OTP via SMS gateway (primary) + **Android Keystore** device binding | JWT + device attestation claim |
| Drivers (mobile — iOS) | Phone OTP via SMS gateway (primary) + **iOS Keychain + Secure Enclave** device binding | JWT + device attestation claim |
| Mobile-as-tracker (Android) | `provisioning-svc` minted MQTT JWT | 24h TTL, per-session |
| Mobile-as-tracker (iOS) | `provisioning-svc` minted MQTT JWT | 24h TTL, per-session |
| Hardware tracker (MQTT) | X.509 client cert from internal CA (step-ca/Vault PKI) — **Phase 1** (T-02) | mTLS, **90-day rotation** |
| Hardware tracker (TCP-only) | IMEI + HMAC token, adapter holds mTLS — **Phase 1** (T-02) | Trust boundary at adapter |
| Admin Portal (internal roles 4–9) | **Password or Google Sign-In — no MFA (AL-37)** via shared `iam-svc`; compensating controls = failed-attempt lock-out, session binding, optional IP allow-list. No driver/Phone-OTP login (AL-02/AL-07) | JWT RS256, 30 min access / 30d refresh, `role` claim |
| Fleet Portal (Fleet Owner + sub-users) | **Email+Password / Google / Apple** via shared `iam-svc` (AL-03/AL-07) | JWT RS256, 30 min access / 30d refresh, `fleet_role` claim |
| Internal services | mTLS (Linkerd/Istio service mesh) | SPIFFE/SPIRE identities |

**Token-store details (D-29).** Access tokens are stateless RS256 JWTs signed by `iam-svc` and verifiable against a JWKS endpoint (cached for 15 min at EMQX, gateway, and `fanout-svc` to avoid thundering-herd — **D-21**). Refresh tokens are **opaque** strings; the canonical record lives in `iam.sessions` and is **mirrored in Redis as `refresh:{jti}`** for O(1) revocation. Refresh-token semantics:

- Single-use: every successful refresh rotates `jti` and invalidates the prior token.
- A `new-device` login (different `device_id`) revokes any active refresh for that user on other devices, satisfying URD US-1.11 (“one active device per account”).
- Admin force-logout is an O(1) `DEL refresh:{jti}` and SQL `UPDATE … SET revoked_at=now()`.
- All sensitive endpoints check the cached `refresh-revoked` set in addition to JWT signature/expiry.

**Hardware tracker authentication (T-02, T-08, T-12).** Devices are provisioned by `provisioning-svc` against a private CA (step-ca). MQTT-capable devices receive a per-device X.509 client certificate, CN = `tracker:{imei}`, SAN = vehicleId; rotated every 90 days. Legacy TCP-only devices receive a pre-shared per-device bearer + HMAC-of-IMEI; the adapter binds the live socket to the IMEI claim on every frame and re-validates against the `provisioning-svc` Redis cache every 5 minutes. **Anti-cloning**: if two physically distinct sockets present the same IMEI within a 24 h window, both are force-closed and quarantined until admin review. **Revocation propagation** ≤ 60 s end-to-end via Redis pub/sub `tracker:revoked` channel consumed by EMQX dynamic ACL and every adapter pod.

### 12.2 Transport Security

- **TLS 1.3 everywhere** — no plaintext MQTT (port 1883 disabled)
- EMQX exposes `8883` (mTLS for hardware) and `8084` (WSS with JWT for mobile)
- HAProxy/Envoy terminates HTTPS/WSS; **SNI passthrough** for `8883` to EMQX
- Internal mesh: **Linkerd** (lightweight, MVP-friendly) or Istio (Phase 3)

### 12.3 Authorization

- EMQX ACL: device can `PUB veh/{ownVehicleId}/pos` only — no cross-vehicle writes
- API: claims-based checks in `iam-svc`; OPA (Open Policy Agent) for complex policies at scale
- **No subscription/premium tier for passengers** — Passenger App is fully free; live map of public vehicles (Mode A) is unauthenticated-allowed for browsing, authenticated for booking
- **Mode B (Private Transport) vehicle visibility** gated by sharing-grant lookup at SignalR group join time — `fanout-svc` calls `registry-svc` to validate the user has an active grant for the vehicle in the cell before adding them to the cell group's private overlay
- **Drivers at Level 1** (Driver Level System) cannot accept scheduled rides from the Job Board — `dispatch-svc` checks `dispatch.driver_levels` before offering scheduled rides. **Not a permanent ban** — drivers can still operate in immediate dispatch mode.

### 12.4 Secrets Management

- **HashiCorp Vault** (self-hosted) or `sealed-secrets` (K8s MVP)
- No secrets in environment variables, Docker files, or Git repositories
- Database credentials via **Vault dynamic secrets** (auto-rotated, short-lived)
- Certificate rotation automated via Vault PKI + CRL/OCSP

### 12.5 PII & OCR Pipeline (Gemini Flash 3.0 Risk)

- **Mode-C onboarding auto-verify (AL-27, Change 6/22)**: Gemini **Flash 3.0** extracts per-document fields that drive a Verified/Pending verdict — **insurance**(expiry) · **revenue licence**(no + expiry) · **front/back photos**(plate == entered Reg No) · **vehicle details**(entered). When **all four are Verified** `registry-svc` auto-transitions the vehicle `PENDING→APPROVED` **with no Verification Officer step**; any Pending doc routes to the Verification Officer queue. The same redaction pass below runs first.
- **Pre-LLM redaction pass (D-36)**: every uploaded document goes through an in-perimeter pipeline **before** Gemini is invoked:
  - **OpenCV face-blur** for any detected face region.
  - **Tesseract** is run to obtain bounding boxes for the regex-detected ID number on the document (NIC / driving licence number). The pixels in those boxes are blacked out.
  - Only the redacted image is sent to Gemini for structured-field extraction. Raw image stays in our perimeter.
- Document processing log: hash + policy version + redaction-pass version stored per extraction.
- **Fallback OCR path**: Tesseract (free, on-prem) or Azure Document Intelligence (regulated).
- Raw documents in **S3-compatible bucket with SSE-KMS**, signed-URL access only.
- 90-day raw retention then automated redaction.
- Privacy impact assessment required before production rollout.

### 12.6 Security Threat Matrix (OWASP-aligned)

| Threat | OWASP | Mitigation |
|---|---|---|
| Spoofed GPS positions | A01, A07 | mTLS device identity + plausibility filter using **per-vehicle-type max speeds** (Bus 120, Sedan 180, Mini Van 140, Van 130, Flex 200, Three-wheeler 80, Motorbike 180 km/h) **(D-18)**; jump < 1 km/s; samples with reported accuracy circle > 200 m are discarded |
| Replay of MQTT messages | A02 | Monotonic timestamp in payload; processor rejects msgs older than last seen per vehicleId |
| Misbehaving client publishing above the per-vehicle ceiling (> 5 msg/s) | A05 | **EMQX rule-engine rate-limit enforced server-side** (5 msg/s, §7.5.2); `position-processor-svc` second-line check at 10 msg/s (see §7.5, D-17) |
| Account takeover | A07 | MFA for drivers (SMS OTP with 60 s resend cooldown, 5/h cap), **Android Keystore** / **iOS Keychain + Secure Enclave** device binding, Play Integrity (Android) / **App Attest** (iOS) |
| Tampered / cracked client APK/IPA | A02, A07 | **YARP gateway middleware enforces Play Integrity (Android) / App Attest (iOS) attestation header (D-30)** on all sensitive endpoints (`/api/payments/**`, `/api/wallet/**`, `/api/dispatch/**`); requests without a valid token are rejected `403`. JWKS keys cached 15 min |
| Outdated client app | A06 | **API gateway checks `X-App-Version` header against a minimum-version table per platform (D-31)**; below-min apps receive `HTTP 426 Upgrade Required` |
| Passenger sees private vehicle | A01 | Sharing grants server-side only; `fanout-svc` validates entitlement on SignalR group join via Redis `share:{userId}` cache (pub/sub-invalidated on revoke) |
| MQTT broker DoS via publish flood | A05 | Per-client publish rate limit (EMQX QoS inflight cap), max payload 1 KB |
| Tracker IMEI cloning | A07 | Bind IMEI to first provisioned cert; alert on conflict; manual re-provision required |
| Insider DB access | A01, A09 | Postgres row-level security on PII tables; Vault ephemeral creds; audit log |
| Daily fee bypass (driver starts 2nd trip without wallet deduction) | A01 | Idempotent fee charge keyed on `(driverId, vehicleId, fee_date)` enforced server-side before 2nd trip dispatch; **first trip is always free**; client cannot suppress the charge |
| Driver online-on-two-vehicles bypass | A01 | **`trip-state-svc` Redis `lock:driver:{driverId}` SETNX + Postgres UNIQUE partial index** on `trips.sessions(driver_id) WHERE state='ACTIVE'` (D-03, US-9.6) |
| Geo data scraping | A05 | Per-user QPS rate limit on query-svc; anomaly detection |
| PII in LLM (OCR pipeline) | A02 | **In-perimeter redaction pass (§12.5) executed before any Gemini call**; fallback on-prem Tesseract |
| Trip-share link abuse | A05, A01 | Token bound to `tripId`, expires at `trip_end + 1 h`, rate-limited 60 req/min, revocable, no historical replay (D-34) |
| Wallet balance manipulation | A01, A08 | All wallet mutations server-side only via `wallet-svc`; **double-entry ledger** with balanced postings; idempotent on client `Idempotency-Key`; audit trail for every balance change |
| Fraudulent wallet top-up (fake bank transfer receipt) | A07 | Bank transfer top-ups held pending until auto-reconciled via Commercial Bank IPG webhook or manually approved by admin within 4 h SLA |
| Credit-transfer / voucher-discount abuse | A01 | Driver-to-driver transfers move **exact value, no commission** (no per-transfer margin to game); **bulk-voucher discount tiers are server-side & admin-configured** (`billing.voucher_discount_tiers`, applied only at purchase); all transfers/purchases server-side via `wallet-svc` double-entry ledger with idempotency + audit log |
| Admin/Fleet Portal XSS/CSRF | A03, A07 | Standard web security: CSRF tokens, Content-Security-Policy headers, input sanitisation, HttpOnly cookies |
| Privileged admin misuse | A01, A09 | `admin-bff` interceptor writes `audit.events` row for every mutation (approve, ban, fare-config, wallet adjust); reviewable from observability stack (D-35) |
| Ride-farming / passenger-driver collusion (E-07) | A07, A04 | `reputation-svc` pair-frequency detector flags when same `(passenger_id, driver_id)` exceeds N rides / 30 d; cross-checks device-binding hashes and IP/ASN clustering; emits `fraud.suspected` to admin queue; auto-suspends both accounts on Tier-2 thresholds |
| Concurrent ride double-acceptance | A04, A08 | Conditional `UPDATE rides ... WHERE state IN ('Matching','Offered') AND offer_expires_at>now() AND version=:v` + Postgres `UNIQUE(driver_id) WHERE status IN ('OFFERED','ACCEPTED')` + Redis Lua reservation (R-02, R-10, §11.11) |
| Replay of mutating ride command | A01, A08 | Mandatory `Idempotency-Key` header; `rides.command_log(idempotency_key UNIQUE)` replays stored response on retry (R-14) |
| Late payment callback after cash fallback (R-19) | A08 | Provider transaction id UNIQUE; `payment.overpaid` reconciliation queue + refund workflow (§11.14) |
| PDPA right-to-erasure / data export non-compliance (E-06) | A01, A09 | `pdpa.requests` workflow enforces 30 d SLA; statutory hold subset (active rides, open disputes, immutable audit) retained; soft-anonymise via reversible key only where compelled (no hard delete of immutable audit trail) |
| Driver document expiry abuse (driving on expired licence) (E-03) | A07 | `registry.documents.expires_at` nightly scan; auto-suspends dispatch on expiry; passenger never receives an offer from a non-compliant driver |

---

## 13. Observability Architecture

### 13.1 Tooling Stack

| Pillar | MVP Tool | Scale Tool |
|---|---|---|
| Metrics | Prometheus + Grafana | Same + Thanos (long-term retention) |
| Logs | Loki + Promtail | Same or OpenSearch |
| Traces | Jaeger | Grafana Tempo |
| Collector | OpenTelemetry Collector | Same |
| Alerting | Alertmanager | Alertmanager + PagerDuty/OpsGenie |
| Synthetic | Custom MQTT probe + Blackbox exporter | Same |
| Real-user | App-side OTLP HTTP counters | Same |
| Dashboards | Grafana | Same |

All .NET services use `prometheus-net`, Serilog (structured JSON), and `OpenTelemetry.NET` SDK. Trace IDs propagate across MQTT payloads (as a header field), stream messages, and HTTP calls.

### 13.2 Golden Signals Per Service

| Service | Key Metrics |
|---|---|
| EMQX | connections, msgs/s in, msgs/s out, dropped, queue depth, auth failures |
| Stream (Redpanda) | consumer lag per partition (alert > 5 s), produce rate, storage, under-replicated partitions |
| Redis | cmds/s, evictions, memory%, slowlog count, replication lag |
| position-processor | events/s consumed, plausibility rejections/s, processing latency p99 |
| fanout-svc | WS sessions, group join rate, send latency p99, send backlog, reconnects/s |
| Postgres | TPS, replication lag, long-running queries, vacuum lag, connection pool saturation |
| End-to-end | Synthetic position latency from probe vehicle → probe passenger (continuous) |

### 13.3 SLOs

| SLO | Target | Burn-Rate Alert |
|---|---|---|
| Position E2E latency **p95 < 5 s, p99 < 8 s** (consistent with NFR-01 and the 4 s moving cadence floor in US-5.5) | 99% of 5-min windows | 14× budget burn over 1 h |
| **SOS dispatch latency** (button-tap → SMS handed to primary gateway) **p99 ≤ 5 s** | 99.9% monthly | Any 5-min window > 5 s p99 pages on-call immediately |
| **Payment callback resolve** (Initiated → terminal state) p95 < 30 s, p99 < 90 s | 99% monthly | 2% budget over 1 h |
| **VoIP call-setup time** (signalling → first audio frame) p95 < 4 s (when Phase 1 voip-svc shipped) | 99% monthly | 5% over 1 h |
| Tracking plane availability | 99.5% monthly | 2% budget consumed in 6 h |
| API (registry, trips) availability | 99.9% monthly | 2% budget consumed in 6 h |
| WebSocket connection success rate | 99% | 5% failure rate over 5 min |
| **Offer push latency** (`ride.requested` → driver device push received-ack) p95 < 2 s, p99 < 4 s | 99% monthly | 5% budget over 1 h |
| **Atomic accept resolution** (driver tap → 200/409 returned) p95 < 300 ms, p99 < 800 ms | 99% monthly | 5% budget over 1 h |

#### 13.3.1 Stuck-State Business SLOs (R-20)

Unlike infrastructure latency SLOs, these track ride workflows that are stuck — a state machine remaining in a non-terminal state past expected window. Any sustained breach pages on-call. All are computed as `count(rides WHERE state=S AND age > T) > 0` rolling 1 min.

| Stuck condition | Threshold | Likely cause | Page |
|---|---|---|---|
| `state='Matching'` age > 60 s | any | Dispatch starvation; candidate pool empty; geo index drift | Yes |
| `state='Offered'` age > 20 s | any | Timer fault — Quartz job not firing offer_expiry | Yes |
| `state='Accepted'` AND `accepted_driver_id` has no live pos for > 60 s | any | Driver app killed / network black-hole | Yes |
| `state='DriverArrived'` age > 10 min | any | Rider no-show timer fault; or app stuck UI | Yes (after 12 min) |
| `state='InProgress'` AND no GPS sample for > 5 min | any | Foreground service killed; recovery via FCM hi-priority + local replay buffer | Yes (after 7 min) |
| `state='Completed' AND payment.state='PaymentPending'` age > 10 min | any | Provider callback stuck; check OnePay webhook health | Yes |
| `payment.state='Overpaid'` count > 0 for > 1 h | any | Late-callback storm; check OnePay reconciliation | Yes |
| `documents.expires_at < now()` AND driver still `dispatch_active` | any | Doc-expiry job not running | Yes |

### 13.4 Alerting Runbooks (Key)

- **Consumer lag > 5 s sustained 2 min:** scale `position-processor` replicas immediately
- **EMQX auth failure rate > 1%:** possible credential spray; trigger security alert
- **Redis evictions > 0:** memory undersized; emergency scale or eviction policy review
- **Postgres replication lag > 30 s:** failover risk; page on-call DBA
- **`rides.timers` backlog > 100 fired_at IS NULL AND fire_at < now()-30s:** Quartz cluster member partitioned or job-store ill; restart scheduler, validate `qrtz_*` tables
- **`outbox` lag > 1 s p95:** LISTEN/NOTIFY listener dead; restart outbox-dispatcher, replay from `last_published_id` watermark
- **`stuck_state_*` alert (per §13.3.1):** run `runbooks/ride-stuck.md` — inspect last `rides.transitions`, check driver MQTT session, manual force-transition only with admin approval (always via `admin-bff`, never raw SQL)

---

## 14. High Availability & Failover

| Component | MVP HA | Production HA |
|---|---|---|
| Load Balancer | 2× HAProxy + Keepalived VRRP | Cloud NLB (anycast) |
| EMQX | Single node (accepted risk) | 3-node cluster, shared subscriptions, Mria RLOG |
| Event Stream | Single Redpanda broker (RF=1) | 3-node Redpanda cluster (RF=3) |
| Redis | Single + AOF persistence | Redis Cluster 3M+3R or Dragonfly 2-node |
| PostgreSQL | Single + daily backup | Patroni + etcd, 1P + 2R, automatic failover, pgBackRest |
| Stateless services | 1 replica | 3+ replicas, PDB (minAvailable=1), anti-affinity rules |
| AZ failure | N/A (single VPS) | 2-AZ K8s, zone-aware routing |
| Region failure | Manual restore from backup | Phase 3: warm DR, Redpanda MirrorMaker, Postgres logical replication |

### 14.1 Graceful Degradation

| Failure | User Impact | System Behaviour |
|---|---|---|
| Redis failure | Live map stale by ≤ 30 s | fanout-svc serves last in-memory buffer; query-svc returns `"limited_live"` flag |
| Stream consumer lag | Position updates delayed | Payload includes `data_age` field; app shows "updating..." indicator |
| Postgres primary failure | No new registrations, trip history unavailable | Tracking continues; Patroni promotes replica within 30 s |
| EMQX node failure | Vehicles on that node reconnect | Load balancer detects TCP failure; clients reconnect to healthy node within 5 s |
| Single fanout-svc pod failure | ~1/N of WebSocket clients reconnect | K8s restarts pod; clients auto-reconnect via SignalR retry; backfill from Redis on reconnect |
| `wallet-svc` unavailable (during dispatch attempt) | Driver may be granted dispatch with a *grace flag*; **first trip of day always allowed** | `dispatch-svc` reads stale `wallet:bal:{driverId}` Redis cache (5 s TTL); if cache and service both unavailable, **first trip is allowed** (Namma Yatri policy), **second trip is blocked with `WALLET_UNREACHABLE` message** and retried with backoff. Eventual reconciliation via outbox. (D-08) |
| `voip-svc` unavailable | In-app voice call fails | App offers **"Call normally instead?"** — direct cellular dial to the counterparty's real number (AL-48; D-25 masked-SMS relay removed). |
| `tile-cdn` (Cloudflare) outage | Map tile load slow | App falls back to last embedded PMTiles bundle (read-only, lower zoom); admin can switch to Bunny.net origin via remote-config. (D-16) |
| `nominatim-svc` down | Search by name unavailable | Fall back to client-side bounding-box browse; ride requests by pin-drop unaffected. |

---

## 15. Disaster Recovery

| Asset | Backup Method | RPO | RTO |
|---|---|---|---|
| PostgreSQL | pgBackRest continuous WAL → S3, daily base backup | 5 min | 30 min |
| Redis | RDB snapshot every 15 min + AOF; rebuildable from stream replay | 0 (ephemeral data) | 10 min |
| Redpanda | Tiered storage to S3 (7-day retention buffer) | 0 | N/A |
| Object storage (docs) | Cross-region bucket replication | 1 h | N/A |
| Vault secrets | Auto-unseal w/ KMS; Vault snapshots to S3 every hour | 1 h | 1 h |
| K8s cluster state | etcd snapshot every 30 min; GitOps via ArgoCD reconstructs | N/A | 1 h |
| Infrastructure | Terraform in Git; full rebuild documented | N/A | 2 h |

**DR Drills:** quarterly. Runbook target: tracking plane restored in < 2 h, full operational plane in < 4 h from clean DR region.

---

## 16. Capacity Planning & Sizing

### 16.1 Steady-State Ingest

**Blended publish rate (derived from US-5.5 adaptive cadence — D-20):**

| Fleet state | Share | Cadence | Rate (Hz) |
|---|---|---|---|
| Moving (> 5 km/h) | 30% | 4 s | 0.250 |
| Stationary (engine on, idle) | 40% | 10 s | 0.100 |
| Standby (online, parked) | 30% | 60 s | 0.017 |
| **Blended average** |  |  | **0.120 msg/s/vehicle** |

**Initial production target (10k vehicles):**

```
10,000 vehicles × 0.12 msg/s (blended)  = 1,200 msg/s ingest
Burst factor (rush-hour + reconnect storm) = 5×  = 6,000 msg/s burst budget
Payload:     100 B wire avg                  = ~0.12 MB/s steady, 0.6 MB/s burst
Daily raw:   ~10 GB
7-day retain: ~70 GB raw (compressed ~21 GB)
```

**Scale-out ceiling (100k vehicles):**

```
100,000 vehicles × 0.12 msg/s              = 12,000 msg/s sustained
Burst (5×)                                 = 60,000 msg/s
Daily raw:                                 ~100 GB
7-day retain:                              ~700 GB raw (compressed ~210 GB)
```

### 16.2 Infrastructure Sizing Estimates

Container sizes for **Development** match `stories.txt` §trial setup (2 GB / 1 vCPU per service, 4 GB for Postgres). **Production (initial)** is sized for 10k vehicles / 100k passengers \u2014 the `stories.txt` HAProxy LB topology. **Scale-out** is what the same architecture grows into by adding nodes.

| Component | Development (1 VPS, per `stories.txt`) | Production \u2014 Initial Launch (10k veh / 100k pax) | Scale-Out (100k veh / 1M pax) |
|---|---|---|---|
| HAProxy + Keepalived | 1\u00d7 (no HA) | **2\u00d7 active/standby**, 2 GB / 1 vCPU each | Cloud NLB / K8s Ingress |
| EMQX | 1 node, 2 GB / 1 vCPU | 2 nodes, 4 GB / 2 vCPU each | 3\u20135 nodes, 8 GB / 4 vCPU each |
| Event Stream | Redpanda, 1 broker, 1 GB | Redpanda 3-node RF=3, 2 GB each | Redpanda 5-node, 16 GB, NVMe 500 GB + S3 tiered |
| Redis | 1\u00d7 2 GB + AOF | Sentinel 3\u00d7 2 GB | Cluster 3M+3R, 4 GB each |
| `position-processor` | 1\u00d7 2 GB | 2\u20133 pods, 2 GB each | 5\u201320 pods HPA, 2 GB each |
| `fanout-svc` (SignalR) | 1\u00d7 2 GB | 3 pods, 2 GB each | 15\u201350 pods HPA, 4 GB each |
| `tcp-adapter-svc` | 1\u00d7 2 GB (optional) | 2 pods behind HAProxy TCP frontend | HPA per protocol family |
| PostgreSQL | 4 GB, 100 GB SSD | Patroni 1P+2R, 8 GB / 2 vCPU, 500 GB | 32 GB primary, NVMe 2 TB |
| **Hardware trackers (Phase 1, T-10)** | n/a | 5,000 active devices supported — reuses existing EMQX/processor pods | 100,000 active devices: +1 EMQX node, +2 position-processor pods, +3 adapter pods per protocol family |
| **TCP/UDP adapter sockets** | 1× small pod | 2 pods per protocol family, 5k sockets each | 3 StatefulSet pods × 10k sockets each per protocol; sticky-hash by IMEI |
| **TimescaleDB telemetry hypertable** | n/a | Co-located on Patroni cluster, 100 GB tablespace | Dedicated cluster, 16-partition hypertable, NVMe 4 TB, ~6 TB raw / ~600 GB compressed @ 30 d hot |
| Total infra (est., incl. tracker plane at scale) | 1\u00d7 \u20ac15\u201318/mo VPS (Contabo VPS-30, 24 GB) | 3\u20135\u00d7 \u20ac20\u201340/mo VPS (or small K3s) | 20 nodes ~$3,000\u2013$5,000/mo |

### 16.3 Fan-out Cost Model

> **Note (D-40):** the SignalR throughput figures below assume **10–25k WebSocket sends per pod per second** on a 2 GB / 1 vCPU pod (within published ASP.NET Core SignalR benchmarks; higher numbers occasionally cited online require larger pods and Kestrel tuning). Initial launch pod count is derived from the lower bound.

**Initial production (10k vehicles, 100k passengers):**

```
Assume ~30% of vehicles broadcasting at any time = 3,000 active.
Average 5 vehicles per 3 km H3 ring, average 30 passengers per cell.

Per vehicle update:
  → Position processor: 1 Redis GEOADD + 1 Redis XADD     (2 ops)
  → Fanout: broadcast to ~30 subscribers in cell group
  → Total: 3,000 × 0.12 Hz × 30  =  ~10,800 SignalR sends/s

Required fanout pods: 11k sends/s ÷ ~10k sends/pod/s  =  ~1–2 pods
→ Run 3 pods for HA / rolling deploys (matches §10.2 sizing).
```

**Scale-out reference (100k vehicles, 1M passengers):**

```
At 1M concurrent passengers, 30k vehicles active, average 50 vehicles per 3 km cell:

Per vehicle update:
  → Position processor: 1 Redis GEOADD + 1 Redis XADD (2 ops)
  → Fanout: broadcast to avg 100 subscribers in cell group
  → Total: 30,000 × 0.12 Hz × 100 = ~360,000 SignalR sends/s

Required fanout pods: 360k sends/s ÷ ~20k sends/pod/s = ~18 pods.
```

### 16.4 PostgreSQL Write Load

**Initial production (10k vehicles):**

```
Position sampling:  10k vehicles × 1 write/min  = ~167 WPS   (trivial)
Trip events:        10k trips/day × 10 events    = ~1 TPS     (trivial)
User registrations: 1k/day peak                  = ~0.01 TPS  (trivial)
```

**Scale-out ceiling (100k vehicles):**

```
Position sampling:  100k vehicles × 1 write/min = 1,667 WPS   (easy)
Trip events:        100k trips/day × 10 events  = ~12 TPS     (trivial)
User registrations: 10k/day peak                 = ~0.1 TPS    (trivial)
```

---

## 17. MVP vs Scale Guidance

### 17.1 Design from Day One (No Exceptions)

| Decision | Reason |
|---|---|
| Domain-separated Postgres schemas (iam, registry, trips, billing) | Enables future service extraction without data migration |
| Canonical internal event schema (vehicleId, lat, lng, ts, speed, heading, accuracy) | All consumers share the same contract; changing it later is expensive |
| H3 geocell model in position processor | Cannot change partitioning key once clients depend on group names |
| Per-vehicle MQTT topic `veh/{vehicleId}/pos` | ACL and routing is correct from day one |
| Structured logging with trace IDs | Debugging distributed issues without this is painful |
| Device binding (vehicleId ↔ deviceId) in Postgres | Core to security; retrofitting is complex |
| TLS for all external connections | Never ship without it |
| Plausibility filter on position ingestion | GPS spoofing starts at day 1 |

### 17.2 Appropriate for MVP (Single VPS / K3s)

| Component | MVP Choice | Notes |
|---|---|---|
| Event stream | Redpanda (single broker dev) → 3-node RF=3 (MVP/prod) | Same Kafka-API, no re-architecture |
| Container orchestration | K3s single-node | Manifests transfer to full K8s when needed |
| Redis | Single instance with AOF | Sentinel or Cluster when you need HA |
| SignalR backplane | Redis pub/sub | Redpanda backplane when > 5 fanout pods |
| EMQX | Single node | Cluster at 5k+ concurrent devices |
| Monitoring | Prometheus + Grafana (single stack) | Full LGTM stack when you have a second engineer |
| Auth | Phone OTP (SMS gateway, e.g., Notify.lk) + Firebase Auth (Google Sign-In optional) | Keycloak at scale |
| Secrets | Docker secrets / env vars (dev), sealed-secrets (MVP K3s) | Vault when you have > 3 services |

### 17.3 Over-Engineered for MVP (Postpone)

| Component | When to Introduce |
|---|---|
| Kafka / Redpanda | > 20k vehicles or analytics needed |
| Citus (Postgres sharding) | > 5 TB position history |
| Kafka Flink streaming analytics | Phase 3 / national scale |
| mTLS service mesh (Istio/Linkerd) | Pilot phase, > 5 services |
| Multi-AZ Kubernetes | Phase 2 / investor checkpoint |
| ClickHouse for analytics | Phase 3 |
| OSRM / pgRouting for map-matching | Phase 3 (ETA feature) |
| Dragonfly (Redis replacement) | Only if Redis single-node throughput is measured bottleneck |
| OPA (Open Policy Agent) | When policy rules exceed 5 simple checks |

---

## 18. Technology Stack Recommendations

### 18.1 Core Stack

| Layer | Recommended | Alternatives | Notes |
|---|---|---|---|
| Mobile (passenger + driver) | **KMP** shared logic (Kotlin) + **Jetpack Compose** (Android UI) + **SwiftUI** (iOS UI) | Flutter (if team prefers single-UI codebase) | KMP shares 60–70% of code (business logic, networking, data models) while keeping background GPS/MQTT fully native on each platform. 4 app targets: Passenger Android, Passenger iOS, Driver Android, Driver iOS |
| KMP shared module | **Ktor** (HTTP), **kotlinx.serialization** (JSON/CBOR), **kotlinx.coroutines**, **kotlinx-datetime**, **Koin** (DI), **multiplatform-settings** | — | All shared across 4 app targets; compiles to JVM (Android) and native binary (iOS) |
| Map renderer (Android) | Maplibre GL Native (Android SDK) | — | **Free** BSD license; fork of Mapbox GL pre-proprietary. Avoids Google Maps SDK per-load billing |
| Map renderer (iOS) | Maplibre GL Native (iOS SDK) | — | **Free** BSD license; same renderer as Android, native Swift/ObjC API |
| Map renderer (web admin) | Maplibre GL JS | — | **Free** BSD license |
| Map tile format | PMTiles (Protomaps) | MBTiles, TileServer GL | Single static file; no tile server process needed |
| Map tile storage + CDN | **Cloudflare R2 + Cloudflare CDN** (primary) / **Bunny.net Storage Zones + CDN** (fallback) | S3 + CloudFront | R2 has **$0 egress**; Cloudflare free-tier TOS §2.8 restricts non-HTML caching, so at sustained > 50 GB/mo tile traffic upgrade to Cloudflare Pro ($20/mo) or migrate to Bunny.net (~$0.01/GB egress, no equivalent restriction). Either path is < $100/mo vs Google Maps $7k–14k/mo at 1M users |
| Map data | OpenStreetMap (open license) | — | Free; Sri Lanka extract ~2–5 GB as PMTiles |
| Geocoding | **Nominatim self-hosted on SL extract (~8 GB RAM)** | Photon, Pelias | Refreshed weekly by `osm-pipeline` CronJob |
| Routing / ETA (Phase 3) | OSRM or Valhalla (self-hosted) | — | Free; self-hosted |
| **In-app VoIP** | **LiveKit SFU + coturn TURN cluster** (`voip-svc`) — the "Free call" option | Janus, Jitsi | Trip-scoped signalling tokens; VoIP failure → direct-dial prompt to the real number (AL-48 — masking removed; D-25 SMS relay dropped) |
| API gateway | YARP (.NET reverse proxy) | Kong, Envoy | YARP is first-class .NET, easy to start |
| Services | **.NET 10 LTS Minimal API + Dapper** | Go (if team skills allow) | .NET 10 LTS AOT reduces cold start; excellent performance. **Dapper** (micro-ORM) for all DB access — hand-written parameterised SQL, no EF Core/`DbContext`/LINQ-to-SQL |
| Data access | **Dapper** over **Npgsql** (parameterised SQL, repository-per-bounded-context) | EF Core, Marten | Full control over SQL for PostGIS/Timescale + hot-path performance; aligns with the raw-SQL DDL in D4. **No ORM change tracking** |
| DB migrations | **DbUp** (or **Grate**) versioned SQL scripts, run as a one-shot `migrate` job | `dotnet ef migrations` | Plain idempotent/ordered `.sql` scripts in source control; Dapper has no migration tooling of its own |
| MQTT broker | EMQX 5 | Mosquitto, HiveMQ | EMQX best for scale; Mosquitto too simple |
| Event stream | Redpanda (Kafka-API, single binary, no JVM, no ZooKeeper) | Apache Kafka, NATS JetStream | Redpanda is used at **every** stage — 1 broker in dev (RF=1), 3-node RF=3 at MVP/prod, 5-node + tiered storage at national scale. Same `Confluent.Kafka` client throughout; no streaming-substrate swap on the roadmap |
| Live state / geo | Redis 7 (Cluster) | Dragonfly, KeyDB | Redis is safest; Dragonfly for high single-node throughput |
| Relational DB | PostgreSQL 16 + PostGIS | CockroachDB (Phase 3) | Postgres is correct; CockroachDB for global distribution only |
| Object storage | MinIO (self-hosted) / Wasabi | S3, Backblaze B2 | Avoid AWS if cost-sensitive at MVP |
| Container runtime | Docker + containerd | Podman | containerd preferred in K3s/K8s |
| Orchestration | K3s (MVP/testing, Contabo EU replica) → **DigitalOcean DOKS, Singapore (production — decided 2026-07-05)** → AWS EKS (Phase 3) | AKS, GKE | DigitalOcean excellent price/performance for startup (free HA control plane, ~$300–400/mo); AWS EKS for national scale (1M+ WebSocket connections, managed ALB/NLB, Aurora) |
| Service mesh | Linkerd (pilot) → Istio (scale) | Cilium eBPF | Linkerd lighter; Istio when WASM filters needed |
| Geospatial library | H3 (Uber) via H3Lib .NET | S2, geohash | H3 best hex neighbourhood properties |
| OCR | Gemini Flash 3.0 (primary) + Tesseract (fallback) | Azure Doc Intelligence | Must have on-prem fallback; drives Mode-C onboarding auto-verify (AL-27) |
| Auth (users) | Phone OTP via SMS gateway (Notify.lk, ~Rs 0.50–1.50/SMS) with **Redis token-bucket rate limit (60 s resend, 5/h) and secondary SMS gateway fallback (Dialog/Mobitel) for SOS**. **Apps (Passenger + Driver): Phone OTP only** (no Google). **Admin Portal: Password or Google Sign-In — no MFA (AL-37).** **Fleet Portal: Email+Password / Google / Apple.** Keycloak at scale. | Auth0, Ory Hydra | Phone OTP as primary app auth (AL-07). Google/Apple on web portals only (free up to 50k MAUs). **Avoid Firebase Phone Auth** ($0.27/SMS) |
| Push notifications | **FCM HTTP v1** (Android) + **APNs HTTP/2** (iOS) with batch send + exponential-backoff worker | — | Handles FCM/APNs provider rate limits (D-27) |
| App attestation | **Play Integrity API** (Android) + **App Attest / DeviceCheck** (iOS) | — | **Enforced at YARP gateway middleware** on sensitive endpoints (D-30) |
| Device binding | **Android Keystore** (Android) + **iOS Keychain + Secure Enclave** (iOS) | — | Hardware-backed credential storage for driver identity |
| MQTT client (Driver App) | **HiveMQ MQTT Android client** (Android) / **CocoaMQTT** (iOS) | MQTTnet | Runs in native foreground service (Android) / background task (iOS); MQTT config shared via KMP module |
| SignalR client (Passenger App) | **SignalR Java client** (Android) / **SignalR Swift client** (iOS) | — | WebSocket fan-out for geocell subscriptions |
| Secrets | sealed-secrets (K3s) → Vault | AWS Secrets Manager | Vault when multi-service |
| Observability | Prometheus + Loki + Grafana + Tempo | Datadog (if budget) | Self-hosted LGTM stack preferred |
| Payment (driver platform fee) | **In-app wallet top-up only** (Driver App) + **wallet-svc** (.NET 10 LTS). **In-app top-ups**: **(1) Credit/Debit Card** (OnePay), **(2) OnePay wallet**, **(3) LankaQR** — **no Bank Transfer (AL-05)**. **Namma Yatri-style daily fee**: Mode A = Free, Motorbike Rs 50, Three-wheeler Rs 100, Flex Rs 150, Sedan Rs 200, Mini Van Rs 250, Van Rs 300. **First trip free**; fee auto-deducted before 2nd trip. Mode B = monthly per vehicle ~Rs 300 (first month free). **Bulk credit vouchers** (per-tier purchase discount configured in DB, applied at purchase, credited to buyer's wallet). **Driver-to-driver credit transfer** = by **Driver ID**, **exact value, no commission**. **No Google Play Subscription, no per-trip fee.** | OnePay / Commercial Bank IPG | In-app top-ups use external payment methods (card/LankaQR), permitted by Google Play for off-platform service fees. **"Reseller" is not a role/account/capability** — just a driver who bought bulk credit and resells it; all driver-to-driver transfers carry no commission |
| Web framework (Admin + Fleet Portals) | **Next.js** (React, TypeScript) | Remix, SvelteKit | Responsive, mobile-first; SSR; shared auth via `iam-svc` JWT. Admin Portal = `admin.mageride.lk`, Fleet Portal = `fleet.mageride.lk` (AL-02/AL-03). Styled exclusively with **Tailwind CSS** (AL-52, row below) |
| Web styling (all web surfaces) | **Tailwind CSS** + PostCSS, shared **`@mageride/tailwind-preset`** (D2 §A brand/vehicle tokens, Outfit+Inter type scale, 375/768/1024 `screens`, `dark:` variant) | CSS Modules, styled-components, MUI, Bootstrap | **Sole styling system** for `admin-portal`, `fleet-portal`, and the SCR-WT web-subview pages (AL-52). Utility-first; headless primitives (Radix/Headless UI) styled with Tailwind permitted; **no runtime CSS-in-JS** — build-time compilation only (SSR-safe, smaller bundles, strict-CSP friendly) |
| Payment (ride fares) | **Cash** (default) + **LankaQR scan-driver-QR** (no surcharge; settles by **attestation → `DriverConfirmedQR`**, AL-47) + **OnePay** (+5% surcharge, gateway-verified) with **persisted payment state machine** (Initiated→Pending→Succeeded/Failed/Retried/FellBackToCash/QrClaimedByPassenger/DriverConfirmedQR) | PayHere | OnePay ~3% processing fee covered by 5% passenger surcharge. Driver-QR payments are bank-to-bank into the driver's own QR — no platform webhook, no bank IPG (AL-05/AL-47); OnePay webhook is the only gateway reconciliation (D-10) |
| Localised content | **`content-svc`** + Postgres `content.*` schema | i18next files | Si/Ta/En notification templates, FAQ, admin broadcasts, fare-tariff strings (D-26) |

### 18.2 Mobile Architecture (KMP + Native UI)

**Architecture:** Shared Kotlin business logic modules via **Kotlin Multiplatform (KMP)** + **Jetpack Compose** (Android UI) + **SwiftUI** (iOS UI).

This approach shares 60–70% of code (all business logic, networking, data models, validation) while keeping background GPS, MQTT, and platform-specific services fully native. The critical background GPS tracking and MQTT client must reside in the native layer to ensure the OS does not terminate the process.

#### What Gets Shared (KMP Common Module — Kotlin)

```
shared/
├── commonMain/
│   ├── data/
│   │   ├── models/          # Position, Trip, Vehicle, Fare, Wallet DTOs
│   │   ├── api/             # Ktor HTTP client for REST APIs (query-svc, fare-svc, etc.)
│   │   └── repository/      # Repository pattern — data access abstraction
│   ├── domain/
│   │   ├── trip/            # Trip state machine logic
│   │   ├── fare/            # Fare calculation (Mode C rules, peak/night surcharges)
│   │   ├── dispatch/        # Dispatch offer handling, Driver Level System
│   │   ├── geo/             # H3 geocell computation, adaptive rate logic
│   │   ├── wallet/          # Wallet balance display logic, transaction history
│   │   └── auth/            # JWT token management, refresh logic
│   ├── mqtt/
│   │   ├── MqttConfig.kt    # Broker URLs, topic patterns, QoS settings
│   │   ├── PositionPayload.kt  # Canonical position event schema (§ Appendix A)
│   │   └── AdaptiveRateEngine.kt  # 1 call/4s, 1 call/10s, 1 call/60s logic (§7.5)
│   └── util/
│       ├── DateTimeUtils.kt
│       └── Validators.kt
├── androidMain/             # Android-specific expect/actual implementations
└── iosMain/                 # iOS-specific expect/actual implementations
```

#### What Stays Native (Platform-Specific)

| Component | Android (Kotlin) | iOS (Swift) |
|---|---|---|
| **Background GPS service** | `ForegroundService` + `FusedLocationProviderClient` | `CLLocationManager` + background location mode |
| **MQTT connection** | HiveMQ Android client in foreground service | CocoaMQTT in background task |
| **Device binding** | Android Keystore | iOS Keychain + Secure Enclave |
| **Map rendering** | MapLibre GL Native (Android SDK) | MapLibre GL Native (iOS SDK) |
| **App attestation** | Play Integrity API | App Attest (DeviceCheck) |
| **Push notifications** | FCM (native) | APNs via FCM |
| **UI** | Jetpack Compose | SwiftUI |
| **Payment** | In-app top-up (OnePay SDK / LankaQR **deep link to bank app**, AL-15) — no portal hand-off | In-app top-up (OnePay / LankaQR deep link) |
| **VoIP call** | Android WebRTC / native call | iOS CallKit + WebRTC |

#### Proposed Repository Structure

```
MageRide-mobile/
├── shared/                          # KMP shared module (Kotlin)
│   ├── commonMain/                  # Platform-agnostic business logic
│   ├── androidMain/                 # Android expect/actual
│   └── iosMain/                     # iOS expect/actual
│
├── driver-android/                  # Android Driver App (Jetpack Compose)
│   ├── ui/                          # Compose screens
│   ├── service/                     # ForegroundService (GPS + MQTT publish)
│   ├── map/                         # MapLibre GL Android integration
│   └── security/                    # Keystore, Play Integrity
│
├── driver-ios/                      # iOS Driver App (SwiftUI)
│   ├── Views/                       # SwiftUI views
│   ├── Services/                    # CLLocationManager + CocoaMQTT
│   ├── Map/                         # MapLibre GL iOS integration
│   └── Security/                    # Keychain, App Attest
│
├── passenger-android/               # Android Passenger App (Jetpack Compose)
│   ├── ui/                          # Compose screens
│   ├── signalr/                     # SignalR Java client
│   └── map/                         # MapLibre GL Android + live markers
│
└── passenger-ios/                   # iOS Passenger App (SwiftUI)
    ├── Views/                       # SwiftUI views
    ├── SignalR/                     # SignalR Swift client
    └── Map/                         # MapLibre GL iOS + live markers
```

#### KMP Shared Libraries

| Concern | Library | Notes |
|---|---|---|
| HTTP client | **Ktor** | Multiplatform; talks to query-svc, fare-svc, wallet-svc, etc. |
| Serialization | **kotlinx.serialization** | JSON/CBOR for API + MQTT payloads |
| Coroutines | **kotlinx.coroutines** | Async/concurrency model |
| Date/Time | **kotlinx-datetime** | Multiplatform date handling |
| DI | **Koin** | Lightweight multiplatform DI |
| Settings/Prefs | **multiplatform-settings** | Key-value storage abstraction |
| Geospatial | **H3 Kotlin** | H3 geocell computation (shared) |

#### Why KMP + Native UI Over Alternatives

| Criterion | KMP + Native UI | Flutter | Fully Native (4 codebases) |
|---|---|---|---|
| Background GPS reliability | Native (best) | Requires native anyway | Native (best) |
| Code sharing | 60–70% (business logic) | 80–90% (UI + logic) | 0% |
| Native platform feel | 100% (Compose + SwiftUI) | ~85% (Skia rendering) | 100% |
| MapLibre integration | Direct native SDK | Community plugin | Direct native SDK |
| Team skill alignment | Kotlin (existing) | Dart (new language) | Kotlin + Swift |
| Development effort | 1.4× (vs single platform) | 1.2× | 2× |
| Production proven (ride-hailing) | Careem | — | Uber, Lyft |

### 18.3 What NOT to Use

| Technology | Reason to Avoid |
|---|---|
| **Google Maps SDK** | **$7,000–14,000/mo at 1M users.** Use Maplibre GL Native (free) + PMTiles + Cloudflare R2 ($20–50/mo at 1M users) |
| **OSM public tile servers** (tile.openstreetmap.org) | **Will block commercial/heavy use.** Must self-serve tiles via PMTiles + Cloudflare R2 |
| **Firebase Phone/OTP auth** | **$0.27/SMS in Sri Lanka with no free allowance.** Use a **local SMS gateway** (e.g., Notify.lk at Rs 0.50–1.50/SMS) for OTP delivery instead |
| **React Native** | Same background service problems as Flutter; bridge overhead for real-time MQTT; no mature MQTT library; weaker MapLibre integration. KMP preferred for this use case |
| **Flutter** | Background GPS + MQTT must be written in native code anyway (Kotlin + Swift), negating Flutter’s main code-sharing benefit. Team would need Dart (3rd language). KMP shares heavy logic in Kotlin (existing skill) |
| RabbitMQ as event backbone | No partition-ordered consumers; wrong tool for telemetry streaming |
| MongoDB for position data | Postgres + PostGIS is strictly superior; don't add a third DB |
| Elasticsearch for geospatial | Overkill; Redis GEO + PostGIS covers all cases |
| Server-Sent Events (SSE) instead of WebSocket | One-directional; cannot receive cell subscription changes |
| NATS (any flavour) | Adds a second streaming substrate without removing Redpanda; not worth the operational duplication |
| Docker Swarm for > 5 nodes | Operational ceiling is too low |

---

## 19. Multi-Stage Evolution Roadmap

### Phase 0 — Development (Months 0–3)

**Target:** development & integration environment, ~100 vehicles, ~1,000 passengers, single VPS.
Matches `stories.txt` §trial setup. All containers sized at **2 GB / 1 vCPU** (Postgres at 4 GB).

> **Phase 1 — Hardware Tracker Plane (Epic 3 promoted, T-01–T-12).** Deploy `provisioning-svc` (step-ca + Vault PKI), `tracker-adapter-svc` per protocol family (GT06, JT808, H02, NMEA-UDP), `fleet-health-svc`, TimescaleDB hypertable `telemetry.positions` with continuous aggregates and 7-day compression. Pilot with 1,000 trackers (mix of individual Mode C cabs and one public-bus fleet of 200 vehicles) before opening to the 10,000 launch ceiling. Acceptance: NFR-34 to NFR-44 met in staging load test; anti-clone tested; 24 h replay-storm soak test passed.

- [ ] Single 24 GB / 6 vCPU Contabo VPS (VPS-30), K3s single-node (or Docker Compose)
- [ ] EMQX single node (2 GB) + Redpanda single broker (1 GB, RF=1)
- [ ] `mqtt-bridge-svc` split out from `fanout-svc` (do **not** put both responsibilities in SignalR — see §1.2 critique)
- [ ] `position-processor-svc` + `fanout-svc` (SignalR, Redis backplane), 2 GB each
- [ ] Redis single instance + AOF (2 GB)
- [ ] PostgreSQL + PostGIS single instance (4 GB)
- [ ] `iam-svc` (Phone OTP via SMS gateway — **no Google Sign-In on Driver App**), `registry-svc`, `trip-state-svc`
- [ ] **KMP shared module** — establish shared Kotlin business logic (DTOs, Ktor HTTP client, domain logic, H3 geocell, adaptive rate engine)
- [ ] **Driver app (Android):** Jetpack Compose UI + native `ForegroundService` (GPS + MQTT publish) + KMP shared module
- [ ] **Passenger app (Android):** Jetpack Compose UI + SignalR WebSocket subscribe + live MapLibre map + KMP shared module
- [ ] **Driver app (iOS) — begin development mid-Phase 0:** SwiftUI + native `CLLocationManager` + CocoaMQTT + KMP shared module
- [ ] **Passenger app (iOS) — begin development mid-Phase 0:** SwiftUI + SignalR Swift client + MapLibre iOS + KMP shared module
- [ ] HAProxy + Cloudflare for TLS (single instance — HA arrives in Phase 1)
- [ ] Basic Prometheus + Grafana
- [ ] Nightly Postgres backup to Wasabi

**Deliberately excluded from the day-0 local dev spike** (note: hardware GPS trackers (Epic 3) and fleet features (Epic 13) are **Phase 1** for production per §1.6/§1.8 AL-03 — they are merely out of the very first single-VPS bring-up): multi-node HA, payment, OCR, hardware GPS trackers, fleet operator features, ad-hoc sharing (Epic 6). Single-point-of-failure is accepted. Redpanda is used from day one (single broker, RF=1) to keep the streaming substrate identical to production.

---

### Phase 1 — Production Launch (Months 3–8)

**Target:** **10,000 concurrent vehicles, 100,000 concurrent passengers** — the initial production sizing.

This is the topology specified in `stories.txt` §production setup: SignalR + EMQX behind HAProxy, two HAProxy containers with Keepalived for HA.

- [ ] **HAProxy + Keepalived (2 instances, VRRP)** fronting SignalR (WSS), EMQX (TCP 8883) — per `stories.txt` §production
- [ ] DigitalOcean DOKS cluster: 3 nodes (4 vCPU / 8 GB each, **Singapore region — decided 2026-07-05**)
- [ ] EMQX 2-node cluster + Redpanda 3-node cluster (RF=3)
- [ ] Redis Sentinel (3-node)
- [ ] Postgres Patroni (1P + 2R) + PgBouncer
- [ ] **iOS apps launch** — Driver iOS (SwiftUI + CocoaMQTT) and Passenger iOS (SwiftUI + SignalR Swift) ship to App Store
- [ ] `subscription-svc` (7-tier daily fee plans — Mode A = Free, Motorbike Rs 50, Three-wheeler Rs 100, Flex Rs 150, Sedan Rs 200, Mini Van Rs 250, Van Rs 300 — **first trip free**, idempotent fee deduction before 2nd trip, Mode B monthly per vehicle ~Rs 300, **driver-to-driver credit transfer** processing — exact value, **no commission**, by Driver ID). **Namma Yatri methodology, no Google Play Subscription**
- [ ] `wallet-svc` (driver wallet management — no separate reseller account/role; top-up via **OnePay card / OnePay wallet / LankaQR only, no bank transfer (AL-05)** + **bulk credit vouchers** with DB-configurable per-tier purchase discount; driver-to-driver credit-transfer ledger (exact value, no commission); OnePay/LankaQR gateway settlement reconciliation)
- [ ] `admin-bff` + **Admin Portal** (`admin.mageride.lk`, Next.js + Tailwind CSS) — single back-office for all six internal roles (RBAC; no MFA, AL-37): verification, moderation, support, finance/reconciliation, **bulk-voucher discount-tier configuration**, **GTFS Dataset Manager (SCR-AP-016, AL-54 — day-0 full-feed upload before go-live)**, tariff/config, audit, reporting (AL-02)
- [ ] **`fleet-svc` + Fleet Portal** (`fleet.mageride.lk`, Next.js + Tailwind CSS) — **Phase 1 (AL-03)**: fleet org (Verification-Officer-gated), Mode A/B onboarding, driver assignment, ST-901 auto-sessions, scheduling/alarms, per-fleet map & analytics (own-org RLS), monthly per-Mode-B-vehicle billing + fleet wallet, Owner/Manager/Viewer sub-roles — **URD Epic 13**
- [ ] `fare-svc` (Mode C fare calculation + **upfront fare estimate** + **in-app ride payment** via LankaQR / OnePay +5% surcharge) — P0 per URD Epic 8. **No Mode B fare** (monthly charge only).
- [ ] `notification-svc` (FCM + APNs push + SMS gateway) — P0 per URD Epic 10 (interest alerts, access request alerts, low-balance/daily fee warnings, session auto-end alerts, dispatch offers, scheduled-ride reminders, payment confirmations, SOS-to-emergency-contact)
- [ ] `dispatch-svc` (Mode C dispatch by distance + Driver Level + vehicle category, 15 s offer TTL, **Job Board** for scheduled rides, **cancellation penalty** Rs 50, Driver Level System, **Directional Travel / Destination Filter** DT-01..DT-08) — P0 per URD Epic 6A
- [ ] **Hardware GPS Tracker plane (promoted to Phase 1, v2.3 §1.6, T-01..T-12)** — `provisioning-svc` (step-ca + Vault PKI, per-device X.509 / HMAC, 90-day rotation, anti-clone), `tracker-adapter-svc` per protocol family (GT06, JT808, H02, NMEA-UDP), `fleet-health-svc`, TimescaleDB hypertable `telemetry.positions` (continuous aggregates + 7-day compression) — URD Epic 3 (NFR-34..NFR-44)
- [ ] `safety-svc` (SOS for passengers + drivers → SMS via primary + secondary gateway, **SOS p99 ≤ 5 s SLO**, live-trip web share token **scoped to trip + 1 h grace + 60 req/min rate-limit + revocable**, vehicle reports + 3-strike auto-delisting, **passenger block driver**) — P0 per URD Epic 12 (D-33, D-34)
- [ ] `support-svc` (in-app FAQ, support ticket creation/tracking/resolution) — P1 per URD Epic 16
- [ ] `ocr-svc` (Gemini Flash + Tesseract fallback, **with in-perimeter PII redaction pre-pass** — OpenCV face-blur + Tesseract bounding-box ID masking) — D-36
- [ ] **`voip-svc`** (LiveKit SFU + coturn TURN cluster + signalling REST, direct-dial fallback on VoIP failure — AL-48, 500 concurrent calls target) — D-24
- [ ] **`tile-cdn` setup** (Cloudflare R2 bucket + Worker for PMTiles serving, signed URLs for offline bundles) + **`osm-pipeline` CronJob** (weekly Geofabrik SL diff → osm2pgsql → tippecanoe → PMTiles → R2 sync) — D-14/D-15
- [ ] **`nominatim-svc`** (self-hosted forward/reverse geocoder on SL OSM extract, ~8 GB RAM, weekly refresh) — D-14
- [ ] **`content-svc`** (Si/Ta/En notification templates, FAQ articles, admin broadcasts, fare-tariff display strings; versioned with admin approval) — D-26
- [ ] **`reputation-svc`** (unified cancellation / no-show / vehicle-report counters with rolling-window reset; gRPC `block_status` + `driver_level` consumed by `dispatch-svc`/`iam-svc`) — D-04
- [ ] **`trip-state-svc` active-session mutex** (Redis `lock:driver:{driverId}` SETNX + Postgres UNIQUE partial index on `trips.sessions(driver_id) WHERE state='ACTIVE'`) — D-03 / URD US-9.6
- [ ] **`fare-svc` payment state machine** (Initiated→Pending→Succeeded/Failed/Retried/FellBackToCash) + **cross-trip cancellation settlement** outbox — D-10/D-05
- [ ] **`registry-svc` OnePay merchant onboarding** (driver payout-account binding on vehicle approval) — D-11
- [ ] **API gateway middleware** for Play Integrity / App Attest enforcement (D-30) and `X-App-Version` min-version gate (returns 426 Upgrade Required) — D-31
- [ ] **EMQX rule-engine rate-limit** (suppress pubs > **5 msg/s/vehicle** — ceiling raised from 2/s to accommodate the 1 s near-geofence cadence + retries, §7.5.2) + EMQX JWKS cache (15 min TTL) — D-17/D-21
- [ ] **Double-entry ledger** (`billing.accounts`, `journal_entries`, `journal_postings` with balanced-postings DB constraint) — D-09
- [ ] **`admin-bff` audit-log interceptor** (writes `audit.events` on every mutation: approve/reject, ban, wallet adjust, fare config, level-system param change) — D-35
- [ ] Full LGTM observability stack (Loki, Grafana, Tempo, Prometheus)
- [ ] CI/CD pipeline (GitHub Actions + ArgoCD)
- [ ] Terraform IaC for all infrastructure
- [ ] Linkerd service mesh

**Scale-out within Phase 1:** new capacity is added by **scaling node count and pod replicas only** (see §10.2.1 triggers table). The architecture does not change.

---

### Phase 2 — Regional Scale (Months 8–18)

**Target:** 30,000 vehicles, 300,000 passengers, nationwide coverage

- [ ] DigitalOcean DOKS Multi-AZ (2 availability zones, scale node pool to 5–8 nodes)
- [ ] Scale Redpanda cluster to 5 nodes (RF=3) and enable tiered storage to S3/R2 for long-tail retention
- [ ] Redis Cluster (3M + 3R)
- [ ] Postgres with read replicas + Citus if needed
- [ ] ~~admin-bff (operator console)~~ — **moved to Phase 1** (Admin Portal is the consolidated back-office, AL-02)
- [ ] ~~**`fleet-svc`** (fleet operator org…)~~ — **promoted to Phase 1 (v2.6, §1.8 AL-03); see Phase 1 checklist.** Phase 2 only adds **route-deviation/geofence alerts** (Phase 3 per URD US-13.5) and scales fleet analytics — **URD Epic 13**
- [ ] ~~`tcp-adapter-svc` / `provisioning-svc` + step-ca (Hardware GPS Tracker Support)~~ — **promoted to Phase 1 (v2.3, §1.6, T-01/T-02); see Phase 1 checklist below.** Phase 2 only *scales* the tracker plane (more adapter pods, separate Timescale cluster)
- [ ] **Ad-hoc route sharing** (seat tracking, FULL state) — **URD Epic 6 (deferred from Phase 1)**
- [ ] Keycloak replacing Phone OTP SMS gateway (unified IdP migration path)
- [ ] HashiCorp Vault cluster (Raft storage)
- [ ] Anti-spoofing ML model (baseline statistical outlier detection)
- [ ] SLO monitoring + PagerDuty integration
- [ ] Load testing at 2× target capacity before launch

---

### Phase 3 — National Scale (Months 18–36)

**Target:** 100,000 vehicles, 1,000,000 passengers

- [ ] Migration to **AWS EKS** (managed K8s) — Mumbai or Singapore region. AWS required for: ALB/NLB handling 1M+ concurrent WebSocket connections, Aurora PostgreSQL, managed MSK (Kafka), and enterprise-grade networking
- [ ] Warm DR in second region (Redpanda MirrorMaker, Postgres logical replication)
- [ ] ClickHouse for analytics (operator dashboards, heatmaps, route analytics)
- [ ] OSRM / pgRouting for ETA and route matching
- [ ] Flink for real-time stream analytics (anomaly detection, demand forecasting)
- [ ] Driver ride earnings payout system to bank accounts (regulated)
- [ ] Multi-tenant operator onboarding (city councils, private bus companies)
- [ ] GTFS-RT feed export (open transit data standard)
- [ ] Istio service mesh + OPA for complex policy
- [ ] Dragonfly evaluation to replace Redis at throughput ceiling

---

## Appendix A — Position Event Schema (Canonical)

```json
{
  "vehicleId": "veh_01J4KX...",
  "ts": 1714128000000,
  "lat": 6.927079,
  "lng": 79.861243,
  "speed": 42.5,
  "heading": 270,
  "accuracy": 8.0,
  "altitude": 12.0,
  "tripSessionId": "trip_01J4...",
  "source": "mobile_gps | hardware_gt06 | hardware_st901",
  "traceId": "4bf92f3577b34da6a3ce929d0e0e4736"
}
```

## Appendix B — Trip State Machine

```
          ┌─────────────────────────────────────────┐
          │                                         │
   START  ▼                                         │
  ┌──────────────┐   driver_starts    ┌─────────────┴──┐
  │   INACTIVE   │──────────────────► │    ACTIVE      │
  └──────────────┘                    └────────┬───────┘
                                               │
                                    ┌──────────┴──────────┐
                                    │                     │
                           driver_ends           idle > 30 min
                           /auto_end_geo         (background timer)
                                    │                     │
                                    ▼                     ▼
                             ┌──────────────────────────────┐
                             │          COMPLETED           │
                             │  (calculate distance, fare)  │
                             └──────────────────────────────┘
```

**Ad-hoc vehicle additions (Phase 2 — Epic 6):**
- `ACTIVE` → `FULL` when available_seats reach 0 (stop broadcasting position)
- `FULL` → `ACTIVE` if passenger taps "exit vehicle" (increment seats)

**Grace period restart (US-5.10):**
- `COMPLETED` (auto-ended) → `ACTIVE` if driver restarts within 5-minute grace period. No additional daily fee charge.

---

### Appendix B.2 — Mode C Ride State Machine (v2.1, R-01)

This is a **separate aggregate** from the tracking session above. The vehicle tracking session (Modes A/B/C) is owned by `trip-state-svc` and represents "is the device live-streaming GPS". The Mode C *ride* below is owned by `ride-svc` and represents "a passenger requested a ride; what is its commercial state". A single tracking session can span zero or more Mode C rides; a Mode C ride always requires an active tracking session.

```
               Rider POST /rides/request
                       |
                       v
               +----------------+
               |   Requested    |
               +----------------+
                       |
                       v
               +----------------+   no candidates / round timeout  +-------------------+
               |    Matching    |---------------------------------> |  ExpiredNoDriver  | (terminal)
               +----------------+                                  +-------------------+
                       |
                       | dispatch reserves driver, sends offer
                       v
               +----------------+   decline / expire (15s)
               |    Offered     |--------------------------+
               +----------------+                          |
                       |                                   v
                       | atomic accept (§11.11)        re-enter Matching
                       v
               +----------------+   rider cancel        +-----------------------------+
               |    Accepted    |---------------------> | CancelledByRiderAfterAccept | (terminal, Rs 50)
               +----------------+                       +-----------------------------+
                       |
                       | driver enters pickup geofence
                       v
               +----------------+   rider no-show 5min  +---------------+
               | DriverArrived  |---------------------> |  NoShowRider  | (terminal, Rs 100)
               +----------------+                       +---------------+
                       |
                       | driver taps Start
                       v
               +----------------+   rider cancel mid-trip   +-----------------------------+
               |   InProgress   |-------------------------> | CancelledByRiderAfterAccept | (terminal, full fare)
               +----------------+                           +-----------------------------+
                       |
                       | driver taps Complete
                       v
               +----------------+
               |   Completed    |
               +----------------+
                       |
                       v
               +-----------------+    cash fallback    +---------------+
               | PaymentPending  |-------------------> |  CashSettled  | (terminal)
               +-----------------+                    +---------------+
                       |     |
     provider Succeeded|     | provider Failed (after FellBackToCash)
                       v     v
              +-------+   +-------------+
              | Paid  |   |  Disputed   | (terminal-with-followup; refund queue)
              +-------+   +-------------+
               (terminal)

  Any pre-Accepted state + rider cancel → CancelledByRiderBeforeAccept (terminal, no penalty)
  Any post-Accepted state + driver cancel / LWT offline beyond grace → CancelledByDriver (terminal, reputation hit)
  late callback while CashSettled → Overpaid → admin refund (§11.14)
```

**Authoritative invariants enforced by `ride-svc`:**

1. A rider has **at most one** non-terminal ride at any time (`UNIQUE partial (passenger_id)`).
2. A driver has **at most one** non-terminal ride in `Accepted/DriverArrived/InProgress/PaymentPending` (`UNIQUE partial (accepted_driver_id)`) — this is the v2.1 reinforcement of the O2 driver-vehicle-exclusivity constraint at the ride layer.
3. A driver has **at most one** live offer (`UNIQUE partial (driver_id) WHERE status IN ('OFFERED','ACCEPTED')` on `dispatch.offers`).
4. Every state transition is recorded in `rides.transitions` and surfaces a domain event through `rides.outbox`.
5. Every mutating API call carries `Idempotency-Key`; replays return the original response from `rides.command_log`.
6. **(v2.2)** The state machine is **kind-agnostic**: `rides.kind ∈ {passenger, proxy, package}` traverses the *same* states. `proxy` differs only in the `booker_id ≠ rider_id` invariant and the optional FCM location-request sub-flow (§11.15). `package` differs only in OTP gating of `Accepted→InProgress` (pickup OTP) and `InProgress→Completed` (delivery OTP or proof photo) and adds `CashOnDelivery → CashOnDeliveryCollected` as a payment terminal (P-06, P-07, P-08).
7. **(v2.4)** **Directional Travel does not appear in this state machine.** It is a `dispatch-svc` candidate filter (DT-01..DT-08) applied *before* an offer is ever created; the ride aggregate, its states, transitions, and invariants are entirely unchanged whether or not a matched driver had an active Destination Filter. It composes with all three `kind`s (DT-07).

## Appendix C — Suggested Domain Service API Contracts

### registry-svc

```
POST   /vehicles                  # register new vehicle (incl. driver profile photo + name)
GET    /vehicles/{vehicleId}      # get vehicle details + driver profile + registration status
GET    /vehicles/{vehicleId}/status  # registration status (pending/approved/rejected + rejection reason)
POST   /vehicles/{vehicleId}/share  # create sharing grant
DELETE /vehicles/{vehicleId}/share/{grantId}  # revoke grant
POST   /vehicles/{vehicleId}/device  # bind device/IMEI
PUT    /vehicles/{vehicleId}/driver-profile  # update driver photo/name
POST   /vehicles/{vehicleId}/deactivate  # deactivate/remove vehicle (US-2.16)
DELETE /vehicles/{vehicleId}/subscribers/{userId}  # passenger unsubscribes from Mode B (US-NEW.1)
```

### query-svc

```
GET    /nearby?lat={}&lng={}&radius={}&types=[]   # nearby vehicles (response incl. driver name, photo, ETA)
GET    /trips/{userId}                            # trip history
GET    /trips/{userId}/{tripId}                   # trip detail + polyline
GET    /earnings/{driverId}?period=today|week|month  # driver earnings dashboard
GET    /earnings/{driverId}/sessions               # per-session earnings breakdown
```

### transit-svc (NEW — AL-18; GTFS public-transport routing)

```
GET    /transit/options?fromLat&fromLng&toLat&toLng  # ALL direct routes (route_no, headsign/description, shape) + transit (≥1 transfer) options for a destination — feeds SCR-PA-009 (item 3)
GET    /transit/routes/{routeId}                     # route detail + shape polyline + nearest halts
GET    /geo/parse-maps-link?url=                     # AL-20: resolve a Google Maps URL (incl. short maps.app.goo.gl) → {lat,lng}; used by "Paste link" (items 5,6). Full URLs parsed client-side; short links resolved here (follow redirect, no Google API)
POST   /admin/transit/gtfs/uploads                   # admin: upload full GTFS zip (multipart, ≤200 MB) → 202 {feedVersionId} (SCR-AP-016, AL-54)
GET    /admin/transit/gtfs/uploads/{feedVersionId}   # admin: poll status (Uploaded/Validating/Validated/Failed) + summary counts/warnings
GET    /admin/transit/gtfs/uploads/{feedVersionId}/report  # admin: full row-level validation error report (JSON/CSV)
POST   /admin/transit/gtfs/uploads/{feedVersionId}/activate  # admin: atomic staging→live swap + transit-svc cache reload (Idempotency-Key)
GET    /admin/transit/gtfs/versions                  # admin: feed version history (active/archived/failed)
GET    /admin/transit/gtfs/versions/{feedVersionId}/download  # admin: signed URL for the original zip
POST   /admin/transit/gtfs-import                    # [SUPERSEDED → /admin/transit/gtfs/uploads + /activate, AL-54] retained as the internal import step
```

### fare-svc

```
GET    /fare/estimate?fromLat={}&fromLng={}&toLat={}&toLng={}&vehicleType={}  # upfront fare estimate
POST   /fare/calculate             # final fare after trip ends
POST   /fare/pay                   # initiate payment (method: cash | lankaqr | onepay | scan_driver_qr)
POST   /fare/pay/scan-driver-qr    # complete payment by scanning the driver's QR (printed/on-screen/sticker) — AL-22 (item 18)
GET    /fare/pay/{paymentId}/status # payment status
POST   /fare/pay/onepay/webhook    # OnePay payment confirmation webhook
POST   /fare/pay/lankaqr/confirm   # LankaQR payment confirmation
```

### subscription-svc

```
GET    /fees/rates                                  # 7-tier daily fee rates (Mode A=Free, Motorbike 50, 3W 100, Flex 150, Sedan 200, MiniVan 250, Van 300)
GET    /fees/{driverId}/today                       # today's fee status per vehicle (paid/unpaid, amount, vehicle type, first-trip-free indicator)
POST   /fees/{driverId}/charge-before-trip           # internal: idempotent daily fee deduction before 2nd trip (key: driverId+vehicleId+date). First trip = free.
GET    /fees/{driverId}/history?from=&to=            # daily fee deduction history
# Driver-to-driver credit transfer (Driver App APIs, no portal — AL-01). No separate reseller role/capability; transfers move EXACT value, no commission.
POST   /subscriptions/credit-transfer/request        # driver requests a credit transfer from another driver (body: holderDriverId, amount)
POST   /subscriptions/credit-transfer/{id}/approve   # holding driver approves (debits sender EXACT amount, credits recipient EXACT amount; no commission)
POST   /subscriptions/credit-transfer/{id}/reject    # holding driver rejects request
GET    /subscriptions/credit-transfer/pending        # list pending incoming requests (for the credit-holding driver)
PUT    /admin/fees/rates                             # admin updates vehicle-type daily rates (Admin Portal)
PUT    /admin/voucher-discount-tiers                 # admin sets the bulk-voucher commission/discount % per voucher value (denomination) (Admin Portal Config)
# --- Mode B passenger subscriptions, access requests & payments (AL-23, AL-24, AL-25; Epic 23) ---
# Access requests & grants are PER VEHICLE; a Mode B marker tap opens the access-request screen with vehicleId pre-filled.
POST   /mode-b/{vehicleId}/access-requests           # passenger requests access to a Mode B vehicle (item 8/15)
GET    /mode-b/{vehicleId}/access-requests           # driver/owner: pending requests for THIS vehicle (name, mobile, PAX id)
POST   /mode-b/access-requests/{id}/accept           # driver/owner accepts → creates grant + starts subscription (item 15)
POST   /mode-b/access-requests/{id}/reject           # driver/owner rejects
GET    /mode-b/subscriptions/{passengerId}           # passenger's subscriptions (SCR-PA-025): paid/free, monthly_fare, next_due, status
POST   /mode-b/subscriptions/{id}/unsubscribe        # passenger unsubscribes → grant.status='unsubscribed' + revocation push (D-22); loses visibility (item 17)
DELETE /mode-b/{vehicleId}/subscribers/{subId}       # OWNER deletes a muted/unsubscribed subscriber (hard-delete) (item 17)
PUT    /mode-b/{vehicleId}/subscribers/{subId}/fare  # owner sets/overrides this subscriber's monthly fare (item 16f)
GET    /mode-b/{vehicleId}/subscribers               # fleet/driver: subscriber roster for THIS vehicle (fare, cycle, this-month status, muted flag)
# Subscription payments (passenger → fleet owner; pass-through, not platform revenue)
POST   /mode-b/subscriptions/{id}/pay                # init payment: method = lankaqr_deeplink | lankaqr_scan | onepay | online_transfer (item 16e)
                                                     #   → response carries payTo from the org's VERIFIED payout profile (AL-49): lankaqrImageUrl (signed, bank-app QR) | bank/branch/accountNo/accountHolderName (transfer)
POST   /mode-b/payments/{paymentId}/transfer-slip    # passenger uploads online-transfer screenshot → status=pending_verification (item 16e)
POST   /mode-b/payments/{paymentId}/confirm          # OWNER confirms a transfer slip → status=paid (item 16f)
POST   /mode-b/{vehicleId}/subscribers/{subId}/mark-cash  # OWNER marks a cash payment received → status=paid (item 16f)
POST   /mode-b/pay/onepay/webhook                    # OnePay confirmation (subscription)
POST   /mode-b/pay/lankaqr/confirm                   # LankaQR confirmation (subscription)
GET    /mode-b/subscriptions/{id}/payments           # passenger payment history (SCR-PA-025b) (item 16h)
GET    /mode-b/{vehicleId}/subscribers/{subId}/payments  # owner per-subscriber ledger (SCR-FP-012) (item 16i)
```

### wallet-svc

```
GET    /wallet/{userId}                            # wallet balance + summary
GET    /wallet/{userId}/transactions               # transaction history (top-ups, deductions, transfers)
POST   /wallet/topup/card                          # initiate card top-up via OnePay gateway (returns redirect URL)
POST   /wallet/topup/onepay                         # initiate OnePay wallet top-up
POST   /wallet/topup/lankaqr                       # initiate LankaQR top-up (returns "Pay" deep link to bank app; QR fallback — AL-15)
POST   /wallet/topup/onepay/webhook                # OnePay payment confirmation webhook (credits wallet)
POST   /wallet/topup/lankaqr/confirm               # LankaQR payment confirmation (credits wallet)
# Bank-transfer top-up endpoints removed (AL-05).
POST   /wallet/voucher/purchase                     # buy bulk credit voucher; applies per-tier DB discount at purchase, credits buyer wallet (e.g. pay 900 → 1,000 credited)
GET    /wallet/voucher/discount-tiers               # current bulk-voucher purchase-discount tiers (Driver App)
GET    /wallet/{driverId}/transfers                 # driver's credit-transfer history (sent & received) (Driver App)
POST   /wallet/credit-transfer/initiate             # driver proactively sends credit to another driver by Driver ID (exact value, no commission) (Driver App)
GET    /wallet/admin/voucher-discount-tiers         # list bulk-voucher discount tiers + usage stats (Admin Portal)
```

### trip-state-svc

> v2.1 scope: **Mode A / Mode B tracking sessions only**. Mode C ride lifecycle has moved to `ride-svc` (below).

```
POST   /sessions/start            # driver starts journey (mode: A | B | C)
POST   /sessions/{id}/end         # driver ends journey
POST   /sessions/{id}/restart     # driver restarts auto-ended session within 5-min grace period (US-5.10)
POST   /sessions/{id}/rating      # passenger submits 1–5 star rating + optional text (US-8.6, US-18.1)
POST   /sessions/{id}/driver-rating  # driver rates passenger 1–5 stars (US-18.2)
GET    /sessions/{vehicleId}/active     # current trip state
```

### ride-svc (v2.1 — Mode C Ride Aggregate)

All mutating endpoints **require** an `Idempotency-Key` header (ULID/UUID, max 128 chars). Duplicates replay the original response. All responses include a `version` field; clients echo it back on subsequent mutations for optimistic-concurrency error surfacing.

```
POST   /rides/request                                # body: {clientRequestId, pickup, dropoff, vehicleType, fareEstimateToken}
                                                     # 202 -> {rideId, state, version}
GET    /rides/{rideId}                               # full ride detail incl. state, offer, driver, fare
GET    /rides/{rideId}/state                         # lightweight {state, version, offerExpiresAt?}
POST   /rides/{rideId}/offer/{driverId}/accept       # driver accepts (atomic §11.11); 200 winner | 409 already accepted | 410 expired
POST   /rides/{rideId}/offer/{driverId}/decline      # driver declines; ride returns to Matching
POST   /rides/{rideId}/arrive                        # driver marks arrived at pickup (auto-fired by geofence; manual fallback)
POST   /rides/{rideId}/start                         # driver taps Start (Accepted/DriverArrived -> InProgress)
POST   /rides/{rideId}/complete                      # driver taps Complete -> Completed/PaymentPending
POST   /rides/{rideId}/cancel                        # body: {reason ∈ RIDER_CHANGED_MIND|DRIVER_TOO_FAR|EMERGENCY|OTHER}; effect per §11.12
POST   /rides/{rideId}/dispute                       # rider-initiated dispute after payment
GET    /rides/passenger/{passengerId}/active         # rider's current non-terminal ride if any (idempotent client recovery)
GET    /rides/driver/{driverId}/active               # driver's current non-terminal ride if any

# Internal (service-to-service, mTLS only)
POST   /internal/rides/{rideId}/system-cancel        # called by dispatch-svc on LWT offline beyond grace; reputation-svc on fraud lock
POST   /internal/rides/{rideId}/payment-settled      # called by fare-svc on terminal payment state
GET    /internal/rides/{rideId}/saga-state           # ops/admin diagnostics

# v2.2 — Proxy Booking (P-01, P-02, P-13)
POST   /rides/request                                # extended body fields: {isProxy, riderPhone, riderName, kind ∈ 'passenger'|'proxy'|'package'}
POST   /location-requests                            # booker initiates FCM location request; body: {bookerId, riderPhone}; 202 -> {requestId, ttl:300, state}
GET    /location-requests/{requestId}                # poll/diagnostic; clients normally use WS group booker:{bookerId}:loc-req:{requestId}
POST   /location-requests/{requestId}/confirm        # called by rider app; body: {lat, lng, accuracy}
POST   /location-requests/{requestId}/decline        # called by rider app

# v2.2 — Package Delivery (P-06, P-07, P-08)
POST   /rides/request                                # extended body: {kind:'package', packageSize ∈ S|M|L, packageDescription, recipientPhone, recipientName, paymentMethod ∈ lankaqr|onepay|cod}
                                                     # response includes pickup_otp (plaintext, shown once to sender)
POST   /rides/{rideId}/package/pickup-otp            # driver enters sender-shown OTP; 200 ok | 400 invalid (counted) | 423 locked (>5 attempts)
POST   /rides/{rideId}/package/delivery-otp          # driver enters recipient-shown OTP
POST   /rides/{rideId}/package/proof-photo           # multipart upload alternative when recipient absent; persists rides.proof_artifacts
POST   /rides/{rideId}/cod-collected                 # driver confirms cash received; transitions ride_payment to CashOnDeliveryCollected
```

### pdpa-svc (v2.1 — E-06; surfaced via admin-bff for users, internal queue for fulfillment)

```
POST   /pdpa/export                                   # user-initiated data export request; 202 -> {requestId, dueBy}
POST   /pdpa/erasure                                  # user-initiated right-to-erasure; 202 -> {requestId, dueBy, holdReasons[]}
GET    /pdpa/{requestId}                              # request status + download URL when fulfilled
POST   /admin/pdpa/{requestId}/fulfill                # admin marks fulfilled (storage URL signed)
POST   /admin/pdpa/{requestId}/reject                 # admin rejects with reason (statutory hold etc.)
```

### dispatch-svc (Mode C — On-Demand Standby)

> v2.1 scope: **candidate generation, scoring, offer dispatch, driver presence, scheduled rides, job board only**. The ride lifecycle endpoints below (`/rides/request`, `/rides/{rideId}/offer/.../accept|reject`, `/rides/{rideId}/cancel`) have moved to `ride-svc`; dispatch-svc consumes ride events and emits offer events.

```
POST   /standby/online                              # driver toggles standby online (location + vehicle)
POST   /standby/offline                             # driver toggles offline (also clears any active directional filter, DT-04)
POST   /standby/directional                         # set Directional Travel filter (body: destination {lat,lng}); 409 if daily-use limit reached; returns {expiresAt, usesRemaining} (DT-01, DT-03)
GET    /standby/directional                         # current active filter + usesRemaining + timeRemaining (DT-08)
DELETE /standby/directional                         # turn off active filter early (still consumes one daily use, DT-03)
PUT    /admin/dispatch/directional-config           # admin: theta_max, detour_max, progress_min, max_uses_per_day, max_duration, clear_on_first_trip (DT-02, DT-03)
POST   /rides/request                               # passenger requests on-demand ride (pickup, dropoff, vehicleType)
POST   /rides/{rideId}/offer/{driverId}/accept      # driver accepts dispatch offer (within 15 s TTL)
POST   /rides/{rideId}/offer/{driverId}/reject      # driver rejects offer (no penalty)
POST   /rides/{rideId}/cancel                       # passenger or driver cancels (Rs 50 penalty after acceptance; 3 continuous = booking disabled)
POST   /rides/scheduled                             # passenger books scheduled ride (pickup_time in future)
GET    /rides/scheduled/{driverId}                  # driver's upcoming scheduled rides
DELETE /rides/scheduled/{rideId}                    # cancel scheduled ride
GET    /rides/job-board?lat={}&lng={}&radius=30km    # Job Board: ALL future scheduled rides within 30 km — post-intent only, no direct accept (US-6A.5)
POST   /rides/job-board/{rideId}/intent             # driver posts intent; at T-30 min the ride is offered to the closest intent-poster (by Level) on the dispatch screen, where it is accepted
GET    /drivers/{driverId}/level                    # current driver level (1–3; L1 = no scheduled rides)
GET    /drivers/{driverId}/stats                    # acceptance rate, no-show history, rating points (US-6A.14)
POST   /internal/drivers/{driverId}/no-show         # internal: scheduler reports no-show → decrement level
GET    /admin/drivers/level-1                       # admin: list Level-1 drivers (restricted from Job Board)
POST   /admin/drivers/{driverId}/level/restore      # admin manual restore (appeal)
PUT    /admin/drivers/level-config                  # admin: configure level system parameters (US-14.12)
```

### safety-svc

```
POST   /sos                                         # passenger or driver triggers SOS (body: tripId, lat, lng, role) → SMS to emergency contact
GET    /sos/{userId}/history                        # user's past SOS events
POST   /trip-share/{tripId}                         # issue public read-only share token (returns URL)
GET    /trip-share/public/{token}                   # public live-trip view (no auth) — used by share link
DELETE /trip-share/{tripId}                         # revoke share token
POST   /reports/vehicle                             # passenger reports vehicle (body: vehicleId, reason, tripId?)
POST   /drivers/{driverId}/block                    # passenger blocks a driver (US-12.10)
DELETE /drivers/{driverId}/block                    # passenger unblocks a driver
GET    /admin/reports/queue                         # admin: pending vehicle reports for review
POST   /admin/reports/{reportId}/resolve            # admin: confirm/dismiss; 3 confirmed → auto-delist vehicle
```

### fleet-svc (Phase 1 — AL-03; Fleet Portal `fleet.mageride.lk`)

```
POST   /fleets                                      # register fleet operator organization
GET    /fleets/{fleetId}                            # fleet details
POST   /fleets/{fleetId}/vehicles                   # add vehicle to fleet (Mode B body incl. mode_b_billing=paid|free + default_monthly_fare — AL-24, item 16b)
DELETE /fleets/{fleetId}/vehicles/{vehicleId}
PUT    /fleets/{fleetId}/vehicles/{vehicleId}/classification  # set/change "Service payment" Free|Paid + default monthly fare (item 16b; UI label renamed AL-51, path unchanged; Paid → 409 unless payout profile verified — AL-49)
# --- Org bank & payout profile (AL-49; Epic 27, SCR-FP-002a) ---
GET    /fleets/{fleetId}/payout-profile             # bank, branch, accountNo, accountHolderName, docs, status
PUT    /fleets/{fleetId}/payout-profile             # upsert → pending_verification (versioned; re-verify on edit)
POST   /fleets/{fleetId}/payout-profile/documents   # kind: bank_statement | passbook_first_page | lankaqr_code
# --- Per-vehicle document slots (AL-50; Epic 27, SCR-FP-004) ---
GET    /fleets/{fleetId}/vehicles/{vehicleId}/documents   # named slots + per-doc verified|pending|missing
POST   /fleets/{fleetId}/vehicles/{vehicleId}/documents   # kind: registration_copy|insurance|revenue_license|route_permit → OCR extraction
POST   /fleets/{fleetId}/assignments                # assign driver to vehicle (time-bounded; supports temp-hired Mode A/B — AL-23)
GET    /fleets/{fleetId}/map                        # all fleet vehicles' live positions (scoped query)
GET    /fleets/{fleetId}/alerts                     # route-deviation / geofence enter/exit alerts
GET    /fleets/{fleetId}/analytics                  # per-fleet metrics (utilization, distance, earnings)
PUT    /fleets/{fleetId}/geofences                  # define operational geofence(s) for fleet
# --- Mode B subscriptions, requests & payments per vehicle (AL-23/24/25; Epic 23). Fleet Portal SCR-FP-011/012. ---
# These proxy to subscription-svc but are exposed through fleet-bff scoped to the org's vehicles (row-level security).
GET    /fleets/{fleetId}/vehicles/{vehicleId}/requests        # incoming passenger subscription requests for this vehicle (item 15)
POST   /fleets/{fleetId}/vehicles/{vehicleId}/requests/{id}/accept
POST   /fleets/{fleetId}/vehicles/{vehicleId}/requests/{id}/reject
GET    /fleets/{fleetId}/vehicles/{vehicleId}/subscribers     # subscriber roster (fare, cycle, status, muted) (items 16,17)
PUT    /fleets/{fleetId}/vehicles/{vehicleId}/subscribers/{subId}/fare   # set per-subscriber monthly fare (item 16f)
POST   /fleets/{fleetId}/vehicles/{vehicleId}/subscribers/{subId}/mark-cash  # owner marks cash received (item 16f)
POST   /fleets/{fleetId}/payments/{paymentId}/confirm         # owner confirms online-transfer slip (item 16f)
DELETE /fleets/{fleetId}/vehicles/{vehicleId}/subscribers/{subId}  # owner deletes a muted/unsubscribed subscriber (item 17)
GET    /fleets/{fleetId}/vehicles/{vehicleId}/subscribers/{subId}/payments  # per-subscriber payment ledger (item 16i)
```

### admin-bff

```
GET    /admin/dashboard                             # platform-wide metrics
PUT    /admin/fares/tariffs                         # configure Mode C fare tariffs (per vehicle type, peak/night windows). No Mode B fare.
PUT    /admin/fees/rates                            # configure 7-tier daily platform fee rates per vehicle type
POST   /admin/announcements                         # publish broadcast announcement (in-app banner + push)
POST   /admin/vehicles/{vehicleId}/suspend          # admin suspend vehicle
POST   /admin/vehicles/{vehicleId}/approve          # admin approve vehicle registration (US-2.9)
POST   /admin/vehicles/{vehicleId}/reject           # admin reject registration with reason (US-2.15)
POST   /admin/drivers/{driverId}/suspend            # admin suspend driver
GET    /admin/users                                 # user search / lookup
GET    /admin/support/tickets                       # support ticket queue (US-16.3)
POST   /admin/support/tickets/{ticketId}/resolve    # resolve support ticket
GET    /admin/audit-log                             # admin action audit log (US-19.3)
```

### support-svc

```
GET    /support/faq                                  # list FAQ articles (filtered by language, category)
GET    /support/faq/{articleId}                      # single FAQ article
POST   /support/tickets                              # create support ticket (body: category, description, tripId?, screenshot?)
GET    /support/tickets/{userId}                     # user's tickets with status
GET    /support/tickets/{userId}/{ticketId}           # ticket detail + admin response
```

### version-check (API Gateway)

```
GET    /version/check?platform={android|ios}&current={semver}  # returns {updateRequired: bool, latestVersion, updateUrl, isMandatory}
```

---

*End of Architecture Design Document v2.4*

*Next review checkpoint: Phase 0 MVP completion (3 months)*
*Document owner: Principal Solution Architect*
*Distribution: Engineering, Product, Security, Investors*
