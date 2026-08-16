#!/bin/sh
# =====================================================================================
# Seed this member's sentinel configuration once, then run it (C132).
#
# THE FILE IS WRITTEN ONCE AND NEVER REWRITTEN BY THIS SCRIPT. Sentinel rewrites its own
# configuration as it learns the topology — the current primary, the other sentinels' run ids,
# the known replicas — and that file lives on the PVC, so it is the group's memory across a
# restart. Regenerating it on every boot would erase exactly the state that makes a cold start
# recoverable, and would re-point a healthy group at whatever this script guessed.
#
# `resolve-hostnames` has to appear before `monitor`, or sentinel refuses a hostname there.
# =====================================================================================
set -eu

GROUP=mageride-primary
QUORUM=2
DOMAIN="redis-peers.${POD_NAMESPACE}.svc.cluster.local"
MYSELF="${POD_NAME}.${DOMAIN}"
CONF=/data/sentinel.conf

log() { echo "[sentinel-entrypoint] $*"; }

if [ ! -f "$CONF" ]; then
  master=""
  for i in 0 1 2; do
    peer="redis-${i}.${DOMAIN}"
    if [ "$peer" = "$MYSELF" ]; then
      continue
    fi
    answer="$(redis-cli -h "$peer" -p 26379 -t 2 sentinel get-master-addr-by-name "$GROUP" 2>/dev/null | head -1 || true)"
    if [ -n "$answer" ]; then
      master="$answer"
      break
    fi
  done
  if [ -z "$master" ]; then
    master="redis-0.${DOMAIN}"
  fi
  log "no ${CONF} on this volume — seeding it with primary ${master}"

  cat > "$CONF" <<EOF
port 26379
dir /data

# Track members by their StatefulSet DNS names rather than by pod IP: a pod IP is valid until
# the pod is rescheduled, and every pod is rescheduled on a node-pool upgrade.
sentinel resolve-hostnames yes
sentinel announce-hostnames yes
sentinel announce-ip ${MYSELF}
sentinel announce-port 26379

# Quorum 2 of 3 — two sentinels must agree the primary is unreachable before one of them may
# promote. With three members that is also the majority needed to authorise the failover, so
# a single partitioned sentinel can neither promote nor block.
sentinel monitor ${GROUP} ${master} 6379 ${QUORUM}

# 5 s to declare a primary down. Fast enough that ADD §15's 10-minute Redis RTO is never the
# binding constraint, slow enough that a GC pause or a node's brief network blip does not
# trigger a promotion.
sentinel down-after-milliseconds ${GROUP} 5000
sentinel failover-timeout ${GROUP} 30000

# One replica resynchronises at a time. Both at once would leave the new primary serving the
# live map while it streams a full RDB to two peers.
sentinel parallel-syncs ${GROUP} 1
EOF
else
  log "${CONF} exists — using the group state this member already had"
fi

exec redis-server "$CONF" --sentinel
