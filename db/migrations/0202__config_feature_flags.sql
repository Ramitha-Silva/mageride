-- =====================================================================================
-- 0202 — config: platform feature flags (C062)
-- Source: URD §2.3 row "Platform config — Driver Level params, feature flags, system
--         settings" · US-14.12 · ADD §1.8 AL-06
--
-- **No spec prints DDL for this table.** URD §2.3 gives feature flags a whole matrix row
-- (Super Admin ✅, Admin ◐ subset, Auditor 👁) and US-14.12 makes them an Admin Portal
-- Config surface, but D4' §17b and server_db_schema.md §17b carry only
-- `config.operating_cities`. The other three configuration surfaces the C062 deliverable
-- names already have their tables and their owners — `fares.tariffs` (1001),
-- `billing.plans` (1103) and `dispatch.level_config` (0713) — and this is the fourth,
-- the only one with nowhere to live. Raised as a micro-change-set in the C062 handoff.
--
-- Deliberately not a JSONB blob in a singleton row: a flag has an owner, a reason and a
-- last-changed-by, and a blob has one `updated_at` for all of them. The audit row in
-- `audit.events` says who flipped which flag; these columns say what it is currently set
-- to and why, which is the question the Config screen asks.
-- =====================================================================================

CREATE TABLE IF NOT EXISTS config.feature_flags (
  -- Kebab or snake, matched to the code that reads it. TEXT primary key rather than a
  -- surrogate: the key IS the identity, and every caller looks it up by name.
  key TEXT PRIMARY KEY CONSTRAINT ck_feature_flags_key
    CHECK (key ~ '^[a-z][a-z0-9_.-]{1,80}$'),
  enabled BOOLEAN NOT NULL DEFAULT false,
  -- What flipping it does, in the operator's words. Shown on SCR-AP-007 beside the toggle:
  -- a flag whose name is the only documentation is a flag nobody dares turn off.
  description TEXT,
  -- The admin who last set it. Nullable because a seeded default has no author.
  updated_by UUID REFERENCES iam.users(id),
  updated_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  created_at TIMESTAMPTZ NOT NULL DEFAULT now());

SELECT public.attach_set_updated_at('config','feature_flags');

COMMENT ON TABLE config.feature_flags IS
  'Platform feature flags (URD §2.3 "Platform config — … feature flags …", US-14.12). Admin-editable in Admin Portal Config; every change is audited to audit.events (D-35). No spec prints this DDL — micro-change-set raised in the C062 handoff.';
COMMENT ON COLUMN config.feature_flags.enabled IS
  'Defaults to false, so a flag that exists but has never been configured is OFF. A new flag defaulting on would ship a behaviour change with the migration rather than with an operator decision.';

-- No seed rows. A flag is created by the deploy that reads it, and a table seeded with
-- flags nothing consults is a list of switches with no wires behind them.
