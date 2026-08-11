#!/usr/bin/env bash
# =====================================================================================
# Bring up a MageRide dev stack (C009).
#
#   bash infra/scripts/dev-up.sh              # slim  — infra only, ~5.9 GB
#   bash infra/scripts/dev-up.sh full         # full  — D7' §3, adds the app containers
#   bash infra/scripts/dev-up.sh slim --build # rebuild the images first
#
# Both stacks share the compose project `mageride`, its `mageride_mr` network and its
# named volumes, so `full` is genuinely `slim` plus the application containers rather than
# a second copy of Postgres.
#
# Fence (root CLAUDE.md "Build Host"): this box also hosts the lightweight production
# replica, and ~17-20 GB of replica does not fit alongside a build. This script refuses to
# start if the replica project is running.
# =====================================================================================
set -Eeuo pipefail

REPO_ROOT="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/../.." && pwd)"
STACK="${1:-slim}"
shift || true

case "$STACK" in
  slim) COMPOSE_FILE="$REPO_ROOT/infra/docker-compose.dev.slim.yml" ;;
  full) COMPOSE_FILE="$REPO_ROOT/infra/docker-compose.dev.yml" ;;
  *)    echo "usage: dev-up.sh [slim|full] [extra docker compose up args]" >&2; exit 2 ;;
esac

RED=''; GREEN=''; YELLOW=''; RESET=''
if [[ -t 1 ]]; then RED=$'\033[31m'; GREEN=$'\033[32m'; YELLOW=$'\033[33m'; RESET=$'\033[0m'; fi

step() { printf '\n%s==> %s%s\n' "$YELLOW" "$1" "$RESET"; }
die()  { printf '%serror: %s%s\n' "$RED" "$1" "$RESET" >&2; exit 1; }

command -v docker >/dev/null 2>&1 || die "docker is not installed"
docker info >/dev/null 2>&1        || die "the docker daemon is not reachable"

# --- Replica fence --------------------------------------------------------------------
if docker compose ls --format json 2>/dev/null \
     | jq -e '.[]? | select(.Name | test("replica")) | select(.Status | test("running"))' \
     >/dev/null 2>&1; then
  die "the lightweight production replica is running. Bring it down first — this box
       cannot hold the replica and a dev stack at the same time (root CLAUDE.md)."
fi

# --- Dev TLS certificate for HAProxy ---------------------------------------------------
# Only the full stack runs HAProxy, but generating it here keeps the one place that
# creates local secrets in one script. infra/deploy/certs/ is gitignored.
CERT_DIR="$REPO_ROOT/infra/deploy/certs"
CERT_PEM="$CERT_DIR/mageride-dev.pem"
if [[ ! -f "$CERT_PEM" ]]; then
  step "generating a self-signed dev certificate (infra/deploy/certs/)"
  mkdir -p "$CERT_DIR"
  # SANs cover the vhosts haproxy.cfg routes on, so a browser and a KMP client both get
  # a name match and fail only on the (expected) unknown issuer.
  openssl req -x509 -newkey rsa:2048 -nodes -days 825 \
    -subj "/CN=localhost/O=MageRide Dev" \
    -addext "subjectAltName=DNS:localhost,DNS:admin.localhost,DNS:fleet.localhost,DNS:s3.localhost,IP:127.0.0.1" \
    -keyout "$CERT_DIR/mageride-dev.key" \
    -out    "$CERT_DIR/mageride-dev.crt" 2>/dev/null
  # HAProxy wants one PEM with the key and the certificate concatenated.
  cat "$CERT_DIR/mageride-dev.key" "$CERT_DIR/mageride-dev.crt" > "$CERT_PEM"
  chmod 600 "$CERT_PEM" "$CERT_DIR/mageride-dev.key"
  # Δ C125: readable by the haproxy container's user, which is uid 99 in haproxy:2.9-alpine — a
  # 600 root-owned pem is unreadable to it and the edge exits with "cannot open the file". Nothing
  # had ever noticed because the dev stack could not come up at all without an app-services image,
  # so its haproxy container had never started.
  chown 99:99 "$CERT_PEM" 2>/dev/null || true
fi

# The device CA (T-02). Shared with slim-verify.sh, which needs it for the same reason: EMQX will
# not boot without `certs/ca_chain.crt`, and the directory is gitignored so a fresh checkout has
# none. Idempotent.
bash "$REPO_ROOT/infra/scripts/ensure-device-ca.sh"

# --- Wave-2 guard ---------------------------------------------------------------------
# Only api-gateway (C008) has a Dockerfile today. Say which components are missing rather
# than letting docker fail with a bare "failed to read dockerfile".
if [[ "$STACK" == "full" ]]; then
  missing=()
  while IFS='|' read -r svc owner; do
    df=$(docker compose -f "$COMPOSE_FILE" config --format json \
           | jq -r --arg s "$svc" '.services[$s].build.dockerfile // empty')
    [[ -n "$df" && -f "$df" ]] || missing+=("$svc (lands with $owner)")
  done <<'EOF'
hot-path|C038 C039 C040 C044
app-services|C026-C066
fanout|C041
tcp-adapter|C043
EOF
  if (( ${#missing[@]} > 0 )); then
    printf '%sThe full stack is not buildable yet — no Dockerfile for:%s\n' "$RED" "$RESET" >&2
    printf '  %s\n' "${missing[@]}" >&2
    die "run \`dev-up.sh slim\` until those components land"
  fi
fi

step "starting the $STACK stack"
docker compose -f "$COMPOSE_FILE" up -d "$@"

step "waiting for containers"
bash "$REPO_ROOT/infra/scripts/wait-healthy.sh" "$COMPOSE_FILE"

step "endpoints"
cat <<EOF
  postgres          127.0.0.1:${PG_PORT:-5432}        (user ${PG_USER:-postgres} / db ${PG_DATABASE:-mageride})
  pgbouncer         127.0.0.1:${PGBOUNCER_PORT:-6432}        transaction mode
  redis             127.0.0.1:${REDIS_PORT:-6379}
  redpanda kafka    127.0.0.1:${REDPANDA_KAFKA_PORT:-19092}       (in-cluster: redpanda:9092)
  redpanda admin    127.0.0.1:${REDPANDA_ADMIN_PORT:-9644}
  emqx mqtt         127.0.0.1:${EMQX_MQTT_PORT:-1883}        mqtts ${EMQX_MQTTS_PORT:-8883} · wss ${EMQX_WSS_PORT:-8084} · dashboard ${EMQX_DASHBOARD_PORT:-18083}
  minio             127.0.0.1:${MINIO_API_PORT:-9000}        console ${MINIO_CONSOLE_PORT:-9001}
EOF
if [[ "$STACK" == "full" ]]; then
  cat <<EOF
  haproxy           https://127.0.0.1:${HAPROXY_HTTPS_PORT:-443}  (self-signed; admin./fleet./s3. vhosts)
  api-gateway       127.0.0.1:${GATEWAY_PORT:-5000}
  fanout (SignalR)  127.0.0.1:${FANOUT_PORT:-5001}
EOF
fi

printf '\n%sup.%s  Tear down with: bash infra/scripts/dev-down.sh %s\n' "$GREEN" "$RESET" "$STACK"
