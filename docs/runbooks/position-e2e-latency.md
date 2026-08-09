# Runbook — position latency (D-19, NFR-01, ADD §13.3)

**Alerts:** `PositionE2ELatencyBudgetBurning` · `PositionE2ELatencyBudgetBurningSlowly` ·
`PositionE2ELatencyP99Breached` · `PositionPipelineSilent` · `MqttBridgeFailing`
**Severity:** page (slow burn: ticket) · **Dashboard:** Grafana → `mageride-position-plane`

---

## First action

**Open `mageride-position-plane` and read the "Where the five seconds go" panel.** It shows the
end-to-end p95 beside its two halves — ingest (device → Redis cell stream) and fan-out (cell stream →
group). Whichever half moved is the service to look at, and that single glance saves the whole
diagnosis.

If the panel is *empty* rather than high, you have `PositionPipelineSilent` — skip to that section.

---

## What is measured

`mageride_positions_e2e_latency_milliseconds` is recorded by fanout-svc at the moment a frame leaves
for a subscriber, against the sample's own GNSS capture instant. The whole journey in one histogram,
which is what makes a quantile over it mean anything — `p95(ingest) + p95(fanout)` is a number no SLO
is written about.

The SLO (§13.3 row 1): **p95 < 5 s, p99 < 8 s, 99% of 5-minute windows, 14× burn over 1 h.**

Measured against the *device's* clock, deliberately: it is the delay the passenger experiences, and a
handset with a wrong clock is one of the ways that delay goes wrong.

---

## Diagnose by stage

The path is EMQX → `telemetry.raw` → position-processor → Redis → fanout-svc → SignalR. The
"Throughput along the pipeline" panel draws all four stages; a step down between two adjacent lines
is where samples are being lost.

### Ingest is the slow half

1. **Consumer lag on `telemetry.raw` / `telemetry.normalized`** — the most common cause by a wide
   margin. Check `mageride-stream`; if lag is up, go to [consumer-lag.md](consumer-lag.md). ADD §13.4:
   *"scale position-processor replicas immediately."*
2. **Redis slow or saturated.** The cell streams and `veh:meta` are the ingest destination. Check
   `mageride-redis` — a non-zero slowlog on a single-threaded server is every other command waiting.
3. **Plausibility filter churn.** A spike in `mageride_positions_implausible_total` is not latency,
   but a fleet whose samples are all being refused looks like a frozen map. Check the `check` label:
   `accuracy` is a coverage problem, `speed` or `jump` is a spoofer or a wrongly-typed vehicle.
4. **Replay throttling (T-05).** A fleet reconnecting after an outage drains its backlog at 20/s per
   device. `mageride_mqtt_bridge_replay_throttled_total` rising is that working as designed —
   intentional latency, and R-09 keeping the backlog off the live path.

### Fan-out is the slow half

1. **The batch interval is 2 s** (`Fanout:BatchInterval`, the floor of `signalr-hub.md` §3's 2–8 s
   band). A fan-out p95 near 2 s is the pump working, not the pump slow. Near 8 s is the interval
   having been widened.
2. **Too many cells per replica.** Each replica reads the cell streams it has members in; a replica
   holding thousands of subscribers across a city reads a lot of streams per tick. Scale fanout-svc
   horizontally — there is no backplane, so replicas do not multiply each other's work.
3. **The visibility filter's Redis reads.** `VisibilityIndex.ReadAsync` runs once per frame batch. If
   Redis is slow this shows up here rather than in ingest.

### Neither half moved but end-to-end did

The device's own clock. Check whether the increase is concentrated on one fleet — a batch of trackers
with a drifting RTC will push the quantile without anything on the platform being slow. The
`surface` label on the histogram (geocell / ride / vehicle) tells you if it is one pump or all three.

---

## `PositionPipelineSilent`

**The failure a latency SLO cannot express: nothing to be slow.** The bridge is still lifting
payloads off EMQX and position-processor has normalised nothing for five minutes, so every histogram
is empty and every burn-rate rule with it. Every map on the platform is frozen.

```bash
# Is the consumer group still assigned?
docker compose -f infra/docker-compose.dev.yml exec -T redpanda \
  rpk group describe position-processor-svc --brokers redpanda:9092

# What did it last say?
docker compose -f infra/docker-compose.dev.yml logs --tail 100 hot-path
```

Most often: the consumer crashed and is in a restart loop, or it is stuck on a poison message. Check
`telemetry.normalized.dlq` ([dead-letter-queue.md](dead-letter-queue.md)) and restart `hot-path`.
Nothing is lost — `telemetry.normalized` is retained seven days.

## `MqttBridgeFailing`

Payloads are not reaching `telemetry.raw`. They are **not acknowledged**, so EMQX still holds them
and nothing is lost yet; the map is behind by whatever the redelivery costs. Usual cause is Redpanda
refusing produces — check `mageride-stream` and [redpanda-partitions.md](redpanda-partitions.md).

---

## Fix

- Consumer lag → scale `position-processor` (ADD §13.4's own instruction).
- Redis pressure → [redis-evictions.md](redis-evictions.md).
- Fan-out saturation → scale `fanout-svc`.
- A stuck consumer → restart it; the offsets are committed in Redpanda and the retention covers the
  gap.

---

## What not to do

- **Do not widen `Fanout:BatchInterval` to make the graph look better.** It moves latency out of the
  histogram and into the passenger's experience, which is the thing the SLO exists to measure.
- **Do not reset the consumer group's offsets to skip a backlog.** Those are positions that have not
  reached the hypertable; skipping them loses trip history and the D-19 distance figures computed
  from it. Drain instead.
- **Do not disable the plausibility filter to raise throughput.** D-18/T-07 is what keeps a spoofed
  position off a passenger's map, and the drops are counted per check precisely so a spike can be
  attributed rather than switched off.
- **Do not treat a clock-skew spike as an outage.** Check the `surface` split and the per-fleet
  distribution before scaling anything.
