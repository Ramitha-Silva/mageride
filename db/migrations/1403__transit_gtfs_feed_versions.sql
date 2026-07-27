-- =====================================================================================
-- 1403 — transit: GTFS feed version lifecycle
-- Source: server_db_schema.md §27 (Δ 2026-07-22 #2) · D4' Δ 2026-07-22 #2
--         ADD §1.16 · AL-54 · BR-32.2 / BR-32.3 · SCR-AP-016
--
-- The ledger for the GTFS Dataset Manager: upload → validate → activate → archive, with
-- exactly one 'active' version at any moment. The feed data itself lives in transit.gtfs_*
-- (1402); this table records where each version came from and what happened to it.
--
-- SPEC GAP (micro-change-set): both sources write
--   uploaded_by UUID NOT NULL REFERENCES iam.users(user_id)
-- but iam.users has no user_id column — its primary key is `id`, in §1 of the same
-- document and everywhere else in both specs. Landed against iam.users(id).
-- =====================================================================================

CREATE TABLE IF NOT EXISTS transit.gtfs_feed_versions (
  feed_version_id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  file_name TEXT NOT NULL,
  file_size_bytes BIGINT NOT NULL CHECK (file_size_bytes >= 0),
  -- Duplicate-upload guard (US-28.1): re-uploading the same zip collides here instead of
  -- creating a second version of identical data.
  sha256 TEXT NOT NULL UNIQUE,
  feed_info_version TEXT,                                     -- feed_info.txt, as supplied
  -- Feed-supplied service calendar bounds, read out of the uploaded file. NOT business
  -- dates in the D-38 sense — nothing computes them in Asia/Colombo — so they carry no
  -- tz_at companion, and migrate-verify.sh exempts this table for exactly that reason.
  service_start DATE,
  service_end DATE,
  -- {agencies, routes, trips, stops, stop_times, shapes, frequencies}
  counts JSONB NOT NULL DEFAULT '{}'::jsonb,
  status TEXT NOT NULL DEFAULT 'uploaded' CONSTRAINT ck_gtfs_feed_versions_status
    CHECK (status IN ('uploaded','validating','validated','failed','active','archived')),
  -- {errors:[{file,row,code,message}], warnings:[...]}
  validation_report JSONB,
  -- The original zip on SSE object storage, retained so a rollback can reimport rather
  -- than reconstruct (BR-32.3).
  storage_key TEXT NOT NULL,
  uploaded_by UUID NOT NULL REFERENCES iam.users(id),
  uploaded_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  activated_at TIMESTAMPTZ,
  archived_at TIMESTAMPTZ,
  CONSTRAINT ck_gtfs_feed_versions_service_span
    CHECK (service_start IS NULL OR service_end IS NULL OR service_end >= service_start),
  -- A version that has never been active cannot have been archived from active, and the
  -- current active one has not been archived yet.
  CONSTRAINT ck_gtfs_feed_versions_activated
    CHECK (status <> 'active' OR (activated_at IS NOT NULL AND archived_at IS NULL)),
  CONSTRAINT ck_gtfs_feed_versions_archived
    CHECK (status <> 'archived' OR archived_at IS NOT NULL));

-- BR-32.2: exactly one active feed. The index expression is a constant, so every 'active'
-- row collides with every other one — the standard single-row guard.
CREATE UNIQUE INDEX IF NOT EXISTS ux_gtfs_feed_one_active
  ON transit.gtfs_feed_versions ((TRUE)) WHERE status = 'active';

-- The SCR-AP-016 version list is newest-first.
CREATE INDEX IF NOT EXISTS ix_gtfs_feed_versions_uploaded
  ON transit.gtfs_feed_versions(uploaded_at DESC);

COMMENT ON TABLE transit.gtfs_feed_versions IS
  'GTFS import lifecycle (AL-54, SCR-AP-016). Never staged and never swapped — transit_staging mirrors only the five gtfs_* data tables.';
COMMENT ON INDEX transit.ux_gtfs_feed_one_active IS
  'BR-32.2: at most one feed version may be active. Indexing the constant TRUE makes the partial index admit a single row.';
COMMENT ON COLUMN transit.gtfs_feed_versions.uploaded_by IS
  'References iam.users(id). Both DDL sources write iam.users(user_id), which does not exist — micro-change-set raised in the C005 handoff.';
