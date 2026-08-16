#!/usr/bin/env bash
# =====================================================================================
# 63 — network-partition.  app-services is severed from the internal network.
#
# Not a stop and not a crash: the container keeps running, its process keeps its heap and its open
# file handles, and every socket it holds to Postgres, Redis, Redpanda, EMQX and MinIO is silently
# black-holed. From HAProxy's side the backend simply stops answering. This is the failure a
# `docker stop` cannot reproduce — a refusal is fast and honest, a partition is neither — and the
# one a Kubernetes NetworkPolicy mistake or a node's network partition produces.
#
# ADD §14.1 has no row for it. That is the finding this drill is most likely to produce, and the
# two questions it can answer are the ones an operator has:
#
#   * does the edge FAIL, or does it hang? A 503 lets a client retry; a hung request holds a
#     connection until something times out, and the thing that times out first is usually the
#     client.
#   * does the platform come back BY ITSELF when the partition heals? A reconnected container is
#     given a new IP by Docker, and HAProxy resolves its backends by name — `haproxy.replica.cfg`
#     uses `resolvers docker`, so this drill is also the test of whether that resolver works.
#
# ------------------------------------------------------------------------------------
# THE ROLLBACK HAS TWO STAGES AND BOTH ARE ARMED BEFORE THE CUT
# ------------------------------------------------------------------------------------
# Reconnect, and — if the edge is still not serving — restart HAProxy. The second stage exists
# because a stale backend address is the expected failure here, and a chaos drill that leaves the
# edge pointed at an address nothing answers on is an outage rather than a drill.
# =====================================================================================

drill_begin "63" "Network partition — app-services severed" \
  "ADD §14.1 (no row for a partition) · §14 (stateless services, 1 replica on MVP) · D7' §5" \
  "app-services loses every socket to Postgres, Redis, Redpanda, EMQX, MinIO and HAProxy; the process keeps running" \
  "docker network connect, then restart HAProxy if the edge is still not serving"

NETWORK="${CHAOS_NETWORK:-mageride-replica_internal}"
CONTAINER=$(dc ps -q app-services 2>/dev/null | head -1)

if [ -z "$CONTAINER" ]; then
  bad "app-services is not running — nothing to partition"
  drill_end 30
  return 0 2>/dev/null || true
fi

ip_before=$(docker inspect "$CONTAINER" --format "{{(index .NetworkSettings.Networks \"${NETWORK}\").IPAddress}}" 2>/dev/null)
note "app-services is ${CONTAINER:0:12} at ${ip_before} on ${NETWORK}"

arm_rollback "dc restart haproxy"
arm_rollback "docker network connect --alias app-services --alias mageride-replica-app-services-1 '${NETWORK}' '${CONTAINER}'"

cut_at=$(now_ms)
docker network disconnect "$NETWORK" "$CONTAINER" >/dev/null 2>&1 \
  || { bad "could not disconnect app-services from ${NETWORK}"; drill_end 60; return 0 2>/dev/null || true; }
ok "app-services severed from ${NETWORK} after $(human_ms "$(since_ms "$cut_at")")"

sleep 2

degraded_table_open

# -------------------------------------------------------------------------------------
# Does the edge fail fast, or hang?
# -------------------------------------------------------------------------------------
# A tight client timeout on purpose: the question is whether HAProxy answers inside the window a
# phone would wait, and a 20 s curl timeout would make every partition look like a slow success.
probe_started=$(now_ms)
code=$(CHAOS_HTTP_TIMEOUT=8 edge_code /v1/.well-known/jwks.json)
probe_ms=$(since_ms "$probe_started")

degraded_row "The edge (\`GET /v1/.well-known/jwks.json\`)" "HTTP ${code} after ${probe_ms} ms" \
  "not described — ADD §14.1 has no partition row"

case "$code" in
  502|503|504)
    ok "the edge failed fast and honestly: HTTP ${code} in ${probe_ms} ms — a client can retry"
    ;;
  000)
    finding MED "The edge HANGS through a partition rather than refusing. \
\`GET /v1/.well-known/jwks.json\` produced no response inside 8 s with app-services unreachable. \
\`haproxy.replica.cfg\`'s \`timeout server\` is what decides this, and every second of it is a \
connection held open on both sides — during a partition that is exactly when a client should be \
told to back off. ADD §14.1 has no row for a partition, so no documented behaviour says which of \
the two this should be."
    ;;
  *)
    warn "the edge answered ${code} in ${probe_ms} ms with app-services partitioned"
    ;;
esac

# -------------------------------------------------------------------------------------
# What survives — the planes that do not go through Container 7
# -------------------------------------------------------------------------------------
if timeout 5 bash -c "</dev/tcp/127.0.0.1/${HAPROXY_MQTT_WSS_PORT:-8084}" 2>/dev/null; then
  degraded_row "MQTT ingest (\`:${HAPROXY_MQTT_WSS_PORT:-8084}\`)" "accepts TCP" \
    "implied — MQTT is never routed through the API gateway (infra/CLAUDE.md)"
  ok "the ingest plane is unaffected: MQTT is a separate listener and a separate container"
else
  degraded_row "MQTT ingest (\`:${HAPROXY_MQTT_WSS_PORT:-8084}\`)" "refused" "implied — separate plane"
  bad "the MQTT listener refused while only app-services was partitioned"
fi

hot_health=$(dc ps --format '{{.Service}} {{.Health}}' 2>/dev/null | awk '$1 == "hot-path" {print $2}')
fanout_health=$(dc ps --format '{{.Service}} {{.Health}}' 2>/dev/null | awk '$1 == "fanout" {print $2}')
degraded_row "hot-path / fanout containers" "${hot_health:-?} / ${fanout_health:-?}" "not described"

# The partitioned container's own view. Its healthcheck is `curl 127.0.0.1:5000/health/ready`,
# which does not leave the container — so a service that cannot reach one dependency of its
# twenty-three still reports itself ready, and Docker still calls it healthy.
own_health=$(docker exec "$CONTAINER" sh -c "curl -fsS -o /dev/null -w '%{http_code}' --max-time 8 http://127.0.0.1:5000/health/ready" 2>/dev/null | tr -d '\r')
container_health=$(dc ps --format '{{.Service}} {{.Health}}' 2>/dev/null | awk '$1 == "app-services" {print $2}')
degraded_row "app-services' own \`/health/ready\`" "HTTP ${own_health:-no answer} (Docker says ${container_health:-?})" \
  "D7' §5.1 — readiness is what an orchestrator routes on"

if [ "$own_health" = "200" ]; then
  finding MED "A fully partitioned app-services reports itself READY. Its \`/health/ready\` \
answered 200 from inside the container while every socket to Postgres, Redis, Redpanda, EMQX and \
MinIO was black-holed, and Docker's health state stayed \`${container_health}\`. D7' §5.1 makes \
readiness the signal an orchestrator routes traffic on, so on DOKS this pod would keep taking \
requests it cannot serve until its liveness probe or the Service's own endpoint controller noticed \
— which, for a partition rather than a crash, neither will. A readiness probe that checks no \
dependency answers a different question from the one it is asked."
fi

degraded_table_close

# -------------------------------------------------------------------------------------
# Heal, and see whether the platform comes back on its own
# -------------------------------------------------------------------------------------
partition_ms=$(since_ms "$cut_at")
heal_started=$(now_ms)

docker network connect --alias app-services --alias mageride-replica-app-services-1 \
  "$NETWORK" "$CONTAINER" >/dev/null 2>&1
ip_after=$(docker inspect "$CONTAINER" --format "{{(index .NetworkSettings.Networks \"${NETWORK}\").IPAddress}}" 2>/dev/null)
note "reconnected; the container's address is now ${ip_after} (was ${ip_before})"

if healed_ms=$(wait_for 120 3 steady_state_quiet); then
  ok "the platform served again $(human_ms "$healed_ms") after the partition healed, with no restart"
  if [ "$ip_before" != "$ip_after" ]; then
    ok "and HAProxy followed the container to its new address (${ip_before} → ${ip_after}) — \
\`resolvers docker\` on the backend is doing its job"
  fi
else
  warn "the platform did not recover on its own; restarting HAProxy (the second armed rollback)"
  dc restart haproxy >/dev/null 2>&1

  if restart_ms=$(wait_for 120 3 steady_state_quiet); then
    finding HIGH "A healed network partition needs HAProxy restarted by hand. app-services came \
back at ${ip_after} (it was ${ip_before}), the edge kept the old address, and the platform only \
served again $(human_ms "$restart_ms") after \`docker compose restart haproxy\`. \
\`haproxy.replica.cfg\` declares \`resolvers docker\` on the backends for exactly this, so either \
the resolver is not re-resolving or its hold period outlasts the outage. On DOKS the equivalent is \
a Service endpoint that is not re-resolved after a pod moves — same failure, and no operator would \
think to restart the ingress for it."
  else
    bad "the platform did not recover even after restarting HAProxy"
  fi
fi

report ""
report "| Network partition | Measured |"
report "|---|---|"
report "| Partition length | $(human_ms "$partition_ms") |"
report "| Edge answer under partition | HTTP ${code} in ${probe_ms} ms |"
report "| Container address before → after | ${ip_before} → ${ip_after} |"
report "| Serving again after healing | $(human_ms "${healed_ms:-${restart_ms:-0}}") |"
report ""

drill_end 180
