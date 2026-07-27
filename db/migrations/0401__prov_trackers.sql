-- =====================================================================================
-- 0401 — prov: hardware tracker bindings and device credentials
-- Source: server_db_schema.md §3 · D4' §3 · T-02, T-03, T-08
-- =====================================================================================

-- IMEI ↔ vehicle source of truth (T-03). tcp-adapter and mqtt-bridge resolve an inbound
-- IMEI to a vehicle through this table (cached in Redis imei:{imei}).
CREATE TABLE IF NOT EXISTS prov.tracker_bindings (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  imei TEXT NOT NULL,                                         -- 15-digit IMEI
  vehicle_id UUID NOT NULL REFERENCES registry.vehicles(id) ON DELETE CASCADE,
  fleet_id UUID REFERENCES registry.operators(id),            -- fleet scope (RLS)
  credential_serial TEXT NOT NULL,
  credential_type TEXT NOT NULL CHECK (credential_type IN ('x509','psk')),
  state TEXT NOT NULL DEFAULT 'ACTIVE'
    CHECK (state IN ('ACTIVE','QUARANTINED','REVOKED')),      -- anti-clone (T-08)
  rotates_at TIMESTAMPTZ NOT NULL,                            -- 90-day credential rotation (T-02)
  source TEXT,
  last_seen_at TIMESTAMPTZ,
  signal_strength SMALLINT,
  battery_mv INTEGER,
  sat_count SMALLINT,
  created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  updated_at TIMESTAMPTZ NOT NULL DEFAULT now());

-- T-08 anti-clone: one ACTIVE binding per IMEI. A second device presenting the same IMEI
-- cannot bind; both records are quarantined by provisioning-svc (US-3.4).
CREATE UNIQUE INDEX IF NOT EXISTS ux_tracker_imei_active
  ON prov.tracker_bindings(imei) WHERE state = 'ACTIVE';
CREATE INDEX IF NOT EXISTS ix_tracker_vehicle ON prov.tracker_bindings(vehicle_id);
CREATE INDEX IF NOT EXISTS ix_tracker_rotation
  ON prov.tracker_bindings(rotates_at) WHERE state = 'ACTIVE';   -- T-02 rotation sweep

SELECT public.attach_set_updated_at('prov','tracker_bindings');

COMMENT ON INDEX prov.ux_tracker_imei_active IS
  'T-08: at most one ACTIVE binding per IMEI.';

-- Credential lifecycle (T-02): step-ca issued x509 or PSK, rotated every 90 days.
CREATE TABLE IF NOT EXISTS prov.device_certs (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  binding_id UUID NOT NULL REFERENCES prov.tracker_bindings(id) ON DELETE CASCADE,
  serial TEXT NOT NULL UNIQUE,
  kind TEXT NOT NULL CHECK (kind IN ('x509','psk')),
  pem_or_token_hash BYTEA NOT NULL,                           -- never the credential itself
  issued_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  expires_at TIMESTAMPTZ NOT NULL,
  revoked_at TIMESTAMPTZ);

CREATE INDEX IF NOT EXISTS ix_device_certs_binding ON prov.device_certs(binding_id);
CREATE INDEX IF NOT EXISTS ix_device_certs_expiry
  ON prov.device_certs(expires_at) WHERE revoked_at IS NULL;
