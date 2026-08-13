#!/usr/bin/env bash
# =====================================================================================
# chaos/configure.sh — the fixture the drills break things around, and the three refusals that
# keep it on the replica.
#
#   bash chaos/configure.sh              # accounts + bearers -> chaos/env.json (gitignored, 0600)
#   bash chaos/configure.sh --pairs 4    # more (passenger, driver) pairs
#
# ------------------------------------------------------------------------------------
# WHY THIS IS NOT load/configure.sh
# ------------------------------------------------------------------------------------
# C129's suite writes `load/env.json` with twelve pairs and a 750-vehicle cell map, and its
# accounts are the `+9477 003 xxxx` block. A chaos run needs three pairs and no cell map, and it
# must not be able to invalidate the capacity suite's fixture by running: `env.json` is one file
# per suite and each is written whole. So this is the same recipe at a different address —
# `+9477 004 xxxx`, plates `WP-CH-xxxx` — and the two can be provisioned in either order.
#
# ------------------------------------------------------------------------------------
# THE SAME THREE REFUSALS seed.sh AND load/configure.sh MAKE
# ------------------------------------------------------------------------------------
#   1. the compose project is `mageride-replica`;
#   2. `replica.synthetic_marker` exists — this database was created by seed.sql;
#   3. every account is in the +9477 004 xxxx block and every vehicle in the WP-CH series.
#
# A chaos suite is the last thing that should be pointed at a database whose contents it has not
# established are synthetic: half of what follows is `FLUSHALL`, `DROP DATABASE` and
# `docker stop postgres`.
#
# ------------------------------------------------------------------------------------
# THE BEARERS ARE OBTAINED THROUGH THE REAL ROUTES
# ------------------------------------------------------------------------------------
# RS256, signed by iam-svc's own key ring — there is no honest way to mint one outside the
# platform. Each account signs in as its app does: `POST /v1/auth/otp/request`, the code out of
# the dev SMS sender's log line, `POST /v1/auth/otp/verify`. See load/configure.sh's header for
# why that sender exists here and cannot exist in production.
# =====================================================================================
set -uo pipefail

cd "$(dirname -- "${BASH_SOURCE[0]}")" || exit 2
CHAOS_DIR="$PWD"
cd .. || exit 2
REPO_ROOT="$PWD"

COMPOSE="infra/replica/docker-compose.light-replica.yml"
PROJECT="mageride-replica"
ENV_FILE="$REPO_ROOT/infra/replica/.env.replica"
OUT="$CHAOS_DIR/env.json"

PAIRS=3

while [ $# -gt 0 ]; do
  case "$1" in
    --pairs) PAIRS="$2"; shift 2 ;;
    -h|--help) sed -n '2,12p' "$0"; exit 0 ;;
    *) echo "unknown argument: $1" >&2; exit 2 ;;
  esac
done

ok()   { printf '  \033[32m✓\033[0m %s\n' "$*"; }
warn() { printf '  \033[33m!\033[0m %s\n' "$*"; }
note() { printf '    %s\n' "$*"; }
step() { printf '\n\033[1m▸ %s\033[0m\n' "$*"; }
die()  { printf '  \033[31m✗\033[0m %s\n' "$*" >&2; exit 1; }

for tool in curl jq python3 openssl docker; do
  command -v "$tool" >/dev/null 2>&1 || die "$tool is required and is not on PATH"
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

# The same thing, with the SQL on STDIN.
#
# The provisioning block below goes through here rather than through `psql_q "…"` because that
# argument is a double-quoted bash string: a backtick pair inside it is command substitution, and
# a SQL comment that names `iam.users.emergency_contact_name` the way every other comment in this
# repository does becomes an attempt to RUN it. That is not a hypothetical — it happened twice
# while this script was being written, the second time silently truncating the transaction so the
# accounts were provisioned and their emergency contacts were not, and the symptom was a 400 from
# an endpoint three drills later. A quoted heredoc has no such layer.
psql_stdin() {
  docker compose -f "$COMPOSE" exec -T postgres \
    psql -U "$PG_USER_EFF" -d "$PG_DB_EFF" -v ON_ERROR_STOP=1 -qtAX 2>&1
}

api() {
  local method="$1" path="$2" body="${3:-}"
  local args=(-sS -k --max-time 30 -H "Host: ${HOSTHDR}" -H 'Content-Type: application/json'
              -H "Idempotency-Key: $(openssl rand -hex 16)")
  [ -n "$body" ] && args+=(-d "$body")
  curl "${args[@]}" -X "$method" "${EDGE}${path}" 2>/dev/null
}

# The gateway's `auth` policy is 30 requests a minute per caller address and on this deployment
# every caller has the same address (load/report.md's finding: `KnownProxies__0` is the literal
# string `haproxy`, which `IPAddress.TryParse` rejects, so X-Forwarded-For is ignored). Six pairs
# is 24 auth calls — under the ceiling, but a chaos run that follows a load run within the minute
# is not, so the backoff is here too.
api_paced() {
  local method="$1" path="$2" body="${3:-}" attempt=1 response

  while [ "$attempt" -le 5 ]; do
    response=$(api "$method" "$path" "$body")
    case "$response" in
      *'"rate-limited"'*|*'Rate limit exceeded'*)
        sleep "${CHAOS_AUTH_BACKOFF:-25}"; attempt=$((attempt + 1)) ;;
      *) printf '%s' "$response"; return 0 ;;
    esac
  done

  printf '%s' "$response"
  return 1
}

# -------------------------------------------------------------------------------------
step "1/4  is this the replica?"
# -------------------------------------------------------------------------------------
running=$(docker compose -f "$COMPOSE" ps --services --filter status=running 2>/dev/null | tr '\n' ' ')
case "$running" in
  *postgres*) ok "the $PROJECT project is up" ;;
  *) die "postgres is not running under the $PROJECT project. This script — and every drill that
      reads the file it writes — talks to that database and to nothing else." ;;
esac

marker=$(psql_q "SELECT count(*) FROM replica.synthetic_marker WHERE marker = 'mageride-replica-synthetic';" | tr -d ' \r')
[ "$marker" = "1" ] || die "replica.synthetic_marker is absent. This database was not created by
      infra/replica/seed.sql. The drills that follow FLUSHALL Redis, stop Postgres and DROP the
      database; none of them may be pointed at anything else."
ok "replica.synthetic_marker is present — synthetic data only"

jwks=$(api GET /v1/.well-known/jwks.json)
case "$jwks" in
  *'"keys"'*) ok "the edge answers and publishes the JWKS a bearer is validated against" ;;
  *) die "GET /v1/.well-known/jwks.json answered: ${jwks:0:160}" ;;
esac

# -------------------------------------------------------------------------------------
step "2/4  the chaos accounts"
# -------------------------------------------------------------------------------------
psql_stdin <<SQL >/dev/null || die "could not provision the chaos accounts"
BEGIN;
INSERT INTO iam.users (phone, role, first_name, language)
SELECT '+9477004' || lpad(n::text, 4, '0'), 'passenger', 'Chaos Passenger ' || n, 'en'
  FROM generate_series(1, ${PAIRS}) AS n
ON CONFLICT (phone) DO NOTHING;

INSERT INTO iam.users (phone, role, first_name, language)
SELECT '+9477004' || lpad((1000 + n)::text, 4, '0'), 'driver', 'Chaos Driver ' || n, 'en'
  FROM generate_series(1, ${PAIRS}) AS n
ON CONFLICT (phone) DO NOTHING;

-- APPROVED, for seed.sql's and load/configure.sh's reason: the AL-50 gate is a Verification
-- Officer's decision and no route on the platform makes this write for a fixture. A replica of
-- PENDING vehicles dispatches nothing at all, and a chaos drill against a dispatch plane that
-- was never going to dispatch proves nothing about a fault.
-- The plate is DERIVED from the phone rather than numbered by row_number over the drivers that
-- have no vehicle: a re-run with a larger --pairs restarts that numbering at 1 and collides on
-- ux_vehicles_regno_active.
INSERT INTO registry.vehicles
  (owner_id, registration_number, vehicle_type, mode, status, driver_name, onboarding_status)
SELECT u.id, 'WP-CH-' || right(u.phone, 4),
       'three_wheeler', 'C', 'APPROVED', u.first_name, 'approved'
  FROM iam.users u
 WHERE u.role = 'driver' AND u.phone LIKE '+94770041%'
   AND NOT EXISTS (SELECT 1 FROM registry.vehicles v WHERE v.owner_id = u.id);

-- D-08's daily-fee gate refuses a driver from their SECOND trip of the Colombo day with an empty
-- wallet. A chaos run books several rides per driver, and a drill that measured the wallet rule
-- while Redpanda was down would be measuring the wrong thing. Funded exactly as tests/E2E's
-- ModeCFleet funds it. The D-08 wallet drill empties one of these deliberately, and puts it back.

-- D-33's SOS is probed under every infrastructure fault, and without a contact on file safety-svc
-- answers 400 no-emergency-contact (AL-13) before doing any of the work whose latency the SLO is
-- about. One contact per chaos account, in the same synthetic block, so the drill measures the
-- dual-gateway dispatch rather than the guard in front of it.
--
-- BOTH halves are written, and the denormalised one is the one that matters. safety-svc reads
-- `iam.users.emergency_contact_name`/`_phone` and NEVER joins `iam.emergency_contacts` —
-- SosRepository's remarks quote iam-svc's own reason: "D-33 budgets five seconds for the whole SOS
-- fan-out". Seeding only the contacts table leaves the API answering 400 with six rows visible in
-- it, which is exactly what the first version of this script did.
INSERT INTO iam.emergency_contacts (user_id, name, phone)
SELECT u.id, 'Chaos Contact', '+94770049999'
  FROM iam.users u
 WHERE u.phone LIKE '+9477004%'
   AND NOT EXISTS (SELECT 1 FROM iam.emergency_contacts c WHERE c.user_id = u.id);

UPDATE iam.users
   SET emergency_contact_name = 'Chaos Contact', emergency_contact_phone = '+94770049999'
 WHERE phone LIKE '+9477004%' AND emergency_contact_phone IS NULL;

WITH account AS (
  INSERT INTO billing.accounts (owner_type, owner_id, currency, balance_minor)
  SELECT 'driver', u.id, 'LKR', 5000000 FROM iam.users u
   WHERE u.role = 'driver' AND u.phone LIKE '+94770041%'
      ON CONFLICT (owner_type, owner_id, currency) WHERE owner_id IS NOT NULL
      DO UPDATE SET balance_minor = EXCLUDED.balance_minor
  RETURNING id)
INSERT INTO billing.wallets (account_id, balance_minor)
SELECT id, 5000000 FROM account
    ON CONFLICT (account_id) DO UPDATE SET balance_minor = EXCLUDED.balance_minor;
COMMIT;
SQL

seeded=$(psql_q "SELECT count(*) FROM iam.users WHERE phone LIKE '+9477004%';" | tr -d ' \r')
vehicles=$(psql_q "SELECT count(*) FROM registry.vehicles WHERE registration_number LIKE 'WP-CH-%';" | tr -d ' \r')
ok "${seeded} chaos accounts, ${vehicles} APPROVED Mode C vehicles"

# -------------------------------------------------------------------------------------
step "3/4  bearers, through the routes the apps use"
# -------------------------------------------------------------------------------------
# Two passes — every OTP requested, THEN one read of the log, THEN every verify. The obvious
# per-account shape costs about a minute each on this box: `docker logs --since` has no index into
# the json-file driver's output and rescans the whole file, which is 96 GB-scale on this replica
# because its compose file sets no `logging:` options (load/report.md's finding). `--tail` seeks
# from the end and costs 70 ms.
APP_CONTAINER=$(docker compose -f "$COMPOSE" ps -q app-services 2>/dev/null | head -1)
[ -n "$APP_CONTAINER" ] || die "app-services is not running"

signed_in=""
requested=""

request_otp() {
  local phone="$1" role="$2" auth
  auth=$(api_paced POST /v1/auth/otp/request \
    "{\"phone\":\"${phone}\",\"deviceId\":\"c130-chaos-${phone#+}\",\"role\":\"${role}\"}" \
    | jq -r '.authId // empty')
  [ -n "$auth" ] || { warn "no authId for ${phone}"; return 1; }
  requested="${requested}${phone}	${role}	${auth}
"
}

for n in $(seq 1 "$PAIRS"); do
  request_otp "+9477004$(printf '%04d' "$n")" passenger || true
done
for n in $(seq 1 "$PAIRS"); do
  request_otp "+9477004$(printf '%04d' $((1000 + n)))" driver || true
done

asked=$(printf '%s' "$requested" | grep -c . || true)
note "${asked} OTPs requested; reading them out of the dev SMS sender's log"

codes=$(docker logs --tail "${CHAOS_LOG_TAIL:-4000}" "$APP_CONTAINER" 2>&1 \
  | grep -o "OTP for +[0-9]* (..) is [0-9]\{6\}" \
  | sed 's/OTP for \(+[0-9]*\) (..) is \([0-9]\{6\}\)/\1 \2/')

[ -n "$codes" ] || die "no '[dev-sms] OTP for …' lines in the app-services log — check
      Sms__Provider=dev and Sms__AllowDevSenderOutsideDevelopment=true in .env.replica."

while IFS=$'\t' read -r phone role auth; do
  [ -n "$phone" ] || continue

  # The LAST code for this phone: a re-run requests a second OTP and the first is no longer live.
  code=$(printf '%s\n' "$codes" | grep "^${phone} " | tail -1 | awk '{print $2}')
  [ -n "$code" ] || { warn "no OTP found for ${phone}"; continue; }

  token=$(api_paced POST /v1/auth/otp/verify \
    "{\"authId\":\"${auth}\",\"otp\":\"${code}\",\"deviceId\":\"c130-chaos-${phone#+}\"}")

  access=$(printf '%s' "$token" | jq -r '.accessToken // empty')
  # `.user.userId`, not `.user.id` — `UserProfile` in backend/contracts/iam.yaml names it userId.
  user_id=$(printf '%s' "$token" | jq -r '.user.userId // .user.id // empty')
  # The opaque single-use refresh token (D-29). Captured because it is the ONE credential on the
  # platform that lives in Redis as well as Postgres (`iam.sessions` + `refresh:{jti}`), which
  # makes it the probe that says what a `FLUSHALL` costs a signed-in user — a question ADD §14.1's
  # Redis row does not answer.
  refresh=$(printf '%s' "$token" | jq -r '.refreshToken // empty')

  [ -n "$access" ] || { warn "sign-in refused for ${phone}: $(printf '%s' "$token" | head -c 160)"; continue; }

  signed_in="${signed_in}${phone}	${role}	${user_id}	${access}	${refresh}
"
done <<EOF
$requested
EOF

count=$(printf '%s' "$signed_in" | grep -c . || true)
[ "$count" -gt 0 ] || die "not one account could sign in — no drill can establish a steady state"
ok "${count} bearers obtained (30-minute access tokens, D-29)"

DRIVER_VEHICLES=$(psql_q "
  SELECT u.phone || ' ' || v.id
    FROM iam.users u JOIN registry.vehicles v ON v.owner_id = u.id
   WHERE u.phone LIKE '+94770041%' AND v.registration_number LIKE 'WP-CH-%';" | tr -d '\r')

# -------------------------------------------------------------------------------------
step "4/4  chaos/env.json"
# -------------------------------------------------------------------------------------
# `signed_in` is an ARGUMENT, not stdin: `python3 - <<'PY'` already uses stdin for the program
# text, so a `printf | python3 - <<PY` pipeline is silently discarded (load/CLAUDE.md records the
# run where that produced "24 bearers obtained" followed by "passengers=0 drivers=0").
python3 - "$OUT" "$MQTT_URL" "$MQTT_JWT_SECRET" "$EDGE" "$HOSTHDR" "$DRIVER_VEHICLES" "$signed_in" <<'PY'
import json, sys

path, mqtt_url, secret, edge, host, driver_vehicles, signed_in = sys.argv[1:8]

vehicles = {}
for line in driver_vehicles.splitlines():
    parts = line.split()
    if len(parts) == 2:
        vehicles[parts[0]] = parts[1]

passengers, drivers = [], []
for line in signed_in.splitlines():
    parts = line.split('\t')
    if len(parts) != 5:
        continue
    phone, role, user_id, bearer, refresh = parts
    entry = {'phone': phone, 'id': user_id, 'bearer': bearer, 'refreshToken': refresh}
    if role == 'driver':
        entry['vehicleId'] = vehicles.get(phone)
        drivers.append(entry)
    else:
        passengers.append(entry)

json.dump({
    'mqttUrl': mqtt_url,
    'mqttSecret': secret,
    'edge': edge,
    'host': host,
    'passengers': passengers,
    'drivers': drivers,
}, open(path, 'w'), indent=2)

print(f"  passengers={len(passengers)} drivers={len(drivers)}")
PY

chmod 600 "$OUT"
ok "wrote chaos/env.json (gitignored, 0600 — it carries the broker secret and live bearers)"

echo
echo "  Ready. The access tokens live 30 minutes (D-29); re-run this when they lapse."
echo "    bash chaos/run-drills.sh --env replica --report chaos/out/report.md"
