# Runbook — Redis evictions, memory and availability (ADD §13.4 bullet 3)

**Alerts:** `RedisEvictions` · `RedisMemoryHigh` · `RedisDown`
**Severity:** page (memory: ticket) · **Dashboard:** Grafana → `mageride-redis`

> ADD §13.4: *"Redis evictions > 0: memory undersized; emergency scale or eviction policy review."*

---

## First action

```bash
docker compose -f infra/docker-compose.dev.yml exec -T redis redis-cli INFO memory | \
  grep -E "used_memory_human|maxmemory_human|maxmemory_policy|evicted_keys"
```

**Read `maxmemory_policy` first.** The stack runs `--maxmemory-policy noeviction`
(`infra/docker-compose.dev.slim.yml`). If it says anything else, an eviction means *both* that memory
is short and that the policy is not what the compose file says — somebody ran `CONFIG SET` on a live
server, and the next restart will silently change behaviour back.

---

## Why an eviction is a page and not a warning

Redis is not a cache here. It holds the live map:

| Key | Written by | What its loss looks like |
|---|---|---|
| `veh:meta:{vehicleId}` | position-processor-svc | The vehicle vanishes from every map and from `GET /v1/nearby`. Nothing errors. |
| `cell:{h3index}` | position-processor-svc | A whole geocell stops updating for everyone watching it. |
| `geo:live` | position-processor-svc | The R-08 candidate pool shrinks — "no drivers available" with a full car park outside. |
| `share:{userId}` | fanout-svc | D-23 entitlement lost, **and it has no rebuild path** (fanout-svc's known gap): entitled passengers lose Mode B visibility until their next grant event. |
| `offer:{rideId}` | dispatch-svc | D-07's expiry keyspace notification never fires; the R-04 durable backstop still does, one sweep later. |
| `lock:driver-offer:{driverId}` | dispatch-svc | The fast path for single-winner accept; the authoritative accept is pure Postgres, so this degrades rather than breaks. |

Under `noeviction` none of that is evicted — the next write past the limit is an **error**, and
position-processor stops indexing instead. That is the designed failure: loud, and recoverable.

---

## Diagnose

```bash
# What is actually big.
docker compose -f infra/docker-compose.dev.yml exec -T redis redis-cli --bigkeys
docker compose -f infra/docker-compose.dev.yml exec -T redis redis-cli INFO keyspace
```

Usual causes, in order:

1. **`geo:live` has no expiry.** Nothing removes a member, so the index is a superset of the live
   fleet and grows monotonically with every vehicle ever seen. query-svc already pays for this by
   re-checking every candidate against its own current position (`out_of_radius` on
   `mageride_query_nearby_filtered_total`). On a long-running deployment this is the first thing to
   grow.
2. **Cell streams not being trimmed.** `cell:{h3index}` is a stream; if the `XADD` is not capped, a
   busy cell grows without bound.
3. **A fleet came online.** Legitimate growth. Check `mageride_fleet_device_health` on
   `mageride-position-plane` for a step change in device count.
4. **`mem_limit` is 512 m in the slim dev stack** and much larger on the replica. Confirm which
   ceiling you are actually hitting — the container's or Redis's `maxmemory`.

---

## Fix

- **Immediate headroom**: raise `maxmemory` and the container's `mem_limit` together. Raising only
  Redis's makes the kernel OOM-kill the container instead, which loses the AOF write buffer.
- **`geo:live` growth**: it is safe to `ZREM` members with no `veh:meta` — position-processor rebuilds
  membership from `telemetry.normalized` as samples arrive. Do this from a script that reads
  `veh:meta` first, not by clearing the key.
- **Never `FLUSHALL`.** See below.

---

## `RedisDown`

ADD §12's degradation ladder applies and it is worth knowing before you start:

- query-svc serves `limited_live` — an empty or partial map **with a flag**, not an error
  (`mageride_query_nearby_limited_live_total`).
- fanout-svc loses its control plane: directed sends stop crossing replicas, so a revocation applies
  only on the replica that received it. That is a **visibility leak**, not just degradation.
- ride-svc is unaffected — it holds no Redis connection at all (`UseRedis = false`), which is what
  makes R-04's "the backstop fires independently of any Redis TTL" structural.

Bring it back with the AOF intact (`appendonly yes`, `appendfsync everysec`): a hard kill loses at
most a second of writes.

---

## What not to do

- **Do not `FLUSHALL` or `FLUSHDB`.** `share:{userId}` has no rebuild path — this service is its only
  writer and it builds the set from `registry.events`. A flush leaves entitled passengers with no
  Mode B visibility until their next grant event, which for a school-van parent could be a term.
- **Do not switch to `allkeys-lru` to stop the alert.** That converts a loud, recoverable failure
  (writes error, indexing stops, the alert fires) into a silent one (vehicles disappear from maps one
  at a time and nothing is logged).
- **Do not `CONFIG SET maxmemory` as the permanent fix.** It does not survive a restart; change the
  compose file.
