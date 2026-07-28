#!/usr/bin/env bash
# =====================================================================================
# MageRide — the walking skeleton, end to end (C025).
#
#   bash e2e/walking-skeleton/run.sh              # up, migrate, seed, run, assert
#   KEEP_UP=1 bash e2e/walking-skeleton/run.sh    # leave the stack running afterwards
#   SKIP_BUILD=1 bash e2e/walking-skeleton/run.sh # reuse the images already built
#
# One command, from nothing, as this component's definition of done requires. It brings up
# `infra/docker-compose.skeleton.yml` — the slim infrastructure plus the eight services the
# skeleton needs behind the API gateway — waits for every container to be healthy, applies
# `db/seed/skeleton.sql`, and then runs `:e2e:walking-skeleton`, which drives one Mode C ride
# through the same KMP api-client, SignalR contract and MQTT topics the two Android apps use.
#
# What the run asserts is in Main.kt; the four headlines are:
#   * a booked ride reaches PaymentPending
#   * the driver's live position reaches the booking passenger's SignalR group
#   * an ignored offer expires at 15 s and the ride re-enters Matching
#   * the passenger joins exactly the 19 res-7 + ring(2) cells (R-06)
#
# The stack is torn down WITH ITS VOLUMES on the way out unless KEEP_UP=1, including on
# failure — but the container logs are dumped first, because a failed run's logs are the whole
# point of it.
# =====================================================================================
set -Eeuo pipefail

REPO_ROOT="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/../.." && pwd)"
COMPOSE_FILE="$REPO_ROOT/infra/docker-compose.skeleton.yml"
COMPOSE=(docker compose -f "$COMPOSE_FILE")

RED=''; GREEN=''; YELLOW=''; RESET=''
if [[ -t 1 ]]; then RED=$'\033[31m'; GREEN=$'\033[32m'; YELLOW=$'\033[33m'; RESET=$'\033[0m'; fi

say()  { printf '\n%s==> %s%s\n' "$YELLOW" "$1" "$RESET"; }
die()  { printf '%serror: %s%s\n' "$RED" "$1" "$RESET" >&2; exit 1; }

command -v docker >/dev/null 2>&1 || die "docker is not on PATH"
docker info >/dev/null 2>&1 || die "the docker daemon is not reachable"

# The services the skeleton needs. Named so a failure can dump exactly these rather than
# every container on the box.
SERVICES=(iam-svc registry-svc fare-svc ride-svc dispatch-svc mqtt-bridge position-processor fanout-svc api-gateway)

cleanup() {
  local status=$?

  if [[ $status -ne 0 ]]; then
    printf '\n%s--- container logs (the run failed) ---%s\n' "$RED" "$RESET" >&2
    "${COMPOSE[@]}" logs --tail 60 "${SERVICES[@]}" >&2 || true
  fi

  if [[ "${KEEP_UP:-0}" != "1" ]]; then
    # --volumes, so the next run starts from a genuinely clean database. That matters more than
    # it looks: R-02 allows one live ride per driver and the seeded driver is the same account
    # every run, while `POST /v1/rides/{rideId}/cancel` does not exist yet (C035) — so a run that
    # died mid-ride would otherwise poison every run after it with no way back through the API.
    say "tearing the stack down"
    "${COMPOSE[@]}" down --volumes --remove-orphans >/dev/null 2>&1 || true
  else
    printf '\n%snote:%s KEEP_UP=1 — the stack is still running. `%s down` when you are done.\n' \
      "$YELLOW" "$RESET" "docker compose -f $COMPOSE_FILE"
  fi

  exit $status
}
trap cleanup EXIT

# --- 1. the stack ----------------------------------------------------------------------
say "bringing up the walking-skeleton stack"
if [[ "${SKIP_BUILD:-0}" == "1" ]]; then
  "${COMPOSE[@]}" up -d
else
  "${COMPOSE[@]}" up -d --build
fi

say "waiting for every container to report healthy"
bash "$REPO_ROOT/infra/scripts/wait-healthy.sh" "$COMPOSE_FILE" "${WAIT_TIMEOUT:-420}"

# --- 2. the seeded driver ---------------------------------------------------------------
# One driver with one APPROVED, selected Mode C three-wheeler (C021). The passenger is NOT
# seeded — iam-svc creates that account on the first successful OTP verify, which is the
# flow a real passenger takes.
say "seeding the skeleton driver and vehicle"
bash "$REPO_ROOT/infra/scripts/seed-skeleton.sh"

# --- 3. the run -------------------------------------------------------------------------
# Every endpoint the harness needs. The defaults in Environment.kt describe this same stack,
# so these are here to be explicit rather than because they differ.
export MAGERIDE_GATEWAY="http://127.0.0.1:${GATEWAY_PORT:-5000}"
export MAGERIDE_MQTT_HOST="127.0.0.1"
export MAGERIDE_MQTT_PORT="${EMQX_MQTT_PORT:-1883}"
export MAGERIDE_KAFKA="127.0.0.1:${REDPANDA_KAFKA_PORT:-19092}"
export MAGERIDE_MQTT_SECRET="${MQTT_JWT_SECRET:-mageride-dev-mqtt-jwt-secret-change-me}"
export MAGERIDE_OTP_LOG_CMD="docker compose -f $COMPOSE_FILE logs --no-log-prefix --since 120s iam-svc"

say "running the end-to-end ride"
(cd "$REPO_ROOT" && ./gradlew --quiet --console=plain :e2e:walking-skeleton:run) \
  || die "the walking-skeleton run failed — see the assertion above and the logs below"

printf '\n%s✓%s walking skeleton green: one booked ride, end to end, on the real stack\n' "$GREEN" "$RESET"
