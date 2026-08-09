# Runbook — Postgres saturation and availability

**Alerts:** `PostgresConnectionSaturation` · `PostgresLongRunningQuery` · `PostgresDown`
**Severity:** page (`PostgresDown`) / ticket · **Dashboard:** Grafana → `mageride-postgres`

---

## First action

```bash
docker compose -f infra/docker-compose.dev.yml exec -T postgres \
  psql -U postgres -d mageride -c "
    SELECT pid, state, now() - state_change AS in_state, now() - xact_start AS xact_age,
           wait_event_type, wait_event, left(query, 100) AS query
      FROM pg_stat_activity
     WHERE datname = 'mageride' AND state <> 'idle'
     ORDER BY xact_age DESC NULLS LAST
     LIMIT 20;"
```

**Look for `idle in transaction` first.** It holds locks and blocks vacuum while doing no work, and
it is the single most common cause of both alerts. The usual origin is a service that opened a
transaction and then made a network call inside it.

---

## `PostgresConnectionSaturation`

`sum(pg_stat_activity_count) / max(pg_settings_max_connections) > 0.85`.

Every service reaches Postgres through PgBouncer in transaction mode (M-5, ADD §9.3), so a *high*
count with the pooler in front means one of three things:

1. **The pool is sized past the server.** PgBouncer's `DEFAULT_POOL_SIZE` × the number of distinct
   (user, database) pairs must stay under `max_connections` (200 in the dev stack), with headroom.
2. **Sessions are not being returned.** Transaction pooling returns at COMMIT; a long transaction
   holds its server connection for the whole time.
3. **Direct connections.** The E-09 outbox LISTEN holds one session per replica **by design**
   (`ConnectionStrings__PostgresDirect` bypasses the pooler because LISTEN is session-scoped). That
   is expected and bounded by the replica count; anything else connecting directly is not.

```bash
# What is connecting, and from where.
docker compose -f infra/docker-compose.dev.yml exec -T postgres \
  psql -U postgres -d mageride -c "
    SELECT application_name, client_addr, state, count(*)
      FROM pg_stat_activity GROUP BY 1,2,3 ORDER BY count DESC;"
```

**Fix:** raise `max_connections` only after checking the pooler, because more backends on the same
CPU is slower, not faster. The pooler exists so the answer is usually "size the pool down".

---

## `PostgresLongRunningQuery`

`max(pg_stat_activity_max_tx_duration{state="active"}) > 300`.

A transaction open for five minutes also holds back autovacuum across every table it touched, so this
and the vacuum-lag panel move together. Check the "Vacuum lag" panel on `mageride-postgres` — dead
tuples piling up on `rides.rides` or `dispatch.driver_presence` (the two that churn hardest) is the
consequence.

Legitimate long operations exist: the analytics rollup reads five tables across three days, and a
migration takes as long as it takes. Confirm what it is before cancelling:

```sql
SELECT pg_cancel_backend(<pid>);   -- polite; lets the transaction roll back
SELECT pg_terminate_backend(<pid>); -- last resort
```

Never terminate a `migrate` job mid-script. DbUp runs one transaction per script; killing it leaves
the journal and the schema disagreeing, which is a much worse morning.

---

## `PostgresDown`

The system of record. What survives, per ADD §12's degradation ladder:

- The **live map keeps running** from Redis until persistence-writer's buffer fills — `veh:meta` and
  the cell streams are Redis, not Postgres. `mageride_telemetry_writer_stalls_total` is the countdown.
- **No ride can change state.** ride-svc's every mutation is one transaction.
- **No timer fires**, so nothing recovers on its own; expect every §13.3.1 alert once it returns.
- **Nothing is published** — the outbox is a table.

```bash
docker compose -f infra/docker-compose.dev.yml logs --tail 200 postgres
docker compose -f infra/docker-compose.dev.yml exec -T postgres pg_isready -U postgres -d mageride
```

Disk full is the usual cause on a single-VPS deployment, and `telemetry.positions` is what fills it.
Check TimescaleDB retention and compression before adding disk.

---

## What not to do

- **Do not raise `max_connections` as the first move.** It trades a queue you can see for a slowdown
  you cannot.
- **Do not `pg_terminate_backend` across the board** to clear saturation. Terminating a backend
  mid-transaction rolls it back — which is safe for the data and means the ride, payment or
  settlement it was performing simply did not happen, silently, from the caller's point of view.
- **Do not point services at the read replica to relieve the primary.** Read-after-write breaks, and
  if the replica is lagging you get [postgres-replication-lag.md](postgres-replication-lag.md) as
  well.
