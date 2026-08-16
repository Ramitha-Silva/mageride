#!/usr/bin/env bash
# =====================================================================================
# 61 — replay-flood.  Every reconnected vehicle empties its offline buffer at once (R-09, R-17).
#
# ADD §7.5.1: the live/replay split "prevents a reconnect storm — where every vehicle replays its
# local buffer — from drowning live samples; mqtt-bridge-svc consumes both but applies a lower
# rate-limit and lower priority to `pos/replay`."
#
# The claim is comparative, so the measurement is too: one vehicle publishes on `pos/live` for the
# whole run and the fleet floods `pos/replay` around it. What matters is the control's
# acknowledgement latency and count, not the flood's throughput.
# =====================================================================================

drill_begin "61" "Replay flood" \
  "R-09 / R-17 (ADD §7.5.1) · MqttBridge:ThrottleReplay, ReplaySamplesPerSecond=20, ReplayQueueDepth=256" \
  "the bridge's replay lane and EMQX's queues — client-side only, nothing is stopped" \
  "the generator's sessions close themselves; no server state is changed"

command -v k6 >/dev/null 2>&1 || {
  bad "drill 61 could not run: k6 is not on PATH"
  drill_end 30
  return 0 2>/dev/null || true
}

FLEET="${CHAOS_FLOOD_FLEET:-40}"
DURATION="${CHAOS_FLOOD_DURATION:-30}"

hot_before=$(metrics_of hot-path 5200)
throttled_before=$(metric "$hot_before" mageride_mqtt_bridge_replay_throttled_total)
shed_before=$(metric "$hot_before" mageride_mqtt_bridge_replay_shed_total)
forwarded_before=$(metric "$hot_before" mageride_mqtt_bridge_forwarded_total)
dropped_before=$(emqx_ctl broker metrics 2>/dev/null | awk -F: '/delivery.dropped.queue_full/ {gsub(/ /,"",$2); print $2; exit}')

mkdir -p "${CHAOS_DIR}/out"
summary="${CHAOS_DIR}/out/replay-flood.json"

flood_started=$(now_ms)
k6 run --quiet "${CHAOS_DIR}/k6/replay-flood.js" \
  -e CHAOS_FLEET="$FLEET" -e CHAOS_DURATION="$DURATION" \
  --summary-export="$summary" >/dev/null 2>&1
flood_ms=$(since_ms "$flood_started")

read_metric() { jq -r "${1} // 0" "$summary" 2>/dev/null | head -1; }

replay_pub=$(read_metric '.metrics.flood_replay_published.count')
replay_ack=$(read_metric '.metrics.flood_replay_acked.count')
live_pub=$(read_metric '.metrics.flood_live_published.count')
live_ack=$(read_metric '.metrics.flood_live_acked.count')
live_med=$(read_metric '.metrics.flood_live_ack_ms.med')
live_p95=$(read_metric '.metrics.flood_live_ack_ms["p(95)"]')
live_max=$(read_metric '.metrics.flood_live_ack_ms.max')

hot_after=$(metrics_of hot-path 5200)
throttled=$(( $(metric "$hot_after" mageride_mqtt_bridge_replay_throttled_total) - throttled_before ))
shed=$(( $(metric "$hot_after" mageride_mqtt_bridge_replay_shed_total) - shed_before ))
forwarded=$(( $(metric "$hot_after" mageride_mqtt_bridge_forwarded_total) - forwarded_before ))
dropped_after=$(emqx_ctl broker metrics 2>/dev/null | awk -F: '/delivery.dropped.queue_full/ {gsub(/ /,"",$2); print $2; exit}')

replay_rate=$(python3 -c "print(f'{${replay_pub:-0} / max(${flood_ms}/1000.0, 0.001):.0f}')")

note "${FLEET} vehicles published ${replay_pub} replay samples (~${replay_rate}/s) over $(human_ms "$flood_ms")"
note "the bridge throttled ${throttled} and shed ${shed}; EMQX's delivery.dropped.queue_full moved by $(( ${dropped_after:-0} - ${dropped_before:-0} ))"

degraded_table_open
degraded_row "Replay lane (\`veh/+/pos/replay\`)" \
  "${replay_pub} published, ${replay_ack} PUBACKed at ~${replay_rate}/s; bridge throttled ${throttled}, shed ${shed}" \
  "\"replay throttled … lower rate-limit and lower priority\""
degraded_row "Live lane (\`veh/+/pos/live\`, control vehicle)" \
  "${live_ack}/${live_pub} PUBACKed · ack med ${live_med} ms, p95 ${live_p95} ms, max ${live_max} ms" \
  "the split exists so this column does not move"
degraded_row "Bridge forwarding to \`telemetry.raw\`" "${forwarded} payloads across the run" "not described"
degraded_table_close

# -------------------------------------------------------------------------------------
# 1. Is the replay lane actually throttled, or merely another topic?
# -------------------------------------------------------------------------------------
if [ "$throttled" -gt 0 ] || [ "$shed" -gt 0 ]; then
  ok "R-09's replay throttle engaged: ${throttled} samples delayed, ${shed} shed \
(MqttBridge:ReplaySamplesPerSecond=20 against ~${replay_rate}/s offered)"
else
  finding MED "A ~${replay_rate}/s replay flood produced no throttling and no shedding: \
\`mageride.mqtt.bridge.replay_throttled\` and \`…replay_shed\` did not move, with \
\`MqttBridge__ThrottleReplay=true\` and \`ReplaySamplesPerSecond=20\` on the container. Either the \
samples never reached the bridge — EMQX's \`delivery.dropped.queue_full\` moved by \
$(( ${dropped_after:-0} - ${dropped_before:-0} )) over the same window, which is load/report.md's \
central finding and would mean the throttle was never asked to do anything — or the lane's limiter \
is not on this path. The distinction matters: the first is a broker sizing problem and the second \
would mean a replay flood reaches the ingest chain ungoverned."
fi

# -------------------------------------------------------------------------------------
# 2. THE ASSERTION: what did the live lane pay?
# -------------------------------------------------------------------------------------
if [ "${live_pub:-0}" -eq 0 ]; then
  bad "the control vehicle published nothing — the flood profile did not run correctly"
elif [ "${live_ack:-0}" -ge "${live_pub:-1}" ]; then
  ok "R-09 HELD: the live lane kept every one of its ${live_pub} acknowledgements through a \
~${replay_rate}/s replay flood (ack med ${live_med} ms, max ${live_max} ms)"
else
  bad "the live lane lost $(( live_pub - live_ack )) of ${live_pub} acknowledgements under the flood"
  finding HIGH "A replay flood drowns live samples — the failure R-09 and the live/replay topic \
split exist to prevent. The control vehicle published ${live_pub} samples on \
\`veh/{id}/pos/live\` during a ~${replay_rate}/s flood of \`pos/replay\` and only ${live_ack} were \
PUBACKed. ADD §7.5.1: \"mqtt-bridge-svc consumes both but applies a lower rate-limit and lower \
priority to \`pos/replay\`\"."
fi

# A latency comparison is the softer half of the same question, and worth stating because a lane
# that keeps every ack but takes ten times as long has still been drowned.
if [ "${live_max:-0}" != "0" ]; then
  note "live-lane acknowledgement under flood: med ${live_med} ms, p95 ${live_p95} ms, max ${live_max} ms"
  report ""
  report "| Live lane under a ~${replay_rate}/s replay flood | |"
  report "|---|---|"
  report "| Samples published / acknowledged | ${live_pub} / ${live_ack} |"
  report "| PUBACK latency (med · p95 · max) | ${live_med} ms · ${live_p95} ms · ${live_max} ms |"
  report "| Replay samples throttled / shed by the bridge | ${throttled} / ${shed} |"
  report ""
fi

drill_end 60
