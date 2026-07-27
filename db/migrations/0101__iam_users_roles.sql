-- =====================================================================================
-- 0101 — iam: users, role catalog, multi-role grants
-- Source: server_db_schema.md §1 · D4' §1 · AL-06, AL-07, AL-13, AL-14
--
-- Nine canonical roles (AL-06). iam.users.role is the PRIMARY role; effective permissions
-- are the union of iam.user_roles, evaluated deny-by-default.
-- =====================================================================================

CREATE TABLE IF NOT EXISTS iam.users (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  phone TEXT UNIQUE,                                          -- +94 E.164; NULL for web-only internal accounts
  email TEXT UNIQUE,                                          -- Fleet/Admin Portal sign-in (AL-07)
  role TEXT NOT NULL DEFAULT 'passenger' CHECK (role IN
    ('passenger','driver','fleet_owner','admin','super_admin',
     'verification_officer','support_csr','finance_officer','auditor')),
  first_name TEXT,
  photo_url TEXT,
  language TEXT NOT NULL DEFAULT 'en' CHECK (language IN ('si','ta','en')),
  notif_prefs JSONB NOT NULL DEFAULT '{}',                    -- per-type prefs (US-10.7)
  default_payment_method TEXT NOT NULL DEFAULT 'cash'
    CHECK (default_payment_method IN ('cash','lankaqr','onepay')),   -- passenger default (AL-14, US-22.4)
  emergency_contact_name TEXT,                                -- driver SOS quick-fill (AL-13)
  emergency_contact_phone TEXT,
  is_blocked BOOLEAN NOT NULL DEFAULT false,
  created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  updated_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  CONSTRAINT ck_users_credential CHECK (phone IS NOT NULL OR email IS NOT NULL));

-- operating_city_code is added by 0201, once config.operating_cities exists to reference.

SELECT public.attach_set_updated_at('iam','users');

COMMENT ON TABLE iam.users IS
  'End-user and internal-role accounts. role is the primary role (AL-06); the union of iam.user_roles is authoritative for permissions.';
COMMENT ON COLUMN iam.users.language IS
  'UI language si|ta|en. The onboarding picker lists Sinhala first (AL-26) but the stored default is en.';

-- Admin-readable role catalog. RBAC enforcement uses the CHECK sets, not this table.
CREATE TABLE IF NOT EXISTS iam.roles (
  role TEXT PRIMARY KEY CHECK (role IN
    ('passenger','driver','fleet_owner','admin','super_admin',
     'verification_officer','support_csr','finance_officer','auditor')),
  label TEXT NOT NULL,
  is_internal BOOLEAN NOT NULL DEFAULT false);                -- roles 4-9 are super_admin-provisioned

-- Seed from server_db_schema.md §20.
INSERT INTO iam.roles(role, label, is_internal) VALUES
  ('passenger','Passenger',false),
  ('driver','Driver',false),
  ('fleet_owner','Fleet Owner',false),
  ('admin','Administrator',true),
  ('super_admin','Super Administrator',true),
  ('verification_officer','Verification Officer',true),
  ('support_csr','Support CSR',true),
  ('finance_officer','Finance Officer',true),
  ('auditor','Auditor',true)
ON CONFLICT (role) DO NOTHING;

-- Multi-role union, deny-by-default (AL-06).
CREATE TABLE IF NOT EXISTS iam.user_roles (
  user_id UUID NOT NULL REFERENCES iam.users(id) ON DELETE CASCADE,
  role TEXT NOT NULL CHECK (role IN
    ('passenger','driver','fleet_owner','admin','super_admin',
     'verification_officer','support_csr','finance_officer','auditor')),
  granted_by UUID REFERENCES iam.users(id),                   -- internal roles: super_admin only
  granted_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  PRIMARY KEY (user_id, role));

CREATE INDEX IF NOT EXISTS ix_user_roles_role ON iam.user_roles(role);
