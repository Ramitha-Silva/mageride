# Runbook — outbox dispatch lag (ADD §13.4 bullet 6)

**Alerts:** `OutboxDispatchLagHigh` · `OutboxPublishFailing` · **Severity:** page
**Dashboard:** Grafana → `mageride-money-safety`

> ADD §13.4: *"`outbox` lag > 1 s p95: LISTEN/NOTIFY listener dead; restart outbox-dispatcher, replay
> from `last_published_id` watermark."*

---

## First action

**Check whether the LISTEN connection is alive**, because the degradation is silent — the dispatcher
falls back to a 250 ms poll and keeps working, just slower, and the only visible symptom is this
metric.

```bash
docker compose -f infra/docker-compose.dev.yml exec -T postgres \
  psql -U postgres -d mageride -c "
    SELECT application_name, state, wait_event_type, wait_event, now() - state_change AS idle_for
      FROM pg_stat_activity
     WHERE wait_event = 'ClientRead' OR query ILIKE 'listen%'
     ORDER BY idle_for DESC;"
```

No LISTEN sessions at all → restart the affected service. That is §13.4's own instruction.

---

## Why this is a platform-wide alert

The transactional outbox is the **only** way state crosses a service boundary — CLAUDE.md's universal
rule, D6' §2.4. There are no direct HTTP calls between services for state changes. So lag here is
every consumer on the platform reacting late, at once:

- dispatch-svc placing an offer for a ride requested a second ago
- fanout-svc removing a passenger from a vehicle group after a share was revoked (D-22's budget is
  200 ms — this alone blows it)
- notification-svc sending the push that announces something the user already saw
- fare-svc settling, and every ride sitting one step longer in `PaymentPending`

E-09 budgets a **median under 50 ms**. The p95 threshold here is §13.4's 1 s.

---

## The common cause: LISTEN through PgBouncer

`ConnectionStrings__PostgresDirect` exists for this and nothing else. LISTEN is session-scoped, and
PgBouncer in transaction mode returns the server connection at COMMIT — so a LISTEN registered
through the pooler is dropped and the dispatcher never wakes. The sub-50 ms path then silently
degrades to the 250 ms poll it was meant to replace.

```bash
docker compose -f infra/docker-compose.dev.yml exec -T app-services env | grep PostgresDirect
```

Unset means the kernel fell back to the pooled DSN **with a warning at start-up**. ride-svc and
dispatch-svc require it; everything else may omit it. Search Loki for that warning.

---

## Diagnose the rest

1. **Is the backlog in the table or in flight?**

   ```sql
   SELECT count(*) AS undispatched, min(created_at) AS oldest
     FROM rides.outbox WHERE dispatched_at IS NULL;
   ```

   (Each bounded context has its own: `rides.outbox`, `dispatch.outbox`, `safety.outbox`, …)

   A large undispatched count with the dispatcher alive is a broker problem, not a listener problem.

2. **Is Redpanda accepting produces?** `OutboxPublishFailing` fires on
   `mageride_outbox_publish_failures_total`. Rows stay undispatched and are retried, so nothing is
   lost — check [redpanda-partitions.md](redpanda-partitions.md).

3. **Is one service responsible?** The histogram is on the platform meter, so filter by `service` on
   the dashboard. A single service lagging is its own listener; all of them lagging is Postgres or
   the broker.

---

## Fix

- **Listener dead** → restart the service. The dispatcher resumes from the `last_published_id`
  watermark; there is no manual replay to run and no risk of double-publishing that consumers do not
  already handle (delivery is at least once, and R-14's replay covers the rest).
- **Broker refusing** → fix Redpanda; the outbox drains on its own once produces succeed.
- **`Outbox:DispatcherEnabled` off** → nothing is ever published. The row is still written (there is
  no switch that skips the outbox row itself — a `Safety:OutboxEnabled` flag existed for one commit
  and was removed for exactly this reason), so turning it back on drains the whole backlog in order.

---

## What not to do

- **Do not mark rows dispatched to clear the backlog.** Every one is a state change another service
  has not seen. A `ride.completed` marked dispatched but never published leaves dispatch-svc holding
  a ghost-busy driver for ever.
- **Do not point the dispatcher's LISTEN at PgBouncer** "because it works". It appears to work — the
  poll keeps the platform correct — and it costs the 50 ms budget every event, permanently.
- **Do not reorder or skip.** Every event of an aggregate is keyed by its id on one topic
  specifically so a penalty cannot overtake the cancellation that caused it.
