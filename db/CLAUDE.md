# Database Migration Conventions

- **Stack:** PostgreSQL 16 + PostGIS + TimescaleDB + pgcrypto + citext, applied by **DbUp**
  (`backend/src/MageRide.Migrations`) as ordered, idempotent `.sql` scripts. Never `dotnet ef`
  (AL-53, D7' §1).
- **`specs/D4_mageride_data_model.md` and `specs/server_db_schema.md` are the source of truth.**
  Where they disagree, D4' wins (it is the canonical data model; server_db_schema.md is a
  consolidated mirror) — and record the divergence in `build/progress.md`.

## Writing a migration

- One file per cohesive group of tables: `NNNN__<schema>_<topic>.sql`, four-digit prefix,
  double underscore. The prefix is the apply order and is never reused or renumbered — a
  released script is immutable, corrections ship as a new file.
- Ranges: `00xx` bootstrap · `01xx` iam · `02xx` config · `03xx` registry · `04xx` prov ·
  `05xx` trips · `06xx` rides · `07xx` dispatch · `08xx` reputation · `09xx` safety ·
  `10xx` fares · `11xx` billing · `12xx` subscription · `13xx` comms/docs/support/content/
  audit/pdpa · `14xx` spatial/transit/analytics · `18xx` telemetry · `19xx` seed /
  reference data (§20).
- A file may create objects in another schema when a foreign key forces the order; name it for
  what it creates and say why in the header (see `0302__iam_fleet_members.sql`).
- **Every script must be re-runnable**: `CREATE TABLE IF NOT EXISTS`, `CREATE INDEX IF NOT
  EXISTS`, `ADD COLUMN IF NOT EXISTS`, `CREATE OR REPLACE`, `INSERT … ON CONFLICT DO NOTHING`.
  The journal is not the safety net — `migrate-verify.sh` re-runs every script with the journal
  disabled specifically to catch a script that is not.
- `TIMESTAMPTZ` for every temporal column. A business-date `DATE` column carries an
  `Asia/Colombo` `tz_at TIMESTAMPTZ` audit companion (D-38); the verify script enforces this.
- A range-partitioned table ships an `ensure_<table>_partition(DATE)` helper plus a rolling
  window of partitions, and **no `DEFAULT` partition** — a default that has collected
  out-of-range rows blocks `CREATE ... PARTITION OF` for the period those rows belong to.
  See `0503__trips_position_samples.sql`. Partition bounds are built as explicit `TIMESTAMPTZ`
  values, never bare date literals, which would resolve against the session `TimeZone`.
- Money is integer minor units (`*_minor`), enumerations are `TEXT` + `CHECK` (never a PG enum
  type), and every table with an `updated_at` calls
  `SELECT public.attach_set_updated_at('<schema>','<table>');`.
  The verify enforces that every `*_minor` column is an integer type with a `>= 0` CHECK and
  every `currency` column defaults to `'LKR'`. The only exemption is the five signed ledger
  columns §0 names (`billing.accounts.balance_minor`, `journal_postings.amount_minor`, and the
  two `wallets` / `wallet_transactions` mirrors) — they are BIGINT and may be negative.
- **A service with idempotent POSTs owns its own `command_log`** (`iam` 0104, `registry` 0307,
  `dispatch` 0710), shaped like `rides.command_log` (0603) minus the aggregate-id column. D4' §5
  prints DDL for `rides` only; pointing a second bounded context at that table would give two
  services one shared primary key, so a registration and a ride could collide on an identical
  client-generated `Idempotency-Key`. All three are raised as micro-change-sets in
  `build/progress.md`.
- A seed `INSERT` is re-runnable or it is wrong: use `ON CONFLICT DO NOTHING` where a key
  exists, `WHERE NOT EXISTS` where the PK is a generated UUID, and pin any column that would
  otherwise default to `now()` into the conflict target (see `1901`'s `effective_from`).
  Seeds are admin-editable in production — a re-run must never revert an operator's change.
- `transit_staging.gtfs_*` mirrors `transit.gtfs_*` via `LIKE ... INCLUDING DEFAULTS INCLUDING
  CONSTRAINTS INCLUDING COMMENTS` rather than a copied column list, because the AL-54 activation
  swap renames one schema into the other and silent column drift would corrupt the live feed.
  Keys, indexes and FKs are not copied by `LIKE`; declare them explicitly, pointing *within*
  `transit_staging`. The verify asserts the two sides stay column-for-column identical.

## TimescaleDB (`telemetry`, `18xx`)

Four rules the printed DDL in D4' §17 / `server_db_schema.md` §18 does not survive. All four
were found by running it; see the C006 handoff in `build/progress.md`.

- **A unique index on a hypertable must contain every partitioning column.** `telemetry.positions`
  is partitioned by `sample_ts` *and* `vehicle_id`, so the specs' `UNIQUE (vehicle_id, seq)` is
  rejected outright and the replay-dedupe key is `(vehicle_id, seq, sample_ts)`.
- **Row-level security and compression are mutually exclusive.** TimescaleDB refuses
  `ENABLE ROW LEVEL SECURITY` on a hypertable with columnstore enabled, and refuses columnstore
  on a table with row security. Compression wins on `telemetry.positions`; fleet scoping is a
  `security_barrier` view (`telemetry.positions_fleet`) that the fleet role holds its only grant
  on. RLS cannot go on a continuous aggregate either — it is a view.
- **`CREATE MATERIALIZED VIEW … WITH (timescaledb.continuous)` must say `WITH NO DATA`**, because
  the runner gives every script a transaction (`WithTransactionPerScript`) and `WITH DATA` cannot
  run inside one. `refresh_continuous_aggregate` is likewise a procedure that needs its own
  statement outside a transaction.
- **Idempotency comes from the `if_not_exists` argument**, not from SQL syntax:
  `create_hypertable`, `add_compression_policy`, `add_retention_policy` and
  `add_continuous_aggregate_policy` all take one, and `CREATE MATERIALIZED VIEW IF NOT EXISTS`
  works for the aggregates themselves.

## Seeds that are not migrations (`db/seed/`)

DbUp applies **everything** in `db/migrations/` to every database, production included. A
script that invents an account, or that puts a row into a state the real service would refuse,
belongs in `db/seed/` instead and is applied by its own shell script — never by the `migrate`
container. `db/seed/skeleton.sql` (C021) is the first: it creates the walking skeleton's driver
and one APPROVED vehicle with no insurance document, which AL-10 forbids in production.

The §20 reference data (`iam.roles`, `config.operating_cities`, `billing.plans`,
`fares.tariffs`, notification templates) is the opposite case and stays in `19xx` migrations —
it is real data every environment needs.

## Running

```bash
# Against a running database
dotnet run --project backend/src/MageRide.Migrations -- \
  --connection "Host=localhost;Database=mageride;Username=postgres;Password=…"

dotnet run --project backend/src/MageRide.Migrations -- --what-if   # list pending, apply nothing

# One-shot container (compose `migrate` service)
docker build -f backend/src/MageRide.Migrations/Dockerfile -t mageride/migrate .
```

## Verify

```bash
bash infra/scripts/migrate-verify.sh
```

Starts a throwaway `timescale/timescaledb-ha:pg16`, applies everything, re-applies it with and
without the journal, then asserts the schema objects and that the constraints actually reject
bad rows. Requires Docker.
