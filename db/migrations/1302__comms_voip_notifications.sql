-- =====================================================================================
-- 1302 — comms: VoIP sessions, push tokens, call log
-- Source: server_db_schema.md §11 + §23 + §24 + §25 · D4' §11-16 + the same three Δ sets
--         ADD §9.1 · D-24, D-27, AL-36, AL-48
--
-- AL-48 removed number masking entirely. Landed in the final post-Δ shape, so:
--   * comms.voip_sessions has NO masked_sms_fallback column (D-25 dropped),
--   * comms.call_log has NO share_token column and call_type is ('free_voip','direct_dial')
--     only — 'normal_masked' and 'web_masked' are migration history.
-- The earlier addenda that still describe masked calling are superseded (planner finding 4).
-- =====================================================================================

CREATE TABLE IF NOT EXISTS comms.voip_sessions (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  -- Bare in both specs: a call can attach to a Mode C ride or a Mode A/B session.
  ride_id UUID NOT NULL,
  livekit_room TEXT NOT NULL,
  started_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  ended_at TIMESTAMPTZ,
  CONSTRAINT ck_voip_sessions_span CHECK (ended_at IS NULL OR ended_at >= started_at));

CREATE INDEX IF NOT EXISTS ix_voip_sessions_ride ON comms.voip_sessions(ride_id, started_at DESC);
-- Sessions still believed open, for the reaper and for the room-in-use check.
CREATE INDEX IF NOT EXISTS ix_voip_sessions_open
  ON comms.voip_sessions(started_at) WHERE ended_at IS NULL;

COMMENT ON TABLE comms.voip_sessions IS
  'Free in-app LiveKit calls (D-24). The masked_sms_fallback flag both specs print in §11 is deliberately absent — AL-48 dropped D-25 masked calling, and §25 removes the column.';

CREATE TABLE IF NOT EXISTS comms.notification_tokens (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  user_id UUID NOT NULL REFERENCES iam.users(id) ON DELETE CASCADE,
  platform TEXT NOT NULL CONSTRAINT ck_notification_tokens_platform
    CHECK (platform IN ('android','ios')),
  token TEXT NOT NULL,
  updated_at TIMESTAMPTZ NOT NULL DEFAULT now());

SELECT public.attach_set_updated_at('comms','notification_tokens');

CREATE INDEX IF NOT EXISTS ix_notif_tokens_user ON comms.notification_tokens(user_id);
-- FCM and APNs both reissue a token to whichever install now owns it. Without this, a
-- reinstalled device leaves the old row behind and E-01 offers fan out to a dead handle.
CREATE UNIQUE INDEX IF NOT EXISTS ux_notif_tokens_token ON comms.notification_tokens(token);

COMMENT ON TABLE comms.notification_tokens IS
  'FCM / APNs registration tokens (D-27). Dispatch offers are sent high-priority through these (E-01), with a 3 s no-ack SMS fallback.';

CREATE TABLE IF NOT EXISTS comms.call_log (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  ride_id UUID REFERENCES rides.rides(id) ON DELETE CASCADE,
  -- Nullable since AL-44; AL-48 then removed the share_token that was its alternative, so
  -- a web-originated call can no longer be logged at all. Left nullable to match the specs
  -- and because the log is best-effort (see below).
  caller_id UUID REFERENCES iam.users(id),
  callee_role TEXT NOT NULL CONSTRAINT ck_call_log_callee_role
    CHECK (callee_role IN ('driver','passenger','sender','recipient')),
  call_type TEXT NOT NULL CONSTRAINT ck_call_log_call_type
    CHECK (call_type IN ('free_voip','direct_dial')),
  started_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  ended_at TIMESTAMPTZ,
  outcome TEXT);

CREATE INDEX IF NOT EXISTS ix_call_log_ride ON comms.call_log(ride_id, started_at DESC);
CREATE INDEX IF NOT EXISTS ix_call_log_caller ON comms.call_log(caller_id, started_at DESC);

COMMENT ON TABLE comms.call_log IS
  'Best-effort record of which channel a caller chose (AL-36, narrowed by AL-48). direct_dial is a plain tel: link the client reports having opened — the platform never sees the PSTN leg, so a missing row means nothing.';
