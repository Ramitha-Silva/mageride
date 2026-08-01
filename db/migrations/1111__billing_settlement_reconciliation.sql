-- =====================================================================================
-- 1111 — billing: the gateway settlement reconciliation read
-- Source: specs/D6_mageride_integration.md §7.1/§7.2 ("OnePay/LankaQR gateway settlement
--           reconciliation (exceptions → Finance queue in Admin Portal)")
--         specs/architecture-design-document.md §6 wallet-svc · AL-05 · D-12 · R-19
--         specs/D2_mageride_ui_spec.md SCR-AP-006 · build/manifest.yaml C065
--
-- C065. `billing.topups` (1107) is the only table on this platform that records a gateway
-- settlement — AL-57 removed `onepay` and platform-merchant `lankaqr` as *ride* payment methods,
-- so what OnePay and LankaQR settle is wallet top-ups and nothing else. 1107 indexes it three
-- ways: by driver, by provider reference, and the open sessions the §7.1 status poll walks. All
-- three start from a session; SCR-AP-006 starts from a **rail and a day** ("what did OnePay
-- settle yesterday, and does it agree with the ledger"), which none of them can serve as a
-- prefix — so the reconciliation view would scan every top-up the platform has ever taken.
--
-- `(method, created_at DESC)`: the screen is one rail at a time, most recent first, and the same
-- index answers the per-day rollup and the exception queue's scan of a bounded window.
--
-- No new table and no new column. In particular there is **no bank-transfer reconciliation
-- queue** (AL-05) — 1107 states that as an absence and `ck_topups_method` enforces it, and this
-- file adds nothing that could hold one.
-- =====================================================================================

CREATE INDEX IF NOT EXISTS ix_topups_method_created
  ON billing.topups(method, created_at DESC);

COMMENT ON INDEX billing.ix_topups_method_created IS
  'D6'' §7.2 gateway settlement reconciliation (SCR-AP-006, C065): per-rail, per-day totals and the exception queue. The 1107 indexes all start from one session; this one starts from a rail and a window.';
