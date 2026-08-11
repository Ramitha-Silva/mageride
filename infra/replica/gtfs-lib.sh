#!/usr/bin/env bash
# =====================================================================================
# infra/replica/gtfs-lib.sh — what gtfs-day0-load.sh and gtfs-day0-verify.sh share.
#
# Sourced, never executed. It carries the edge, the database, the operator credential and the
# run journal, so the two scripts cannot disagree about any of them — the load writes the journal
# the verify reads, and a helper that drifted between them would make a green verify meaningless.
#
# Every request goes through the EDGE, exactly as smoke.sh does and for the same reason: the
# Admin Portal talks to HAProxy on 443, and SCR-AP-016's five calls are the ones being rehearsed.
# Talking to app-services:5119 directly would skip TLS, the gateway's route table and the
# `/v1/admin/**` authorization path, which is most of what day-0 is proving.
# =====================================================================================

# shellcheck shell=bash

if [ -z "${BASH_VERSION:-}" ]; then
  echo "gtfs-lib.sh is bash, and it is sourced rather than run" >&2
  exit 2
fi

GTFS_LIB_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
REPLICA_DIR="$GTFS_LIB_DIR"
REPO_ROOT="$(cd -- "$GTFS_LIB_DIR/../.." && pwd)"

COMPOSE="infra/replica/docker-compose.light-replica.yml"
JOURNAL="${GTFS_JOURNAL:-$REPLICA_DIR/gtfs-day0-journal.json}"
CORRIDORS="${GTFS_CORRIDORS:-$REPLICA_DIR/gtfs-corridors.json}"
SHAPE_CHECK="$REPLICA_DIR/gtfs_shape.py"

cd "$REPO_ROOT" || exit 2

# -------------------------------------------------------------------------------------
# Output. The same four marks smoke.sh uses, so a day-0 transcript reads like a smoke one.
# -------------------------------------------------------------------------------------
pass=0
fail=0
skip=0

ok()    { pass=$((pass+1)); printf '  \033[32m✓\033[0m %s\n' "$*"; }
bad()   { fail=$((fail+1)); printf '  \033[31m✗\033[0m %s\n' "$*" >&2; }
warn()  { printf '  \033[33m!\033[0m %s\n' "$*"; }
skip_() { skip=$((skip+1)); printf '  \033[33m•\033[0m %s\n' "$*"; }
note()  { printf '    %s\n' "$*"; }
step()  { printf '\n\033[1m%s\033[0m\n' "$*"; }
die()   { printf '\n\033[31mstopped:\033[0m %s\n' "$*" >&2; exit "${2:-2}"; }

# -------------------------------------------------------------------------------------
# Prerequisites. Named individually because "command not found" three steps into an upload is a
# worse message than this one, and because a partially-run day-0 has to be reasoned about.
# -------------------------------------------------------------------------------------
for tool in curl jq python3 openssl docker; do
  command -v "$tool" >/dev/null 2>&1 || die "$tool is required and is not on PATH"
done

[ -f "$REPLICA_DIR/.env.replica" ] || die "no .env.replica — run infra/replica/deploy.sh first"

set -a
# shellcheck disable=SC1090,SC1091
. "$REPLICA_DIR/.env.replica"
set +a

EDGE_PORT="${HAPROXY_HTTPS_PORT:-443}"
EDGE="https://127.0.0.1:${EDGE_PORT}"
HOSTHDR="${REPLICA_HOSTNAME:-replica.mageride.lk}"

# -k because the certificate is self-signed BY DESIGN (smoke.sh's reasoning). The long ceiling is
# for the upload: a 200 MB multipart over loopback is quick, but validation of a national feed is
# not, and a curl that times out mid-upload leaves a version row nobody asked for.
CURL=(curl -sS -k --max-time "${GTFS_HTTP_TIMEOUT:-300}" -H "Host: ${HOSTHDR}")

# -------------------------------------------------------------------------------------
# The database. `PG_USER=mageride` is what deploy.sh initialises the cluster as — see its
# comment; `postgres` does not exist on this replica.
# -------------------------------------------------------------------------------------
psql_q() {
  docker compose -f "$COMPOSE" exec -T postgres \
    psql -U "${PG_USER:-postgres}" -d "${PG_DATABASE:-mageride}" -qtAX -c "$1" 2>&1
}

# `psql_v <sql> [name=value ...]` — the parameterised form. Values reach SQL as `:'name'`, quoted by
# psql, because a PHC verifier contains `$` and base64 contains `+/=` and neither survives
# string-building intact.
#
# `-f -` and not `-c`: psql performs NO variable interpolation in a `-c` string (its own
# documentation says so), and the failure is a bare `syntax error at or near ":"` that looks like a
# bug in the SQL. Read from stdin it interpolates, which is the whole reason to use variables here.
psql_v() {
  local sql="$1"; shift
  local args=()
  local kv
  for kv in "$@"; do args+=(-v "$kv"); done
  printf '%s\n' "$sql" | docker compose -f "$COMPOSE" exec -T postgres \
    psql -U "${PG_USER:-postgres}" -d "${PG_DATABASE:-mageride}" -qtAX "${args[@]}" -f - 2>&1
}

# A scalar, trimmed. psql -qtAX already gives one field per line; this drops the stray whitespace
# that `[ "$x" -eq 0 ]` chokes on.
pg_scalar() { psql_q "$1" | tr -d ' \r'; }

# -------------------------------------------------------------------------------------
# The API. `api <method> <path> [curl args…]` leaves the body in API_BODY and the status in
# API_STATUS. It prints NOTHING.
#
# Both are globals rather than stdout because a caller needs the status as often as the body — a 409
# `feed-duplicate` is a different outcome from a 202, not a different body — and `body=$(api …)` runs
# the function in a SUBSHELL, where an assignment to API_STATUS is discarded silently. The first
# version of this file did exactly that, and the branch that read the status got whatever the last
# non-substituted call had left there: a stale 401 that made a successful sign-in look like a
# refusal. Call it as a statement, then read the two variables.
# -------------------------------------------------------------------------------------
API_STATUS=""
API_BODY=""

api() {
  local method="$1" path="$2"; shift 2
  local body_and_code
  # An array, not an interpolated string: `${VAR:+-H "a: b"}` word-splits into four arguments with
  # literal quote characters in them, and curl then sends a header called `"Authorization:`.
  local auth=()

  [ -n "${GTFS_TOKEN:-}" ] && auth=(-H "Authorization: Bearer ${GTFS_TOKEN}")

  body_and_code=$("${CURL[@]}" -X "$method" -w $'\n%{http_code}' \
    "${auth[@]}" \
    "$@" "${EDGE}${path}" 2>/dev/null) || body_and_code=$'\n000'

  API_STATUS="${body_and_code##*$'\n'}"
  API_BODY="${body_and_code%$'\n'*}"
}

# The error code out of an RFC 7807 body: `type` is `https://mageride.lk/errors/{code}` (D3' §0),
# and the code is the last segment. Empty for a body that is not a problem document.
problem_code() { printf '%s' "$1" | jq -r '.type // "" | split("/") | last // ""' 2>/dev/null; }

# An Idempotency-Key the kernel accepts: 16–128 of [A-Za-z0-9_-] (GtfsAdminEndpoints
# .RequireIdempotencyKey). A fresh one per call, because the *upload* dedupes on the file's sha256
# and the *activation* is what the key protects — a retried activation must be the same key, so
# callers that retry pass theirs in.
idem_key() { openssl rand -hex 16; }

now_iso() { date -u +%Y-%m-%dT%H:%M:%SZ; }
now_epoch() { date +%s.%N; }
# Elapsed seconds to one decimal, for a runbook table rather than a benchmark.
elapsed_since() { python3 -c 'import sys; print(f"{float(sys.argv[2]) - float(sys.argv[1]):.1f}")' "$1" "$(now_epoch)"; }

# -------------------------------------------------------------------------------------
# The operator (AL-06: Admin or Super Admin, deny-by-default).
#
# The replica's seed has no internal account — every synthetic actor in seed.sql is a passenger, a
# driver or a fleet owner — so SCR-AP-016 has nobody who may open it. One is provisioned here
# rather than in seed.sql for one reason: a committed seed would carry a committed password hash
# for a known password, on a box with a public IP. The password is generated into `.env.replica`,
# which is gitignored and is where every other replica credential already lives.
#
# This is the same write a Super Admin makes through admin-bff when provisioning staff (AL-06);
# there is no self-service portal sign-up, by design.
# -------------------------------------------------------------------------------------
GTFS_ADMIN_EMAIL="${GTFS_ADMIN_EMAIL:-gtfs-day0@replica.invalid}"

ensure_operator() {
  local password hash user_id

  password="${GTFS_ADMIN_PASSWORD:-}"

  if [ -z "$password" ]; then
    # 33 raw bytes → 44 base64 characters, comfortably over AuthPolicyOptions.MinimumPasswordLength.
    password=$(openssl rand -base64 33 | tr -d '\n=' | tr '+/' '-_')
    {
      echo
      echo "# --- generated by gtfs-lib.sh: the SCR-AP-016 day-0 operator (AL-06, C126) ---"
      echo "GTFS_ADMIN_EMAIL=${GTFS_ADMIN_EMAIL}"
      echo "GTFS_ADMIN_PASSWORD=${password}"
    } >> "$REPLICA_DIR/.env.replica"
    note "generated a day-0 operator password into .env.replica (gitignored)"
  fi

  GTFS_ADMIN_PASSWORD="$password"

  # PBKDF2-HMAC-SHA256, 600 000 iterations, 128-bit salt, 256-bit output, PHC-encoded — exactly
  # Iam.Api/Auth/PasswordHasher, whose format comment is the specification this reproduces. The
  # cost is the point: it is also ~0.4 s here, which is why this runs once and not per sign-in.
  hash=$(python3 - "$password" <<'PY'
import base64, hashlib, os, sys

password = sys.argv[1].encode()
salt = os.urandom(16)
iterations = 600_000
digest = hashlib.pbkdf2_hmac("sha256", password, salt, iterations, 32)
print(f"$pbkdf2-sha256$i={iterations}${base64.b64encode(salt).decode()}${base64.b64encode(digest).decode()}")
PY
  ) || die "could not derive a password verifier"

  # ON CONFLICT on both tables, so re-running the day-0 script re-asserts the credential rather
  # than failing on the account it made last time. The role is re-asserted too: an account that
  # had been demoted would otherwise sign in and be refused by transit-svc with a 403 that looks
  # like a routing fault.
  user_id=$(psql_v "
    WITH upserted AS (
      INSERT INTO iam.users (email, role, first_name, language)
      VALUES (:'email', 'admin', 'GTFS Day-0 Operator (synthetic)', 'en')
      ON CONFLICT (email) DO UPDATE SET role = 'admin', updated_at = now()
      RETURNING id)
    INSERT INTO iam.user_credentials (user_id, password_hash, password_updated_at, failed_attempts, locked_until)
    SELECT id, :'hash', now(), 0, NULL FROM upserted
    ON CONFLICT (user_id) DO UPDATE
      SET password_hash = EXCLUDED.password_hash,
          password_updated_at = now(),
          failed_attempts = 0,       -- AL-37's counter: a re-provision must not inherit a lock-out
          locked_until = NULL
    RETURNING user_id;" \
    "email=${GTFS_ADMIN_EMAIL}" "hash=${hash}" | tr -d ' \r' | tail -1)

  case "$user_id" in
    ????????-????-????-????-????????????) GTFS_ADMIN_ID="$user_id" ;;
    *) die "could not provision the day-0 operator: ${user_id}" ;;
  esac
}

# A bearer for the Admin Portal surface, through the edge, via the route the screen uses
# (POST /v1/admin/auth/login, password arm — AL-07/AL-37, no MFA step).
#
# The Idempotency-Key is not optional: the C002 kernel demands one on EVERY POST mutation, sign-in
# included, and without it the answer is `400 idempotency-key-required` rather than a token. The
# Admin Portal sends one per submit for the same reason.
sign_in() {
  local token

  api POST /v1/admin/auth/login \
    -H 'Content-Type: application/json' \
    -H "Idempotency-Key: $(idem_key)" \
    --data-binary "$(jq -nc --arg e "$GTFS_ADMIN_EMAIL" --arg p "$GTFS_ADMIN_PASSWORD" \
      '{email: $e, password: $p}')"

  if [ "$API_STATUS" != "200" ]; then
    die "admin sign-in returned ${API_STATUS}: $(printf '%s' "$API_BODY" | head -c 400)"
  fi

  token=$(printf '%s' "$API_BODY" | jq -r '.accessToken // .access_token // ""')
  [ -n "$token" ] || die "sign-in succeeded and carried no accessToken: $(printf '%s' "$API_BODY" | head -c 200)"

  GTFS_TOKEN="$token"
}

# `require_token` — what every step that talks to `/v1/admin/**` calls first.
require_token() {
  [ -n "${GTFS_TOKEN:-}" ] && return 0
  ensure_operator
  sign_in
}

# -------------------------------------------------------------------------------------
# The journal — the day-0 run's evidence, and the only place a *timing* can live.
#
# The verify re-reads live state for everything that is still observable, but three of C126's
# definition-of-done items are not: the pre-first-import empty state stops existing the moment a
# feed is activated, and an activation's and a rollback's elapsed time are gone once they are
# done. Those are written here, at the moment they happen, by the script that caused them.
# -------------------------------------------------------------------------------------
journal_init() {
  [ -f "$JOURNAL" ] && return 0
  jq -n --arg at "$(now_iso)" '{schemaVersion: 1, startedAt: $at}' > "$JOURNAL"
}

# `journal_set <jq-path> <json>` — e.g. journal_set '.activation.current.elapsedSec' 12.4
journal_set() {
  local tmp
  tmp=$(mktemp)
  jq --argjson v "$2" "$1 = \$v" "$JOURNAL" > "$tmp" && mv "$tmp" "$JOURNAL"
}

# `journal_set_str <jq-path> <string>` — the same, for a value jq must not parse.
journal_set_str() {
  local tmp
  tmp=$(mktemp)
  jq --arg v "$2" "$1 = \$v" "$JOURNAL" > "$tmp" && mv "$tmp" "$JOURNAL"
}

journal_get() { jq -r "$1 // empty" "$JOURNAL" 2>/dev/null; }

# -------------------------------------------------------------------------------------
# Shared reads. Both scripts ask these, and both must ask them the same way.
# -------------------------------------------------------------------------------------

# The live row counts of the five mirrored tables (§18c) plus the version ledger.
live_counts_json() {
  psql_q "
    SELECT json_build_object(
      'routes',     (SELECT count(*) FROM transit.gtfs_routes),
      'trips',      (SELECT count(*) FROM transit.gtfs_trips),
      'stops',      (SELECT count(*) FROM transit.gtfs_stops),
      'stop_times', (SELECT count(*) FROM transit.gtfs_stop_times),
      'shapes',     (SELECT count(*) FROM transit.gtfs_shapes),
      'versions',   (SELECT count(*) FROM transit.gtfs_feed_versions),
      'active',     (SELECT count(*) FROM transit.gtfs_feed_versions WHERE status = 'active'))::text;" \
    | tr -d '\r' | tail -1
}

# `options_for <fromLat> <fromLng> <toLat> <toLng>` — the passenger-facing read (SCR-PA-009's
# item 3), which is what "serves direct routes" means. Leaves the answer in API_BODY, like `api`.
# Authenticated: /v1/transit is RequireAuthorization, and the day-0 operator's own bearer is an
# authenticated principal — the answer does not depend on which one asks.
options_for() {
  api GET "/v1/transit/options?fromLat=$1&fromLng=$2&toLat=$3&toLng=$4"
}

# The active version id, straight from the ledger. `ux_gtfs_feed_one_active` guarantees at most
# one row, so this is a scalar and not a list.
active_version_id() {
  pg_scalar "SELECT feed_version_id FROM transit.gtfs_feed_versions WHERE status = 'active';"
}

# The cache's own account of what it loaded, out of the container log:
#   "Loaded GTFS feed {FeedVersionId} ({FeedInfoVersion}) in {Elapsed}: … halts, … routes, …"
# This is the only evidence that names the version the *cache* is serving — the wire's
# `feedVersion` is `feed_info.version`, which two releases of one national feed may share.
cache_loaded_since() {
  local since="$1"
  docker compose -f "$COMPOSE" logs --since "$since" --no-log-prefix app-services 2>/dev/null \
    | grep -F 'Loaded GTFS feed' | tail -5
}
