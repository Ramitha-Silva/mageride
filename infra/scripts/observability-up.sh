#!/usr/bin/env bash
# =====================================================================================
# C119 — bring the observability stack up and wait for it (ADD §13.1).
#
#   bash infra/scripts/observability-up.sh
#
# Same project and network as the dev stacks, so this adds containers to whatever is
# already running rather than starting a second copy of anything. It does NOT require the
# platform to be up: a scrape target that is absent is a target that is down, which is the
# correct reading and is what the `up` alerts fire on.
#
# ~1.6 GB of limits against the slim stack's ~5.9 GB. Both together fit beside a
# `dotnet build` on the 24 GB box; the full lightweight production replica does not
# (root CLAUDE.md, "Build Host").
# =====================================================================================
set -Eeuo pipefail

REPO_ROOT="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/../.." && pwd)"
OBS="$REPO_ROOT/infra/observability/docker-compose.yml"
TIMEOUT="${1:-180}"

YELLOW=''; GREEN=''; RED=''; RESET=''
if [[ -t 1 ]]; then YELLOW=$'\033[33m'; GREEN=$'\033[32m'; RED=$'\033[31m'; RESET=$'\033[0m'; fi

printf '%s==> starting the observability stack%s\n' "$YELLOW" "$RESET"
docker compose -f "$OBS" up -d

printf '%s==> waiting for health (up to %ss)%s\n' "$YELLOW" "$TIMEOUT" "$RESET"
deadline=$(( SECONDS + TIMEOUT ))
while (( SECONDS < deadline )); do
  pending=$(docker compose -f "$OBS" ps --format json 2>/dev/null \
    | jq -rs '[.[] | select(.Health != "" and .Health != "healthy") | .Service] | join(" ")' 2>/dev/null || echo "")
  [[ -z "$pending" ]] && break
  sleep 3
done

if [[ -n "${pending:-}" ]]; then
  printf '%serror: still unhealthy after %ss: %s%s\n' "$RED" "$TIMEOUT" "$pending" "$RESET" >&2
  docker compose -f "$OBS" ps
  exit 1
fi

# otel-collector and redis-exporter carry no healthcheck: both images are the binary on a
# base with no shell. Prometheus watching them from outside is the health signal, and it is
# a better test anyway — a collector that cannot be reached is down whatever it thinks.
printf '\n%s==> up%s\n' "$GREEN" "$RESET"
cat <<EOF
  Grafana       http://127.0.0.1:${GRAFANA_PORT:-3000}       (anonymous read-only; admin / \$GRAFANA_PASSWORD)
  Prometheus    http://127.0.0.1:${PROMETHEUS_PORT:-9090}
  Alertmanager  http://127.0.0.1:${ALERTMANAGER_PORT:-9093}
  Tempo         http://127.0.0.1:${TEMPO_PORT:-3200}
  Loki          http://127.0.0.1:${LOKI_PORT:-3100}
  OTLP          grpc 127.0.0.1:${OTLP_GRPC_PORT:-4317} · http 127.0.0.1:${OTLP_HTTP_PORT:-4318}

  Point a service at it with  Otel__Endpoint=http://otel-collector:4317
  Tear down with              bash infra/scripts/observability-down.sh
EOF
