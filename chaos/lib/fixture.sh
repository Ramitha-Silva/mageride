#!/usr/bin/env bash
# =====================================================================================
# chaos/lib/fixture.sh — the live ride, the live offer and the steady-state probes the drills
# break things around.
#
# ------------------------------------------------------------------------------------
# EVERY DRILL NEEDS A STEADY STATE IT CAN NAME
# ------------------------------------------------------------------------------------
# "Redis went away and the platform degraded" is only a sentence if somebody first established
# that the platform was not degraded. `steady_state` is the before-picture and is taken again
# after the rollback; a drill that cannot establish it does not inject its fault, because a fault
# on top of an already-broken plane measures nothing.
#
# ------------------------------------------------------------------------------------
# THE OFFER FIXTURE DOES NOT PUBLISH TELEMETRY, AND THAT IS DELIBERATE
# ------------------------------------------------------------------------------------
# `POST /v1/standby/online` writes `dispatch.driver_presence` with the position AND
# `last_seen_at = now()` (PresenceRepository's upsert), so a ride booked within D5' §3.2's
# freshness window — `Dispatch:ExpectedPositionInterval` x `PositionFreshnessFactor` = 120 s —
# finds the driver without one MQTT sample. load/dispatch.js publishes for its whole run because
# it books for three minutes; a drill books once and measures in seconds.
#
# That matters for what the R-04 drill can claim: the offer it creates is reached through the
# ordinary candidate build (H3 pre-filter from Redis, exact ST_DWithin on Postgres), so a
# FLUSHALL between the offer and its deadline breaks the same Redis state a real offer depends
# on. Measured: 750 ms from `POST /v1/rides/request` to state `Offered`.
# =====================================================================================

# The grid, borrowed from load/dispatch.js, which borrowed it from tests/E2E's ModeCFleet: every
# (passenger, driver) pair gets its own square of Sri Lanka so two fixtures cannot end up in one
# candidate pool and steal each other's driver. 0.12° is ~13 km — comfortably over twice
# `Dispatch:SearchRadiusM`.
fixture_pickup_lat() { python3 -c "print(round(6.0 + 0.12 * ($1 // 19), 6))"; }
fixture_pickup_lng() { python3 -c "print(round(79.6 + 0.12 * ($1 % 19), 6))"; }

# The ~9.5 km Colombo Fort -> Dehiwala hop, so every ride in a run is priced off one distance
# band and a fare difference is never why a booking was refused.
fixture_dropoff_lat() { python3 -c "print(round($1 - 0.083, 6))"; }
fixture_dropoff_lng() { python3 -c "print(round($1 + 0.0225, 6))"; }

# Set by make_live_offer.
FIXTURE_RIDE=""
FIXTURE_OFFER=""
FIXTURE_DRIVER=""
FIXTURE_PASSENGER=""
FIXTURE_VEHICLE=""
FIXTURE_LAT=""
FIXTURE_LNG=""
FIXTURE_OFFER_WAIT_MS=0

# Why `make_live_offer` returned 1, when it does. `booking` means the ride was never created —
# a fixture problem, and evidence about nothing. `no-offer` means the platform accepted the booking
# and produced no offer, which IS evidence about the dispatch plane. A caller that cannot tell them
# apart will report the first as the second: drill 70 raised a HIGH finding about D-08's
# first-trip-free rule for a booking that `ux_rides_open_passenger` had refused.
FIXTURE_FAILURE=""

# -------------------------------------------------------------------------------------
# driver_online <pair-index> — go on standby at that pair's square.
# -------------------------------------------------------------------------------------
driver_online() {
  local i="$1"
  local bearer vehicle lat lng answer

  bearer=$(env_json ".drivers[${i}].bearer")
  vehicle=$(env_json ".drivers[${i}].vehicleId")
  lat=$(fixture_pickup_lat "$i")
  lng=$(fixture_pickup_lng "$i")

  [ -n "$vehicle" ] || { echo "driver ${i} has no vehicleId in chaos/env.json" >&2; return 1; }

  answer=$(edge_post_as "$bearer" /v1/standby/online \
    "{\"vehicleId\":\"${vehicle}\",\"position\":{\"lat\":${lat},\"lng\":${lng}}}")

  case "$answer" in
    *AVAILABLE*) return 0 ;;
    *) echo "standby/online for driver ${i}: ${answer:0:200}" >&2; return 1 ;;
  esac
}

driver_offline() {
  local bearer; bearer=$(env_json ".drivers[${1}].bearer")
  edge_post_as "$bearer" /v1/standby/offline '{}' >/dev/null 2>&1 || true
}

# -------------------------------------------------------------------------------------
# `request_ride <passenger-index> [square-index]` — quote, book. Echoes the rideId, or nothing.
#
# The square defaults to the passenger's own, which is the ordinary pairing. It is separable
# because drill 70 has to book a SECOND ride into driver 2's square while passenger 2 is still
# holding the first one: `ux_rides_open_passenger` does not exempt `PaymentPending`, and
# `ride.yaml`'s own note on `/complete` flags that hazard ("a passenger who books a new ride inside
# that window can wedge the old one"). A different passenger at the same square asks the same
# question of the same driver without going near it.
request_ride() {
  local i="$1" square="${2:-$1}"
  local bearer lat lng dlat dlng quote token booking

  bearer=$(env_json ".passengers[${i}].bearer")
  lat=$(fixture_pickup_lat "$square");  lng=$(fixture_pickup_lng "$square")
  dlat=$(fixture_dropoff_lat "$lat"); dlng=$(fixture_dropoff_lng "$lng")

  # The quote first: ride-svc verifies the token's signature (D5' §1.1), so a booking cannot be
  # made without this hop. It is also the drill's cheapest probe of whether fare-svc is answering
  # at all, which is why several drills call it on its own.
  quote=$(edge_get_as "$bearer" \
    "/v1/fare/estimate?fromLat=${lat}&fromLng=${lng}&toLat=${dlat}&toLng=${dlng}&vehicleType=three_wheeler&kind=passenger")
  token=$(printf '%s' "$quote" | jq -r '.fareEstimateToken // empty' 2>/dev/null)

  [ -n "$token" ] || { echo "fare/estimate refused: ${quote:0:200}" >&2; return 1; }

  booking=$(edge_post_as "$bearer" /v1/rides/request "$(jq -cn \
    --arg cr "$(uuidgen)" --arg tok "$token" \
    --argjson plat "$lat" --argjson plng "$lng" --argjson dlat "$dlat" --argjson dlng "$dlng" \
    '{clientRequestId:$cr, kind:"passenger",
      pickup:{lat:$plat, lng:$plng, address:"C130 chaos pickup"},
      dropoff:{lat:$dlat, lng:$dlng, address:"C130 chaos dropoff"},
      vehicleType:"three_wheeler", fareEstimateToken:$tok, paymentMethod:"cash"}')")

  local ride; ride=$(printf '%s' "$booking" | jq -r '.rideId // empty' 2>/dev/null)
  [ -n "$ride" ] || { echo "rides/request refused: ${booking:0:240}" >&2; return 1; }

  printf '%s' "$ride"
}

ride_state() {
  local bearer; bearer=$(env_json ".passengers[${2:-0}].bearer")
  edge_get_as "$bearer" "/v1/rides/${1}/state" | jq -r '.state // "unreadable"' 2>/dev/null
}

# `cancel_ride <rideId> [passenger-index]` — free the passenger and release the driver.
#
# The body is `{reason, version}`, both required and both spelled as `ride.yaml` spells them:
# `reason` is the SCREAMING_SNAKE enum `[RIDER_CHANGED_MIND, DRIVER_TOO_FAR, EMERGENCY, OTHER]`,
# and the command is a `VersionedCommand` whose `version` must be the one the client last saw.
# Sending `{"reason":"other"}` — which is what the first version of this file did — is answered
# `400 validation-failed` on both counts, silently, so every fixture ride stayed open, and the
# NEXT booking by that passenger was refused by `ux_rides_open_passenger`. The drill that failed
# was drill 50, three drills after the one that leaked the ride.
cancel_ride() {
  local ride="$1" i="${2:-0}" bearer state version
  bearer=$(env_json ".passengers[${i}].bearer")

  state=$(edge_get_as "$bearer" "/v1/rides/${ride}/state")
  version=$(printf '%s' "$state" | jq -r '.version // empty' 2>/dev/null)
  [ -n "$version" ] || return 0   # Already gone, or unreadable — nothing to cancel.

  edge_post_as "$bearer" "/v1/rides/${ride}/cancel" \
    "{\"reason\":\"OTHER\",\"version\":${version}}" >/dev/null 2>&1 || true
}

# Every non-terminal ride this fixture's passengers hold. `ux_rides_open_passenger` allows one
# each, so a leaked ride is a booking refusal in whichever drill runs next — which is a fault the
# drill did not inject and would report as if it had.
open_fixture_rides() {
  psql_one "SELECT count(*) FROM rides.rides r JOIN iam.users u ON u.id = r.passenger_id
             WHERE u.phone LIKE '+9477004%'
               AND r.state NOT IN ('Completed','ExpiredNoDriver','CancelledByRiderBeforeAccept',
                                   'CancelledByRiderAfterAccept','CancelledByDriver',
                                   'CancelledBySystem','Disputed');"
}

# Cancel every one of them. Called by run-drills.sh between drills, so one drill's leak cannot be
# the next drill's finding.
release_all_fixture_rides() {
  local i ride
  for i in 0 1 2; do
    for ride in $(psql_q "SELECT r.id FROM rides.rides r JOIN iam.users u ON u.id = r.passenger_id
                           WHERE u.phone = '+9477004$(printf '%04d' $((i + 1)))'
                             AND r.state NOT IN ('Completed','ExpiredNoDriver','CancelledByRiderBeforeAccept',
                                                 'CancelledByRiderAfterAccept','CancelledByDriver',
                                                 'CancelledBySystem','Disputed');" | tr -d ' \r'); do
      [ -n "$ride" ] && cancel_ride "$ride" "$i"
    done
    driver_offline "$i"
  done
}

# -------------------------------------------------------------------------------------
# make_live_offer <pair-index> — a ride sitting in `Offered` with its R-04 backstop armed.
#
# Returns 0 with FIXTURE_* set, or 1 having said why. Polls at 200 ms because the whole offer
# window is 15 s and a drill that means to break something INSIDE it has no time to spare.
# -------------------------------------------------------------------------------------
make_live_offer() {
  local i="${1:-0}"
  local started state

  FIXTURE_RIDE=""; FIXTURE_OFFER=""; FIXTURE_OFFER_WAIT_MS=0; FIXTURE_FAILURE=""
  FIXTURE_PASSENGER=$(env_json ".passengers[${i}].id")
  FIXTURE_DRIVER=$(env_json ".drivers[${i}].id")
  FIXTURE_VEHICLE=$(env_json ".drivers[${i}].vehicleId")
  FIXTURE_LAT=$(fixture_pickup_lat "$i")
  FIXTURE_LNG=$(fixture_pickup_lng "$i")

  driver_online "$i" || { FIXTURE_FAILURE="standby"; return 1; }

  started=$(now_ms)
  FIXTURE_RIDE=$(request_ride "$i") || { FIXTURE_FAILURE="booking"; return 1; }

  while [ "$(since_ms "$started")" -lt 30000 ]; do
    state=$(ride_state "$FIXTURE_RIDE" "$i")

    case "$state" in
      Offered|Accepted)
        FIXTURE_OFFER_WAIT_MS=$(since_ms "$started")
        FIXTURE_OFFER=$(psql_one "SELECT id FROM dispatch.offers
                                   WHERE ride_id = '${FIXTURE_RIDE}' AND status = 'OFFERED' LIMIT 1;")
        return 0
        ;;
      ExpiredNoDriver|Cancelled*)
        FIXTURE_FAILURE="no-offer"
        echo "ride ${FIXTURE_RIDE} reached ${state} without an offer — the candidate pool was empty" >&2
        return 1
        ;;
    esac

    sleep 0.2
  done

  FIXTURE_FAILURE="no-offer"
  echo "ride ${FIXTURE_RIDE} was still ${state} after 30 s — no offer to drill against" >&2
  return 1
}

# Leaves the plane as it was found: the ride terminal, the driver off standby. Both are
# idempotent, because every drill arms this as a rollback and `drill_end` runs it on the happy
# path too.
release_fixture() {
  [ -n "$FIXTURE_RIDE" ] && cancel_ride "$FIXTURE_RIDE" "${1:-0}"
  driver_offline "${1:-0}"
  FIXTURE_RIDE=""; FIXTURE_OFFER=""
}

# -------------------------------------------------------------------------------------
# Predicates. `wait_for` takes a command, so these exist to keep the drills from embedding a
# `bash -c` with three levels of quoting around one psql call — which is what the first version
# of drill 10 did, and it was unreadable and wrong.
# -------------------------------------------------------------------------------------

# `timer_fired <rideId> <kind>` — has the durable backstop for this ride fired yet?
timer_fired() {
  [ "$(psql_one "SELECT count(*) FROM rides.timers
                  WHERE ride_id = '${1}' AND kind = '${2}' AND fired_at IS NOT NULL;")" = "1" ]
}

# `dispatch_timer_armed <driverId> <kind>` — R-15's grace and C036's expiries live in the other
# timer table, keyed by the driver rather than the ride.
dispatch_timer_armed() {
  [ "$(psql_one "SELECT count(*) FROM dispatch.timers
                  WHERE driver_id = '${1}' AND kind = '${2}' AND fired_at IS NULL;")" -ge 1 ]
}

# The R-08 candidate index exists at all. After a FLUSHALL this is the thing that has to come
# back before any ride can be dispatched again.
pool_index_present() {
  [ "$(redis_cli --scan --pattern 'geo:drivers:available:*' 2>/dev/null | grep -c .)" -gt 0 ]
}

ride_reached() {
  local state; state=$(ride_state "$1" "${3:-0}")
  [ "$state" = "$2" ]
}

# The live map is answering un-degraded again. A predicate rather than an inline `bash -c` in the
# drills: `wait_for` runs its argument as a command in THIS shell, and a `bash -c` subshell does
# not inherit these functions — it would silently never succeed.
nearby_not_limited() {
  [ "$(probe_nearby | awk '{print $2}')" = "false" ]
}

# `steady_state` without the reporting, for use as a `wait_for` predicate: the recovery loops need
# to ask "is it serving yet?" a dozen times, and a version that wrote a line per attempt would put
# a dozen failures in the report on the way to one success.
# `outbox_drained <schema>` — every row in that service's outbox has been published.
outbox_drained() {
  [ "$(psql_one "SELECT count(*) FROM ${1}.outbox WHERE dispatched_at IS NULL;")" = "0" ]
}

# `undispatched <schema>` — how many are waiting.
undispatched() { psql_one "SELECT count(*) FROM ${1}.outbox WHERE dispatched_at IS NULL;"; }

# mqtt-bridge-svc is back on the broker with its shared subscription.
#
# `emqx ctl clients list`, not `emqx ctl subscriptions list` — the latter prints nothing on this
# broker even while every client reports `subscriptions=1`, because a `$share/…` subscription is
# held on the shared-subscription table rather than the per-topic one. The client line carries the
# count, which is the fact the drill needs.
bridge_subscribed() {
  emqx_ctl clients list 2>/dev/null | grep -q 'username=svc-mqtt-bridge.*subscriptions=[1-9]'
}

# `outbox_failures_advanced <baseline>` — has the publish-failure counter moved past what it was?
outbox_failures_advanced() {
  [ "$(metric "$(metrics_of app-services 5000)" mageride_outbox_publish_failures_total)" -gt "$1" ]
}

# `trips_today <driverId>` — D-08's `tripsToday`, counted the way the gate counts it.
#
# `DailyFeeRepository`: `dispatch.offers` where `status = 'ACCEPTED'` and
# `(responded_at AT TIME ZONE 'Asia/Colombo')::date = today`. NOT every offer sent today, which is
# what the first version of drill 70 counted — a driver who has been *offered* three rides and
# accepted none is still on their first trip, so that drill was reading a number the gate does not
# use and concluding the second-trip rule had been exercised when it had not.
trips_today() {
  psql_one "SELECT count(*) FROM dispatch.offers o
             WHERE o.driver_id = '${1}' AND o.status = 'ACCEPTED'
               AND (o.responded_at AT TIME ZONE 'Asia/Colombo')::date
                   = (now() AT TIME ZONE 'Asia/Colombo')::date;"
}

# `ride_version <rideId> [passenger-index]` — the optimistic-concurrency counter every
# VersionedCommand has to carry.
ride_version() {
  edge_get_as "$(env_json ".passengers[${2:-0}].bearer")" "/v1/rides/${1}/state" \
    | jq -r '.version // empty' 2>/dev/null
}

# `run_ride_to_completion <pair-index>` — accept the live offer and drive the ride to `Completed`.
#
# The offer id comes out of `dispatch.offers`, standing in for the FCM push payload, exactly as
# `load/accept-race.sh` takes it: `POST /v1/rides/{id}/offer/{driverId}/accept` requires the
# offerId and dispatch.yaml has no driver-side offer read at all, so a REST client cannot accept an
# offer it was not pushed.
#
# Completion rather than cancellation, deliberately. A post-acceptance cancel by the passenger
# counts toward AL-16's three-consecutive rule and would disable this fixture's booking on the
# third chaos run; a driver cancel costs the driver a reputation hit and a brief delist. A
# completed ride has neither effect and resets both counters. `body.Otp is accepted and ignored`
# (RideEndpoints' own comment), so `/start` needs no rider code.
#
# Echoes the final state.
# `run_ride_to_completion <driver-index> <rideId> [passenger-index]` — the passenger defaults to the
# driver's own pair, and is separable for the same reason `request_ride`'s square is.
run_ride_to_completion() {
  local i="$1" ride="$2" p="${3:-$1}" driver offer version answer
  driver=$(env_json ".drivers[${i}].id")
  local dbearer
  dbearer=$(env_json ".drivers[${i}].bearer")

  offer=$(psql_one "SELECT id FROM dispatch.offers
                     WHERE ride_id = '${ride}' AND status = 'OFFERED' LIMIT 1;")
  [ -n "$offer" ] || { echo "no OFFERED row for ride ${ride}" >&2; return 1; }

  version=$(ride_version "$ride" "$p")
  answer=$(edge_post_as "$dbearer" "/v1/rides/${ride}/offer/${driver}/accept" \
    "{\"offerId\":\"${offer}\",\"version\":${version}}")
  case "$answer" in
    *'"state"'*) ;;
    *) echo "accept refused: ${answer:0:200}" >&2; return 1 ;;
  esac

  local step
  for step in arrive start complete; do
    version=$(ride_version "$ride" "$p")
    [ -n "$version" ] || { echo "could not read the version before /${step}" >&2; return 1; }
    answer=$(edge_post_as "$dbearer" "/v1/rides/${ride}/${step}" "{\"version\":${version}}")
    case "$answer" in
      *'"state"'*) ;;
      *) echo "/${step} refused: ${answer:0:200}" >&2; return 1 ;;
    esac
  done

  ride_state "$ride" "$p"
}

# `free_passenger` — the index of a fixture passenger holding no non-terminal ride, or empty.
#
# `ux_rides_open_passenger` allows one each, and a completed cash ride wedges its passenger on this
# deployment for good (drill 70's finding), so a drill that hard-codes an index eventually asks a
# question it cannot ask and reads the refusal as a platform answer. Asking for a free one instead
# keeps the drill running against whichever accounts are still usable, and says so when none are.
free_passenger() {
  local i phone open
  for i in 0 1 2 3 4 5; do
    phone=$(env_json ".passengers[${i}].phone")
    [ -n "$phone" ] || continue
    open=$(psql_one "SELECT count(*) FROM rides.rides r JOIN iam.users u ON u.id = r.passenger_id
                      WHERE u.phone = '${phone}'
                        AND r.state NOT IN ('Completed','ExpiredNoDriver','CancelledByRiderBeforeAccept',
                                            'CancelledByRiderAfterAccept','CancelledByDriver',
                                            'CancelledBySystem','Disputed');")
    [ "${open:-1}" = "0" ] && { printf '%s' "$i"; return 0; }
  done
  return 1
}

# `settle_cash <rideId> <passenger-index>` — best effort, so a completed ride does not sit in
# `PaymentPending` holding the passenger's one-open-ride slot for the next run.
#
# `POST /v1/fare/pay {rideId, method:"cash"}` is where D-10's payment state machine starts;
# `fare.yaml` says cash "settles on confirmation", so this may leave the ride short of terminal and
# the caller must not depend on it. It is here because leaving the slot held is worse than trying.
settle_cash() {
  edge_post_as "$(env_json ".passengers[${2:-0}].bearer")" /v1/fare/pay \
    "{\"rideId\":\"${1}\",\"method\":\"cash\"}" >/dev/null 2>&1 || true
}

# `wallet_rejected <rideId> <driverId>` — the audit says the wallet gate is why this driver was not
# offered this ride. `EligibilityGates.Wallet` is the string `wallet_daily_fee`
# (`DispatchRecords.cs`), and an EXCLUDED candidate still gets a `candidate_scores` row naming it —
# which is the assertion, rather than "no offer was placed", because an absent offer has half a
# dozen other causes.
wallet_rejected() {
  [ "$(psql_one "SELECT count(*) FROM dispatch.candidate_scores
                  WHERE ride_id = '${1}' AND driver_id = '${2}'
                    AND breakdown->>'rejectedBy' = 'wallet_daily_fee';")" -ge 1 ]
}

# `offer_released <rideId>` — R-15's release: the offer row is settled and the driver is free.
offer_released() {
  [ "$(psql_one "SELECT count(*) FROM dispatch.offers
                  WHERE ride_id = '${1}' AND (released_at IS NOT NULL OR status <> 'OFFERED');")" -ge 1 ]
}

ride_dispatched() {
  case "$(ride_state "$1" "${2:-0}")" in
    Offered|Accepted|ExpiredNoDriver) return 0 ;;
    *) return 1 ;;
  esac
}

steady_state_quiet() {
  local bearer; bearer=$(env_json '.passengers[0].bearer')
  [ "$(edge_code /v1/.well-known/jwks.json)" = "200" ] || return 1
  case "$(probe_nearby)" in 200*) ;; *) return 1 ;; esac
  # A read that has to reach Postgres, so "serving again" means the database is back and not
  # merely that Redis answered.
  [ "$(edge_code "/v1/trips/$(env_json '.passengers[0].id')" -H "Authorization: Bearer ${bearer}")" = "200" ] || return 1
  [ -n "$(edge_get_as "$bearer" \
      '/v1/fare/estimate?fromLat=6.9271&fromLng=79.8612&toLat=6.8449&toLng=79.8837&vehicleType=three_wheeler&kind=passenger' \
      | jq -r '.fareEstimateToken // empty' 2>/dev/null)" ]
}

# `offer_expiry_lag <rideId>` — milliseconds between the offer's deadline and the instant the
# R-04 backstop marked its timer fired. Negative is impossible; a large number is the finding.
offer_expiry_lag() {
  psql_one "SELECT round(extract(epoch FROM (t.fired_at - o.expires_at)) * 1000)
              FROM rides.timers t
              JOIN dispatch.offers o ON o.ride_id = t.ride_id
             WHERE t.ride_id = '${1}' AND t.kind = 'offer_expiry' AND t.fired_at IS NOT NULL
             ORDER BY o.sent_at DESC LIMIT 1;"
}

# `rotate_refresh <passenger-index>` — spends the stored refresh token, echoes the HTTP code, and
# persists whatever replaced it.
#
# The token is SINGLE-USE (D-29), so a probe that does not write the rotation back leaves the
# fixture holding a spent credential and the NEXT probe is answered 401 for a reason that has
# nothing to do with the fault being drilled. That is not hypothetical: it is what the first run
# of drill 10 did, and it produced a HIGH finding about a platform that had done nothing wrong.
rotate_refresh() {
  local i="$1" token answer code rotated tmp

  token=$(env_json ".passengers[${i}].refreshToken")
  [ -n "$token" ] || { printf 'absent'; return 1; }

  answer=$(edge_curl -w '\n%{http_code}' -X POST -H 'Content-Type: application/json' \
    -H "Idempotency-Key: $(openssl rand -hex 16)" \
    -d "{\"refreshToken\":\"${token}\"}" "${EDGE}/v1/auth/refresh" 2>/dev/null)

  code=$(printf '%s' "$answer" | tail -1)
  rotated=$(printf '%s' "$answer" | sed '$d' | jq -r '.refreshToken // empty' 2>/dev/null)

  if [ -n "$rotated" ]; then
    tmp=$(mktemp)
    jq --arg t "$rotated" ".passengers[${i}].refreshToken = \$t" "$ENV_JSON" > "$tmp" \
      && mv "$tmp" "$ENV_JSON" && chmod 600 "$ENV_JSON"
  fi

  printf '%s' "${code:-000}"
}

# -------------------------------------------------------------------------------------
# The steady state. Four probes, one per plane, each the cheapest honest question about it.
# -------------------------------------------------------------------------------------

# Echoes `<http-code> <limitedLive> <vehicle-count>` for the passenger live map.
probe_nearby() {
  local bearer lat lng body code
  bearer=$(env_json '.passengers[0].bearer')
  lat="${1:-6.9271}"; lng="${2:-79.8612}"

  body=$(edge_curl -H "Authorization: Bearer ${bearer}" -w '\n%{http_code}' \
         "${EDGE}/v1/nearby?lat=${lat}&lng=${lng}&radiusM=5000" 2>/dev/null)
  code=$(printf '%s' "$body" | tail -1)
  body=$(printf '%s' "$body" | sed '$d')

  printf '%s %s %s' \
    "${code:-000}" \
    "$(printf '%s' "$body" | jq -r 'if .limitedLive == null then "-" else .limitedLive end' 2>/dev/null || echo '-')" \
    "$(printf '%s' "$body" | jq -r '.vehicles | length' 2>/dev/null || echo '-')"
}

# `probe_sos <passenger-index>` — raise a real SOS and time it. Echoes `<ms> <code> <smsStatus>`.
#
# D-33 is the one SLO on this platform with a person on the other end of it: "SMS dispatched ≤ 5 s
# p99, secondary gateway fallback". It is measured under every infrastructure fault rather than in
# the steady state, because a five-second budget that only holds when nothing is broken is not a
# safety guarantee. `smsStatus` is what separates "the alert went out" from "the alert is on the
# admin console and nowhere else" — the C052 contract change exists for exactly this question.
#
# It writes a real `safety.sos_events` row and sends over the replica's dev SMS sender. That is
# the point: an SOS that were mocked here would be measuring the mock.
probe_sos() {
  local i="${1:-0}" bearer started answer code body ms
  bearer=$(env_json ".passengers[${i}].bearer")

  started=$(now_ms)
  answer=$(edge_curl -w '\n%{http_code}' -X POST \
    -H "Authorization: Bearer ${bearer}" -H 'Content-Type: application/json' \
    -H "Idempotency-Key: $(openssl rand -hex 16)" \
    -d '{"lat":6.9271,"lng":79.8612,"role":"passenger"}' \
    "${EDGE}/v1/sos" 2>/dev/null)
  ms=$(since_ms "$started")

  code=$(printf '%s' "$answer" | tail -1)
  body=$(printf '%s' "$answer" | sed '$d')

  printf '%s %s %s' "$ms" "${code:-000}" \
    "$(printf '%s' "$body" | jq -r '.smsStatus // "-"' 2>/dev/null || echo '-')"
}

# What an SOS did with nothing broken. Set once by run-drills.sh's pre-flight so that every
# under-fault probe is compared against this deployment's own baseline rather than against the
# contract — on this replica the baseline is already `Failed`, and a drill that scored that as a
# chaos finding would blame the fault for a configuration.
CHAOS_SOS_BASELINE=""
CHAOS_SOS_BASELINE_MS=""

# `sos_degraded_row <probe-output>` — the §14.1 table row and the D-33 verdict, in one place, so
# every drill words it identically.
sos_degraded_row() {
  local out="$1"
  local ms; ms=$(printf '%s' "$out" | awk '{print $1}')
  local code; code=$(printf '%s' "$out" | awk '{print $2}')
  local status; status=$(printf '%s' "$out" | awk '{print $3}')

  degraded_row "SOS (\`POST /v1/sos\`, D-33)" \
    "HTTP ${code} in ${ms} ms, smsStatus=${status} (baseline ${CHAOS_SOS_BASELINE:-?} in ${CHAOS_SOS_BASELINE_MS:-?} ms)" \
    "not described — §14.1 has no SOS row; §13.3 sets the SLO at p99 ≤ 5 s"

  if [ "$code" != "200" ]; then
    finding HIGH "SOS is refused under this fault: \`POST /v1/sos\` answered HTTP ${code} after \
${ms} ms, where the same call answered 200 with nothing broken. D-33 makes this the one request \
with a person on the other end of it, and ADD §14.1's degradation table has no SOS row at all."
    return
  fi

  if [ "${ms:-99999}" -gt 5000 ]; then
    finding HIGH "D-33's five-second budget does not survive this fault: \`POST /v1/sos\` \
answered ${status} after ${ms} ms against a ${CHAOS_SOS_BASELINE_MS} ms baseline. \
\`Sos:SloMs\` is 5000 and ADD §13.3 states it as p99."
    return
  fi

  # A status that got WORSE under the fault is the interesting case; one that was already
  # `Failed` before anything was broken is drill 00's finding, not this drill's.
  if [ "$status" = "$CHAOS_SOS_BASELINE" ]; then
    ok "D-33 held under fault: SOS answered ${status} in ${ms} ms (≤ 5 s, baseline ${CHAOS_SOS_BASELINE_MS} ms)"
  elif [ "$status" = "Dispatched" ]; then
    ok "D-33 held under fault and did better than the baseline: ${status} in ${ms} ms"
  else
    finding HIGH "The SOS SMS path degrades under this fault: smsStatus went from \
${CHAOS_SOS_BASELINE} to ${status}. The alert is recorded and on the admin live feed; nobody was \
texted."
  fi
}

# `steady_state <label>` — records the four probes and fails if the platform is not already
# serving. Called before a fault and after the rollback.
steady_state() {
  local label="$1" failures=0

  local nearby; nearby=$(probe_nearby)
  local jwks;   jwks=$(edge_code /v1/.well-known/jwks.json)
  local quote;  quote=$(edge_get_as "$(env_json '.passengers[0].bearer')" \
                  "/v1/fare/estimate?fromLat=6.9271&fromLng=79.8612&toLat=6.8449&toLng=79.8837&vehicleType=three_wheeler&kind=passenger" \
                  | jq -r 'if .fareEstimateToken then "ok" else "refused" end' 2>/dev/null)
  local active; active=$(edge_code /v1/rides/active -H "Authorization: Bearer $(env_json '.passengers[0].bearer')")

  case "$jwks"   in 200) ;; *) failures=$((failures+1)) ;; esac
  case "$nearby" in 200*) ;; *) failures=$((failures+1)) ;; esac
  case "$quote"  in ok) ;; *) failures=$((failures+1)) ;; esac
  # 404 is the *success* shape here: `GET /v1/rides/active` answers 404 when the passenger has no
  # non-terminal ride, which is the steady state a drill starts from. Either code proves the same
  # three things — the bearer validated, ride-svc answered, and it reached Postgres to find out.
  case "$active" in 200|404) ;; *) failures=$((failures+1)) ;; esac

  if [ "$failures" -eq 0 ]; then
    note "${label}: jwks=${jwks} nearby=[${nearby}] fare=${quote} rides/active=${active}"
    return 0
  fi

  bad "${label}: ${failures} of 4 steady-state probes failed — jwks=${jwks} nearby=[${nearby}] fare=${quote} rides/active=${active}"
  return 1
}
