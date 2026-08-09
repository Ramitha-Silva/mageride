# Runbook — atomic accept resolution latency (ADD §13.3 row 9)

**Alert:** `AcceptResolutionLatencyBudgetBurning` · **Severity:** page
**Dashboard:** Grafana → `mageride-slo`

> ADD §13.3 row 9: driver tap → 200/409 returned, **p95 < 300 ms, p99 < 800 ms**, 99% monthly,
> 5% budget over 1 h.

**Not instrumented yet** as a dedicated histogram — see the C119 handoff. Until it is, the route's
own golden signal is a good proxy:

```promql
histogram_quantile(0.95, sum by (le) (
  rate(http_server_request_duration_seconds_bucket{http_route=~"/v1/rides/.*/accept"}[5m])))
```

---

## First action

**Look at Postgres, not at the application.** The accept is *one conditional UPDATE and nothing else*
— no advisory lock, no pre-flight `SELECT`, no application-side ordering (ADD §11.11). There is
almost no application work to be slow, so a slow accept is contention on `rides.rides`.

```bash
docker compose -f infra/docker-compose.dev.yml exec -T postgres \
  psql -U postgres -d mageride -c "
    SELECT pid, state, wait_event_type, wait_event, now() - xact_start AS age, left(query, 90)
      FROM pg_stat_activity
     WHERE datname = 'mageride' AND state <> 'idle'
     ORDER BY age DESC NULLS LAST LIMIT 15;"
```

`wait_event_type = 'Lock'` on rows of `rides.rides` is the answer. See
[postgres-saturation.md](postgres-saturation.md).

---

## Why 300 ms

A driver taps Accept while driving. The response is the whole interaction: either they got the ride
or somebody else did, and there is nothing useful to show in between. Beyond about a second the app
has to invent a spinner for an operation that is a single row update.

The 409 matters as much as the 200 — a concurrent double-accept must resolve to exactly one winner
and one clean loser. The database picks the winner, which is what makes the latency a database
question.

---

## Diagnose

1. **Lock contention.** Several drivers accepting offers on the same ride is the designed race and is
   cheap. Contention with *other* work on `rides.rides` — a long-running report, a migration, an
   `idle in transaction` session — is not.
2. **Connection pool.** A saturated pool adds queueing before the statement runs, which shows as
   latency with no lock waits. [postgres-saturation.md](postgres-saturation.md).
3. **Idempotency middleware.** Every mutation goes through the command log (R-14). A slow
   `rides.command_log` insert is on this path; check its table size and vacuum state.
4. **The gateway.** Confirm the latency is in the service and not at the edge by comparing
   `http_server_request_duration_seconds` on ride-svc with the same route on api-gateway.

---

## Fix

- Kill the contending transaction (having identified it), or wait for the migration.
- Vacuum `rides.rides` and `rides.command_log` if dead tuples have accumulated — the "Vacuum lag"
  panel on `mageride-postgres`.
- Scale ride-svc only if the pool is the bottleneck; more replicas against a contended row make
  contention worse.

---

## What not to do

- **Do not add an advisory lock or an application-side queue to "serialise" accepts.** The absence of
  one is deliberate: the database picks the winner in a single statement, and there is deliberately
  no `offered_driver_id` predicate on the UPDATE — adding one would turn a concurrent double-accept
  into two 403s instead of a 200 and a 409.
- **Do not cache the ride state to avoid the read.** There is no read to avoid; the statement is the
  read.
