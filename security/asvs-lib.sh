#!/usr/bin/env bash
# =====================================================================================
# security/asvs-lib.sh — what every C127 check file shares.
#
# Sourced, never executed. Carries the four marks, the counters, the repository root and the
# optional live targets, so a check file is nothing but its assertions.
#
# THE FOUR MARKS, AND WHAT EACH ONE MEANS FOR THE EXIT CODE
# ---------------------------------------------------------
#   ok    the control is in place and was observed             — exit unaffected
#   bad   the control is absent or was observed failing        — EXIT 1
#   warn  worth a reviewer's attention, not a finding          — exit unaffected
#   skip_ the target was not available to ask                  — exit unaffected, and SAID SO
#
# `skip_` is the one that needs arguing. Half of an ASVS review is a question you can only ask a
# running deployment — is /metrics published, does the app connect as a superuser — and a build
# agent has no replica. A check that could not run must not read as a check that passed, so every
# skip is counted, printed at the end, and named in the summary line. `--strict` turns every skip
# into a failure, which is what a release gate uses.
# =====================================================================================

# shellcheck shell=bash

if [ -z "${BASH_VERSION:-}" ]; then
  echo "asvs-lib.sh is bash, and it is sourced rather than run" >&2
  exit 2
fi

ASVS_LIB_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd -- "$ASVS_LIB_DIR/.." && pwd)"

cd "$REPO_ROOT" || exit 2

pass=0
fail=0
warned=0
skipped=0
declare -a FAILURES=()
declare -a SKIPS=()

if [ -t 1 ]; then
  C_OK=$'\033[32m'; C_BAD=$'\033[31m'; C_WARN=$'\033[33m'; C_BOLD=$'\033[1m'; C_OFF=$'\033[0m'
else
  C_OK=''; C_BAD=''; C_WARN=''; C_BOLD=''; C_OFF=''
fi

ok()    { pass=$((pass+1));       printf '  %s✓%s %s\n' "$C_OK" "$C_OFF" "$*"; }
bad()   { fail=$((fail+1));       FAILURES+=("$*"); printf '  %s✗%s %s\n' "$C_BAD" "$C_OFF" "$*" >&2; }
warn()  { warned=$((warned+1));   printf '  %s!%s %s\n' "$C_WARN" "$C_OFF" "$*"; }
skip_() { skipped=$((skipped+1)); SKIPS+=("$*"); printf '  %s•%s %s\n' "$C_WARN" "$C_OFF" "$*"; }
note()  { printf '    %s\n' "$*"; }
step()  { printf '\n%s%s%s\n' "$C_BOLD" "$*" "$C_OFF"; }

# -------------------------------------------------------------------------------------
# The live replica, if there is one. Both are optional and every check that needs one skips
# without it — the fence is "test against the replica, never against production data", so nothing
# here ever discovers a target by itself.
# -------------------------------------------------------------------------------------
REPLICA_ENV="$REPO_ROOT/infra/replica/.env.replica"
COMPOSE_FILE="infra/replica/docker-compose.light-replica.yml"

EDGE=""
HOSTHDR="${MAGERIDE_LIVE_HOST:-replica.mageride.lk}"

if [ -n "${MAGERIDE_LIVE_EDGE:-}" ]; then
  EDGE="$MAGERIDE_LIVE_EDGE"
elif [ -f "$REPLICA_ENV" ]; then
  # The same two variables gtfs-lib.sh reads, and read the same way, so the two scripts cannot
  # disagree about where the replica is.
  set -a
  # shellcheck disable=SC1090,SC1091
  . "$REPLICA_ENV"
  set +a
  EDGE="https://127.0.0.1:${HAPROXY_HTTPS_PORT:-443}"
  HOSTHDR="${REPLICA_HOSTNAME:-replica.mageride.lk}"
fi

# `-k`: the replica's edge certificate is self-signed by design (C125's smoke suite says the same).
# Trusting it on the build host would make the checks pass for a reason no phone enjoys.
CURL=(curl -sS -k --max-time "${ASVS_HTTP_TIMEOUT:-20}" -H "Host: ${HOSTHDR}")

edge_available() { [ -n "$EDGE" ]; }

# Sets ASVS_STATUS and ASVS_BODY. Never fails the script — a dead edge is a status of 000, which
# is a finding for the check to interpret rather than an error for bash to abort on.
probe() {
  local method="$1" path="$2"
  shift 2

  local response
  response=$("${CURL[@]}" -o /dev/stdout -w $'\n%{http_code}' -X "$method" "$@" "${EDGE}${path}" 2>/dev/null) \
    || response=$'\n000'

  ASVS_STATUS="${response##*$'\n'}"
  ASVS_BODY="${response%$'\n'*}"
}

# Whether the replica's Postgres container is up and answerable. Read-only queries only: the fence
# is the replica, and even there this script never writes.
replica_db_available() {
  [ -n "${PG_PASSWORD:-}" ] || return 1
  command -v docker >/dev/null 2>&1 || return 1
  docker inspect -f '{{.State.Running}}' mageride-replica-postgres-1 2>/dev/null | grep -qx true
}

psql_replica() {
  docker exec -e PGPASSWORD="$PG_PASSWORD" mageride-replica-postgres-1 \
    psql -U "${PG_USER:-mageride}" -d "${PG_DATABASE:-mageride}" -Atc "$1" 2>/dev/null
}
