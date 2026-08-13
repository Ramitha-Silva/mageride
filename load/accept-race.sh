#!/usr/bin/env bash
# =====================================================================================
# load/accept-race.sh — ADD §11.11's atomic single-winner accept, under contention.
#
#   bash load/accept-race.sh            # 12 concurrent accepts on one offer, 5 rounds
#   bash load/accept-race.sh --rounds 20 --racers 24
#
# ------------------------------------------------------------------------------------
# WHY THIS IS A SHELL SCRIPT AND NOT PART OF load/dispatch.js
# ------------------------------------------------------------------------------------
# `POST /v1/rides/{rideId}/offer/{driverId}/accept` takes the **offerId** and matches it against
# `rides.rides.current_offer_id` in the conditional update. The offer id reaches a driver in the
# FCM `RIDE_OFFER` payload and in nothing else: `GET /v1/rides/{id}/state` answers
# `{state, version, offerExpiresAt}`, `RideDetail` carries a `driver` block only from `Accepted`
# onward, and `backend/contracts/dispatch.yaml` has no driver-side offer read at all. A REST
# client therefore cannot accept an offer it was not pushed, and k6 cannot read Postgres.
#
# So the offer id is taken from `dispatch.offers` — standing in for the push payload, which is
# the same thing notification-svc would have delivered — and the race is driven from curl. That
# stand-in is for something OUTSIDE the platform (the push transport), which is the same line
# `tests/E2E/CLAUDE.md` draws for its SMS gateway.
#
# THE INVARIANT
# ------------------------------------------------------------------------------------
# N drivers tap Accept on one offer within a few milliseconds. Exactly one row count is 1
# (§11.11, R-02's `WHERE state IN ('Matching','Offered') AND offer_expires_at > now() AND
# version = :v`), every other caller is refused, and the ride ends with exactly one
# accepted_driver_id. `tests/E2E`'s ConcurrentAcceptScenario asserts the same property in
# process, a hundred times; this asserts it through TLS, HAProxy, the gateway and PgBouncer.
# =====================================================================================
set -uo pipefail

cd "$(dirname -- "${BASH_SOURCE[0]}")" || exit 2
LOAD_DIR="$PWD"
cd .. || exit 2

COMPOSE="infra/replica/docker-compose.light-replica.yml"
ROUNDS=5
RACERS=12

while [ $# -gt 0 ]; do
  case "$1" in
    --rounds) ROUNDS="$2"; shift 2 ;;
    --racers) RACERS="$2"; shift 2 ;;
    *) echo "unknown argument: $1" >&2; exit 2 ;;
  esac
done

ok()   { printf '  \033[32m✓\033[0m %s\n' "$*"; }
bad()  { printf '  \033[31m✗\033[0m %s\n' "$*"; }
note() { printf '    %s\n' "$*"; }

[ -f infra/replica/.env.replica ] || { echo "no .env.replica" >&2; exit 2; }
set -a
# shellcheck disable=SC1091
. infra/replica/.env.replica
set +a

EDGE="https://127.0.0.1:${HAPROXY_HTTPS_PORT:-443}"
HOSTHDR="${REPLICA_HOSTNAME:-replica.mageride.lk}"
ENVJSON="$LOAD_DIR/env.json"

[ -f "$ENVJSON" ] || { echo "no load/env.json — run \`bash load/configure.sh\`" >&2; exit 2; }

psql_q() {
  docker compose -f "$COMPOSE" exec -T postgres \
    psql -U "${PG_USER:-mageride}" -d "${PG_DATABASE:-mageride}" -qtAX -c "$1" 2>/dev/null | tr -d ' \r'
}

# The passenger who books, and the drivers who will race. The winner is decided by the
# platform: only one of them holds the live offer, and the others are presenting an offer id
# that is not theirs — which is precisely the case §11.11 has to refuse.
PASSENGER=$(python3 -c "
import json; d=json.load(open('$ENVJSON'))
p=d['passengers'][0]; print(p['bearer'])" 2>/dev/null)

[ -n "$PASSENGER" ] || { echo "no passenger bearer in load/env.json" >&2; exit 2; }

mapfile -t DRIVER_IDS < <(python3 -c "
import json; d=json.load(open('$ENVJSON'))
for x in d['drivers'][:$RACERS]: print(x['id'], x['bearer'], x['vehicleId'])")

[ "${#DRIVER_IDS[@]}" -gt 1 ] || { echo "need at least two drivers in load/env.json" >&2; exit 2; }

api() {
  local method="$1" path="$2" bearer="$3" body="${4:-}"
  local args=(-sS -k --max-time 30 -H "Host: ${HOSTHDR}" -H 'Content-Type: application/json'
              -H "Authorization: Bearer ${bearer}" -H "Idempotency-Key: $(openssl rand -hex 16)")
  [ -n "$body" ] && args+=(-d "$body")
  curl "${args[@]}" -X "$method" "${EDGE}${path}" -w '\n%{http_code}' 2>/dev/null
}

echo "  ${ROUNDS} rounds, ${#DRIVER_IDS[@]} racers each"

# Every driver online at one pickup, so they are all in one candidate pool and any of them
# could be the one dispatch chooses.
PICKUP_LAT=6.9271
PICKUP_LNG=79.8612

for entry in "${DRIVER_IDS[@]}"; do
  read -r _ bearer vehicle <<< "$entry"
  api POST /v1/standby/online "$bearer" \
    "{\"vehicleId\":\"${vehicle}\",\"position\":{\"lat\":${PICKUP_LAT},\"lng\":${PICKUP_LNG}}}" >/dev/null
done

winners=0
rounds_run=0
double_accepts=0
no_offer=0

for round in $(seq 1 "$ROUNDS"); do
  quote=$(api GET "/v1/fare/estimate?fromLat=${PICKUP_LAT}&fromLng=${PICKUP_LNG}&toLat=6.8441&toLng=79.8837&vehicleType=three_wheeler&kind=passenger" "$PASSENGER")
  token=$(printf '%s' "$quote" | head -n -1 | python3 -c "import sys,json;print(json.load(sys.stdin).get('fareEstimateToken',''))" 2>/dev/null)

  [ -n "$token" ] || { bad "round ${round}: no fare estimate"; continue; }

  booking=$(api POST /v1/rides/request "$PASSENGER" "{
    \"clientRequestId\":\"$(python3 -c 'import uuid;print(uuid.uuid4())')\",
    \"kind\":\"passenger\",
    \"pickup\":{\"lat\":${PICKUP_LAT},\"lng\":${PICKUP_LNG},\"address\":\"C129 race pickup\"},
    \"dropoff\":{\"lat\":6.8441,\"lng\":79.8837,\"address\":\"C129 race dropoff\"},
    \"vehicleType\":\"three_wheeler\",
    \"fareEstimateToken\":\"${token}\",
    \"paymentMethod\":\"cash\"}")

  status=$(printf '%s' "$booking" | tail -1)
  ride=$(printf '%s' "$booking" | head -n -1 | python3 -c "import sys,json;print(json.load(sys.stdin).get('rideId',''))" 2>/dev/null)

  if [ "$status" != "202" ] || [ -z "$ride" ]; then
    bad "round ${round}: booking answered ${status}"
    continue
  fi

  # Wait for dispatch-svc to place the offer. Read from the table, because that is where the
  # push payload would have come from.
  offer=""; version=""
  for _ in $(seq 1 60); do
    offer=$(psql_q "SELECT current_offer_id::text FROM rides.rides WHERE id = '${ride}' AND state = 'Offered';")
    [ -n "$offer" ] && break
    sleep 0.5
  done

  if [ -z "$offer" ]; then
    no_offer=$((no_offer + 1))
    api POST "/v1/rides/${ride}/cancel" "$PASSENGER" '{"reason":"OTHER","version":0}' >/dev/null
    continue
  fi

  version=$(psql_q "SELECT version FROM rides.rides WHERE id = '${ride}';")
  rounds_run=$((rounds_run + 1))

  # The race. Every accept is fired into the background at once and the statuses are collected
  # afterwards; starting them sequentially would let the first finish before the second begins,
  # which is a test of nothing.
  tmp=$(mktemp -d)
  index=0
  for entry in "${DRIVER_IDS[@]}"; do
    read -r driver bearer _ <<< "$entry"
    (
      api POST "/v1/rides/${ride}/offer/${driver}/accept" "$bearer" \
        "{\"offerId\":\"${offer}\",\"version\":${version}}" | tail -1 > "${tmp}/${index}"
    ) &
    index=$((index + 1))
  done
  wait

  accepted=$(grep -lx 200 "${tmp}"/* 2>/dev/null | wc -l)
  rm -rf "$tmp"

  # The platform's own answer, which is the one that matters: a second winner would show as two
  # ACCEPTED offers or as a ride whose accepted_driver_id moved.
  accepted_rows=$(psql_q "SELECT count(*) FROM dispatch.offers WHERE ride_id = '${ride}' AND status = 'ACCEPTED';")
  accepted_driver=$(psql_q "SELECT coalesce(accepted_driver_id::text, '') FROM rides.rides WHERE id = '${ride}';")

  if [ "$accepted" = "1" ] && [ "$accepted_rows" = "1" ] && [ -n "$accepted_driver" ]; then
    winners=$((winners + 1))
  else
    double_accepts=$((double_accepts + 1))
    bad "round ${round}: ${accepted} HTTP 200s, ${accepted_rows} ACCEPTED offer rows, driver='${accepted_driver}'"
  fi

  # Put the ride and the driver back. A system cancel is the ride-svc route dispatch-svc and
  # admin-bff use; from here the passenger's own cancel is the reachable one.
  version=$(psql_q "SELECT version FROM rides.rides WHERE id = '${ride}';")
  api POST "/v1/rides/${ride}/cancel" "$PASSENGER" "{\"reason\":\"OTHER\",\"version\":${version}}" >/dev/null
  sleep 1
done

for entry in "${DRIVER_IDS[@]}"; do
  read -r _ bearer _ <<< "$entry"
  api POST /v1/standby/offline "$bearer" '{}' >/dev/null
done

echo
if [ "$rounds_run" -eq 0 ]; then
  bad "no round reached an offer (${no_offer} bookings never got one) — nothing was raced"
  exit 1
fi

if [ "$double_accepts" -eq 0 ]; then
  ok "${winners}/${rounds_run} rounds had exactly one winner over ${#DRIVER_IDS[@]} concurrent accepts"
  note "ADD §11.11 holds through TLS, HAProxy, the gateway and PgBouncer, not only in process"
  exit 0
fi

bad "${double_accepts}/${rounds_run} rounds did not have exactly one winner"
exit 1
