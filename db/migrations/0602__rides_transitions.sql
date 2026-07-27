-- =====================================================================================
-- 0602 — rides: immutable state-change audit
-- Source: server_db_schema.md §5 · D4' §5 · ADD Appendix B.2 invariant 4 · R-01
-- =====================================================================================

-- Append-only. Every rides.rides state move writes exactly one row here and one rides.outbox
-- row, in the same transaction as the UPDATE (ADD Appendix B.2 invariant 4).
CREATE TABLE IF NOT EXISTS rides.transitions (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  ride_id UUID NOT NULL REFERENCES rides.rides(id) ON DELETE CASCADE,
  from_state TEXT,                                            -- NULL on the initial Requested row
  to_state TEXT NOT NULL,
  reason_code TEXT,                                           -- D5' §7 cancellation matrix
  actor_type TEXT NOT NULL,                                   -- rider | driver | system | admin
  actor_id UUID,                                              -- NULL for system transitions
  ts TIMESTAMPTZ NOT NULL DEFAULT now());

CREATE INDEX IF NOT EXISTS ix_transitions_ride ON rides.transitions(ride_id, ts);

COMMENT ON TABLE rides.transitions IS
  'Immutable per-ride state-change audit. No updated_at and no UPDATE path by design — a correction is a new row, never an edit.';
COMMENT ON COLUMN rides.transitions.to_state IS
  'Deliberately unconstrained: rides.rides.state already restricts the reachable set, and the audit must be able to record a historical state name that a later migration removes.';
