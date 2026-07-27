-- =====================================================================================
-- 0001 — Extensions and bounded-context schemas
-- Source: server_db_schema.md §0.1 · ADD §9.1
--
-- Every schema in the topology is created here, including the ones whose tables land in
-- later components (C004 trips/rides/dispatch, C005 business/content, C006 telemetry).
-- Creating them up front keeps the ordering of the table migrations free of schema
-- bootstrapping and lets a partially-built database still be introspected.
-- =====================================================================================

-- TimescaleDB must be the first statement in its transaction, so it leads the file.
-- On timescale/timescaledb-ha it is already present in template1 and this is a no-op.
CREATE EXTENSION IF NOT EXISTS timescaledb;

CREATE EXTENSION IF NOT EXISTS postgis;
CREATE EXTENSION IF NOT EXISTS pgcrypto;     -- gen_random_uuid(), HMAC
CREATE EXTENSION IF NOT EXISTS citext;

-- The 21 bounded-context schemas of server_db_schema.md §0.1 ------------------------
CREATE SCHEMA IF NOT EXISTS iam;             -- identity, auth, RBAC                     (C003)
CREATE SCHEMA IF NOT EXISTS registry;        -- vehicles, profiles, documents, fleets    (C003)
CREATE SCHEMA IF NOT EXISTS prov;            -- tracker provisioning                     (C003)
CREATE SCHEMA IF NOT EXISTS trips;           -- Mode A/B tracking sessions               (C004)
CREATE SCHEMA IF NOT EXISTS rides;           -- Mode C ride aggregate                    (C004)
CREATE SCHEMA IF NOT EXISTS dispatch;        -- candidate scoring, offers, directional   (C004)
CREATE SCHEMA IF NOT EXISTS reputation;      -- counters, block states                   (C004)
CREATE SCHEMA IF NOT EXISTS safety;          -- SOS, trip share, reports                 (C005)
CREATE SCHEMA IF NOT EXISTS fares;           -- tariffs, payments, earnings              (C005)
CREATE SCHEMA IF NOT EXISTS billing;         -- daily fee, double-entry ledger           (C005)
CREATE SCHEMA IF NOT EXISTS comms;           -- VoIP and notification tokens             (C005)
CREATE SCHEMA IF NOT EXISTS docs;            -- uploads and OCR extractions              (C005)
CREATE SCHEMA IF NOT EXISTS support;         -- tickets                                  (C005)
CREATE SCHEMA IF NOT EXISTS content;         -- localised templates, FAQ, broadcasts     (C005)
CREATE SCHEMA IF NOT EXISTS audit;           -- immutable admin log                      (C005)
CREATE SCHEMA IF NOT EXISTS pdpa;            -- erasure and export requests              (C005)
CREATE SCHEMA IF NOT EXISTS spatial;         -- PostGIS system of record                 (C005)
CREATE SCHEMA IF NOT EXISTS telemetry;       -- tracker hypertable                       (C006)

-- Omitted from the §0.1 CREATE SCHEMA block although §17b/§18b/§18c define tables in
-- them. Created here so C005 can land those tables (C003 fence).
CREATE SCHEMA IF NOT EXISTS config;          -- launch/operating cities   §17b           (C003)
CREATE SCHEMA IF NOT EXISTS subscription;    -- Mode B subscriptions      §18b           (C005)
CREATE SCHEMA IF NOT EXISTS transit;         -- GTFS routing              §18c           (C005)

-- Introduced by their own change sets rather than §0.1, for the same reason.
CREATE SCHEMA IF NOT EXISTS analytics;       -- dashboard rollup          §23, AL-38     (C005)
CREATE SCHEMA IF NOT EXISTS transit_staging; -- GTFS import target        §27, AL-54     (C005)

COMMENT ON SCHEMA iam        IS 'Identity, authentication and RBAC (server_db_schema §1).';
COMMENT ON SCHEMA registry   IS 'Vehicles, driver profiles, documents, fleets and payouts (§2).';
COMMENT ON SCHEMA prov       IS 'Hardware tracker provisioning and credentials (§3, T-02/T-03/T-08).';
COMMENT ON SCHEMA config     IS 'Admin-managed launch/operating cities (§17b, AL-27).';
