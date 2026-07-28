-- =====================================================================================
-- 0405 — prov: bulk tracker onboarding jobs (T-09, US-3.2)
-- Source: D3' `POST /v1/fleets/{fleetId}/trackers/bulk` · NFR-43 (5,000 rows ≤ 5 min)
--
-- ⚠ Spec gap — micro-change-set, raised in the C030 handoff.
--
--   D3' specifies the endpoint completely — 202 with `{jobId, totalRows, status, errorReportUrl}`,
--   `429 bulk-in-progress` for a second job, "SAGA validates rows; materialises bindings; queues
--   credential-mint jobs; per-row error report" — and D4' §3 has no table for any of it. A job
--   that only exists in memory cannot answer `GET /v1/fleets/{id}/trackers/bulk/{jobId}` after a
--   restart, which is the poll the Admin Portal drives.
--
--   **D4' §3 should carry both tables.**
--
--   Two tables rather than one JSONB column of rows: the error report is per row, the worker
--   claims rows in batches, and 5,000 rows re-serialised on every row's completion is 5,000
--   rewrites of a 300 KB document inside the NFR-43 budget.
-- =====================================================================================

CREATE TABLE IF NOT EXISTS prov.bulk_jobs (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  -- registry.fleets, not registry.operators — 0404 repoints prov.tracker_bindings.fleet_id at
  -- the same table for the reason recorded there.
  fleet_id UUID NOT NULL REFERENCES registry.fleets(id) ON DELETE CASCADE,
  requested_by UUID NOT NULL REFERENCES iam.users(id),
  status TEXT NOT NULL DEFAULT 'PROCESSING'
    CHECK (status IN ('PROCESSING', 'COMPLETED', 'FAILED')),
  total_rows INTEGER NOT NULL CHECK (total_rows >= 0 AND total_rows <= 5000),   -- D3' ceiling
  succeeded_rows INTEGER NOT NULL DEFAULT 0 CHECK (succeeded_rows >= 0),
  failed_rows INTEGER NOT NULL DEFAULT 0 CHECK (failed_rows >= 0),
  credential_type TEXT NOT NULL CHECK (credential_type IN ('x509', 'psk')),
  created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  updated_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  finished_at TIMESTAMPTZ);

-- D3' `429 bulk-in-progress`: "Only one bulk job per fleet may be in flight". Enforced by the
-- index rather than by a SELECT-then-INSERT, because two Admin Portal tabs submitting at once
-- is exactly the race that check loses.
CREATE UNIQUE INDEX IF NOT EXISTS ux_bulk_jobs_in_flight
  ON prov.bulk_jobs(fleet_id) WHERE status = 'PROCESSING';

CREATE INDEX IF NOT EXISTS ix_bulk_jobs_fleet ON prov.bulk_jobs(fleet_id, created_at DESC);

SELECT public.attach_set_updated_at('prov', 'bulk_jobs');

COMMENT ON TABLE prov.bulk_jobs IS
  'T-09 bulk IMEI onboarding. One PROCESSING job per fleet (ux_bulk_jobs_in_flight) is D3''s 429 bulk-in-progress.';

-- Every parsed row, with the outcome that becomes the per-row error report. Rows are written in
-- the same transaction as the job (the "validates atomically … without partial commits" half);
-- the bindings they name are minted afterwards, one row at a time, by the mint worker.
CREATE TABLE IF NOT EXISTS prov.bulk_job_rows (
  job_id UUID NOT NULL REFERENCES prov.bulk_jobs(id) ON DELETE CASCADE,
  row_number INTEGER NOT NULL,                                -- 1-based, as the CSV numbers it
  imei TEXT NOT NULL,
  registration_number TEXT NOT NULL,
  vehicle_id UUID REFERENCES registry.vehicles(id) ON DELETE SET NULL,   -- resolved at validation
  status TEXT NOT NULL DEFAULT 'PENDING'
    CHECK (status IN ('PENDING', 'BOUND', 'FAILED')),
  error_code TEXT,                                            -- the D3' kebab code, e.g. imei-duplicate
  error_detail TEXT,
  binding_id UUID REFERENCES prov.tracker_bindings(id) ON DELETE SET NULL,
  PRIMARY KEY (job_id, row_number));

-- The mint worker's claim: `WHERE job_id = … AND status = 'PENDING' ORDER BY row_number
-- FOR UPDATE SKIP LOCKED`, so several replicas can drain one job inside the NFR-43 budget.
CREATE INDEX IF NOT EXISTS ix_bulk_job_rows_pending
  ON prov.bulk_job_rows(job_id, row_number) WHERE status = 'PENDING';

COMMENT ON TABLE prov.bulk_job_rows IS
  'One row per CSV line. `error_code` is the same kebab registry the HTTP API uses, so the downloadable report and a single bind fail with the same vocabulary.';
