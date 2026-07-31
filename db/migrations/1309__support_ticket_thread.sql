-- =====================================================================================
-- 1309 — support: the ticket thread, the agent's handling columns, and the replay log
-- Source: server_db_schema.md §13 · D3' support-svc + admin-bff `/v1/admin/support/**`
--         backend/contracts/support.yaml · URD Epic 16 (US-16.1/16.2/16.3), US-9.23,
--         US-14.11, US-14.13 · D-35 · NFR-28
--
-- ⚠ Every object here is a micro-change-set, recorded in the C053 handoff. §13 gives
--   `support.tickets` eleven columns and one `admin_response TEXT`, which is a queue that
--   can remember exactly one sentence and cannot say who wrote it, when, or what the
--   ticket's status was before they did. The contracts already promise more than that:
--   `support.yaml`'s `Ticket` has a `resolvedAt` §13 has no column for, and admin-bff's
--   ticket queue offers a resolve whose text is "shown verbatim to the user".
-- =====================================================================================

-- -------------------------------------------------------------------------------------
-- (1) support.tickets — the columns an agent's handling needs
--
-- `assigned_to` / `assigned_at`  — C053's deliverable is "list, assign, respond, resolve".
--   §13 has no way to say a ticket is somebody's, so two CSRs answer the same one and the
--   Finance queue (US-9.23's daily-fee refund) cannot be worked as a queue at all.
-- `resolved_at` / `resolved_by`  — `support.yaml` already returns `resolvedAt`, and D-35's
--   appealability needs the *who*. `updated_at` cannot stand in: it moves on every reply.
-- `screenshot_upload_id`         — the definition of done: "stores the artifact in object
--   storage and links it by id, not by public URL". §13's `screenshot_url TEXT` is the
--   public URL that phrasing rules out, so the link becomes a foreign key onto
--   `docs.uploads` (D-36's pointer table, whose bytes are on SSE-KMS storage and whose
--   `auto_delete_at` carries NFR-28's 90-day raw delete). The old column stays: it is in
--   §13, it is nullable, and nothing here writes it — deleting a released column would be
--   a different change set from adding one.
-- -------------------------------------------------------------------------------------
ALTER TABLE support.tickets
  ADD COLUMN IF NOT EXISTS assigned_to UUID REFERENCES iam.users(id),
  ADD COLUMN IF NOT EXISTS assigned_at TIMESTAMPTZ,
  ADD COLUMN IF NOT EXISTS resolved_at TIMESTAMPTZ,
  ADD COLUMN IF NOT EXISTS resolved_by UUID REFERENCES iam.users(id),
  ADD COLUMN IF NOT EXISTS screenshot_upload_id UUID REFERENCES docs.uploads(id);

-- A ticket is RESOLVED exactly when it carries the instant it was resolved at. Without
-- this the two can disagree, and `resolvedAt` is what the user's thread renders as "this
-- was answered" — a RESOLVED ticket with a null instant reads as still open in the app,
-- and an OPEN ticket with one reads as answered on a queue nobody has answered.
--
-- NOT VALID, for 1307's reason: `resolved_at` is new, so every ticket already resolved by
-- subscription-svc's refund intake (US-9.23, the one pre-existing writer of this table)
-- has a NULL in it and a validating ADD would fail the deploy. Every *new* write is
-- checked, which is what closes the hole. Backfill with
--   UPDATE support.tickets SET resolved_at = updated_at WHERE status = 'RESOLVED' AND resolved_at IS NULL;
-- then VALIDATE CONSTRAINT.
DO $$ BEGIN
  ALTER TABLE support.tickets
    ADD CONSTRAINT ck_tickets_resolution
    CHECK ((status = 'RESOLVED') = (resolved_at IS NOT NULL)) NOT VALID;
EXCEPTION WHEN duplicate_object THEN NULL; END $$;

COMMENT ON COLUMN support.tickets.assigned_to IS
  'The CSR or Finance Officer working this ticket (US-16.3, US-14.13). NULL = unassigned, which is what the queue is.';
COMMENT ON COLUMN support.tickets.resolved_by IS
  'Who resolved it. D-35 makes a resolution appealable, and admin_response alone cannot say who wrote it.';
COMMENT ON COLUMN support.tickets.screenshot_upload_id IS
  'US-16.2 screenshot, as a docs.uploads id (D-36). The bytes are on object storage and the user is served a short-lived signed link, never a public URL.';
COMMENT ON COLUMN support.tickets.screenshot_url IS
  'server_db_schema.md §13''s original public-URL column. Superseded by screenshot_upload_id (C053) and written by nothing.';

-- The Support CSR / Finance queue paged by status: 1303's `ix_tickets_open` answers
-- "everything unresolved, oldest first" and cannot serve `?status=RESOLVED`, which the
-- admin-bff queue offers. `id` is in the key because two tickets raised in the same
-- transaction share `created_at` to the microsecond, and a cursor over the timestamp
-- alone would drop whichever straddled a page boundary.
CREATE INDEX IF NOT EXISTS ix_tickets_status_created
  ON support.tickets(status, created_at DESC, id DESC);

-- -------------------------------------------------------------------------------------
-- (2) support.ticket_events — the thread
--
-- The definition of done: "ticket status transitions are recorded and visible to the user
-- in the thread". §13 holds one `admin_response`, so a second reply overwrites the first
-- and a status change leaves no trace at all — the user sees the latest sentence and no
-- history. This is the append-only history behind `GET /v1/support/tickets/{userId}/
-- {ticketId}`; the ticket row keeps `status` and `admin_response` as the current values,
-- which is what admin-bff's `TicketRow` returns.
--
-- Not `audit.events`. That table is admin-bff's (D-35, C065) and is deliberately invisible
-- to users; this one is *for* the user, so it carries only what a user may read — no
-- before/after images, no internal note field.
-- -------------------------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS support.ticket_events (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  ticket_id UUID NOT NULL REFERENCES support.tickets(id) ON DELETE CASCADE,
  -- What happened. `assigned` is recorded and NOT shown to the user (see the C053 service
  -- rules): who inside MageRide is handling a complaint is not the complainant's business,
  -- and the queue still needs the trail.
  kind TEXT NOT NULL CONSTRAINT ck_ticket_events_kind
    CHECK (kind IN ('opened','assigned','responded','resolved','reopened')),
  -- The person who caused it: the ticket's own user for `opened`, the agent otherwise.
  -- Nullable because a system-raised event (a sweeper, an escalation) has no human behind
  -- it and naming one would be a lie in an audit trail.
  actor_id UUID REFERENCES iam.users(id),
  -- The actor's canonical role at the time (AL-06), so a later role change cannot rewrite
  -- who answered as what. Free text rather than a CHECK against the nine: `iam.roles` is a
  -- table, and a CHECK here would have to be migrated every time it grows.
  actor_role TEXT,
  -- The transition, when this event is one. Both NULL on `responded`, which moves nothing.
  from_status TEXT,
  to_status TEXT,
  -- The agent's words, on `responded` and `resolved`. Shown verbatim to the user
  -- (admin-bff.yaml's own wording), so it is stored verbatim.
  body TEXT,
  at TIMESTAMPTZ NOT NULL DEFAULT now());

-- The thread, oldest first — one query per ticket detail. `at` alone is not enough: the
-- `opened` event and the ticket share a transaction timestamp with anything else written
-- in it, so `id` breaks the tie deterministically.
CREATE INDEX IF NOT EXISTS ix_ticket_events_ticket
  ON support.ticket_events(ticket_id, at, id);

COMMENT ON TABLE support.ticket_events IS
  'Append-only thread behind GET /v1/support/tickets/{userId}/{ticketId} (Epic 16). Records every status transition and every agent reply; support.tickets keeps the current values.';

-- -------------------------------------------------------------------------------------
-- (3) support.command_log — R-14 idempotent replay for this service's POST mutations
--
-- ⚠ Spec gap — the same micro-change-set C020 (0104), C021 (0307), C034 (0710), C033
--   (0803), C045 (1307), C046 (1107), C047 (1203) and C049 (1005) each raised, now for the
--   twelfth bounded context. D3' §0 requires an `Idempotency-Key` on every POST mutation
--   and replays a duplicate "from a per-service command log"; D4' §5 prints DDL for
--   `rides.command_log` only, and pointing a second context at it would give two services
--   one shared primary key.
--
--   `POST /v1/support/tickets` is why it exists rather than being argued away: a proxy
--   retry or a double-tapped Submit on the raise-ticket sheet (SCR-PA-030a / SCR-DA-033a)
--   puts a **second identical complaint** on the queue, and there is no natural key that
--   would collide — a user may legitimately raise two tickets about the same trip.
--
--   Shape is 0307 exactly (0603 minus `ride_id`): a ticket is created by the command, so
--   there is no aggregate id to record when the key is claimed, and
--   `CommandLog:AggregateIdColumn` is null for this service.
-- -------------------------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS support.command_log (
  idempotency_key TEXT PRIMARY KEY,
  actor_type TEXT NOT NULL,
  actor_id UUID,
  command TEXT NOT NULL,
  request_hash BYTEA NOT NULL,                                -- same key + different body ⇒ 409
  response_status SMALLINT,                                   -- NULL while in flight
  response_body JSON,                                         -- json, not jsonb: replay is byte for byte
  response_content_type TEXT,                                 -- so a replayed error stays problem+json
  ts TIMESTAMPTZ NOT NULL DEFAULT now());

CREATE INDEX IF NOT EXISTS ix_support_command_log_inflight
  ON support.command_log(ts) WHERE response_status IS NULL;

COMMENT ON TABLE support.command_log IS
  'R-14 idempotent replay for support-svc''s POST mutations (D3'' §0). 5xx responses are never stored, so a retry re-executes rather than replaying a failure.';

-- -------------------------------------------------------------------------------------
-- (4) docs.uploads.kind gains support_screenshot
--
-- The column is deliberately un-CHECKed (1301's header says so — the set grows with every
-- upload surface), so this is a comment rather than a constraint change. Named here so the
-- vocabulary stays discoverable from the schema.
-- -------------------------------------------------------------------------------------
COMMENT ON COLUMN docs.uploads.kind IS
  'Free text, deliberately un-CHECKed. Known values: driving_license, registration, insurance, revenue_license, permit, vehicle_photo (registry); bank_statement, passbook_first_page, lankaqr_code (AL-49); support_screenshot (US-16.2, C053).';
