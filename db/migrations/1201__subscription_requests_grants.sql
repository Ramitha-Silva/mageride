-- =====================================================================================
-- 1201 — subscription: Mode B access requests and grants
-- Source: D4' Δ 2026-06-21 (Epic 23) · server_db_schema.md §18b · ADD §9.1
--         AL-23, AL-24, AL-25 · US-4.9…4.12
--
-- Everything in the `subscription` schema is PER VEHICLE, not per fleet: a passenger asks
-- to track one specific Mode B vehicle, and the fleet owner accepts or rejects that one
-- request. See build/progress.md planner finding 1 — these tables originated in D4' and
-- were back-filled into server_db_schema.md §18b on 2026-07-26; the two now agree.
--
-- PKs are the §0 UUID convention. D4's original addendum listed BIGSERIAL PKs with BIGINT
-- FKs, which cannot reference the UUID PKs of iam.users / registry.vehicles; §18b records
-- the correction.
-- =====================================================================================

CREATE TABLE IF NOT EXISTS subscription.access_requests (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  vehicle_id UUID NOT NULL REFERENCES registry.vehicles(id) ON DELETE CASCADE,
  passenger_id UUID NOT NULL REFERENCES iam.users(id) ON DELETE CASCADE,
  status TEXT NOT NULL DEFAULT 'pending' CONSTRAINT ck_access_requests_status
    CHECK (status IN ('pending','accepted','rejected')),
  requested_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  decided_at TIMESTAMPTZ,
  decided_by UUID REFERENCES iam.users(id),                   -- the fleet owner / manager
  -- A decided request must say when and by whom; a pending one must claim neither.
  CONSTRAINT ck_access_requests_decision CHECK (
    (status = 'pending' AND decided_at IS NULL AND decided_by IS NULL)
    OR (status <> 'pending' AND decided_at IS NOT NULL)));

-- One OPEN request per (vehicle, passenger) — a rejected passenger may ask again later,
-- which is why this is partial rather than a plain UNIQUE.
CREATE UNIQUE INDEX IF NOT EXISTS ux_access_request_open
  ON subscription.access_requests(vehicle_id, passenger_id) WHERE status = 'pending';
-- The fleet portal request queue (SCR-FP-016) is per vehicle, oldest first.
CREATE INDEX IF NOT EXISTS ix_access_requests_pending
  ON subscription.access_requests(vehicle_id, requested_at) WHERE status = 'pending';

COMMENT ON TABLE subscription.access_requests IS
  'Per-vehicle Mode B tracking-access request queue (US-4.9/4.10, AL-23). Accepting one creates a subscription.grants row.';

CREATE TABLE IF NOT EXISTS subscription.grants (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  vehicle_id UUID NOT NULL REFERENCES registry.vehicles(id) ON DELETE CASCADE,
  passenger_id UUID NOT NULL REFERENCES iam.users(id) ON DELETE CASCADE,
  status TEXT NOT NULL DEFAULT 'active' CONSTRAINT ck_grants_status
    CHECK (status IN ('active','unsubscribed')),
  granted_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  expires_at TIMESTAMPTZ,
  -- Passenger unsubscribe: they lose visibility immediately, but the row stays MUTED in
  -- the fleet portal so the owner can see who left (US-4.12).
  unsubscribed_at TIMESTAMPTZ,
  -- Fleet-owner hard delete. ONLY the owner sets this, and it is what finally frees the
  -- (vehicle, passenger) pair for a new grant.
  deleted_at TIMESTAMPTZ,
  CONSTRAINT ck_grants_unsubscribed_pair
    CHECK ((status = 'unsubscribed') = (unsubscribed_at IS NOT NULL)));

-- Uniqueness is scoped by deleted_at, not by status: an unsubscribed grant still occupies
-- the slot until the owner deletes it, which is exactly the "stays MUTED" rule.
CREATE UNIQUE INDEX IF NOT EXISTS ux_grant_active
  ON subscription.grants(vehicle_id, passenger_id) WHERE deleted_at IS NULL;
-- "Which vehicles may I track?" on every passenger Mode B map open.
CREATE INDEX IF NOT EXISTS ix_grants_passenger
  ON subscription.grants(passenger_id) WHERE deleted_at IS NULL AND status = 'active';

COMMENT ON TABLE subscription.grants IS
  'Mode B tracking-access grant lifecycle (US-4.11/4.12). Passenger unsubscribe sets status + unsubscribed_at; only the fleet owner may set deleted_at, and only that frees the pair for a new grant.';
COMMENT ON INDEX subscription.ux_grant_active IS
  'Partial on deleted_at IS NULL — an unsubscribed grant still holds the (vehicle, passenger) slot until the owner hard-deletes it.';
