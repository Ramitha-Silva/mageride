-- =====================================================================================
-- 1901 — seed: tariffs, peak windows, daily-fee plans, voucher tiers
-- Source: server_db_schema.md §20 · D4' §19 · ADD §9.1 · URD US-9.19 · AL-09
--
-- config.operating_cities and iam.roles are also §20 seeds but landed with their tables in
-- C003 (0201 / 0101). billing.accounts platform rows landed with the ledger (1101), where
-- the singleton indexes that make them unique are declared.
--
-- Every INSERT is ON CONFLICT DO NOTHING: migrate-verify.sh re-runs every script with the
-- journal disabled, and these values are admin-editable in production — a re-run must
-- never revert an operator's change.
-- =====================================================================================

-- Mode C fare tariffs (§20, URD §8). One row per Mode-C-bookable vehicle type (AL-09).
-- Rs in minor units: 8000 = Rs 80.00 for the first km.
INSERT INTO fares.tariffs
  (vehicle_type, first_km_minor, per_km_minor, peak_surcharge_pct, night_surcharge_pct, effective_from)
VALUES
  ('motorbike',     8000,  6000, 20, 15, 'epoch'::timestamptz),
  ('three_wheeler',10000,  8000, 20, 15, 'epoch'::timestamptz),
  ('flex',         13000,  9000, 20, 15, 'epoch'::timestamptz),
  ('sedan',        15000, 10000, 20, 15, 'epoch'::timestamptz),
  ('mini_van',     15000, 11000, 20, 15, 'epoch'::timestamptz),
  ('van',          15000, 12000, 20, 15, 'epoch'::timestamptz)
ON CONFLICT (vehicle_type, effective_from) DO NOTHING;
-- effective_from is pinned to the epoch rather than now(): the column defaults to now(),
-- which would make every re-run a new tariff version instead of a no-op, and the UNIQUE
-- key would not catch it. Truck / mini_truck (package delivery, Epic 20) carry no seeded
-- tariff — §20 leaves delivery rates to be configured before such a vehicle can be booked.

-- Peak and night windows (§20), evaluated in Asia/Colombo (D-38). The night window wraps
-- midnight, which fare-svc must handle — see fares.peak_windows.end_local.
INSERT INTO fares.peak_windows (kind, start_local, end_local, multiplier_pct)
  SELECT v.kind, v.start_local::time, v.end_local::time, v.multiplier_pct
    FROM (VALUES
      ('peak',  '07:00', '09:00', 20),
      ('peak',  '17:00', '19:00', 20),
      ('night', '22:00', '05:00', 15)
    ) AS v(kind, start_local, end_local, multiplier_pct)
   WHERE NOT EXISTS (
     SELECT 1 FROM fares.peak_windows p
      WHERE p.kind = v.kind
        AND p.start_local = v.start_local::time
        AND p.end_local = v.end_local::time);

-- Daily-fee plans (§20, ADD §9.1). Seven rate tiers across eight rows: Mode A is free and
-- has two vehicle types, then the six Mode C tiers.
INSERT INTO billing.plans (vehicle_type, daily_fee_minor, mode) VALUES
  ('bus',              0, 'A'),
  ('train',            0, 'A'),
  ('motorbike',     5000, 'C'),
  ('three_wheeler',10000, 'C'),
  ('flex',         15000, 'C'),
  ('sedan',        20000, 'C'),
  ('mini_van',     25000, 'C'),
  ('van',          30000, 'C')
ON CONFLICT (vehicle_type) DO NOTHING;
-- Truck / mini_truck are package-delivery types with admin-configured rates (§20) — no
-- default row, so a delivery vehicle cannot go online until Finance sets one.

-- Bulk-voucher purchase discounts (US-9.19, AL-01).
--
-- The five denominations are pinned by URD US-9.19 (Rs 1,000 / 2,000 / 3,000 / 5,000 /
-- 10,000). The percentages are NOT: every spec that mentions them says the rate is
-- admin-configurable per denomination, with larger denominations typically earning more,
-- and gives exactly one worked example — ADD §9.1 and URD US-9.19 both use
-- "100000 → 1000 bps = 10% = pay 90,000, get 100,000". That example is seeded literally;
-- the ladder above it is a defensible default for Finance to edit in SCR-AP-007, not a
-- spec value. See the C005 handoff.
INSERT INTO billing.voucher_discount_tiers (denomination_minor, discount_bps) VALUES
  ( 100000, 1000),   -- Rs  1,000 → 10.0%  (the spec's worked example: pay Rs 900)
  ( 200000, 1100),   -- Rs  2,000 → 11.0%
  ( 300000, 1200),   -- Rs  3,000 → 12.0%
  ( 500000, 1300),   -- Rs  5,000 → 13.0%
  (1000000, 1500)    -- Rs 10,000 → 15.0%
ON CONFLICT (denomination_minor) DO NOTHING;
