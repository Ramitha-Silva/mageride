-- =====================================================================================
-- 0805 — reputation: network observations (the E-07 IP/ASN clustering input)
-- Source: ADD §6 `reputation-svc` · ADD §12.6 · D5' §15 · E-07
--
-- ⚠ MICRO-CHANGE-SET, raised in the C033 handoff. E-07 names three detectors and only two of
--   them have an input in the schema as it stands:
--
--     pair frequency        → rides.rides (passenger_id, accepted_driver_id) — exists
--     device-binding hashes → iam.devices.device_key (0105) — exists
--     IP / ASN clustering   → **nothing anywhere records a client IP**
--
--   The only INET column in the whole schema is prov.tracker_bindings.remote_addr (0404), which
--   is a tracker's address and not a user's. So the third detector has no input, and a detector
--   with no input is a gate that always passes — which reads like a gate that works.
--
--   This table is that input. It is reputation-svc's own (the fence for this component is that
--   counters and detection live here and nowhere else) and is written through
--   `POST /v1/internal/reputation/observations` by whoever holds the client address: today the
--   API gateway (C008) and iam-svc (C020) are the two that see it. **No producer exists yet** —
--   the detector is proven against seeded rows, and the intake is here so that landing a
--   producer is a caller change and not a migration. D4' §7 should print this DDL.
--
--   PDPA (E-06): an IP is personal data. Rows carry no other identifier than the user they are
--   already about, and the sweep in reputation-svc deletes anything older than
--   Reputation:NetworkObservationRetention (90 d) — the erasure path deletes by user_id.
-- =====================================================================================

CREATE TABLE IF NOT EXISTS reputation.network_observations (
  id BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
  user_id UUID NOT NULL REFERENCES iam.users(id) ON DELETE CASCADE,
  ride_id UUID,                                               -- no FK: outlives the ride it was seen on
  ip INET NOT NULL,
  -- Autonomous system the address belonged to at observation time, when the caller could
  -- resolve one. Nullable: a lookup that failed must not stop the row being recorded, and the
  -- clustering detector falls back to the /24 (v4) or /48 (v6) prefix.
  asn INTEGER,
  user_agent TEXT,
  observed_at TIMESTAMPTZ NOT NULL DEFAULT now());

-- The clustering detector's query: every user seen on an address inside the window.
CREATE INDEX IF NOT EXISTS ix_network_observations_ip
  ON reputation.network_observations(ip, observed_at DESC);
CREATE INDEX IF NOT EXISTS ix_network_observations_asn
  ON reputation.network_observations(asn, observed_at DESC) WHERE asn IS NOT NULL;
-- The retention sweep and the PDPA erasure path.
CREATE INDEX IF NOT EXISTS ix_network_observations_user
  ON reputation.network_observations(user_id, observed_at DESC);

COMMENT ON TABLE reputation.network_observations IS
  'E-07 IP/ASN clustering input. Personal data under PDPA (E-06): retained 90 days, deleted by user_id on erasure. No producer exists yet — see the header note and the C033 handoff.';
