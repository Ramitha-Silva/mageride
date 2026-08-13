#!/usr/bin/env bash
# =====================================================================================
# load/step.sh — find the rate at which the ingest chain stops keeping up.
#
#   bash load/step.sh                          # 25 -> 3000 msg/s, 45 s a step
#   bash load/step.sh --rates 20,50,100,200 --seconds 30
#
# ------------------------------------------------------------------------------------
# WHY A SWEEP AND NOT A SINGLE PROFILE
# ------------------------------------------------------------------------------------
# `load/ingest.js` answers "did the platform carry ADD §3.2's rate". This answers the question
# that turned out to matter more: "at what rate does it stop carrying anything", and it is the
# only shape that finds it, because the failure is SILENT on the client side. Every publish is
# PUBACKed by EMQX whether or not the broker then delivers it to mqtt-bridge-svc — so a client,
# a k6 summary and the `mqtt_puback` counter all report success while the samples are being
# discarded inside the broker.
#
# The number that tells the truth is EMQX's own `delivery.dropped.queue_full`, and this script
# reads it either side of every step alongside the bridge's `mageride.mqtt.bridge.forwarded`
# and Redpanda's end offsets. Three independent counts of the same messages.
# =====================================================================================
set -uo pipefail

cd "$(dirname -- "${BASH_SOURCE[0]}")" || exit 2
LOAD_DIR="$PWD"
cd .. || exit 2

COMPOSE="infra/replica/docker-compose.light-replica.yml"
OUT="$LOAD_DIR/out"
mkdir -p "$OUT"

RATES="25,50,100,200,400,800,1500,3000"
SECONDS_PER_STEP=45
PER_CONNECTION=4

while [ $# -gt 0 ]; do
  case "$1" in
    --rates) RATES="$2"; shift 2 ;;
    --seconds) SECONDS_PER_STEP="$2"; shift 2 ;;
    --per-connection) PER_CONNECTION="$2"; shift 2 ;;
    *) echo "unknown argument: $1" >&2; exit 2 ;;
  esac
done

set -a
# shellcheck disable=SC1091
. infra/replica/.env.replica
set +a

emqx_metric() {
  docker compose -f "$COMPOSE" exec -T emqx /opt/emqx/bin/emqx ctl broker metrics 2>/dev/null \
    | awk -v k="$1" -F: '$1 ~ k { gsub(/ /, "", $2); print $2; exit }'
}

bridge_forwarded() {
  docker compose -f "$COMPOSE" exec -T hot-path \
    sh -c 'wget -qO- http://127.0.0.1:5200/metrics' 2>/dev/null \
    | awk '/^mageride_mqtt_bridge_forwarded_total/{s+=$NF} END{printf "%d", s+0}'
}

processed() {
  docker compose -f "$COMPOSE" exec -T hot-path \
    sh -c 'wget -qO- http://127.0.0.1:5200/metrics' 2>/dev/null \
    | awk '/^mageride_positions_processed_total/{s+=$NF} END{printf "%d", s+0}'
}

printf '\n\033[1m  target   conns |  published   delivered   forwarded   indexed | queue_full   carried\033[0m\n'
printf '  ---------------+-----------------------------------------------+---------------------\n'

ROWS='['
first=1

IFS=',' read -ra WANTED <<< "$RATES"
for target in "${WANTED[@]}"; do
  connections=$(( (target + PER_CONNECTION - 1) / PER_CONNECTION ))

  r0=$(emqx_metric "messages.qos1.received")
  s0=$(emqx_metric "messages.qos1.sent")
  d0=$(emqx_metric "delivery.dropped.queue_full")
  f0=$(bridge_forwarded)
  p0=$(processed)

  k6 run --quiet "$LOAD_DIR/ingest.js" \
    -e LOAD_CONNECTIONS="$connections" \
    -e LOAD_RATE="$PER_CONNECTION" \
    -e LOAD_DURATION="$SECONDS_PER_STEP" \
    -e LOAD_WATCH=0 \
    >/dev/null 2>&1

  # The bridge acknowledges after the produce, so a tail of in-flight work outlives the run.
  sleep 10

  r1=$(emqx_metric "messages.qos1.received")
  s1=$(emqx_metric "messages.qos1.sent")
  d1=$(emqx_metric "delivery.dropped.queue_full")
  f1=$(bridge_forwarded)
  p1=$(processed)

  published=$((r1 - r0))
  delivered=$((s1 - s0))
  dropped=$((d1 - d0))
  forwarded=$((f1 - f0))
  indexed=$((p1 - p0))

  carried=0
  [ "$published" -gt 0 ] && carried=$(python3 -c "print(f'{100*$indexed/$published:.1f}')")

  printf '  %6s   %5s | %10s  %10s  %10s  %8s | %10s   %6s%%\n' \
    "$target" "$connections" "$published" "$delivered" "$forwarded" "$indexed" "$dropped" "$carried"

  [ "$first" = "1" ] || ROWS="${ROWS},"
  first=0
  ROWS="${ROWS}{\"targetMsgPerSecond\":${target},\"connections\":${connections},\"seconds\":${SECONDS_PER_STEP},\"publishedToBroker\":${published},\"deliveredToBridge\":${delivered},\"forwardedToRedpanda\":${forwarded},\"indexedInRedis\":${indexed},\"droppedQueueFull\":${dropped},\"carriedPercent\":${carried}}"

  # Let the broker's queues and the writer's buffer settle, so the next step starts from rest
  # rather than from the previous step's backlog.
  sleep 15
done

ROWS="${ROWS}]"
printf '%s' "$ROWS" | python3 -m json.tool > "$OUT/step.json" 2>/dev/null || printf '%s' "$ROWS" > "$OUT/step.json"

echo
echo "  wrote load/out/step.json"
echo
echo "  'published' is what EMQX accepted and PUBACKed. 'delivered' is what it handed to"
echo "  mqtt-bridge-svc's shared subscription. The gap between them is queue_full: messages the"
echo "  broker took responsibility for at QoS 1 and then discarded, invisibly to every client."
