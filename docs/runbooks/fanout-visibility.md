# Runbook — the visibility filter looks inert (US-7.16, D-22, D-23)

**Alert:** `FanoutVisibilityFilterInert` · **Severity:** ticket, `security: "true"`
**Dashboard:** Grafana → `mageride-fanout`

---

## First action

**Check whether any ride is actually in progress**, because that decides whether this is a bug or a
quiet night.

```bash
docker compose -f infra/docker-compose.dev.yml exec -T postgres \
  psql -U postgres -d mageride -c "
    SELECT state, count(*) FROM rides.rides
     WHERE state IN ('Accepted','DriverArrived','InProgress') GROUP BY state;"
```

Zero engaged rides and frames still flowing is a Mode A platform at 3 a.m. — legitimate, and the
alert is being conservative. Engaged rides *and* no `reason="engaged"` filtering in thirty minutes is
the real thing: **passengers can see vehicles that are already carrying somebody.**

---

## Why an alert for this at all

A working filter and an **absent** filter look identical from a passenger's map: vehicles appear,
positions move, nothing errors. The difference only shows when somebody sees a vehicle they should
not, and by then it has been true for a while. `mageride_fanout_filtered_total` is the one number
that says the filter is doing anything at all.

The four reasons it withholds a frame:

| `reason` | Rule | Source of truth |
|---|---|---|
| `engaged` | US-7.16 — the vehicle is on hire | `veh:engaged:{vehicleId}`, written by fanout-svc from `ride.events` |
| `stale` | US-7.17 — the sample is older than the freshness window | the frame's own `sampleTs` |
| `offline` | US-7.17 — the EMQX last will fired | `veh:offline:{vehicleId}` |
| `private` | D-22/D-23 — Mode B, not entitled | `share:{userId}` |

---

## Diagnose

1. **Is the projection being fed?** `veh:engaged` is written from `ride.events`. If fanout-svc's
   consumer is lagging or `Fanout:EventsEnabled` is off, the engagement set is empty and every
   engaged vehicle is public.

   ```bash
   docker compose -f infra/docker-compose.dev.yml exec -T redis redis-cli --scan --pattern 'veh:engaged:*' | head
   docker compose -f infra/docker-compose.dev.yml exec -T redpanda \
     rpk group describe fanout-svc --brokers redpanda:9092
   ```

2. **Are the switches on?** fanout-svc announces every disabled filter at start-up, and the reason it
   does is this exact failure mode.

   ```bash
   docker compose -f infra/docker-compose.dev.yml logs fanout 2>&1 | grep -iE "disabled|enabled|filter"
   ```

   `Fanout:EventsEnabled`, `ControlPlaneEnabled`, `PresenceEnabled`, `PumpEnabled` all default on and
   each gates one filter.

3. **Do the two planes agree?** The socket (`mageride_fanout_filtered_total`) and the snapshot
   (`mageride_query_nearby_filtered_total`) apply one rule, `VehicleVisibilityRules`. Their
   `engaged`, `stale`, `offline` and `private` rates should move together. One plane filtering and
   the other not is a projection that reached one service and not the other — that panel is on
   `mageride-fanout`.

4. **Was Redis flushed?** `share:{userId}` **has no rebuild path** — fanout-svc is its only writer and
   builds it from `registry.events`. A flush leaves entitled passengers with no Mode B visibility
   (fails closed, which is the right direction) but also empties the set the `private` reason is
   computed against.

---

## Fix

- **Consumer lagging** → [consumer-lag.md](consumer-lag.md).
- **A switch off** → turn it on and restart. Note *when* it was turned off; everything between then
  and now was visible to people who should not have seen it, and that may be reportable.
- **Projection cold after a deployment** → a fresh consumer group replays `ride.events`, so it
  rebuilds. `Fanout:RideProjectionTtl` is 24 h and `EngagementTtl` 12 h, both deliberately longer
  than a ride.

---

## What not to do

- **Do not dismiss this because no user has complained.** Nobody complains about seeing a vehicle
  they should not — they complain when one disappears.
- **Do not "fix" it by removing engaged vehicles at the client.** The rule is server-side because the
  client is not trusted with who may see what.
- **Do not raise the 30-minute window to stop the noise on a quiet platform.** Add the engaged-ride
  condition instead, which the rule already has.
