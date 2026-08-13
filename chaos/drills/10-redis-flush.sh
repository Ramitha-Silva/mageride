#!/usr/bin/env bash
# =====================================================================================
# 10 — redis-flush.  The keyspace disappears while an offer is live.
#
# This is the drill this component exists for. R-04 says the Redis `offer:{rideId}` TTL is a
# "fast hint, NOT authoritative" and that `rides.timers` is the source of truth; ADD §9.4 repeats
# it on the key itself. The only way to know which of the two the platform actually obeys is to
# take the hint away in the fifteen seconds it is supposed to matter.
#
# ------------------------------------------------------------------------------------
# THE CONTROL COMES FIRST
# ------------------------------------------------------------------------------------
# An offer expiry measured only under a flush produces a number nobody can read: is 1.5 s the
# backstop being slow, or the backstop being the ONLY clock now that D-07's keyspace-notification
# accelerator has been flushed away with everything else? So this drill expires one offer
# untouched, then expires a second with the keyspace destroyed, and reports both. The difference
# between them is the accelerator's contribution, which is the number R-04's "≤ 1 s" has to be
# read against.
#
# Sourced by run-drills.sh. Uses the shared counters, the fixture globals and the one trap.
# =====================================================================================

drill_begin "10" "Redis keyspace lost mid-offer" \
  "R-04 (ADD §6, §9.4, §11.12) · ADD §14.1 Redis failure · ADD §15 (Redis RPO 0, rebuildable)" \
  "the whole Redis keyspace — live map, driver pool, offer hints, wallet cache, refresh tokens" \
  "none needed: Redis holds derived state only. The drill proves it repopulates."

# -------------------------------------------------------------------------------------
# 0. Control — one offer expires with Redis untouched
# -------------------------------------------------------------------------------------
CONTROL_LAG_MS=""

if make_live_offer 1; then
  arm_rollback "release_fixture 1"
  control_ride="$FIXTURE_RIDE"

  if wait_for 25 0.5 timer_fired "$control_ride" offer_expiry >/dev/null; then
    CONTROL_LAG_MS=$(offer_expiry_lag "$control_ride")
    note "control: offer expiry fired ${CONTROL_LAG_MS} ms after its deadline, Redis intact"
  else
    warn "the control offer did not expire inside 25 s — the comparison below is against nothing"
  fi

  release_fixture 1
else
  warn "no control offer could be made; the flushed measurement will stand on its own"
fi

# -------------------------------------------------------------------------------------
# 1. A live offer, and everything that should be watching it
# -------------------------------------------------------------------------------------
if ! make_live_offer 0; then
  bad "could not put a ride into Offered — nothing to drill R-04 against"
  drill_end 60
else

arm_rollback "release_fixture 0"

ok "ride ${FIXTURE_RIDE} reached Offered in $(human_ms "$FIXTURE_OFFER_WAIT_MS")"

offer_key_ttl=$(redis_cli PTTL "offer:${FIXTURE_RIDE}" | tr -d '\r')
timer_before=$(psql_one "SELECT count(*) FROM rides.timers
                          WHERE ride_id = '${FIXTURE_RIDE}' AND kind = 'offer_expiry' AND fired_at IS NULL;")
deadline=$(psql_one "SELECT to_char(expires_at, 'YYYY-MM-DD\"T\"HH24:MI:SS.MSOF') FROM dispatch.offers
                      WHERE ride_id = '${FIXTURE_RIDE}' AND status = 'OFFERED';")

expect "the Redis offer hint exists with a TTL" "$([ "${offer_key_ttl:-0}" -gt 0 ] && echo yes || echo "no (PTTL=${offer_key_ttl})")" "yes"
expect "the durable backstop is armed in rides.timers" "$timer_before" "1"
note "offer deadline ${deadline}"

# The control probe for the session credential, spent BEFORE the fault so that a 401 afterwards
# can only be the fault's doing. See rotate_refresh's remarks.
refresh_before=$(rotate_refresh 1)
case "$refresh_before" in
  200) ok "control: POST /v1/auth/refresh answers 200 with Redis intact" ;;
  *)   warn "the refresh control answered ${refresh_before}; the post-flush probe below proves nothing" ;;
esac

keys_before=$(redis_cli DBSIZE | tr -d '\r')
geo_before=$(redis_cli ZCARD geo:live | tr -d '\r')

timers_fired_before=$(metric "$(metrics_of app-services 5000)" mageride_rides_timers_fired_total)

# -------------------------------------------------------------------------------------
# 2. The fault
# -------------------------------------------------------------------------------------
flush_at=$(now_ms)
redis_cli FLUSHALL >/dev/null
note "FLUSHALL: ${keys_before} keys and a ${geo_before}-member geo:live are gone"

keys_after=$(redis_cli DBSIZE | tr -d '\r')
expect "the keyspace is empty" "$keys_after" "0"
expect "the offer hint is gone" "$(redis_cli EXISTS "offer:${FIXTURE_RIDE}" | tr -d '\r')" "0"

# -------------------------------------------------------------------------------------
# 3. What the platform does with no Redis state and a deadline still to keep
# -------------------------------------------------------------------------------------
degraded_table_open

nearby=$(probe_nearby)
degraded_row "Live map (\`GET /v1/nearby\`)" "\`${nearby}\` (code, limitedLive, vehicles)" \
  "\"Live map stale by ≤ 30 s … query-svc returns \`limited_live\` flag\""

# Redis is UP — only its contents are gone — so `limitedLive` stays false and the map answers with
# an empty city. That is a different failure from the one §14.1 describes, and the report says so
# rather than scoring it either way.
case "$nearby" in
  "200 false 0")
    finding MED "A flushed Redis is not an unreachable Redis, and the platform cannot tell a caller \
which it is. \`GET /v1/nearby\` answered \`200 {limitedLive:false, vehicles:[]}\` with the entire \
live index destroyed — indistinguishable from a city with no vehicles in it. ADD §14.1's Redis row \
covers only the unreachable case; \`LiveVehicleIndex\` raises the flag on \`RedisException\` and on \
\`TimeoutException\`, and an empty \`GEOSEARCH\` is neither. Drill 11 is the case it does cover."
    ;;
  *) note "nearby answered [${nearby}] on an empty keyspace" ;;
esac

refresh_after=$(rotate_refresh 1)
degraded_row "Session refresh (\`POST /v1/auth/refresh\`)" "HTTP ${refresh_after}" \
  "not described — §14.1's Redis row names the live map only"

if [ "$refresh_before" = "200" ]; then
  case "$refresh_after" in
    200) ok "a signed-in user can still refresh: iam-svc falls back to iam.sessions" ;;
    401|403)
      finding HIGH "A Redis flush signs every user out. The same session refreshed 200 with Redis \
intact and ${refresh_after} seconds later with the keyspace empty, while its \`iam.sessions\` row \
was untouched — so \`refresh:{jti}\` is load-bearing rather than a cache and there is no fallback \
to the durable half. ADD §14.1's Redis row promises a stale live map and nothing else; the real \
blast radius is every 30-minute access token on the platform expiring with no way to renew it, \
i.e. a full re-authentication of every driver and rider mid-shift. ADD §15 rates Redis \
\"RPO 0 (ephemeral data)\" — a live session is not ephemeral data." ;;
    *) warn "POST /v1/auth/refresh answered ${refresh_after} — neither a renewal nor a refusal" ;;
  esac
fi

booking=$(edge_get_as "$(env_json '.passengers[2].bearer')" \
  "/v1/fare/estimate?fromLat=6.9271&fromLng=79.8612&toLat=6.8449&toLng=79.8837&vehicleType=three_wheeler&kind=passenger" \
  | jq -r 'if .fareEstimateToken then "quoted" else "refused" end' 2>/dev/null)
degraded_row "Fare quote (\`GET /v1/fare/estimate\`)" "$booking" "not described"
expect "fare-svc still quotes with an empty keyspace" "$booking" "quoted"

degraded_table_close

# -------------------------------------------------------------------------------------
# 4. THE ASSERTION: the deadline is kept by Postgres, not by the key that was flushed
# -------------------------------------------------------------------------------------
if fired_ms=$(wait_for 25 0.5 timer_fired "$FIXTURE_RIDE" offer_expiry); then
  lag_ms=$(offer_expiry_lag "$FIXTURE_RIDE")

  ok "THE OFFER EXPIRY SURVIVED THE FLUSH: rides.timers fired ${lag_ms} ms after the deadline, \
$(human_ms "$(( $(now_ms) - flush_at ))") after the keyspace was destroyed"

  report ""
  report "| Offer expiry | Redis intact (control) | Keyspace flushed |"
  report "|---|---|---|"
  report "| Fired after its deadline | ${CONTROL_LAG_MS:-not measured} ms | ${lag_ms} ms |"
  report ""

  # R-04: "fires within 1 s of expiry independently of any Redis TTL".
  if [ "${lag_ms:-99999}" -le 1000 ]; then
    ok "within R-04's 1 s budget, with no Redis to help"
  elif [ -n "$CONTROL_LAG_MS" ] && [ "${CONTROL_LAG_MS}" -le 1000 ]; then
    finding MED "R-04's \"within 1 s of expiry\" holds only while Redis is healthy. The control \
offer fired ${CONTROL_LAG_MS} ms after its deadline; the flushed one took ${lag_ms} ms. D-07's \
keyspace-notification accelerator (\`OfferKeyspaceListener\`) is what makes the fast case fast, \
and it is exactly what a Redis failure removes — so the ONE second the ADD promises is a figure \
for the healthy path, and the path the guarantee exists for is slower than it. \
\`Dispatch:TimerPollInterval\` is 500 ms and \`TimerBatchSize\` 100: a sweep is a poll plus a \
claim plus one ride-svc call per due row."
  else
    finding MED "The R-04 backstop fired ${lag_ms} ms after the deadline; ADD §11.11 says \
\"within 1 s of expiry\". No usable control was measured this run, so the number stands alone."
  fi

  state=$(ride_state "$FIXTURE_RIDE" 0)
  expect_one_of "the ride returned to the pool" "$state" "Matching" "ExpiredNoDriver"

  offer_status=$(psql_one "SELECT status FROM dispatch.offers WHERE ride_id = '${FIXTURE_RIDE}' ORDER BY sent_at LIMIT 1;")
  expect "the offer row is settled" "$offer_status" "EXPIRED"

  # The counter an operator would see this by. It does not move, and the reason is not that the
  # backstop did not fire — the four lines above just proved it did.
  timers_fired_after=$(metric "$(metrics_of app-services 5000)" mageride_rides_timers_fired_total)
  if [ "$((timers_fired_after - timers_fired_before))" -ge 1 ]; then
    ok "mageride.rides.timers_fired advanced by $((timers_fired_after - timers_fired_before))"
  else
    finding MED "R-04's backstop fires and nothing counts it. \
\`MageRideDiagnostics.RideTimersFired\` (\`mageride.rides.timers_fired\`, \"Durable ride timers \
that fired and changed the aggregate (R-04)\") is declared in the shared kernel and incremented \
by no service — \`grep -rn RideTimersFired backend/src\` returns its declaration and nothing \
else. Two Grafana panels chart it and are therefore permanently empty: \
\`business-stuck-states.json\` (\"fired/min · {{kind}}\") and \`money-and-safety.json\` \
(\`kind=\"payment_pending\"\` timers fired/h). The neighbouring gauge is fine — \
\`mageride_rides_timer_backlog\` is published by ride-svc's \`StuckStateObserver\` and is what \
\`alerts.infrastructure.yml\`'s RideTimerBacklog rule fires on — so the backlog is visible and \
the drain is not."
  fi
else
  bad "the offer expiry did NOT fire within 25 s of a flush ${fired_ms} ms ago. R-04's durable \
backstop is the guarantee and the Redis TTL was the only thing keeping the deadline."
  finding HIGH "R-04's durable backstop did not fire after the Redis keyspace was flushed: ride \
${FIXTURE_RIDE} held its offer past the deadline with no \`rides.timers.fired_at\`. The offer TTL \
is documented as a fast hint and is behaving as the only clock."
fi

# -------------------------------------------------------------------------------------
# 5. Redis rebuilds itself — the claim ADD §15 makes for RPO 0
# -------------------------------------------------------------------------------------
if driver_online 2 >/dev/null 2>&1; then
  if rebuilt_ms=$(wait_for 20 1 pool_index_present); then
    ok "the R-08 driver pool index rebuilt itself $(human_ms "$rebuilt_ms") after a driver went online"
  else
    finding MED "\`geo:drivers:available:*\` did not reappear within 20 s of a driver going online \
after a flush. ADD §15 rates Redis \"RPO 0 (ephemeral data), rebuildable from stream replay\", \
which is a claim about a mechanism nothing here performs: the pool is rebuilt only by the NEXT \
go-online or the next position sample, so every driver already online is invisible to dispatch \
until they publish again."
  fi
  driver_offline 2
fi

drill_end 60
fi
