# MageRide — Server Database Schema (PostgreSQL 16 + PostGIS + TimescaleDB)

> **Authoritative, runnable DDL for the MageRide backend.**
> Consolidated from `mageride-specs/D4_mageride_data_model.md` (the canonical data model) and
> reconciled against `architecture-design-document.md` ADD v2.6 §9 (Data Architecture), §6
> (microservices), §7 (real-time plane) and §1.8 (AL-01…AL-16 URD v2.2 alignment).
>
> **Engine:** PostgreSQL 16 on TimescaleDB-HA (PostGIS + TimescaleDB + pgcrypto + citext).
> **Topology:** one PG16 cluster, **18 bounded-context schemas**, primary + 2 read replicas
> (streaming), PgBouncer (transaction mode) in front of every service. The high-frequency tracker
> plane (`telemetry.positions`) is a logically separated TimescaleDB hypertable.
> **Access:** Dapper over Npgsql — hand-written parameterised SQL, repository-per-schema. **This DDL is
> the source of truth** (no EF Core / model-first generation). Migrations applied by **DbUp/Grate**
> as ordered, idempotent SQL scripts.

---

## 0. Conventions

| Concern | Rule |
|---|---|
| **Primary keys** | `id UUID PRIMARY KEY DEFAULT gen_random_uuid()` (ULID-compatible; ULIDs stored as UUID). High-volume append logs use `BIGINT GENERATED ALWAYS AS IDENTITY`. |
| **Foreign keys** | **Real** `FOREIGN KEY` constraints within and across schemas; `ON DELETE` is always explicit. |
| **Enumerations** | `TEXT` + `CHECK (col IN (...))` for small closed sets; lookup tables for admin-editable catalogs. **No native PG `ENUM` types** (avoids migration locks). |
| **Money** | Integer **minor units** (Rs × 100): `*_minor INTEGER NOT NULL CHECK (… >= 0)`. Ledger balances/postings use `BIGINT` (may be signed). `currency CHAR(3) NOT NULL DEFAULT 'LKR'`. |
| **Time** | `TIMESTAMPTZ` everywhere. Any business-date logic (daily fee, peak windows, monthly subscription, directional daily-use) is computed in **`Asia/Colombo`** and persisted as `DATE` (D-38). |
| **PII** | Registered parties referenced by FK. **Unregistered** parties (proxy riders, package recipients) stored hash-only: `*_phone_hash BYTEA`. ID documents live in `docs.*` on SSE-KMS object storage. |
| **Audit columns** | `created_at TIMESTAMPTZ NOT NULL DEFAULT now()` on all tables; `updated_at` on all mutable tables. |
| **Spatial** | `GEOGRAPHY(POINT,4326)` for app/device points (metre distance math); `GEOMETRY(...,4326)` for the PostGIS system-of-record (routes/stops/geofences). GiST indexes on all spatial columns. |
| **Tenancy** | Single market (Sri Lanka). Fleet scoping via `fleet_id` + Row-Level Security where required (Epic 13). No per-city tenancy column. |
| **Idempotency** | Mutating ride APIs key on `rides.command_log(idempotency_key)`; ledger writes on `billing.journal_entries(idempotency_key)`; payment callbacks on `fares.ride_payments(provider_transaction_id)`. |

### 0.1 Extensions & schemas (apply first)

```sql
CREATE EXTENSION IF NOT EXISTS postgis;
CREATE EXTENSION IF NOT EXISTS timescaledb;
CREATE EXTENSION IF NOT EXISTS pgcrypto;     -- gen_random_uuid(), HMAC
CREATE EXTENSION IF NOT EXISTS citext;

CREATE SCHEMA IF NOT EXISTS iam;
CREATE SCHEMA IF NOT EXISTS registry;
CREATE SCHEMA IF NOT EXISTS prov;
CREATE SCHEMA IF NOT EXISTS trips;
CREATE SCHEMA IF NOT EXISTS rides;
CREATE SCHEMA IF NOT EXISTS dispatch;
CREATE SCHEMA IF NOT EXISTS reputation;
CREATE SCHEMA IF NOT EXISTS safety;
CREATE SCHEMA IF NOT EXISTS fares;
CREATE SCHEMA IF NOT EXISTS billing;
CREATE SCHEMA IF NOT EXISTS comms;
CREATE SCHEMA IF NOT EXISTS docs;
CREATE SCHEMA IF NOT EXISTS support;
CREATE SCHEMA IF NOT EXISTS content;
CREATE SCHEMA IF NOT EXISTS audit;
CREATE SCHEMA IF NOT EXISTS pdpa;
CREATE SCHEMA IF NOT EXISTS spatial;
CREATE SCHEMA IF NOT EXISTS telemetry;
```

### 0.2 Shared trigger — `set_updated_at`

```sql
CREATE OR REPLACE FUNCTION public.set_updated_at() RETURNS trigger AS $$
BEGIN NEW.updated_at = now(); RETURN NEW; END; $$ LANGUAGE plpgsql;
-- Attach per table where an updated_at column exists, e.g.:
--   CREATE TRIGGER trg_users_updated BEFORE UPDATE ON iam.users
--     FOR EACH ROW EXECUTE FUNCTION public.set_updated_at();
```

---

## 1. `iam` — Identity, Auth, RBAC

Nine canonical roles (AL-06); a user may hold several (`iam.user_roles`) — effective permissions are the
union, deny-by-default. The single `iam.users.role` column is the **primary** role. Apps authenticate by
**Phone OTP**; Admin Portal by Password/Google; Fleet Portal by Email/Google/Apple (AL-07). Single active
device is enforced **per app** (AL-08).

```sql
CREATE TABLE iam.users (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  phone TEXT UNIQUE,                                          -- +94 E.164; nullable for web-only internal accounts
  email TEXT UNIQUE,                                          -- Fleet/Admin Portal sign-in (AL-07)
  role TEXT NOT NULL DEFAULT 'passenger' CHECK (role IN
    ('passenger','driver','fleet_owner','admin','super_admin',
     'verification_officer','support_csr','finance_officer','auditor')),
  first_name TEXT,
  photo_url TEXT,
  language TEXT NOT NULL DEFAULT 'en' CHECK (language IN ('si','ta','en')),
  notif_prefs JSONB NOT NULL DEFAULT '{}',                    -- per-type prefs (US-10.7)
  default_payment_method TEXT NOT NULL DEFAULT 'cash'
    CHECK (default_payment_method IN ('cash','lankaqr','onepay')),  -- passenger default (AL-14, US-22.4)
  emergency_contact_name TEXT,                                -- driver SOS (AL-13)
  emergency_contact_phone TEXT,
  is_blocked BOOLEAN NOT NULL DEFAULT false,
  created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  updated_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  CHECK (phone IS NOT NULL OR email IS NOT NULL));            -- at least one credential

-- Optional role catalog (admin-readable labels/descriptions). RBAC enforcement uses the CHECK sets above.
CREATE TABLE iam.roles (
  role TEXT PRIMARY KEY CHECK (role IN
    ('passenger','driver','fleet_owner','admin','super_admin',
     'verification_officer','support_csr','finance_officer','auditor')),
  label TEXT NOT NULL,
  is_internal BOOLEAN NOT NULL DEFAULT false);                -- roles 4–9 are internal, super_admin-provisioned

CREATE TABLE iam.user_roles (                                 -- multi-role union, deny-by-default (AL-06)
  user_id UUID NOT NULL REFERENCES iam.users(id) ON DELETE CASCADE,
  role TEXT NOT NULL CHECK (role IN
    ('passenger','driver','fleet_owner','admin','super_admin',
     'verification_officer','support_csr','finance_officer','auditor')),
  granted_by UUID REFERENCES iam.users(id),                   -- internal roles provisioned only by super_admin
  granted_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  PRIMARY KEY (user_id, role));

CREATE TABLE iam.fleet_members (                              -- org-scoped fleet sub-roles (AL-03)
  fleet_id UUID NOT NULL REFERENCES registry.fleets(id) ON DELETE CASCADE,
  user_id UUID NOT NULL REFERENCES iam.users(id) ON DELETE CASCADE,
  fleet_role TEXT NOT NULL CHECK (fleet_role IN ('owner','manager','viewer')),
  created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  PRIMARY KEY (fleet_id, user_id));

CREATE TABLE iam.saved_addresses (                            -- Home/Work + labelled (AL-14, US-22.1/22.2)
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  user_id UUID NOT NULL REFERENCES iam.users(id) ON DELETE CASCADE,
  label TEXT NOT NULL,                                        -- 'home' | 'work' | custom
  line1 TEXT, line2 TEXT, line3 TEXT,
  geo GEOGRAPHY(POINT,4326) NOT NULL,                         -- reverse-geocoded OSM pin
  created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  updated_at TIMESTAMPTZ NOT NULL DEFAULT now());
CREATE INDEX ix_saved_addr_user ON iam.saved_addresses(user_id);

CREATE TABLE iam.devices (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  user_id UUID NOT NULL REFERENCES iam.users(id) ON DELETE CASCADE,
  platform TEXT NOT NULL CHECK (platform IN ('android','ios')),
  fcm_apns_token TEXT,
  keystore_pubkey TEXT,                                       -- device-bound key (Keystore / Secure Enclave)
  attestation_verified_at TIMESTAMPTZ,                        -- Play Integrity / App Attest (D-30)
  created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  updated_at TIMESTAMPTZ NOT NULL DEFAULT now());
CREATE INDEX ix_devices_user ON iam.devices(user_id);

CREATE TABLE iam.sessions (                                   -- refresh-token store (D-29); access JWT is stateless RS256
  jti UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  user_id UUID NOT NULL REFERENCES iam.users(id) ON DELETE CASCADE,
  device_id UUID NOT NULL REFERENCES iam.devices(id) ON DELETE CASCADE,
  app TEXT NOT NULL DEFAULT 'passenger' CHECK (app IN ('passenger','driver')),  -- single-active-device is PER APP (AL-08)
  issued_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  last_used_at TIMESTAMPTZ,
  revoked_at TIMESTAMPTZ);
-- single active device PER APP (AL-08, US-1.12): new-device login revokes only that app's prior session
CREATE UNIQUE INDEX ux_sessions_active_app ON iam.sessions(user_id, app) WHERE revoked_at IS NULL;
CREATE INDEX ix_sessions_user ON iam.sessions(user_id);

CREATE TABLE iam.otp_attempts (                               -- token-bucket OTP rate limit (D-32)
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  phone TEXT NOT NULL,
  auth_id UUID NOT NULL,
  otp_hash BYTEA NOT NULL,
  attempts SMALLINT NOT NULL DEFAULT 0,
  expires_at TIMESTAMPTZ NOT NULL,
  verified BOOLEAN NOT NULL DEFAULT false,
  created_at TIMESTAMPTZ NOT NULL DEFAULT now());
CREATE INDEX ix_otp_phone ON iam.otp_attempts(phone, created_at DESC);

CREATE TABLE iam.emergency_contacts (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  user_id UUID NOT NULL REFERENCES iam.users(id) ON DELETE CASCADE,
  name TEXT NOT NULL,
  phone TEXT NOT NULL,
  created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  updated_at TIMESTAMPTZ NOT NULL DEFAULT now());
CREATE INDEX ix_emergency_user ON iam.emergency_contacts(user_id);
```

---

## 2. `registry` — Vehicles, Profiles, Documents, Sharing, Fleets, Payouts

```sql
CREATE TABLE registry.fleets (                                -- Fleet Owner org (AL-03, Epic 13; VO-gated)
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  owner_id UUID NOT NULL REFERENCES iam.users(id),            -- the fleet_owner primary account
  name TEXT NOT NULL,
  business_reg TEXT,
  status TEXT NOT NULL DEFAULT 'PENDING' CHECK (status IN ('PENDING','APPROVED','REJECTED')),
  rejection_reason TEXT,
  created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  updated_at TIMESTAMPTZ NOT NULL DEFAULT now());
CREATE INDEX ix_fleets_owner ON registry.fleets(owner_id);

CREATE TABLE registry.vehicles (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  owner_id UUID NOT NULL REFERENCES iam.users(id),
  registration_number TEXT NOT NULL,
  vehicle_type TEXT NOT NULL CHECK (vehicle_type IN          -- canonical (AL-09); "car"→"sedan"
    ('motorbike','three_wheeler','flex','sedan','mini_van','van','truck','mini_truck','bus','train')),
  mode CHAR(1) NOT NULL CHECK (mode IN ('A','B','C')),
  status TEXT NOT NULL DEFAULT 'PENDING' CHECK (status IN ('PENDING','APPROVED','REJECTED','DEACTIVATED')),
  rejection_reason TEXT,                                      -- US-2.15
  driver_name TEXT NOT NULL,                                 -- shown to passengers (US-2.12)
  driver_photo_url TEXT,
  vehicle_photo_url TEXT,
  dispatch_state TEXT NOT NULL DEFAULT 'ACTIVE'
    CHECK (dispatch_state IN ('ACTIVE','DISPATCH_SUSPENDED')),  -- E-03 doc-expiry auto-suspend
  created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  updated_at TIMESTAMPTZ NOT NULL DEFAULT now());
-- D-37: registration uniqueness across the live (non-rejected/deactivated) set only:
CREATE UNIQUE INDEX ux_vehicles_regno_active ON registry.vehicles(registration_number)
  WHERE status IN ('PENDING','APPROVED');
CREATE INDEX ix_vehicles_owner ON registry.vehicles(owner_id);

CREATE TABLE registry.driver_profiles (
  driver_id UUID PRIMARY KEY REFERENCES iam.users(id) ON DELETE CASCADE,
  display_name TEXT NOT NULL,
  photo_url TEXT,
  verified_at TIMESTAMPTZ,
  created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  updated_at TIMESTAMPTZ NOT NULL DEFAULT now());

CREATE TABLE registry.driver_payouts (                        -- OnePay merchant binding (D-11)
  driver_id UUID PRIMARY KEY REFERENCES iam.users(id) ON DELETE CASCADE,
  onepay_merchant_id TEXT NOT NULL,
  bound_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  status TEXT NOT NULL DEFAULT 'ACTIVE' CHECK (status IN ('ACTIVE','SUSPENDED')));

CREATE TABLE registry.documents (                             -- doc expiry tracking (E-03)
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  driver_id UUID NOT NULL REFERENCES iam.users(id) ON DELETE CASCADE,
  vehicle_id UUID REFERENCES registry.vehicles(id) ON DELETE CASCADE,
  kind TEXT NOT NULL CHECK (kind IN ('driving_license','registration','permit','insurance')),
  file_url TEXT NOT NULL,
  issued_at TIMESTAMPTZ,
  expires_at TIMESTAMPTZ,
  status TEXT NOT NULL DEFAULT 'VALID' CHECK (status IN ('VALID','EXPIRING','EXPIRED','REJECTED')),
  created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  updated_at TIMESTAMPTZ NOT NULL DEFAULT now());
CREATE INDEX ix_documents_expiry ON registry.documents(expires_at) WHERE status <> 'EXPIRED';  -- E-03 nightly job
CREATE INDEX ix_documents_driver ON registry.documents(driver_id);
-- AL-10: a valid kind='insurance' document is MANDATORY for ALL modes (A/B/C). A vehicle cannot reach
--   status='APPROVED' without one (enforced in registry-svc). Admin-registered trains are exempt (line cover).

CREATE TABLE registry.fleet_vehicles (                        -- a fleet operates Mode A and/or B only (NEVER C)
  fleet_id UUID NOT NULL REFERENCES registry.fleets(id) ON DELETE CASCADE,
  vehicle_id UUID NOT NULL REFERENCES registry.vehicles(id) ON DELETE CASCADE,
  mode CHAR(1) NOT NULL CHECK (mode IN ('A','B')),            -- AL-03
  PRIMARY KEY (fleet_id, vehicle_id));

CREATE TABLE registry.fleet_assignments (                     -- driver ↔ vehicle (US-13.2/13.9)
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  fleet_id UUID NOT NULL REFERENCES registry.fleets(id) ON DELETE CASCADE,
  vehicle_id UUID NOT NULL REFERENCES registry.vehicles(id) ON DELETE CASCADE,
  driver_id UUID NOT NULL REFERENCES iam.users(id),
  assigned_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  revoked_at TIMESTAMPTZ);
CREATE UNIQUE INDEX ux_fleet_assign_active ON registry.fleet_assignments(vehicle_id, driver_id)
  WHERE revoked_at IS NULL;

CREATE TABLE registry.shares (                                -- Mode B sharing grant (D-22)
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  vehicle_id UUID NOT NULL REFERENCES registry.vehicles(id) ON DELETE CASCADE,
  grantee_user_id UUID NOT NULL REFERENCES iam.users(id) ON DELETE CASCADE,
  state TEXT NOT NULL DEFAULT 'PENDING' CHECK (state IN ('PENDING','ACCEPTED','REVOKED','EXPIRED')),
  expires_at TIMESTAMPTZ,
  accepted_at TIMESTAMPTZ,
  revoked_at TIMESTAMPTZ,
  created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  updated_at TIMESTAMPTZ NOT NULL DEFAULT now());
CREATE UNIQUE INDEX ux_shares_active ON registry.shares(vehicle_id, grantee_user_id)
  WHERE state IN ('PENDING','ACCEPTED');

CREATE TABLE registry.operators (                             -- legacy fleet-org stub (referenced by prov.tracker_bindings)
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  name TEXT NOT NULL,
  created_at TIMESTAMPTZ NOT NULL DEFAULT now());
```

---

## 3. `prov` — Tracker Provisioning (T-02, T-03, T-08)

```sql
CREATE TABLE prov.tracker_bindings (                          -- IMEI ↔ vehicle source of truth (T-03)
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  imei TEXT NOT NULL,                                         -- 15-digit IMEI
  vehicle_id UUID NOT NULL REFERENCES registry.vehicles(id) ON DELETE CASCADE,
  fleet_id UUID REFERENCES registry.operators(id),            -- fleet scope (RLS)
  credential_serial TEXT NOT NULL,
  credential_type TEXT NOT NULL CHECK (credential_type IN ('x509','psk')),
  state TEXT NOT NULL DEFAULT 'ACTIVE' CHECK (state IN ('ACTIVE','QUARANTINED','REVOKED')),  -- anti-clone (T-08)
  rotates_at TIMESTAMPTZ NOT NULL,                            -- 90-day rotation (T-02)
  source TEXT,
  last_seen_at TIMESTAMPTZ,
  signal_strength SMALLINT,
  battery_mv INTEGER,
  sat_count SMALLINT,
  created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  updated_at TIMESTAMPTZ NOT NULL DEFAULT now());
CREATE UNIQUE INDEX ux_tracker_imei_active ON prov.tracker_bindings(imei) WHERE state = 'ACTIVE';  -- anti-clone
CREATE INDEX ix_tracker_vehicle ON prov.tracker_bindings(vehicle_id);

CREATE TABLE prov.device_certs (                              -- credential lifecycle (T-02)
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  binding_id UUID NOT NULL REFERENCES prov.tracker_bindings(id) ON DELETE CASCADE,
  serial TEXT NOT NULL UNIQUE,
  kind TEXT NOT NULL CHECK (kind IN ('x509','psk')),
  pem_or_token_hash BYTEA NOT NULL,
  issued_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  expires_at TIMESTAMPTZ NOT NULL,
  revoked_at TIMESTAMPTZ);
```

---

## 4. `trips` — Mode A/B Tracking Sessions (D-03)

Mode A (public transport) and Mode B (private/shared) tracking only. Mode C ride control-plane lives in
`rides`.

```sql
CREATE TABLE trips.sessions (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  vehicle_id UUID NOT NULL REFERENCES registry.vehicles(id),
  driver_id UUID NOT NULL REFERENCES iam.users(id),
  mode CHAR(1) NOT NULL CHECK (mode IN ('A','B')),
  state TEXT NOT NULL DEFAULT 'ACTIVE' CHECK (state IN ('ACTIVE','COMPLETED')),
  route_id UUID REFERENCES spatial.routes(id),                -- Mode A route
  auto_end_at_destination BOOLEAN NOT NULL DEFAULT false,     -- US-5.4
  destination_geo GEOGRAPHY(POINT,4326),                      -- 100 m geofence end
  end_reason TEXT CHECK (end_reason IN ('driver_ended','idle_timeout','geofence','admin')),
  started_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  ended_at TIMESTAMPTZ,
  created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  updated_at TIMESTAMPTZ NOT NULL DEFAULT now());
-- D-03 / US-9.6: a driver may have only ONE vehicle live at a time:
CREATE UNIQUE INDEX ux_sessions_active_driver ON trips.sessions(driver_id) WHERE state = 'ACTIVE';
CREATE INDEX ix_sessions_vehicle ON trips.sessions(vehicle_id, started_at DESC);

CREATE TABLE trips.events (                                   -- business events on a session
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  session_id UUID NOT NULL REFERENCES trips.sessions(id) ON DELETE CASCADE,
  kind TEXT NOT NULL,
  payload JSONB,
  ts TIMESTAMPTZ NOT NULL DEFAULT now());
CREATE INDEX ix_trip_events_session ON trips.events(session_id, ts DESC);

CREATE TABLE trips.ratings (                                  -- 1–5 stars + optional comment (US-8.6/18.1/18.2)
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  subject_kind TEXT NOT NULL CHECK (subject_kind IN ('session','ride')),
  subject_id UUID NOT NULL,
  rater_id UUID NOT NULL REFERENCES iam.users(id),
  ratee_id UUID NOT NULL REFERENCES iam.users(id),
  stars SMALLINT NOT NULL CHECK (stars BETWEEN 1 AND 5),
  comment TEXT,
  direction TEXT NOT NULL CHECK (direction IN ('passenger_to_driver','driver_to_passenger')),
  created_at TIMESTAMPTZ NOT NULL DEFAULT now());
CREATE INDEX ix_ratings_ratee ON trips.ratings(ratee_id);
CREATE INDEX ix_ratings_subject ON trips.ratings(subject_kind, subject_id);

-- Operational 1/min position sample for Mode A/B history (monthly range partitions; §9.2).
-- High-frequency hardware telemetry goes to telemetry.positions (§17), NOT here.
CREATE TABLE trips.position_samples (
  id BIGINT GENERATED ALWAYS AS IDENTITY,
  session_id UUID NOT NULL,
  vehicle_id UUID NOT NULL,
  geo GEOGRAPHY(POINT,4326) NOT NULL,
  speed_mps REAL,
  heading_deg SMALLINT,
  sample_ts TIMESTAMPTZ NOT NULL,
  PRIMARY KEY (id, sample_ts)
) PARTITION BY RANGE (sample_ts);
CREATE INDEX ix_possample_session ON trips.position_samples(session_id, sample_ts DESC);
-- Example monthly partition (created by a maintenance job):
-- CREATE TABLE trips.position_samples_2026_06 PARTITION OF trips.position_samples
--   FOR VALUES FROM ('2026-06-01') TO ('2026-07-01');
```

---

## 5. `rides` — Mode C Ride Aggregate (R-01, R-02, R-14, R-18, P-01, P-06)

```sql
CREATE TABLE rides.rides (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  passenger_id UUID NOT NULL REFERENCES iam.users(id),
  client_request_id UUID NOT NULL,                            -- idempotency partner (R-18)
  booker_id UUID NOT NULL REFERENCES iam.users(id),           -- = passenger unless proxy (P-01)
  rider_id UUID REFERENCES iam.users(id),                     -- NULL if unregistered (P-01)
  rider_phone_hash BYTEA,                                     -- hashed PII unregistered rider (P-03)
  rider_name TEXT,
  is_proxy BOOLEAN NOT NULL DEFAULT false,                    -- P-01
  kind SMALLINT NOT NULL DEFAULT 0 CHECK (kind IN (0,1,2)),   -- 0=passenger,1=proxy,2=package (P-06)
  vehicle_type TEXT NOT NULL,                                 -- requested tier
  pickup_geo GEOGRAPHY(POINT,4326) NOT NULL,
  dropoff_geo GEOGRAPHY(POINT,4326) NOT NULL,
  state TEXT NOT NULL DEFAULT 'Requested' CHECK (state IN
    ('Requested','Matching','Offered','Accepted','DriverArrived','InProgress','Completed',
     'PaymentPending','Paid','CashSettled','CashOnDeliveryCollected','Disputed',
     'CancelledByRiderBeforeAccept','CancelledByRiderAfterAccept','CancelledByDriver',
     'ExpiredNoDriver','NoShowRider','NoShowDriver')),
  accepted_driver_id UUID REFERENCES iam.users(id),
  accepted_vehicle_id UUID REFERENCES registry.vehicles(id),
  current_offer_id UUID,
  offer_expires_at TIMESTAMPTZ,                               -- 15s TTL hint
  dispatch_algorithm_version SMALLINT,                        -- R-11
  package_size CHAR(1) CHECK (package_size IN ('S','M','L')), -- P-06
  package_description TEXT,
  pickup_otp_hash BYTEA,                                      -- HMAC-SHA256 4-digit OTP (P-07)
  delivery_otp_hash BYTEA,
  pickup_otp_attempts SMALLINT NOT NULL DEFAULT 0,            -- max 5 → admin queue
  delivery_otp_attempts SMALLINT NOT NULL DEFAULT 0,
  payment_method TEXT NOT NULL DEFAULT 'cash'
    CHECK (payment_method IN ('cash','lankaqr','onepay','cod')),  -- P-04/P-08
  version BIGINT NOT NULL DEFAULT 0,                          -- optimistic concurrency (R-02)
  created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  updated_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  terminal_at TIMESTAMPTZ,
  CHECK (kind <> 2 OR (package_size IS NOT NULL AND pickup_otp_hash IS NOT NULL AND delivery_otp_hash IS NOT NULL)),
  CHECK (is_proxy = false OR (rider_name IS NOT NULL AND (rider_id IS NOT NULL OR rider_phone_hash IS NOT NULL))));
CREATE UNIQUE INDEX ux_rides_idem ON rides.rides(passenger_id, client_request_id);          -- R-18
CREATE UNIQUE INDEX ux_rides_open_passenger ON rides.rides(passenger_id)                    -- one open ride/rider
  WHERE state NOT IN ('Completed','Paid','CashSettled','CashOnDeliveryCollected','Disputed',
    'CancelledByRiderBeforeAccept','CancelledByRiderAfterAccept','CancelledByDriver',
    'ExpiredNoDriver','NoShowRider','NoShowDriver');
CREATE UNIQUE INDEX ux_rides_driver_busy ON rides.rides(accepted_driver_id)                 -- O2 + R-10
  WHERE state IN ('Accepted','DriverArrived','InProgress','PaymentPending');
CREATE INDEX ix_rides_driver ON rides.rides(accepted_driver_id, created_at DESC);
CREATE INDEX ix_rides_passenger_hist ON rides.rides(passenger_id, created_at DESC);

CREATE TABLE rides.transitions (                              -- immutable state-change audit
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  ride_id UUID NOT NULL REFERENCES rides.rides(id) ON DELETE CASCADE,
  from_state TEXT,
  to_state TEXT NOT NULL,
  reason_code TEXT,
  actor_type TEXT NOT NULL,
  actor_id UUID,
  ts TIMESTAMPTZ NOT NULL DEFAULT now());
CREATE INDEX ix_transitions_ride ON rides.transitions(ride_id, ts);

CREATE TABLE rides.command_log (                              -- idempotent replay (R-14)
  idempotency_key TEXT PRIMARY KEY,
  ride_id UUID,
  actor_type TEXT NOT NULL,
  actor_id UUID,
  command TEXT NOT NULL,
  request_hash BYTEA NOT NULL,
  response_status SMALLINT,
  response_body JSONB,
  ts TIMESTAMPTZ NOT NULL DEFAULT now());

CREATE TABLE rides.timers (                                   -- durable backstop (R-04); Quartz scans due rows
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  ride_id UUID NOT NULL REFERENCES rides.rides(id) ON DELETE CASCADE,
  kind TEXT NOT NULL CHECK (kind IN ('offer_expiry','arrival_grace','no_show','payment_pending',
    'offline_grace','location_request_expiry','otp_attempt_window','cod_uncollected')),
  fire_at TIMESTAMPTZ NOT NULL,
  fired_at TIMESTAMPTZ,
  payload JSONB);
CREATE INDEX ix_timers_due ON rides.timers(fire_at) WHERE fired_at IS NULL;

CREATE TABLE rides.location_requests (                        -- proxy GPS round-trip (P-02, P-03)
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  ride_id UUID REFERENCES rides.rides(id) ON DELETE CASCADE,
  request_id UUID NOT NULL UNIQUE,
  booker_id UUID NOT NULL REFERENCES iam.users(id),
  rider_id UUID REFERENCES iam.users(id),
  rider_phone_hash BYTEA,
  state TEXT NOT NULL DEFAULT 'Pending'
    CHECK (state IN ('Pending','Confirmed','Declined','Expired','RiderNotRegistered')),
  issued_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  ttl_seconds INTEGER NOT NULL DEFAULT 300,
  resolved_at TIMESTAMPTZ,
  resolved_geo GEOGRAPHY(POINT,4326),
  resolved_accuracy_m NUMERIC);

CREATE TABLE rides.proof_artifacts (                          -- delivery proof (P-10); 365-day retention, PDPA-erasable
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  ride_id UUID NOT NULL REFERENCES rides.rides(id) ON DELETE CASCADE,
  kind TEXT NOT NULL CHECK (kind IN ('delivery_photo','signature','pickup_photo')),
  storage_url TEXT NOT NULL,
  sha256 BYTEA NOT NULL,
  captured_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  captured_geo GEOGRAPHY(POINT,4326));
CREATE INDEX ix_proof_ride ON rides.proof_artifacts(ride_id);

CREATE TABLE rides.outbox (                                   -- transactional outbox (R-13, E-09)
  id BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
  aggregate_id UUID NOT NULL,
  event_type TEXT NOT NULL,
  payload JSONB NOT NULL,
  created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  dispatched_at TIMESTAMPTZ);
CREATE INDEX ix_outbox_undispatched ON rides.outbox(id) WHERE dispatched_at IS NULL;
-- A NOTIFY on this table's INSERT (LISTEN/NOTIFY channel 'ride_outbox') wakes the dispatcher sub-50ms (E-09).
```

---

## 6. `dispatch` — Candidate Scoring, Offers, Job Board, Levels, Directional

```sql
CREATE TABLE dispatch.driver_presence (
  driver_id UUID PRIMARY KEY REFERENCES iam.users(id),
  vehicle_id UUID NOT NULL REFERENCES registry.vehicles(id),
  vehicle_type TEXT NOT NULL,
  state TEXT NOT NULL DEFAULT 'OFFLINE' CHECK (state IN ('OFFLINE','AVAILABLE','OFFERED','ON_RIDE')),
  geo GEOGRAPHY(POINT,4326),
  driver_home GEOGRAPHY(POINT,4326),                          -- D-06 Job Board ST_DWithin
  last_seen_at TIMESTAMPTZ,
  updated_at TIMESTAMPTZ NOT NULL DEFAULT now());
CREATE INDEX ix_presence_geo ON dispatch.driver_presence USING gist(geo) WHERE state = 'AVAILABLE';
CREATE INDEX ix_presence_home ON dispatch.driver_presence USING gist(driver_home);

CREATE TABLE dispatch.offers (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  ride_id UUID NOT NULL REFERENCES rides.rides(id) ON DELETE CASCADE,
  driver_id UUID NOT NULL REFERENCES iam.users(id),
  status TEXT NOT NULL DEFAULT 'OFFERED' CHECK (status IN ('OFFERED','ACCEPTED','DECLINED','EXPIRED')),
  sent_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  expires_at TIMESTAMPTZ NOT NULL,
  responded_at TIMESTAMPTZ);
CREATE UNIQUE INDEX ux_offers_driver_live ON dispatch.offers(driver_id) WHERE status IN ('OFFERED','ACCEPTED');  -- R-10
CREATE INDEX ix_offers_ride ON dispatch.offers(ride_id);

CREATE TABLE dispatch.candidate_scores (                      -- versioned scoring audit (R-11, P-11, DT-02)
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  ride_id UUID NOT NULL REFERENCES rides.rides(id) ON DELETE CASCADE,
  driver_id UUID NOT NULL REFERENCES iam.users(id),
  score NUMERIC NOT NULL,
  package_size_compatible BOOLEAN,                            -- P-11
  breakdown JSONB NOT NULL,                                   -- includes directional bearings/dist (DT-02)
  dispatch_algorithm_version SMALLINT NOT NULL,
  evaluated_at TIMESTAMPTZ NOT NULL DEFAULT now());
CREATE INDEX ix_candidate_ride ON dispatch.candidate_scores(ride_id);

CREATE TABLE dispatch.scheduled_rides (                       -- advance bookings (US-6A.4)
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  ride_id UUID REFERENCES rides.rides(id),
  passenger_id UUID NOT NULL REFERENCES iam.users(id),
  pickup_geo GEOGRAPHY(POINT,4326) NOT NULL,
  dropoff_geo GEOGRAPHY(POINT,4326) NOT NULL,
  vehicle_type TEXT NOT NULL,
  pickup_time TIMESTAMPTZ NOT NULL,
  status TEXT NOT NULL DEFAULT 'SCHEDULED' CHECK (status IN ('SCHEDULED','DISPATCHED','CANCELLED')),
  created_at TIMESTAMPTZ NOT NULL DEFAULT now());
CREATE INDEX ix_sched_pickup ON dispatch.scheduled_rides USING gist(pickup_geo);             -- Job Board 30km (D-06)

CREATE TABLE dispatch.job_board_intents (                     -- driver intent (US-6A.5)
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  scheduled_ride_id UUID NOT NULL REFERENCES dispatch.scheduled_rides(id) ON DELETE CASCADE,
  driver_id UUID NOT NULL REFERENCES iam.users(id),
  ts TIMESTAMPTZ NOT NULL DEFAULT now(),
  UNIQUE (scheduled_ride_id, driver_id));

CREATE TABLE dispatch.driver_levels (                         -- Driver Level System (US-6A.6)
  driver_id UUID PRIMARY KEY REFERENCES iam.users(id),
  level SMALLINT NOT NULL DEFAULT 3 CHECK (level BETWEEN 1 AND 3),  -- L1 loses scheduled-ride access
  rating_points INTEGER NOT NULL DEFAULT 0,
  level_up_threshold INTEGER NOT NULL DEFAULT 500,
  updated_at TIMESTAMPTZ NOT NULL DEFAULT now());

CREATE TABLE dispatch.no_show_events (                        -- level-decrement audit (US-6A.7)
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  driver_id UUID NOT NULL REFERENCES iam.users(id),
  ride_id UUID,
  ts TIMESTAMPTZ NOT NULL DEFAULT now());

CREATE TABLE dispatch.cancellation_penalties (                -- Rs50 cross-trip (D-05)
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  passenger_id UUID NOT NULL REFERENCES iam.users(id),
  original_ride_id UUID NOT NULL,
  affected_driver_id UUID NOT NULL REFERENCES iam.users(id),
  amount_minor INTEGER NOT NULL DEFAULT 5000 CHECK (amount_minor >= 0),
  status TEXT NOT NULL DEFAULT 'OUTSTANDING' CHECK (status IN ('OUTSTANDING','SETTLED')),
  applied_ride_id UUID,
  created_at TIMESTAMPTZ NOT NULL DEFAULT now());
CREATE UNIQUE INDEX ux_penalty_apply ON dispatch.cancellation_penalties(id, applied_ride_id);  -- idempotent (D-05)

CREATE TABLE dispatch.directional_filters (                   -- Directional Travel (DT-01, DT-03)
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  driver_id UUID NOT NULL REFERENCES iam.users(id),
  destination_geo GEOGRAPHY(POINT,4326) NOT NULL,
  label TEXT,
  set_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  expires_at TIMESTAMPTZ NOT NULL,
  cleared_at TIMESTAMPTZ,
  cleared_reason TEXT CHECK (cleared_reason IN ('expiry','manual','offline','first_matched_trip')),
  used_date DATE NOT NULL,                                    -- Asia/Colombo (D-38)
  created_at TIMESTAMPTZ NOT NULL DEFAULT now());
CREATE UNIQUE INDEX ux_directional_active ON dispatch.directional_filters(driver_id) WHERE cleared_at IS NULL;
CREATE INDEX ix_directional_uses ON dispatch.directional_filters(driver_id, used_date);       -- max_uses_per_day (DT-03)

CREATE TABLE dispatch.timers (                                -- directional expiry backstop (DT-04)
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  driver_id UUID NOT NULL REFERENCES iam.users(id),
  kind TEXT NOT NULL DEFAULT 'directional_expiry',
  fire_at TIMESTAMPTZ NOT NULL,
  fired_at TIMESTAMPTZ);
CREATE INDEX ix_dispatch_timers_due ON dispatch.timers(fire_at) WHERE fired_at IS NULL;

CREATE TABLE dispatch.directional_config (                    -- admin params (DT-02), single row id=1
  id SMALLINT PRIMARY KEY DEFAULT 1,
  theta_max_deg SMALLINT NOT NULL DEFAULT 45,
  detour_max_m INTEGER NOT NULL DEFAULT 2000,
  progress_min_m INTEGER NOT NULL DEFAULT 250,
  max_uses_per_day SMALLINT NOT NULL DEFAULT 2,
  max_duration_sec INTEGER NOT NULL DEFAULT 7200,
  clear_on_first_trip BOOLEAN NOT NULL DEFAULT false);
```

---

## 7. `reputation` — Counters & Block States (D-04, E-07)

```sql
CREATE TABLE reputation.counters (
  user_id UUID PRIMARY KEY REFERENCES iam.users(id),
  cancellations_continuous SMALLINT NOT NULL DEFAULT 0,       -- 3 continuous → BOOKING_DISABLED (US-6A.10b)
  reports_total INTEGER NOT NULL DEFAULT 0,
  no_shows INTEGER NOT NULL DEFAULT 0,
  window_reset_at TIMESTAMPTZ,
  updated_at TIMESTAMPTZ NOT NULL DEFAULT now());

CREATE TABLE reputation.block_states (                        -- consumed via gRPC by dispatch-svc (D-04)
  user_id UUID PRIMARY KEY REFERENCES iam.users(id),
  state TEXT NOT NULL DEFAULT 'OK' CHECK (state IN ('OK','WARN','BOOKING_DISABLED','DELISTED')),
  expires_at TIMESTAMPTZ,
  updated_at TIMESTAMPTZ NOT NULL DEFAULT now());

CREATE TABLE reputation.fraud_flags (                         -- anti-collusion / ride-farming (E-07)
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  kind TEXT NOT NULL,
  subject_id UUID,
  related_id UUID,
  detail JSONB,
  ts TIMESTAMPTZ NOT NULL DEFAULT now());
```

---

## 8. `safety` — SOS, Trip Share, Reports, Blocks (D-33, D-34)

```sql
CREATE TABLE safety.sos_events (                              -- passenger + driver SOS (US-12.11)
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  user_id UUID NOT NULL REFERENCES iam.users(id),
  role TEXT NOT NULL CHECK (role IN ('passenger','driver')),
  ride_id UUID,
  lat DOUBLE PRECISION NOT NULL,
  lng DOUBLE PRECISION NOT NULL,
  emergency_contact TEXT,
  sms_status TEXT,
  primary_gateway TEXT,
  secondary_gateway TEXT,
  admin_acked_at TIMESTAMPTZ,
  ts TIMESTAMPTZ NOT NULL DEFAULT now());
CREATE INDEX ix_sos_user ON safety.sos_events(user_id, ts DESC);

CREATE TABLE safety.trip_share_tokens (                       -- D-34; reused for package recipient (P-09)
  token TEXT PRIMARY KEY,
  trip_id UUID NOT NULL,
  scope TEXT NOT NULL CHECK (scope IN ('trip_view','package_recipient')),
  expires_at TIMESTAMPTZ NOT NULL,
  revoked_at TIMESTAMPTZ,
  created_at TIMESTAMPTZ NOT NULL DEFAULT now());

CREATE TABLE safety.vehicle_reports (                         -- 3 confirmed → auto-delist (US-12.6)
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  reporter_id UUID NOT NULL REFERENCES iam.users(id),
  vehicle_id UUID NOT NULL REFERENCES registry.vehicles(id),
  ride_id UUID,
  reason TEXT NOT NULL,
  status TEXT NOT NULL DEFAULT 'PENDING' CHECK (status IN ('PENDING','CONFIRMED','DISMISSED')),
  created_at TIMESTAMPTZ NOT NULL DEFAULT now());
CREATE INDEX ix_vreports_vehicle ON safety.vehicle_reports(vehicle_id);

CREATE TABLE safety.blocked_drivers (                         -- passenger blocks driver (US-12.10)
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  passenger_id UUID NOT NULL REFERENCES iam.users(id),
  driver_id UUID NOT NULL REFERENCES iam.users(id),
  created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  UNIQUE (passenger_id, driver_id));

CREATE TABLE safety.location_request_audit (                  -- proxy-abuse audit (P-12)
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  booker_id UUID NOT NULL REFERENCES iam.users(id),
  rider_phone_hash BYTEA NOT NULL,
  request_id UUID NOT NULL,
  decision TEXT NOT NULL CHECK (decision IN ('Confirmed','Declined','Expired','NotRegistered')),
  ts TIMESTAMPTZ NOT NULL DEFAULT now());
```

---

## 9. `fares` — Tariffs, Payments, Refunds, Earnings (D-10, E-05, E-10)

```sql
CREATE TABLE fares.tariffs (                                  -- Mode C only (vehicle_type × rate)
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  vehicle_type TEXT NOT NULL,
  first_km_minor INTEGER NOT NULL CHECK (first_km_minor >= 0),
  per_km_minor INTEGER NOT NULL CHECK (per_km_minor >= 0),
  peak_surcharge_pct SMALLINT NOT NULL DEFAULT 20,
  night_surcharge_pct SMALLINT NOT NULL DEFAULT 15,
  effective_from TIMESTAMPTZ NOT NULL DEFAULT now(),
  UNIQUE (vehicle_type, effective_from));

CREATE TABLE fares.peak_windows (                             -- admin peak/night windows
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  kind TEXT NOT NULL CHECK (kind IN ('peak','night')),
  start_local TIME NOT NULL,
  end_local TIME NOT NULL,
  multiplier_pct SMALLINT NOT NULL);

CREATE TABLE fares.ride_payments (                            -- payment state machine (D-10, P-04, P-08, E-10)
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  ride_id UUID NOT NULL REFERENCES rides.rides(id),
  state TEXT NOT NULL DEFAULT 'Initiated' CHECK (state IN
    ('Initiated','Pending','Succeeded','Failed','Retried','FellBackToCash',
     'CashOnDelivery','CashOnDeliveryCollected','Overpaid','Refunded','PartiallyRefunded','Disputed')),
  method TEXT NOT NULL CHECK (method IN ('cash','lankaqr','onepay','cod')),
  amount_minor INTEGER NOT NULL CHECK (amount_minor >= 0),
  surcharge_minor INTEGER NOT NULL DEFAULT 0 CHECK (surcharge_minor >= 0),  -- OnePay +5% (US-8.11)
  tip_amount_minor INTEGER NOT NULL DEFAULT 0 CHECK (tip_amount_minor >= 0),-- E-10
  currency CHAR(3) NOT NULL DEFAULT 'LKR',
  payer_role TEXT NOT NULL DEFAULT 'rider' CHECK (payer_role IN ('rider','booker')),  -- P-04
  payer_user_id UUID REFERENCES iam.users(id),
  retry_of_payment_id UUID REFERENCES fares.ride_payments(id),               -- D-10 retry chain
  provider_transaction_id TEXT UNIQUE,                                       -- callback idempotency (R-19)
  attempt_no SMALLINT NOT NULL DEFAULT 1,
  created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  updated_at TIMESTAMPTZ NOT NULL DEFAULT now());
CREATE INDEX ix_ridepay_ride ON fares.ride_payments(ride_id);

CREATE TABLE fares.refunds (                                  -- refund/dispute (E-05)
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  ride_payment_id UUID NOT NULL REFERENCES fares.ride_payments(id),
  kind TEXT NOT NULL CHECK (kind IN ('full','partial','overpaid_reversal')),
  amount_minor INTEGER NOT NULL CHECK (amount_minor >= 0),
  status TEXT NOT NULL DEFAULT 'Requested' CHECK (status IN ('Requested','Submitted','Succeeded','Failed')),
  provider_refund_id TEXT,
  reason_code TEXT,
  requested_by UUID,
  requested_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  settled_at TIMESTAMPTZ);

CREATE TABLE fares.driver_earnings (                          -- daily earnings aggregate (Asia/Colombo, D-38)
  driver_id UUID NOT NULL REFERENCES iam.users(id),
  earn_date DATE NOT NULL,
  trips INTEGER NOT NULL DEFAULT 0,
  gross_minor INTEGER NOT NULL DEFAULT 0 CHECK (gross_minor >= 0),
  daily_fee_minor INTEGER NOT NULL DEFAULT 0 CHECK (daily_fee_minor >= 0),
  PRIMARY KEY (driver_id, earn_date));
```

---

## 10. `billing` — Daily Fee, Double-Entry Ledger, Credit Transfers, Vouchers, Fleet

> "Reseller" is **not a role/account/capability** (AL-01: `owner_type` has no `reseller`) — any driver who bought bulk credit transfers it to others by Driver ID with **no per-transfer commission**.
> Bank-transfer top-ups removed (AL-05). Fleet wallet added (`owner_type='fleet'`, AL-03).
> The double-entry ledger (`accounts`/`journal_entries`/`journal_postings`) is the **master** of money;
> `wallets`/`wallet_transactions` are read-model mirrors.

```sql
CREATE TABLE billing.plans (                                  -- 7-tier daily fee rates
  vehicle_type TEXT PRIMARY KEY,
  daily_fee_minor INTEGER NOT NULL CHECK (daily_fee_minor >= 0),
  mode CHAR(1) NOT NULL,
  updated_at TIMESTAMPTZ NOT NULL DEFAULT now());

CREATE TABLE billing.daily_fee_charges (                      -- idempotent daily fee, first trip free (D-13)
  driver_id UUID NOT NULL REFERENCES iam.users(id),
  vehicle_id UUID NOT NULL REFERENCES registry.vehicles(id),
  fee_date DATE NOT NULL,                                     -- Asia/Colombo (D-13/D-38)
  amount_minor INTEGER NOT NULL CHECK (amount_minor >= 0),
  trips_that_day INTEGER NOT NULL DEFAULT 0,
  status TEXT NOT NULL DEFAULT 'PAID' CHECK (status IN ('PAID','WAIVED_FIRST_TRIP')),
  charged_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  PRIMARY KEY (driver_id, vehicle_id, fee_date));

CREATE TABLE billing.monthly_subscriptions (                  -- Mode B ~Rs300/mo, first month free
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  vehicle_id UUID NOT NULL REFERENCES registry.vehicles(id),
  period_month DATE NOT NULL,
  amount_minor INTEGER NOT NULL DEFAULT 30000 CHECK (amount_minor >= 0),
  status TEXT NOT NULL DEFAULT 'DUE' CHECK (status IN ('FREE','DUE','PAID')),
  UNIQUE (vehicle_id, period_month));

-- Double-entry ledger (D-09):
CREATE TABLE billing.accounts (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  owner_type TEXT NOT NULL CHECK (owner_type IN ('driver','fleet','platform','suspense')),  -- AL-01/AL-03
  owner_id UUID,
  currency CHAR(3) NOT NULL DEFAULT 'LKR',
  balance_minor BIGINT NOT NULL DEFAULT 0,                    -- may be negative (suspense); driver non-negativity in app
  created_at TIMESTAMPTZ NOT NULL DEFAULT now());
CREATE INDEX ix_accounts_owner ON billing.accounts(owner_type, owner_id);

CREATE TABLE billing.journal_entries (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  ts TIMESTAMPTZ NOT NULL DEFAULT now(),
  kind TEXT NOT NULL CHECK (kind IN ('topup','daily_fee','trip_payment','penalty_settle',
    'adjustment','tip_payout','payment_refund','overpaid_reversal',
    'voucher_purchase','driver_transfer')),  -- no 'reseller_commission' (AL-01: no per-transfer commission)
  idempotency_key TEXT NOT NULL UNIQUE,
  description TEXT);

CREATE TABLE billing.journal_postings (
  id BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
  entry_id UUID NOT NULL REFERENCES billing.journal_entries(id) ON DELETE CASCADE,
  account_id UUID NOT NULL REFERENCES billing.accounts(id),
  amount_minor BIGINT NOT NULL);                              -- Σ per entry MUST = 0 (trigger below)
CREATE INDEX ix_postings_account ON billing.journal_postings(account_id);
CREATE INDEX ix_postings_entry ON billing.journal_postings(entry_id);

-- Balanced-entry enforcement (deferred, so a multi-row entry can be inserted in one txn):
CREATE OR REPLACE FUNCTION billing.assert_balanced() RETURNS trigger AS $$
BEGIN
  IF (SELECT COALESCE(SUM(amount_minor),0) FROM billing.journal_postings WHERE entry_id = NEW.entry_id) <> 0 THEN
    RAISE EXCEPTION 'journal entry % not balanced', NEW.entry_id;
  END IF;
  RETURN NULL;
END; $$ LANGUAGE plpgsql;
CREATE CONSTRAINT TRIGGER trg_balanced AFTER INSERT OR UPDATE ON billing.journal_postings
  DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION billing.assert_balanced();

-- Read-model mirrors (master = ledger above):
CREATE TABLE billing.wallets (
  account_id UUID PRIMARY KEY REFERENCES billing.accounts(id) ON DELETE CASCADE,
  balance_minor BIGINT NOT NULL DEFAULT 0,
  updated_at TIMESTAMPTZ NOT NULL DEFAULT now());
CREATE TABLE billing.wallet_transactions (                    -- denormalised journal projection for fast history reads
  id BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
  account_id UUID NOT NULL REFERENCES billing.accounts(id) ON DELETE CASCADE,
  entry_id UUID NOT NULL REFERENCES billing.journal_entries(id),
  kind TEXT NOT NULL,
  amount_minor BIGINT NOT NULL,
  balance_after_minor BIGINT NOT NULL,
  description TEXT,
  ts TIMESTAMPTZ NOT NULL DEFAULT now());
CREATE INDEX ix_wallet_tx_account ON billing.wallet_transactions(account_id, ts DESC);

-- "Reseller" is NOT a role/account/capability (AL-01). Any driver who buys bulk credit cheaply can transfer it
-- to other drivers by Driver ID. Transfers move the EXACT value — there is NO per-transfer commission.
CREATE TABLE billing.voucher_discount_tiers (                 -- bulk-voucher commission/discount % per VOUCHER VALUE (denomination), admin-set in Admin Portal (AL-01)
  denomination_minor BIGINT PRIMARY KEY,                      -- the voucher value, e.g. 100000 = Rs 1,000
  discount_bps INTEGER NOT NULL CHECK (discount_bps BETWEEN 0 AND 10000),  -- per-value commission %, admin-set (e.g. 1000 bps = 10% → pay 90,000, credit 100,000); = reseller margin
  active BOOLEAN NOT NULL DEFAULT true,
  updated_by UUID REFERENCES iam.users(id),                   -- admin who set the tier (Admin Portal)
  updated_at TIMESTAMPTZ NOT NULL DEFAULT now());

CREATE TABLE billing.voucher_purchases (                      -- bulk credit voucher purchase (US-9.19) — credits buyer wallet at purchase, no redeem code
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  buyer_id UUID NOT NULL REFERENCES iam.users(id),
  denomination_minor BIGINT NOT NULL CHECK (denomination_minor >= 0),
  discount_bps_applied INTEGER NOT NULL CHECK (discount_bps_applied BETWEEN 0 AND 10000),
  paid_minor BIGINT NOT NULL CHECK (paid_minor >= 0),         -- amount charged to buyer (denomination − discount)
  credited_minor BIGINT NOT NULL CHECK (credited_minor >= 0), -- amount credited to buyer wallet (= denomination)
  gateway_ref TEXT,
  journal_entry_id UUID REFERENCES billing.journal_entries(id),
  created_at TIMESTAMPTZ NOT NULL DEFAULT now());

CREATE TABLE billing.credit_transfers (                       -- driver↔driver credit transfer, EXACT value, NO commission (US-9.13/9.21, AL-01)
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  sender_driver_id UUID NOT NULL REFERENCES iam.users(id),    -- credit-holding driver (approver / proactive sender)
  recipient_driver_id UUID NOT NULL REFERENCES iam.users(id), -- requester / recipient
  amount_minor BIGINT NOT NULL CHECK (amount_minor >= 0),     -- exact value debited from sender and credited to recipient (no commission)
  direction TEXT NOT NULL DEFAULT 'REQUESTED' CHECK (direction IN ('REQUESTED','DIRECT')),
  status TEXT NOT NULL DEFAULT 'PENDING' CHECK (status IN ('PENDING','APPROVED','REJECTED')),
  journal_entry_id UUID REFERENCES billing.journal_entries(id),
  created_at TIMESTAMPTZ NOT NULL DEFAULT now());
CREATE INDEX ix_credit_transfers_sender ON billing.credit_transfers(sender_driver_id, created_at DESC);
CREATE INDEX ix_credit_transfers_recipient ON billing.credit_transfers(recipient_driver_id, created_at DESC);

CREATE TABLE billing.fleet_invoices (                         -- monthly per-Mode-B-vehicle fleet billing (AL-03)
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  fleet_id UUID NOT NULL REFERENCES registry.fleets(id),
  period_month DATE NOT NULL,
  total_minor INTEGER NOT NULL CHECK (total_minor >= 0),      -- Σ per-Mode-B-vehicle monthly fee (Mode A free)
  status TEXT NOT NULL DEFAULT 'DUE' CHECK (status IN ('FREE','DUE','PAID')),
  journal_entry_id UUID REFERENCES billing.journal_entries(id),
  UNIQUE (fleet_id, period_month));
-- billing.bank_transfer_topups REMOVED (AL-05). Top-up = OnePay card / OnePay wallet / LankaQR only.
```

---

## 11. `comms` — VoIP & Notification Tokens (D-24/D-25)

```sql
CREATE TABLE comms.voip_sessions (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  ride_id UUID NOT NULL,
  livekit_room TEXT NOT NULL,
  started_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  ended_at TIMESTAMPTZ,
  masked_sms_fallback BOOLEAN NOT NULL DEFAULT false);

CREATE TABLE comms.notification_tokens (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  user_id UUID NOT NULL REFERENCES iam.users(id),
  platform TEXT NOT NULL CHECK (platform IN ('android','ios')),
  token TEXT NOT NULL,
  updated_at TIMESTAMPTZ NOT NULL DEFAULT now());
CREATE INDEX ix_notif_tokens_user ON comms.notification_tokens(user_id);
```

---

## 12. `docs` — Uploads & OCR Extractions (D-36)

```sql
CREATE TABLE docs.uploads (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  owner_id UUID NOT NULL REFERENCES iam.users(id),
  storage_url TEXT NOT NULL,
  sha256 BYTEA,
  kind TEXT,
  auto_delete_at TIMESTAMPTZ,                                 -- 90-day raw delete (NFR-28)
  created_at TIMESTAMPTZ NOT NULL DEFAULT now());

CREATE TABLE docs.extractions (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  upload_id UUID NOT NULL REFERENCES docs.uploads(id),
  doc_type TEXT NOT NULL,
  extracted JSONB,
  confidence NUMERIC,
  status TEXT NOT NULL DEFAULT 'PENDING' CHECK (status IN ('PENDING','EXTRACTED','MANUAL_REVIEW','FAILED')),
  redaction_applied BOOLEAN NOT NULL DEFAULT true,            -- D-36 pre-LLM PII redaction
  created_at TIMESTAMPTZ NOT NULL DEFAULT now());
```

---

## 13. `support` — Tickets (Epic 16)

```sql
CREATE TABLE support.tickets (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  user_id UUID NOT NULL REFERENCES iam.users(id),
  category TEXT NOT NULL,
  description TEXT NOT NULL,
  ride_id UUID,
  screenshot_url TEXT,
  status TEXT NOT NULL DEFAULT 'OPEN' CHECK (status IN ('OPEN','IN_PROGRESS','RESOLVED')),
  admin_response TEXT,
  created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  updated_at TIMESTAMPTZ NOT NULL DEFAULT now());
CREATE INDEX ix_tickets_user ON support.tickets(user_id, created_at DESC);
```

---

## 14. `content` — Localised Templates, FAQ, Broadcasts (D-26, Si/Ta/En)

```sql
CREATE TABLE content.notification_templates (
  template_key TEXT NOT NULL,
  language TEXT NOT NULL CHECK (language IN ('si','ta','en')),
  subject TEXT,
  body TEXT NOT NULL,
  version INTEGER NOT NULL DEFAULT 1,
  approved_by UUID,
  PRIMARY KEY (template_key, language, version));

CREATE TABLE content.faq_articles (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  category TEXT NOT NULL,
  title TEXT NOT NULL,
  body TEXT NOT NULL,
  language TEXT NOT NULL CHECK (language IN ('si','ta','en')),
  sort_order INTEGER NOT NULL DEFAULT 0);

CREATE TABLE content.broadcasts (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  audience JSONB,
  message_by_lang JSONB NOT NULL,
  scheduled_at TIMESTAMPTZ,
  created_at TIMESTAMPTZ NOT NULL DEFAULT now());
```

---

## 15. `audit` — Immutable Admin Log (D-35)

```sql
CREATE TABLE audit.events (
  id BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
  actor_id UUID,
  action TEXT NOT NULL,
  entity_type TEXT,
  entity_id UUID,
  before JSONB,
  after JSONB,
  ts TIMESTAMPTZ NOT NULL DEFAULT now());
REVOKE UPDATE, DELETE ON audit.events FROM PUBLIC;            -- append-only; 7-year retention if regulated
CREATE INDEX ix_audit_entity ON audit.events(entity_type, entity_id, ts DESC);
```

---

## 16. `pdpa` — Right-to-Erasure / Data Export (E-06)

```sql
CREATE TABLE pdpa.requests (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  user_id UUID NOT NULL REFERENCES iam.users(id),
  kind TEXT NOT NULL CHECK (kind IN ('export','erasure')),
  status TEXT NOT NULL DEFAULT 'Received'
    CHECK (status IN ('Received','InProgress','FulfilledHold','Fulfilled','Rejected')),
  requested_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  due_by TIMESTAMPTZ NOT NULL DEFAULT now() + INTERVAL '30 days',
  fulfilled_at TIMESTAMPTZ,
  hold_reason TEXT);

CREATE TABLE pdpa.fulfillment_artifacts (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  request_id UUID NOT NULL REFERENCES pdpa.requests(id),
  kind TEXT NOT NULL CHECK (kind IN ('export_zip','erasure_log')),
  storage_url TEXT NOT NULL,
  sha256 BYTEA,
  signed_at TIMESTAMPTZ);
```

---

## 17. `spatial` — PostGIS System of Record (§8)

```sql
CREATE TABLE spatial.routes (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  name TEXT NOT NULL,
  route_number TEXT,
  geom GEOMETRY(LineString,4326) NOT NULL,
  mode CHAR(1));
CREATE TABLE spatial.stops (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  name TEXT NOT NULL,
  geom GEOMETRY(Point,4326) NOT NULL);
CREATE TABLE spatial.geofences (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  name TEXT,
  kind TEXT,
  geom GEOMETRY(Polygon,4326) NOT NULL);
CREATE INDEX ix_routes_geom ON spatial.routes USING gist(geom);
CREATE INDEX ix_stops_geom ON spatial.stops USING gist(geom);
CREATE INDEX ix_geofences_geom ON spatial.geofences USING gist(geom);
```

---

## 18. `telemetry` — High-Frequency Tracker Hypertable (T-06, TimescaleDB)

`telemetry.positions` is a **TimescaleDB hypertable** (1-day chunks × 16 vehicle-hash space partitions)
on a logically separated tablespace — high-frequency tracker + mobile GPS. Operational Mode A/B 1-min
samples stay in `trips.position_samples`; this is the historical telematics system of record.

```sql
CREATE TABLE telemetry.positions (
  vehicle_id   UUID NOT NULL,
  sample_ts    TIMESTAMPTZ NOT NULL,                          -- GNSS UTC
  received_ts  TIMESTAMPTZ NOT NULL DEFAULT now(),
  seq          BIGINT NOT NULL,                               -- monotonic per vehicle (replay dedup, T-05/R-17)
  lat DOUBLE PRECISION NOT NULL,
  lng DOUBLE PRECISION NOT NULL,
  speed_mps REAL, heading_deg SMALLINT, accuracy_m REAL, hdop REAL, sat_count SMALLINT,
  source SMALLINT NOT NULL,                                   -- 0=mobile,1=gt06,2=jt808,3=h02,4=nmea-mqtt
  fleet_id UUID,                                              -- RLS scoping
  trip_id UUID);
SELECT create_hypertable('telemetry.positions','sample_ts',
  partitioning_column => 'vehicle_id', number_partitions => 16, chunk_time_interval => INTERVAL '1 day');
CREATE INDEX ON telemetry.positions (vehicle_id, sample_ts DESC);
CREATE INDEX ON telemetry.positions (fleet_id, sample_ts DESC) WHERE fleet_id IS NOT NULL;
CREATE UNIQUE INDEX ON telemetry.positions (vehicle_id, seq);  -- replay idempotency (T-05/R-17)

-- 1-minute rollup (continuous aggregate):
CREATE MATERIALIZED VIEW telemetry.positions_1m WITH (timescaledb.continuous) AS
  SELECT vehicle_id, time_bucket('1 minute', sample_ts) AS bucket,
         avg(speed_mps) AS avg_speed, max(speed_mps) AS max_speed, count(*) AS samples,
         last(lat, sample_ts) AS last_lat, last(lng, sample_ts) AS last_lng
  FROM telemetry.positions GROUP BY vehicle_id, bucket;
SELECT add_continuous_aggregate_policy('telemetry.positions_1m',
  start_offset => INTERVAL '3 hours', end_offset => INTERVAL '1 minute', schedule_interval => INTERVAL '1 minute');

-- Per-fleet health rollup (fleet-health-svc):
CREATE MATERIALIZED VIEW telemetry.fleet_health_5m WITH (timescaledb.continuous) AS
  SELECT fleet_id, time_bucket('5 minutes', sample_ts) AS bucket,
         count(DISTINCT vehicle_id) AS active_vehicles, count(*) AS samples
  FROM telemetry.positions WHERE fleet_id IS NOT NULL GROUP BY fleet_id, bucket;

-- Compression (after 7 days, ~10×) + raw retention (30 days):
ALTER TABLE telemetry.positions SET (timescaledb.compress,
  timescaledb.compress_segmentby = 'vehicle_id', timescaledb.compress_orderby = 'sample_ts DESC');
SELECT add_compression_policy('telemetry.positions', INTERVAL '7 days');
SELECT add_retention_policy('telemetry.positions', INTERVAL '30 days');

-- Fleet Row-Level Security (Epic 13):
ALTER TABLE telemetry.positions ENABLE ROW LEVEL SECURITY;
CREATE POLICY fleet_scope ON telemetry.positions USING (fleet_id = current_setting('app.fleet_id')::uuid);
```

---

## 19. Enumerations Reference (text + CHECK, no PG enum types)

| Enum | Values | Used by |
|---|---|---|
| `role` | passenger, driver, fleet_owner, admin, super_admin, verification_officer, support_csr, finance_officer, auditor | `iam.users.role`, `iam.user_roles` (nine canonical, AL-06; **no `reseller`**) |
| `fleet_role` | owner, manager, viewer | `iam.fleet_members` (AL-03) |
| `language` | si, ta, en | `iam.users.language`, `content.*` |
| `vehicle_type` | motorbike, three_wheeler, flex, sedan, mini_van, van, truck, mini_truck, bus, train | `registry.vehicles` (canonical, AL-09; **"car"→"sedan"**) |
| `mode` | A, B, C | `registry.vehicles`, `trips.sessions`, `registry.fleet_vehicles` (A/B only) |
| `vehicle.status` | PENDING, APPROVED, REJECTED, DEACTIVATED | `registry.vehicles.status` |
| `dispatch_state` | ACTIVE, DISPATCH_SUSPENDED | `registry.vehicles.dispatch_state` (E-03) |
| `ride.state` | Requested, Matching, Offered, Accepted, DriverArrived, InProgress, Completed, PaymentPending, Paid, CashSettled, CashOnDeliveryCollected, Disputed, CancelledByRiderBeforeAccept, CancelledByRiderAfterAccept, CancelledByDriver, ExpiredNoDriver, NoShowRider, NoShowDriver | `rides.rides.state` (R-01) |
| `payment.method` | cash, lankaqr, onepay, cod | `rides.rides.payment_method`, `fares.ride_payments.method` |
| `payment.state` | Initiated, Pending, Succeeded, Failed, Retried, FellBackToCash, CashOnDelivery, CashOnDeliveryCollected, Overpaid, Refunded, PartiallyRefunded, Disputed | `fares.ride_payments.state` (D-10) |
| `block_state` | OK, WARN, BOOKING_DISABLED, DELISTED | `reputation.block_states` |
| `document.status` | VALID, EXPIRING, EXPIRED, REJECTED | `registry.documents` (E-03) |
| `credential_type` | x509, psk | `prov.tracker_bindings`, `prov.device_certs` (T-02) |
| `journal.kind` | topup, daily_fee, trip_payment, penalty_settle, adjustment, tip_payout, payment_refund, overpaid_reversal, voucher_purchase, driver_transfer | `billing.journal_entries` (D-09); no `reseller_commission` (AL-01) |
| `owner_type` | driver, fleet, platform, suspense | `billing.accounts` (AL-01/AL-03; **no `reseller`**) |

> **Note on `NoShowDriver` (B0 GAP-G3 / backlog B4):** the state is present in the `ride.state` CHECK for
> completeness, but D5 §7 models driver-side no-show as `CancelledByDriver`. No transition currently writes
> `NoShowDriver`; keep it reserved or drop from the CHECK once the state machine owner decides.

---

## 20. Seed / Reference Data

```sql
-- Role catalog labels (optional; RBAC enforcement is via CHECK sets):
INSERT INTO iam.roles(role,label,is_internal) VALUES
  ('passenger','Passenger',false),('driver','Driver',false),('fleet_owner','Fleet Owner',false),
  ('admin','Administrator',true),('super_admin','Super Administrator',true),
  ('verification_officer','Verification Officer',true),('support_csr','Support CSR',true),
  ('finance_officer','Finance Officer',true),('auditor','Auditor',true)
ON CONFLICT (role) DO NOTHING;

-- 7-tier daily fee (billing.plans). Mode A free; Mode C tiered. Every Mode-C-registerable type has a row (AL-09):
INSERT INTO billing.plans(vehicle_type, daily_fee_minor, mode) VALUES
  ('bus',0,'A'),('train',0,'A'),
  ('motorbike',5000,'C'),('three_wheeler',10000,'C'),('flex',15000,'C'),
  ('sedan',20000,'C'),('mini_van',25000,'C'),('van',30000,'C')
ON CONFLICT (vehicle_type) DO NOTHING;
  -- Truck / Mini Truck: package-delivery types — admin-configured daily-fee rows (no default seeded).

-- Mode C fare tariffs (Rs minor units, AL-09; URD §8 v2.2). Every Mode-C-bookable type has a tariff row:
INSERT INTO fares.tariffs(vehicle_type, first_km_minor, per_km_minor, peak_surcharge_pct, night_surcharge_pct) VALUES
  ('motorbike',8000,6000,20,15),('three_wheeler',10000,8000,20,15),('flex',13000,9000,20,15),
  ('sedan',15000,10000,20,15),('mini_van',15000,11000,20,15),('van',15000,12000,20,15);
  -- Truck / Mini Truck (package delivery, Epic 20): admin-configured delivery rates, same structure.

-- Peak / night windows:
INSERT INTO fares.peak_windows(kind,start_local,end_local,multiplier_pct) VALUES
  ('peak','07:00','09:00',20),('peak','17:00','19:00',20),('night','22:00','05:00',15);

-- Platform ledger accounts:
INSERT INTO billing.accounts(owner_type, currency, balance_minor) VALUES ('platform','LKR',0),('suspense','LKR',0);

-- Directional config defaults (single row id=1):
INSERT INTO dispatch.directional_config(id) VALUES (1) ON CONFLICT (id) DO NOTHING;

-- Content templates (Si/Ta/En) sample:
INSERT INTO content.notification_templates(template_key,language,body) VALUES
  ('ride_offer','en','New ride request: {{pickup}} → {{dropoff}}'),
  ('ride_offer','si','නව ගමන් ඉල්ලීමක්: {{pickup}} → {{dropoff}}'),
  ('ride_offer','ta','புதிய பயண கோரிக்கை: {{pickup}} → {{dropoff}}');
```

> Mode B monthly subscription is seeded per vehicle at registration (`FREE` first month).
> City centroid default = Colombo (6.9271, 79.8612).

---

## 21. Query Patterns, Partitioning & Read Scaling

**Hot-path queries** (see ADD §9.4 for the Redis caches that front these):

| Query | Tables / index | Cadence |
|---|---|---|
| Verify session / refresh | `iam.sessions(jti)`, `ux_sessions_active_app` | Very high |
| Dispatch candidate build | `dispatch.driver_presence` GiST + `reputation` gRPC | Very high |
| Atomic accept | `rides.rides(id, state, version, offer_expires_at)` | High |
| Daily-fee idempotent charge | `billing.daily_fee_charges` PK | Medium |
| Ledger posting | `billing.journal_postings(entry_id, account_id)` | Medium |
| Tracker IMEI resolve | `prov.tracker_bindings` partial UQ (+Redis) | Very high (per connect) |
| Telemetry write | `telemetry.positions` (COPY batch ~40k rows/s) | Very high |
| Trip / ride history | `rides.rides(passenger_id, created_at)`, `trips.sessions` | Medium |

**Partitioning & retention (§9.2):**
- `telemetry.positions` — TimescaleDB 1-day chunks × 16 hash partitions; compress @7d, retain raw 30d.
- `trips.position_samples` — monthly range partitions; 12 months hot, then cold archive (MinIO/Wasabi).
- `audit.events`, `rides.outbox` — monthly range-partition candidates as volume grows.
- High-frequency raw GPS never lands in Postgres operational tables — only 1/min sampled + trip summary.

**Read scaling (§9.3):** primary + 2 streaming read replicas; `query-svc` reads replicas with
read-after-write only where required; PgBouncer (transaction mode) in front of every service.

---

## 22. Coverage Note

All **18** ADD §9 bounded-context schemas are present with full DDL: `iam, registry, prov, trips, rides,
dispatch, reputation, safety, fares, billing, comms, docs, support, content, audit, pdpa, spatial,
telemetry`. This document is the consolidation of `D4_mageride_data_model.md` (canonical) with the
additional read-model / lookup tables enumerated in ADD §9.1 prose (`iam.roles`, `billing.wallets`,
`billing.wallet_transactions`, `billing.credit_transfers`, `trips.position_samples`) made explicit
here. Where D4 and the ADD prose differed only in shape (not intent), D4's typed DDL is authoritative.

---

## 23. Δ Change Set 2026-06-28 (ADD v2.9 §1.11 AL-36…AL-43 · URD v2.5 Epic 24)

> Server-schema deltas for the 2026-06-28 review. The admin **passenger/driver/vehicle directories** (items 9–11) and the **verification split** (item 8) are **read-models / joins over existing schemas** — no new entity tables. The additions below are the analytics rollup, two audit-action values, document-capture provenance, and the call-type log. Canonical mirror of `D4 §Δ Addendum 2026-06-28`.

```sql
-- New schema: analytics rollup feeding GET /admin/dashboard/stats (item 7, AL-38)
CREATE SCHEMA IF NOT EXISTS analytics;
CREATE TABLE analytics.daily_metrics (                       -- one row per metric-day, Asia/Colombo
  metric_date DATE PRIMARY KEY,
  completed_trips         INT    NOT NULL DEFAULT 0,
  gross_fare_minor        BIGINT NOT NULL DEFAULT 0,         -- integer minor units (Rs×100)
  new_riders              INT    NOT NULL DEFAULT 0,
  new_drivers             INT    NOT NULL DEFAULT 0,
  daily_fee_revenue_minor BIGINT NOT NULL DEFAULT 0,
  refreshed_at TIMESTAMPTZ NOT NULL DEFAULT now());
-- period/custom-range queries aggregate metric_date; live cards (online drivers, pending
-- verifications, open tickets) are read real-time from their services, not from this table.

-- docs.uploads: record capture provenance of onboarding images (item 6, AL-43)
ALTER TABLE docs.uploads
  ADD COLUMN captured_via TEXT CHECK (captured_via IN ('camera_dragcrop','gallery','other'));

-- comms.call_log: passenger call-type chooser — free in-app VoIP vs masked PSTN (item 4, AL-36)
CREATE TABLE comms.call_log (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  ride_id UUID REFERENCES rides.rides(id),
  caller_id UUID NOT NULL REFERENCES iam.users(id),
  callee_role TEXT NOT NULL CHECK (callee_role IN ('driver','passenger','sender','recipient')),
  call_type TEXT NOT NULL CHECK (call_type IN ('free_voip','normal_masked')),
  started_at TIMESTAMPTZ NOT NULL DEFAULT now(), ended_at TIMESTAMPTZ, outcome TEXT);
```

**Enum / value & behaviour notes (2026-06-28):**
- `audit.events.action` adds **`DOC_VIEW`** (full-size document opened in Admin Portal, SCR-AP-003b) and **`PII_READ`** (passenger/driver directory detail opened) — supports read-access auditing for items 8–11 (AL-39/40/41/42). Append-only, retention unchanged.
- **Item 2 (AL-36):** no DDL change — `dispatch.scheduled_rides.dropoff_geo` is already `NOT NULL`; "select the location to go" is now enforced at API/UI (`POST /v1/rides/schedule` rejects a missing destination).
- **Item 5 (AL-37):** the admin **MFA/TOTP enrolment is removed from the login flow**; any `iam.user_mfa`-style rows are deprecated/unused (no second factor for internal roles), replaced by failed-attempt lock-out + optional IP allow-list.

## 24. Δ Change Set 2026-07-05 (ADD v3.0 §1.12 AL-44…AL-46 · URD v2.6 Epic 25)

> Server-schema deltas for the Passenger Web subview contract pass. The six `SCR-WT` pages are **read-models over existing ride/package state** — no new entity tables; the deltas extend the share-token model to the two new web scopes, add public-surface access metering, and let web-originated SOS/masked calls log against a token instead of an `iam.users` row. Canonical mirror of `D4 §Δ Addendum 2026-07-05`.

```sql
-- Items 1–3, 7 (AL-44/45): extend safety.trip_share_tokens to the two new web scopes.
ALTER TABLE safety.trip_share_tokens
  DROP CONSTRAINT IF EXISTS trip_share_tokens_scope_check,
  ADD CONSTRAINT trip_share_tokens_scope_check
    CHECK (scope IN ('trip_view','package_recipient','proxy_rider','pickup_confirm')),
  ALTER COLUMN trip_id DROP NOT NULL,                        -- pickup_confirm exists pre-ride
  ADD COLUMN location_request_id UUID REFERENCES rides.location_requests(id),
  ADD COLUMN last_access_at TIMESTAMPTZ,                     -- public-surface metering
  ADD COLUMN access_count INT NOT NULL DEFAULT 0,
  ADD CONSTRAINT trip_share_tokens_subject_check
    CHECK ( (scope = 'pickup_confirm' AND location_request_id IS NOT NULL)
         OR (scope <> 'pickup_confirm' AND trip_id IS NOT NULL) );

-- Item 5 (AL-44/US-25.5): web SOS carries no app identity — record the channel.
ALTER TABLE safety.sos_events
  ADD COLUMN source TEXT NOT NULL DEFAULT 'app' CHECK (source IN ('app','web')),
  ALTER COLUMN user_id DROP NOT NULL,                        -- web guest: token, not user
  ADD COLUMN share_token TEXT REFERENCES safety.trip_share_tokens(token),
  ADD CONSTRAINT sos_events_actor_check CHECK (user_id IS NOT NULL OR share_token IS NOT NULL);

-- Item 4 (AL-44/US-25.4): web-originated masked calls log against the token, not a user.
ALTER TABLE comms.call_log
  ALTER COLUMN caller_id DROP NOT NULL,
  ADD COLUMN share_token TEXT REFERENCES safety.trip_share_tokens(token),
  ADD CONSTRAINT call_log_actor_check CHECK (caller_id IS NOT NULL OR share_token IS NOT NULL);
ALTER TABLE comms.call_log DROP CONSTRAINT IF EXISTS call_log_call_type_check;
ALTER TABLE comms.call_log ADD CONSTRAINT call_log_call_type_check
  CHECK (call_type IN ('free_voip','normal_masked','web_masked'));
```

**No-DDL notes (2026-07-05):** `/public/track/*` snapshot, live feed and receipt read from `rides.rides`, `telemetry.positions`, `rides.proof_artifacts`, `fares.ride_payments`; the delivered-page outcome (`otp_verified`/`photo_proof`/`cod_collected`/`disputed`) is derived (P-08/P-10/P-14). `pickup_confirm` tokens burn on confirm/decline/expiry (`revoked_at`); `package_recipient` TTL = delivery + 1 h; `proxy_rider` TTL = trip completion.

## 25. Δ Change Set 2026-07-05 #2 (ADD v3.1 §1.13 AL-47…AL-48 · URD v2.7 Epic 26)

> Driver-QR attestation settlement + number-masking removal. Canonical mirror of `D4 §Δ Addendum 2026-07-05 #2`.

```sql
-- AL-47: attestation terminal states for driver-QR payments (bank-to-bank; no gateway callback).
ALTER TABLE fares.ride_payments DROP CONSTRAINT IF EXISTS ride_payments_state_check;
ALTER TABLE fares.ride_payments ADD CONSTRAINT ride_payments_state_check
  CHECK (state IN ('Initiated','Pending','Succeeded','Failed','Retried','FellBackToCash',
                   'CashOnDelivery','CashOnDeliveryCollected','Overpaid','Refunded','Disputed',
                   'QrClaimedByPassenger','DriverConfirmedQR'));
ALTER TABLE fares.ride_payments
  ADD COLUMN qr_claimed_at   TIMESTAMPTZ,
  ADD COLUMN qr_confirmed_at TIMESTAMPTZ,
  ADD COLUMN qr_claim_artifact_id UUID REFERENCES rides.proof_artifacts(id);   -- optional receipt screenshot
-- rides.proof_artifacts.kind gains 'qr_receipt'. Earning posts on DriverConfirmedQR as on CashSettled (R-05).

-- AL-48: masking removed — call log is a best-effort client tap log; web /call endpoint gone.
ALTER TABLE comms.call_log DROP CONSTRAINT IF EXISTS call_log_actor_check;
ALTER TABLE comms.call_log DROP COLUMN IF EXISTS share_token;
ALTER TABLE comms.call_log DROP CONSTRAINT IF EXISTS call_log_call_type_check;
ALTER TABLE comms.call_log ADD CONSTRAINT call_log_call_type_check
  CHECK (call_type IN ('free_voip','direct_dial'));
-- comms.voip_sessions: masked-SMS-relay flag dropped (D-25 removed).
-- safety.sos_events.share_token (web SOS) is KEPT — only the call-side token goes.
-- Phone visibility is an API rule, not DDL: post-accept ride payloads expose the counterparty MSISDN
-- (P-05: proxy driver sees rider, never booker). Admin-portal RBAC PII masking is unrelated and unchanged.
```

## 26. Δ Change Set 2026-07-18 (ADD v3.2 §1.14 AL-49…AL-51 · URD v2.8 Epic 27)

> Fleet Portal bank & payout profile (SCR-FP-002a) + SCR-FP-004 named vehicle-document slots. Canonical mirror of `D4 §Δ Addendum 2026-07-18`. Mobile SQLite unaffected (web-only surface; passenger pay sheet reads `payTo` live).

```sql
-- AL-49: org bank & payout profile — receives Mode B pass-through payments (BR-23.10).
CREATE TABLE registry.fleet_payout_profiles (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  fleet_id UUID NOT NULL REFERENCES registry.fleets(id) ON DELETE CASCADE,
  bank TEXT NOT NULL, branch TEXT NOT NULL,
  account_no TEXT NOT NULL, account_holder_name TEXT NOT NULL,
  proof_upload_id   UUID REFERENCES docs.uploads(id),   -- bank_statement | passbook_first_page
  lankaqr_upload_id UUID REFERENCES docs.uploads(id),   -- bank-app-generated LankaQR image
  status TEXT NOT NULL DEFAULT 'pending_verification'
    CHECK (status IN ('pending_verification','verified','rejected')),
  rejection_reason TEXT,
  verified_by UUID REFERENCES iam.users(id), verified_at TIMESTAMPTZ,
  created_at TIMESTAMPTZ NOT NULL DEFAULT now(), updated_at TIMESTAMPTZ NOT NULL DEFAULT now());
CREATE UNIQUE INDEX ux_payout_profile_verified ON registry.fleet_payout_profiles(fleet_id)
  WHERE status = 'verified';                             -- at most one live verified profile per org
-- Edits INSERT a new row + re-verify (versioned); payTo always reads the latest 'verified' row (BR-31.1).
-- docs.uploads.kind gains: bank_statement, passbook_first_page, lankaqr_code.

-- AL-50: fleet-uploaded vehicle documents (SCR-FP-004 named slots).
ALTER TABLE registry.documents ALTER COLUMN driver_id DROP NOT NULL;
ALTER TABLE registry.documents ADD COLUMN fleet_id UUID REFERENCES registry.fleets(id) ON DELETE CASCADE;
ALTER TABLE registry.documents ADD CONSTRAINT ck_documents_owner
  CHECK (driver_id IS NOT NULL OR fleet_id IS NOT NULL);
-- kind values already cover the slots: 'registration' (CR copy), 'insurance', 'revenue_license', 'permit' (route permit).
-- Approval gate (extends AL-10): verified registration+insurance+revenue_license for ALL modes,
--   + verified 'permit' for Mode A, before registry.vehicles.status→'APPROVED'; expiry auto-suspends dispatch (E-03).

-- AL-51: "Service payment" (Free/Paid) is a UI/docs RENAME ONLY — registry.vehicles.mode_b_billing unchanged.
```

## 27. Δ Change Set 2026-07-22 #2 (ADD v3.4 §1.16 AL-54…AL-55 · URD v2.9 Epic 28)

> GTFS Dataset Manager (SCR-AP-016). Existing `transit.gtfs_*` tables unchanged; they gain a versioned import lifecycle — importer loads `transit_staging.gtfs_*` (identical DDL), activation swaps staging↔live in one transaction (`NOTIFY transit_feed_activated` → transit-svc cache reload). Canonical mirror of `D4 §Δ Addendum 2026-07-22 #2`. Mobile SQLite unaffected (admin-web-only surface).

```sql
-- AL-54: versioned full-feed GTFS imports (SCR-AP-016)
CREATE TABLE transit.gtfs_feed_versions (
  feed_version_id   UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  file_name         TEXT        NOT NULL,
  file_size_bytes   BIGINT      NOT NULL,
  sha256            TEXT        NOT NULL UNIQUE,
  feed_info_version TEXT,
  service_start     DATE,
  service_end       DATE,
  counts            JSONB       NOT NULL DEFAULT '{}'::jsonb,
  status            TEXT        NOT NULL DEFAULT 'uploaded'
                    CHECK (status IN ('uploaded','validating','validated','failed','active','archived')),
  validation_report JSONB,
  storage_key       TEXT        NOT NULL,
  uploaded_by       UUID        NOT NULL REFERENCES iam.users(user_id),
  uploaded_at       TIMESTAMPTZ NOT NULL DEFAULT now(),
  activated_at      TIMESTAMPTZ,
  archived_at       TIMESTAMPTZ
);
CREATE UNIQUE INDEX ux_gtfs_feed_one_active ON transit.gtfs_feed_versions ((TRUE)) WHERE status = 'active';
CREATE SCHEMA IF NOT EXISTS transit_staging;   -- importer target; swapped into transit.* on activate
```

*End of server_db_schema.md — PostgreSQL 16 + PostGIS + TimescaleDB.*
