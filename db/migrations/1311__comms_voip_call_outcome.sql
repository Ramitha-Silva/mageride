-- =====================================================================================
-- 1311 — comms: one open room per ride, and a vocabulary for how a call ended (C055, voip-svc)
-- Source: D6' §6 · D3' voip-svc · ADD §14 (voip-svc unavailable), §16 (call-setup SLO)
--         AL-48 (masking withdrawn) · D-24
--
-- 1302 landed both tables in their final post-AL-48 shape and neither has had a writer until
-- now. Two things are missing, and both are about facts the service cannot otherwise state:
--
--   (1) `comms.voip_sessions` has no way to say "one open session per room". D3' gives a ride
--       exactly one room (`ride_{id}`), and BOTH parties call POST /v1/calls/start for the same
--       conversation — the driver taps Call, the rider taps Call back. Without a uniqueness
--       rule each tap opens its own session row, the teardown at trip end closes whichever it
--       finds, and `ix_voip_sessions_open` (1302's own index, "for the reaper and the
--       room-in-use check") answers a question with two rows where there is one room.
--
--   (2) `comms.call_log.outcome` is free text with no vocabulary. ADD §16 sets a p95
--       call-setup SLO and ADD §14 documents the "Call normally instead?" fallback; neither is
--       measurable unless a call that never connected can be told from one that did. The value
--       that matters is `voip_failed` — it is what the client reports when it puts the fallback
--       prompt up, and a `direct_dial` row that follows it on the same ride is the fallback
--       actually being taken. Nothing else distinguishes that from a user who simply preferred
--       to dial.
--
-- Raised as a micro-change-set in the C055 handoff. server_db_schema.md §11 / D4' §11-16
-- should carry both.
-- =====================================================================================

-- (1) One open session per room. Partial, because a ride that is called twice over its life —
-- the driver rings on the way, the passenger rings back after arrival — legitimately has two
-- CLOSED sessions and one open one at most.
CREATE UNIQUE INDEX IF NOT EXISTS ux_voip_sessions_open_room
  ON comms.voip_sessions(livekit_room) WHERE ended_at IS NULL;

COMMENT ON INDEX comms.ux_voip_sessions_open_room IS
  'D3'' gives a ride one LiveKit room (ride_{id}); both parties start a call into it. This is what makes the second start join the first session instead of opening a rival one.';

-- (2) The outcome vocabulary. NOT VALID is unnecessary — the column has never had a writer, so
-- there are no rows to grandfather.
DO $$
BEGIN
  IF NOT EXISTS (SELECT 1 FROM pg_constraint
                  WHERE conrelid = 'comms.call_log'::regclass
                    AND conname = 'ck_call_log_outcome') THEN
    ALTER TABLE comms.call_log
      ADD CONSTRAINT ck_call_log_outcome
      CHECK (outcome IS NULL OR outcome IN
        ('completed','missed','declined','cancelled','voip_failed'));
  END IF;

  -- An outcome describes a call that finished; a finished call has an end. Without this a row
  -- can claim `voip_failed` and still be open, which is exactly the row the SLO query counts.
  IF NOT EXISTS (SELECT 1 FROM pg_constraint
                  WHERE conrelid = 'comms.call_log'::regclass
                    AND conname = 'ck_call_log_ended') THEN
    ALTER TABLE comms.call_log
      ADD CONSTRAINT ck_call_log_ended
      CHECK ((outcome IS NULL) = (ended_at IS NULL));
  END IF;

  -- 1302 leaves the span unguarded, unlike ck_voip_sessions_span on the sibling table.
  IF NOT EXISTS (SELECT 1 FROM pg_constraint
                  WHERE conrelid = 'comms.call_log'::regclass
                    AND conname = 'ck_call_log_span') THEN
    ALTER TABLE comms.call_log
      ADD CONSTRAINT ck_call_log_span
      CHECK (ended_at IS NULL OR ended_at >= started_at);
  END IF;
END $$;

COMMENT ON COLUMN comms.call_log.outcome IS
  'How the call ended: completed | missed | declined | cancelled | voip_failed. No spec names these (C055); voip_failed is the signal ADD §14''s direct-dial fallback hangs on, and a direct_dial row after one on the same ride is the fallback being taken.';

-- The fallback-rate question the two above exist to answer: how many in-app calls never
-- connected. Partial, so the index is the size of the failures rather than of every call.
CREATE INDEX IF NOT EXISTS ix_call_log_voip_failed
  ON comms.call_log(started_at) WHERE outcome = 'voip_failed';
