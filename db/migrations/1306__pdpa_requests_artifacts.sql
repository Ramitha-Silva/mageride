-- =====================================================================================
-- 1306 — pdpa: right-to-erasure and data export
-- Source: server_db_schema.md §16 · D4' §11-16 · ADD §9.1 · E-06
--
-- Sri Lanka PDPA: an export or erasure request must be fulfilled within 30 days, which is
-- why due_by is a stored default rather than something a service computes each time.
-- =====================================================================================

CREATE TABLE IF NOT EXISTS pdpa.requests (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  -- No ON DELETE CASCADE: an erasure request must outlive the anonymisation it ordered,
  -- or the platform loses its own proof of compliance.
  user_id UUID NOT NULL REFERENCES iam.users(id),
  kind TEXT NOT NULL CONSTRAINT ck_pdpa_requests_kind
    CHECK (kind IN ('export','erasure')),
  status TEXT NOT NULL DEFAULT 'Received' CONSTRAINT ck_pdpa_requests_status
    CHECK (status IN ('Received','InProgress','FulfilledHold','Fulfilled','Rejected')),
  requested_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  due_by TIMESTAMPTZ NOT NULL DEFAULT now() + INTERVAL '30 days',
  fulfilled_at TIMESTAMPTZ,
  -- FulfilledHold: erased everywhere except the records a statute requires be kept
  -- (financial history, audit log). The reason is the legal basis for that retention.
  hold_reason TEXT,
  CONSTRAINT ck_pdpa_requests_hold
    CHECK (status <> 'FulfilledHold' OR hold_reason IS NOT NULL));

CREATE INDEX IF NOT EXISTS ix_pdpa_requests_user ON pdpa.requests(user_id, requested_at DESC);
-- The 30-day SLA queue: everything still open, soonest deadline first.
CREATE INDEX IF NOT EXISTS ix_pdpa_requests_due
  ON pdpa.requests(due_by) WHERE status IN ('Received','InProgress');

COMMENT ON TABLE pdpa.requests IS
  'PDPA data-export and erasure requests (E-06), 30-day statutory deadline. FulfilledHold records a partial erasure where a statute forces retention.';

CREATE TABLE IF NOT EXISTS pdpa.fulfillment_artifacts (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  request_id UUID NOT NULL REFERENCES pdpa.requests(id) ON DELETE CASCADE,
  kind TEXT NOT NULL CONSTRAINT ck_pdpa_artifacts_kind
    CHECK (kind IN ('export_zip','erasure_log')),
  storage_url TEXT NOT NULL,
  -- The signature over the delivered artifact: sha256 is what makes a later "this is not
  -- what you sent me" answerable.
  sha256 BYTEA,
  signed_at TIMESTAMPTZ);

CREATE INDEX IF NOT EXISTS ix_pdpa_artifacts_request ON pdpa.fulfillment_artifacts(request_id);

COMMENT ON TABLE pdpa.fulfillment_artifacts IS
  'The signed ZIP handed to the data subject, or the log of what an erasure removed (E-06).';
