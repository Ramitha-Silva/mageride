-- =====================================================================================
-- 0103 — iam: saved addresses and emergency contacts
-- Source: server_db_schema.md §1 · D4' §1 · D4' Δ 2026-06-21 (AL-26) · AL-13, AL-14
-- =====================================================================================

-- Home/Work plus free-text labelled addresses (AL-14, AL-26; US-22.1/22.2).
--
-- The two specs disagree on this table and the shape below is their union:
--   * server_db_schema.md §1 and D4' §2 model Home/Work through `label` ('home'|'work'|custom)
--     and carry an updated_at column.
--   * D4' Δ 2026-06-21 models them as is_home/is_work booleans with partial unique indexes,
--     makes line1 NOT NULL and has no updated_at.
-- Keeping both preserves the "at most one Home, at most one Work" invariant, which only the
-- Δ form can enforce, without dropping the label the other form addresses rows by. C027
-- should settle on one representation; see the C003 handoff note.
CREATE TABLE IF NOT EXISTS iam.saved_addresses (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  user_id UUID NOT NULL REFERENCES iam.users(id) ON DELETE CASCADE,
  label TEXT NOT NULL,                                        -- 'home' | 'work' | free text ("Save Address As")
  line1 TEXT NOT NULL,                                        -- street / building
  line2 TEXT,                                                 -- area / suburb
  line3 TEXT,                                                 -- city / district
  geo GEOGRAPHY(POINT,4326) NOT NULL,                         -- reverse-geocoded OSM pin
  is_home BOOLEAN NOT NULL DEFAULT false,
  is_work BOOLEAN NOT NULL DEFAULT false,
  created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  updated_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  CONSTRAINT ck_saved_addr_home_work CHECK (NOT (is_home AND is_work)));

CREATE INDEX IF NOT EXISTS ix_saved_addr_user ON iam.saved_addresses(user_id);
CREATE UNIQUE INDEX IF NOT EXISTS uq_saved_home ON iam.saved_addresses(user_id) WHERE is_home;
CREATE UNIQUE INDEX IF NOT EXISTS uq_saved_work ON iam.saved_addresses(user_id) WHERE is_work;
CREATE INDEX IF NOT EXISTS ix_saved_addr_geo ON iam.saved_addresses USING GIST (geo);

SELECT public.attach_set_updated_at('iam','saved_addresses');

-- Driver emergency contacts (AL-13). iam.users carries a denormalised primary contact for
-- the SOS fast path (D-33 budgets 5 s); this table is the full editable list.
CREATE TABLE IF NOT EXISTS iam.emergency_contacts (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  user_id UUID NOT NULL REFERENCES iam.users(id) ON DELETE CASCADE,
  name TEXT NOT NULL,
  phone TEXT NOT NULL,
  created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  updated_at TIMESTAMPTZ NOT NULL DEFAULT now());

CREATE INDEX IF NOT EXISTS ix_emergency_user ON iam.emergency_contacts(user_id);
SELECT public.attach_set_updated_at('iam','emergency_contacts');
