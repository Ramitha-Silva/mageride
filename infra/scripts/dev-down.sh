#!/usr/bin/env bash
# =====================================================================================
# Bring down a MageRide dev stack (C009).
#
#   bash infra/scripts/dev-down.sh                # stop the slim stack, keep the data
#   bash infra/scripts/dev-down.sh full           # stop the full stack, keep the data
#   bash infra/scripts/dev-down.sh slim --volumes # ... and delete pgdata/redis/redpanda/emqx/minio
#
# Both compose files share one project, so `down` on either removes only the containers
# that file declares. `--volumes` is deliberately not the default: the named volumes hold
# the migrated database, and re-running the C003-C006 migrations from empty takes minutes.
#
# Note for EMQX: the broker persists whatever it learned into emqxdata. emqx.conf beats
# that persisted config on every start (its own precedence order), so a stale volume
# cannot silently override the JWT or ACL settings — but a topic retained on a volume can
# still be seen by the next stack. `--volumes` is the clean slate.
# =====================================================================================
set -Eeuo pipefail

REPO_ROOT="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/../.." && pwd)"
STACK="${1:-slim}"
shift || true

case "$STACK" in
  slim) COMPOSE_FILE="$REPO_ROOT/infra/docker-compose.dev.slim.yml" ;;
  full) COMPOSE_FILE="$REPO_ROOT/infra/docker-compose.dev.yml" ;;
  all)  COMPOSE_FILE="" ;;
  *)    echo "usage: dev-down.sh [slim|full|all] [--volumes] [extra docker compose down args]" >&2; exit 2 ;;
esac

YELLOW=''; GREEN=''; RESET=''
if [[ -t 1 ]]; then YELLOW=$'\033[33m'; GREEN=$'\033[32m'; RESET=$'\033[0m'; fi
step() { printf '\n%s==> %s%s\n' "$YELLOW" "$1" "$RESET"; }

# `all` covers the case where the full stack was started and then only the slim file is
# remembered: down both, so no container is orphaned holding a port or a volume.
if [[ "$STACK" == "all" ]]; then
  for f in "$REPO_ROOT/infra/docker-compose.dev.yml" "$REPO_ROOT/infra/docker-compose.dev.slim.yml"; do
    step "stopping $(basename "$f")"
    docker compose -f "$f" down --remove-orphans "$@"
  done
else
  step "stopping $(basename "$COMPOSE_FILE")"
  docker compose -f "$COMPOSE_FILE" down "$@"
fi

printf '\n%sdown.%s\n' "$GREEN" "$RESET"
docker compose ls --all 2>/dev/null | sed -n '1p;/mageride/p'
