# Lightweight Production Replica — Container & Service Layout

> **Goal:** Run the *exact same domain boundaries, event flow, and service separation* as production on a **single 24 GB / 6 vCPU VPS** (Contabo VPS-30 or equivalent). This is a true replica of the production architecture — not a simplified toy — just with **single replicas and no HA**.
>
> **Aligned to:** Architecture Design Document (ADD) **v3.4** *(refreshed 2026-07-22; previously v3.0 / 2026-07-05)*. Incorporates v2.1 (Ride Aggregate / `ride-svc`), v2.2 (proxy booking & package delivery), v2.3 (hardware trackers → Phase 1), v2.4 (Directional Travel), **v2.6 (AL-01…16: single Admin Portal + Fleet Portal, no bank-transfer top-ups, Phone-OTP-only apps, fleet-svc → Phase 1)**, **v2.7 (AL-17…26: `transit-svc` GTFS routing + paste-link, Mode B subscription payments, scan-driver-QR pay)**, **v2.8–v2.9 (AL-28…43: onboarding scanner, admin directories, admin MFA removed)**, **v3.0 (AL-44…46: `public-bff` Passenger Web subview SCR-WT-001…006)**, **v3.1 (AL-47…48: driver-QR attestation settlement, number masking withdrawn)**, **v3.2 (AL-49…51: fleet bank & payout profile SCR-FP-002a, named vehicle-document slots, "Service payment" rename)**, **v3.3 (AL-52…53: Tailwind CSS on all web frontends; .NET 10 Minimal API + Dapper over Npgsql reaffirmed)**, and **v3.4 (AL-54…55: GTFS Dataset Manager SCR-AP-016, versioned feed lifecycle, full-feed-at-launch)**. **No container-set change v3.1→v3.4** — the deltas are service-internal plus the `gtfs-import` staging-swap job and `transit_staging` schema. *Micro-change-set 2026-07-23: portal images bumped Node 20-alpine → **Node 24-alpine** (Node 20 EOL April 2026); mirrored in D7 §1/§2.1.*
>
> **Hosting decision (2026-07-05):** this replica runs on **Contabo EU — for testing, integration, CI and demos only**. **Production runs on DigitalOcean Kubernetes (DOKS), Singapore region** (see `technical_feasibility.md` §2.2 and ADD §19). EU-based testing caveats: functional results, contract tests, state machines and end-to-end position-latency measurements from Sri Lanka **are representative**; VoIP call *quality* (EU TURN adds ~350–400 ms mouth-to-ear) and hardware-tracker socket-setup timing (+~0.2 s RTT) **are not** — run those acceptance tests in the Singapore region. Keep this box on **synthetic data only** (no real PII or driver documents — PDPA cross-border hygiene).

---

## Production vs Light Replica — What Changes

| Concern | Production (§10.2) | Light Replica |
|---|---|---|
| Replicas per service | 2–3+ | **1** |
| HAProxy + Keepalived | 2× active/standby VRRP | **1× HAProxy** (no Keepalived) |
| EMQX | 2–4 node cluster | **1 node** |
| Redpanda | 3-node cluster (RF=3) | **1 node** (RF=1, no replication) |
| Redis | 3× Sentinel → Cluster 3M+3R | **1 node** + AOF |
| PostgreSQL + TimescaleDB | Patroni 1P+2R + PgBouncer | **1 node** (TimescaleDB extension co-located) + PgBouncer sidecar |
| Observability | Full LGTM stack (Prometheus + Loki + Tempo + Grafana) | **Prometheus + Grafana** (minimal) |
| TLS | mTLS everywhere | **TLS on edge** (self-signed internal) |
| Service mesh | Linkerd | **None** |
| VoIP (LiveKit + coturn) | 2× SFU + 2× TURN pods | **1× combined** (optional) |
| Geocoding (Nominatim) | Dedicated 8 GB Postgres | **External API or separate VPS** |
| Tracker adapters | Per-protocol StatefulSets | **1 container** (all protocols) |
| .NET version | .NET 10 LTS | .NET 10 LTS |
| Total RAM | ~60–80 GB across nodes | **~20 GB** (fits in 24 GB box) |

> [!IMPORTANT]
> The light replica preserves **every service boundary** from production. No services are merged. This means when you scale to production, you only change replica counts and add HA — zero re-architecture.

> [!CAUTION]
> **This is an architecture-replica, not a load-replica.** It targets ADD §2 *Development* scale (≈100 concurrent vehicles, ≈1,000 concurrent passengers). The 4 GB Postgres + co-located TimescaleDB will **not** survive ADD §2 *Production* loads (10k vehicles → 2k inserts/s sustained). Use this layout to validate flows, contracts, and integration paths — graduate to the production §10.2 layout before any load test above 1,000 vehicles.

---

## Container Layout — 11 Core Containers, 27+ Logical Services

The layout uses **11 core Docker containers** (via Docker Compose) plus up to 5 optional containers. Some containers run a single process; others co-locate tightly coupled lightweight services to stay within the 24 GB budget.

### Container 1 — `haproxy` (Edge / Load Balancer)

| Property | Value |
|---|---|
| **Image** | `haproxy:2.9-alpine` |
| **RAM / CPU** | 256 MB / 0.25 vCPU |
| **Ports exposed** | `443` (HTTPS/WSS — SignalR + REST API), `8883` (MQTTS — L4 passthrough to EMQX), `8084` (MQTT-over-WSS for mobile — L4 passthrough to EMQX `8084`), `5023` (GT06 TCP), `5024` (JT808 TCP), `5025` (H02 TCP), `5026` (NMEA UDP) |
| **Role** | TLS termination for HTTPS/WSS on 443, L4 TCP passthrough for MQTT (8883, 8084) and per-protocol tracker adapter ports |

**Services inside:** HAProxy only.

**Production equivalent:** 2× HAProxy + Keepalived VRRP. In the light replica we run 1 instance — SPOF is accepted.

> [!NOTE]
> **MQTT-WSS path:** mobile clients that cannot open raw TCP/8883 connect on `wss://<host>:8084` — HAProxy passes this through to EMQX `8084` (EMQX terminates TLS internally with the same cert). The alternative is multiplexing MQTT-WSS on `443` via HAProxy ALPN/SNI; this replica chooses the dedicated `8084` path for simpler routing.

> [!TIP]
> If VoIP container is deployed, **do NOT route TURN media through HAProxy.** Expose LiveKit HTTP (`7880`) via HAProxy; expose TURN handshake (`3478/UDP`) and the TURN media relay range (`50000-50100/UDP`) **directly on the Docker host** — HAProxy is L4/L7 TCP/HTTP and cannot relay UDP media efficiently.

---

### Container 2 — `emqx` (MQTT Broker)

| Property | Value |
|---|---|
| **Image** | `emqx/emqx:5.8` |
| **RAM / CPU** | 2 GB / 1 vCPU |
| **Ports** | `1883` (internal MQTT), `8883` (MQTTS via HAProxy passthrough), `8084` (WSS for mobile), `18083` (dashboard) |
| **Role** | Device ingest (mobile + hardware trackers via adapter), QoS1, persistent sessions, ACL enforcement, **JWKS-cached JWT auth** (15 min TTL, JIT lookup on miss — D-21), **per-vehicleId 5 msg/s rate-limit** via rule engine (D-17; raised from 2 Hz to accommodate the 1 s near-geofence cadence + retries, §7.5.2), rule engine bridge to Redpanda |

**Services inside:** EMQX broker + built-in rule engine (replaces need for a separate `mqtt-bridge-svc` at light scale — EMQX rule engine natively publishes to Redpanda via the Kafka-protocol sink).

**Listeners:** `1883` (internal plain MQTT for in-cluster services), `8883` (MQTTS for hardware trackers — via HAProxy L4 passthrough), `8084` (MQTT-over-WSS for mobile — via HAProxy L4 passthrough), `18083` (dashboard).

**Production equivalent:** 2–4 node EMQX cluster. Light replica runs 1 node.

> [!TIP]
> EMQX 5's built-in rule engine can directly sink to Redpanda via the Kafka-protocol producer, so at light scale we skip deploying a separate `mqtt-bridge-svc` container. In production (2+ EMQX nodes), you deploy `mqtt-bridge-svc` as a separate service for better control via shared subscriptions (`$share/posGroup/veh/+/pos/live` — E-08).

---

### Container 3 — `redpanda` (Event Backbone)

| Property | Value |
|---|---|
| **Image** | `redpandadata/redpanda:v24.2` (single-binary, no JVM, no ZooKeeper) |
| **RAM / CPU** | 1 GB / 0.5 vCPU |
| **Ports** | `9092` (Kafka API), `9644` (admin/metrics), `8081` (Schema Registry), `8082` (HTTP Proxy) |
| **Topics** | `telemetry.raw`, `telemetry.normalized`, `trip.events`, `ride.events`, `audit.events`, `dispatch.events` (all RF=1, partitions=3 in replica) |
| **Role** | Durable, partitioned, ordered event log (Kafka-API compatible). All consumers (position-processor, persistence-writer, trip-state, ride-svc, fleet-health, anti-spoof) read from here using `Confluent.Kafka` clients |

**Services inside:** Redpanda broker (single binary; embeds Kafka API, Raft, Schema Registry, HTTP Proxy).

**Durability config:** `--mode dev-container` is **not** used; replica runs in production mode with `fsync` enabled per partition. Single-node means **RF=1** (no replication) — on disk loss the topic data is lost, but on graceful or hard process kill, acked writes survive (Redpanda flushes per produce-ack by default). Production uses RF=3 across 3 brokers.

**Production equivalent:** 3-node Redpanda cluster (RF=3) at MVP / pilot → 5-node cluster (RF=3, tiered-storage to S3/R2) at national scale. **Same broker software, same client code — only broker count and RF change.**

---

### Container 4 — `redis` (Live State + Geo Index)

| Property | Value |
|---|---|
| **Image** | `redis:7.4-alpine` |
| **RAM / CPU** | 1.5 GB / 0.5 vCPU |
| **Ports** | `6379` (internal) |
| **Persistence** | AOF `appendonly yes`, `appendfsync everysec` |
| **Role** | Live geo index, vehicle metadata cache, per-cell streams, trip/ride active state, dispatch availability & offer management, IMEI→vehicleId cache, rate limiting, wallet balance cache, Mode B entitlement cache, SignalR backplane, OTP/refresh token state |

**Key data structures (from ADD §9.4):**

| Key Pattern | Type | Purpose |
|---|---|---|
| `geo:live` | GEO | Last position of all active vehicles |
| `veh:meta:{vehicleId}` | HASH | Cached vehicle metadata (type, colour, route) |
| `cell:{h3index}` | STREAM | Per-cell position events for fanout |
| `trip:active:{vehicleId}` | HASH | Live trip state (Mode A/B sessions) |
| `imei:{imei}` | STRING | IMEI → vehicleId lookup cache (T-03) |
| `rate:{vehicleId}` | STRING+TTL | Publish rate limiter token bucket |
| `geo:drivers:available:{type}:{cell}` | GEO | Dispatch candidate index — available drivers by vehicle type and H3 res-5 cell (R-08) |
| `driver:availability:{driverId}` | HASH (60s TTL) | Driver state, lastSeen, vehicleType, level, walletOk, currentRideId (R-08) |
| `offer:{rideId}` | HASH + PEXPIRE | Offer state {driverId, expiresAt, status}; 15 s TTL fast hint (R-04) |
| `lock:driver-offer:{driverId}` | STRING (Lua SET NX PX) | Atomic single-driver reservation for dispatch (R-10) |
| `lock:ride:{rideId}` | STRING (SET NX PX) | Ride workflow single-writer saga lock |
| `lock:driver:{driverId}` | STRING (SETNX) | Active-session mutex for trip-state-svc (D-03) |
| `wallet:bal:{driverId}` | STRING (5s TTL) | Cached wallet balance for dispatch pre-check (D-08) |
| `share:{userId}` | SET | Mode B entitlement cache, pub/sub-invalidated (D-23) |
| `refresh:{jti}` | STRING | Opaque refresh token store, rotated on use (D-29) |
| `veh:seq:{vehicleId}` | STRING | Last-seen GPS sequence number for replay dedup (R-17) |
| `otp:{phone}` | STRING+TTL | OTP rate-limit token bucket: 60 s cooldown, 5/hr (D-32) |

**Production equivalent:** Redis Sentinel 3-node → Redis Cluster 3M+3R at scale.

---

### Container 5 — `postgres` (System of Record + TimescaleDB)

| Property | Value |
|---|---|
| **Image** | `timescale/timescaledb-ha:pg16` (includes PostGIS + TimescaleDB) |
| **RAM / CPU** | 4 GB / 1 vCPU |
| **Ports** | `5432` (internal) |
| **Storage** | 100 GB SSD volume |
| **Schemas** | `iam`, `registry`, `prov`, `trips`, `rides`, `dispatch`, `reputation`, `safety`, `fares`, `billing`, `comms`, `docs`, `support`, `content`, `audit`, `pdpa`, `spatial`, `telemetry`, `transit` (GTFS + `gtfs_feed_versions` — AL-18/54) + `transit_staging` (importer target, swapped on activate — AL-54), `subscription` (Mode B payments — AL-24), `analytics` (dashboard rollup — AL-38) |
| **Role** | All persistent operational data — users, devices, sessions, vehicles, permits, driver documents, trips, **rides (passenger + proxy + package)**, position samples, fares, **payment state machine**, **wallets (double-entry ledger)**, daily fee charges, reseller transfers, dispatch offers, **candidate scores**, driver levels, scheduled rides, SOS events, vehicle reports, **reputation counters**, **VoIP sessions**, **support tickets**, **localised content templates**, **PDPA requests**, **proof-of-delivery artifacts**, **TimescaleDB hypertable for high-frequency telematics** (T-06) |

**Sidecar (same container or adjacent):** PgBouncer in transaction mode for connection pooling. **In this replica PgBouncer is a separate container** (`pgbouncer`, 128 MB / 0.25 vCPU, port `6432`); co-locating it in the same Docker container as PostgreSQL via a process supervisor conflates lifecycle and log streams and is discouraged. All app containers connect via `pgbouncer:6432`.

**TimescaleDB (T-06):** The `telemetry.positions` hypertable stores high-frequency hardware-tracker samples (partitioned by `sample_ts` 1-day chunks + `vehicle_id` hash 16 partitions). Continuous aggregates provide 1-min / 5-min rollups. Compression after 7 days (~10× ratio). 30-day hot retention. In light replica this co-locates on the same Postgres instance; in production it moves to a dedicated cluster.

**Production equivalent:** Patroni 1P+2R, 8 GB / 2 vCPU each, PgBouncer sidecar. TimescaleDB on separate cluster from Phase 2.

**Backup:** Nightly `pg_dump` → S3-compatible (Wasabi/Backblaze).

---

### Container 6 — `hot-path` (Position Processor + Persistence + Fleet Health)

| Property | Value |
|---|---|
| **Image** | Custom .NET 10 LTS multi-project image |
| **RAM / CPU** | 2 GB / 1 vCPU |
| **Ports** | None (internal consumers only) |
| **Role** | The critical hot-path processing pipeline + fleet telemetry aggregation |

**Services inside (4 logical services, single process with hosted services):**

| Service | Responsibility |
|---|---|
| **`mqtt-bridge`** | Subscribes to EMQX topics → produces to Redpanda `telemetry.raw` topic (backup to EMQX rule engine; can be disabled if rule engine handles it) |
| **`position-processor-svc`** | Consumes `telemetry.raw` → validates / anti-spoof (per-vehicle-type max-speed, accuracy > 200 m discarded, hardware: monotonic GNSS UTC + min satellite count — D-18, T-07) → computes H3 geohash → `GEOADD` to Redis `geo:live` → updates `geo:drivers:available:*` on phase transition → `XADD` to `cell:{h3index}` stream → produces to Redpanda `telemetry.normalized` topic |
| **`persistence-writer-svc`** | Consumes `telemetry.normalized` → batched writes to Postgres `trips.position_samples` (1/min per vehicle for operational data) + `COPY` batches to TimescaleDB `telemetry.positions` hypertable (full-resolution for hardware tracker samples — T-06) |
| **`fleet-health-svc`** *(new, T-01)* | Aggregates `veh/{vehicleId}/status` events and tracker diagnostics into per-fleet rollups (Online / Stale / Offline / Decommissioned); emits alerts on threshold breach (> 10% of fleet offline within 5 min); writes to `telemetry.fleet_health_5m` Timescale continuous aggregate |

> [!NOTE]
> These 4 services are co-located in **one container** for the light replica to save RAM. They run as separate `IHostedService` instances in a single .NET process. In production, each is a **separate pod** scaled independently.

> [!WARNING]
> **Backpressure isolation is sacrificed in the replica.** Because all four services share one .NET process (and one thread-pool / GC heap), a stall in any one — e.g. `position-processor-svc` on a slow anti-spoof check, or `persistence-writer-svc` blocking on a Postgres write spike — pauses the entire hot path. Production isolates each as an independently-scaled pod with its own HPA. Validate replica behaviour against ADD §3.2 SLOs (p95 < 5 s, p99 < 8 s) under a simulated Postgres slowdown before treating the replica as launch-equivalent.

---

### Container 7 — `app-services` (Domain Microservices)

| Property | Value |
|---|---|
| **Image** | Custom .NET 10 LTS multi-project image |
| **RAM / CPU** | 3 GB / 1.5 vCPU |
| **Ports** | `5000` (HTTP API, behind HAProxy) |
| **Role** | All domain/CRUD services + API gateway |

**Services inside (21 logical services, hosted behind YARP reverse proxy in a single process):**

| Service | Responsibility | Production Equivalent |
|---|---|---|
| **`api-gateway`** (YARP) | Routes `/api/*` to internal handlers. **App attestation middleware** (Play Integrity / App Attest on sensitive endpoints — D-30). **Minimum-version gate** (`X-App-Version` check, rejects with `426 Upgrade Required` — D-31) | Separate YARP pod |
| **`iam-svc`** | **Phone OTP via SMS gateway (Fit SMS — AL-60)** with **Redis token-bucket rate-limit** (60 s resend, 5/hr — D-32). Device binding (Android Keystore / iOS Keychain). **Token model**: 30 min RS256 API access JWT (JWKS-rotated) + opaque refresh in `iam.sessions` + Redis (`refresh:{jti}`, rotated on use, new-device revokes prior — D-29). **Separate MQTT JWT** (TTL = max(ride duration + 2 h, 4 h), bound to `vehicleId, deviceId, [rideId]` — E-02). **Apps are Phone-OTP only (AL-07)** — Google/Apple sign-in exists only on the Admin/Fleet web portals | Separate pod, HPA on RPS |
| **`registry-svc`** | Vehicle CRUD with **uniqueness per registration number** (D-37), sharing grants, permit OCR orchestration, driver profile (photo/name), **OnePay merchant onboarding** during approval (D-11). **Train registration (Mode A) is admin-only** — trains are created/managed via `admin-bff`, never the Driver App. **Document expiry tracker** (E-03): nightly job emits `document.expiring` at T−30d/T−7d/T−1d; expired → `DISPATCH_SUSPENDED`. Mode B unsubscribe emits `share.revoked` (D-22) | Separate pod |
| **`provisioning-svc`** | Per-device credential lifecycle: mints X.509 certs (MQTT-capable) via an **embedded `step-ca` instance** (root + intermediate keys on a dedicated Docker named volume `provisioning-ca-data`, root key offline-rotated quarterly), or signed bearer-with-IMEI-binding for legacy TCP devices. Maintains `registry.tracker_bindings`, Redis `imei:{imei}` cache with pub/sub invalidation, 90-day rotation, immediate revocation (sub-60 s propagation — T-12), **anti-clone quarantine** on duplicate IMEI (T-08), bulk-mint worker for fleet CSV uploads (T-09) | Separate pod; `step-ca` is a sidecar container in production |
| **`query-svc`** | Nearby vehicles (Redis GEO + driver profile + ETA), **filterable by transport type including trains**, **destination-based transport options** (Mode A buses & trains + on-demand options), trip history, vehicle details, **driver earnings aggregation**. **Visibility rules**: excludes **Mode C vehicles on an active hire** and any vehicle whose last position is stale (GPS off / app offline) beyond the freshness window | Separate pod |
| **`trip-state-svc`** | **Mode A / Mode B tracking-session lifecycle only** (R-01). Active-session mutex via Redis `lock:driver:{driverId}` + Postgres UNIQUE partial index (D-03). Idle timer 30 min, auto-end at end-position geofence 100 m, 5-min grace restart, trip rating capture. **Mode C ride lifecycle moved to `ride-svc`** | Separate pod |
| **`ride-svc`** *(new, R-01)* | **Sole authoritative writer of the Mode C Ride Aggregate** across three sub-kinds: `passenger`, `proxy` (booker ≠ rider — P-01), `package` (P-06). State machine: `Requested → Matching → Offered → Accepted → DriverArrived → InProgress → Completed → PaymentPending → Paid / CashSettled / CashOnDeliveryCollected / Disputed`. **Atomic single-winner accept** (R-02). **MassTransit saga** + **Quartz.NET clustered scheduler** for durable timers (offer expiry, arrival grace, no-show, payment, location-request TTL, OTP window, COD uncollected — R-04). **Transactional outbox** with Postgres `LISTEN/NOTIFY` sub-50 ms wake-up (E-09). **Proxy booking**: FCM location-request round-trip (P-02), driver offer carries `is_proxy` badge (P-05). **Package delivery**: generates 4-digit pickup/delivery OTPs (HMAC-SHA256 hashed at rest), max 5 attempts each (P-07); proof-photo upload (P-10). **Idempotent ride commands** via `rides.command_log` (R-14). Consumes `share.revoked`, `wallet.debited`, `payment.settled`, EMQX LWT events | Separate pod, sharded by rideId |
| **`dispatch-svc`** | **Mode C candidate generation, scoring, and offer dispatch** — ride state owned by `ride-svc`. Candidate index from Redis `geo:drivers:available:{type}:{cell}` (H3 res-5 pre-filter + PostGIS `ST_DWithin` exact — D-06). **Phase 1 = sequential matching** (R-12). **Redis Lua atomic reservation** + Postgres UNIQUE constraint (R-10). **Versioned weighted scoring** persisted to `dispatch.candidate_scores` (R-11). **Pre-dispatch wallet gate**: reads `wallet:bal:{driverId}` cache; first trip free, 2nd+ refused if balance < fee (D-08). **Job Board**: PostGIS `ST_DWithin(pickup, driver_home, 30 km)`. **Driver Level System**: L1 = no Job Board / scheduled rides. **Cancellation penalty Rs 50** outbox (D-05). Consumes `reputation-svc.block_status` and EMQX LWT to release stale offers (R-15) | Separate pod |
| **`fare-svc`** | **Mode C fare** (1st-km + per-km + peak/night surcharges) with **Kalman-filter + accuracy-weighted resample** on raw GPS (E-04). **Upfront fare estimation**. **In-app ride payment** with state machine: `Initiated → Pending → Succeeded / Failed / Retried / FellBackToCash / CashOnDelivery / CashOnDeliveryCollected / QrClaimedByPassenger / DriverConfirmedQR / Overpaid / Refunded / Disputed` (D-10, AL-47). **Driver-QR attestation (AL-47):** scan-driver-QR payments have no gateway callback — passenger "I've paid" claim + driver confirm → terminal `DriverConfirmedQR` (settles like cash; disputes → Support/Finance). **Late-callback handler**: provider `Succeeded` after `FellBackToCash` → `Overpaid` → admin refund queue (R-19). **Tip capture** (E-10). **Refund/dispute workflow** via OnePay/LankaQR reverse APIs (E-05). **Cross-trip cancellation settlement** (D-05). Driver earning posts **only on payment terminal state** (R-05). **Proxy-booking payment routing**: `Cash` → rider, `LankaQR`/`OnePay` → booker (P-04). **Package COD** (P-08). **Mode B has no per-trip fare** | Separate pod |
| **`subscription-svc`** | **7-tier daily fee rates** (Mode A = Free, Motorbike Rs 50, Three-wheeler Rs 100, Flex Rs 150, Sedan Rs 200, Mini Van Rs 250, Van Rs 300 — admin-configurable). **First trip free**; idempotent fee deduction before 2nd trip per calendar day per vehicle (`PK: driver_id, vehicle_id, fee_date` in `Asia/Colombo` — D-13). Mode B **monthly ~Rs 300** (first month free). **Driver-to-driver credit transfers move the exact value — no commission, no reseller role (AL-01)**; the informal reseller's margin is the bulk-voucher purchase discount only. **Bulk credit voucher issuance** (Rs 1,000–10,000 denominations, discount % configured per denomination in DB, applied at purchase, credited to the buyer's own wallet). No per-trip charging. | Separate pod |
| **`wallet-svc`** | Driver wallet balances on a **double-entry ledger** (`billing.accounts`, `billing.journal_entries`, `billing.journal_postings` — every mutation balanced, idempotent on `Idempotency-Key` — D-09). **In-app top-ups via OnePay card / OnePay wallet / LankaQR only — bank-transfer top-ups removed (AL-05)**. **Bulk credit voucher** purchase (discount on purchase only) and **commission-free driver-to-driver transfer ledger** (Rs 1,000–10,000 denominations; exact value debited/credited — AL-01). Publishes `wallet.debited` / `wallet.credited` events → invalidates `dispatch-svc` Redis balance cache | Separate pod |
| **`notification-svc`** | Push notifications via **FCM HTTP v1 batch send** + **APNs HTTP/2** with exponential-backoff worker. **Dispatch-offer push uses high-priority paths** (E-01): Android `priority=high` (bypasses Doze), iOS `apns-priority:10` + `content-available:1`; 3 s ack-wait → SMS fallback. SMS via primary (Fit SMS — AL-60) + secondary (Dialog/Mobitel) for SOS. Renders templates from `content-svc` in user's language (Si/Ta/En). **SOS p99 ≤ 5 s** (D-33). **Proxy-booking location request** FCM (P-02, P-12): per-booker rate-limit 5/h, 30/day. **Package recipient notify** (P-09): FCM or SMS with trip-share-token link. Document-expiry warnings (E-03) | Separate stateless pod |
| **`safety-svc`** | SOS for **both passengers and drivers** (location + trip context → SMS via primary + secondary gateway in parallel, **fan-out to admin live-feed WebSocket channel** — D-33). **Live-trip share token** (bound to tripId, valid until trip end + 1 h, rate-limited 60 req/min, revocable, no historical replay — D-34). "Report Vehicle" intake feeding `reputation-svc`. **Passenger block-driver** list consulted by `dispatch-svc` and `fanout-svc`. Admin review queue | Separate pod |
| **`reputation-svc`** *(new, D-04)* | Unifies cancellation, no-show, vehicle-report counters with **rolling-window reset**. Exposes `block_status` (`OK / WARN / BOOKING_DISABLED / DELISTED`) and `driver_level` via gRPC. **Anti-collusion detector** (E-07): flags pair frequency (same `(passenger, driver)` > N rides / 30 d), device-binding cross-check, IP/ASN clustering; emits `fraud.suspected` for admin review | Separate pod |
| **`content-svc`** *(new, D-26)* | Localised content store (Sinhala/Tamil/English): notification templates, FAQ articles, admin broadcasts, fare-tariff display strings. Versioned with admin approval workflow | Separate pod, heavily cached |
| **`support-svc`** *(new)* | **In-app FAQ** article management, **support ticket** creation (with trip ID / screenshot attachment), ticket status tracking, admin ticket queue and resolution | Separate pod |
| **`fleet-svc`** | **Phase 1 (AL-03)** — backs the **Fleet Portal** (`fleet.mageride.lk`, SCR-FP-001…012): fleet-org onboarding/KYC gate, **bank & payout profile (SCR-FP-002a, `registry.fleet_payout_profiles` — AL-49)**, Mode A/B vehicle onboarding (single + bulk CSV, **named document slots incl. route permit — AL-50**, **"Service payment" Paid/Free — AL-51**), vehicle↔driver assignment, tracker binding, per-fleet map scoping (row-level security), scheduling & not-started alarms, analytics, monthly per-Mode-B-vehicle billing, **Mode B subscriptions & per-subscriber payment ledger** (pass-through to owner). Route-deviation/geofence alerts remain Phase 3 | Separate pod |
| **`admin-bff`** | Operator console (Admin Portal `admin.mageride.lk`, AL-02; staff sign-in = password/Google, **no MFA — AL-37**) with **audit interceptor on every mutation** (D-35). Vehicle/driver suspend, **train (Mode A) registration & lifecycle (admin-only)**, fare tariff & daily fee configuration, **bulk-voucher discount-% per denomination (AL-01 — no reseller role)**, vehicle report review, broadcast announcements, support tickets, **passenger/driver/vehicle directories + dashboard stats filter (AL-38…42)**, **GTFS Dataset Manager (SCR-AP-016 — upload/validate/preview/activate/rollback, proxied to `transit-svc`, AL-54)**. **PDPA workflow** (E-06): data-export fulfilled within 30 d (signed ZIP); data-erasure soft-anonymises within 30 d with statutory hold list. **Refund queue**, **document-expiry queue**, **fraud-review queue**. *In production the PDPA workflow is extracted to its own `pdpa-svc` pod; replica folds it into `admin-bff` to save a process.* | Separate pod (+ `pdpa-svc` pod in production) |
| **`ocr-svc`** | Document extraction (Gemini Flash + Tesseract fallback). **PII redaction pre-pass** (D-36): OpenCV face-blur + Tesseract bounding-box ID-number masking **before** any data leaves perimeter to Gemini. Raw documents in object storage with SSE-KMS, 90-day auto-delete | Separate queue-driven pod |
| **`transit-svc`** *(new, AL-18 — Phase 1)* | **GTFS public-transport routing.** Serves `GET /transit/options?from&to` — **all DIRECT bus/train routes** (route no, headsign, shape polyline) + TRANSIT (≥1 transfer) options for the passenger booking screen (SCR-PA-009). **Versioned feed lifecycle (AL-54):** `/admin/transit/gtfs/uploads*` — full-zip upload (sha256 dedupe) → async validation + row-level report → **atomic activation** (`transit_staging.gtfs_*` load → one-transaction swap → `NOTIFY transit_feed_activated` → cache reload ≤ 60 s) → history/rollback (`transit.gtfs_feed_versions`, exactly one `active`). **Full national feed loads day-0 via SCR-AP-016 before Mode A tests (AL-55).** Also hosts `GET /geo/parse-maps-link` — resolves short Google-Maps URLs → lat/lng for the **Paste-link** input (AL-20). No Google API used | Separate pod, reads Postgres replica |
| **`public-bff`** *(new, AL-44)* | **No-login Passenger Web subview** (`passenger.mageride.lk`, SCR-WT-001…006): token-scoped `GET /public/track/{token}` snapshot + SSE live feed, unregistered-rider **pickup-confirm** (AL-45), **tap-to-call `tel:` link** (driver's number in the snapshot — AL-48; the `/call` DID-lease endpoint was removed), **web SOS**, **receipt**. The `trip_share_tokens` token is the only credential — per-token + per-IP rate limits, scope-shaped payloads (P-02/P-09), zero data on dead tokens | Separate thin stateless pod |

> [!NOTE]
> All 21 domain services are co-located behind a single YARP gateway process. Each service is a **separate class library** with its own DI registration, so extracting to individual containers for production is a configuration change, not a code change.

---

### Container 8 — `fanout` (SignalR WebSocket Server)

| Property | Value |
|---|---|
| **Image** | Custom ASP.NET Core SignalR image (.NET 10 LTS) |
| **RAM / CPU** | 2 GB / 1 vCPU |
| **Ports** | `5001` (WSS, behind HAProxy) |
| **Role** | WebSocket fan-out to passenger/driver apps |

**Services inside:**

| Service | Responsibility |
|---|---|
| **`fanout-svc`** | Manages WebSocket sessions, SignalR groups keyed by `cell:{h3index}`, consumes Redis cell streams (`XREAD`), broadcasts position updates to subscribed geocell groups. **Public map visibility filter**: fans out only **Mode A (buses & trains)** and entitled **Mode B** positions; **Mode C vehicles on an active hire are excluded from public groups** (sent only to the assigned ride's passenger), and **stale/offline vehicles** (GPS off / app offline / EMQX LWT `status=offline`) are dropped until live ingest resumes. **Mode B entitlement cache** in Redis `share:{userId}` (SET, pub/sub-invalidated — D-23) checked on group-join. Listens to `share.revoked` events and pushes **directed `RemoveFromGroupAsync`** immediately (D-22). **Proxy-booking location-request round-trip** fan-out to booker WebSocket (P-13) |

**Backplane:** Redis pub/sub (same Redis instance in Container 4).

**Production equivalent:** 3–50 SignalR pods with sticky sessions via HAProxy `source` hash, Redpanda backplane at scale (Kafka-API compatible).

> [!WARNING]
> **Restart blast radius.** Because the replica runs a single `fanout` container, any restart (deploy, OOM, crash) drops **every connected WebSocket simultaneously**. Mobile clients per ADD §18.2 implement exponential-backoff reconnect, but the reconnect storm hits all clients at once rather than the 1-of-N rolling effect in production. Schedule deploys for low-traffic windows.

---

### Container 9 — `tcp-adapter` (Hardware GPS Tracker Ingest)

| Property | Value |
|---|---|
| **Image** | Custom .NET 10 LTS background worker |
| **RAM / CPU** | 512 MB / 0.5 vCPU |
| **Ports** | `5023` (GT06 TCP), `5024` (JT808 TCP), `5025` (H02 TCP), `5026` (NMEA UDP) — all via HAProxy L4 passthrough |
| **Role** | Decodes hardware GPS tracker binary protocols → normalises to canonical `PositionSample` → publishes to EMQX as `veh/{vehicleId}/pos/live` (or `pos/replay` for batched backlog) |

**Protocol coverage (ADD §7.7.1):**

| Family | Transport | Notable Devices | Internal Worker |
|---|---|---|---|
| **GT06 / GT06N** | TCP, binary framed | Concox GT06, TK103, **ST-901/ST-902 clones** | `adapter-gt06` |
| **JT/T 808** | TCP, binary framed | Chinese standard trackers, many SL imports | `adapter-jt808` |
| **H02 / H02X** | TCP, ASCII pipe-delimited | Older bus trackers | `adapter-h02` |
| **Generic UDP-NMEA** | UDP | Low-cost asset trackers | `adapter-nmea-udp` |

> [!NOTE]
> **MQTT-capable trackers** (Teltonika, Queclink newer FW with NMEA-over-MQTT) bypass the adapter entirely and connect to EMQX directly, authenticated with the same per-device credentials from `provisioning-svc`.

**Light replica approach:** All 4 protocol workers run as separate `IHostedService` instances in a **single .NET process** on one container. In production, each protocol family is a **separate StatefulSet** with 3 pods × 10k sockets, sticky-hashed by IMEI.

**Key behaviours:**
- Validates IMEI against `provisioning-svc` Redis cache on every connection; re-validates every 5 min on long-lived sockets (T-12)
- **Hot-path cache short-circuit:** `tcp-adapter` reads `imei:{imei}` **directly from Redis** on every connect (sub-millisecond). It calls `provisioning-svc` over the API gateway **only on cache miss** — so `provisioning-svc` is sized for low, bursty traffic and must not be HPA'd on socket-establishment rate. Cache invalidation is push-based via Redis pub/sub (`imei.revoked`), so revocation propagates without the adapter polling.
- **Anti-clone detection**: two sockets with same IMEI in 24 h → both quarantined (T-08)
- **Offline replay**: routes tracker backlog burst to `veh/{vehicleId}/pos/replay` topic, rate-limited 20 msg/s/device, monotonic `seq` dedup (T-05)
- **Downlink commands**: subscribes to `veh/{vehicleId}/cmd`, translates canonical JSON → protocol-native binary command frame over open socket (§7.7.5)
- **LWT emulation**: publishes `status=offline` retained message on socket half-close (T-04)

**Production equivalent:** Separate StatefulSet per protocol family, behind NLB consistent-hash, 3 pods × 10k sockets each.

---

### Container 10 — `minio` (S3-Compatible Object Storage)

| Property | Value |
|---|---|
| **Image** | `minio/minio:latest` |
| **RAM / CPU** | 256 MB / 0.25 vCPU |
| **Ports** | `9000` (API, behind HAProxy passthrough), `9001` (Console) |
| **Storage** | 50 GB volume |
| **Role** | S3-compatible local object storage for document uploads (`ocr-svc` raw docs), proof-of-delivery photos, driver profile pictures, and Postgres backups. Serves as a local drop-in replacement for Cloudflare R2 / AWS S3. |

**Production equivalent:** Cloudflare R2, Wasabi, or AWS S3. In a fully self-hosted national scale deployment, a multi-node MinIO cluster.

---

### Optional Container — `voip` (LiveKit SFU + TURN Relay)

| Property | Value |
|---|---|
| **Image** | `livekit/livekit-server` + `coturn/coturn` (multi-service compose) |
| **RAM / CPU** | 1 GB / 0.5 vCPU |
| **Ports** | `7880` (LiveKit HTTP — via HAProxy), `7881` (LiveKit RTC — direct host), `3478/UDP` (TURN handshake — direct host), `50000-50100/UDP` (TURN media relay range — direct host, NOT via HAProxy) |
| **Role** | In-app voice between passenger ↔ driver — the **"Free call"** option (D-24; masking is no longer a requirement, AL-48). Signalling tokens are tripId-scoped, expire at trip end. **VoIP failure → "Call normally instead?" direct-dial prompt** to the real number (D-25 masked-SMS relay removed, AL-48). Recordings off by default (PDPA) |

> [!WARNING]
> coturn **must** be configured with `min-port=50000` / `max-port=50100` and that UDP range exposed directly on the Docker host (`network_mode: host` or explicit `-p 50000-50100:50000-50100/udp`). Without the media-relay range, TURN-relayed calls fail end-to-end whenever a peer sits behind symmetric / carrier-grade NAT (the common case on Sri Lankan mobile carriers).

> [!TIP]
> Only deploy this container when testing VoIP features. For tracking-only or ride-dispatch pilot, skip it. The `voip-svc` signalling REST layer runs inside `app-services` and gracefully returns "VoIP unavailable" if LiveKit is not reachable.

**Production equivalent:** 2× LiveKit SFU pods (4 GB / 2 vCPU each, 500 concurrent calls) + 2× coturn TURN pods (2 GB / 1 vCPU each).

---

### Optional Container — `admin-portal` (Admin Portal — `admin.mageride.lk`, AL-02)

| Property | Value |
|---|---|
| **Image** | Custom Next.js (Node 24-alpine — Δ 2026-07-23, was Node 20: EOL Apr 2026) |
| **RAM / CPU** | 512 MB / 0.25 vCPU |
| **Ports** | `3001` (HTTP, behind HAProxy at `admin.mageride.lk`) |
| **Role** | **The single back-office web app for all six internal roles** (Sinhala/Tamil/English i18n): verification queues + full-size document viewer, moderation, support & disputes, **finance & OnePay/LankaQR settlement reconciliation (no bank transfer — AL-05)**, platform config (fare tariffs, daily fees, **bulk-voucher discount % per denomination — AL-01**), RBAC, audit trail, **passenger/driver/vehicle directories**, dashboard stats filter, **GTFS Dataset Manager (SCR-AP-016 — day-0 full-feed upload/activate + rollback, AL-54/55)**. Staff sign-in = password/Google, **no MFA (AL-37)**. Styled exclusively with **Tailwind CSS** (AL-52 — compiled at build, no runtime dependency; same for `fleet-portal`) |

**Calls:** `admin-bff` via the same `app-services` API gateway (shared `iam-svc` JWT).

### Optional Container — `fleet-portal` (Fleet Portal — `fleet.mageride.lk`, AL-03)

| Property | Value |
|---|---|
| **Image** | Custom Next.js (Node 24-alpine — Δ 2026-07-23, was Node 20: EOL Apr 2026) |
| **RAM / CPU** | 512 MB / 0.25 vCPU |
| **Ports** | `3002` (HTTP, behind HAProxy at `fleet.mageride.lk`) |
| **Role** | **Fleet-owner web app (Phase 1** — SCR-FP-001…012 + 002a): org onboarding/KYC gate, **bank & payout details (SCR-FP-002a — statement/passbook + bank-app LankaQR uploads, AL-49)**, Mode A/B vehicle onboarding (single + bulk CSV, named document slots incl. route permit — AL-50, "Service payment" Free/Paid — AL-51), driver assignment, tracker binding, org-scoped live fleet map, scheduling & not-started alarms, analytics, billing & fleet wallet, **Mode B subscriptions & per-subscriber payment ledger** |

**Calls:** `fleet-svc` routes via the same gateway, org-scoped row-level security.

> [!TIP]
> For MVP pilot you can skip both portal containers and host them on **Vercel** or **Cloudflare Pages** for free — they only call the platform API. Self-host on the VPS only if you want a single-domain deployment. Driver wallet top-ups are **in-app** (AL-05); neither portal carries an end-user payment hot path.

---

### Optional Container — `nominatim` (Self-Hosted Geocoding)

| Property | Value |
|---|---|
| **Image** | `mediagis/nominatim:4.4` |
| **RAM / CPU** | **8 GB / 1 vCPU** (SL OSM extract fits in RAM) |
| **Ports** | `8080` (internal HTTP) |
| **Role** | Self-hosted forward/reverse geocoding for Sri Lanka extract. Refreshed by weekly `osm-pipeline` CronJob (diff → osm2pgsql → tippecanoe → PMTiles → R2 sync) |

> [!WARNING]
> Nominatim requires **8 GB RAM** for the Sri Lanka OSM extract, which is a third of the 24 GB budget. **Recommended for light replica: host Nominatim on a separate cheap VPS** (e.g., €5/mo Contabo 8 GB) or use an external geocoding API (Geoapify free tier / Nominatim public instance). Only co-locate if you have RAM headroom during development.

**Production equivalent:** Dedicated 8 GB / 2 vCPU Postgres with Nominatim + 1 read replica from Phase 2.

---

### Batch Container — `osm-pipeline` (Weekly Tile + Geocoder Refresh)

| Property | Value |
|---|---|
| **Image** | Custom (osm2pgsql + tippecanoe + go-pmtiles + awscli for R2 sync) |
| **RAM / CPU** | 2 GB / 1 vCPU (only while running) |
| **Schedule** | Weekly via host `cron` or Compose `profiles: [batch]` one-shot (`docker compose run --rm osm-pipeline`) |
| **Role** | D-15 / T pipeline: download SL OSM diff → osm2pgsql into Nominatim → tippecanoe → PMTiles → sync to Cloudflare R2 → emit `tiles.refreshed` event |

> [!NOTE]
> In production this runs as a Kubernetes `CronJob`. In the replica it is **not** part of the always-on container set — it is invoked on schedule and exits, so it does not consume the 24 GB steady-state budget.

---

## Durability & Backpressure Assumptions

Replica durability windows (per-store, on hard kill):

| Store | Setting | Worst-case data loss |
|---|---|---|
| PostgreSQL (incl. TimescaleDB) | `synchronous_commit=on`, `fsync=on` | 0 (single-node; replica still loses unreplicated data, but no commits) |
| Redis | `appendonly yes`, `appendfsync everysec` | ≤ 1 s of writes |
| Redpanda | `fsync=on` per produce-ack, RF=1 (single node) | 0 on process kill; full topic on disk loss |
| EMQX | `persistent_session=true`, `session_expiry_interval=2h`, message store on disk | ≤ flush interval (~1 s) for in-flight QoS1 |

Replica backpressure caveats:

- The `hot-path` container couples `mqtt-bridge`, `position-processor`, `persistence-writer`, and `fleet-health` in one .NET process. A stall in any one pauses the hot path. Production splits these into independent pods.
- A single `fanout` container = single-pod SignalR. Deploys drop all WebSocket sessions simultaneously.
- A single Redis instance is the SPOF for both live geo state and the wallet/entitlement caches. Loss of Redis triggers degraded-mode rules in `dispatch-svc` (D-08) but `fanout-svc` group state must rebuild from `cell:{h3index}` stream replay.
- A single Redpanda node is the SPOF for the durable event backbone. Acked events survive process kill (fsync per ack), but the broker's disk volume is a single point of failure; production runs RF=3 across 3 brokers.

---

### Optional Container — `monitoring` (Observability)

| Property | Value |
|---|---|
| **Image** | `prom/prometheus` + `grafana/grafana` |
| **RAM / CPU** | 1 GB / 0.5 vCPU |
| **Ports** | `3000` (Grafana), `9090` (Prometheus) |
| **Role** | Basic metrics collection and dashboards. Production adds Loki (logs), Tempo (traces), Alertmanager, and OpenTelemetry Collector |

---

## Resource Summary

| Container | RAM | vCPU | Notes |
|---|---|---|---|
| `haproxy` | 256 MB | 0.25 | Edge routing + per-protocol TCP passthrough |
| `emqx` | 2 GB | 1.0 | MQTT broker + rule engine bridge |
| `redpanda` | 1 GB | 0.5 | Event backbone (Kafka-API, single broker RF=1) |
| `redis` | 1.5 GB | 0.5 | Live state + geo + dispatch + ride locks |
| `postgres` | 4 GB | 1.0 | System of record + TimescaleDB hypertable |
| `pgbouncer` | 128 MB | 0.25 | Postgres transaction-mode pooler (separate container, M-5) |
| `hot-path` | 2 GB | 1.0 | Position processor + persistence + fleet-health |
| `app-services` | 3 GB | 1.5 | 21 domain services behind YARP (incl. `transit-svc` + `public-bff`; embedded step-ca in `provisioning-svc`) |
| `fanout` | 2 GB | 1.0 | SignalR WebSocket fan-out |
| `tcp-adapter` | 512 MB | 0.5 | Hardware tracker protocol translation (all protocols) |
| `minio` | 256 MB | 0.25 | S3-compatible local object storage |
| **Total (core 11)** | **~16.7 GB** | **~6.75** | **Fits comfortably in 24 GB VPS** |
| `voip` *(optional)* | 1 GB | 0.5 | LiveKit SFU + coturn (skip for tracking-only pilot) |
| `admin-portal` *(optional)* | 512 MB | 0.25 | Next.js Admin Portal `admin.mageride.lk` (or host on Vercel/CF Pages) |
| `fleet-portal` *(optional)* | 512 MB | 0.25 | Next.js Fleet Portal `fleet.mageride.lk` (or host on Vercel/CF Pages) |
| `nominatim` *(optional)* | 8 GB | 1.0 | **Host on separate VPS** — too heavy to co-locate |
| `monitoring` *(optional)* | 1 GB | 0.5 | Prometheus + Grafana |
| `osm-pipeline` *(batch, on-demand)* | 2 GB | 1.0 | Weekly one-shot; not part of steady-state RAM |
| **Total (core 11 + voip + both portals + monitoring)** | **~19.7 GB** | **~8.25** | **Fits in 24 GB with headroom** |

> [!TIP]
> With a 24 GB VPS, you can run all core containers + VoIP + both web portals + monitoring comfortably (~19.7 GB), leaving ~4.3 GB for OS overhead and buffer. Nominatim should be hosted separately. The `osm-pipeline` batch container runs on schedule and is not part of the steady-state budget.

---

## Data Flow — Exact Production Path Preserved

```
Driver/Tracker App
       │
       │ MQTT (QoS1, TLS)                    ST-901/GT06/JT808/H02
       │                                            │
       ▼                                            │ Raw TCP/UDP
┌─────────────┐                              ┌──────┴───────┐
│  HAProxy    │◄──── L4 TCP passthrough ────►│ tcp-adapter  │
│  (TLS term) │                              │ (per-proto)  │
└──────┬──────┘                              └──────┬───────┘
       │                                            │
       │ WSS/HTTPS                    MQTT PUB (mTLS internal)
       │                                            │
       ▼                                            ▼
┌─────────────┐     EMQX Rule Engine      ┌──────────┐
│  EMQX       │◄─── or mqtt-bridge ──────►│ Redpanda │
│  (broker)   │                            │ (Kafka)  │
└─────────────┘                            └────┬─────┘
                                                │
                    ┌──────────────┬─────────────┼──────────────┬───────────────┐
                    ▼              ▼             ▼              ▼               ▼
             ┌────────────┐ ┌────────────┐ ┌──────────┐ ┌────────────┐ ┌──────────────┐
             │ position-  │ │ persist-   │ │ trip-    │ │ fleet-     │ │ ride-svc     │
             │ processor  │ │ writer     │ │ state   │ │ health     │ │ (Mode C)     │
             └─────┬──────┘ └─────┬──────┘ │ (A/B)   │ └────────────┘ └──────────────┘
                   │              │        └──────────┘
              GEOADD/XADD    Batch INSERT
                   │         (Postgres + TimescaleDB)
                   │              │
                   ▼              ▼
             ┌──────────┐  ┌──────────┐
             │  Redis   │  │ Postgres │
             └─────┬────┘  └──────────┘
                   │
              XREAD (cell streams)
                   │
                   ▼
┌─────────────┐         ┌──────────┐
│  Passenger  │◄── WSS ─│ fanout   │
│  App        │         │ (SignalR)│
└─────────────┘         └──────────┘

Domain API path (non-hot):

  Mobile Apps ──► HAProxy ──► YARP Gateway ──► { iam, registry, provisioning,
                                                  query, trip-state, ride-svc,
                                                  dispatch, fare, subscription,
                                                  wallet, notification, safety,
                                                  reputation, content, support,
                                                  fleet, transit, public-bff,
                                                  admin-bff, ocr }
                                                         │
                                                    ┌────┴────┐
                                                    │ Postgres│
                                                    └─────────┘
```

---

## Docker Compose Service Map

```yaml
# docker-compose.light-replica.yml (conceptual)
services:

  haproxy:          # Container 1  — Edge (HTTPS/WSS + MQTTS + per-protocol TCP tracker ports)
  emqx:             # Container 2  — MQTT Broker (EMQX 5.8 + rule engine bridge to Redpanda)
  redpanda:         # Container 3  — Event Backbone (Redpanda single broker, Kafka-API compatible, RF=1)
  redis:            # Container 4  — Live State (geo, dispatch, ride locks, wallet cache, entitlements)
  postgres:         # Container 5  — System of Record (TimescaleDB-HA pg16: PostGIS + Timescale + 18 schemas)
  hot-path:         # Container 6  — position-processor + persistence-writer + mqtt-bridge + fleet-health-svc
  app-services:     # Container 7  — YARP gateway + iam + registry + provisioning + query + trip-state
                    #                + ride-svc + dispatch + fare + subscription + wallet + notification
                    #                + safety + reputation + content + support + fleet + transit
                    #                + public-bff + admin-bff + ocr
  fanout:           # Container 8  — SignalR WebSocket fan-out (geocell groups + Mode B entitlement + proxy booking WS)
  tcp-adapter:      # Container 9  — Hardware tracker protocol translation (GT06/JT808/H02/NMEA-UDP, all-in-one)
  minio:            # Container 10 — S3-Compatible local object storage
  voip:             # Container 11 — (optional) LiveKit SFU + coturn TURN relay
  admin-portal:     # Container 12 — (optional) Next.js Admin Portal admin.mageride.lk (or Vercel/CF Pages)
  fleet-portal:     # Container 13 — (optional) Next.js Fleet Portal fleet.mageride.lk (or Vercel/CF Pages)
  nominatim:        # Container 14 — (optional) Self-hosted geocoding (host on separate VPS — 8 GB RAM)
  monitoring:       # Container 15 — (optional) Prometheus + Grafana

networks:
  internal:         # All containers on a single bridge network
    driver: bridge
```

---

## Upgrade Path to Production

When you're ready for production (10k vehicles / 100k passengers) — **target substrate: DigitalOcean Kubernetes (DOKS), Singapore region** (hosting decision 2026-07-05; the Contabo EU box stays behind as the testing replica):

| Step | Change |
|---|---|
| 1. Split `hot-path` container | Deploy `position-processor-svc`, `persistence-writer-svc`, `mqtt-bridge-svc`, `fleet-health-svc` as **4 separate containers** |
| 2. Split `app-services` container | Deploy each domain service (`iam-svc`, `registry-svc`, `ride-svc`, `dispatch-svc`, `reputation-svc`, etc.) as **individual containers** |
| 3. Split `tcp-adapter` container | Deploy **one StatefulSet per protocol family** (`adapter-gt06`, `adapter-jt808`, `adapter-h02`, `adapter-nmea-udp`) with sticky-hash by IMEI |
| 4. Add replicas | Scale `fanout-svc` to 3, `position-processor` to 2–3, `ride-svc` to 2, domain services to 2 each |
| 5. Add HA to infra | EMQX → 2-node cluster, Redpanda → 3-node cluster (RF=3), Redis → 3× Sentinel, Postgres → Patroni 1P+2R |
| 6. Add second HAProxy | Enable Keepalived VRRP between 2 HAProxy instances |
| 7. Deploy VoIP pods | LiveKit SFU × 2 (4 GB / 2 vCPU each) + coturn × 2 (2 GB / 1 vCPU) — 500 concurrent calls |
| 8. Deploy Nominatim | Dedicated 8 GB / 2 vCPU Postgres with Sri Lanka OSM extract |
| 9. Separate TimescaleDB | Move `telemetry.positions` hypertable to dedicated Postgres cluster (or Citus distributed node) |
| 10. Move to K8s | Same Docker images, wrap in K8s manifests (Deployments, Services, StatefulSets) — deployed to **DigitalOcean DOKS, Singapore** (LiveKit/coturn pinned to SGP; evaluate a Colombo TURN node during pilot) |
| 11. Add full observability | Deploy Loki, Tempo, Alertmanager, OpenTelemetry Collector alongside Prometheus + Grafana |
| 12. Migrate sagas (Phase 3) | Replace MassTransit saga + Quartz.NET with **Temporal.io** for ride workflow orchestration |

> [!IMPORTANT]
> **Zero code changes required** for steps 1–11. The upgrade is purely operational — container topology and replica counts. Step 12 (Temporal.io) is a Phase 3 re-implementation of the saga runner, not a domain logic change.

---

## What This Light Replica Proves

- ✅ Every production service boundary exists and runs independently
- ✅ The exact MQTT → Redpanda → Redis → SignalR hot path works end-to-end
- ✅ Geocell (H3) fan-out model is exercised
- ✅ Trip state machine (Mode A/B) runs as a separate concern from ride aggregate (Mode C)
- ✅ **Mode C ride aggregate** (passenger + proxy booking + package delivery) state machine exercised end-to-end
- ✅ **Proxy booking** FCM location-request round-trip testable
- ✅ **Package delivery** OTP verification flow (pickup + delivery) works end-to-end
- ✅ **Atomic single-winner dispatch** with Redis Lua reservation exercised
- ✅ Persistence is decoupled from the hot path
- ✅ **TimescaleDB hypertable** ingestion for high-frequency telematics testable
- ✅ Domain services are separated by bounded context
- ✅ Hardware tracker adaptation path (TCP → MQTT) is testable — **all 4 protocol families** (GT06/JT808/H02/NMEA-UDP)
- ✅ **Per-fleet tracker health aggregation** exercised
- ✅ **Reputation and anti-collusion pipeline** exercised
- ✅ **VoIP signalling path** testable (if VoIP container deployed)
- ✅ **PDPA data-export/erasure workflow** testable
- ✅ **Double-entry ledger** wallet operations exercised
- ✅ **GTFS transit options** (`transit-svc` direct/transit routes for the booking screen + paste-link parsing, AL-18/20) testable
- ✅ **GTFS feed lifecycle** (SCR-AP-016: full-zip upload → validation report → atomic activate → rollback; `transit_staging` swap + cache reload — AL-54/55) exercised end-to-end, incl. the **day-0 full-feed load before any Mode A booking test**
- ✅ **No-login Passenger Web subview** (`public-bff` `/public/track/*` token flows — recipient track, pickup-confirm, tap-to-call link, web SOS, receipt; SCR-WT-001…006) testable
- ✅ **Driver-QR attestation settlement** (claim → confirm → `DriverConfirmedQR`, nudge timer, dispute path — AL-47) exercised end-to-end
- ✅ **Mode B subscription payments** (`subscription.*` pass-through ledger, owner confirm) exercised
- ✅ Capacity: ~100 vehicles (mobile + hardware mix), ~1,000 passengers — sufficient for dev, demo, and pilot
