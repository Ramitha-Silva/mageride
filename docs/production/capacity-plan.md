# Capacity plan — DOKS Singapore, 10,000 vehicles / 100,000 passengers

C132 · 2026-08-13 · inputs: ADD §10.2, §16, D7' §8, §11, and the measurements in `load/report.md`
(C129), `chaos/report.md` (C130) and `infra/k8s/README.md`'s capacity note (C124).

> **Updated 2026-08-14.** §1's ceiling is fixed: the chain carried ~10 msg/s because
> `messages_rate = "5/s"` was set on EMQX's in-cluster 1883 listener, which no device reaches and
> mqtt-bridge-svc does. Measured after the fix on the replica: **240 msg/s carried with zero drops**,
> against 1,200 msg/s needed at launch. That is a defect closed and a capacity gap still open.

---

## 1. The measured ceiling, and what it does to this plan

ADD §16.1 derives the launch load from D-20's blended publish rate:

```
10,000 vehicles × 0.12 msg/s (blended)   = 1,200 msg/s ingest
× 5 burst factor (rush hour + reconnect) = 6,000 msg/s burst budget
```

C129 measured the deployed chain — EMQX → mqtt-bridge-svc → `telemetry.raw` →
position-processor-svc → Redis — at **~10 msg/s**, with everything above that discarded inside
EMQX (`delivery.dropped.queue_full`) and every publisher acknowledged anyway. End-to-end position
latency was 33.6 s p95 against D-19's 5 s, at one thirtieth of the launch rate.

**The cause was configuration, and it is fixed.** `messages_rate = "5/s"` — D-17's per-vehicle
publish ceiling — was set on EMQX's **1883** listener. No device connects to 1883; the production
LoadBalancer publishes 8883 and 8084 and names 1883 only as a health-check target, and
`Mqtt__Port=1883` is every platform service. So a per-connection message limit there was a
per-FLEET limit on ingest, charged against the one connection holding E-08's shared subscription.
The limiter is charged for **QoS-1 delivery**, which is why C129's QoS-0 control subscriber cleared
EMQX and sent the search into the bridge, where the stage timings were 8–31 ms produce, 0–36 ms
PUBACK and an in-flight count of 1–12 against a window of 32 — starved, not saturated.

Measured on the replica after the fix, same suite:

| offered | achieved | delivered to the bridge | dropped |
|---|---|---|---|
| 100 msg/s | 99.4 msg/s | 3,198 | **0** |
| 240 msg/s | 239.1 msg/s | 14,798 | **0** |
| 1,000 msg/s | 513 msg/s (the load generator, on this box) | — | 7,189 |

**So one replica carries at least 240 msg/s cleanly, against the ~10 it did, and against 1,200
needed at launch.** What remains is capacity rather than a defect, and it is the ordinary kind:

* ~~`mqtt.max_inflight = 32`~~ **applied 2026-08-15**: 512, on a `services` zone bound to 1883 so
  device sessions keep EMQX's defaults. At ~520 msg/s offered the same run went from **7,189
  dropped to 0**, and 400 and 500 connections are also clean;
* more bridge replicas: E-08's shared subscription is what they are for, and `telemetry.raw` has six
  partitions in production;
* **a box that is not also running the load generator — this is now the binding constraint.** k6
  plateaus at 490-550 msg/s whatever the connection count, on the same 8 vCPU that runs the whole
  replica, and the platform drops nothing at any of it. 1,200 msg/s cannot be offered here, so it
  cannot be measured here.
* **`queue_full` is still the number to watch**, because the loss is silent and there is no other
  symptom. `EmqxMessagesDropped` (C119) is the alert.

---

## 2. Per-workload sizing

ADD §10.2's table is the specification. The middle column is what the manifests actually request,
which is D7' §5's template (`cpu: 500m, memory: 1Gi`) applied uniformly.

| Component | ADD §10.2 | Manifests (production overlay) | Note |
|---|---|---|---|
| Postgres (Patroni 1P+2R) | 3 × 8 GB / 2 vCPU | 3 × 8 Gi / 2 vCPU, requests **=** limits | Guaranteed QoS: the kubelet must not evict a database to save a stateless pod |
| PgBouncer | sidecar | 2 × 64 Mi / 100m | a Deployment, not a sidecar — it holds no state and every replica is interchangeable |
| Redis (Sentinel ×3) | 3 × 2 GB / 1 vCPU | 3 × (1 Gi + 64 Mi) / 550m | the sentinel is a sidecar in the same pod |
| EMQX (2-node) | 2 × 4 GB / 2 vCPU | 2 × 2 Gi / 1 vCPU | **under-provisioned against §10.2** — see §6 |
| Redpanda (3-node RF=3) | 3 × 2 GB / 1 vCPU | 3 × 2 Gi / 1 vCPU | matches |
| fanout-svc | 3 × 2 GB / 1 vCPU | HPA 2–20, 1 Gi / 500m each | §16.3 sizes 3 pods at launch; the HPA floor is 2 |
| position-processor-svc | 2–3 × 2 GB | HPA 2–12, 1 Gi / 500m | six partitions is the useful ceiling (§6) |
| mqtt-bridge-svc | 2 × 2 GB | HPA 2–8, 1 Gi / 500m | |
| tcp-adapter | 2 × 2 GB | 2 × 512 Mi / 500m | StatefulSet for stable identity, not storage |
| the other 25 services | 2 × 2 GB / 1 vCPU, or 1 GB for the light ones | 2 × 1 Gi / 500m | D7' §5's template |
| LiveKit + coturn | 2 × 4 GB / 2 vCPU, 2 × 2 GB | **not in the cluster** | host UDP, `infra/sg/` (C131) |
| Nominatim | 1 × 8 GB / 2 vCPU | **not in the cluster** | its own VPS (D-14) |

---

## 3. What the manifests actually ask a scheduler for

Summed from the rendered production overlay (`kubectl kustomize infra/k8s/overlays/production`),
at the declared replica counts — i.e. at every HPA's **floor**, which is the smallest a healthy
platform can be:

```
43.45 vCPU     97.8 GiB      of REQUESTS, 39 workloads
```

At every HPA's ceiling simultaneously — not a state to provision for, but the bound the platform
can reach on its own without anybody approving anything:

```
147.75 vCPU   306.8 GiB
```

**D7' §8's launch substrate is "DOKS 3 nodes (4 vCPU / 8 GB)" — 12 vCPU and 24 GB.** The floor
above is 3.6× the CPU and 4.1× the memory of that row. This is the tension C124 recorded and
handed here, and it is not a mistake in either document: §8's row and ADD §16.2's "3–5 × €20–40/mo
VPS" both describe D7' §2.1's CO-LOCATED layout, where 21 services share one container. The
repository builds a Deployment per service (there is no `app-services` project and no Dockerfile
for one), which is the shape D7' §5 prints, and 29 stateless services at 2 replicas is 58 pods
whatever each one weighs.

### The requests are a template, not a measurement

`500m / 1Gi` is D7' §5's example block, applied to every service because nothing had measured
any of them. What the replica actually uses, on this box, right now — the same 21 domain services
in one process:

```
app-services   13.35 % of a core     324.6 MiB       (21 services)
hot-path        3.57 %                77.6 MiB       (bridge + processor + writer)
fanout          3.38 %                50.8 MiB
tcp-adapter     0.68 %                32.4 MiB
postgres        3.81 %               309.6 MiB
redpanda       25.28 %               323.3 MiB
emqx            1.42 %               206.7 MiB
redis           1.41 %                 6.2 MiB
```

That is a **near-idle replica with synthetic data**, so it is a floor and not a working set — but
it bounds the answer from below by two orders of magnitude, and it makes one thing certain: a
1 GiB request per stateless service is a scheduling claim nobody has ever justified.

**Right-sizing is a real decision with money attached, and it is not made here** — it needs the
services under C129's load profile, per service, which needs the ingest defect fixed first (§1).
The arithmetic if it were made: 29 stateless services at `100m / 256Mi` (twice the dev overlay,
and still ~15× the measured idle for the whole family) brings the floor to about **21 vCPU and
55 GiB** — half the pool, at the same limits, with nothing allowed to take more than it was.

---

## 4. The node pool

Sized for §3's measured-manifest figure (43.45 vCPU / 97.8 GiB), with three constraints that are
not negotiable:

1. **At least 3 nodes**, because Postgres and Redis both carry `requiredDuringScheduling`
   anti-affinity on `kubernetes.io/hostname`. On fewer, the third member of each is `Pending`
   for ever and the topology is a fiction.
2. **N+1 on the application pool.** A DOKS node upgrade drains one node at a time; a pool with no
   spare capacity cannot reschedule what it evicts, and the drain hangs against the PDBs.
3. **A node has to be able to hold the largest single pod.** Postgres requests 8 GiB, so no node
   in the data pool may be smaller than about 12 GiB allocatable.

DOKS reserves roughly 1.5 GiB and ~0.4 vCPU per node for kubelet, the CNI and the eviction
threshold, so allocatable is materially less than the SKU.

| Pool | Nodes | SKU | Holds | Per-node peak request |
|---|---|---|---|---|
| `data` | 3 | `g-8vcpu-32gb` | one Postgres member, one Redis member, one Redpanda, and EMQX on two of the three | 4.55 vCPU / 13.1 GiB against ~7.6 / ~29.5 allocatable |
| `app` | 6 | `s-8vcpu-16gb` | everything else — 32 workloads at their HPA floors | ~5.2 vCPU / ~10.2 GiB against ~7.6 / ~13.4 allocatable |

**9 nodes, 72 vCPU, 192 GB.** Order of magnitude on DigitalOcean's published list prices, to be
confirmed against the current one at purchase: roughly **US$1,100–1,400 a month** for the pool,
plus block storage (§5), plus three load balancers, plus the Singapore media host (C131) and the
Nominatim VPS (D-14).

That is three to four times ADD §16.2's "3–5 × €20–40/mo VPS" estimate for initial production.
The estimate was written against the co-located layout; this is the per-service one. **Both the
node count and the cost drop by roughly half if §3's right-sizing is done**, which is the single
highest-value capacity action available and is item 14 on the go-live checklist.

### Separate pools rather than one, for two reasons

* **Eviction.** Postgres at requests == limits is Guaranteed QoS and is evicted last, but a node
  under memory pressure still kills something; keeping the stateless tier off the database's
  nodes means what it kills is restartable.
* **Scaling.** The application pool grows with passengers and can use DOKS autoscaling; the data
  pool grows with a deliberate decision and must not autoscale, because a new node with no
  Postgres member on it does nothing and a removed one takes a member with it.

---

## 5. Storage

| Volume | Per member | Total | Why |
|---|---|---|---|
| `pgdata` | 200 Gi × 3 | 600 Gi | ADD §16.1: ~10 GB/day raw positions, T-06's hypertable compressed after 7 days, 90-day retention (T-10). Each member holds the whole database — it is a replica, not a shard |
| `redisdata` | 10 Gi × 3 | 30 Gi | AOF only; the geo index for 10k vehicles is under 100 MB (§10.2) |
| Redpanda | 3 × the base default | — | 7-day retention at 1,200 msg/s × 100 B ≈ 70 GB raw before compression, ÷3 partitionwise but ×3 for RF=3 |
| `pg-dump-wasabi` scratch | 50 Gi node ephemeral | — | the dump is written to an `emptyDir` before upload; the node must have room for it |

Wasabi (backups) and Cloudflare R2 (documents, tiles) are metered, not provisioned.

**`storageClassName` is immutable after the first apply.** `do-block-storage` is set in both
overlays; changing it later is a delete-and-restore of the whole data plane, not an edit.

---

## 6. The scale-out triggers

ADD §10.2's table, as alerts rather than prose — the C132 definition of done. Every rule is in
`infra/observability/prometheus/rules/alerts.capacity.yml` and every one routes to a
`capacity-ticket` whose PagerDuty title is the ACTION, not the symptom
(`alertmanager.production.yml`). The procedure for each is `docs/runbooks/capacity-scale-out.md`.

| §10.2 trigger | Alert | Threshold |
|---|---|---|
| EMQX CPU > 60 %, or > 8k clients/node | `EmqxCpuAtScaleOutThreshold`, `EmqxClientsAtScaleOutThreshold` | 60 % / 15 m, 8,000 / 10 m |
| consumer lag > 5 s sustained | `ConsumerLagAtScaleOutThreshold` | 5 s / 30 m |
| fanout CPU > 60 % or > 30k sessions/pod | `FanoutSendRateAtScaleOutThreshold` | 10,000 frames/s/pod — **a proxy, see below** |
| Redis memory > 70 % or > 50k ops/s | `RedisMemoryAtScaleOutThreshold`, `RedisOpsAtScaleOutThreshold` | 70 % / 30 m, 50k/s / 15 m |
| replication lag > 10 s, or replica CPU > 70 % | `PostgresReplicaLagAtScaleOutThreshold` | 10 s / 20 m |
| sustained > 50k vehicles | `IngestRateAtRedpandaScaleOutThreshold` | 6,000 msg/s / 1 h (D-20's 0.12 msg/s × 50k) |

**Three of §10.2's own signals have no metric on this platform** and the rule file says so where
the rule would be, rather than in a document nobody opens:

* **container CPU** (the EMQX row's first half, the fanout row's first half, the Postgres row's
  second half) — needs cAdvisor or node-exporter. C119 scrapes neither and the DOKS cluster has
  no Prometheus at all yet. EMQX's own `emqx_vm_cpu_use` covers the first of the three.
* **WebSocket sessions per pod** — fanout-svc publishes `mageride.fanout.{frames,filtered,
  latency,signals}` and no connection gauge. The alert uses sends/pod/s instead, which is the
  quantity ADD §16.3 derives the pod count from anyway.
* **active vehicles** — no gauge; converted from ingest rate through D-20's blended figure.

### The one number in §2 that is deliberately below spec

EMQX is 2 Gi / 1 vCPU per node in the manifests against §10.2's 4 GB / 2 vCPU. That is C124's
inherited value and it is **not** raised here, for a reason that is a measurement rather than a
preference: C129 drove the broker to 100 msg/s with 40 concurrent sessions and saw `emqx ≤ 134 %
of one core` with nothing pinned, while the bottleneck sat entirely in the bridge's
acknowledgement path. Doubling the broker before the chain below it can drain is spending against
the wrong constraint. **Raise it when `EmqxCpuAtScaleOutThreshold` fires**, which is what that
alert is for, or when the launch fleet is actually connected — whichever is first.

---

## 7. What has to be measured, and when

| Measurement | When | Why it cannot be done now |
|---|---|---|
| ingest at 1,200 msg/s sustained, `queue_full` at zero | before go-live | 240 msg/s is proven clean (§1); 1,200 needs `max_inflight`, more replicas and a box that is not the generator |
| per-service CPU and memory under the C129 profile | now unblocked | the services are co-located on the replica; per-service attribution needs the per-service deployment under load |
| p95/p99 position latency at launch rate | before go-live | the 33.6 s p95 was the backlog behind the 5/s cap; re-measure now that the cap is gone |
| DR restore against the real Wasabi repository | before go-live | the rehearsed 122 s is on 7.7 MB against MinIO and does not extrapolate (`dr-restore.md` §6) |
| fanout sends/pod/s at 100k subscribers | first month | needs real clients; §16.3's model is the input until then |
| the right-sizing in §3 | first month after launch | needs a month of production working-set data |
