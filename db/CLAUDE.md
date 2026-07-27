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
  audit/pdpa · `14xx` spatial/transit/analytics · `15xx` telemetry.
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
