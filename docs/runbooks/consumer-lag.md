# Runbook — consumer lag (ADD §13.4 bullet 1, D7' §12)

**Alerts:** `ConsumerLagSecondsHigh` · `ConsumerLagMessagesWarning` · `ConsumerLagMessagesCritical`
**Severity:** page (10k warning: ticket) · **Dashboard:** Grafana → `mageride-stream`

---

## First action

**Scale the lagging consumer.** ADD §13.4 says so outright — *"scale `position-processor` replicas
immediately"* — and for the position plane it is almost always right, because the consumer is
stateless and the partition count (3 in dev, per D6' §2.1) is the ceiling on how far it helps.

```bash
docker compose -f infra/docker-compose.dev.yml up -d --scale hot-path=3
# production
kubectl -n mageride scale deployment/position-processor --replicas=6
```

Then find out why, using the sections below. Scaling first is correct here because the backlog grows
while you diagnose and `telemetry.normalized` is only retained seven days.

---

## What is measured

**Redpanda v24.2 publishes no lag metric.** Verified against the pinned broker: `/public_metrics`
carries `redpanda_kafka_consumer_group_committed_offset` and `redpanda_kafka_max_offset`, and nothing
named `..._lag_...` at all. So lag is computed in
`infra/observability/prometheus/rules/recording.stream.yml` as high-watermark minus committed offset,
and every alert and panel reads the recorded series:

| Series | Unit | Threshold |
|---|---|---|
| `mageride:consumer_lag:seconds` | seconds of production behind | ADD §13.2/§13.4: **> 5 s sustained 2 min** |
| `mageride:consumer_lag:messages` | messages | D7' §12: **warn > 10k**, **page > 100k** |
| `mageride:consumer_lag:messages_max_partition` | messages, worst partition | ADD §13.2 says "per partition" |

The seconds figure divides the message lag by the topic's produce rate: how long the consumer would
take to catch up if production stopped. A topic with no production is dropped rather than reported as
infinitely lagging.

---

## Which consumer, and what it means

| Group | Topic | What the lag costs |
|---|---|---|
| `position-processor-svc` | `telemetry.raw` | The live map is behind. Directly the D-19 SLO. |
| `persistence-writer-svc` | `telemetry.normalized` | Trip history and distance are behind. The live map is fine — that separation is C040's fence. |
| `fanout-svc` | `ride.events`, `registry.events` | Visibility state is stale: a revoked share still visible, an engaged vehicle still on the public map. |
| `dispatch-svc` | `ride.events` | Offers placed against rides that have already moved; expired offers not reassigned. |
| `notification-svc` | every topic | Push notifications arriving after the thing they announce. |
| `admin-bff` | `audit.events` | The console is behind. Lowest urgency. |

---

## Diagnose

```bash
# The authoritative view, per partition.
docker compose -f infra/docker-compose.dev.yml exec -T redpanda \
  rpk group describe <group> --brokers redpanda:9092
```

1. **Is the consumer alive?** A group with `LAG` rising and no `MEMBER-ID` has no consumer attached at
   all — the process died or lost its assignment. That is not a scaling problem.
2. **One partition or all of them?** Check `mageride:consumer_lag:messages_max_partition` against the
   total. A single hot partition is a key-distribution problem: everything is keyed by `vehicleId` or
   `rideId` (D6' §2.1), so one very busy vehicle or a hashing accident can pin one partition while the
   others idle. Scaling does not fix that.
3. **Is the consumer slow or is the producer fast?** The "Produced and fetched, per topic" panel
   answers it. A produce-rate step change is a fleet coming online or a device firmware change; the
   consumer is fine and needs capacity.
4. **Is the consumer stuck on one message?** Flat committed offset, rising watermark, process alive.
   Check the DLQ ([dead-letter-queue.md](dead-letter-queue.md)) and the consumer's logs.

---

## Fix

- **Under-provisioned** → scale, up to the partition count. Beyond that, add partitions
  (`infra/deploy/redpanda/bootstrap-topics.sh` is the declared shape; adding partitions changes key
  distribution, so do it deliberately).
- **Downstream is the bottleneck** — persistence-writer stalling on Postgres shows as
  `mageride_telemetry_writer_stalls_total`. Fix Postgres, not Redpanda:
  [postgres-saturation.md](postgres-saturation.md).
- **Stuck consumer** → restart. Offsets are committed in the broker; nothing is lost.

---

## What not to do

- **Do not reset offsets to the end to clear the lag.** That discards positions that never reached
  the hypertable, and the trip summaries computed from them are then wrong for ever. The retention is
  seven days — there is time to drain.
- **Do not delete and recreate the consumer group.** Same effect, less visibly.
- **Do not scale past the partition count** expecting improvement. Extra consumers in a group sit
  idle with no assignment.
