# Runbook — a ride is stuck (R-20, ADD §13.3.1)

**Alerts:** `RideStuckMatching` · `RideStuckOffered` · `RideStuckAccepted` ·
`RideStuckDriverArrived` · `RideStuckInProgress` · `StuckStateMetricsMissing` ·
`RideStuckDetectionRateHigh`
**Severity:** page · **Dashboard:** Grafana → `mageride-stuck-states`

---

## First action

**Open `mageride-stuck-states` and check `RideTimerBacklogHigh` first.** If overdue ride timers are
above 100, that is the cause of most of these and you are on the wrong runbook — go to
[ride-timer-backlog.md](ride-timer-backlog.md). Alertmanager inhibits `RideStuckOffered` and
`RideStuckDriverArrived` when the backlog alert fires, so if you got one of those two *and* the
backlog alert, the backlog is what to fix.

Otherwise: get the ride ids.

```bash
# The count only. Which rides is a database question — the gauge is a count by design.
docker compose -f infra/docker-compose.dev.yml exec -T postgres \
  psql -U postgres -d mageride -c "
    SELECT id, state, passenger_id, offered_driver_id, accepted_driver_id,
           now() - updated_at AS in_state
      FROM rides.rides
     WHERE state = 'Matching' AND now() - updated_at > interval '60 seconds'
     ORDER BY updated_at
     LIMIT 20;"
```

Substitute the state and window from the alert's `stuck_state` label and the table below.

---

## What each alert measures

ADD §13.3.1 computes every row as `count(rides WHERE state=S AND age > T)` rolling 1 minute.
ride-svc's `StuckStateObserver` publishes exactly that as `mageride_rides_stuck{state=…}`, so
**the threshold is inside the metric** and the rule is only `> 0` plus a `for:`.

| Alert | State | Window | Pages after | Likely cause (§13.3.1) |
|---|---|---|---|---|
| `RideStuckMatching` | `Matching` | 60 s | +1 min | Dispatch starvation; candidate pool empty; geo index drift |
| `RideStuckOffered` | `Offered` | 20 s | +1 min | Timer fault — `offer_expiry` not firing |
| `RideStuckAccepted` | `Accepted` | 60 s | +1 min | Driver app killed / network black hole |
| `RideStuckDriverArrived` | `DriverArrived` | 10 min | +2 min | Rider no-show timer fault, or app stuck |
| `RideStuckInProgress` | `InProgress` | 5 min | +2 min | Foreground service killed |

### Two of them are approximated, and it matters here

`Accepted` and `InProgress` carry `approximated="true"`. §13.3.1 qualifies both with "no live
position" / "no GPS sample", and **ride-svc holds no positions** — telemetry belongs to the hot path
and R-01's boundary is what keeps the ride aggregate small. What is published instead is *time in
state*, which is a superset: every ride whose driver has gone dark is in it, along with rides that
are simply slow.

So before treating either as an incident, check whether the driver is actually reporting:

```bash
# The vehicle's last known sample, from the live index.
docker compose -f infra/docker-compose.dev.yml exec -T redis \
  redis-cli HGETALL "veh:meta:<vehicleId>"
```

A `sampleTs` within the last minute means the ride is slow, not dark, and the alert is the
approximation showing. A `sampleTs` minutes old — or no key at all — is the real thing.

---

## Diagnose, in the order worth checking

### 1. Is the timer sweep alive? (covers Offered, DriverArrived, InProgress)

```bash
docker compose -f infra/docker-compose.dev.yml exec -T postgres \
  psql -U postgres -d mageride -c "
    SELECT kind, count(*) AS overdue, min(fire_at) AS oldest
      FROM rides.timers
     WHERE fired_at IS NULL AND fire_at < now() - interval '30 seconds'
     GROUP BY kind ORDER BY overdue DESC;"
```

R-04's guarantee is a fire within about a second of expiry. Anything here is the lease-poll not
claiming rows — see [ride-timer-backlog.md](ride-timer-backlog.md).

### 2. Is there anybody to dispatch to? (covers Matching)

```bash
# The R-08 candidate index.
docker compose -f infra/docker-compose.dev.yml exec -T redis redis-cli ZCARD "geo:live"
docker compose -f infra/docker-compose.dev.yml exec -T postgres \
  psql -U postgres -d mageride -c "
    SELECT state, count(*) FROM dispatch.driver_presence
     WHERE last_seen_at > now() - interval '60 seconds' GROUP BY state;"
```

An empty `geo:live` with drivers present in `driver_presence` is **geo index drift** — the pool the
matcher reads and the presence table have diverged. Check `mageride_dispatch_pool_changes_total` on
`mageride-position-plane`: a sustained `removed` rate with no matching `added` is drivers ageing out
of the hot index faster than they are put back, which looks to a passenger like "no drivers
available" with a full car park outside.

### 3. Is the driver's MQTT session up? (covers Accepted, InProgress)

```bash
docker compose -f infra/docker-compose.dev.yml exec -T emqx emqx ctl clients list | grep <vehicleId>
```

No session plus a stale `veh:meta` is R-15/R-16 territory: the offline grace timer should already be
armed, and the ride will move on its own when it fires. Confirm the grace exists before intervening —
`SELECT * FROM rides.timers WHERE ride_id = '<id>' AND kind = 'offline_grace';`

### 4. Read the ride's own history

```bash
docker compose -f infra/docker-compose.dev.yml exec -T postgres \
  psql -U postgres -d mageride -c "
    SELECT ts, from_state, to_state, reason_code, actor
      FROM rides.transitions WHERE ride_id = '<id>' ORDER BY ts;"
```

`rides.transitions` is append-only with one row per move. The last row and its `reason_code` say what
the platform last decided and why.

---

## Fix

- **Timer sweep stalled** → [ride-timer-backlog.md](ride-timer-backlog.md). Restarting ride-svc
  re-arms nothing and loses nothing: the timers are rows, not in-process state.
- **Empty candidate pool** → restart `position-processor` so the pool is rebuilt from
  `telemetry.normalized`, and check that dispatch-svc is consuming (`mageride:consumer_lag:messages`
  on `mageride-stream`).
- **A ride that genuinely has to be moved by hand** → **always through `admin-bff`, never raw SQL.**
  ADD §13.4 states this for exactly this case. The admin force-transition writes the
  `rides.transitions` row and the audit entry; a `psql` UPDATE writes neither, and the next person to
  read the ride's history sees a state change nobody made.

---

## What not to do

- **Do not `UPDATE rides.rides SET state = …`.** ride-svc is the sole writer of `rides.state` (R-01,
  D5' §6). A direct write skips the transition row (invariant 4), the outbox events every other
  service is waiting for, and the timers armed inside the same transaction. The ride will then be
  stuck in a *different* way that no alert covers.
- **Do not cancel the rides to clear the alert.** The gauge is a count; cancelling makes the number
  go away and the cause stays. Every cancellation is also a passenger who was charged nothing and
  told nothing.
- **Do not silence `RideStuckAccepted` because it is approximated.** The approximation is a superset,
  not a false positive — a real dark driver is always inside it.

---

## `StuckStateMetricsMissing`

Different failure, same runbook. ride-svc is serving requests and no `mageride_rides_stuck` series is
being scraped, so **all eight §13.3.1 pages are silent**.

1. `curl -s http://<ride-svc>:5000/metrics | grep mageride_rides_stuck` — absent?
2. Check `Ride:StuckStateMetricsEnabled` (defaults on). Off means the observer was never constructed.
3. Check the service is in `infra/observability/prometheus/prometheus.yml` under
   `platform-services` or `platform-composed`.
4. `StuckStateObserver` reports 0 and logs a warning rather than failing the scrape when its query
   throws — search Loki for `"could not be measured"`.

## `RideStuckDetectionRateHigh`

The durable backstop is rescuing rides faster than a scrape can see them. Not an outage — R-04 is
doing its job — but a sustained rate means something upstream is failing and being papered over.
Look at `mageride_rides_stuck_detected_total` by state on `mageride-stuck-states` and work back to
whichever of the causes above matches that state.
