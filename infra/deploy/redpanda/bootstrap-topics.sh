#!/usr/bin/env sh
# =====================================================================================
# Redpanda topic bootstrap — D6' §2.1 topic registry, §2.3 DLQ convention (C009)
#
# Idempotent: creating a topic that already exists is not an error, and the script
# re-applies the configuration of every topic it finds so a hand-edited retention is
# corrected on the next `dev-up`.
#
# Runs as the `redpanda-init` one-shot in both dev compose files, and by hand:
#
#   docker compose -f infra/docker-compose.dev.slim.yml run --rm redpanda-init
#   REDPANDA_BROKERS=localhost:19092 sh infra/deploy/redpanda/bootstrap-topics.sh
#
# `sh`, not `bash`: the redpandadata/redpanda image ships busybox ash only.
# =====================================================================================
set -eu

BROKERS="${REDPANDA_BROKERS:-redpanda:9092}"

# Partitions and replication factor: lightweight-production-replica.md "Container 3"
# pins RF=1 / partitions=3 for the replica. Production is RF=3 across 3 brokers (D6' §2)
# — the topic names and keys do not change, only these two numbers.
PARTITIONS="${REDPANDA_PARTITIONS:-3}"
REPLICAS="${REDPANDA_REPLICAS:-1}"

# Retention. No spec pins a per-topic retention, so these are dev defaults chosen from
# what each topic is for, not spec values:
#   telemetry.*  high volume, and durable in trips.position_samples /
#                telemetry.positions once persistence-writer has consumed it  -> 24 h
#   *.events     domain events; a consumer that has been down over a weekend
#                must still be able to catch up                               -> 7 d
#   *.dlq        a dead letter that expires before anyone reads it is not a
#                dead-letter queue                                            -> 30 d
TELEMETRY_RETENTION_MS="${REDPANDA_TELEMETRY_RETENTION_MS:-86400000}"
EVENT_RETENTION_MS="${REDPANDA_EVENT_RETENTION_MS:-604800000}"
DLQ_RETENTION_MS="${REDPANDA_DLQ_RETENTION_MS:-2592000000}"

log() { printf '  %s\n' "$*"; }

ensure_topic() { # ensure_topic <name> <partitions> <retention_ms>
  name="$1"; parts="$2"; retention="$3"

  if rpk topic describe "$name" --brokers "$BROKERS" >/dev/null 2>&1; then
    log "exists   $name"
  else
    # Two callers racing (a compose restart and a manual run) both see "not found" and
    # both create; the loser gets TOPIC_ALREADY_EXISTS, which is success here.
    if out=$(rpk topic create "$name" \
               --brokers "$BROKERS" \
               --partitions "$parts" \
               --replicas "$REPLICAS" 2>&1); then
      log "created  $name  partitions=$parts replicas=$REPLICAS"
    else
      case "$out" in
        *ALREADY_EXISTS*|*already\ exists*) log "exists   $name (raced)" ;;
        *) printf 'error: could not create %s: %s\n' "$name" "$out" >&2; exit 1 ;;
      esac
    fi
  fi

  rpk topic alter-config "$name" --brokers "$BROKERS" \
    --set retention.ms="$retention" \
    --set cleanup.policy=delete >/dev/null
}

# Each primary topic gets its D6' §2.3 dead-letter partner. One partition on the DLQ — it
# is drained by a human or a replay tool, and total ordering beats parallelism there.
ensure_pair() { # ensure_pair <name> <retention_ms>
  ensure_topic "$1" "$PARTITIONS" "$2"
  ensure_topic "$1.dlq" 1 "$DLQ_RETENTION_MS"
}

printf 'Bootstrapping Redpanda topics on %s (D6 §2.1)\n' "$BROKERS"

# rpk's own retry is per-request; a compose healthcheck can report healthy a moment before
# the controller leader is elected, so wait for the cluster to answer a metadata call.
i=0
until rpk cluster info --brokers "$BROKERS" >/dev/null 2>&1; do
  i=$((i + 1))
  [ "$i" -lt 60 ] || { echo "error: $BROKERS did not become reachable within 60s" >&2; exit 1; }
  sleep 1
done

# ------------------------------------------------------------------------------------
# D6' §2.1 topic registry. The partition key in each comment is a producer-side choice,
# not a topic setting — it is recorded here because it is what makes per-aggregate
# ordering work and the easiest thing for a new producer to get wrong.
# ------------------------------------------------------------------------------------
ensure_pair telemetry.raw        "$TELEMETRY_RETENTION_MS"   # key vehicleId — mqtt-bridge-svc
ensure_pair telemetry.normalized "$TELEMETRY_RETENTION_MS"   # key vehicleId — position-processor
ensure_pair trip.events          "$EVENT_RETENTION_MS"       # key vehicleId — trip-state-svc
ensure_pair ride.events          "$EVENT_RETENTION_MS"       # key rideId    — ride-svc (outbox)
ensure_pair dispatch.events      "$EVENT_RETENTION_MS"       # key rideId    — dispatch-svc
# Not one of D6' §2.1's six. share.revoked (D-22) has a producer and a consumer in the specs and
# no topic; C028 added it, and the handoff raises the micro-change-set against §2.1.
ensure_pair registry.events      "$EVENT_RETENTION_MS"       # key vehicleId — registry-svc (outbox)
# Not one of D6' §2.1's six either. tracker.bound/tracker.unbound (D6' §4.3) and
# tracker.revoked (T-12) have producers and consumers in the specs and no topic; C030 added
# it, and the handoff raises the micro-change-set against §2.1.
ensure_pair provisioning.events  "$EVENT_RETENTION_MS"       # key vehicleId — provisioning-svc (outbox)
# Not one of D6' §2.1's six either. fraud.suspected (E-07) has a producer (ADD §6
# "emits fraud.suspected for admin review") and a consumer (ADD §12.6's admin fraud queue)
# and no topic; C033 added it, and the handoff raises the micro-change-set against §2.1.
ensure_pair reputation.events    "$EVENT_RETENTION_MS"       # key userId    — reputation-svc (outbox)
ensure_pair audit.events         "$EVENT_RETENTION_MS"       # key entityId  — all (admin-bff)

echo
rpk topic list --brokers "$BROKERS"
