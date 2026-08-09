#!/usr/bin/env bash
# =====================================================================================
# C119 — take the observability stack down.
#
#   bash infra/scripts/observability-down.sh              keep the metric history
#   bash infra/scripts/observability-down.sh --volumes    drop it
#
# **Run this BEFORE dev-down.sh.** Both stacks share the `mageride_mr` network, and compose
# will not remove a network that still has endpoints attached — so tearing the platform
# down first leaves an error and a dangling network.
#
# --- NEVER PASS --remove-orphans HERE --------------------------------------------------
# It was here for one commit and it **deleted the entire slim dev stack**: Postgres, Redis,
# Redpanda, EMQX, MinIO, PgBouncer and the network, in one command. `--remove-orphans`
# operates on the compose PROJECT, not on the file — and this stack deliberately shares the
# `mageride` project with the dev stacks so that it is more containers on one network
# rather than a second copy of anything. Every container the observability file does not
# declare is, to compose, an orphan of that project.
#
# The same applies to `dev-down.sh`, whose own `--remove-orphans` would remove the
# observability containers. That is why the order matters and why this script exists at all
# instead of a documented one-liner.
#
# `--volumes` discards Prometheus's TSDB, Loki's chunks, Tempo's blocks and Grafana's
# state. Everything that matters is provisioned from files, so nothing is lost except the
# history — which during an incident is the thing you want least to lose.
# =====================================================================================
set -Eeuo pipefail

REPO_ROOT="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/../.." && pwd)"
OBS="$REPO_ROOT/infra/observability/docker-compose.yml"

YELLOW=''; RESET=''
if [[ -t 1 ]]; then YELLOW=$'\033[33m'; RESET=$'\033[0m'; fi

if [[ "${1:-}" == "--volumes" ]]; then
  printf '%s==> stopping the observability stack and dropping its volumes%s\n' "$YELLOW" "$RESET"
  docker compose -f "$OBS" down --volumes
else
  printf '%s==> stopping the observability stack (volumes kept)%s\n' "$YELLOW" "$RESET"
  docker compose -f "$OBS" down
fi
