#!/usr/bin/env bash
# =====================================================================================
# Wait until every container in a MageRide dev compose stack is up (C009).
#
#   bash infra/scripts/wait-healthy.sh [compose-file] [timeout-seconds]
#
# Defaults to infra/docker-compose.dev.slim.yml and 300 s. This is the second half of
# C009's verify command and the thing every later component's verify should call after
# bringing the slim stack up.
#
# "Up" means, per service:
#   * has a healthcheck              -> Health must be `healthy`
#   * is a one-shot (restart: "no")  -> must have exited, with code 0
#   * anything else                  -> State must be `running`
#
# A service that has exited non-zero, or a healthcheck that has gone `unhealthy`, fails
# immediately rather than burning the whole timeout — a Postgres that died in initdb is
# not going to recover by waiting.
# =====================================================================================
set -Eeuo pipefail

REPO_ROOT="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/../.." && pwd)"
COMPOSE_FILE="${1:-$REPO_ROOT/infra/docker-compose.dev.slim.yml}"
TIMEOUT="${2:-300}"
INTERVAL=3

RED=''; GREEN=''; YELLOW=''; RESET=''
if [[ -t 1 ]]; then RED=$'\033[31m'; GREEN=$'\033[32m'; YELLOW=$'\033[33m'; RESET=$'\033[0m'; fi

die() { printf '%serror: %s%s\n' "$RED" "$1" "$RESET" >&2; exit 1; }

[[ -f "$COMPOSE_FILE" ]] || die "no such compose file: $COMPOSE_FILE"
command -v jq >/dev/null 2>&1 || die "jq is required"

dc() { docker compose -f "$COMPOSE_FILE" "$@"; }

# `docker compose ps --format json` emits NDJSON on some versions and a single JSON array
# on others. Normalise to one object per line so the rest of the script does not care.
ps_json() {
  dc ps --all --format json 2>/dev/null \
    | jq -c 'if type == "array" then .[] else . end' 2>/dev/null || true
}

# Services declared with `restart: "no"` are the one-shots (migrate, redpanda-init,
# minio-init). Read it out of the resolved config rather than hard-coding the names, so
# adding a one-shot to either compose file needs no edit here.
mapfile -t ONE_SHOTS < <(
  dc config --format json \
    | jq -r '.services | to_entries[] | select(.value.restart == "no") | .key'
)

is_one_shot() {
  local svc="$1" s
  for s in "${ONE_SHOTS[@]-}"; do [[ "$s" == "$svc" ]] && return 0; done
  return 1
}

# Δ C119: the services THIS file declares, so a row belonging to another file in the same
# compose project is skipped rather than judged.
#
# `docker compose ps` filters by project, not by file, and every stack in this repository
# declares `name: mageride` on purpose — the observability stack is more containers on one
# network, not a second copy of anything. So waiting on `infra/observability/…` used to see
# the slim stack's `migrate`, `minio-init` and `redpanda-init` sitting at `exited 0`, decide
# they were long-running services that had died, and fail. Filtering here rather than
# widening the one-shot rule keeps the contract right: this script waits for the containers
# the file it was given declares.
mapfile -t DECLARED < <(dc config --services)

is_declared() {
  local svc="$1" s
  for s in "${DECLARED[@]-}"; do [[ "$s" == "$svc" ]] && return 0; done
  return 1
}

printf '%s==> waiting for %s (timeout %ss)%s\n' \
  "$YELLOW" "$(basename "$COMPOSE_FILE")" "$TIMEOUT" "$RESET"

deadline=$(( SECONDS + TIMEOUT ))
last_report=""

while :; do
  pending=()
  failed=()
  ok=()

  while IFS= read -r row; do
    [[ -n "$row" ]] || continue
    svc=$(jq -r '.Service // ""'  <<<"$row")
    state=$(jq -r '.State // ""'  <<<"$row")
    health=$(jq -r '.Health // ""' <<<"$row")
    exit_code=$(jq -r '.ExitCode // 0' <<<"$row")
    [[ -n "$svc" ]] || continue
    is_declared "$svc" || continue

    if is_one_shot "$svc"; then
      case "$state" in
        exited) [[ "$exit_code" == "0" ]] && ok+=("$svc exited 0") \
                                          || failed+=("$svc exited $exit_code") ;;
        *)      pending+=("$svc ($state)") ;;
      esac
      continue
    fi

    if [[ "$state" != "running" ]]; then
      # A long-running service that exited is never coming back on its own.
      [[ "$state" == "exited" || "$state" == "dead" ]] \
        && failed+=("$svc $state (code $exit_code)") \
        || pending+=("$svc ($state)")
      continue
    fi

    case "$health" in
      healthy)            ok+=("$svc healthy") ;;
      unhealthy)          failed+=("$svc unhealthy") ;;
      starting)           pending+=("$svc (starting)") ;;
      ""|null|"unknown")  ok+=("$svc running (no healthcheck)") ;;
      *)                  pending+=("$svc ($health)") ;;
    esac
  done < <(ps_json)

  if (( ${#failed[@]} > 0 )); then
    printf '%sfailed:%s\n' "$RED" "$RESET"
    printf '  %s\n' "${failed[@]}"
    echo
    for f in "${failed[@]}"; do dc logs --tail 40 "${f%% *}" || true; done
    die "stack did not come up"
  fi

  if (( ${#pending[@]} == 0 )) && (( ${#ok[@]} > 0 )); then
    printf '%s\n' "${ok[@]}" | sed "s/^/  ${GREEN}ok${RESET}    /"
    printf '%s==> all %d containers up%s\n' "$GREEN" "${#ok[@]}" "$RESET"
    exit 0
  fi

  report="${pending[*]-}"
  if [[ "$report" != "$last_report" ]]; then
    printf '  waiting: %s\n' "${report:-(no containers yet)}"
    last_report="$report"
  fi

  (( SECONDS < deadline )) || {
    printf '%sstill pending after %ss:%s\n' "$RED" "$TIMEOUT" "$RESET"
    printf '  %s\n' "${pending[@]-}"
    dc ps --all
    die "timed out"
  }

  sleep "$INTERVAL"
done
