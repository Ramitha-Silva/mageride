-- =====================================================================================
-- 1304 — content: localised templates, FAQ, broadcasts
-- Source: server_db_schema.md §14 · D4' §11-16 · ADD §9.1 · D-26 (Si / Ta / En)
--
-- D-26 and the root CLAUDE.md rule are the same requirement seen from two sides: no
-- user-facing string is ever hardcoded, and every one exists in all three languages. The
-- language CHECK is the schema-level half of that; the seed in 1902 is the other half.
-- =====================================================================================

CREATE TABLE IF NOT EXISTS content.notification_templates (
  template_key TEXT NOT NULL,
  language TEXT NOT NULL CONSTRAINT ck_notification_templates_language
    CHECK (language IN ('si','ta','en')),
  subject TEXT,
  body TEXT NOT NULL,
  -- Versioned, so a template edit cannot retroactively change what a delivered message
  -- said. content-svc resolves the highest version per (key, language).
  version INTEGER NOT NULL DEFAULT 1 CHECK (version >= 1),
  approved_by UUID REFERENCES iam.users(id),
  created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  PRIMARY KEY (template_key, language, version));

-- "Give me the current template for this key and language" on every notification send.
CREATE INDEX IF NOT EXISTS ix_notification_templates_current
  ON content.notification_templates(template_key, language, version DESC);

COMMENT ON TABLE content.notification_templates IS
  'Push / SMS bodies in Si, Ta and En (D-26). Rendered by content-svc; placeholders are {{name}} style. A key is only usable once all three languages exist.';

CREATE TABLE IF NOT EXISTS content.faq_articles (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  category TEXT NOT NULL,
  title TEXT NOT NULL,
  body TEXT NOT NULL,
  language TEXT NOT NULL CONSTRAINT ck_faq_articles_language
    CHECK (language IN ('si','ta','en')),
  sort_order INTEGER NOT NULL DEFAULT 0,
  created_at TIMESTAMPTZ NOT NULL DEFAULT now());

-- GET /v1/support/faq?lang=&category= (D3' support-svc, US-16.1).
CREATE INDEX IF NOT EXISTS ix_faq_articles_lookup
  ON content.faq_articles(language, category, sort_order);

COMMENT ON TABLE content.faq_articles IS
  'In-app FAQ (US-16.1), served per language and category. One row per language per article — the three translations are siblings, not columns.';

CREATE TABLE IF NOT EXISTS content.broadcasts (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  -- Audience selector, e.g. {"role":"driver","city":"colombo"}. Interpreted by
  -- notification-svc; JSONB because the admin composes it in the portal.
  audience JSONB,
  -- {"si":"…","ta":"…","en":"…"} — all three required by D-26, enforced below rather than
  -- by three columns so the shape matches what the Admin Portal posts.
  message_by_lang JSONB NOT NULL,
  scheduled_at TIMESTAMPTZ,
  created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  CONSTRAINT ck_broadcasts_trilingual CHECK (
    message_by_lang ?& array['si','ta','en']));

CREATE INDEX IF NOT EXISTS ix_broadcasts_scheduled
  ON content.broadcasts(scheduled_at) WHERE scheduled_at IS NOT NULL;

COMMENT ON TABLE content.broadcasts IS
  'Admin announcements (D-26). ck_broadcasts_trilingual is the schema-level expression of the platform rule that no user-facing string ships in fewer than three languages.';
