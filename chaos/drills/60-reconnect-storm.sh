#!/usr/bin/env bash
# =====================================================================================
# 60 — reconnect-storm.  Every device on the network arrives in the same second (R-09).
#
# ADD §7.5.3's first control: "EMQX connection rate limit per listener (e.g. 500 new
# connections/s/listener) + per ASN guardrail, so a regional 4G outage recovery cannot flood the
# broker." `emqx.conf` sets `max_conn_rate = "500/s"` on tcp, ssl and wss.
#
# The promise being tested is not that the broker survives — it is that the devices ALREADY
# connected do not pay for the ones arriving. So the drill runs one incumbent publisher through
# the whole storm and counts its acknowledgements.
# =====================================================================================

drill_begin "60" "Reconnect storm" \
  "R-09 (ADD §7.5.3, §7.5.1) · emqx.conf max_conn_rate = 500/s" \
  "EMQX's connection table and TLS handshake budget — client-side only, nothing is stopped" \
  "the generator's sessions close themselves; no server state is changed"

command -v k6 >/dev/null 2>&1 || {
  warn "k6 is not on PATH — the client-side drills need it (https://github.com/grafana/k6/releases)"
  bad "drill 60 could not run"
  drill_end 30
  return 0 2>/dev/null || true
}

SESSIONS="${CHAOS_STORM_SESSIONS:-400}"
ROUNDS="${CHAOS_STORM_ROUNDS:-3}"

conn_before=$(emqx_ctl broker metrics 2>/dev/null | awk -F: '/client.connect/ {gsub(/ /,"",$2); print $2; exit}')
forwarded_before=$(metric "$(metrics_of hot-path 5200)" mageride_mqtt_bridge_forwarded_total)

mkdir -p "${CHAOS_DIR}/out"
summary="${CHAOS_DIR}/out/storm.json"

storm_started=$(now_ms)
k6 run --quiet "${CHAOS_DIR}/k6/storm.js" \
  -e CHAOS_SESSIONS="$SESSIONS" -e CHAOS_ROUNDS="$ROUNDS" \
  --summary-export="$summary" >/dev/null 2>&1
storm_ms=$(since_ms "$storm_started")

read_metric() { jq -r "${1} // 0" "$summary" 2>/dev/null | head -1; }

attempted=$(( SESSIONS * ROUNDS ))
connected=$(read_metric '.metrics.storm_connected.count')
refused=$(read_metric '.metrics.storm_refused.count')
connack_med=$(read_metric '.metrics.storm_connack_ms.med')
connack_p95=$(read_metric '.metrics.storm_connack_ms["p(95)"]')
connack_max=$(read_metric '.metrics.storm_connack_ms.max')
inc_pub=$(read_metric '.metrics.storm_incumbent_published.count')
inc_ack=$(read_metric '.metrics.storm_incumbent_acked.count')

rate_achieved=$(python3 -c "print(f'{${connected:-0} / max(${storm_ms}/1000.0, 0.001):.0f}')")

note "${connected}/${attempted} connections established in $(human_ms "$storm_ms") (~${rate_achieved}/s), ${refused} refused"
note "CONNACK latency: med ${connack_med} ms, p95 ${connack_p95} ms, max ${connack_max} ms"

degraded_table_open
degraded_row "New connections" "${connected} accepted, ${refused} refused at ~${rate_achieved}/s" \
  "\"connection rate limit … 500 new connections/s/listener\""
degraded_row "CONNACK latency" "med ${connack_med} ms · p95 ${connack_p95} ms · max ${connack_max} ms" \
  "not described"
degraded_row "The incumbent publisher" "${inc_ack}/${inc_pub} samples acknowledged through the storm" \
  "implied — the split exists so a storm does not cost the devices that never left"
degraded_table_close

# -------------------------------------------------------------------------------------
# The assertion R-09 is actually about
# -------------------------------------------------------------------------------------
if [ "${inc_pub:-0}" -gt 0 ] && [ "${inc_ack:-0}" -ge "${inc_pub:-1}" ]; then
  ok "R-09 HELD for the incumbent: every one of its ${inc_pub} samples was PUBACKed during the storm"
elif [ "${inc_pub:-0}" -gt 0 ]; then
  bad "the incumbent lost $(( inc_pub - inc_ack )) of ${inc_pub} acknowledgements during the storm"
  finding HIGH "A reconnect storm costs the devices that never disconnected. The incumbent \
publisher had ${inc_ack} of ${inc_pub} samples acknowledged while ${connected} sessions arrived \
at ~${rate_achieved}/s. R-09 and ADD §7.5.1 exist so that a regional recovery does not drown live \
samples; a QoS-1 publisher losing PUBACKs means its in-flight window is not draining."
else
  bad "the incumbent published nothing — the storm profile did not run correctly"
fi

# -------------------------------------------------------------------------------------
# What the storm cost, honestly, including what this box could not reach
# -------------------------------------------------------------------------------------
if [ "${rate_achieved:-0}" -lt 500 ]; then
  note "the generator reached ~${rate_achieved} connections/s against a 500/s listener limit — \
this run did not reach the limit and cannot say where it binds"
  report ""
  report "> **Coverage limit.** The generator reached **~${rate_achieved} connections/s** against"
  report "> \`max_conn_rate = \"500/s\"\`. Each session is a TLS handshake plus a WebSocket upgrade"
  report "> plus a CONNECT, driven from the same eight-vCPU box as the broker, so this run measures"
  report "> what a storm COSTS and not where the broker's limit binds. The per-ASN guardrail is not"
  report "> exercised at all: every connection here has one source address, the same deployment"
  report "> property that makes the gateway's per-caller rate limit a per-platform one"
  report "> (load/report.md)."
  report ""
fi

if [ "${connack_max:-0%.*}" != "0" ]; then
  slowdown=$(python3 -c "
med = ${connack_med:-0}; mx = ${connack_max:-0}
print(f'{mx / med:.1f}' if med > 0 else 'n/a')")
  note "CONNACK latency spread across the storm: ${slowdown}x from median to max"
fi

if [ "${refused:-0}" -gt 0 ]; then
  finding MED "${refused} of ${attempted} connections were refused at ~${rate_achieved}/s, well \
under \`max_conn_rate = \"500/s\"\`. Whatever refused them is not the documented limiter — check \
\`emqx ctl listeners\` for the current connection count against \`max_connections\`, and the \
handshake budget on this box before reading it as a broker defect."
fi

# Telemetry has to still be flowing afterwards: a storm that leaves the bridge's shared
# subscription broken is drill 40's finding arriving by a different route.
forwarded_after=$(metric "$(metrics_of hot-path 5200)" mageride_mqtt_bridge_forwarded_total)
expect_at_least "the bridge kept forwarding through and after the storm" \
  "$((forwarded_after - forwarded_before))" 1

drill_end 60
