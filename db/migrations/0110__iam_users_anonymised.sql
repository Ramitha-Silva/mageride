-- =====================================================================================
-- 0110 — iam: the column a PDPA erasure writes
-- Source: specs/server_db_schema.md §16 (pdpa) · ADD §6 admin-bff ("PDPA workflow (E-06):
--         erasure soft-anonymise within 30 d with statutory hold list") · E-06, US-1.8
--         backend/contracts/admin-bff.yaml PassengerRow.status
--
-- ⚠ Spec gap — micro-change-set, raised in the C065 handoff.
--
--   E-06's erasure is a **soft anonymisation**: the account row survives so that every ride,
--   payment, ledger posting and audit event that references it keeps resolving, and what is
--   removed is the identity on it. Nothing in §1 records that this happened. Two consequences,
--   both real:
--
--     (a) `admin-bff.yaml`'s `PassengerRow.status` carries a `deleted` value **nothing could
--         produce** — the C064 handoff records exactly that and leaves the enum alone for this
--         component to fill. Without a column the directory answers `active` for an account
--         somebody erased, which is the one answer that is certainly wrong.
--     (b) A second erasure request would re-anonymise an already-anonymised account and report
--         it as fresh work, and no read anywhere could tell the two apart.
--
--   `anonymised_at` is that fact and only that fact. Deliberately **not** a `status` column and
--   deliberately **not** `is_blocked`: blocking is a moderation decision (US-14.3) that an admin
--   can undo, and an erasure is neither undoable nor a sanction. Deriving "deleted" from a
--   timestamp is the same shape `registry.driver_profiles.verified_at` already uses for APPROVED.
--
--   **D4' §1 should carry this column.**
--
-- No default and no back-fill: every existing account is one nobody has erased, which is what
-- NULL already says.
-- =====================================================================================

ALTER TABLE iam.users
  ADD COLUMN IF NOT EXISTS anonymised_at TIMESTAMPTZ;

COMMENT ON COLUMN iam.users.anonymised_at IS
  'When a PDPA erasure (E-06) anonymised this account. The row survives so every ride, payment and audit event referencing it still resolves; the identifying columns do not. NULL = never erased.';

-- The PDPA fulfilment reads "is this account already erased" before doing anything, and the
-- three directories read it on every row to derive `deleted`. Partial because the answer is NULL
-- for effectively every account on the platform, so the index holds only the erased ones.
CREATE INDEX IF NOT EXISTS ix_users_anonymised
  ON iam.users(anonymised_at) WHERE anonymised_at IS NOT NULL;

COMMENT ON INDEX iam.ix_users_anonymised IS
  'E-06: the erased set, which is small by construction. Also what stops a second erasure request re-anonymising an account and reporting it as fresh work.';
