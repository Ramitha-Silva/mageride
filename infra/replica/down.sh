#!/usr/bin/env bash
# =====================================================================================
# infra/replica/down.sh — stop the replica.
#
#   bash infra/replica/down.sh             # stop and remove the containers; volumes SURVIVE
#   bash infra/replica/down.sh --volumes   # ...and discard the data (asks first)
#
# NEVER `--remove-orphans`. It operates on the compose PROJECT, and while this project is
# `mageride-replica` and the dev stacks are `mageride`, the flag is one typo away from deleting the
# other stack's Postgres. infra/CLAUDE.md records that exact incident shape for the dev stacks.
# =====================================================================================
set -uo pipefail

cd "$(dirname -- "${BASH_SOURCE[0]}")" || exit 2
REPLICA_DIR="$PWD"
cd ../.. || exit 2

COMPOSE="infra/replica/docker-compose.light-replica.yml"

[ -f "$REPLICA_DIR/.env.replica" ] && { set -a; . "$REPLICA_DIR/.env.replica"; set +a; }

if [ "${1:-}" = "--volumes" ]; then
  printf 'This discards the replica database, the Redpanda log and the object store.\n'
  printf 'Type discard to continue: '
  read -r answer
  [ "$answer" = "discard" ] || { echo "aborted"; exit 1; }
  docker compose -f "$COMPOSE" --profile voip --profile portals down --volumes
  echo "✓ replica down, volumes discarded. The next deploy.sh starts from an empty database."
else
  docker compose -f "$COMPOSE" --profile voip --profile portals down
  echo "✓ replica down. Volumes kept — deploy.sh brings it back with its data."
fi
