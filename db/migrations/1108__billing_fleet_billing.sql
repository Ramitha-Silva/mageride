-- =====================================================================================
-- 1108 — billing: the fleet invoice's per-vehicle breakdown, the fleet wallet's top-up
--                 sessions, this plane's outbox and its replay log
-- Source: server_db_schema.md §10 · D4' §10 · ADD §6 (`fleet-billing-svc`), §9.1 ·
--         URD Epic 13 §13.G (US-13.10, US-13.10b) · D5' §2.1 · AL-03, AL-05, D-09, D-38,
--         R-13, R-14, R-19
--
-- Owned by C060 (fleet-billing-svc). 1106 gave the platform one row per fleet per month and
-- nothing else; everything a consolidated invoice actually needs is here. Six changes, six
-- gaps:
--
--   (1) **The journal has no kind a fleet's monthly platform fee can be posted under.**
--       `billing.fleet_invoices.journal_entry_id` has existed since 1106 and
--       `ck_journal_entries_kind` (1101) admits ten values, none of which is it — so the
--       column could never have been filled and C047's handoff recorded the Mode B
--       consolidation as "handed to C060 unledgered". ADD §6 is explicit that
--       fleet-billing-svc "posts to the same `billing` ledger with `owner_type='fleet'`",
--       and the C060 definition of done is that an invoice's lines "post to a balanced
--       journal entry". `adjustment` was the alternative and is wrong: it is the Finance
--       queue's correction kind (US-14.11), and netting the platform's largest recurring
--       revenue line into it would make revenue and corrections one number for ever.
--
--   (2) **`billing.fleet_invoices.status` cannot hold the state the contract returns.**
--       `fleet.yaml`'s `FleetInvoice.status` enum is `[FREE, DUE, PAID, OVERDUE]` and
--       1106's CHECK admits three of those four. Without OVERDUE there is nowhere to record
--       that dunning has begun, and the Fleet Portal's billing screen has no state to draw
--       between "issued" and "settled".
--
--   (3) **An invoice has a total and no lines.** US-13.10 asks for "a single consolidated
--       monthly invoice with a per-vehicle line breakdown (so the operator pays once, but
--       the amount is the sum of per-Mode-B-vehicle monthly fees)". Deriving the breakdown
--       live from `billing.monthly_subscriptions` ⋈ `registry.fleet_vehicles` would make a
--       *settled* invoice's lines change under it — a vehicle that leaves the fleet next
--       week would leave the month it was billed for, and Σ lines would stop equalling
--       `total_minor`. The lines are therefore snapshotted at generation and the invoice is
--       immutable evidence.
--
--   (4) **A fleet top-up has nowhere to live.** `billing.topups` (1107) is wallet-svc's and
--       its `driver_id UUID NOT NULL REFERENCES iam.users(id)` is a driver, not an
--       organisation; a fleet's session has a fleet, an account, and the Owner who started
--       it. Same shape as 1107's argument (1), one level up. AL-05 is a CHECK here too.
--
--   (5) **The dunning signal has a producer and two named consumers and no table.** C060's
--       deliverable is "dunning / overdue signalling to the Fleet Portal and
--       notification-svc". `billing.outbox` (1107) belongs to wallet-svc and drains to
--       `wallet.events`; one table cannot serve two dispatchers publishing to two topics.
--       Keyed by fleetId onto `fleet.events`, which C044 opened for exactly this kind of
--       organisation-scoped fact.
--
--   (6) **R-14 needs a per-service command log** and `billing.command_log` (1107) is
--       wallet-svc's, whose primary key is the bare idempotency key. Two services sharing it
--       would let one client's `Idempotency-Key` collide across a service boundary and be
--       served the other service's response. Thirteenth bounded context to need one.
--
-- AL-03 is visible in this file as an absence: nothing here has a `mode` column, because a
-- line can only exist for a vehicle `billing.monthly_subscriptions` already raised a charge
-- for, and 1104's writer filters `v.mode = 'B'`. A Mode A vehicle is free and a Mode C
-- vehicle is never fleet-owned, so neither can reach a line — held by the row not existing
-- rather than by a filter somebody could forget.
-- =====================================================================================

-- -------------------------------------------------------------------------------------
-- (1) The journal kind. DROP then ADD, which is what keeps the script re-runnable —
-- ALTER TABLE has no ADD CONSTRAINT IF NOT EXISTS.
-- -------------------------------------------------------------------------------------
ALTER TABLE billing.journal_entries DROP CONSTRAINT IF EXISTS ck_journal_entries_kind;
ALTER TABLE billing.journal_entries ADD CONSTRAINT ck_journal_entries_kind
  CHECK (kind IN ('topup','daily_fee','trip_payment','penalty_settle','adjustment',
                  'tip_payout','payment_refund','overpaid_reversal','voucher_purchase',
                  'driver_transfer',
                  -- Δ C060: the consolidated monthly per-Mode-B-vehicle charge to a fleet
                  -- (AL-03). Still no 'reseller_commission' — AL-01 removed it and this
                  -- file does not put it back.
                  'fleet_invoice'));

COMMENT ON COLUMN billing.journal_entries.idempotency_key IS
  'D-05 double-apply guard for penalties uses exactly penalty_id || '':'' || rideId (D5'' §7.1) — C004 left the real guard here, so that spelling must not drift. wallet-svc (C046) composes: topup:{topupId}, voucher_purchase:{purchaseId}, driver_transfer:{transferId}. fleet-billing-svc (C060) composes: fleet_invoice:{invoiceId}, fleet_topup:{fleetTopupId}.';

-- -------------------------------------------------------------------------------------
-- (2) OVERDUE, plus the three instants an invoice's life needs
-- -------------------------------------------------------------------------------------
ALTER TABLE billing.fleet_invoices DROP CONSTRAINT IF EXISTS ck_fleet_invoices_status;
ALTER TABLE billing.fleet_invoices ADD CONSTRAINT ck_fleet_invoices_status
  CHECK (status IN ('FREE','DUE','PAID','OVERDUE'));

-- When the fleet has to have paid by. NO SPEC PINS A PAYMENT TERM: `FleetBilling:PaymentTerm`
-- decides it and the invoice records the answer, so moving the setting cannot retro-date an
-- invoice that has already been issued.
ALTER TABLE billing.fleet_invoices ADD COLUMN IF NOT EXISTS due_at TIMESTAMPTZ;
-- When dunning began. Distinct from `due_at`: the sweep may be down when a term lapses, and
-- "we told them on the 9th" is not "it was due on the 8th". Written once and never moved.
ALTER TABLE billing.fleet_invoices ADD COLUMN IF NOT EXISTS overdue_at TIMESTAMPTZ;
-- When the *last* reminder went. Its own column rather than moving `overdue_at`, because the two
-- answer different questions and one column would lose the first: an invoice stays overdue until
-- it is paid, and `FleetBilling:DunningInterval` reads this to decide whether to say so again.
ALTER TABLE billing.fleet_invoices ADD COLUMN IF NOT EXISTS last_dunned_at TIMESTAMPTZ;
ALTER TABLE billing.fleet_invoices ADD COLUMN IF NOT EXISTS settled_at TIMESTAMPTZ;
ALTER TABLE billing.fleet_invoices ADD COLUMN IF NOT EXISTS updated_at TIMESTAMPTZ NOT NULL DEFAULT now();

SELECT public.attach_set_updated_at('billing','fleet_invoices');

-- Only a settled invoice may carry a posting. 1106's ck_fleet_invoices_free already forbids a
-- FREE one; this closes the other three, so a DUE or OVERDUE row can never point at money that
-- moved. The pair is what makes `journal_entry_id IS NOT NULL` readable as "this was paid".
ALTER TABLE billing.fleet_invoices DROP CONSTRAINT IF EXISTS ck_fleet_invoices_posting;
ALTER TABLE billing.fleet_invoices ADD CONSTRAINT ck_fleet_invoices_posting
  CHECK (status = 'PAID' OR journal_entry_id IS NULL);

ALTER TABLE billing.fleet_invoices DROP CONSTRAINT IF EXISTS ck_fleet_invoices_settled;
ALTER TABLE billing.fleet_invoices ADD CONSTRAINT ck_fleet_invoices_settled
  CHECK (status <> 'PAID' OR (journal_entry_id IS NOT NULL AND settled_at IS NOT NULL));

-- The dunning sweep's queue: everything issued, still owed, and past its term.
CREATE INDEX IF NOT EXISTS ix_fleet_invoices_dunning
  ON billing.fleet_invoices(due_at) WHERE status = 'DUE';
-- The Fleet Portal's own list, newest month first.
CREATE INDEX IF NOT EXISTS ix_fleet_invoices_fleet_period
  ON billing.fleet_invoices(fleet_id, period_month DESC);

COMMENT ON COLUMN billing.fleet_invoices.status IS
  'FREE (every vehicle in its first month, or a Mode-A-only fleet — total 0, never posts) · DUE (issued, unpaid) · OVERDUE (DUE past due_at; dunning has been signalled) · PAID (settled against the fleet wallet by a balanced fleet_invoice entry). fleet.yaml FleetInvoice.status, verbatim.';

-- -------------------------------------------------------------------------------------
-- (3) billing.fleet_invoice_lines — US-13.10's per-vehicle breakdown, snapshotted
-- -------------------------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS billing.fleet_invoice_lines (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  invoice_id UUID NOT NULL REFERENCES billing.fleet_invoices(id) ON DELETE CASCADE,
  vehicle_id UUID NOT NULL REFERENCES registry.vehicles(id),
  -- The charge this line consolidates. FK rather than a copied amount alone, so the invoice
  -- can be reconciled back to the per-vehicle row subscription-svc (C047) raised — and so a
  -- charge cannot be consolidated onto two invoices.
  monthly_subscription_id UUID NOT NULL REFERENCES billing.monthly_subscriptions(id),
  -- Denormalised at generation on purpose: an operator re-reading last March's invoice must
  -- see the plate and the type as they were billed, not as they are now. A vehicle can be
  -- re-plated, re-typed or leave the organisation entirely.
  registration_number TEXT NOT NULL,
  vehicle_type TEXT NOT NULL,
  amount_minor INTEGER NOT NULL CHECK (amount_minor >= 0),
  currency CHAR(3) NOT NULL DEFAULT 'LKR',
  -- The per-vehicle charge's own status at generation: FREE (this vehicle's first month) or
  -- DUE. A FREE line is worth 0 and is still printed — it is how the operator sees that the
  -- vehicle was considered and why it cost nothing.
  status TEXT NOT NULL CONSTRAINT ck_fleet_invoice_lines_status
    CHECK (status IN ('FREE','DUE')),
  created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  -- One line per vehicle per invoice: re-running generation for a month must not append a
  -- second copy of a vehicle already on the invoice (the C060 idempotence requirement).
  CONSTRAINT ux_fleet_invoice_lines_vehicle UNIQUE (invoice_id, vehicle_id),
  -- And one invoice per raised charge, ever. Without it a charge could be consolidated onto
  -- two fleets' invoices after a vehicle transfer and the platform would collect twice.
  CONSTRAINT ux_fleet_invoice_lines_charge UNIQUE (monthly_subscription_id),
  CONSTRAINT ck_fleet_invoice_lines_free CHECK (status <> 'FREE' OR amount_minor = 0));

CREATE INDEX IF NOT EXISTS ix_fleet_invoice_lines_invoice
  ON billing.fleet_invoice_lines(invoice_id);

COMMENT ON TABLE billing.fleet_invoice_lines IS
  'US-13.10''s per-vehicle breakdown of one consolidated fleet invoice, snapshotted at generation. Σ amount_minor = billing.fleet_invoices.total_minor, by construction and by test. Mode A vehicles never appear: billing.monthly_subscriptions only carries Mode B rows (1104), so there is no line for them to be excluded from.';

-- -------------------------------------------------------------------------------------
-- (4) billing.fleet_topups — one row per fleet-wallet gateway session (US-13.10b, AL-05)
-- -------------------------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS billing.fleet_topups (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  fleet_id UUID NOT NULL REFERENCES registry.fleets(id) ON DELETE CASCADE,
  -- Resolved at initiate, like 1107's: the account is what the credit posts to, and looking
  -- it up twice is how a top-up lands in the wrong wallet after an account rebuild.
  account_id UUID NOT NULL REFERENCES billing.accounts(id),
  -- The Fleet Owner who started it. An organisation cannot press a button; a person can, and
  -- a receipt with no purchaser is not a receipt.
  initiated_by UUID NOT NULL REFERENCES iam.users(id),
  -- AL-05, as a constraint, in the second place it has to hold. OnePay covers both the card
  -- and the OnePay-wallet rails (D6' §7.1). There is no bank transfer and no receipt column.
  method TEXT NOT NULL CONSTRAINT ck_fleet_topups_method CHECK (method IN ('onepay','lankaqr')),
  amount_minor BIGINT NOT NULL CONSTRAINT ck_fleet_topups_amount CHECK (amount_minor > 0),
  currency CHAR(3) NOT NULL DEFAULT 'LKR',
  state TEXT NOT NULL DEFAULT 'Pending' CONSTRAINT ck_fleet_topups_state
    CHECK (state IN ('Pending','Succeeded','Failed')),
  provider_order_id TEXT,
  -- R-19, one level up from 1107: the gateway's own id and the callback's dedupe key.
  provider_transaction_id TEXT,
  journal_entry_id UUID REFERENCES billing.journal_entries(id),
  failure_reason TEXT,
  created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  updated_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  settled_at TIMESTAMPTZ,
  CONSTRAINT ck_fleet_topups_posting CHECK (state = 'Succeeded' OR journal_entry_id IS NULL),
  CONSTRAINT ck_fleet_topups_settled CHECK (state = 'Pending' OR settled_at IS NOT NULL));

SELECT public.attach_set_updated_at('billing','fleet_topups');

-- R-19. Partial because a Pending session has no provider id yet and an organisation may have
-- several open at once. The two are in one key space with wallet-svc's driver sessions only in
-- the sense that a provider never reuses a transaction id; the tables are separate because
-- the owners are.
CREATE UNIQUE INDEX IF NOT EXISTS ux_fleet_topups_provider_txn
  ON billing.fleet_topups(provider_transaction_id) WHERE provider_transaction_id IS NOT NULL;
CREATE UNIQUE INDEX IF NOT EXISTS ux_fleet_topups_provider_order
  ON billing.fleet_topups(provider_order_id) WHERE provider_order_id IS NOT NULL;
CREATE INDEX IF NOT EXISTS ix_fleet_topups_fleet
  ON billing.fleet_topups(fleet_id, created_at DESC);

COMMENT ON TABLE billing.fleet_topups IS
  'One gateway top-up session for a fleet wallet (US-13.10b). The wallet is credited only on the signed provider callback, through wallet-svc''s ledger seam, by a balanced journal entry (D-09). AL-05: method admits onepay and lankaqr only — bank transfer is not a top-up method anywhere on this platform.';

-- -------------------------------------------------------------------------------------
-- (5) billing.fleet_outbox — fleet.invoice_issued / _paid / _overdue on `fleet.events`
-- -------------------------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS billing.fleet_outbox (
  id BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
  -- The fleet, and the Kafka partition key. Two verdicts about one organisation have to
  -- arrive in the order they were reached, or a paid notice can precede the invoice it
  -- settles — the same argument C044 makes for `fleet.health_alert`.
  aggregate_id UUID NOT NULL,
  event_type TEXT NOT NULL,           -- fleet.invoice_issued | fleet.invoice_paid | fleet.invoice_overdue
  payload JSONB NOT NULL,
  created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
  dispatched_at TIMESTAMPTZ);

CREATE INDEX IF NOT EXISTS ix_billing_fleet_outbox_undispatched
  ON billing.fleet_outbox(id) WHERE dispatched_at IS NULL;

COMMENT ON TABLE billing.fleet_outbox IS
  'Transactional outbox for fleet-billing-svc''s share of fleet.events (D6'' §2.4, R-13). Separate from billing.outbox (1107), which is wallet-svc''s and drains to wallet.events: one table cannot serve two dispatchers publishing to two topics.';

-- -------------------------------------------------------------------------------------
-- (6) billing.fleet_command_log — R-14 replay for this service's POSTs
-- -------------------------------------------------------------------------------------
-- Shape is 0307 exactly (0603 minus `ride_id`). The ledger's own `idempotency_key` already
-- makes a *posting* single-shot; this replays the **response**, so a retried top-up initiate
-- gets the same `topupId` and the same gateway hand-off rather than a second session against
-- the same money.
CREATE TABLE IF NOT EXISTS billing.fleet_command_log (
  idempotency_key TEXT PRIMARY KEY,
  actor_type TEXT NOT NULL,
  actor_id UUID,
  command TEXT NOT NULL,
  request_hash BYTEA NOT NULL,
  response_status SMALLINT,
  response_body JSON,
  response_content_type TEXT,
  ts TIMESTAMPTZ NOT NULL DEFAULT now());

CREATE INDEX IF NOT EXISTS ix_billing_fleet_command_log_inflight
  ON billing.fleet_command_log(ts) WHERE response_status IS NULL;

COMMENT ON TABLE billing.fleet_command_log IS
  'R-14 idempotent replay for fleet-billing-svc''s POST mutations (D3'' §0). Separate key space from billing.command_log (wallet-svc''s, 1107): two services sharing one primary key would let a client''s Idempotency-Key collide across a service boundary.';
