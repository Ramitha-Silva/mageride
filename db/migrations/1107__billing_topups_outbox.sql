-- =====================================================================================
-- 1107 — billing: top-up sessions, this plane's outbox, and the replay log
-- Source: D3' wallet-svc (`POST /v1/wallet/topup/onepay` → `{topupId, state, redirectUrl}`) ·
--         D6' §7.1 (OnePay) / §7.2 (LankaQR, AL-15) · D5' §9.1/§9.2 · ADD §6 wallet-svc ·
--         D-09, D-12, D-08, R-13, R-14, R-19, AL-05
--
-- Owned by C046 (wallet-svc). Three objects, three gaps:
--
--   (1) **A top-up has an id, a state and a gateway reference, and nowhere to live.** D3' returns
--       `{topupId, state:"Pending", redirectUrl}` from the initiate call and D6' §7.1 has the
--       webhook arrive later carrying `{orderId, providerTransactionId, status}` — so the id has
--       to survive between two requests, and the webhook has to find what it is confirming.
--       §10 prints no such table: `fares.ride_payments` (1002) is the *ride* payment and its
--       `ride_id` is NOT NULL, so a wallet top-up cannot borrow it. Without `billing.topups`
--       there is no way to answer a redelivered callback, no way to reconcile the 90-second
--       pending window §7.1 asks for, and no record of a failed top-up at all.
--
--   (2) **`wallet.debited` / `wallet.credited` have a producer and two consumers and no topic
--       or outbox table.** ADD §6 and the replica's wallet-svc row both say "publishes
--       wallet.debited / wallet.credited events that invalidate dispatch-svc's Redis balance
--       cache" (D-08, D5' §9.2), and ride-svc's row lists `wallet.debited` among what it
--       consumes. D6' §2.1's registry has neither, and §10 has no `billing.outbox` — the same
--       shape C028 (0309), C030 (0403), C033 (0803) and C044 (1805) each raised.
--
--   (3) **R-14 needs a per-service command log** and D4' §5 prints only `rides.command_log`.
--       Fifth bounded context to need one, after iam (0104), registry (0307), dispatch (0710)
--       and reputation (0803) — and the one where a replayed POST moves money.
--
-- AL-05 is visible in this file as an absence and as a CHECK: `method` admits exactly `onepay`
-- and `lankaqr`. There is no bank-transfer method, no receipt column and no reconciliation-queue
-- table, and a row claiming one is rejected by the database rather than by a code review.
-- =====================================================================================

-- -------------------------------------------------------------------------------------
-- (1) billing.topups — one row per gateway session
-- -------------------------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS billing.topups (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  driver_id UUID NOT NULL REFERENCES iam.users(id),
  -- Resolved at initiate rather than at settlement: the account is what the credit posts to, and
  -- looking it up twice is how a top-up ends up in the wrong wallet after an account rebuild.
  account_id UUID NOT NULL REFERENCES billing.accounts(id),
  -- AL-05, as a constraint. OnePay covers both the card and the OnePay-wallet rails (D6' §7.1;
  -- D3' Part 2 lists one route for them, which is why there is no separate 'card').
  method TEXT NOT NULL CONSTRAINT ck_topups_method CHECK (method IN ('onepay','lankaqr')),
  amount_minor BIGINT NOT NULL CONSTRAINT ck_topups_amount CHECK (amount_minor > 0),
  currency CHAR(3) NOT NULL DEFAULT 'LKR',
  -- The three states `wallet.yaml`'s `Topup.state` enum prints. A session that outlives D6'
  -- §7.1's 90-second window is `Failed` with a reason rather than a fourth state, so the contract
  -- and the column cannot drift.
  state TEXT NOT NULL DEFAULT 'Pending' CONSTRAINT ck_topups_state
    CHECK (state IN ('Pending','Succeeded','Failed')),
  -- Our reference, sent to the gateway and echoed back as `orderId`. Its own column because a
  -- provider that echoes it is the only way to match a callback that arrives with no topupId.
  provider_order_id TEXT,
  -- R-19: the gateway's own id, and the dedupe key for the callback. A redelivery collides on
  -- ux_topups_provider_txn and credits nothing twice.
  provider_transaction_id TEXT,
  journal_entry_id UUID REFERENCES billing.journal_entries(id),
  failure_reason TEXT,
  created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  updated_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  settled_at TIMESTAMPTZ,
  -- Only a settled top-up moves money, so only it may carry a ledger entry — the same shape as
  -- ck_credit_transfers_posting (1105).
  CONSTRAINT ck_topups_posting CHECK (state = 'Succeeded' OR journal_entry_id IS NULL),
  CONSTRAINT ck_topups_settled CHECK (state = 'Pending' OR settled_at IS NOT NULL));

SELECT public.attach_set_updated_at('billing','topups');

-- R-19, as an index. Partial because a Pending session has no provider id yet and several may be
-- open at once for one driver.
CREATE UNIQUE INDEX IF NOT EXISTS ux_topups_provider_txn
  ON billing.topups(provider_transaction_id) WHERE provider_transaction_id IS NOT NULL;
CREATE UNIQUE INDEX IF NOT EXISTS ux_topups_provider_order
  ON billing.topups(provider_order_id) WHERE provider_order_id IS NOT NULL;
CREATE INDEX IF NOT EXISTS ix_topups_driver ON billing.topups(driver_id, created_at DESC);
-- D6' §7.1's "reconcile open orders by status poll": this is the queue that poll walks.
CREATE INDEX IF NOT EXISTS ix_topups_pending
  ON billing.topups(created_at) WHERE state = 'Pending';

COMMENT ON TABLE billing.topups IS
  'One gateway top-up session (D6'' §7.1/§7.2). The wallet is credited only on the callback, by a balanced journal entry (D-09). AL-05: method admits onepay and lankaqr only — bank transfer is not a top-up method.';
COMMENT ON COLUMN billing.topups.provider_transaction_id IS
  'R-19 dedupe key for the provider callback. UNIQUE where present, so a redelivered webhook credits the wallet once.';

-- -------------------------------------------------------------------------------------
-- (2) billing.outbox — wallet.debited / wallet.credited / wallet.low_balance
-- -------------------------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS billing.outbox (
  id BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
  -- The driver (or fleet) whose wallet moved, and the Kafka partition key. Keyed by the *owner*
  -- rather than by the journal entry: a debit and the credit that follows it must reach
  -- dispatch-svc's cache in the order they happened, and only the owner key guarantees that.
  aggregate_id UUID NOT NULL,
  event_type TEXT NOT NULL,                                   -- wallet.debited | wallet.credited | wallet.low_balance
  payload JSONB NOT NULL,
  created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  dispatched_at TIMESTAMPTZ);                                 -- set once the broker has acked

CREATE INDEX IF NOT EXISTS ix_billing_outbox_undispatched
  ON billing.outbox(id) WHERE dispatched_at IS NULL;

COMMENT ON TABLE billing.outbox IS
  'Transactional outbox for wallet.events (D6'' §2.4, R-13). The row commits with the postings it describes, so no balance change can be published that was rolled back — and none can be lost, which would leave dispatch-svc gating on a stale cache (D-08).';

-- -------------------------------------------------------------------------------------
-- (3) billing.command_log — R-14 replay for the money POSTs
-- -------------------------------------------------------------------------------------
-- Shape is 0307 exactly (0603 minus `ride_id`). The journal's own `idempotency_key` already makes
-- a *posting* single-shot; this is the other half — it replays the **response**, so a client that
-- retried a top-up initiate gets the same `topupId` and the same gateway redirect instead of a
-- second session against the same money.
CREATE TABLE IF NOT EXISTS billing.command_log (
  idempotency_key TEXT PRIMARY KEY,
  actor_type TEXT NOT NULL,
  actor_id UUID,
  command TEXT NOT NULL,
  request_hash BYTEA NOT NULL,
  response_status SMALLINT,
  response_body JSON,
  response_content_type TEXT,
  ts TIMESTAMPTZ NOT NULL DEFAULT now());

CREATE INDEX IF NOT EXISTS ix_billing_command_log_inflight
  ON billing.command_log(ts) WHERE response_status IS NULL;

COMMENT ON TABLE billing.command_log IS
  'R-14 idempotent replay for wallet-svc''s POST mutations (D3'' §0). 5xx is never stored, so a retry re-executes rather than replaying a failure.';

-- -------------------------------------------------------------------------------------
-- The idempotency-key spellings this service composes, recorded beside the three 1101 already
-- documents. They are business facts, never random values, so a retry collides in the ledger
-- instead of double-posting:
--   topup             'topup:'            || topup_id
--   voucher purchase  'voucher_purchase:' || voucher_purchase_id
--   driver transfer   'driver_transfer:'  || credit_transfer_id
-- Each of the three ids is minted before the entry is written and is unique, so the key is too.
-- -------------------------------------------------------------------------------------
COMMENT ON COLUMN billing.journal_entries.idempotency_key IS
  'D-05 double-apply guard for penalties uses exactly penalty_id || '':'' || rideId (D5'' §7.1) — C004 left the real guard here, so that spelling must not drift. wallet-svc (C046) composes: topup:{topupId}, voucher_purchase:{purchaseId}, driver_transfer:{transferId}.';
