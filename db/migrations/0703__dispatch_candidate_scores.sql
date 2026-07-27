-- =====================================================================================
-- 0703 — dispatch: versioned candidate-scoring audit
-- Source: server_db_schema.md §6 · D4' §6 · ADD §9.1 · D5' §3.3/§12.1 · R-11, P-11, DT-02
--
-- Immutable. Supports post-hoc "why did this driver get the ride" audits and ML training;
-- dispatch_algorithm_version is what makes a historical decision reproducible when the
-- weighting changes (R-11).
-- =====================================================================================

CREATE TABLE IF NOT EXISTS dispatch.candidate_scores (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  ride_id UUID NOT NULL REFERENCES rides.rides(id) ON DELETE CASCADE,
  driver_id UUID NOT NULL REFERENCES iam.users(id),
  score NUMERIC NOT NULL,
  package_size_compatible BOOLEAN,                            -- P-11; NULL for non-package rides
  -- breakdown.directional carries the DT-02 bearings and distances that decided whether a
  -- driver with an active Destination Filter stayed in the round (D5' §12.1).
  breakdown JSONB NOT NULL,
  dispatch_algorithm_version SMALLINT NOT NULL,
  evaluated_at TIMESTAMPTZ NOT NULL DEFAULT now());

CREATE INDEX IF NOT EXISTS ix_candidate_ride ON dispatch.candidate_scores(ride_id);

COMMENT ON TABLE dispatch.candidate_scores IS
  'Immutable scoring audit per (ride, driver) evaluation (R-11). Written for every scored candidate, not only the winner.';
