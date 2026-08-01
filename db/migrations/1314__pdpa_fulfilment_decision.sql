-- =====================================================================================
-- 1314 — pdpa: who decided a request, and why it was refused
-- Source: specs/D3_mageride_api_contracts.md "pdpa-svc (via admin-bff)" —
--           POST /v1/admin/pdpa/{id}/fulfill · /reject
--         backend/contracts/admin-bff.yaml rejectPdpaRequest (ReasonBody, required)
--         specs/server_db_schema.md §16 · E-06, D-35
--
-- ⚠ Spec gap — micro-change-set, raised in the C065 handoff.
--
--   §16 gives `pdpa.requests` a `Rejected` status and D3' gives it a `/reject` route whose body
--   the contract types as a **required** reason — and there is no column to put the reason in.
--   `hold_reason` is not it: `ck_pdpa_requests_hold` ties that column to `FulfilledHold`, which
--   is the opposite outcome (data was erased, and a statute forced a subset to be kept). Storing
--   a refusal there would make "why was some data retained" and "why was this refused outright"
--   one field, and the SLA queue could no longer tell a partial fulfilment from a denial.
--
--   `decided_by` is the same gap on the other axis. D-35's audit row records who called the
--   route, and that row is the immutable trail — but the request itself is what the data subject
--   reads back on `GET /v1/pdpa/{requestId}` and what the Finance/Admin queue renders, and
--   neither can join `audit.events` to find an operator. Two rows for one action is this
--   platform's documented shape (admin-bff's own CLAUDE.md); this is the second one.
--
--   **D4' §11-16 and server_db_schema.md §16 should carry both columns.**
--
-- `fulfilled_at` already exists and is reused for a rejection: it is the instant the request
-- stopped being open, which is what the 30-day SLA is measured against, and a second
-- `rejected_at` would be a column that is NULL whenever the first one is not.
-- =====================================================================================

ALTER TABLE pdpa.requests
  ADD COLUMN IF NOT EXISTS decided_by UUID REFERENCES iam.users(id);

ALTER TABLE pdpa.requests
  ADD COLUMN IF NOT EXISTS decision_reason TEXT;

DO $$
BEGIN
  -- A refusal must say why — the subject is shown it (E-06) — and nothing else may carry a
  -- reason it did not earn. Same shape as billing.payouts' ck_payouts_failure_reason (1109).
  IF NOT EXISTS (SELECT 1 FROM pg_constraint
                  WHERE conrelid = 'pdpa.requests'::regclass
                    AND conname = 'ck_pdpa_requests_rejection') THEN
    ALTER TABLE pdpa.requests
      ADD CONSTRAINT ck_pdpa_requests_rejection
      CHECK ((status = 'Rejected') = (decision_reason IS NOT NULL));
  END IF;

  -- A decided request names its decider. Left unconstrained for the two open statuses, which
  -- have not been decided by anybody yet.
  IF NOT EXISTS (SELECT 1 FROM pg_constraint
                  WHERE conrelid = 'pdpa.requests'::regclass
                    AND conname = 'ck_pdpa_requests_decided') THEN
    ALTER TABLE pdpa.requests
      ADD CONSTRAINT ck_pdpa_requests_decided
      CHECK (status IN ('Received','InProgress') OR (decided_by IS NOT NULL AND fulfilled_at IS NOT NULL));
  END IF;
END $$;

COMMENT ON COLUMN pdpa.requests.decided_by IS
  'The operator who fulfilled or rejected this request. audit.events holds the immutable record of the action; this is what GET /v1/pdpa/{requestId} and the admin queue render, neither of which can join the audit log.';
COMMENT ON COLUMN pdpa.requests.decision_reason IS
  'Why a request was Rejected, shown to the data subject (E-06). NOT hold_reason: that names the statute that forced a subset to be RETAINED by an erasure that otherwise succeeded.';

-- The admin queue is "everything still open, soonest deadline first" — which is exactly
-- ix_pdpa_requests_due (1306). What that index cannot serve is the second tab: the decided
-- history, newest first, which is how an auditor checks the 30-day obligation was met.
CREATE INDEX IF NOT EXISTS ix_pdpa_requests_decided
  ON pdpa.requests(fulfilled_at DESC) WHERE fulfilled_at IS NOT NULL;

COMMENT ON INDEX pdpa.ix_pdpa_requests_decided IS
  'E-06: the closed half of the admin queue. ix_pdpa_requests_due holds the open half; between them the SLA is answerable without a scan.';
