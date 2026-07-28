-- =====================================================================================
-- 0312 — registry: the E-03 document-expiry notice ledger
-- Source: ADD §1 E-03 · ADD §6 registry-svc · AL-10 · specs/lightweight-production-replica.md §
--         "Document expiry tracker" · D5' §3.2 (doc-expired DISPATCH_SUSPENDED)
--
-- ⚠ Spec gap — micro-change-set, raised in the C029 handoff.
--
--   E-03 says the nightly job "emits `document.expiring` (T−30d/T−07d/T−1d) and
--   `document.expired`". A job that runs every night and decides what to send from
--   `registry.documents(expires_at, status)` alone **cannot do that**: `status` carries three
--   values (VALID · EXPIRING · EXPIRED) and the requirement has four distinct notices, so from
--   the second night onward every document inside 30 days is either notified again or never
--   again. Both are wrong — the first spams a driver nightly for a month, the second means the
--   T−7 and T−1 reminders never arrive.
--
--   What is missing is per-(document, threshold) state. This table is that state and nothing
--   else: one row per notice actually emitted, written in the same transaction as the outbox
--   row, so a crash between the two cannot suppress a notice that was never sent.
--
--   **D4' §2 should carry this table**, or E-03 should say which single notice per document it
--   wants.
--
-- Nothing here changes an existing object's shape; 0305 already carries `expires_at` and the
-- partial index the sweep scans.
-- =====================================================================================

CREATE TABLE IF NOT EXISTS registry.document_notices (
  document_id UUID NOT NULL REFERENCES registry.documents(id) ON DELETE CASCADE,
  -- Days of notice remaining when the notice was emitted: 30, 7 and 1 are E-03's three
  -- `document.expiring` reminders; 0 is the `document.expired` notice, which is also the one
  -- that flips registry.vehicles.dispatch_state to DISPATCH_SUSPENDED.
  threshold_days SMALLINT NOT NULL
    CONSTRAINT ck_document_notices_threshold CHECK (threshold_days IN (30, 7, 1, 0)),
  notified_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  PRIMARY KEY (document_id, threshold_days));

COMMENT ON TABLE registry.document_notices IS
  'One row per E-03 notice emitted for a document. The primary key is what makes the nightly job idempotent: a sweep that runs twice, or a job that resumes after a crash, re-emits nothing.';
COMMENT ON COLUMN registry.document_notices.threshold_days IS
  'E-03: 30 | 7 | 1 = document.expiring reminders; 0 = document.expired. A sweep that finds several thresholds crossed at once emits only the tightest and records the looser ones as moot, so a job that was down for a week does not send three pushes.';

-- The suspension decision and the approval gate both ask "what is the CURRENT insurance /
-- revenue licence for this vehicle" — the newest row of that kind, not every row ever uploaded.
-- Without this the read degenerates to a scan of ix_documents_vehicle plus a sort, and a
-- superseded document that expired would otherwise have to be excluded by the reader anyway.
CREATE INDEX IF NOT EXISTS ix_documents_vehicle_kind_current
  ON registry.documents(vehicle_id, kind, created_at DESC) WHERE vehicle_id IS NOT NULL;

-- The same read for driver-identity documents, which are vehicle-less by AL-27 (0305's comment
-- on registry.documents.vehicle_id).
CREATE INDEX IF NOT EXISTS ix_documents_driver_kind_current
  ON registry.documents(driver_id, kind, created_at DESC) WHERE vehicle_id IS NULL;

COMMENT ON INDEX registry.ix_documents_vehicle_kind_current IS
  'AL-10 approval gate and E-03 suspension read the newest document per (vehicle, kind). A renewal supersedes rather than replaces, so the older row stays EXPIRED and must not suspend a vehicle whose new certificate is on file.';
