#!/usr/bin/env bash
# =====================================================================================
# 40 — emqx-loss.  The MQTT broker goes away and every device on the platform is disconnected.
#
# ADD §14.1:
#
#   | EMQX node failure | Vehicles on that node reconnect
#   |                   | Load balancer detects TCP failure; clients reconnect
#   |                   | to healthy node within 5 s |
#
# ------------------------------------------------------------------------------------
# THERE IS NO HEALTHY NODE HERE EITHER, AND THE ROW STILL HAS A TESTABLE HALF
# ------------------------------------------------------------------------------------
# ADD §14's MVP column says "Single node (accepted risk)"; §14.1's row is written for the 3-node
# production cluster. What can be measured on one node is the first clause and the recovery:
#
#   * does HAProxy DETECT the failure, or does it accept a connection into a hole? A client that
#     connects and hangs is worse than one that is refused — the driver app's reconnect backoff
#     never starts, and the "5 s" the row promises never begins to count.
#   * how long after EMQX returns can a device get a CONNACK again? That is the real reconnect
#     figure this deployment has in place of the 5 s.
#
# The client here is `openssl s_client` against the 8883 MQTTS passthrough and a plain TCP probe
# of 8084 — enough to answer "can a socket be established", which is the whole of the first clause.
# Publishing needs a JWT and a CBOR codec and belongs to drills 60–62.
# =====================================================================================

drill_begin "40" "MQTT broker down" \
  "ADD §14.1 (EMQX node failure) · ADD §14 (MVP = single node, accepted risk) · R-09" \
  "EMQX — every driver app session, every hardware tracker on 8883, the bridge's shared subscription" \
  "docker compose start emqx"

# `mqtt_reachable` asks HAProxy for a TCP connection on the MQTT-over-WSS listener. It goes through
# the EDGE rather than to the container, because the claim under test is about the load balancer.
mqtt_reachable() { timeout 4 bash -c "</dev/tcp/127.0.0.1/${HAPROXY_MQTT_WSS_PORT:-8084}" 2>/dev/null; }
mqtts_reachable() { timeout 4 bash -c "</dev/tcp/127.0.0.1/${HAPROXY_MQTTS_PORT:-8883}" 2>/dev/null; }

expect "the 8084 WSS listener accepts a connection before the fault" \
  "$(mqtt_reachable && echo yes || echo no)" "yes"

bridge_forwarded_before=$(metric "$(metrics_of hot-path 5200)" mageride_mqtt_bridge_forwarded_total)
sessions_before=$(emqx_ctl broker metrics 2>/dev/null | grep -c . || echo 0)

arm_rollback "dc start emqx"

stopped_at=$(now_ms)
dc stop emqx >/dev/null 2>&1
ok "emqx stopped after $(human_ms "$(since_ms "$stopped_at")")"
sleep 2

degraded_table_open

# -------------------------------------------------------------------------------------
# Clause 1 — does the edge DETECT it?
# -------------------------------------------------------------------------------------
probe_started=$(now_ms)
if mqtt_reachable; then
  detect_ms=$(since_ms "$probe_started")
  degraded_row "MQTT over WSS (\`:${HAPROXY_MQTT_WSS_PORT:-8084}\`)" \
    "HAProxy still accepted a TCP connection after ${detect_ms} ms" \
    "\"Load balancer detects TCP failure; clients reconnect … within 5 s\""
  finding MED "HAProxy accepts MQTT connections into a dead broker for as long as its health check \
takes to notice. With EMQX stopped, a TCP connect to the 8084 listener still succeeded after \
${detect_ms} ms: \`haproxy.replica.cfg\` does put \`check\` on \`server emqx emqx:8084\`, so the \
backend is taken out eventually, but the \`bind\` is accepted by HAProxy itself before any backend \
is chosen — a driver app therefore gets an established socket and no CONNACK rather than the \
connection failure its reconnect backoff is written against (ADD §7.5.3: \"jittered exponential \
reconnect backoff, 1 s–60 s\"). The 5 s in §14.1 assumes the LB has already taken the dead node \
out of rotation; nothing here says how long that is allowed to take, and on a single-node stack \
there is no other node to be taken to."
else
  detect_ms=$(since_ms "$probe_started")
  degraded_row "MQTT over WSS (\`:${HAPROXY_MQTT_WSS_PORT:-8084}\`)" \
    "connection refused after ${detect_ms} ms" \
    "\"Load balancer detects TCP failure; clients reconnect … within 5 s\""
  ok "the edge refuses MQTT within ${detect_ms} ms — a client's reconnect backoff can start"
fi

degraded_row "MQTTS trackers (\`:${HAPROXY_MQTTS_PORT:-8883}\`)" \
  "$(mqtts_reachable && echo 'accepts TCP' || echo 'refused')" \
  "same row — hardware trackers are on this listener"

# -------------------------------------------------------------------------------------
# What the rest of the platform does while the ingest plane is dark
# -------------------------------------------------------------------------------------
nearby=$(probe_nearby)
degraded_row "Live map (\`GET /v1/nearby\`)" "\`${nearby}\` (code, limitedLive, vehicles)" \
  "not described — positions stop arriving, the last ones stay in Redis"
expect "the live map still answers with the broker down" "$(printf '%s' "$nearby" | awk '{print $1}')" "200"

# The HTTP plane must be untouched: MQTT is the ingest plane and, per infra/CLAUDE.md, is never
# routed through the API gateway. If a booking fails here, something crosses that fence.
driver_online 0 >/dev/null 2>&1
arm_rollback "driver_offline 0"
ride=$(request_ride 0 2>/dev/null)
if [ -n "$ride" ]; then
  arm_rollback "cancel_ride '${ride}' 0"
  ok "the booking plane is unaffected by the broker (ride ${ride:0:8}…)"
  degraded_row "Booking (\`POST /v1/rides/request\`)" "accepted" "not described"
else
  bad "a booking was refused while EMQX was down"
  finding HIGH "\`POST /v1/rides/request\` failed with EMQX stopped. MQTT is the ingest plane and \
infra/CLAUDE.md's fence says it is never routed through the API gateway; a booking that depends on \
the broker being up crosses it."
  degraded_row "Booking (\`POST /v1/rides/request\`)" "refused" "not described"
fi

sos_degraded_row "$(probe_sos 0)"

# mqtt-bridge-svc holds the shared subscription. Its container is hot-path's, and hot-path also
# carries persistence-writer and fleet-health — so the question is whether one dead dependency
# takes three healthy services with it.
hotpath_health=$(dc ps --format '{{.Service}} {{.Health}}' 2>/dev/null | awk '$1 == "hot-path" {print $2}')
degraded_row "hot-path container (bridge + processor + writer + fleet-health)" \
  "container ${hotpath_health:-unknown}" "not described"
expect "hot-path survived the loss of its broker" "$hotpath_health" "healthy"

degraded_table_close

# -------------------------------------------------------------------------------------
# Recovery — the number this deployment has in place of §14.1's 5 s
# -------------------------------------------------------------------------------------
outage_ms=$(since_ms "$stopped_at")
recovery_started=$(now_ms)
dc start emqx >/dev/null 2>&1

if emqx_ms=$(wait_for 180 3 service_healthy emqx); then
  ok "emqx healthy again $(human_ms "$emqx_ms") after start"
else
  bad "emqx did not report healthy within 180 s"
fi

if socket_ms=$(wait_for 120 2 mqtt_reachable); then
  ok "a device could open a socket again $(human_ms "$socket_ms") after the broker was started"
else
  bad "the 8084 listener was still unreachable two minutes after EMQX started"
fi

# The bridge has to re-subscribe by itself. Its shared subscription is `$share/posGroup/…` and a
# broker restart drops it; if nothing re-establishes it, telemetry stops for good and every
# container still reports healthy.
# 240 s, not 120: on the first run of this drill the bridge took longer than two minutes and the
# drill called it a failure to re-subscribe when what it had found was a slow reconnect. The number
# is the deliverable either way — it is how long telemetry is dark after a broker restart, and
# nothing on the platform reports it.
if resub_ms=$(wait_for 240 3 bridge_subscribed); then
  ok "mqtt-bridge-svc re-established its shared subscription $(human_ms "$resub_ms") after the broker returned"
  if [ "$resub_ms" -gt 60000 ]; then
    finding MED "Telemetry stays dark for $(human_ms "$resub_ms") after the broker comes back, \
and nothing says so. mqtt-bridge-svc's MQTTnet client re-established its \`\$share/posGroup/…\` \
subscription that long after EMQX was reachable again — during which every container is healthy, \
the broker accepts publishes and PUBACKs them, and nothing reaches \`telemetry.raw\`. That is the \
same silent-loss shape as load/report.md's central finding, arrived at from a different \
direction: the publisher is told the sample was accepted and the subscriber that would have \
carried it is not there."
  fi
else
  finding HIGH "mqtt-bridge-svc did not re-subscribe within four minutes of EMQX coming back: \
EMQX lists no \`svc-mqtt-bridge\` client with a subscription. Every container is healthy, the \
broker accepts publishes and PUBACKs them, and nothing reaches \`telemetry.raw\`."
fi

report ""
report "| EMQX recovery | Measured |"
report "|---|---|"
report "| Outage length | $(human_ms "$outage_ms") |"
report "| Container healthy again | $(human_ms "${emqx_ms:-0}") |"
report "| A device could open a socket | $(human_ms "${socket_ms:-0}") after start |"
report "| Bridge re-subscribed | $(human_ms "${resub_ms:-0}") after start |"
report "| ADD §14.1's promise | reconnect to a **healthy node** within 5 s — there is no second node on this stack (ADD §14 MVP column: \"Single node (accepted risk)\") |"
report ""

drill_end 180
