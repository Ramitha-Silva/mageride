# Capacity — the scale-out triggers, and what to do when one fires

Alerts: every rule in `infra/observability/prometheus/rules/alerts.capacity.yml`'s
`mageride.capacity.scale_out` group · Plan: `docs/production/capacity-plan.md` · ADD §10.2

## First action

**Nothing, immediately.** Every alert that points at this runbook is a `ticket`, not a page.
Something has grown to a size that ADD §10.2 says is the moment to add capacity, and the
platform is working. If you were woken by one of these, the routing is wrong — see
`docs/runbooks/oncall.md` §3.

Start by reading the trigger label:

```bash
# the alert carries the ACTION as a label value
kubectl -n mageride get pods -o wide      # where the pressure is
```

| `capacity_trigger` | What ADD §10.2 says to do |
|---|---|
| `add-emqx-node` | add an EMQX replica; the cluster rebalances |
| `add-position-processor` | raise `position-processor-svc`'s HPA floor |
| `add-fanout-replicas` | raise `fanout-svc`'s HPA ceiling |
| `redis-cluster` | Sentinel → Cluster (3M+3R) — read §4 first |
| `add-read-replica` | add a Postgres replica |
| `redpanda-5-brokers` | Redpanda 3 → 5, and start the ClickHouse conversation |

## The rule that governs all of them

**Change the catalog, not the cluster.** `infra/k8s/service-catalog.yaml` is the source of truth
for every replica count and HPA range, and `kubectl scale` is reverted by ArgoCD's `selfHeal`
inside three minutes (`docs/runbooks/rollback.md`). The change is:

```bash
$EDITOR infra/k8s/service-catalog.yaml          # replicas: or autoscale: {min, max}
python3 infra/k8s/tools/generate_manifests.py
bash infra/scripts/k8s-verify.sh
# commit, PR, merge -> staging syncs itself -> promote to production
```

For the data plane (EMQX, Redpanda, Redis, Postgres) the replica counts are overlay patches
rather than catalog entries — `infra/k8s/overlays/production/kustomization.yaml`.

## 1. `add-emqx-node` — EMQX CPU > 60 %, or > 8k clients on a node

Two nodes at launch (D7' §8), each sized for 10k clients (ADD §10.2). The alert fires at 8k so
that losing one node does not put the survivor over its own limit.

```yaml
# overlays/production/kustomization.yaml
- target: { kind: StatefulSet, name: emqx }
  patch: |
    - op: replace
      path: /spec/replicas
      value: 3
```

EMQX discovers peers through DNS on the headless Service and rebalances itself. **E-08's shared
subscription only distributes across a genuinely clustered group**, so check after the rollout
that `mqtt-bridge-svc` is receiving from all three:

```bash
kubectl -n mageride exec emqx-0 -- emqx ctl broker metrics | grep -i shared
```

**Before adding a node, check that the problem is really EMQX.** C129 measured the ingest chain
carrying ~10 msg/s against a 3,000 msg/s target, with the loss inside EMQX's mqueue and the
publisher acknowledged anyway. If `delivery.dropped.queue_full` is non-zero, more brokers will
not help — the drain is downstream and the finding is C129 §1.

## 2. `add-position-processor` — consumer lag over 5 s for half an hour

```yaml
# service-catalog.yaml
- name: position-processor-svc
  autoscale: { min: 3, max: 8 }
```

**Six is the ceiling that matters**, not the HPA's. `telemetry.raw` has 6 partitions in
production and a consumer group cannot have more useful members than partitions — a seventh pod
joins the group and is assigned nothing. Beyond six the change is a partition increase, and a
partition count can only ever go UP:

```bash
kubectl -n mageride exec redpanda-0 -- rpk topic alter-config telemetry.raw --set partitions=12
```

Repartitioning changes which partition a `vehicleId` hashes to, so in-flight ordering per vehicle
is not preserved across the change. Do it in a trough.

## 3. `add-fanout-replicas` — sends per pod at ADD §16.3's floor

```yaml
- name: fanout-svc
  autoscale: { min: 3, max: 20 }
```

Sticky sessions matter: a passenger's WebSocket has to keep landing on the pod holding its
geocell subscription. That is the Ingress's `nginx.ingress.kubernetes.io/affinity` on the `/hubs`
route, and adding replicas without it moves subscribers between pods on every reconnect.

The alert measures frames per pod per second, not sessions, because **fanout-svc publishes no
connection gauge** — `mageride.fanout.{frames,filtered,latency,signals}` and nothing that counts
sockets. Adding one is the right fix and it is not this component's; the note is in the rule file.

## 4. `redis-cluster` — memory over 70 %, or 50k ops/s

**This is the one trigger whose documented action is usually the wrong first move.** ADD §10.2
says Sentinel → Cluster (3M+3R), which is a re-architecture: the platform's Lua locks, its
`notify-keyspace-events` consumers and the SignalR backplane all assume one keyspace, and Redis
Cluster does not give you one.

Do these first, in order:

1. **Grow the member.** `overlays/production` sets Redis at 2 Gi; ADD §16.2's scale-out column
   is 4 Gi. That is one patch and no downtime beyond a rolling restart.
2. **Find what is big.** `redis-cli --bigkeys`, and check the geo index against ADD §10.2's own
   estimate — 10k vehicles is "under 100 MB". Memory far above that is something else (a leaked
   key pattern, an unbounded stream) and Cluster would only postpone it.
3. **Only then** consider Cluster, with a written plan for the locks.

Note `maxmemory-policy noeviction`: at 100 % the failure is a write ERROR, not slow degradation,
and Redis holds the dispatch and ride locks. 70 % is where the runway ends, not where it hurts.

## 5. `add-read-replica` — replication lag over 10 s

The launch topology is already 1P+2R, so this is about read TRAFFIC and not about redundancy.

**Check the direction first.** With `synchronous_mode: true` a lagging synchronous standby
applies backpressure to every COMMIT on the platform — so this alert can be the CAUSE of an API
latency incident rather than a symptom of load. `patronictl list` shows which member is the Sync
Standby.

Nothing on this platform opens a read-only connection yet: `Service/postgres-replicas` exists and
has no callers. Pointing a read-heavy path at it (the analytics read model is the obvious first)
is an application change, not a capacity one.

## 6. `redpanda-5-brokers` — ingest at ~50k vehicles

```yaml
- target: { kind: StatefulSet, name: redpanda }
  patch: |
    - op: replace
      path: /spec/replicas
      value: 5
```

Adding brokers does NOT rebalance existing partitions on its own — `rpk cluster partitions
balance` does. Until it runs, five brokers carry three brokers' worth of data and the two new
ones idle.

RF stays 3. `bootstrap-topics.sh` sets it at creation and it is not changed by adding brokers.

## What not to do

* **Never `kubectl scale` in production.** ArgoCD reverts it within three minutes and the
  incident becomes "why did the fix disappear".
* **Never lower a partition count.** Kafka and Redpanda both refuse; the only route is a new
  topic and a migration.
* **Never add capacity to a plane C129 has shown is not the bottleneck.** The ingest chain's
  measured ceiling is a defect, not a size, and paying for brokers to work around it buys
  nothing.
