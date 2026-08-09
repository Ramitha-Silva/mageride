# Runbook — Postgres replication lag (ADD §13.4 bullet 4)

**Alert:** `PostgresReplicationLag` · **Severity:** page (on-call DBA)
**Dashboard:** Grafana → `mageride-postgres`

> ADD §13.4: *"Postgres replication lag > 30 s: failover risk; page on-call DBA."*

---

## First action

**Stop routing reads to the replica.** ADD §9.3 sends read-heavy queries there; while it is 30
seconds behind, every one of them is answering from stale data — and read-after-write consistency,
which query-svc tracks with `mageride_query_replica_reads_total`, is already violated.

```bash
# Take the replica out of the read path (production).
kubectl -n mageride set env deployment/query-svc ConnectionStrings__PostgresReplica-
```

The primary absorbs the extra load; that is the cheaper failure.

---

## What is measured

`pg_replication_lag_seconds` from postgres_exporter, guarded by `pg_replication_is_replica == 1`.
The guard is load-bearing: the metric is derived from `pg_last_xact_replay_timestamp()`, which only
means anything on a standby. **On a primary it reads 0 whatever is happening**, so without the guard
the rule would be watching a number with no content.

The MVP is a single Postgres (ADD §14), so this alert is armed and silent until the first standby
exists. It becomes live with Patroni + etcd in production.

---

## Diagnose

On the **primary**:

```sql
SELECT client_addr, state, sent_lsn, write_lsn, flush_lsn, replay_lsn,
       pg_wal_lsn_diff(sent_lsn, replay_lsn) AS replay_bytes_behind,
       write_lag, flush_lag, replay_lag
  FROM pg_stat_replication;
```

Which of the three lags is large tells you where the problem is:

| Large | Meaning |
|---|---|
| `write_lag` | Network between primary and standby, or the standby cannot keep up receiving. |
| `flush_lag` | The standby's disk. |
| `replay_lag` | The standby is *applying* slowly — almost always a conflicting long-running query on the standby holding replay off. |

On the **standby**, if `replay_lag` is the large one:

```sql
SELECT pid, now() - query_start AS duration, state, left(query, 120)
  FROM pg_stat_activity
 WHERE state <> 'idle' ORDER BY duration DESC LIMIT 10;
```

An analytics query holding replay is the classic case — `analytics.daily_metrics`'s rollup reads
five tables across three days and is exactly the shape that conflicts.

---

## Fix

- **Replay conflict** → cancel the offending backend on the standby
  (`SELECT pg_cancel_backend(<pid>)`). Consider `hot_standby_feedback` and
  `max_standby_streaming_delay` tuning, but not during the incident.
- **WAL volume spike** → check what is writing. `telemetry.positions` is a hypertable taking `COPY`
  batches; a replay storm (T-05) can multiply that. `mageride_telemetry_rows_written_total` on
  `mageride-position-plane`.
- **Slot retention** → if a replication slot is inactive the primary retains WAL for it for ever and
  will eventually fill its disk. `SELECT * FROM pg_replication_slots WHERE NOT active;` — an
  abandoned slot must be dropped, and that is a separate emergency.

---

## Failover

Only with the DBA. Promoting a standby that is 30 seconds behind **loses those 30 seconds of
committed transactions**: rides that reached a terminal state, payments that settled, SOS rows. On
this platform the SOS rows are the reason to hesitate.

If the primary is gone and there is no choice, record the `replay_lsn` at promotion so the gap can be
reconciled from `audit.events` and the outbox afterwards.

---

## What not to do

- **Do not promote to clear the alert.** Lag is not a reason to fail over; a dead primary is.
- **Do not drop an active replication slot** to reclaim WAL. That breaks the standby permanently and
  it has to be rebuilt from a base backup.
- **Do not point services at the standby to relieve the primary.** It is behind — that is the alert.
