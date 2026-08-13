#!/usr/bin/env bash
# =====================================================================================
# 11 — redis-loss.  Redis is unreachable, not merely empty.
#
# Drill 10 destroyed the contents; this one takes the server away. They are different failures and
# the platform answers them differently, which is the point: ADD §14.1's Redis row —
#
#   | Redis failure | Live map stale by ≤ 30 s | fanout-svc serves last in-memory buffer;
#   |               |                          | query-svc returns `limited_live` flag |
#
# — is about THIS one. `LiveVehicleIndex` raises the flag on `RedisException` and on
# `TimeoutException`, both of which need a socket that is not there.
#
# `docker compose stop`, not `kill`: a stopped container is a clean refusal on connect, which is
# what a dead pod behind a Service looks like. `restart: unless-stopped` does not fight a
# deliberate stop — `docker compose start` is the whole rollback.
# =====================================================================================

drill_begin "11" "Redis unreachable" \
  "ADD §14.1 (Redis failure) · ADD §12 limited_live · D-33 under fault" \
  "every service's Redis client — live map, driver pool, offer locks, rate limits, sessions" \
  "docker compose start redis"

before_limited=$(metric "$(metrics_of app-services 5000)" mageride_query_nearby_limited_live_total)
nearby_before=$(probe_nearby)

arm_rollback "dc start redis"

stopped_at=$(now_ms)
dc stop redis >/dev/null 2>&1
ok "redis stopped after $(human_ms "$(since_ms "$stopped_at")")"

# StackExchange.Redis reconnects in the background and its first failures are timeouts rather than
# refusals; give the connection multiplexer a moment to notice, so the probes below measure the
# platform's degraded answer rather than its last cached success.
sleep 3

degraded_table_open

# -------------------------------------------------------------------------------------
# The documented behaviour: the map degrades and says so
# -------------------------------------------------------------------------------------
nearby=$(probe_nearby)
code=$(printf '%s' "$nearby" | awk '{print $1}')
limited=$(printf '%s' "$nearby" | awk '{print $2}')

degraded_row "Live map (\`GET /v1/nearby\`)" "\`${nearby}\` (code, limitedLive, vehicles)" \
  "\"Live map stale by ≤ 30 s … query-svc returns \`limited_live\` flag\""

expect "the live map still answers 200 rather than 500" "$code" "200"
if [ "$limited" = "true" ]; then
  ok "ADD §14.1 HELD: query-svc returned limitedLive=true (it was ${nearby_before% *} before)"
else
  bad "query-svc answered limitedLive=${limited} with Redis stopped"
  finding HIGH "ADD §14.1's Redis row promises \`limited_live\` and \`GET /v1/nearby\` did not \
set it with the server stopped. A passenger cannot tell a degraded map from an empty city."
fi

after_limited=$(metric "$(metrics_of app-services 5000)" mageride_query_nearby_limited_live_total)
expect_at_least "mageride.query.nearby.limited_live advanced" \
  "$((after_limited - before_limited))" 1

# -------------------------------------------------------------------------------------
# The rest of the plane, measured rather than assumed
# -------------------------------------------------------------------------------------
quote=$(edge_get_as "$(env_json '.passengers[0].bearer')" \
  "/v1/fare/estimate?fromLat=6.9271&fromLng=79.8612&toLat=6.8449&toLng=79.8837&vehicleType=three_wheeler&kind=passenger" \
  | jq -r 'if .fareEstimateToken then "quoted" else "refused" end' 2>/dev/null)
degraded_row "Fare quote (\`GET /v1/fare/estimate\`)" "$quote" "not described"

# Booking is the interesting one: the ride aggregate is Postgres's, but R-18's idempotency replay,
# the dispatch reservation and the candidate pre-filter are all Redis's. §14.1 says nothing about
# whether a passenger may still book during a Redis outage, so whatever this is, it is news.
book_ms=$(now_ms)
ride=$(request_ride 0 2>/dev/null)
book_ms=$(since_ms "$book_ms")

if [ -n "$ride" ]; then
  degraded_row "Booking (\`POST /v1/rides/request\`)" "accepted in ${book_ms} ms, ride ${ride:0:8}…" \
    "not described"
  arm_rollback "cancel_ride '${ride}' 0"

  # It was accepted. Can it be dispatched? The R-08 candidate index is a Redis key.
  sleep 4
  state=$(ride_state "$ride" 0)
  degraded_row "…and its dispatch" "state=${state} four seconds later" \
    "not described — §14.1 has no dispatch row for a Redis failure"

  case "$state" in
    Offered|Accepted) ok "a ride was still dispatched with Redis down: ${state}" ;;
    *)
      finding HIGH "A Redis outage stops Mode C dispatch silently. \`POST /v1/rides/request\` was \
accepted in ${book_ms} ms and answered 202, the passenger's app shows a ride being matched, and \
the ride sat in \`${state}\` because the candidate set is built from the Redis key \
\`geo:drivers:available:{type}:{cell}\` before the exact \`ST_DWithin\` post-filter ever runs \
(ADD §6, dispatch-svc). ADD §14.1's Redis row describes a stale live map and nothing else — the \
real impact is that no passenger can be matched to any driver, and nothing in the response says \
so. The ride ends at the 120 s global timeout as \`ExpiredNoDriver\`, which is the same answer \
the platform gives when the city really is empty."
      ;;
  esac
else
  degraded_row "Booking (\`POST /v1/rides/request\`)" "refused after ${book_ms} ms" "not described"
  finding MED "\`POST /v1/rides/request\` is refused outright while Redis is down. That is \
defensible — R-18's idempotency replay is a Redis key — but ADD §14.1's Redis row says the impact \
is a stale live map, and a passenger who cannot book at all is a different outage."
fi

sos_degraded_row "$(probe_sos 0)"

# ADD §14.1 also promises "fanout-svc serves last in-memory buffer". fanout-svc has no HTTP surface
# a drill can ask, so what is checked is the weaker, honest thing: that the container did not fall
# over, i.e. that its Redis dependency is not a liveness dependency.
fanout_health=$(dc ps --format '{{.Service}} {{.Health}}' 2>/dev/null | awk '$1 == "fanout" {print $2}')
degraded_row "fanout-svc" "container ${fanout_health:-unknown}" \
  "\"fanout-svc serves last in-memory buffer\""
expect "fanout-svc stayed up without Redis" "$fanout_health" "healthy"

degraded_table_close

# -------------------------------------------------------------------------------------
# Recovery
# -------------------------------------------------------------------------------------
recovery_started=$(now_ms)
dc start redis >/dev/null 2>&1

if back_ms=$(wait_for 90 2 service_healthy redis); then
  ok "redis reported healthy $(human_ms "$back_ms") after start"
else
  bad "redis did not come back healthy within 90 s"
fi

# The flag has to come DOWN again by itself: a degradation nobody clears is an outage. This is also
# the number that says how long StackExchange.Redis's reconnect actually takes, which is the part
# of the recovery no container health check can see.
if cleared_ms=$(wait_for 60 2 nearby_not_limited); then
  ok "limitedLive cleared $(human_ms "$cleared_ms") after Redis returned"
else
  bad "limitedLive is still set $(human_ms "$cleared_ms") after Redis came back: $(probe_nearby)"
  finding HIGH "The \`limited_live\` degradation does not clear itself. Redis returned healthy \
and \`GET /v1/nearby\` was still flagged a minute later, so a recovered platform keeps telling \
every passenger its map is incomplete until something restarts query-svc."
fi

note "total Redis outage: $(human_ms "$(( recovery_started - stopped_at ))"); \
recovery to healthy: $(human_ms "$(since_ms "$recovery_started")")"

drill_end 120
