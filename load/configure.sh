#!/usr/bin/env bash
# =====================================================================================
# load/configure.sh — write load/env.json, and refuse to do it anywhere but the replica.
#
#   bash load/configure.sh                 # accounts + a 750-vehicle fleet fixture
#   bash load/configure.sh --fleet 3750    # size the fixture for the burst profile
#   bash load/configure.sh --accounts-only # skip the warm-up; keep the cell map you have
#
# ------------------------------------------------------------------------------------
# SYNTHETIC DATA ONLY, AND THE SAME THREE REFUSALS seed.sh MAKES
# ------------------------------------------------------------------------------------
# This writes rows into iam.users, registry.vehicles and billing.wallets, and it publishes
# hundreds of thousands of positions. Against a database holding real riders' phone numbers
# that would be a catastrophe rather than a mistake, so the checks are the ones
# `infra/replica/seed.sh` makes and they are not advisory:
#
#   1. the compose project is `mageride-replica`;
#   2. `replica.synthetic_marker` exists — this database was created by seed.sql;
#   3. every account this script provisions is in the +9477 003 xxxx block and every vehicle
#      in the WP-LT-xxxx series, so its own rows are always distinguishable from seed.sh's.
#
# ------------------------------------------------------------------------------------
# WHY THE BEARERS ARE OBTAINED THROUGH THE REAL ROUTES
# ------------------------------------------------------------------------------------
# The tokens are RS256, signed by iam-svc's own key ring, so there is no honest way to mint one
# outside the platform — and a load suite carrying a signing key would be a load suite that
# could not be pointed at anything but a deployment it had the key for. So each account signs
# in exactly as its app does: `POST /v1/auth/otp/request`, the code out of the dev SMS sender's
# log line, `POST /v1/auth/otp/verify`. That sender writes the OTP in clear and refuses to run
# outside Development unless `Sms:AllowDevSenderOutsideDevelopment` is set — the replica sets
# it, on synthetic numbers, and that is the whole reason this is possible here and impossible
# in production.
#
# The MQTT session JWT is NOT obtained this way (E-02: it is a different credential): the
# publishers mint their own with the broker's shared secret, exactly as mqtt-bridge-svc does.
# See load/lib/jwt.js.
# =====================================================================================
set -uo pipefail

cd "$(dirname -- "${BASH_SOURCE[0]}")" || exit 2
LOAD_DIR="$PWD"
cd .. || exit 2
REPO_ROOT="$PWD"

COMPOSE="infra/replica/docker-compose.light-replica.yml"
PROJECT="mageride-replica"
ENV_FILE="$REPO_ROOT/infra/replica/.env.replica"
OUT="$LOAD_DIR/env.json"

FLEET=750
ACCOUNTS_ONLY=0
PASSENGERS=12
DRIVERS=12

while [ $# -gt 0 ]; do
  case "$1" in
    --fleet) FLEET="$2"; shift 2 ;;
    --passengers) PASSENGERS="$2"; shift 2 ;;
    --drivers) DRIVERS="$2"; shift 2 ;;
    --accounts-only) ACCOUNTS_ONLY=1; shift ;;
    *) echo "unknown argument: $1" >&2; exit 2 ;;
  esac
done

ok()   { printf '  \033[32m✓\033[0m %s\n' "$*"; }
warn() { printf '  \033[33m!\033[0m %s\n' "$*"; }
note() { printf '    %s\n' "$*"; }
step() { printf '\n\033[1m▸ %s\033[0m\n' "$*"; }
die()  { printf '  \033[31m✗\033[0m %s\n' "$*" >&2; exit 1; }

for tool in curl jq python3 openssl docker k6; do
  command -v "$tool" >/dev/null 2>&1 || die "$tool is required and is not on PATH
      (k6: https://github.com/grafana/k6/releases — the suite needs no extension build)"
done

[ -f "$ENV_FILE" ] || die ".env.replica is absent — run infra/replica/deploy.sh first"
set -a
# shellcheck disable=SC1090
. "$ENV_FILE"
set +a

PG_USER_EFF="${PG_USER:-mageride}"
PG_DB_EFF="${PG_DATABASE:-mageride}"
EDGE="https://127.0.0.1:${HAPROXY_HTTPS_PORT:-443}"
HOSTHDR="${REPLICA_HOSTNAME:-replica.mageride.lk}"
MQTT_URL="wss://127.0.0.1:${HAPROXY_MQTT_WSS_PORT:-8084}/mqtt"

psql_q() {
  docker compose -f "$COMPOSE" exec -T postgres \
    psql -U "$PG_USER_EFF" -d "$PG_DB_EFF" -v ON_ERROR_STOP=1 -qtAX -c "$1" 2>&1
}

redis_eval() {
  docker compose -f "$COMPOSE" exec -T redis redis-cli --no-raw EVAL "$1" 0 2>&1
}

api() {
  local method="$1" path="$2" body="${3:-}"
  local args=(-sS -k --max-time 30 -H "Host: ${HOSTHDR}" -H 'Content-Type: application/json'
              -H "Idempotency-Key: $(openssl rand -hex 16)")
  [ -n "$body" ] && args+=(-d "$body")
  curl "${args[@]}" -X "$method" "${EDGE}${path}" 2>/dev/null
}

# The same call, waiting out a 429.
#
# The gateway's `auth` policy is 30 requests per minute per CALLER ADDRESS, and on this
# deployment every caller has the same address: `Gateway__ForwardedHeaders__KnownProxies__0` is
# set to the string `haproxy`, which `IPAddress.TryParse` rejects, so the known-proxy list is
# empty, X-Forwarded-For is ignored and `context.Connection.RemoteIpAddress` is HAProxy's for
# every request on the platform. Provisioning 48 accounts is 96 auth calls and the 31st is
# refused. The defect is load/report.md's; this is what the script has to do meanwhile.
api_paced() {
  local method="$1" path="$2" body="${3:-}" attempt=1 response

  while [ "$attempt" -le 5 ]; do
    response=$(api "$method" "$path" "$body")

    case "$response" in
      *'"rate-limited"'*|*'Rate limit exceeded'*)
        sleep "${LOAD_AUTH_BACKOFF:-25}"
        attempt=$((attempt + 1))
        ;;
      *) printf '%s' "$response"; return 0 ;;
    esac
  done

  printf '%s' "$response"
  return 1
}

# -------------------------------------------------------------------------------------
step "1/5  is this the replica?"
# -------------------------------------------------------------------------------------
running=$(docker compose -f "$COMPOSE" ps --services --filter status=running 2>/dev/null | tr '\n' ' ')
case "$running" in
  *postgres*) ok "the $PROJECT project is up" ;;
  *) die "postgres is not running under the $PROJECT project. This script talks to that database
      and to nothing else, and it must never reach another one." ;;
esac

marker=$(psql_q "SELECT count(*) FROM replica.synthetic_marker WHERE marker = 'mageride-replica-synthetic';" | tr -d ' \r')
[ "$marker" = "1" ] || die "replica.synthetic_marker is absent. This database was not created by
      infra/replica/seed.sql, so it is not one this script may write synthetic accounts into."
ok "replica.synthetic_marker is present — synthetic data only"

# The JWKS, not a health probe: `/health/**` is denied at the edge by design (smoke.sh asserts
# that denial), and a bearer that cannot be validated is what every later step depends on.
jwks=$(api GET /v1/.well-known/jwks.json)
case "$jwks" in
  *'"keys"'*) ok "the edge answers and publishes the JWKS a bearer is validated against" ;;
  *) die "GET /v1/.well-known/jwks.json answered: ${jwks:0:160}
      Every authenticated call will answer 500 until this serves (the C126 defect)." ;;
esac

# The C129 defect this suite found on its first run: fanout-svc was still carrying the
# pre-C126 `Jwt__JwksUrl`, so every authenticated /hubs/live connection answered 500 while
# every contract operation stayed green. Checked here because the failure is invisible from
# the outside until a subscriber tries to connect.
jwks_from_fanout=$(docker compose -f "$COMPOSE" exec -T fanout printenv Jwt__JwksUrl 2>/dev/null | tr -d '\r')
case "$jwks_from_fanout" in
  */v1/.well-known/jwks.json) ok "fanout-svc's Jwt__JwksUrl resolves (C126's corrected path)" ;;
  '') warn "could not read fanout-svc's Jwt__JwksUrl" ;;
  *) die "fanout-svc has Jwt__JwksUrl=${jwks_from_fanout}, which answers 404: every /hubs/live
      connection will answer 500 and no D-19 measurement is possible. The container is running a
      pre-C126 environment — recreate it:
        docker compose -f $COMPOSE up -d --no-deps --force-recreate fanout" ;;
esac

# -------------------------------------------------------------------------------------
step "2/5  the load accounts"
# -------------------------------------------------------------------------------------
# +9477 003 xxxx: seed.sql uses 0000/0010/0020 for passengers, drivers and the fleet owner, so
# this block cannot collide with it and `LIKE '+9477003%'` is the whole cleanup predicate.
psql_q "
BEGIN;
INSERT INTO iam.users (phone, role, first_name, language)
SELECT '+9477003' || lpad(n::text, 4, '0'), 'passenger', 'Load Passenger ' || n, 'en'
  FROM generate_series(1, ${PASSENGERS}) AS n
ON CONFLICT (phone) DO NOTHING;

INSERT INTO iam.users (phone, role, first_name, language)
SELECT '+9477003' || lpad((1000 + n)::text, 4, '0'), 'driver', 'Load Driver ' || n, 'en'
  FROM generate_series(1, ${DRIVERS}) AS n
ON CONFLICT (phone) DO NOTHING;

-- One APPROVED Mode C three-wheeler per load driver. APPROVED deliberately, and for the same
-- reason seed.sql records: the AL-50 gate is a Verification Officer's decision and no route on
-- the platform makes this write for a load fixture. A replica of PENDING vehicles dispatches
-- nothing at all.
-- The plate is derived from the driver's phone, NOT from a row_number over the drivers who do
-- not have a vehicle yet. That is what the first version did, and a re-run with more drivers
-- restarts the numbering at 1 and collides with ux_vehicles_regno_active on WP-LT-0001. A
-- derived plate makes the whole statement idempotent for any --drivers value.
INSERT INTO registry.vehicles
  (owner_id, registration_number, vehicle_type, mode, status, driver_name, onboarding_status)
SELECT u.id, 'WP-LT-' || right(u.phone, 4),
       'three_wheeler', 'C', 'APPROVED', u.first_name, 'approved'
  FROM iam.users u
 WHERE u.role = 'driver' AND u.phone LIKE '+94770031%'
   AND NOT EXISTS (SELECT 1 FROM registry.vehicles v WHERE v.owner_id = u.id);

-- D-08's daily-fee gate refuses a driver from their SECOND trip of the Colombo day with an
-- empty wallet, and a dispatch profile books many rides per driver. Seeded exactly as
-- tests/E2E's ModeCFleet seeds it, and for the same reason: a load run must not be measuring a
-- wallet rule.
WITH account AS (
  INSERT INTO billing.accounts (owner_type, owner_id, currency, balance_minor)
  SELECT 'driver', u.id, 'LKR', 5000000 FROM iam.users u
   WHERE u.role = 'driver' AND u.phone LIKE '+94770031%'
      ON CONFLICT (owner_type, owner_id, currency) WHERE owner_id IS NOT NULL
      DO UPDATE SET balance_minor = EXCLUDED.balance_minor
  RETURNING id)
INSERT INTO billing.wallets (account_id, balance_minor)
SELECT id, 5000000 FROM account
    ON CONFLICT (account_id) DO UPDATE SET balance_minor = EXCLUDED.balance_minor;
COMMIT;" >/dev/null || die "could not provision the load accounts"

seeded=$(psql_q "SELECT count(*) FROM iam.users WHERE phone LIKE '+9477003%';" | tr -d ' \r')
vehicles=$(psql_q "SELECT count(*) FROM registry.vehicles WHERE registration_number LIKE 'WP-LT-%';" | tr -d ' \r')
ok "${seeded} load accounts, ${vehicles} APPROVED Mode C vehicles"

# -------------------------------------------------------------------------------------
step "3/5  bearers, through the routes the apps use"
# -------------------------------------------------------------------------------------
# In two passes — every OTP requested, THEN one read of the log, THEN every verify.
#
# The obvious shape (request, read, verify, per account) is what the first version did, and it
# takes about a minute PER ACCOUNT on this box. `docker logs --since` has no index into the
# json-file driver's output and rescans the whole file, and app-services' log is 2.2 GB after a
# day and a half of running idle — the replica's compose file sets no `logging:` options at all,
# so nothing rotates. `--tail` seeks from the end instead and costs 70 ms. The finding is in
# load/report.md; this is the workaround it needs in the meantime.
APP_CONTAINER=$(docker compose -f "$COMPOSE" ps -q app-services 2>/dev/null | head -1)
[ -n "$APP_CONTAINER" ] || die "app-services is not running"

# `signed_in` accumulates lines of `phone<TAB>role<TAB>userId<TAB>bearer`.
signed_in=""
requested=""

request_otp() {
  local phone="$1" role="$2" auth
  auth=$(api_paced POST /v1/auth/otp/request \
    "{\"phone\":\"${phone}\",\"deviceId\":\"c129-load-${phone#+}\",\"role\":\"${role}\"}" \
    | jq -r '.authId // empty')

  [ -n "$auth" ] || { warn "no authId for ${phone}"; return 1; }
  requested="${requested}${phone}	${role}	${auth}
"
}

for n in $(seq 1 "$PASSENGERS"); do
  request_otp "+9477003$(printf '%04d' "$n")" passenger || true
done
for n in $(seq 1 "$DRIVERS"); do
  request_otp "+9477003$(printf '%04d' $((1000 + n)))" driver || true
done

asked=$(printf '%s' "$requested" | grep -c . || true)
note "${asked} OTPs requested; reading them out of the dev SMS sender's log"

# 4,000 lines covers 24 accounts comfortably on a container logging ~200 lines/s.
codes=$(docker logs --tail "${LOAD_LOG_TAIL:-4000}" "$APP_CONTAINER" 2>&1 \
  | grep -o "OTP for +[0-9]* (..) is [0-9]\{6\}" \
  | sed 's/OTP for \(+[0-9]*\) (..) is \([0-9]\{6\}\)/\1 \2/')

[ -n "$codes" ] || die "no '[dev-sms] OTP for …' lines in the app-services log. That sender is what
      makes this possible at all — check Sms__Provider=dev and
      Sms__AllowDevSenderOutsideDevelopment=true in .env.replica."

while IFS=$'\t' read -r phone role auth; do
  [ -n "$phone" ] || continue

  # The LAST code for this phone: a re-run of this script requests a second OTP and the first is
  # no longer the live one.
  code=$(printf '%s\n' "$codes" | grep "^${phone} " | tail -1 | awk '{print $2}')

  if [ -z "$code" ]; then
    warn "no OTP found for ${phone}"
    continue
  fi

  token=$(api_paced POST /v1/auth/otp/verify \
    "{\"authId\":\"${auth}\",\"otp\":\"${code}\",\"deviceId\":\"c129-load-${phone#+}\"}")

  access=$(printf '%s' "$token" | jq -r '.accessToken // empty')
  # `.user.userId`, not `.user.id` — `UserProfile` in backend/contracts/iam.yaml names it
  # userId and requires it. `.user.id` is silently empty, which produced an env.json whose
  # driver ids were all blank and an accept race that presented an empty driverId.
  user_id=$(printf '%s' "$token" | jq -r '.user.userId // .user.id // empty')

  if [ -z "$access" ]; then
    warn "sign-in refused for ${phone}: $(printf '%s' "$token" | head -c 160)"
    continue
  fi

  signed_in="${signed_in}${phone}	${role}	${user_id}	${access}
"
done <<EOF
$requested
EOF

count=$(printf '%s' "$signed_in" | grep -c . || true)
[ "$count" -gt 0 ] || die "not one account could sign in — no profile can run"
ok "${count} bearers obtained (30-minute access tokens, D-29)"

# The driver's vehicle, which `POST /v1/standby/online` names and which the dispatch profile
# publishes positions for. Read once for the whole set rather than per driver.
DRIVER_VEHICLES=$(psql_q "
  SELECT u.phone || ' ' || v.id
    FROM iam.users u JOIN registry.vehicles v ON v.owner_id = u.id
   WHERE u.phone LIKE '+94770031%' AND v.registration_number LIKE 'WP-LT-%';" | tr -d '\r')

# -------------------------------------------------------------------------------------
step "4/5  the fleet fixture — which res-7 cell each vehicle publishes into"
# -------------------------------------------------------------------------------------
CELLS_JSON='{}'

if [ "$ACCOUNTS_ONLY" = "1" ]; then
  CELLS_JSON=$(jq -c '.cellsByVehicle // {}' "$OUT" 2>/dev/null || echo '{}')
  warn "--accounts-only: keeping the cell map already in env.json"
else
  # A preliminary env.json, because warmup.js reads its target out of it.
  python3 - "$OUT" "$MQTT_URL" "$MQTT_JWT_SECRET" "$EDGE" "$HOSTHDR" <<'PY'
import json, sys
path, mqtt_url, secret, edge, host = sys.argv[1:6]
json.dump({'mqttUrl': mqtt_url, 'mqttSecret': secret, 'edge': edge, 'host': host},
          open(path, 'w'), indent=2)
PY

  note "publishing one orbit for ${FLEET} vehicles and reading the cell back out of veh:meta"
  k6 run --quiet "$LOAD_DIR/warmup.js" -e LOAD_CONNECTIONS="$FLEET" >/dev/null 2>&1 \
    || warn "the warm-up run reported failures; the cell map may be short"

  # One round trip for the whole fleet. A HGET per vehicle would be 3,750 round trips through
  # `docker compose exec`, which takes longer than the warm-up itself.
  raw=$(redis_eval "
local out = {}
local cursor = '0'
repeat
  local r = redis.call('SCAN', cursor, 'MATCH', 'veh:meta:10ad10ad-*', 'COUNT', 1000)
  cursor = r[1]
  for _, k in ipairs(r[2]) do
    local cell = redis.call('HGET', k, 'cell')
    if cell then out[#out+1] = string.sub(k, 10) .. ' ' .. cell end
  end
until cursor == '0'
return out")

  CELLS_JSON=$(printf '%s' "$raw" | python3 -c '
import re, sys, json

cells = {}
for line in sys.stdin:
    found = re.search(r"10ad10ad-0000-4000-8000-(\d{12})\s+([0-9a-f]{15})", line)
    if found:
        cells[str(int(found.group(1)))] = found.group(2)
print(json.dumps(cells))')

  mapped=$(printf '%s' "$CELLS_JSON" | jq 'length')
  distinct=$(printf '%s' "$CELLS_JSON" | jq '[.[]] | unique | length')

  [ "$mapped" -gt 0 ] || die "no vehicle reached veh:meta. The chain EMQX -> mqtt-bridge ->
      telemetry.raw -> position-processor -> Redis is broken somewhere; run
      \`k6 run load/probe.js\` for the shortest possible reproduction."

  ok "${mapped}/${FLEET} vehicles mapped into ${distinct} res-7 cells (~$(python3 -c "print(f'{${mapped}/${distinct}:.1f}')") per cell; ADD §16.3 models 5)"

  if [ "$mapped" -lt "$FLEET" ]; then
    warn "$((FLEET - mapped)) vehicles have no veh:meta hash — they were refused between EMQX and Redis"
  fi
fi

# -------------------------------------------------------------------------------------
step "5/5  load/env.json"
# -------------------------------------------------------------------------------------
# `signed_in` is an ARGUMENT, not stdin. `python3 - <<'PY'` already uses stdin for the program
# text, so a `printf | python3 - <<PY` pipeline is silently discarded and every account vanishes
# — which is exactly what the first version did: "24 bearers obtained" followed by
# "passengers=0 drivers=0".
python3 - "$OUT" "$MQTT_URL" "$MQTT_JWT_SECRET" "$EDGE" "$HOSTHDR" "$CELLS_JSON" "$FLEET" "$DRIVER_VEHICLES" "$signed_in" <<'PY'
import json, sys

path, mqtt_url, secret, edge, host, cells_json, fleet, driver_vehicles, signed_in = sys.argv[1:10]

vehicles = {}
for line in driver_vehicles.splitlines():
    parts = line.split()
    if len(parts) == 2:
        vehicles[parts[0]] = parts[1]

passengers, drivers = [], []
for line in signed_in.splitlines():
    parts = line.split('\t')
    if len(parts) != 4:
        continue
    phone, role, user_id, bearer = parts
    entry = {'phone': phone, 'id': user_id, 'bearer': bearer}
    if role == 'driver':
        # The vehicle `POST /v1/standby/online` names and whose topic the dispatch profile
        # publishes on: a driver with no fresh position falls out of the candidate pool at
        # D5' §3.2's freshness gate, and the ride then rests in Matching for no visible reason.
        entry['vehicleId'] = vehicles.get(phone)
        drivers.append(entry)
    else:
        passengers.append(entry)

document = {
    'mqttUrl': mqtt_url,
    'mqttSecret': secret,
    'edge': edge,
    'host': host,
    # Any passenger's bearer will do for a geocell subscriber: the public map is not
    # entitlement-scoped (D6' §5.2 grants Mode A and idle Mode C unconditionally), and reusing
    # one identity across sockets is what makes the §16.3 subscriber scale reachable at all.
    # It does mean the per-USER entitlement lookup at connect is measured once rather than N
    # times, which load/report.md states.
    'watcherToken': passengers[0]['bearer'] if passengers else None,
    'passengers': passengers,
    'drivers': drivers,
    'fleetSize': int(fleet),
    'cellsByVehicle': json.loads(cells_json),
}

json.dump(document, open(path, 'w'), indent=2)
print(f"  passengers={len(passengers)} drivers={len(drivers)} cells={len(document['cellsByVehicle'])}")
PY

chmod 600 "$OUT"
ok "wrote load/env.json (gitignored, 0600 — it carries the broker secret and live bearers)"

echo
echo "  Ready. The access tokens live 30 minutes (D-29); re-run this script when they lapse."
echo "    k6 run load/ingest.js --summary-export=load/out/ingest.json"
echo "    bash load/run.sh"
