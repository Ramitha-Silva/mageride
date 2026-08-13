#!/usr/bin/env bash
# =====================================================================================
# 20 — postgres-loss.  The system of record goes away.
#
# ADD §14.1:
#
#   | Postgres primary failure | No new registrations, trip history unavailable
#   |                          | Tracking continues; Patroni promotes replica within 30 s |
#
# ------------------------------------------------------------------------------------
# THERE IS NO PATRONI HERE, AND SAYING SO IS HALF THE DRILL
# ------------------------------------------------------------------------------------
# §14's own MVP column says "Single + daily backup", and the replica's compose file opens with
# "one Postgres with no replica … a single-point-of-failure stack by design". So the 30-second
# promotion cannot be measured on this box and this drill does not pretend to: what it measures is
# the OTHER half of the same row, which is testable anywhere and is the half that says whether the
# platform was built to degrade at all —
#
#   * does the TRACKING plane keep serving from Redis while the database is gone?
#   * are new registrations and history refused, or do they hang?
#   * how long does the platform take to be right again once the database returns?
#
# The last of those is the number this replica's operator actually has, and it is not 30 s.
# =====================================================================================

drill_begin "20" "Postgres primary down" \
  "ADD §14.1 (Postgres primary failure) · ADD §14 (MVP HA = single + daily backup)" \
  "the system of record — every service's writes and reads, through pgbouncer" \
  "docker compose start postgres"

# The live map has to have something in it, or "tracking continues" is unfalsifiable: an empty map
# during an outage looks exactly like an empty map before one. A driver going online writes both
# `dispatch.driver_presence` (Postgres) and the R-08 pool index (Redis), and the pool index is what
# survives — so the assertion below is about the Redis-backed read path, not about the vehicle GEO
# set the passenger map uses.
driver_online 0 >/dev/null 2>&1
arm_rollback "driver_offline 0"
pool_keys_before=$(redis_cli --scan --pattern 'geo:drivers:available:*' 2>/dev/null | grep -c . || echo 0)

arm_rollback "dc start postgres"

stopped_at=$(now_ms)
dc stop postgres >/dev/null 2>&1
ok "postgres stopped after $(human_ms "$(since_ms "$stopped_at")")"
sleep 3

degraded_table_open

# -------------------------------------------------------------------------------------
# What §14.1 says is refused
# -------------------------------------------------------------------------------------
otp_started=$(now_ms)
otp=$(edge_curl -o /dev/null -w '%{http_code}' -X POST -H 'Content-Type: application/json' \
  -H "Idempotency-Key: $(openssl rand -hex 16)" \
  -d '{"phone":"+94770049001","deviceId":"c130-chaos-drill20","role":"passenger"}' \
  "${EDGE}/v1/auth/otp/request" 2>/dev/null || echo 000)
otp_ms=$(since_ms "$otp_started")

degraded_row "Registration (\`POST /v1/auth/otp/request\`)" "HTTP ${otp} in ${otp_ms} ms" \
  "\"No new registrations\""
expect_one_of "a new registration is refused rather than hung" "$otp" "500" "503" "502"

# "Trip history unavailable" — query-svc's `GET /v1/trips/{userId}`, which is where trip history
# actually lives. NOT `GET /v1/rides/history`: that route is deliberately unmapped (RideEndpoints'
# own remarks — "Left unmapped rather than stubbed … `GET /history` (AL-36, C048). A stubbed route
# is worse than an absent one"), so it answers 404 whether or not the database is up and would
# have been a probe that proved nothing in either direction. The first version of this drill used
# it and passed.
history_started=$(now_ms)
history=$(edge_code "/v1/trips/$(env_json '.passengers[0].id')" \
  -H "Authorization: Bearer $(env_json '.passengers[0].bearer')")
history_ms=$(since_ms "$history_started")
degraded_row "Trip history (\`GET /v1/trips/{userId}\`)" "HTTP ${history} in ${history_ms} ms" \
  "\"trip history unavailable\""
expect_one_of "trip history is refused" "$history" "500" "503" "502"

# The refusal has to be FAST. A 30-second hang on every request is how a database outage becomes a
# thread-pool outage and takes the tracking plane down with it — which is precisely the cascade
# §14.1's "tracking continues" is a claim against.
if [ "${otp_ms:-99999}" -le 5000 ] && [ "${history_ms:-99999}" -le 5000 ]; then
  ok "both refusals came back inside 5 s (${otp_ms} ms, ${history_ms} ms) — no connection-pool pile-up"
else
  finding HIGH "Requests that need Postgres hang rather than fail while it is down \
(otp ${otp_ms} ms, history ${history_ms} ms). A slow refusal is what turns a database outage into \
an every-request outage: pgbouncer's \`MAX_CLIENT_CONN=500\` and Kestrel's thread pool are shared \
with the read paths ADD §14.1 says must keep serving."
fi

# -------------------------------------------------------------------------------------
# What §14.1 says continues
# -------------------------------------------------------------------------------------
nearby=$(probe_nearby)
degraded_row "Live map (\`GET /v1/nearby\`)" "\`${nearby}\` (code, limitedLive, vehicles)" \
  "\"Tracking continues\""

case "$nearby" in
  200*)
    ok "ADD §14.1 HELD: the tracking plane still serves with the database down — ${nearby}"
    ;;
  *)
    bad "the live map answered [${nearby}] with Postgres down"
    finding HIGH "ADD §14.1 promises \"Tracking continues\" through a Postgres primary failure and \
\`GET /v1/nearby\` answered \`${nearby}\`. The live map is served from Redis \`geo:live\` and needs \
no database row — so whatever pulled it down is a dependency the degradation model does not know \
about."
    ;;
esac

pool_keys_now=$(redis_cli --scan --pattern 'geo:drivers:available:*' 2>/dev/null | grep -c . || echo 0)
degraded_row "R-08 driver pool (Redis)" "${pool_keys_now} cell keys (was ${pool_keys_before})" \
  "not described — implied by \"tracking continues\""

sos_degraded_row "$(probe_sos 0)"

# The tracker plane's own front door. tcp-adapter has no HTTP surface and its liveness signal is
# the GT06 listener, which is exactly the point: a device that connects while the database is away
# is still served (the container's own healthcheck comment says so).
if timeout 5 bash -c "</dev/tcp/127.0.0.1/${HAPROXY_GT06_PORT:-5023}" 2>/dev/null; then
  degraded_row "Tracker ingest (GT06 :${HAPROXY_GT06_PORT:-5023})" "accepts TCP" "\"Tracking continues\""
  ok "the GT06 listener still accepts a connection with Postgres down"
else
  degraded_row "Tracker ingest (GT06 :${HAPROXY_GT06_PORT:-5023})" "refused" "\"Tracking continues\""
  bad "the tracker plane's TCP listener refused while the database was down"
fi

degraded_table_close

# -------------------------------------------------------------------------------------
# Recovery — the number this box actually has in place of Patroni's 30 s
# -------------------------------------------------------------------------------------
outage_ms=$(since_ms "$stopped_at")
recovery_started=$(now_ms)
dc start postgres >/dev/null 2>&1

if pg_ms=$(wait_for 180 3 service_healthy postgres); then
  ok "postgres healthy again $(human_ms "$pg_ms") after start"
else
  bad "postgres did not report healthy within 180 s"
fi

# Healthy is not the same as serving: pgbouncer has to re-establish server connections and every
# service's Npgsql pool has to discard the broken ones. THAT is the recovery a passenger feels.
if serve_ms=$(wait_for 180 3 steady_state_quiet); then
  ok "the platform served a full steady state again $(human_ms "$serve_ms") after Postgres started"
else
  bad "the platform was still not serving 180 s after Postgres came back"
fi

total_rto=$(since_ms "$recovery_started")
note "outage $(human_ms "$outage_ms") · recovery to first good request $(human_ms "$total_rto")"

report ""
report "| Postgres recovery | Measured |"
report "|---|---|"
report "| Container healthy again | $(human_ms "${pg_ms:-0}") |"
report "| Platform serving again | $(human_ms "${serve_ms:-0}") |"
report "| ADD §14.1's promise | Patroni promotes a replica within 30 s — **there is no replica on this stack** (ADD §14 MVP column: \"Single + daily backup\") |"
report ""

if [ "${total_rto:-999999}" -gt 30000 ]; then
  finding MED "A Postgres failure on this deployment costs $(human_ms "$total_rto") of full \
outage, not ADD §14.1's 30 seconds. The 30 s is Patroni's promotion time and Patroni is in the \
*Production HA* column; §14's MVP column says \"Single + daily backup\" and the replica's compose \
file says \"one Postgres with no replica … a single-point-of-failure stack by design\". The two \
are consistent — but §14.1's degradation table quotes only the 30 s, so a reader who stops at the \
table takes a production number for the MVP one. Recorded as a documentation finding, with the \
measured figure, rather than as a defect in this stack."
fi

drill_end 180
