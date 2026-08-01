-- =====================================================================================
-- 0109 — iam: the keyset index the passenger and driver directories page on
-- Source: specs/D3_mageride_api_contracts.md Δ 2026-06-28 (AL-40 passenger directory,
--                                                          AL-41 driver directory)
--         specs/D5_mageride_business_logic.md BR-28.8 · URD US-24.9 / US-24.10
--         backend/contracts/admin-bff.yaml searchPassengers / searchDrivers
--
-- C064. Both people-directories page over `iam.users` by (created_at DESC, id DESC) — the
-- account's own join order, which is what SCR-AP-010/012 list newest-first and what the
-- opaque cursor encodes. Neither can use an existing index: 0101 gives the table a PK and
-- two UNIQUEs on the credentials, and nothing on the ordering key.
--
-- **`role` leads the index because it is what separates the two directories.** URD §2.3's
-- passenger directory is "search for a passenger" and the driver directory is "search
-- verified drivers"; `iam.users.role` is the account's primary role (0101's own comment)
-- and answers exactly that question, while `iam.user_roles` answers a different one — what
-- this account is *permitted* to do, which is where an internal operator granted
-- `passenger` for a test would appear and where a CSR looking for a rider must not find
-- them.
--
-- DESC on both trailing columns rather than relying on a backwards scan: the query is
-- `ORDER BY created_at DESC, id DESC` with a row-wise `(created_at, id) < (cursor)`
-- predicate, and a matching index gives that a plain forward scan under the LIMIT — which
-- is what keeps the first page's cost independent of how many accounts exist (US-24.9's
-- 10k-row directory, DoD p95 < 500 ms).
-- =====================================================================================

CREATE INDEX IF NOT EXISTS ix_users_role_created
  ON iam.users(role, created_at DESC, id DESC);

COMMENT ON INDEX iam.ix_users_role_created IS
  'AL-40/AL-41: the passenger and driver directory keyset (role filter + created_at/id cursor). Ordering key only — the search criteria (name/mobile/email/NIC) are substring filters applied over the page.';
