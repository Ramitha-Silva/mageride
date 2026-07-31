-- =====================================================================================
-- 1807 — fleet-ops row-level security: the tables and joins C059 added
-- Source: ADD §9.5 item 8 · US-13.3, US-13.4, US-13.5, US-13.11 · AL-50 · D7' §4.2
--         · C058 fence, restated for C059: "Every read is row-level-security scoped to
--           the caller's org. A cross-org read is a security bug."
--
-- 1806 put five org-owned tables and three joins behind the `mageride_fleet_reader` role
-- and the `app.fleet_id` GUC. Migration 0314 added four more relations the Fleet Portal
-- reads, and AL-50 gave fleet-svc a document surface it had none of. This file extends the
-- same fence over them and changes nothing about how it works — read 1806's header for the
-- mechanism, the fail-closed argument and why the policies are RESTRICTIVE and
-- role-targeted.
--
-- Numbered beside 1806 for its reason: it depends on `mageride_fleet_reader` (1804) and
-- cannot run from the registry range with the tables it protects.
--
-- ---------------------------------------------------------------------------------------
-- WHICH GETS A POLICY AND WHICH GETS A VIEW
--
-- A policy where the relation carries `fleet_id` and the fleet may hold the whole table:
-- `registry.fleet_schedules`, `registry.fleet_bulk_jobs`, `registry.documents` and
-- `spatial.geofences`. A view where the columns the portal needs come from a table the
-- fleet must NOT be able to read at all — `iam.users` for an assignment's driver name,
-- `registry.document_fields` for the AL-50 extraction, `registry.fleet_bulk_job_rows`
-- for the error report, none of which carries an org.
--
-- `registry.documents` is the interesting one. `ck_documents_owner` is an XOR, so a
-- driver's own licence has `fleet_id IS NULL`; the predicate `fleet_id =
-- registry.current_fleet_id()` therefore evaluates NULL and returns no row. **A fleet
-- reader cannot see a driver document even with the GUC set to that driver's own fleet**,
-- which is the property AL-27 wants and which a `driver_id IS NULL` predicate would only
-- have approximated.
-- =====================================================================================

-- -------------------------------------------------------------------------------------
-- The org-owned tables 0314 added
-- -------------------------------------------------------------------------------------

ALTER TABLE registry.fleet_schedules ENABLE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS fleet_schedules_platform ON registry.fleet_schedules;
DROP POLICY IF EXISTS fleet_schedules_org_scope ON registry.fleet_schedules;
CREATE POLICY fleet_schedules_platform ON registry.fleet_schedules
  FOR ALL TO PUBLIC USING (true) WITH CHECK (true);
CREATE POLICY fleet_schedules_org_scope ON registry.fleet_schedules
  AS RESTRICTIVE FOR ALL TO mageride_fleet_reader
  USING (fleet_id = registry.current_fleet_id());

ALTER TABLE registry.fleet_bulk_jobs ENABLE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS fleet_bulk_jobs_platform ON registry.fleet_bulk_jobs;
DROP POLICY IF EXISTS fleet_bulk_jobs_org_scope ON registry.fleet_bulk_jobs;
CREATE POLICY fleet_bulk_jobs_platform ON registry.fleet_bulk_jobs
  FOR ALL TO PUBLIC USING (true) WITH CHECK (true);
CREATE POLICY fleet_bulk_jobs_org_scope ON registry.fleet_bulk_jobs
  AS RESTRICTIVE FOR ALL TO mageride_fleet_reader
  USING (fleet_id = registry.current_fleet_id());

-- AL-50's four slots. The row for a Mode A route permit and the row for a driver's NIC live
-- in one table; this is what keeps the second out of a fleet's reach.
ALTER TABLE registry.documents ENABLE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS documents_platform ON registry.documents;
DROP POLICY IF EXISTS documents_org_scope ON registry.documents;
CREATE POLICY documents_platform ON registry.documents
  FOR ALL TO PUBLIC USING (true) WITH CHECK (true);
CREATE POLICY documents_org_scope ON registry.documents
  AS RESTRICTIVE FOR ALL TO mageride_fleet_reader
  USING (fleet_id = registry.current_fleet_id());

-- Nullable `fleet_id` here means something different from `registry.documents`: §17's own
-- operating-area polygons belong to no fleet, and the same predicate keeps those invisible
-- to an operator too. That is deliberate — a fleet's geofence screen shows the fences it
-- drew, not the platform's service boundary.
ALTER TABLE spatial.geofences ENABLE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS geofences_platform ON spatial.geofences;
DROP POLICY IF EXISTS geofences_org_scope ON spatial.geofences;
CREATE POLICY geofences_platform ON spatial.geofences
  FOR ALL TO PUBLIC USING (true) WITH CHECK (true);
CREATE POLICY geofences_org_scope ON spatial.geofences
  AS RESTRICTIVE FOR ALL TO mageride_fleet_reader
  USING (fleet_id = registry.current_fleet_id());

GRANT USAGE ON SCHEMA spatial TO mageride_fleet_reader;
GRANT SELECT ON registry.fleet_schedules, registry.fleet_bulk_jobs, registry.documents,
                spatial.geofences
  TO mageride_fleet_reader;

COMMENT ON POLICY documents_org_scope ON registry.documents IS
  'AL-50/AL-27: a fleet reader sees the documents its own organisation uploaded and no others. ck_documents_owner is an XOR, so a driver-owned row has fleet_id NULL and the predicate evaluates NULL — no row, whatever the GUC says (C059).';
COMMENT ON POLICY geofences_org_scope ON spatial.geofences IS
  'US-13.5: an operator sees the zones it drew. §17''s platform polygons carry no fleet_id and are invisible to every fleet reader, which is the same fail-closed direction (C059).';

-- -------------------------------------------------------------------------------------
-- The joins the fleet needs, and the base tables it must never hold
-- -------------------------------------------------------------------------------------

-- SCR-FP-005's assignment list. `iam.users` is every person on the platform and is never
-- granted — 1806 revokes it explicitly — so the driver's name and phone come through here
-- or not at all. The phone IS projected, unlike `iam.fleet_members_fleet`'s deliberate
-- omission: US-13.2 has the operator assign "by User ID / phone", so the number is the
-- identifier they searched by and already hold, not a fact the join discloses.
CREATE OR REPLACE VIEW registry.fleet_assignments_fleet WITH (security_barrier = true) AS
  SELECT a.id,
         a.fleet_id,
         a.vehicle_id,
         a.driver_id,
         a.assigned_at,
         a.valid_from,
         a.expires_at,
         a.revoked_at,
         u.first_name AS driver_name,
         u.phone      AS driver_phone,
         v.registration_number,
         -- What SCR-FP-005 renders as a chip, computed once here so the portal, the roster
         -- and any future export cannot each derive it differently.
         (a.revoked_at IS NULL
            AND a.valid_from <= now()
            AND (a.expires_at IS NULL OR a.expires_at > now())) AS is_active
    FROM registry.fleet_assignments a
    JOIN iam.users u ON u.id = a.driver_id
    JOIN registry.vehicles v ON v.id = a.vehicle_id
   WHERE a.fleet_id = registry.current_fleet_id();

COMMENT ON VIEW registry.fleet_assignments_fleet IS
  'Fleet-scoped driver assignments with the driver and plate a portal row shows (US-13.2/13.8/13.9). The only relation through which a fleet reader reaches iam.users for a driver; is_active is the validity window evaluated now, so an expired row reads inactive with nothing having been written.';

-- AL-50's per-document extraction. `registry.document_fields` carries no org — it is keyed by
-- document — so the scope is inherited through the join rather than restated, which is what
-- makes it impossible to widen by forgetting a predicate here.
CREATE OR REPLACE VIEW registry.document_fields_fleet WITH (security_barrier = true) AS
  SELECT f.id,
         f.document_id,
         f.field_key,
         f.field_value,
         f.confidence,
         f.source,
         f.verify_status,
         f.confirmed_at,
         d.fleet_id,
         d.vehicle_id,
         d.kind
    FROM registry.document_fields f
    JOIN registry.documents d ON d.id = f.document_id
   WHERE d.fleet_id = registry.current_fleet_id();

COMMENT ON VIEW registry.document_fields_fleet IS
  'Fleet-scoped extracted document fields (AL-29/AL-50, SCR-FP-004''s per-document chips). `confirmed_by` is deliberately absent: which Verification Officer confirmed a field is the officer''s, and the operator only needs to know that somebody did.';

-- The downloadable per-row error report (US-13.1). Keyed by job, so the org comes from the
-- job the same way the fields' comes from the document.
CREATE OR REPLACE VIEW registry.fleet_bulk_job_rows_fleet WITH (security_barrier = true) AS
  SELECT r.job_id,
         r.row_number,
         r.registration_number,
         r.vehicle_type,
         r.mode,
         r.mode_b_billing,
         r.default_monthly_fare_minor,
         r.status,
         r.vehicle_id,
         r.error_code,
         r.error_detail,
         j.fleet_id
    FROM registry.fleet_bulk_job_rows r
    JOIN registry.fleet_bulk_jobs j ON j.id = r.job_id
   WHERE j.fleet_id = registry.current_fleet_id();

COMMENT ON VIEW registry.fleet_bulk_job_rows_fleet IS
  'Fleet-scoped bulk-import rows, the source of the downloadable error report (US-13.1).';

GRANT SELECT ON registry.fleet_assignments_fleet, registry.document_fields_fleet,
                registry.fleet_bulk_job_rows_fleet
  TO mageride_fleet_reader;

-- Belt and braces, as 1806 does for its three: the base tables these views exist to keep out
-- of reach, against a permissive ALTER DEFAULT PRIVILEGES anywhere in the cluster.
REVOKE ALL ON registry.document_fields     FROM mageride_fleet_reader;
REVOKE ALL ON registry.fleet_bulk_job_rows FROM mageride_fleet_reader;
