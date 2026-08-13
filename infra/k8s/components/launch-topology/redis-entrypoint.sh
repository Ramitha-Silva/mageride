#!/bin/sh
# =====================================================================================
# Decide whether this member starts as the primary or as a replica, then start Redis (C132).
#
# A Redis node in a Sentinel group cannot be told statically which it is: the answer changes
# every time a failover happens, and a pod that comes back believing it is still the primary
# is the classic way to end up with two of them. So the answer is ASKED FOR, in this order:
#
#   1. any other member's sentinel — the live, authoritative answer;
#   2. this pod's own /data/sentinel.conf — what its sentinel last recorded, which survives
#      the pod because the file is on the PVC. This is the cold-start path: every pod in the
#      group is down, so nobody can answer question 1;
#   3. redis-0, and only if 1 and 2 are both silent, which is true exactly once — the first
#      time the StatefulSet is created.
#
# `podManagementPolicy: OrderedReady` is what makes step 3 safe: on a cold start redis-0 is
# up and answering before redis-1 is created, so redis-1 takes branch 1 and never guesses.
#
# --- HOSTNAMES, NOT POD IPs -----------------------------------------------------------
# `replica-announce-ip` is this pod's stable StatefulSet DNS name and the sentinels are
# configured with `resolve-hostnames`/`announce-hostnames`. A pod IP would work until the day
# every pod restarts at once — a node-pool upgrade — after which every sentinel's persisted
# configuration names three addresses that no longer exist and the group cannot heal itself.
# =====================================================================================
set -eu

GROUP=mageride-primary
DOMAIN="redis-peers.${POD_NAMESPACE}.svc.cluster.local"
MYSELF="${POD_NAME}.${DOMAIN}"
SENTINEL_CONF=/data/sentinel.conf
CONF=/tmp/redis.conf

log() { echo "[redis-entrypoint] $*"; }

master=""

# 1 — ask the other members' sentinels.
for i in 0 1 2; do
  peer="redis-${i}.${DOMAIN}"
  if [ "$peer" = "$MYSELF" ]; then
    continue
  fi
  answer="$(redis-cli -h "$peer" -p 26379 -t 2 sentinel get-master-addr-by-name "$GROUP" 2>/dev/null | head -1 || true)"
  if [ -n "$answer" ]; then
    master="$answer"
    log "sentinel on ${peer} reports the primary is ${master}"
    break
  fi
done

# 2 — our own sentinel's last word, from the volume.
if [ -z "$master" ] && [ -f "$SENTINEL_CONF" ]; then
  master="$(grep -E "^sentinel monitor ${GROUP} " "$SENTINEL_CONF" | tail -1 | cut -d' ' -f4 || true)"
  if [ -n "$master" ]; then
    log "no sentinel answered; ${SENTINEL_CONF} last recorded ${master}"
  fi
fi

# 3 — the one-time seed.
if [ -z "$master" ]; then
  master="redis-0.${DOMAIN}"
  log "cold start with no sentinel state anywhere: seeding ${master} as the primary"
fi

{
  echo "port 6379"
  echo "dir /data"

  # --- the three settings base/data/redis.yaml calls load-bearing, unchanged -----------
  # A hard kill loses at most one second of writes (ADD §9.4).
  echo "appendonly yes"
  echo "appendfsync everysec"
  # Redis holds the dispatch and ride LOCKS. An eviction policy that dropped one would hand
  # two drivers the same ride, so the policy makes that impossible rather than merely alerted.
  echo "maxmemory-policy noeviction"
  # D-07's other half: dispatch-svc subscribes to `__keyevent@0__:expired` so an
  # `offer:{rideId}` lapsing reassigns the offer without waiting for the durable sweep.
  echo "notify-keyspace-events Ex"

  # --- what Sentinel adds ---------------------------------------------------------------
  # A primary that cannot see a replica stops accepting writes. That is deliberate and it is
  # the whole reason a lock in Redis is safe here: an isolated primary that kept taking
  # SETNX on `lock:driver:{driverId}` while a sentinel quorum promoted another one would
  # break D-03's active-session mutex and R-02's single-winner accept — silently, and with
  # two drivers dispatched to one ride as the visible symptom.
  echo "min-replicas-to-write 1"
  echo "min-replicas-max-lag 10"

  # How this member identifies itself to the primary and, through the primary's INFO, to
  # every sentinel.
  echo "replica-announce-ip ${MYSELF}"
  echo "replica-announce-port 6379"

  # No bind directive and no password, so protected mode would otherwise refuse every
  # connection that is not from loopback — which in Kubernetes is every connection there is.
  # The boundary is that 6379 is a ClusterIP the Ingress never routes to; a password is
  # D7' §13's to add and would have to reach `min-replicas-to-write`'s replication link too
  # (`masterauth`), so it is one change and not this one.
  echo "protected-mode no"

  if [ "$master" != "$MYSELF" ]; then
    echo "replicaof ${master} 6379"
  fi
} > "$CONF"

log "starting as $( [ "$master" = "$MYSELF" ] && echo PRIMARY || echo "a replica of ${master}" )"
exec redis-server "$CONF"
