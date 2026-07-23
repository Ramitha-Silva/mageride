# MageRide — Mobile App Database Schema (On-Device SQLite)

> **On-device persistence for the four MageRide apps** — Passenger Android, Passenger iOS, Driver
> Android, Driver iOS — built on **KMP shared logic + Jetpack Compose / SwiftUI** (ADD §18.2).
> Derived from `architecture-design-document.md` (R-14, R-17, R-18, E-01, §7.5 cadence, §11.13 offline
> replay, §18.2) and `mageride-specs/` (D1 user flows, D2 UI spec, D3 API contracts, D4 data model).
>
> **The phone is a cache + outbox + sensor buffer, never a source of truth.** The server
> (`server_db_schema.md`) owns all canonical state. Every table here is one of:
> 1. **Outbound durable queue** — work the app *must* deliver to the server even across crashes/offline
>    (GPS replay buffer R-17, command outbox R-14/R-18, proof-photo upload queue P-10).
> 2. **Read cache / projection** — a local copy of server entities so screens render instantly and
>    survive offline (US-15.1/15.2/15.6), reconciled on sync.
> 3. **Local-only UI state** — search history, draft inputs, downloaded map bundle metadata.

---

## 0. Engine, Storage Strategy & Conventions

### 0.1 Engine & single-sourcing across platforms

| Platform | SQLite access layer |
|---|---|
| Android (Driver / Passenger) | **Room** (`@Entity`/`@Dao`), Room-managed `user_version` migrations |
| iOS (Driver / Passenger) | **SQLite** via GRDB.swift (or raw `sqlite3`) |
| KMP (recommended, optional) | **SQLDelight** — single `.sq` schema compiled for both targets, so the DDL below is authored once |

> The DDL in this document is the **canonical schema**. Room `@Entity` definitions (Android) and the iOS
> SQLite layer must produce byte-compatible tables. Adopting **SQLDelight** in `shared/commonMain` is the
> recommended way to keep all four apps on one schema; the statements below are valid SQLDelight/SQLite.

### 0.2 One database file per app

Each of the four apps ships its **own** database file — there is no shared file across apps on a device,
even when Driver + Passenger are both installed (single-active-device is **per app**, AL-08).

| App | DB file | Table groups instantiated |
|---|---|---|
| Passenger (Android/iOS) | `mageride_passenger.db` | §1 Shared + §2 Passenger |
| Driver (Android/iOS) | `mageride_driver.db` | §1 Shared + §3 Driver |

### 0.3 Type mapping (PostgreSQL → SQLite)

| Server type | SQLite storage | Convention |
|---|---|---|
| `UUID` | `TEXT` | lowercase hyphenated UUID, or ULID string |
| `TIMESTAMPTZ` | `INTEGER` | **epoch milliseconds, UTC**. Display/business-date math converts to `Asia/Colombo` in code (kotlinx-datetime) |
| business `DATE` (Asia/Colombo) | `TEXT` | `'YYYY-MM-DD'` already in `Asia/Colombo` (daily fee, earnings) |
| `BOOLEAN` | `INTEGER` | `0` / `1` |
| money `*_minor` | `INTEGER` | Rs × 100; balances may be negative |
| `GEOGRAPHY(POINT)` | two `REAL` cols | `*_lat REAL`, `*_lng REAL` (no PostGIS on device) |
| `JSONB` | `TEXT` | JSON string (SQLite JSON1 functions available) |
| `BYTEA` (hashes/OTP) | — | **never stored on device**; OTPs are entered live and sent to the server |

### 0.4 Security

- **Secrets are NOT in SQLite.** Access/refresh JWTs, the device-bound private key, and MQTT session
  tokens live in **Android Keystore / iOS Keychain + Secure Enclave** (ADD §18.2). The DB stores only
  *references* and *expiry hints* (`auth_session`).
- The database file SHOULD be encrypted at rest with **SQLCipher** (Android) / **SQLCipher or GRDB
  encryption** (iOS); the key is wrapped by the hardware keystore.
- On logout / `403 device-revoked` (AL-08) / PDPA erasure: **wipe the whole DB file** and Keystore entries.

### 0.5 Common column idioms

- `synced_at INTEGER` — last time the row was reconciled from the server (NULL = never / stale).
- `dirty INTEGER NOT NULL DEFAULT 0` — local edit pending upload (paired with a `command_outbox` row).
- `server_updated_at INTEGER` — server `updated_at` for last-writer-wins conflict resolution.
- `schema`: Room/SQLDelight track `PRAGMA user_version`; a `meta` KV row also records the app schema rev.

---

## 1. Shared Tables (both Passenger & Driver apps)

### 1.1 `auth_session` — who is signed in (single row)

```sql
CREATE TABLE auth_session (
  id                     INTEGER PRIMARY KEY CHECK (id = 1),     -- singleton row
  user_id                TEXT NOT NULL,
  app                    TEXT NOT NULL CHECK (app IN ('passenger','driver')),  -- AL-08 (per-app device)
  device_id              TEXT NOT NULL,
  jti                    TEXT,                                   -- refresh session id (token itself in Keystore)
  access_token_expires_at INTEGER,                              -- epoch ms; drives proactive refresh (D-29)
  mqtt_token_expires_at   INTEGER,                              -- E-02: max(ride+2h, 4h)
  last_refresh_at        INTEGER,
  created_at             INTEGER NOT NULL,
  updated_at             INTEGER NOT NULL
);
```

### 1.2 `user_profile` — cached account (single row)

```sql
CREATE TABLE user_profile (
  id                      TEXT PRIMARY KEY,                      -- = auth_session.user_id
  phone                   TEXT,
  email                   TEXT,
  role                    TEXT NOT NULL,
  first_name              TEXT,
  photo_url               TEXT,
  language                TEXT NOT NULL DEFAULT 'en' CHECK (language IN ('si','ta','en')),
  default_payment_method  TEXT NOT NULL DEFAULT 'cash'
                            CHECK (default_payment_method IN ('cash','lankaqr','onepay')),  -- AL-14
  notif_prefs_json        TEXT NOT NULL DEFAULT '{}',
  emergency_contact_name  TEXT,
  emergency_contact_phone TEXT,
  dirty                   INTEGER NOT NULL DEFAULT 0,            -- local edit pending upload
  synced_at               INTEGER,
  updated_at              INTEGER NOT NULL
);
```

### 1.3 `emergency_contacts` — cached SOS contacts

```sql
CREATE TABLE emergency_contacts (
  id          TEXT PRIMARY KEY,
  name        TEXT NOT NULL,
  phone       TEXT NOT NULL,
  dirty       INTEGER NOT NULL DEFAULT 0,
  synced_at   INTEGER,
  updated_at  INTEGER NOT NULL
);
```

### 1.4 `command_outbox` — durable idempotent command queue (R-14, R-18, §11.13)

Every **mutating** API call is persisted here *before* it is issued. The `idempotency_key` (a
client-generated ULID) is sent as the `Idempotency-Key` header; on network loss the app retries with the
same key and the server replays the original response (`rides.command_log`). Offline edits (e.g. saving an
address, accepting a ride, submitting a job-board intent) all flow through this queue.

```sql
CREATE TABLE command_outbox (
  idempotency_key  TEXT PRIMARY KEY,                            -- client ULID, monotonic
  endpoint         TEXT NOT NULL,                               -- e.g. '/v1/rides/{id}/offer/{d}/accept'
  http_method      TEXT NOT NULL CHECK (http_method IN ('POST','PUT','PATCH','DELETE')),
  command          TEXT NOT NULL,                               -- logical command name (e.g. 'ride.accept')
  entity_type      TEXT,                                        -- 'ride' | 'address' | 'rating' | 'topup' | ...
  entity_id        TEXT,
  request_body     TEXT NOT NULL,                               -- JSON payload
  request_headers  TEXT,                                        -- JSON (non-secret headers only)
  state            TEXT NOT NULL DEFAULT 'PENDING'
                     CHECK (state IN ('PENDING','INFLIGHT','ACKED','FAILED','ABANDONED')),
  attempts         INTEGER NOT NULL DEFAULT 0,
  response_status  INTEGER,
  response_body    TEXT,
  created_at       INTEGER NOT NULL,
  last_attempt_at  INTEGER,
  next_retry_at    INTEGER                                      -- jittered exponential backoff
);
CREATE INDEX ix_outbox_dispatchable ON command_outbox(state, next_retry_at);
```

### 1.5 `gps_buffer` — local GPS ring buffer for MQTT replay (R-17, §7.5.3, US-15.1)

The foreground service writes **every** GPS sample here, then publishes live on `veh/{id}/pos/live`; on
reconnect it replays the backlog on `veh/{id}/pos/replay` **ordered by `seq`**. The server discards
`seq <= last_seen_seq` per vehicle, so replay is idempotent. `seq` is a **monotonic per-vehicle**
sequence the app maintains in `meta`/`sync_state`. Primarily produced by the **Driver app** (also Mode B
private-vehicle sharing); the Passenger app does not run a position publisher.

```sql
CREATE TABLE gps_buffer (
  seq          INTEGER NOT NULL,                                -- monotonic per vehicle_id (replay dedup key)
  vehicle_id   TEXT NOT NULL,
  lat          REAL NOT NULL,
  lng          REAL NOT NULL,
  accuracy_m   REAL,
  speed_mps    REAL,
  heading_deg  INTEGER,
  hdop         REAL,
  sat_count    INTEGER,
  sample_ts    INTEGER NOT NULL,                                -- epoch ms, GNSS UTC
  source       INTEGER NOT NULL DEFAULT 0,                      -- 0=mobile (matches telemetry.positions.source)
  state        TEXT NOT NULL DEFAULT 'PENDING'
                 CHECK (state IN ('PENDING','PUBLISHED','REPLAY_PENDING','ACKED')),
  created_at   INTEGER NOT NULL,
  PRIMARY KEY (vehicle_id, seq)
);
CREATE INDEX ix_gps_replay ON gps_buffer(vehicle_id, state, seq);
-- Eviction (ring buffer): keep last N (e.g. 6h @ phase cadence) per vehicle; delete oldest ACKED first.
-- Anti-spoof note: server applies plausibility checks (§12.6); device only buffers raw samples.
```

### 1.6 `notifications` — local push inbox (Epic 10)

```sql
CREATE TABLE notifications (
  id           TEXT PRIMARY KEY,                                -- server msg id or client UUID
  type         TEXT NOT NULL,                                   -- 'ride_offer','payment_ok','doc_expiring','sos_ack',...
  title        TEXT,
  body         TEXT,
  data_json    TEXT,                                            -- FCM/APNs data payload
  ride_id      TEXT,
  read         INTEGER NOT NULL DEFAULT 0,
  received_at  INTEGER NOT NULL
);
CREATE INDEX ix_notif_unread ON notifications(read, received_at DESC);
```

### 1.7 `content_templates` — offline i18n notification templates (D-26)

```sql
CREATE TABLE content_templates (
  template_key  TEXT NOT NULL,
  language      TEXT NOT NULL CHECK (language IN ('si','ta','en')),
  subject       TEXT,
  body          TEXT NOT NULL,
  version       INTEGER NOT NULL DEFAULT 1,
  synced_at     INTEGER,
  PRIMARY KEY (template_key, language)
);
```

### 1.8 `faq_articles` — cached in-app help (Epic 16)

```sql
CREATE TABLE faq_articles (
  id          TEXT PRIMARY KEY,
  category    TEXT NOT NULL,
  title       TEXT NOT NULL,
  body        TEXT NOT NULL,
  language    TEXT NOT NULL CHECK (language IN ('si','ta','en')),
  sort_order  INTEGER NOT NULL DEFAULT 0,
  synced_at   INTEGER
);
CREATE INDEX ix_faq_cat ON faq_articles(language, category, sort_order);
```

### 1.9 `offline_map_bundles` — downloaded PMTiles bundle metadata (MAP-09)

```sql
CREATE TABLE offline_map_bundles (
  id            TEXT PRIMARY KEY,
  region_name   TEXT NOT NULL,
  bbox_json     TEXT NOT NULL,                                  -- [minLng,minLat,maxLng,maxLat]
  pmtiles_url   TEXT NOT NULL,                                  -- signed Cloudflare R2 URL
  local_path    TEXT,                                           -- on-device file path once downloaded
  size_bytes    INTEGER,
  state         TEXT NOT NULL DEFAULT 'QUEUED'
                  CHECK (state IN ('QUEUED','DOWNLOADING','READY','STALE','FAILED')),
  downloaded_at INTEGER,
  expires_at    INTEGER
);
```

### 1.10 `support_tickets` — cached own tickets (Epic 16)

```sql
CREATE TABLE support_tickets (
  id              TEXT PRIMARY KEY,
  category        TEXT NOT NULL,
  description     TEXT NOT NULL,
  ride_id         TEXT,
  screenshot_url  TEXT,
  status          TEXT NOT NULL DEFAULT 'OPEN' CHECK (status IN ('OPEN','IN_PROGRESS','RESOLVED')),
  admin_response  TEXT,
  dirty           INTEGER NOT NULL DEFAULT 0,
  created_at      INTEGER NOT NULL,
  synced_at       INTEGER
);
```

### 1.11 `ratings_pending` — completed rides awaiting a rating (US-18.1/18.2)

```sql
CREATE TABLE ratings_pending (
  subject_id    TEXT PRIMARY KEY,                               -- ride_id or session_id
  subject_kind  TEXT NOT NULL CHECK (subject_kind IN ('ride','session')),
  ratee_id      TEXT NOT NULL,
  direction     TEXT NOT NULL CHECK (direction IN ('passenger_to_driver','driver_to_passenger')),
  prompt_shown  INTEGER NOT NULL DEFAULT 0,
  created_at    INTEGER NOT NULL
);
```

### 1.12 `sync_state` / `meta` — key-value app state (single store)

Holds the monotonic GPS `seq` counter, the current server-pushed cadence hint, per-entity last-sync
cursors, the minimum-app-version gate (D-31), and the schema revision.

```sql
CREATE TABLE meta (
  key         TEXT PRIMARY KEY,                                 -- e.g. 'gps.seq', 'cadence.intervalMs',
                                                                --      'sync.cursor.rides', 'min_app_version'
  value       TEXT,
  updated_at  INTEGER NOT NULL
);
```

---

## 2. Passenger-App Tables

### 2.1 `saved_addresses` — Home/Work + labelled (AL-14, US-22.1/22.2)

```sql
CREATE TABLE saved_addresses (
  id         TEXT PRIMARY KEY,
  label      TEXT NOT NULL,                                     -- 'home' | 'work' | custom
  line1      TEXT, line2 TEXT, line3 TEXT,
  lat        REAL NOT NULL,
  lng        REAL NOT NULL,
  dirty      INTEGER NOT NULL DEFAULT 0,
  synced_at  INTEGER,
  updated_at INTEGER NOT NULL
);
```

### 2.2 `place_recents` — recent / searched locations (local-only UX)

```sql
CREATE TABLE place_recents (
  id           TEXT PRIMARY KEY,
  label        TEXT NOT NULL,
  line1        TEXT,
  lat          REAL NOT NULL,
  lng          REAL NOT NULL,
  use_count    INTEGER NOT NULL DEFAULT 1,
  last_used_at INTEGER NOT NULL
);
CREATE INDEX ix_recents_recent ON place_recents(last_used_at DESC);
```

### 2.3 `rides` — active ride + trip-history projection (rider's view of `rides.rides`)

One row per ride the passenger booked. The single live ride (`is_active=1`) drives the active-ride screen
and survives app restart; terminal rows form the trip history list (US-8.7).

```sql
CREATE TABLE rides (
  id                    TEXT PRIMARY KEY,
  client_request_id     TEXT NOT NULL,                          -- idempotency partner (R-18)
  state                 TEXT NOT NULL,                          -- mirrors rides.rides.state
  is_active             INTEGER NOT NULL DEFAULT 1,             -- 1 until terminal
  kind                  INTEGER NOT NULL DEFAULT 0,             -- 0=passenger,1=proxy,2=package
  is_proxy              INTEGER NOT NULL DEFAULT 0,
  vehicle_type          TEXT NOT NULL,                          -- requested tier
  pickup_lat            REAL NOT NULL, pickup_lng REAL NOT NULL, pickup_label TEXT,
  dropoff_lat           REAL NOT NULL, dropoff_lng REAL NOT NULL, dropoff_label TEXT,
  -- proxy / package
  rider_name            TEXT,
  rider_phone_masked    TEXT,
  package_size          TEXT CHECK (package_size IS NULL OR package_size IN ('S','M','L')),
  package_description   TEXT,
  -- assigned driver/vehicle snapshot (shown on map)
  accepted_driver_id    TEXT,
  driver_name           TEXT,
  driver_photo_url      TEXT,
  driver_rating         REAL,
  vehicle_reg           TEXT,
  vehicle_actual_type   TEXT,
  vehicle_lat           REAL, vehicle_lng REAL, vehicle_heading_deg INTEGER, -- last-known marker (offline US-15.2)
  offer_expires_at      INTEGER,
  -- fare / payment
  fare_amount_minor     INTEGER,
  surcharge_minor       INTEGER NOT NULL DEFAULT 0,
  tip_amount_minor      INTEGER NOT NULL DEFAULT 0,
  payment_method        TEXT CHECK (payment_method IN ('cash','lankaqr','onepay','cod')),
  payment_state         TEXT,                                   -- mirrors fares.ride_payments.state
  created_at            INTEGER NOT NULL,
  updated_at            INTEGER NOT NULL,
  terminal_at           INTEGER,
  server_updated_at     INTEGER,
  synced_at             INTEGER
);
CREATE INDEX ix_prides_active ON rides(is_active, updated_at DESC);
CREATE INDEX ix_prides_history ON rides(created_at DESC);
```

### 2.4 `fare_estimates` — cached upfront estimates (US-8.2/8.4)

```sql
CREATE TABLE fare_estimates (
  id              TEXT PRIMARY KEY,
  pickup_lat      REAL NOT NULL, pickup_lng REAL NOT NULL,
  dropoff_lat     REAL NOT NULL, dropoff_lng REAL NOT NULL,
  vehicle_type    TEXT NOT NULL,
  estimated_minor INTEGER NOT NULL,
  surcharge_pct   INTEGER NOT NULL DEFAULT 0,
  distance_m      INTEGER,
  computed_at     INTEGER NOT NULL,
  expires_at      INTEGER NOT NULL                              -- short TTL; re-quote on expiry
);
```

### 2.5 `location_requests` — proxy GPS round-trip as booker (P-02, P-13)

```sql
CREATE TABLE location_requests (
  request_id        TEXT PRIMARY KEY,
  ride_id           TEXT,
  rider_phone_masked TEXT,
  state             TEXT NOT NULL DEFAULT 'Pending'
                      CHECK (state IN ('Pending','Confirmed','Declined','Expired','RiderNotRegistered')),
  issued_at         INTEGER NOT NULL,
  ttl_seconds       INTEGER NOT NULL DEFAULT 300,
  resolved_lat      REAL, resolved_lng REAL, resolved_accuracy_m REAL,
  resolved_at       INTEGER
);
```

### 2.6 `blocked_drivers` — cached block list (US-12.10)

```sql
CREATE TABLE blocked_drivers (
  driver_id   TEXT PRIMARY KEY,
  driver_name TEXT,
  dirty       INTEGER NOT NULL DEFAULT 0,
  created_at  INTEGER NOT NULL,
  synced_at   INTEGER
);
```

### 2.7 `trip_shares` — active live-trip share tokens (D-34, US-12.8)

```sql
CREATE TABLE trip_shares (
  token       TEXT PRIMARY KEY,
  ride_id     TEXT NOT NULL,
  share_url   TEXT NOT NULL,
  expires_at  INTEGER NOT NULL,
  revoked     INTEGER NOT NULL DEFAULT 0,
  created_at  INTEGER NOT NULL
);
```

> **Live nearby-vehicle markers** (US-7.x map) are streamed over SignalR and held **in memory**, not in
> SQLite — they churn at 1–8 s and are pure view state. Only the *last-known* position of the assigned
> ride vehicle is persisted (in `rides.vehicle_lat/lng`) to render the offline "connection lost" banner
> with a frozen marker (US-15.2/15.6).

---

## 3. Driver-App Tables

### 3.1 `vehicles` — driver's registered vehicles cache (Epic 2)

```sql
CREATE TABLE vehicles (
  id                  TEXT PRIMARY KEY,
  registration_number TEXT NOT NULL,
  vehicle_type        TEXT NOT NULL,                            -- canonical (AL-09)
  mode                TEXT NOT NULL CHECK (mode IN ('A','B','C')),
  status              TEXT NOT NULL CHECK (status IN ('PENDING','APPROVED','REJECTED','DEACTIVATED')),
  dispatch_state      TEXT NOT NULL DEFAULT 'ACTIVE'
                        CHECK (dispatch_state IN ('ACTIVE','DISPATCH_SUSPENDED')),
  rejection_reason    TEXT,
  driver_name         TEXT,
  driver_photo_url    TEXT,
  vehicle_photo_url   TEXT,
  is_selected         INTEGER NOT NULL DEFAULT 0,               -- the vehicle the driver goes live on
  synced_at           INTEGER,
  updated_at          INTEGER NOT NULL
);
```

### 3.2 `standby_state` — online/offline + cadence (single row, US-6A.1, §7.5)

```sql
CREATE TABLE standby_state (
  id                   INTEGER PRIMARY KEY CHECK (id = 1),
  state                TEXT NOT NULL DEFAULT 'OFFLINE'
                         CHECK (state IN ('OFFLINE','AVAILABLE','OFFERED','ON_RIDE')),
  active_vehicle_id    TEXT,
  pos_rate_interval_ms INTEGER NOT NULL DEFAULT 60000,          -- server cadence hint (veh/{id}/cmd)
  updated_at           INTEGER NOT NULL
);
```

### 3.3 `dispatch_offers` — incoming offers (15 s TTL, US-6A.2, R-10)

```sql
CREATE TABLE dispatch_offers (
  id                   TEXT PRIMARY KEY,                        -- offer id
  ride_id              TEXT NOT NULL,
  vehicle_type         TEXT NOT NULL,
  pickup_lat           REAL NOT NULL, pickup_lng REAL NOT NULL, pickup_label TEXT,
  dropoff_lat          REAL NOT NULL, dropoff_lng REAL NOT NULL, dropoff_label TEXT,
  est_fare_minor       INTEGER,
  distance_to_pickup_m INTEGER,
  kind                 INTEGER NOT NULL DEFAULT 0,              -- 0=passenger,1=proxy,2=package
  is_proxy             INTEGER NOT NULL DEFAULT 0,
  rider_name           TEXT,
  rider_phone_masked   TEXT,                                    -- P-05; driver calls rider, not booker
  package_size         TEXT CHECK (package_size IS NULL OR package_size IN ('S','M','L')),
  package_description  TEXT,
  status               TEXT NOT NULL DEFAULT 'OFFERED'
                         CHECK (status IN ('OFFERED','ACCEPTED','DECLINED','EXPIRED')),
  sent_at              INTEGER NOT NULL,
  expires_at           INTEGER NOT NULL
);
CREATE INDEX ix_offers_live ON dispatch_offers(status, expires_at);
```

### 3.4 `active_ride` — driver's view of the accepted ride (R-01, P-06)

```sql
CREATE TABLE active_ride (
  id                  TEXT PRIMARY KEY,
  state               TEXT NOT NULL,                            -- mirrors rides.rides.state
  kind                INTEGER NOT NULL DEFAULT 0,
  is_proxy            INTEGER NOT NULL DEFAULT 0,
  rider_name          TEXT,
  rider_phone_masked  TEXT,
  pickup_lat          REAL NOT NULL, pickup_lng REAL NOT NULL, pickup_label TEXT,
  dropoff_lat         REAL NOT NULL, dropoff_lng REAL NOT NULL, dropoff_label TEXT,
  package_size        TEXT CHECK (package_size IS NULL OR package_size IN ('S','M','L')),
  package_description TEXT,
  needs_pickup_otp    INTEGER NOT NULL DEFAULT 0,               -- package: enter OTP at pickup (P-07)
  needs_delivery_otp  INTEGER NOT NULL DEFAULT 0,               -- package: enter OTP at drop (P-07)
  needs_proof         INTEGER NOT NULL DEFAULT 0,               -- package: delivery photo/signature (P-10)
  payment_method      TEXT CHECK (payment_method IN ('cash','lankaqr','onepay','cod')),
  payment_state       TEXT,
  fare_amount_minor   INTEGER,
  surcharge_minor     INTEGER NOT NULL DEFAULT 0,
  tip_amount_minor    INTEGER NOT NULL DEFAULT 0,
  created_at          INTEGER NOT NULL,
  updated_at          INTEGER NOT NULL,
  server_updated_at   INTEGER
);
-- OTP values are entered live and POSTed for server-side validation; never persisted on device.
```

### 3.5 `ride_history` — completed rides (driver earnings detail, US-8.7)

```sql
CREATE TABLE ride_history (
  id                TEXT PRIMARY KEY,
  state             TEXT NOT NULL,
  kind              INTEGER NOT NULL DEFAULT 0,
  pickup_label      TEXT, dropoff_label TEXT,
  fare_amount_minor INTEGER,
  tip_amount_minor  INTEGER NOT NULL DEFAULT 0,
  payment_method    TEXT,
  completed_at      INTEGER,
  synced_at         INTEGER
);
CREATE INDEX ix_dride_hist ON ride_history(completed_at DESC);
```

### 3.6 `proof_upload_queue` — delivery-proof capture queue (P-10)

Proof photos/signatures captured at delivery, queued for upload even if offline.

```sql
CREATE TABLE proof_upload_queue (
  id            TEXT PRIMARY KEY,
  ride_id       TEXT NOT NULL,
  kind          TEXT NOT NULL CHECK (kind IN ('delivery_photo','signature','pickup_photo')),
  local_path    TEXT NOT NULL,
  sha256_hex    TEXT,
  captured_lat  REAL, captured_lng REAL,
  captured_at   INTEGER NOT NULL,
  state         TEXT NOT NULL DEFAULT 'PENDING'
                  CHECK (state IN ('PENDING','UPLOADING','UPLOADED','FAILED')),
  attempts      INTEGER NOT NULL DEFAULT 0,
  storage_url   TEXT,
  next_retry_at INTEGER
);
CREATE INDEX ix_proof_dispatch ON proof_upload_queue(state, next_retry_at);
```

### 3.7 `wallet` — driver wallet balance cache (single row, D-08)

Drives the **"insufficient balance for 2nd trip"** gate shown before going online (D2 SCR driver
dashboard). Authoritative balance is the server ledger; this is a 5 s-ish cache hint.

```sql
CREATE TABLE wallet (
  id          INTEGER PRIMARY KEY CHECK (id = 1),
  account_id  TEXT,
  balance_minor INTEGER NOT NULL DEFAULT 0,
  currency    TEXT NOT NULL DEFAULT 'LKR',
  updated_at  INTEGER NOT NULL,
  synced_at   INTEGER
);
```

### 3.8 `wallet_transactions` — cached ledger projection (US-9.x)

```sql
CREATE TABLE wallet_transactions (
  id                  TEXT PRIMARY KEY,                         -- server transaction id
  kind                TEXT NOT NULL,                            -- journal.kind
  amount_minor        INTEGER NOT NULL,                         -- signed
  balance_after_minor INTEGER,
  description         TEXT,
  ts                  INTEGER NOT NULL,
  synced_at           INTEGER
);
CREATE INDEX ix_wtx_ts ON wallet_transactions(ts DESC);
```

### 3.9 `daily_fee_status` — first-trip-free + daily fee state (D-13, US-9.4)

```sql
CREATE TABLE daily_fee_status (
  fee_date            TEXT PRIMARY KEY,                         -- 'YYYY-MM-DD' Asia/Colombo
  trips_that_day      INTEGER NOT NULL DEFAULT 0,
  first_trip_free_used INTEGER NOT NULL DEFAULT 0,
  fee_charged         INTEGER NOT NULL DEFAULT 0,
  amount_minor        INTEGER NOT NULL DEFAULT 0,
  updated_at          INTEGER NOT NULL,
  synced_at           INTEGER
);
```

### 3.10 `driver_earnings` — daily earnings cache (US-9.x)

```sql
CREATE TABLE driver_earnings (
  earn_date       TEXT PRIMARY KEY,                             -- 'YYYY-MM-DD' Asia/Colombo
  trips           INTEGER NOT NULL DEFAULT 0,
  gross_minor     INTEGER NOT NULL DEFAULT 0,
  daily_fee_minor INTEGER NOT NULL DEFAULT 0,
  net_minor       INTEGER NOT NULL DEFAULT 0,
  synced_at       INTEGER
);
```

### 3.11 `driver_level` — Driver Level System cache (single row, US-6A.6)

```sql
CREATE TABLE driver_level (
  id                INTEGER PRIMARY KEY CHECK (id = 1),
  level             INTEGER NOT NULL DEFAULT 3 CHECK (level BETWEEN 1 AND 3),
  rating_points     INTEGER NOT NULL DEFAULT 0,
  level_up_threshold INTEGER NOT NULL DEFAULT 500,
  synced_at         INTEGER
);
```

### 3.12 `directional_filter` — active Directional Travel filter (single row, DT-01, US-6A.21)

```sql
CREATE TABLE directional_filter (
  id               INTEGER PRIMARY KEY CHECK (id = 1),
  server_id        TEXT,
  destination_lat  REAL, destination_lng REAL, label TEXT,
  set_at           INTEGER,
  expires_at       INTEGER,                                     -- drives "time left" banner
  uses_today       INTEGER NOT NULL DEFAULT 0,
  max_uses_per_day INTEGER NOT NULL DEFAULT 2,                  -- "uses left" banner (DT-03)
  active           INTEGER NOT NULL DEFAULT 0,
  updated_at       INTEGER NOT NULL
);
```

### 3.13 `job_board` — scheduled rides available + intents (US-6A.4/6A.5)

```sql
CREATE TABLE job_board (
  scheduled_ride_id TEXT PRIMARY KEY,
  pickup_lat        REAL NOT NULL, pickup_lng REAL NOT NULL, pickup_label TEXT,
  dropoff_lat       REAL NOT NULL, dropoff_lng REAL NOT NULL, dropoff_label TEXT,
  vehicle_type      TEXT NOT NULL,
  pickup_time       INTEGER NOT NULL,
  distance_m        INTEGER,
  intent_submitted  INTEGER NOT NULL DEFAULT 0,                 -- mirrors dispatch.job_board_intents
  synced_at         INTEGER
);
CREATE INDEX ix_jobboard_time ON job_board(pickup_time);
```

### 3.14 `documents` — document expiry cache (E-03, US-2.x)

```sql
CREATE TABLE documents (
  id          TEXT PRIMARY KEY,
  vehicle_id  TEXT,
  kind        TEXT NOT NULL CHECK (kind IN ('driving_license','registration','permit','insurance','revenue_license','vehicle_photo')),
  status      TEXT NOT NULL CHECK (status IN ('VALID','EXPIRING','EXPIRED','REJECTED')),
  expires_at  INTEGER,
  synced_at   INTEGER
);
CREATE INDEX ix_docs_expiry ON documents(expires_at);
```

### 3.15 `credit_transfers` — driver-to-driver credit transfer cache (AL-01, US-9.13)

> "Reseller" is **not** a role/account/capability — any driver who bought bulk credit can transfer it to others by **Driver ID**. Transfers move the **exact value, no commission**. (No `reseller_capability`/commission cache.)

```sql
CREATE TABLE credit_transfers (
  id                  TEXT PRIMARY KEY,
  direction           TEXT NOT NULL CHECK (direction IN ('incoming','outgoing')),
  counterparty_driver_id TEXT,                                 -- the other driver's Driver ID
  counterparty_name   TEXT,
  counterparty_phone_masked TEXT,
  amount_minor        INTEGER NOT NULL,                        -- exact value, no commission
  status              TEXT NOT NULL CHECK (status IN ('PENDING','APPROVED','REJECTED')),
  created_at          INTEGER NOT NULL,
  synced_at           INTEGER
);
CREATE INDEX ix_credit_transfers_recent ON credit_transfers(created_at DESC);
```

---

## 4. Sync, Outbox & Eviction Strategy

### 4.1 Write path (outbound) — never lose a user action

1. **User acts** (accept offer, save address, submit intent, request top-up, rate trip).
2. App writes the local projection optimistically (`dirty=1`) **and** inserts a `command_outbox` row with
   a fresh `idempotency_key` (ULID) — in **one transaction**.
3. A background worker drains `command_outbox` (`state=PENDING`, `next_retry_at<=now`) with the
   `Idempotency-Key` header. On `2xx` → `ACKED`, clear `dirty`, reconcile from response. On network loss →
   retry same key (server replays original response, §11.13). On `4xx` (non-retryable) → `FAILED`, surface
   to UI. Backoff is jittered exponential (§7.5.3).
4. **GPS** (`gps_buffer`) and **proof photos** (`proof_upload_queue`) follow the same durable-drain pattern
   but over MQTT (`pos/replay`) and object-storage upload respectively, not the REST outbox.

### 4.2 Read path (inbound) — cache + reconcile

- On foreground / pull-to-refresh / push wake, the app fetches deltas (cursor in `meta.sync.cursor.*`) and
  upserts projections, setting `synced_at` and `server_updated_at`.
- **Conflict rule:** if a row is `dirty=1` and the server `updated_at` is newer, the pending
  `command_outbox` entry is authoritative until ACKed; otherwise last-writer-wins on `server_updated_at`.
- Live, high-churn data (nearby vehicle markers, in-ride driver position) is **in-memory only** (SignalR /
  MQTT), never written to SQLite.

### 4.3 Retention / eviction

| Table | Policy |
|---|---|
| `gps_buffer` | Ring buffer: delete `ACKED` rows immediately after replay-confirm; cap PENDING/REPLAY backlog (e.g. last 6 h per vehicle); never exceed a hard row cap to bound disk. |
| `command_outbox` | Delete `ACKED` after 24 h (kept briefly for idempotent re-replay); keep `FAILED` until user-dismissed. |
| `notifications` | Keep 30 days or last 200, whichever first. |
| `rides` / `ride_history` | Keep last 90 days or 100 rows locally; full history is server-paged on demand. |
| `fare_estimates` | Delete on `expires_at`. |
| `dispatch_offers` | Delete on `expires_at` + small grace; only one is live at a time (R-10). |
| `proof_upload_queue` | Delete `UPLOADED` after server confirms; keep `FAILED` for manual retry. |
| `offline_map_bundles` | Evict `STALE`/expired bundles; respect a total on-disk size budget. |
| All caches | **Full wipe** on logout, device-revoke (AL-08), or PDPA erasure (E-06). |

---

## 5. Coverage Map (mobile feature → table)

| Feature / Spec | Tables |
|---|---|
| Login / session, single-active-device per app (AL-08, D-29) | `auth_session`, `meta` |
| Profile, language, default payment, notif prefs (AL-14, US-10.7) | `user_profile` |
| **Offline GPS buffer + replay** (R-17, US-15.1, §7.5.3) | `gps_buffer`, `meta(gps.seq)` |
| **Idempotent ride/command replay** (R-14, R-18, §11.13) | `command_outbox` |
| Phase-aware GPS cadence hint (R-07, §7.5.1) | `standby_state`, `meta(cadence)` |
| Passenger booking, active ride, history (R-01, US-8.7) | `rides`, `fare_estimates`, `place_recents`, `saved_addresses` |
| Proxy booking + location request (P-01/P-02/P-13) | `rides`, `location_requests` |
| Package delivery + OTP + proof (P-06/P-07/P-10) | `active_ride`, `proof_upload_queue` |
| Driver standby / offers / Driver Level (US-6A.1/6A.2/6A.6, R-10) | `standby_state`, `dispatch_offers`, `driver_level` |
| Directional Travel banner (DT-01/DT-03, US-6A.21) | `directional_filter` |
| Job Board (US-6A.4/6A.5) | `job_board` |
| Wallet, daily fee first-trip-free, earnings, driver-to-driver credit transfer (D-08/D-13, US-9.x, AL-01) | `wallet`, `wallet_transactions`, `daily_fee_status`, `driver_earnings`, `credit_transfers` |
| Document expiry warnings (E-03) | `documents` |
| Ratings (US-18.1/18.2) | `ratings_pending` |
| SOS / trip share / block (D-33/D-34, US-12.x) | `emergency_contacts`, `trip_shares`, `blocked_drivers` |
| Offline i18n + FAQ + tickets (D-26, Epic 16) | `content_templates`, `faq_articles`, `support_tickets` |
| Offline maps (MAP-09) | `offline_map_bundles` |
| Push inbox (Epic 10, E-01) | `notifications` |

---

## 6. Δ Change Set 2026-06-28 (URD v2.5 Epic 24 · ADD v2.9 §1.11)

> On-device (local) deltas for the 2026-06-28 review. Most items are presentation-only and need no schema change; the additions below are the cached driver phone for post-trip calling, a UI preference for the call-type chooser, and capture-provenance carried on the upload outbox.

```sql
-- Item 3 (US-24.4): show the driver's mobile + a Call action on completed-trip history cards.
ALTER TABLE rides ADD COLUMN driver_phone_masked TEXT;   -- masked MSISDN snapshot for post-trip Call (hidden if cancelled pre-assignment)

-- Item 4 (US-24.3): remember the passenger's last call-type choice (Free VoIP vs Normal masked).
CREATE TABLE IF NOT EXISTS ui_prefs (
  key   TEXT PRIMARY KEY,
  value TEXT
);  -- e.g. ('last_call_type','free_voip' | 'normal_masked')
```

**No-schema-change notes (2026-06-28):**
- **Item 6 (US-24.6, driver app):** the camera document-scanner with draggable-corner crop (SCR-DA/DI-005) is a capture/UX change; the perspective-corrected image is queued through the existing upload **`command_outbox`** with `captured_via='camera_dragcrop'` in the command payload (mirrors `docs.uploads.captured_via` server-side, §23 of `server_db_schema.md`). No new local table.
- **Item 2 (US-24.2):** the schedule-ride destination is part of the existing ride-compose payload; the client now **blocks Confirm until a destination is set** (validation only).
- **Items 1 (Get Started bottom), 5/7/8/9/10/11 (admin portal):** passenger CTA layout and all Admin-Portal changes are server/web-side — no on-device schema impact.

## 7. Δ Change Set 2026-07-05 (URD v2.6 Epic 25 · ADD v3.0 §1.12)

> **No on-device schema change.** The 2026-07-05 pass formalizes the `passenger.mageride.lk` no-login web subview (SCR-WT-001…006, `public-bff /public/track/*`) — an entirely server/web-side surface; the web pages hold no local state (no cookies, no localStorage of ride data, BR-29.1). App-side impacts are behavioural only:
> - **Item 3 (US-25.3, booker's app):** when a location request returns `RiderNotRegistered`, the booker UI copy changes from "enter pickup manually" to "we've texted [rider] a link — you can also set the pin manually"; the existing `location_request` sync payload already carries the state, no new column.
> - **Item 8 (US-25.8):** wireframe-annotation hygiene (US-8.7a → US-24.4; splash resume endpoint paths) — documentation only.
> - **Hygiene (2026-07-05 Pass 1, `technical_feasibility.md` §5.2-2):** `documents.kind` CHECK (§3.14) extended with **`revenue_license`** (mandatory per vehicle since US-2.20) and **`vehicle_photo`** — drift fix mirroring server-side `registry.documents.kind`; existing rows unaffected.

## 8. Δ Change Set 2026-07-05 #2 (URD v2.7 Epic 26 · ADD v3.1 §1.13)

> Driver-QR attestation settlement (AL-47) + number-masking removal (AL-48).

```sql
-- AL-47: track the QR attestation locally so the Pay screen survives restarts offline.
ALTER TABLE rides       ADD COLUMN qr_claimed_at INTEGER;    -- passenger tapped "I've paid" (epoch ms)
ALTER TABLE active_ride ADD COLUMN qr_claimed_at INTEGER;    -- driver side: claim received, confirm pending
-- payment_state mirrors the server enum and now includes 'QrClaimedByPassenger' / 'DriverConfirmedQR'.
-- The claim itself goes through command_outbox ('fare.qr_claim' / 'fare.qr_confirm') — idempotent, offline-safe.
-- The optional receipt screenshot rides the existing proof_upload_queue with kind='qr_receipt'.

-- AL-48: masking removed — phone columns now carry the REAL MSISDN (exposed post-accept only).
ALTER TABLE rides             RENAME COLUMN driver_phone_masked        TO driver_phone;
ALTER TABLE rides             RENAME COLUMN rider_phone_masked         TO rider_phone;
ALTER TABLE dispatch_offers   RENAME COLUMN rider_phone_masked         TO rider_phone;
ALTER TABLE active_ride       RENAME COLUMN rider_phone_masked         TO rider_phone;
ALTER TABLE location_requests RENAME COLUMN rider_phone_masked         TO rider_phone;
ALTER TABLE credit_transfers  RENAME COLUMN counterparty_phone_masked  TO counterparty_phone;
```

**Notes (2026-07-05 #2):**
- `proof_upload_queue.kind` CHECK gains **`qr_receipt`** (alongside `delivery_photo`/`signature`/`pickup_photo`).
- `ui_prefs('last_call_type')` values become **`'free_voip' | 'direct_dial'`** (was `'normal_masked'`).
- Server omits the counterparty phone until `Accepted` and for cancelled-before-assignment rides — the renamed columns are simply NULL until then (US-26.2); P-05 unchanged (driver caches the **rider's** number, never the booker's).

*End of mobile_db_schema.md — on-device SQLite (Room / SQLDelight / iOS SQLite).*
