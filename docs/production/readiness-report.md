# C132 — production readiness report

DOKS Singapore · 2026-08-13 · **status: the package is complete; the launch is blocked.**

`bash infra/k8s/verify-readiness.sh` exits **2** and names fifteen open items.
`docs/production/go-live-checklist.md` is the list somebody signs.

---

## What was built

| | |
|---|---|
| `infra/k8s/components/launch-topology/` | Patroni 1P+2R (Kubernetes as the DCS), pgBackRest WAL archiving, a 3-node Redis Sentinel group with the client-side change in the same component |
| `infra/k8s/components/retire-single-instance-data/` | removes base's single Postgres and Redis, so the above can take their names |
| `infra/k8s/overlays/{staging,production}` | both now carry D7' §8's launch row; staging at a smaller size and the same shape |
| `infra/k8s/overlays/production/pg-dump-wasabi.yaml` | D7' §8's nightly logical dump, which verifies its own upload |
| `infra/k8s/platform/ingress-nginx/values.production.yaml` | the HTTP edge — what HAProxy+Keepalived becomes on DOKS, with the argument |
| `infra/scripts/dr-rehearsal.sh` | the restore, executed and timed against the committed configuration |
| `infra/observability/prometheus/rules/alerts.capacity.yml` | ADD §10.2's six scale-out triggers plus the launch topology's own failures — 17 rules |
| `infra/observability/alertmanager/alertmanager.production.yml` | three PagerDuty services, escalation, 11 inhibitions |
| `infra/observability/postgres-exporter/queries.yaml` | `pg_stat_archiver`, which the exporter does not ship and the RPO alerts need |
| `docs/production/{capacity-plan,go-live-checklist}.md` | the node-pool arithmetic, and the checklist |
| `docs/runbooks/{postgres-failover,redis-sentinel-failover,dr-restore,capacity-scale-out,oncall}.md` | five runbooks |
| `infra/k8s/verify-readiness.sh` | the definition of done, offline |

---

## 1. Four defects that would each have produced a production outage

Every one was found by RUNNING the manifests — on a three-node Kubernetes cluster on the build
host, and against a real Postgres with the committed pgBackRest configuration. None is visible in
a diff, a `kustomize build`, or `kubectl apply --dry-run`.

### C132-01 · the production database could never have started · HIGH · **fixed**

`base/data/postgres.yaml` set `fsGroup: 1000` and neither `runAsNonRoot` nor a `seccompProfile`.
`base/namespace.yaml` is `pod-security.kubernetes.io/enforce: restricted`. Applied to a real
cluster:

```
Warning  FailedCreate  create Pod postgres-0 in StatefulSet postgres failed error:
  pods "postgres-0" is forbidden: violates PodSecurity "restricted:latest":
  runAsNonRoot != true (pod or container "postgres" must set securityContext.runAsNonRoot=true),
  seccompProfile (… must set … to "RuntimeDefault" or "Localhost")
```

**Zero pods, and everything else looks fine.** PSA is enforced when the *controller* creates the
pod, not when the StatefulSet is applied — so the StatefulSet applies, ArgoCD reports Synced, the
PVC is even provisioned, and the database simply never exists. Every one of the other 74 pods in
the production overlay was admitted; postgres was the only one.

Nothing in this repository would have caught it. `kubectl apply --dry-run=server` only *warns*,
and `.github/workflows/k8s-validate.yml` runs that step with `continue-on-error: true` — so the
warning was being printed and discarded on every pull request.

**Fixed** in base (`runAsUser: 1000` — numerically, which is what the image already uses) and
asserted for every container in the rendered overlay by `verify-readiness.sh` §3.

### C132-02 · every caller on the internet would have shared one rate-limit bucket · HIGH · **fixed**

`GatewayRateLimitMiddleware` buckets on `context.Connection.RemoteIpAddress`. Behind an ingress
that is the ingress controller's pod address — one value, for everybody. `UseForwardedHeaders`
corrects it only for hops it has been told to trust, `gateway-policy.json` ships
`KnownProxies: []` / `KnownNetworks: []` because the addresses depend on the deployment, and
**nothing in `infra/k8s/` set either of them.**

The `auth` policy is 30 requests a minute. On DOKS that would have been thirty logins a minute
for all 100,000 passengers — and C127-02's entire remediation, the finding that the edge shipped
with no rate limits at all, inert again in a different way.

This is C129-04's shape (`KnownProxies__0=haproxy`, a hostname, which `IPAddress.TryParse`
discards silently) in a deployment where there is nothing to spell wrong.

**Fixed**: `Gateway__ForwardedHeaders__KnownNetworks__{0,1,2}` (RFC 1918, so one value is right
on DOKS *and* K3s) in `service-endpoints.yaml`, paired with `use-forwarded-headers: false` on
ingress-nginx so nginx *replaces* a client-supplied header rather than appending to it. Both
halves are needed and `verify-readiness.sh` §5 checks both. C129-04's replica half is still open
and still C008/C125's.

### C132-03 · WAL archiving would have failed on every segment · HIGH · **fixed**

`timescale/timescaledb-ha:pg16` bakes in

```
PGBACKREST_STANZA=poddb
PGBACKREST_CONFIG=/home/postgres/pgdata/backup/pgbackrest.conf
```

— a config path *inside PGDATA*, for Timescale's own orchestration. So the `pgbackrest-config`
ConfigMap mounted at `/etc/pgbackrest/` is never read, and every `archive_command` fails:

```
ERROR: [055]: unable to open missing file
       '/home/postgres/pgdata/backup/pgbackrest.conf' for read
```

A platform with `archive_mode = on`, a healthy `/readiness`, and no WAL archive at all — which
means no RPO, discovered at the moment somebody needs one. The path is also inside the directory
a restore destroys.

**Fixed**: `PGBACKREST_CONFIG` and `PGBACKREST_CONFIG_INCLUDE_PATH` set explicitly on the
container. Found by `dr-rehearsal.sh` on its first run, which is the entire reason that script
executes the committed configuration instead of describing it.

### C132-04 · a three-member Patroni cluster that can never build a replica · HIGH · **fixed**

Patroni's Kubernetes DCS builds its pod selector as

```python
self._labels[config.get('scope_label', 'cluster-name')] = config['scope']
```

— `kubernetes.labels` **plus** a scope label it adds itself. The pods carried `app: postgres` and
not `cluster-name: mageride-pg`, so the selector matched nothing and every member's view of the
cluster contained exactly one member: itself.

What that looks like from outside is a cluster that works:

```
NAME         READY   STATUS    ROLE
postgres-0   1/1     Running   primary        <- elected, initialised, serving, /readiness 200
postgres-1   0/1     Running
postgres-2   0/1     Running
```

and the other two loop for ever, 1 ms apart, with no mention of `pg_basebackup` anywhere:

```
INFO: trying to bootstrap from leader 'postgres-0'
ERROR: failed to bootstrap from leader 'postgres-0'
INFO: Removing data directory: /home/postgres/pgdata/data
```

The leader is known (from the leader ConfigMap), but its `conn_url` comes from the MEMBER list,
the member list is empty, and `create_replica` drops every method that needs a replication
connection — leaving nothing to run. **A one-node database wearing a three-node topology**, and
`kubectl get statefulset` says `1/3` with no reason attached.

**Fixed**: the scope label is on the pod template, and `verify-readiness.sh` §2 asserts it equals
`patroni.yml`'s `scope`.

---

## 2. What was proven, and how

### 2.1 The Patroni cluster, on a real Kubernetes cluster

Three nodes, the staging overlay's data plane, the manifests as committed (resources reduced to
fit the build host; nothing else changed).

```
+ Cluster: mageride-pg -------+--------------+-----------+----+-------------+-----+
| Member     | Host           | Role         | State     | TL | Receive LSN | Lag |
+------------+----------------+--------------+-----------+----+-------------+-----+
| postgres-0 | 10.244.1.18    | Leader       | running   |  1 |             |     |
| postgres-1 | 10.244.2.18    | Sync Standby | streaming |  1 |   0/5000060 |   0 |
| postgres-2 | 10.244.3.18    | Replica      | streaming |  1 |   0/5000060 |   0 |
+------------+----------------+--------------+-----------+----+-------------+-----+
```

One member per node (the required anti-affinity placed them), synchronous replication active,
zero lag.

**Failover, by deleting the leader's pod:**

```
Service/postgres endpoint before : 10.244.1.18     (postgres-0)
… 6 seconds later …
Service/postgres endpoint after  : 10.244.2.18     (postgres-1, promoted)
```

Six seconds, against ADD §14.1's "Patroni promotes replica within 30 s". The `role: primary`
label moved and kube-proxy followed it, so **the DSN hostname never changed and PgBouncer needed
no failover awareness at all.** The promoted member was the Sync Standby, so no committed
transaction was lost. The old primary rejoined by `pg_rewind` onto the new timeline and a new
Sync Standby was elected — 1P+2R restored with no operator action:

```
| postgres-0 | Replica      | streaming |  2 |   0/6000218 |   0 |
| postgres-1 | Leader       | running   |  2 |             |     |
| postgres-2 | Sync Standby | streaming |  2 |   0/6000218 |   0 |
```

### 2.2 Redis Sentinel, and the half that is usually wrong

The topology is the easy half. The half that decides whether a failover means anything is
whether the CLIENTS follow it — so that was tested first, against
`MageRide.Shared/Caching/RedisServiceCollectionExtensions.cs`'s exact code path
(`ConfigurationOptions.Parse` → `ConnectionMultiplexer.Connect`) and the pinned StackExchange.Redis
3.0.17, with a real 3-node quorum and a real `SENTINEL FAILOVER`:

```
parsed ServiceName = 'mageride-primary'
write BEFORE failover -> ok
primary as the client sees it: 127.0.0.1:6390
--- triggering SENTINEL FAILOVER ---
t+1s   WRITE ACCEPTED, primary = 127.0.0.1:6390
…
t+11s  WRITE ACCEPTED, primary = 127.0.0.1:6391
RESULT: the client followed the failover to a NEW primary with no restart.
```

**So Sentinel is a connection-string change and no C# at all.** The change ships in the same
component as the topology (`launch-topology/kustomization.yaml` patches `common-config`), because
applying one without the other is a platform whose clients write to a demoted replica.

In the cluster, a `SENTINEL FAILOVER` promoted a new primary in **6 s**, all three sentinels
agreed, and the old primary became a replica.

### 2.3 The DR restore, executed and timed

`bash infra/scripts/dr-rehearsal.sh`, against the committed `pgbackrest.conf` and the archive
settings read out of `patroni.yml` — 15 checks, all passing:

```
RTO measured   : 122.2 s      against ADD §15's 30 min
  copy 119.5s · postmaster start 1.6s · WAL replay + promote 1.1s
RPO mechanism  : archive_timeout=60s, 5 segments archived, 0 failures
restored       : 1,000 rows committed before T   ·   0 rows written after T
```

The point-in-time cut is the part that makes it a test rather than a demonstration: a restore
that brings back *everything* proves only that the files copied.

**The 122 s does not extrapolate, and not for the obvious reason.** The copy phase moved 29 MB in
two minutes — that is not throughput, it is per-file cost against the object store (1,264 files,
~10/s). Raising `--process-max` from 2 to 8 was measured at 12 % faster, so parallelism is not the
lever either. Taking the number again against the real Wasabi repository with a production-sized
dataset is go-live checklist item 9, and **that** number is the RTO of record.

---

## 3. Deviations, each with the argument

### 3.1 HAProxy + Keepalived → the DO load balancer + ingress-nginx

D7' §8's launch row says "HAProxy + Keepalived". **Keepalived cannot run on DOKS.** VRRP needs two
hosts on one layer-2 segment exchanging multicast and moving a floating IP by gratuitous ARP;
DOKS gives a pod none of the three, and a DigitalOcean Reserved IP is moved by an API call rather
than by ARP. A Keepalived container there would start, elect itself master, advertise to nobody
and fail over nothing — worse than not deploying it, because it looks like HA.

ADD §10.5's own table already makes this progression: at the "Scale (K8s)" row the edge is
"Envoy / NGINX Ingress + cloud NLB for MQTT (TCP passthrough)". D7' §8's row was written from
`stories.txt`'s two-VPS topology and predates the 2026-07-05 decision to run production on DOKS.

What replaces it, function for function: two ingress-nginx replicas with required anti-affinity
and a PDB, behind a DO load balancer that health-checks both; MQTT and the four tracker protocols
never touch it (`overlays/production/loadbalancers.yaml`, which C124 already built).
**Micro-change-set raised against D7' §8.**

### 3.2 `create_replica_methods` is `basebackup` only

It was `[pgbackrest, basebackup]` — restore from the object store first, so that rebuilding a
200 GB replica does not take its bytes from the primary that is already carrying production
alone. Changed after watching it: **Patroni 4.1.3 does not fall through.** A first method that
exits non-zero ends the attempt, 4 ms later, with the second method never invoked. Listing
pgbackrest first therefore does not buy a cheaper rebuild with a safety net; it makes every
rebuild impossible whenever the repository is unreachable, misconfigured or simply *empty* —
which it is on day one. Recorded as **C132-06**, with the operator procedure for the cheap
rebuild in `postgres-failover.md` §5.

(The same investigation found that `no_master` was renamed `no_leader` in Patroni 4, and that an
entry still spelled the old way is appended to the command as `--no_master=1`. Both are in
patroni.yml's comments.)

### 3.3 Postgres runs under-tuned on purpose

`shared_buffers` is 1 GB — staging's number, not production's 8 GiB member's. Patroni's DCS
parameters are cluster-wide by design and one file serves both clusters, and the two errors are
not symmetrical: too large for the member is a postmaster the kubelet OOM-kills on every boot;
too small is a lower cache hit rate on a platform ADD §16.4 prices at 167 writes/s. The trigger
for raising it, and the drift check that keeps `patronictl edit-config` honest, are in
`capacity-plan.md` §6 and `verify-readiness.sh` §9.

---

## 4. Open findings

### C132-05 · nothing in the production cluster would deliver an alert · HIGH · **open**

Sixty-six Prometheus rules, a production Alertmanager routing configuration, three PagerDuty
services and five runbooks — and **no Prometheus, no Alertmanager and no exporters in either DOKS
cluster.** C119 built the observability stack for the compose project; C124 recorded
`Otel__Endpoint: ""` in every overlay for the same reason.

Every runbook in this package begins with the assumption that a page arrived. Four of the rules
here are additionally inert until the exporters exist: `RedisSentinelQuorumAtRisk` and
`RedisPrimaryHasNoReplica` need `redis_exporter` pointed at :26379, `PgDumpJobFailing` needs
kube-state-metrics, and the two WAL alerts need `postgres_exporter` with C132's `queries.yaml`
(which is now wired into the compose stack, so the gap is deployment and not authoring).

**Go-live blocker 12.** Owner: C132 → C119's owner.

### C132-08 · the two open HIGH findings are assigned to a component that cannot act · MEDIUM · **open**

`security/remediation-backlog.md` assigns C127-01 (superuser DSN) and C128-01 (revoked tracker
certificates) to **"C133"**, "due at go-live", and says "C133's own fence already gates go-live on
no open high findings".

**C133 is `payout-svc`** — a wave-3 backend service that shipped weeks ago, with no deployment
surface and no such fence. The component whose fence that sentence describes is C132. So the two
most consequential remediations on the platform were assigned to an owner that cannot perform
them, in a form that reads as ownership; both were open, both are due at a moment nobody was
watching for.

Re-owned in `go-live-checklist.md` §A1 to the deployment owner and the tracker-plane owner. The
backlog needs the same correction and it is not made here — it is C127's and C128's document, and
overwriting another component's ownership record without its authors is how the next one gets
lost too.

### Smaller, and recorded rather than fixed

* **C132-07 · staging was not the launch shape.** EMQX was 1-node and Redpanda 1-node there while
  production had 2 and 3, so a failover drill or an RF=3 durability test against staging proved
  nothing about production. Fixed in the staging overlay; caught by `verify-readiness.sh` §2,
  which asserts the topology in *both* DOKS overlays.
* **pgBackRest's `conf.d` can only ADD options, never override one** (`ERROR: [031]: option
  'repo1-s3-endpoint' cannot be set multiple times`). It is why the credentials are the only
  thing in the include, and why `dr-rehearsal.sh` repoints the destination by environment
  instead.
* **The backup repository is in the same region as the cluster.** ADD §14's P1–P2 row for region
  failure is "manual restore from backup" and §19 puts warm cross-region DR in Phase 3, so this is
  the phase's intended posture — but it is a posture, not an oversight. The remedy is `repo2-*`
  in a second Wasabi region; `dr-restore.md` §7.
* **`REDPANDA_BROKERS` names one broker** (`redpanda-0.redpanda:9092`) in the topic Job. Harmless
  for topic creation — any broker will do — and a Job that fails if broker 0 happens to be the one
  that is down. C124's file; not changed.

---

## 5. What could not be done here, and why

**There is no DOKS cluster.** Creating one needs a DigitalOcean account, a payment method and a
decision to start paying for it, and none of those is in this repository. So the two definition-of-
done items that need one are not met:

* *"a staging DOKS cluster comes up from the manifests and passes the smoke suite"* — the
  manifests were brought up on a three-node Kubernetes cluster on the build host and the data
  plane was proven to form, fail over and recover (§2). That is as close as this box gets, and it
  is not DOKS: it does not exercise `do-block-storage`, the DO load balancer annotations,
  cert-manager against the real DNS, or ESO against a real Vault.
* *"the go-live checklist is signed off"* — it is written, with an owner against every item and
  fifteen of them open. A signature would be a fiction.

The two that could be done, were: **the DR restore procedure is executed once end to end and
timed** (§2.3), and **the scale-out triggers are wired to alerts, not left as prose** (17 rules,
`promtool` clean, each with a runbook that exists and opens with a First action).

---

## 6. The one thing to read if you read nothing else

The launch topology now survives a node loss, and that is worth having.

**And the ingest ceiling is closed** (2026-08-14). C129 measured the chain at ~10 msg/s against a
1,200 msg/s launch target and handed the cause to the bridge's acknowledgement path; the cause was
`messages_rate = "5/s"` on EMQX's in-cluster **1883** listener — D-17's per-vehicle publish ceiling,
applied to a listener no device reaches and the fleet's whole shared subscription does. C129's
control subscriber was QoS 0 and the limiter is charged for QoS-1 delivery, which is exactly why the
broker was cleared and the search went somewhere nothing was wrong. **240 msg/s now carried with
zero drops on the replica**, and `MqttBridgeThroughputTests` is the regression test C129 §1.4 says
nobody had.

What is left is ordinary: 1,200 msg/s sustained is not yet demonstrated (240 is), and the levers are
`mqtt.max_inflight`, replica count and a load generator that is not on the box under test. That is a
capacity gap. It is not the same kind of thing as a platform that discards nine positions in ten.
