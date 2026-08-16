# Redis — the Sentinel group, and what a failover costs

Alerts: `RedisSentinelQuorumAtRisk`, `RedisPrimaryHasNoReplica` · Topology:
`infra/k8s/components/launch-topology/redis-sentinel.yaml`

## First action

```bash
kubectl -n mageride exec redis-0 -c sentinel -- \
  redis-cli -p 26379 sentinel master mageride-primary | head -8
```

The `ip` field is the current primary's StatefulSet DNS name. If it answers, the group has a
primary and the platform can write; the page is about redundancy, not availability.

If the pod you picked is the one that is down, ask a different one — `redis-1`, `redis-2`. Any
sentinel can answer, which is the point of there being three.

## What is actually running

Three pods, each with two containers: a Redis node and a Sentinel. Quorum is 2 of 3. Clients do
NOT connect to a Redis address — `retire-single-instance-data` deleted `Service/redis` and there
is nothing to replace it with, because a group with a moving primary has no fixed address. Every
service connects through the sentinels:

```
ConnectionStrings__Redis: redis-0.redis-peers:26379,redis-1…,redis-2…,serviceName=mageride-primary
```

StackExchange.Redis sees `ServiceName` set and switches to sentinel discovery. It follows a
failover with no restart — measured at 11 s on the pinned 3.0.17 against a real quorum
(`docs/production/readiness-report.md` §2.2).

## The three states worth recognising

```bash
# who is primary, and how many sentinels agree
kubectl -n mageride exec redis-0 -c sentinel -- redis-cli -p 26379 sentinel master mageride-primary
# the other sentinels this one can see (should be 2)
kubectl -n mageride exec redis-0 -c sentinel -- redis-cli -p 26379 sentinel sentinels mageride-primary | grep -c '^name'
# the replicas the primary has (should be 2)
kubectl -n mageride exec redis-0 -c redis -- redis-cli -p 6379 info replication | grep -E 'role|connected_slaves'
```

| What you see | What it means |
|---|---|
| a primary, 2 sentinels, 2 replicas | healthy |
| a primary, 2 sentinels, 1 replica | a member is down; writes still work; fix it |
| a primary, 2 sentinels, **0 replicas** | **writes are being REFUSED** — §2 |
| `sentinel sentinels` < 2 anywhere | a failover cannot be authorised — §3 |

## 1. A failover just happened. What was lost?

Replication is asynchronous. Anything the old primary acknowledged and had not yet shipped is
gone. For most of what Redis holds here that is genuinely fine — the live geo index is rebuilt by
the next position from each vehicle, and ADD §15 prices Redis at RPO 0 for exactly that reason.

What is not fine, and what to check:

* **`offer:{rideId}` keys.** A lost offer expiry is covered — C023's Quartz backstop re-sweeps
  and C130's drill 10 proved a Redis flush loses no offer expiry.
* **`lock:driver:{driverId}`.** A lost lock could in principle let a second session start for one
  driver. The Postgres UNIQUE partial index on `trips.sessions(driver_id) WHERE state='ACTIVE'`
  (D-03) is the durable half of that mutex and it does not depend on Redis, so the failure is a
  refused second session rather than a double booking.
* **The SignalR backplane.** Subscribers reconnect; fanout-svc backfills from Redis on reconnect
  (ADD §14.1).

## 2. `RedisPrimaryHasNoReplica` — writes are being refused

`min-replicas-to-write 1` is set deliberately. A primary that cannot see a replica stops
accepting writes, and that is the correct behaviour rather than a bug to configure away: if it
kept handing out `lock:driver:{driverId}` while a sentinel quorum on the other side of a
partition promoted a different primary, two drivers would be dispatched to one ride and nothing
would report an error.

The symptom users see is dispatch failing, not "Redis is down".

```bash
kubectl -n mageride get pods -l app=redis          # which member is missing
kubectl -n mageride describe pod redis-1 | tail -20
```

Bring the member back. It rejoins on its own: its entrypoint asks the other sentinels who the
primary is and starts as a replica of the answer.

**If you must restore writes before the member can come back** — and only then, with a written
decision, because it removes the protection above:

```bash
kubectl -n mageride exec redis-0 -c redis -- redis-cli -p 6379 config set min-replicas-to-write 0
```

That is a runtime change and it does NOT survive a restart, which is deliberate: the entrypoint
writes the file, so the safe value comes back on its own.

## 3. The quorum is at risk

Two of three sentinels must agree before one may promote. Below that, the group can watch a
primary die and do nothing — which looks exactly like a healthy platform until the primary dies.

```bash
for i in 0 1 2; do
  echo "== redis-$i"
  kubectl -n mageride exec redis-$i -c sentinel -- \
    redis-cli -p 26379 sentinel sentinels mageride-primary 2>/dev/null | grep -c '^name'
done
```

If a sentinel is up but sees no others, it is a network problem and not a Redis problem — check
that `redis-peers` resolves from inside the pod:

```bash
kubectl -n mageride exec redis-0 -c sentinel -- \
  getent hosts redis-1.redis-peers.mageride.svc.cluster.local
```

## 4. A promotion by hand

Only when a primary is unreachable and the quorum will not act.

```bash
kubectl -n mageride exec redis-0 -c sentinel -- \
  redis-cli -p 26379 sentinel failover mageride-primary
```

Sentinel picks the replica with the best replication offset. It refuses if a failover is already
in progress, which is what `failover-timeout 30000` bounds.

## 5. Rebuilding the group from nothing

If every member is gone and the PVCs are empty, the group bootstraps itself: `redis-0` starts as
primary and the others follow, because `redis-entrypoint.sh` falls back to `redis-0` only when no
sentinel and no persisted `sentinel.conf` can answer.

If the PVCs still hold data, **do not delete `/data/sentinel.conf`.** That file is the group's
memory of which member was primary, and it is what makes a full-cluster restart come back with
the right one. Deleting it makes every member re-seed on `redis-0`, and if `redis-0` was not the
primary, the newest writes are on a member that is about to be made a replica and resynchronised
from behind.

## What not to do

* **Never point a service back at a single Redis address.** There is no `Service/redis` any more
  and re-creating one would send writes to whichever member the selector happened to pick.
* **Never set `maxmemory-policy` to anything but `noeviction`.** Redis holds the locks; an
  eviction policy that dropped one hands two drivers the same ride. C119 alerts on any eviction
  at all for the same reason.
* **Never scale the StatefulSet below 3.** Two members is a quorum of two with no spare vote —
  losing one leaves a group that cannot promote anything.
