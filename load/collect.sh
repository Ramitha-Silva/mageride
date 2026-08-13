#!/usr/bin/env bash
# =====================================================================================
# load/collect.sh — what the SERVER saw, before and after a profile.
#
#   bash load/collect.sh open  <label>     # snapshot, then start sampling in the background
#   bash load/collect.sh close <label>     # stop sampling, write load/out/<label>.server.json
#
# ------------------------------------------------------------------------------------
# WHY THIS EXISTS BESIDE THE k6 SUMMARY
# ------------------------------------------------------------------------------------
# k6 measures what a client experienced. Three of C129's deliverables are about what the
# platform did, and no client can see any of them:
#
#   * `mageride.positions.processed` / `.dropped{reason}` — how much of what was published was
#     actually indexed, and what refused the rest. A publisher that is dropped at the anti-spoof
#     gate looks identical from the outside to one that is indexed.
#   * `telemetry.positions` rows — ADD §16.4's write load, which §16.4 itself does not model
#     (it prices only the 1/min operational downsample).
#   * `rides.rides.created_at -> dispatch.offers.sent_at` — the unquantised offer latency, and
#     `rides.outbox.created_at -> dispatched_at`, which is E-09's "< 50 ms median" budget.
#
# Everything here is read through the containers' own surfaces: the Prometheus endpoints the
# services already expose (`Otel__PrometheusEnabled`), `docker stats`, and psql. Nothing is
# instrumented for this suite and nothing is left behind.
# =====================================================================================
set -uo pipefail

cd "$(dirname -- "${BASH_SOURCE[0]}")" || exit 2
LOAD_DIR="$PWD"
cd .. || exit 2

COMPOSE="infra/replica/docker-compose.light-replica.yml"
OUT_DIR="$LOAD_DIR/out"
mkdir -p "$OUT_DIR"

ACTION="${1:-}"
LABEL="${2:-run}"

[ -f infra/replica/.env.replica ] || { echo "no .env.replica" >&2; exit 2; }
set -a
# shellcheck disable=SC1091
. infra/replica/.env.replica
set +a

psql_q() {
  docker compose -f "$COMPOSE" exec -T postgres \
    psql -U "${PG_USER:-mageride}" -d "${PG_DATABASE:-mageride}" -qtAX -c "$1" 2>/dev/null | tr -d ' \r'
}

# The service's own Prometheus endpoint, scraped from inside the network. `curl` is present in
# the aspnet image (the compose healthcheck uses it); `wget` is what the alpine worker has.
metrics_of() {
  local service="$1" port="$2"
  docker compose -f "$COMPOSE" exec -T "$service" \
    sh -c "curl -fsS http://127.0.0.1:${port}/metrics 2>/dev/null || wget -qO- http://127.0.0.1:${port}/metrics 2>/dev/null" 2>/dev/null
}

# One counter's value out of a Prometheus text exposition, summed over label sets.
counter() {
  local body="$1" name="$2"
  printf '%s\n' "$body" | awk -v n="$name" '
    $0 !~ /^#/ && index($0, n) == 1 { total += $NF }
    END { printf "%.0f", total + 0 }'
}

snapshot() {
  local phase="$1"
  local hot fan

  hot=$(metrics_of hot-path 5200)
  fan=$(metrics_of fanout 5001)

  python3 - "$OUT_DIR/${LABEL}.${phase}.json" <<PY
import json, sys
json.dump({
  'phase': '${phase}',
  'at': '$(date -u +%Y-%m-%dT%H:%M:%SZ)',
  'epoch': $(date +%s),
  'hotPath': {
    'bridgeForwarded': $(counter "$hot" mageride_mqtt_bridge_forwarded_total),
    'positionsProcessed': $(counter "$hot" mageride_positions_processed_total),
    'positionsDropped': $(counter "$hot" mageride_positions_dropped_total),
    'positionsImplausible': $(counter "$hot" mageride_positions_implausible_total),
    'rateViolations': $(counter "$hot" mageride_positions_rate_violations_total),
    'telemetryRowsWritten': $(counter "$hot" mageride_telemetry_rows_written_total),
    'telemetryRowsDeduped': $(counter "$hot" mageride_telemetry_rows_deduped_total),
    'telemetryRowsDeadLettered': $(counter "$hot" mageride_telemetry_rows_dead_lettered_total),
    'telemetryFlushFailures': $(counter "$hot" mageride_telemetry_flush_failures_total),
    'operationalSamples': $(counter "$hot" mageride_telemetry_operational_samples_total)
  },
  'fanout': {
    'frames': $(counter "$fan" mageride_fanout_frames_total),
    'filtered': $(counter "$fan" mageride_fanout_filtered_total),
    'signals': $(counter "$fan" mageride_fanout_signals_total)
  },
  'postgres': {
    'telemetryPositions': $(psql_q "SELECT count(*) FROM telemetry.positions;" || echo 0),
    'loadFleetPositions': $(psql_q "SELECT count(*) FROM telemetry.positions WHERE vehicle_id::text LIKE '10ad10ad-%';" || echo 0),
    'operationalSamples': $(psql_q "SELECT count(*) FROM trips.position_samples;" || echo 0),
    'rides': $(psql_q "SELECT count(*) FROM rides.rides;" || echo 0),
    'offers': $(psql_q "SELECT count(*) FROM dispatch.offers;" || echo 0),
    'outbox': $(psql_q "SELECT count(*) FROM rides.outbox;" || echo 0),
    'penalties': $(psql_q "SELECT count(*) FROM dispatch.cancellation_penalties;" || echo 0)
  },
  'logBytes': {
$(for c in app-services hot-path fanout emqx redpanda postgres haproxy; do
    id=$(docker compose -f "$COMPOSE" ps -q "$c" 2>/dev/null | head -1)
    path=$(docker inspect --format='{{.LogPath}}' "$id" 2>/dev/null)
    size=$(stat -c %s "$path" 2>/dev/null || echo 0)
    printf "    '%s': %s,\n" "$c" "$size"
  done | sed '$ s/,$//')
  }
}, open(sys.argv[1], 'w'), indent=2)
PY
}

case "$ACTION" in
  open)
    snapshot before
    # `docker stats` every 5 s for the duration of the profile. One line per container per
    # sample: the CPU share is what says which container saturated first, and it is the only
    # place that answer exists — the replica runs no cAdvisor and C119's stack is not required
    # to be up for a load run.
    ( while true; do
        docker stats --no-stream --format '{{.Name}} {{.CPUPerc}} {{.MemUsage}}' 2>/dev/null \
          | sed "s|^|$(date +%s) |"
        sleep 5
      done ) > "$OUT_DIR/${LABEL}.stats.txt" 2>/dev/null &
    echo $! > "$OUT_DIR/${LABEL}.stats.pid"
    echo "collecting for '${LABEL}'"
    ;;

  close)
    if [ -f "$OUT_DIR/${LABEL}.stats.pid" ]; then
      kill "$(cat "$OUT_DIR/${LABEL}.stats.pid")" 2>/dev/null
      rm -f "$OUT_DIR/${LABEL}.stats.pid"
    fi

    snapshot after

    # The dispatch distributions, straight out of the tables. Both are unquantised, unlike the
    # client-side poll, and the outbox one is the only measurement of E-09's stated budget.
    offers=$(psql_q "
      SELECT coalesce(json_build_object(
        'count', count(*),
        'medMs', round(percentile_cont(0.5) WITHIN GROUP (ORDER BY ms)::numeric, 1),
        'p95Ms', round(percentile_cont(0.95) WITHIN GROUP (ORDER BY ms)::numeric, 1),
        'p99Ms', round(percentile_cont(0.99) WITHIN GROUP (ORDER BY ms)::numeric, 1),
        'maxMs', round(max(ms)::numeric, 1))::text, '{}')
      FROM (SELECT extract(epoch FROM (o.sent_at - r.created_at)) * 1000 AS ms
              FROM dispatch.offers o JOIN rides.rides r ON r.id = o.ride_id
             WHERE o.sent_at > now() - interval '2 hours') s;")

    outbox=$(psql_q "
      SELECT coalesce(json_build_object(
        'count', count(*),
        'medMs', round(percentile_cont(0.5) WITHIN GROUP (ORDER BY ms)::numeric, 1),
        'p95Ms', round(percentile_cont(0.95) WITHIN GROUP (ORDER BY ms)::numeric, 1),
        'maxMs', round(max(ms)::numeric, 1))::text, '{}')
      FROM (SELECT extract(epoch FROM (dispatched_at - created_at)) * 1000 AS ms
              FROM rides.outbox
             WHERE dispatched_at IS NOT NULL AND created_at > now() - interval '2 hours') s;")

    # AL-16 / D-05: a pre-acceptance cancel must raise no penalty. Asserted rather than assumed,
    # because a load run that quietly disabled a dozen synthetic passengers would look like a
    # dispatch failure on the NEXT run and not on this one.
    penalties=$(psql_q "
      SELECT count(*) FROM dispatch.cancellation_penalties p
        JOIN iam.users u ON u.id = p.passenger_id
       WHERE u.phone LIKE '+9477003%';")

    states=$(psql_q "
      SELECT coalesce(json_object_agg(state, n)::text, '{}') FROM (
        SELECT r.state, count(*) AS n FROM rides.rides r
          JOIN iam.users u ON u.id = r.passenger_id
         WHERE u.phone LIKE '+9477003%' AND r.created_at > now() - interval '2 hours'
         GROUP BY r.state) s;")

    python3 - "$OUT_DIR/${LABEL}.server.json" "$OUT_DIR/${LABEL}.before.json" \
              "$OUT_DIR/${LABEL}.after.json" "$OUT_DIR/${LABEL}.stats.txt" \
              "$offers" "$outbox" "$penalties" "$states" <<'PY'
import json, sys

out, before_path, after_path, stats_path, offers, outbox, penalties, states = sys.argv[1:9]

before = json.load(open(before_path))
after = json.load(open(after_path))
seconds = max(1, after['epoch'] - before['epoch'])


def delta(group):
    return {k: after[group][k] - before[group].get(k, 0) for k in after[group]}


def rate(group):
    return {k: round(v / seconds, 1) for k, v in delta(group).items()}


# Peak CPU per container over the run. The mean is misleading on a profile with a ramp in it:
# what names the first bottleneck is which container SATURATED, not which averaged highest.
peak, mem = {}, {}
try:
    for line in open(stats_path):
        parts = line.split()
        if len(parts) < 4:
            continue
        name, cpu = parts[1], parts[2].rstrip('%')
        try:
            value = float(cpu)
        except ValueError:
            continue
        peak[name] = max(peak.get(name, 0.0), value)
        mem[name] = parts[3]
except FileNotFoundError:
    pass


def as_json(text):
    try:
        return json.loads(text) if text else {}
    except json.JSONDecodeError:
        return {}


json.dump({
    'windowSeconds': seconds,
    'deltas': {g: delta(g) for g in ('hotPath', 'fanout', 'postgres')},
    'perSecond': {g: rate(g) for g in ('hotPath', 'fanout', 'postgres')},
    'peakCpuPercent': dict(sorted(peak.items(), key=lambda kv: -kv[1])),
    'memoryAtEnd': mem,
    'logGrowthBytes': delta('logBytes'),
    'logGrowthBytesPerSecond': rate('logBytes'),
    'offerLatencyFromDb': as_json(offers),
    'outboxLatencyFromDb': as_json(outbox),
    'loadPassengerPenalties': int(penalties or 0),
    'loadRideStates': as_json(states),
}, open(out, 'w'), indent=2)

print(f"  window {seconds}s")
for name, value in sorted(peak.items(), key=lambda kv: -kv[1])[:5]:
    print(f"  peak cpu  {name:42} {value:6.1f}%")
PY

    echo "  wrote ${OUT_DIR}/${LABEL}.server.json"
    ;;

  *)
    echo "usage: bash load/collect.sh open|close <label>" >&2
    exit 2
    ;;
esac
