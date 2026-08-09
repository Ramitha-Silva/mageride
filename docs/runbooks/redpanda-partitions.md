# Runbook — Redpanda partitions unavailable or under-replicated

**Alerts:** `RedpandaPartitionsUnavailable` · `RedpandaUnderReplicatedPartitions`
**Severity:** page / ticket · **Dashboard:** Grafana → `mageride-stream`

---

## First action

```bash
docker compose -f infra/docker-compose.dev.yml exec -T redpanda \
  rpk cluster health --brokers redpanda:9092
docker compose -f infra/docker-compose.dev.yml exec -T redpanda \
  rpk topic describe -a --brokers redpanda:9092 | head -60
```

`rpk cluster health` exits 0 while the cluster is still forming, so **read the verdict, not the exit
code** — the slim stack's healthcheck greps for `Healthy:.+true` for exactly this reason.

---

## What it means, per deployment

| Deployment | RF | `RedpandaPartitionsUnavailable` | `RedpandaUnderReplicatedPartitions` |
|---|---|---|---|
| Dev / replica (single broker, ADD §14) | 1 | The event backbone is **down**, not degraded | Always 0 — nothing to under-replicate |
| Production (3-node cluster) | 3 | Quorum lost for that partition | The warning that precedes it |

On the MVP there is no redundancy by design ("Single Redpanda broker (RF=1)" is an accepted risk in
ADD §14), so the first alert is a full outage of the event backbone.

---

## What stops

Everything that crosses a service boundary, because the outbox is the only path (D6' §2.4):

- `telemetry.raw` / `telemetry.normalized` — the live map freezes; `MqttBridgeFailing` fires because
  produces are refused. **Nothing is lost**: the bridge does not acknowledge, so EMQX still holds the
  payloads.
- `ride.events` — dispatch places no offers, fanout's visibility projection stops updating.
- `audit.events` — the admin console goes quiet.
- Every outbox dispatcher retries; rows stay undispatched and drain when the broker returns
  ([outbox-lag.md](outbox-lag.md)).

---

## Diagnose

1. **Disk.** The most common single-broker cause. Redpanda stops accepting writes rather than
   corrupting.

   ```bash
   docker compose -f infra/docker-compose.dev.yml exec -T redpanda df -h /var/lib/redpanda/data
   docker compose -f infra/docker-compose.dev.yml exec -T redpanda \
     rpk topic describe telemetry.raw -p --brokers redpanda:9092
   ```

   `telemetry.raw` is the volume topic; D6' §2.1 sets its retention, and the seven-day
   `telemetry.normalized` retention is what the "nothing is lost" argument depends on.

2. **Memory.** The container is capped at 1500 m in the slim stack and starts with `--memory=1G
   --reserve-memory=0M`. Redpanda is unusually unhappy when it cannot get what it asked for.

3. **The broker restarted and is recovering.** `redpanda_cluster_partitions` climbing back toward its
   steady value is recovery in progress; give it the start period before intervening.

4. **Production only — a node is gone.** `rpk cluster info` shows the membership. Under-replicated
   with all three nodes present is recovery bandwidth; with two present it is a failed node.

---

## Fix

- **Disk** → extend retention *downwards* first (`rpk topic alter-config <topic>
  retention.ms=<smaller>`), which is reversible and immediate. Adding disk is the real fix.
- **Restart** → `docker compose -f infra/docker-compose.dev.yml restart redpanda`. Production mode
  with fsync is on (`rpk redpanda mode production` before `start`, which is what makes "0 data loss
  on process kill" true), so a restart is safe.
- **Topics missing after a volume loss** → `infra/deploy/redpanda/bootstrap-topics.sh` recreates the
  D6' §2.1 set with the declared partition counts and retention. Auto-create is on but only ever
  fires for a topic nobody declared, producing a 1-partition topic — which is a bug better surfaced
  than hidden.

---

## What not to do

- **Do not delete and recreate a topic to clear an unavailable partition.** That discards every
  message on it, including undispatched outbox events that other services have not seen.
- **Do not disable fsync** (`--unsafe-bypass-fsync`) to recover throughput. The image ships
  `developer_mode: true` and rpk adds that flag silently; the compose file runs
  `rpk redpanda mode production` first specifically to stop it. An acked write that is not on disk
  breaks the outbox's whole guarantee.
- **Do not raise partition counts during an incident.** It changes key distribution, and everything
  is keyed by `vehicleId` or `rideId` so ordering per aggregate depends on it.
