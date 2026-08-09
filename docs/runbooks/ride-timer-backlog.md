# Runbook — ride timer backlog (ADD §13.4 bullet 5)

**Alert:** `RideTimerBacklogHigh` · **Severity:** page
**Dashboard:** Grafana → `mageride-stuck-states`

> ADD §13.4: *"`rides.timers` backlog > 100 fired_at IS NULL AND fire_at < now()-30s: Quartz cluster
> member partitioned or job-store ill; restart scheduler, validate `qrtz_*` tables."*

**There are no `qrtz_*` tables.** ride-svc (C022/C023) built a lease-poll over `rides.timers` instead
of a Quartz cluster, argued in `backend/src/Ride.Api/CLAUDE.md`: what R-04 requires is that the
durable row decides and that a fire lands within about a second on any replica, and the scan is
already multi-replica safe because it claims `FOR UPDATE SKIP LOCKED`. So the second half of §13.4's
sentence reads "validate `rides.timers`".

---

## First action

**Find out whether the sweep is running at all**, because that decides everything else.

```bash
docker compose -f infra/docker-compose.dev.yml exec -T postgres \
  psql -U postgres -d mageride -c "
    SELECT kind,
           count(*) FILTER (WHERE fired_at IS NULL AND fire_at < now() - interval '30 seconds') AS overdue,
           min(fire_at) FILTER (WHERE fired_at IS NULL) AS oldest_due,
           count(*) FILTER (WHERE fired_at > now() - interval '1 minute') AS fired_last_minute
      FROM rides.timers
     GROUP BY kind ORDER BY overdue DESC;"
```

- `fired_last_minute = 0` everywhere → **the sweep is dead.** Restart ride-svc.
- `fired_last_minute > 0` but `overdue` is climbing → the sweep is running and losing. Scale, or the
  batch size is too small.

---

## Why this pages

R-04's durable timers are what move a ride when nobody taps anything. Eight kinds share the table:

| Kind | Owner | What stops when it does not fire |
|---|---|---|
| `offer_expiry` | dispatch-svc | Offers never lapse → `RideStuckOffered`, and drivers hold offers they have ignored |
| `arrival_grace` | ride-svc | The arrival window never closes |
| `no_show` | ride-svc | `RideStuckDriverArrived` — a driver waiting at the kerb, earning nothing |
| `payment_pending` | ride-svc | Nothing moves (by design), but the §13.3.1 alert never gets its ride id |
| `offline_grace` | ride-svc | R-15/R-16 — a driver who went dark is never released, the ride never terminates |
| `cod_uncollected` | ride-svc | P-14 — cash in transit with no clock on it |
| `location_request_expiry` | *nobody* | Cannot be a row: `ride_id` is `NOT NULL` and the request predates the ride. The deadline is `issued_at + ttl_seconds` on the request itself |
| `otp_attempt_window` | *nobody* | No duration in any spec |

The stuck-state pages follow within a minute or two, which is why Alertmanager **inhibits**
`RideStuckOffered` and `RideStuckDriverArrived` while this one is firing: send the cause, not the
symptoms.

---

## Diagnose

1. **Is ride-svc up and is the sweep enabled?** `Ride:TimersEnabled` defaults on;
   `Ride:TimerInterval` is 1 s, `Ride:TimerBatchSize` 100, `Ride:TimerLease` 30 s. A batch size of
   100 at 1 Hz drains 100/s per replica — if the arrival rate exceeds that, the backlog grows with
   everything working correctly.
2. **Are leases being taken and abandoned?** A replica that claims a batch and then dies leaves rows
   locked until the lease expires. A backlog that oscillates on a 30-second period is this.
3. **Is Postgres the bottleneck?** `FOR UPDATE SKIP LOCKED` on a contended table is cheap, but a
   saturated connection pool starves the sweep like anything else:
   [postgres-saturation.md](postgres-saturation.md).
4. **Is one `kind` responsible?** Every query in `RideTimerRepository` is scoped by kind, which is
   what lets two services share one table. A backlog concentrated in `offer_expiry` is dispatch-svc's
   sweep, not ride-svc's.

---

## Fix

- **Sweep dead** → restart ride-svc (and dispatch-svc for `offer_expiry`). Nothing is lost and
  nothing needs re-arming: the timers are rows, not in-process state. Overdue timers fire on the next
  sweep, in `fire_at` order.
- **Sweep outrun** → scale the service, or raise `Ride:TimerBatchSize`. Scaling is safer: the claim
  is `SKIP LOCKED`, so replicas do not contend.
- **Poison timer** — one row failing repeatedly and blocking its batch. Find it by `fire_at` order and
  read ride-svc's logs for the ride id.

---

## What not to do

- **Do not `UPDATE rides.timers SET fired_at = now()` to clear the backlog.** Every one of those rows
  is a state change that was supposed to happen: a no-show that was never charged, an offer that was
  never released back to the pool, an offline driver whose ride never terminated. Marking them fired
  loses the transition permanently and the rides stay stuck with no timer left to rescue them.
- **Do not delete rows.** Same, worse.
- **Do not add a Quartz cluster** to "follow the spec". Eleven `qrtz_*` tables no DDL spec declares,
  to replace a scan that is already multi-replica safe — and running two timer mechanisms over one
  table would be worse than either. Raised and settled in the C023 handoff.
