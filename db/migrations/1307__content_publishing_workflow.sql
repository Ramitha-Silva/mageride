-- =====================================================================================
-- 1307 — content: the publishing workflow, the broadcast window, the onboarding carousel
-- Source: server_db_schema.md §14 · D3' content-svc (`PUT /v1/admin/content/{key}`,
--         `GET /v1/content/broadcasts`) · ADD AL-28 / D5' BR-25.1 / URD US-1.2, US-1.2a ·
--         D-26 (Si / Ta / En)
--
-- Owned by C045 (content-svc). Everything here is a micro-change-set: §14's three tables are
-- five, six and five columns wide and cannot express the three things D3' and the ADD ask of
-- this service.
--
--   (1) **A versioned edit with an approval step.** D3' calls the admin route "versioned
--       template edit (approval workflow)" and §14 gives the table `version` and `approved_by`
--       — a *who*, with no *whether* and no *when*. Without a state there is nowhere to put an
--       edit that has been written and not yet approved, so the only implementable reading of
--       "approval workflow" is "the edit goes live and an admin's id is filed next to it".
--       `status` + `approved_at` are the missing half; `created_by` is who drafted it, which
--       `approved_by` cannot also mean (D-35 wants both sides of a four-eyes edit).
--
--   (2) **A broadcast has a window, not just a start.** `GET /v1/content/broadcasts` serves the
--       announcements "currently in force" (US-14.8) and §14 gives `scheduled_at` alone. A
--       banner with no end never comes down, so either every broadcast is permanent or the rule
--       is unimplementable. `ends_at` is the other end of the window.
--
--   (3) **AL-28's carousel has nowhere to live.** "3 client-paged slides (strings/illustrations
--       served by content-svc, localised Si/Ta/En)" — a slide is an ordered pair of trilingual
--       strings plus an illustration reference, per audience. `notification_templates` is keyed
--       by one string per key per language and has no illustration and no ordering, so putting
--       six slides in it would mean twelve invented template keys and no way to say which slide
--       is second. `content.onboarding_slides` is the table.
--
-- The trilingual rule is enforced **in the database** here, not only in the service, following
-- C005's `ck_broadcasts_trilingual`: CLAUDE.md's "all user-facing strings support Si/Ta/En" and
-- this component's first fence ("... or the template is invalid") are the same requirement, and a
-- constraint is the only form of it that also binds the Admin Portal, a psql session and a later
-- component that has not read this file.
-- =====================================================================================

-- -------------------------------------------------------------------------------------
-- The trilingual test for a JSONB *value*, which `?&` cannot make.
--
-- C005's `ck_broadcasts_trilingual` is `message_by_lang ?& array['si','ta','en']` — a **key
-- presence** test. `{"si":null,"ta":"","en":"ok"}` satisfies it, and so does `{"si":1,…}`: the
-- keys are all there. What ships to a user is a blank message in one language, which is the
-- failure the constraint was written to prevent, and content-svc's reader refuses to serve such a
-- row at all (it throws rather than pretend two languages are three), so one bad row would take a
-- whole list endpoint down. This is the same rule as a string.
-- -------------------------------------------------------------------------------------
CREATE OR REPLACE FUNCTION content.is_trilingual_text(p_value JSONB) RETURNS boolean AS $$
  SELECT p_value IS NOT NULL
     AND (SELECT count(*) FROM (VALUES ('si'), ('ta'), ('en')) AS l(code)
           WHERE jsonb_typeof(p_value -> l.code) = 'string'
             AND length(btrim(p_value ->> l.code)) > 0) = 3;
$$ LANGUAGE sql IMMUTABLE;

COMMENT ON FUNCTION content.is_trilingual_text(JSONB) IS
  'True when a JSONB object carries a non-blank string for si, ta and en (D-26). `?&` tests key presence only, which admits nulls and empty strings.';

-- -------------------------------------------------------------------------------------
-- (1) content.notification_templates — draft → published, with both actors recorded
-- -------------------------------------------------------------------------------------

-- 'published' is the default so that a row inserted by a *migration* is live content: the §20
-- seed in 1902 names neither column, and day-0 templates are published by definition. The admin
-- path names both explicitly ('draft', NULL) — see ck_notification_templates_approval below.
ALTER TABLE content.notification_templates
  ADD COLUMN IF NOT EXISTS status TEXT NOT NULL DEFAULT 'published',
  ADD COLUMN IF NOT EXISTS approved_at TIMESTAMPTZ DEFAULT now(),
  ADD COLUMN IF NOT EXISTS created_by UUID REFERENCES iam.users(id);

DO $$ BEGIN
  ALTER TABLE content.notification_templates
    ADD CONSTRAINT ck_notification_templates_status
    CHECK (status IN ('draft','published','archived'));
EXCEPTION WHEN duplicate_object THEN NULL; END $$;

-- Rows that predate this script (1902's seed on an already-migrated database) get the timestamp
-- the default would have given them, so the approval check below is valid rather than NOT VALID.
UPDATE content.notification_templates
   SET approved_at = created_at
 WHERE status = 'published' AND approved_at IS NULL;

DO $$ BEGIN
  ALTER TABLE content.notification_templates
    ADD CONSTRAINT ck_notification_templates_approval
    CHECK (status <> 'published' OR approved_at IS NOT NULL);
EXCEPTION WHEN duplicate_object THEN NULL; END $$;

COMMENT ON COLUMN content.notification_templates.status IS
  'draft (written, not servable) | published (current if it is the highest published version) | archived. D3'' calls the admin route a versioned edit with an approval workflow; version and approved_by alone cannot express the unapproved state.';
COMMENT ON COLUMN content.notification_templates.approved_at IS
  'When the version was published. Defaults to now() so a seed INSERT that names neither column is valid published content; the admin draft path writes NULL explicitly.';
COMMENT ON COLUMN content.notification_templates.created_by IS
  'The admin who drafted the version. approved_by is who published it — a four-eyes edit needs both (D-35).';

-- "Give me the current template for this key and language" is now "the highest *published*
-- version", so the C005 index gains the status. Partial rather than a fourth column: a draft is
-- never resolved by the render path, and every render on the platform goes through this lookup.
CREATE INDEX IF NOT EXISTS ix_notification_templates_published
  ON content.notification_templates(template_key, language, version DESC)
  WHERE status = 'published';

-- -------------------------------------------------------------------------------------
-- The fence, as a constraint: a template version exists in all three languages or it does not
-- exist.
--
-- A row trigger cannot say this — the invariant is over the *set* of rows sharing
-- (template_key, version) — so it is a DEFERRABLE INITIALLY DEFERRED constraint trigger that
-- fires at COMMIT, by which time the three sibling INSERTs of one publish are all present. That
-- is also what makes it safe for 1902, whose twelve seed rows land in one statement.
--
-- It counts languages per (key, version) irrespective of status: a *draft* in two languages is
-- as invalid as a published one, because the missing translation is the thing that would ship.
-- Deleting the third language of a version is caught by the same check on the two survivors.
--
-- **Both the NEW and the OLD (template_key, version) are checked**, and that is not belt and
-- braces: an UPDATE that moves one language's row to a different version leaves the version it
-- came *from* with two languages, and a trigger that only looked at NEW would let that commit —
-- which is the one hole through which a live template could end up published in Sinhala and
-- English but not Tamil.
-- -------------------------------------------------------------------------------------
CREATE OR REPLACE FUNCTION content.assert_template_trilingual() RETURNS trigger AS $$
DECLARE
  v_langs INTEGER;
  v_pair  RECORD;
BEGIN
  FOR v_pair IN
    SELECT DISTINCT key, version FROM (VALUES
      (NEW.template_key, NEW.version),
      (OLD.template_key, OLD.version)) AS pairs(key, version)
     WHERE key IS NOT NULL AND version IS NOT NULL
  LOOP
    SELECT count(DISTINCT language) INTO v_langs
      FROM content.notification_templates
     WHERE template_key = v_pair.key AND version = v_pair.version;

    -- 0 = the whole version was deleted, which is a withdrawal rather than a partial template.
    IF v_langs NOT IN (0, 3) THEN
      RAISE EXCEPTION
        'template % version % exists in % of 3 languages; every user-facing string exists in si, ta and en or the template is invalid (D-26)',
        v_pair.key, v_pair.version, v_langs
        USING ERRCODE = 'check_violation';
    END IF;
  END LOOP;

  RETURN NULL;
END;
$$ LANGUAGE plpgsql;

COMMENT ON FUNCTION content.assert_template_trilingual() IS
  'Deferred constraint trigger: a (template_key, version) exists in all three languages or in none (D-26, C045 fence 1).';

DROP TRIGGER IF EXISTS trg_notification_templates_trilingual ON content.notification_templates;
CREATE CONSTRAINT TRIGGER trg_notification_templates_trilingual
  AFTER INSERT OR UPDATE OR DELETE ON content.notification_templates
  DEFERRABLE INITIALLY DEFERRED
  FOR EACH ROW EXECUTE FUNCTION content.assert_template_trilingual();

-- -------------------------------------------------------------------------------------
-- (2) content.broadcasts — the other end of the window, and who published it
-- -------------------------------------------------------------------------------------

ALTER TABLE content.broadcasts
  ADD COLUMN IF NOT EXISTS ends_at TIMESTAMPTZ,
  ADD COLUMN IF NOT EXISTS created_by UUID REFERENCES iam.users(id);

DO $$ BEGIN
  ALTER TABLE content.broadcasts
    ADD CONSTRAINT ck_broadcasts_window
    CHECK (ends_at IS NULL OR scheduled_at IS NULL OR ends_at > scheduled_at);
EXCEPTION WHEN duplicate_object THEN NULL; END $$;

COMMENT ON COLUMN content.broadcasts.ends_at IS
  'When the banner comes down; NULL = until it is superseded. §14 gives scheduled_at alone, which makes "active broadcasts" (US-14.8) unimplementable.';
COMMENT ON COLUMN content.broadcasts.created_by IS
  'The admin who published it (D-35).';

-- The read is "every broadcast whose window covers now(), newest first", and C005's index cannot
-- serve it: `ix_broadcasts_scheduled` is `(scheduled_at) WHERE scheduled_at IS NOT NULL`, which is
-- ascending and excludes the NULL-scheduled rows entirely — and §14 makes `scheduled_at` nullable
-- precisely so a banner can be published without one. content-svc always writes an explicit
-- `scheduled_at` (so the response's `startsAt` is the instant it reported rather than whatever
-- `created_at` defaulted to), but the column is open to any writer and the read has to answer for
-- both shapes, which is what the COALESCE expression indexes.
CREATE INDEX IF NOT EXISTS ix_broadcasts_active
  ON content.broadcasts(COALESCE(scheduled_at, created_at) DESC, ends_at);

-- The strong form of C005's `ck_broadcasts_trilingual`, which is a key-presence test (see
-- content.is_trilingual_text above). NOT VALID: the column predates this script, so existing rows
-- are left as they are rather than failing a deploy — every *new* write is checked, which is what
-- closes the hole. A pre-existing blank-language broadcast is a row content-svc refuses to serve;
-- run `SELECT id FROM content.broadcasts WHERE NOT content.is_trilingual_text(message_by_lang)` to
-- find one, then `VALIDATE CONSTRAINT` when it is clean.
DO $$ BEGIN
  ALTER TABLE content.broadcasts
    ADD CONSTRAINT ck_broadcasts_trilingual_strict
    CHECK (content.is_trilingual_text(message_by_lang)) NOT VALID;
EXCEPTION WHEN duplicate_object THEN NULL; END $$;

-- -------------------------------------------------------------------------------------
-- content.command_log — R-14 idempotent replay for this service's POST mutations
--
-- ⚠ Spec gap — the same micro-change-set C020 (0104), C021 (0307), C034 (0710) and C033 (0803)
--   raised, now for the fifth bounded context. D3' §0 requires an `Idempotency-Key` on every POST
--   mutation and replays a duplicate "from a per-service command log"; `content.yaml` declares the
--   header required on `POST /v1/admin/content/broadcasts` and `POST …/approve`. D4' §5 prints DDL
--   for `rides.command_log` only, and pointing a second context at it would give two services one
--   shared primary key.
--
--   `POST /v1/admin/content/broadcasts` is why this table exists rather than being argued away:
--   an approve is self-limiting (a second one is a 409 by the version's own status) and a cache
--   purge is idempotent by nature, but a retried publish — a proxy retry, a portal double-submit,
--   a 502 on the way back — puts a **second identical banner** in front of every user on the
--   platform, and there is no natural key that would collide.
--
--   Shape is 0307 exactly (0603 minus `ride_id`): a broadcast targets no aggregate that exists
--   yet, so `CommandLog:AggregateIdColumn` is null for this service.
-- -------------------------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS content.command_log (
  idempotency_key TEXT PRIMARY KEY,
  actor_type TEXT NOT NULL,
  actor_id UUID,                                              -- the admin; every POST here is authenticated
  command TEXT NOT NULL,
  request_hash BYTEA NOT NULL,                                -- same key + different body ⇒ 409
  response_status SMALLINT,                                    -- NULL while in flight
  response_body JSON,                                          -- json, not jsonb: replay is byte for byte
  response_content_type TEXT,                                   -- so a replayed error stays problem+json
  ts TIMESTAMPTZ NOT NULL DEFAULT now());

CREATE INDEX IF NOT EXISTS ix_content_command_log_inflight
  ON content.command_log(ts) WHERE response_status IS NULL;

COMMENT ON TABLE content.command_log IS
  'R-14 idempotent replay for content-svc''s POST mutations (D3'' §0). 5xx responses are never stored, so a retry re-executes rather than replaying a failure.';

-- -------------------------------------------------------------------------------------
-- (3) content.onboarding_slides — AL-28 / US-1.2 / US-1.2a
--
-- The strings are trilingual JSONB rather than three columns for the same reason C005 chose it
-- for `broadcasts.message_by_lang`: the shape matches what the Admin Portal posts, and `?&` is
-- the schema-level expression of the platform rule. It differs from `faq_articles`' row-per-
-- language deliberately — a carousel is served to a client that has *not yet chosen a language*
-- (the picker is on the same screen, SCR-DA/DI-002), so all three ship in one answer and the
-- language toggle re-renders with no round trip.
-- -------------------------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS content.onboarding_slides (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  -- Which app's first-run screen: SCR-PA/PI-002 (passenger, US-1.2) or SCR-DA/DI-002 (driver,
  -- US-1.2a). Not a role — nobody is authenticated on either screen.
  audience TEXT NOT NULL CONSTRAINT ck_onboarding_slides_audience
    CHECK (audience IN ('driver','passenger')),
  -- 1-based position in the pager. UNIQUE per audience, so "slide 2" is one row and reordering
  -- is an UPDATE rather than a re-sort of ties.
  slot INTEGER NOT NULL CONSTRAINT ck_onboarding_slides_slot CHECK (slot >= 1),
  title_by_lang JSONB NOT NULL CONSTRAINT ck_onboarding_slides_title_trilingual
    CHECK (content.is_trilingual_text(title_by_lang)),
  body_by_lang JSONB NOT NULL CONSTRAINT ck_onboarding_slides_body_trilingual
    CHECK (content.is_trilingual_text(body_by_lang)),
  -- The illustration *reference*: an app-bundled asset key (the seeded form) or an absolute
  -- https URL. content-svc serves the reference and never the bytes — AL-28 is "pure
  -- presentation, no new API" and the platform has no public asset bucket (D7' §4 names four
  -- private ones).
  illustration_ref TEXT NOT NULL CONSTRAINT ck_onboarding_slides_illustration
    CHECK (length(btrim(illustration_ref)) > 0),
  is_active BOOLEAN NOT NULL DEFAULT true,
  created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  updated_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  CONSTRAINT ux_onboarding_slides_slot UNIQUE (audience, slot));

CREATE INDEX IF NOT EXISTS ix_onboarding_slides_active
  ON content.onboarding_slides(audience, slot) WHERE is_active;

SELECT public.attach_set_updated_at('content','onboarding_slides');

COMMENT ON TABLE content.onboarding_slides IS
  'The AL-28 / US-1.2 feature carousel: 3 slides per audience, each an illustration reference plus a trilingual headline and body. Served public and cacheable beside GET /v1/config/cities — both feed the same first-run screen.';
