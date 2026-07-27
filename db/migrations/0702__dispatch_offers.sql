-- =====================================================================================
-- 0702 — dispatch: per-driver offer log
-- Source: server_db_schema.md §6 · D4' §6 · ADD §9.1 + Appendix B.2 invariant 3
--         · D5' §3.5/§3.6 · R-10, R-13
-- =====================================================================================

CREATE TABLE IF NOT EXISTS dispatch.offers (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  ride_id UUID NOT NULL REFERENCES rides.rides(id) ON DELETE CASCADE,
  driver_id UUID NOT NULL REFERENCES iam.users(id),
  status TEXT NOT NULL DEFAULT 'OFFERED' CONSTRAINT ck_offers_status
    CHECK (status IN ('OFFERED','ACCEPTED','DECLINED','EXPIRED')),
  sent_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  expires_at TIMESTAMPTZ NOT NULL,                            -- sent_at + 15 s (D5' §3.5)
  responded_at TIMESTAMPTZ);

-- R-10 / ADD Appendix B.2 invariant 3: at most one live offer per driver. The Redis Lua
-- reservation `SET lock:driver-offer:{driverId} NX PX 15000` (D5' §3.6) is the fast path;
-- this index is what makes the guarantee survive a Redis failure.
CREATE UNIQUE INDEX IF NOT EXISTS ux_offers_driver_live
  ON dispatch.offers(driver_id) WHERE status IN ('OFFERED','ACCEPTED');
CREATE INDEX IF NOT EXISTS ix_offers_ride ON dispatch.offers(ride_id);

COMMENT ON TABLE dispatch.offers IS
  'Offer log per (ride, driver). A DECLINED or EXPIRED offer releases the driver back to the pool and the ride re-enters Matching (D5'' §7).';
COMMENT ON INDEX dispatch.ux_offers_driver_live IS
  'R-10: a driver holds at most one OFFERED or ACCEPTED offer at a time.';
