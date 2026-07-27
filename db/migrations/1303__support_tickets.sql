-- =====================================================================================
-- 1303 — support: tickets
-- Source: server_db_schema.md §13 · D4' §11-16 · ADD §9.1 · Epic 16, US-16.2, US-9.23
-- =====================================================================================

CREATE TABLE IF NOT EXISTS support.tickets (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  user_id UUID NOT NULL REFERENCES iam.users(id),
  -- Free text in both specs. The raise-ticket sheet (SCR-PA-030a / SCR-DA-033a) offers a
  -- fixed list; support-svc (C053) owns it rather than a CHECK that an admin cannot edit.
  category TEXT NOT NULL,
  description TEXT NOT NULL,
  -- Bare in both specs: the sheet offers a dropdown of past trip IDs, which may be a Mode
  -- C ride or a Mode A/B session.
  ride_id UUID,
  screenshot_url TEXT,
  status TEXT NOT NULL DEFAULT 'OPEN' CONSTRAINT ck_tickets_status
    CHECK (status IN ('OPEN','IN_PROGRESS','RESOLVED')),
  admin_response TEXT,
  created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  updated_at TIMESTAMPTZ NOT NULL DEFAULT now());

SELECT public.attach_set_updated_at('support','tickets');

CREATE INDEX IF NOT EXISTS ix_tickets_user ON support.tickets(user_id, created_at DESC);
-- The Support CSR queue (SCR-AP-006): everything not yet resolved, oldest first.
CREATE INDEX IF NOT EXISTS ix_tickets_open
  ON support.tickets(created_at) WHERE status <> 'RESOLVED';

COMMENT ON TABLE support.tickets IS
  'In-app support tickets (Epic 16, US-16.2). Also carries the driver daily-fee refund request (US-9.23) as an ordinary category.';
