-- =====================================================================================
-- 0404 — prov: binding state audit, IMEI sightings and the publisher choice
-- Source: D6' §4.2/§4.3 · ADD §7.7.3 · D3' provisioning-svc route table
--         T-02, T-03, T-08, T-12, US-3.4, US-3.6
--
-- ⚠ Spec gap — micro-change-set, raised in the C030 handoff.
--
--   `prov.tracker_bindings` (0401, from server_db_schema.md §3) carries `state` but nothing that
--   says *when* or *why* it changed. Three requirements need that:
--
--     · T-08 quarantines on "two devices presenting the same IMEI **within 24 h**", so the rule
--       is a time window and needs the timestamps to measure it against.
--     · US-3.4's admin resolution screen has to show an operator what happened, and "the row is
--       QUARANTINED" is not an explanation.
--     · T-12's "revoked ≤ 60 s" is an SLO, and an SLO with no recorded revocation instant cannot
--       be measured after the fact.
--
--   `prov.imei_sightings` is the other half of T-08 and the part that catches a real clone. A
--   cloned device does not call `POST /v1/trackers/bind` — it dials the adapter, which resolves
--   it through `GET /v1/internal/trackers/{imei}/validate`. Two *distinct credential serials*
--   presenting one IMEI inside the window is the discriminator, and nothing recorded it.
--
--   **D4' §3 should carry these columns and this table.**
-- =====================================================================================

-- --- fleet_id points at the fleet the Fleet Portal manages (AL-03) -------------------
-- ⚠ A second micro-change-set, in the same handoff. 0401 took server_db_schema.md §3 at its
--   word and pointed `prov.tracker_bindings.fleet_id` at `registry.operators` — a stub 0306
--   creates *only* to satisfy this one foreign key ("Legacy fleet-org stub … see the C003
--   handoff note"). The `{fleetId}` in D3''s `POST /v1/fleets/{fleetId}/trackers/bulk` is a
--   `registry.fleets` id (AL-03: the organisation a fleet owner signs in to, whose roster is
--   `registry.fleet_vehicles`), and T-11 scopes tracker positions "via `fleet_id` row-level
--   security". Two different id spaces for one column means bulk onboarding writes a fleet id
--   the RLS predicate cannot match, and the scoping silently returns nothing.
--
--   **server_db_schema.md §3 should reference `registry.fleets`.** `registry.operators` is left
--   in place — it is a released table — but this leaves it unreferenced.
ALTER TABLE prov.tracker_bindings
  DROP CONSTRAINT IF EXISTS tracker_bindings_fleet_id_fkey;

ALTER TABLE prov.tracker_bindings
  DROP CONSTRAINT IF EXISTS fk_tracker_bindings_fleet;

ALTER TABLE prov.tracker_bindings
  ADD CONSTRAINT fk_tracker_bindings_fleet
    FOREIGN KEY (fleet_id) REFERENCES registry.fleets(id) ON DELETE SET NULL;

CREATE INDEX IF NOT EXISTS ix_tracker_fleet ON prov.tracker_bindings(fleet_id) WHERE fleet_id IS NOT NULL;

-- --- Binding state audit (T-08, T-12, US-3.4) ----------------------------------------
ALTER TABLE prov.tracker_bindings
  ADD COLUMN IF NOT EXISTS state_changed_at TIMESTAMPTZ NOT NULL DEFAULT now();

ALTER TABLE prov.tracker_bindings
  ADD COLUMN IF NOT EXISTS state_reason TEXT;

COMMENT ON COLUMN prov.tracker_bindings.state_changed_at IS
  'When `state` last moved. T-12 measures the revocation SLO from here; T-08 measures the 24 h anti-clone window from here and from `created_at`.';
COMMENT ON COLUMN prov.tracker_bindings.state_reason IS
  'Why it moved — imei-duplicate | unbound | decommissioned | admin-resolved. Shown on the US-3.4 quarantine queue.';

-- 0401's `source` column is US-3.6's single-publisher choice and 0401 left it free text. The
-- switch-source endpoint takes `mobile | hardware` and nothing else, so a third value could only
-- arrive from a bug — and the dispatch and tracking planes read this column to decide which
-- stream is authoritative (T-11), where an unrecognised value is silently "neither".
ALTER TABLE prov.tracker_bindings
  DROP CONSTRAINT IF EXISTS ck_tracker_bindings_source;

ALTER TABLE prov.tracker_bindings
  ADD CONSTRAINT ck_tracker_bindings_source CHECK (source IS NULL OR source IN ('mobile', 'hardware'));

-- The rotation sweep (T-02) claims due rows with FOR UPDATE SKIP LOCKED; 0401's
-- ix_tracker_rotation already covers `rotates_at WHERE state = 'ACTIVE'`. What it does not cover
-- is the quarantine queue, which is read by admin id order rather than by IMEI.
CREATE INDEX IF NOT EXISTS ix_tracker_quarantined
  ON prov.tracker_bindings(state_changed_at) WHERE state = 'QUARANTINED';

-- --- IMEI sightings (T-08) -----------------------------------------------------------
-- One row per presentation of an IMEI: a bind, or an adapter/broker connect resolved through
-- `GET /v1/internal/trackers/{imei}/validate`. Deliberately NOT keyed to a binding — the whole
-- point is to catch a device presenting an IMEI that belongs to a binding it does not hold.
CREATE TABLE IF NOT EXISTS prov.imei_sightings (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  imei TEXT NOT NULL,
  -- The credential the device presented: an x509 serial or a PSK token serial. NULL when the
  -- presenter could not be identified (an adapter that does not pass one, or a bind request,
  -- which is identified by its actor instead).
  credential_serial TEXT,
  source TEXT NOT NULL CHECK (source IN ('bind', 'validate')),
  actor_id UUID,                                              -- the authenticated binder, when there is one
  remote_addr INET,                                           -- where the presentation came from
  seen_at TIMESTAMPTZ NOT NULL DEFAULT now());

-- The T-08 read: "distinct credential serials for this IMEI since now() - 24 h".
CREATE INDEX IF NOT EXISTS ix_imei_sightings_window ON prov.imei_sightings(imei, seen_at DESC);

COMMENT ON TABLE prov.imei_sightings IS
  'T-08 anti-clone evidence: every presentation of an IMEI, so "two devices within 24 h" is answerable. Retained by the same window the rule uses; provisioning-svc prunes rows older than the window on each sweep.';

-- --- Certificate revocation list support (T-12) --------------------------------------
-- `prov.device_certs.revoked_at` (0401) says a credential is revoked but not why, and a CRL
-- entry carries a reason code. The values are RFC 5280 §5.3.1's, spelled out.
ALTER TABLE prov.device_certs
  ADD COLUMN IF NOT EXISTS revocation_reason TEXT;

ALTER TABLE prov.device_certs
  DROP CONSTRAINT IF EXISTS ck_device_certs_revocation_reason;

ALTER TABLE prov.device_certs
  ADD CONSTRAINT ck_device_certs_revocation_reason CHECK (
    revocation_reason IS NULL
    OR revocation_reason IN ('unspecified', 'key_compromise', 'affiliation_changed', 'superseded',
                             'cessation_of_operation', 'certificate_hold'));

COMMENT ON COLUMN prov.device_certs.revocation_reason IS
  'RFC 5280 §5.3.1 CRL reason code. `superseded` is what a 90-day rotation writes; `cessation_of_operation` is a decommission (US-3.8); `certificate_hold` is a T-08 quarantine — the one reason RFC 5280 lets a CA lift, which is exactly what US-3.4''s admin resolution does.';

-- The CRL is rebuilt from every revoked, unexpired certificate on each publish, so the read is
-- "revoked and not yet past expiry" rather than a per-binding lookup.
CREATE INDEX IF NOT EXISTS ix_device_certs_revoked
  ON prov.device_certs(revoked_at) WHERE revoked_at IS NOT NULL;
