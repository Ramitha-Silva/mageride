-- =====================================================================================
-- 1308 — comms: the outbound notification queue and notification-svc's replay log
-- Source: D5' §14.4 (the per-type table) · D6' §7.3/§7.4 · D3' notification-svc ·
--         ADD §11.15 · E-01, D-27, D-33, P-09, P-12, AL-21, AL-44, AL-45
--
-- ⚠ Spec gap — micro-change-set, raised in the C051 handoff. **No spec declares a table for
--   an outbound notification.** §11's `comms` schema is three tables — VoIP sessions, FCM/APNs
--   registration tokens and the call log (1302) — and D5' §14.4's "notification table" is a
--   matrix of trigger × channel × type × throttle in prose, not DDL. Every producer on the
--   platform is already waiting on one: fare-svc's `QrNudgeSweeper` logs the AL-47 re-push it
--   cannot send "because notification-svc has no outbound queue table" (C050 handoff),
--   fleet-health-svc emits `fleet.health_alert` for a consumer that has nowhere to record what
--   it did with it, and wallet-svc's `wallet.low_balance` is the same shape.
--
--   Two things make the table unavoidable rather than convenient:
--
--   (1) **D-27's "exponential-backoff worker".** A retry schedule that lives only in a process
--       is lost on the next deploy, and the messages in flight with it. `next_attempt_at` +
--       `attempts` is the schedule; the row is what survives the restart.
--
--   (2) **E-01's "3 s no-ack → SMS fallback", exactly once.** The fallback is a *second*
--       message costing real money and interrupting a driver, so "exactly once" cannot be a
--       property of a worker that might run on two replicas or twice after a crash. It is a
--       guarded `UPDATE … WHERE status = 'Sent' AND acked_at IS NULL`, and the row it guards
--       is here. `ux_notifications_dedupe` is the same argument one level up: Kafka delivery is
--       at least once (D6' §2.3), so a redelivered `offer.created` must find its notification
--       already claimed rather than push a second time.
--
-- `comms.command_log` is the tenth instance of the R-14 micro-change-set (iam 0104, registry
-- 0307, dispatch 0710, reputation 0803, content 1307, fares 1005 …): D3' §0 requires an
-- `Idempotency-Key` on every POST mutation and `notification.yaml` declares it on all three of
-- this service's POSTs. D4' §5 prints DDL for `rides.command_log` only.
-- =====================================================================================

CREATE TABLE IF NOT EXISTS comms.notifications (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),

  -- The exactly-once key, and the reason this column is NOT NULL UNIQUE rather than an
  -- afterthought. Every producer this service consumes delivers at least once, so the claim
  -- has to be made by the database: `INSERT … ON CONFLICT (dedupe_key) DO NOTHING` returning
  -- no row *is* "somebody already has this one". Shaped `{source}:{subject}:{type}` by
  -- NotificationDedupe — an event id alone would not do, because one `ride.accepted` produces
  -- a push to the passenger and another to the driver.
  dedupe_key TEXT NOT NULL,

  -- D5' §14.4's Type column (RIDE_OFFER, DRIVER_ASSIGNED, LOW_BALANCE, location_request, …).
  -- The per-type preference switch (US-10.7) and the throttle are both keyed by it.
  notification_type TEXT NOT NULL,

  -- content.notification_templates.template_key. Nullable for the data-only messages that
  -- carry no user-visible string at all — the P-02 location request is a silent FCM data
  -- message the app renders from its own resources, and a template row for it would be a
  -- string nobody displays.
  template_key TEXT,

  channel TEXT NOT NULL CONSTRAINT ck_notifications_channel
    CHECK (channel IN ('push','sms')),

  -- NULL for the two recipients who have no account: AL-21's unregistered package recipient
  -- and AL-45's unregistered proxy rider. Both are addressed by number alone.
  recipient_user_id UUID REFERENCES iam.users(id) ON DELETE CASCADE,

  -- E.164, and only ever set on an SMS row. It is the delivery address — a queue that survives
  -- a restart cannot re-derive it, and for the two unregistered recipients above there is no
  -- account to re-read it from. Held in the clear for the same reason `rides.rides
  -- .recipient_phone` is (C037): P-03 hashes the proxy rider because nothing ever has to
  -- *reach* them, and this table exists precisely to reach somebody. `NotificationRetention`
  -- sweeps the rows away afterwards.
  recipient_phone TEXT,

  -- Resolved at enqueue, not at send: D-26's promise is the language the *recipient* chose,
  -- and re-reading it at delivery time would let a preference change mid-retry produce two
  -- attempts in two languages.
  language TEXT NOT NULL DEFAULT 'en' CONSTRAINT ck_notifications_language
    CHECK (language IN ('si','ta','en')),

  -- E-01: `high` is FCM priority=high + APNs apns-priority:10, which bypasses Doze and wakes a
  -- backgrounded driver app. Reserved for the offer push and the SOS.
  priority TEXT NOT NULL DEFAULT 'normal' CONSTRAINT ck_notifications_priority
    CHECK (priority IN ('normal','high')),

  -- Substitution values for the template plus the client-side data payload (deep links,
  -- request ids). JSONB because the shape is per type and the Admin Portal composes some of it.
  payload JSONB NOT NULL DEFAULT '{}',

  status TEXT NOT NULL DEFAULT 'Pending' CONSTRAINT ck_notifications_status
    CHECK (status IN ('Pending','Sent','Acked','Failed','Suppressed','FellBackToSms')),

  attempts INTEGER NOT NULL DEFAULT 0 CHECK (attempts >= 0),

  -- The D-27 backoff schedule. NULL means "not due" — a Sent, Acked, Failed or Suppressed row
  -- is never picked up again, which is what the partial index below relies on.
  next_attempt_at TIMESTAMPTZ,

  -- Which transport actually took it (fcm | apns | notifylk | secondary | log), and its handle.
  -- Two columns rather than one because a failed send has a provider and no id.
  provider TEXT,
  provider_message_id TEXT,
  last_error TEXT,

  -- E-01's three seconds, as a deadline rather than a duration: the sweep compares it with the
  -- clock, so a worker that was asleep for a minute still fires the fallback for the right rows.
  -- NULL on every notification that has no ack contract, which is all of them but the offer.
  ack_deadline_at TIMESTAMPTZ,
  acked_at TIMESTAMPTZ,

  -- The SMS that replaced a push nobody acked. Self-referencing so the pair can be read back as
  -- one story, and ON DELETE SET NULL so the retention sweep can drop the push without taking
  -- the record of the fallback with it.
  fallback_of UUID REFERENCES comms.notifications(id) ON DELETE SET NULL,

  created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  updated_at TIMESTAMPTZ NOT NULL DEFAULT now(),

  -- A push is addressed by a device token, which is looked up per attempt from
  -- comms.notification_tokens; an SMS is addressed by the number on the row. A row that can be
  -- addressed by neither is undeliverable, and the queue should refuse to hold one.
  CONSTRAINT ck_notifications_addressable CHECK (
    recipient_user_id IS NOT NULL OR recipient_phone IS NOT NULL),
  CONSTRAINT ck_notifications_sms_destination CHECK (
    channel <> 'sms' OR recipient_phone IS NOT NULL));

SELECT public.attach_set_updated_at('comms','notifications');

-- The claim. Unique rather than a primary key because the surrogate id is what `fallback_of`
-- and the workers address; this is the key the *producers* collide on.
CREATE UNIQUE INDEX IF NOT EXISTS ux_notifications_dedupe
  ON comms.notifications(dedupe_key);

-- The delivery worker's queue: `… WHERE status = 'Pending' ORDER BY next_attempt_at
-- FOR UPDATE SKIP LOCKED`. Partial, so the index stays the size of the backlog rather than of
-- the history.
CREATE INDEX IF NOT EXISTS ix_notifications_due
  ON comms.notifications(next_attempt_at)
  WHERE status = 'Pending';

-- E-01's sweep: sent offer pushes whose 3 s ack window has closed. Partial on the same three
-- predicates the guarded UPDATE uses, so the scan is the size of the offers in flight.
CREATE INDEX IF NOT EXISTS ix_notifications_ack_due
  ON comms.notifications(ack_deadline_at)
  WHERE status = 'Sent' AND acked_at IS NULL AND ack_deadline_at IS NOT NULL;

-- "What has this person been sent, and when" — the per-type throttles of §14.4 (once per ride,
-- once below Rs 200) and every support question about a message that did or did not arrive.
CREATE INDEX IF NOT EXISTS ix_notifications_recipient
  ON comms.notifications(recipient_user_id, notification_type, created_at DESC)
  WHERE recipient_user_id IS NOT NULL;

-- The retention sweep, which is also what takes `recipient_phone` back out of the database.
CREATE INDEX IF NOT EXISTS ix_notifications_created
  ON comms.notifications(created_at);

COMMENT ON TABLE comms.notifications IS
  'The outbound push/SMS queue and log (D5'' §14.4). No spec declares it — micro-change-set, C051. It is what makes D-27''s backoff survive a restart and E-01''s 3 s SMS fallback exactly once; dedupe_key is the claim that turns at-least-once event delivery into one message.';
COMMENT ON COLUMN comms.notifications.dedupe_key IS
  'The producer''s claim, `{source}:{subject}:{type}`. A redelivered Kafka message collides here and sends nothing.';
COMMENT ON COLUMN comms.notifications.recipient_phone IS
  'E.164 delivery address for an SMS, in the clear because it is what the message is sent to and the two AL-21/AL-45 recipients have no account to re-read it from. Swept by Notification:Retention.';
COMMENT ON COLUMN comms.notifications.ack_deadline_at IS
  'E-01: an offer push not acked by this instant falls back to SMS, exactly once, by guarded UPDATE.';
COMMENT ON COLUMN comms.notifications.fallback_of IS
  'The push this SMS replaced. Set only by the E-01 sweep.';

-- -------------------------------------------------------------------------------------
-- comms.command_log — R-14 replay for this service's three POSTs
--
-- Shape is 0710 exactly (which is 0603 minus the aggregate id): registering a device token
-- targets no aggregate this service owns, and MageRide.Shared's PostgresCommandLog omits the
-- column when CommandLog:AggregateIdColumn is null.
-- -------------------------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS comms.command_log (
  idempotency_key TEXT PRIMARY KEY,
  actor_type TEXT NOT NULL,
  actor_id UUID,
  command TEXT NOT NULL,
  request_hash BYTEA NOT NULL,
  response_status SMALLINT,
  response_body JSON,
  response_content_type TEXT,
  ts TIMESTAMPTZ NOT NULL DEFAULT now());

CREATE INDEX IF NOT EXISTS ix_comms_command_log_inflight
  ON comms.command_log(ts) WHERE response_status IS NULL;

COMMENT ON TABLE comms.command_log IS
  'R-14 idempotent replay for notification-svc''s POST mutations (D3'' §0). 5xx responses are never stored, so a retry re-executes rather than replaying a failure.';

-- -------------------------------------------------------------------------------------
-- comms.notification_tokens — two columns 1302 could not have known it needed
--
-- (a) `device_id`. `notification.yaml`'s register-token body has carried it since C013 and
--     1302 has nowhere to put it. AL-08 binds one install to one session per app, and a
--     reinstall on the same handset arrives with a *new* FCM token and the same device id: with
--     only `ux_notif_tokens_token` (which moves a token between users) the old handle survives
--     and every E-01 offer fans out to a dead one. `ux_notif_tokens_device` is the other half
--     of the same rule, per (user, app-install).
-- (b) `last_seen_at`. FCM and APNs both retire a token that has not been refreshed in ~270
--     days, and a queue that keeps retrying one is a queue that never drains. `updated_at`
--     cannot serve: it moves on any column change, including one this service makes.
-- -------------------------------------------------------------------------------------
ALTER TABLE comms.notification_tokens
  ADD COLUMN IF NOT EXISTS device_id TEXT,
  ADD COLUMN IF NOT EXISTS last_seen_at TIMESTAMPTZ NOT NULL DEFAULT now();

CREATE UNIQUE INDEX IF NOT EXISTS ux_notif_tokens_device
  ON comms.notification_tokens(user_id, device_id) WHERE device_id IS NOT NULL;

COMMENT ON COLUMN comms.notification_tokens.device_id IS
  'The install this token belongs to (AL-08). A reinstall reuses it with a new token, which is what lets the old handle be replaced rather than accumulated.';
COMMENT ON COLUMN comms.notification_tokens.last_seen_at IS
  'Last refresh from the device. A token untouched past Notification:TokenStaleAfter is not worth a push attempt.';
