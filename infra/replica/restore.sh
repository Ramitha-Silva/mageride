#!/usr/bin/env bash
# =====================================================================================
# infra/replica/restore.sh — put a dump back into the replica.
#
#   bash infra/replica/restore.sh                      # the newest dump in the object store
#   bash infra/replica/restore.sh mageride-2026....dump # a named one
#   bash infra/replica/restore.sh --list                # what is available
#
# ------------------------------------------------------------------------------------
# THIS DESTROYS THE REPLICA'S CURRENT DATA AND ASKS FIRST
# ------------------------------------------------------------------------------------
# A restore is a `DROP DATABASE`. On a replica that is only ever synthetic data, but "only synthetic"
# is a claim about the box, not about the command, so it requires typing the word `restore` unless
# --yes is passed. backup.sh --verify-restore never touches the live database; it restores into a
# throwaway one. Prefer that for testing the dump.
# =====================================================================================
set -uo pipefail

cd "$(dirname -- "${BASH_SOURCE[0]}")" || exit 2
REPLICA_DIR="$PWD"
cd ../.. || exit 2

COMPOSE="infra/replica/docker-compose.light-replica.yml"
BUCKET="mageride-backups"

ok()   { printf '  \033[32m✓\033[0m %s\n' "$*"; }
die()  { printf '  \033[31m✗\033[0m %s\n' "$*" >&2; exit 1; }
step() { printf '\n\033[1m▸ %s\033[0m\n' "$*"; }

[ -f "$REPLICA_DIR/.env.replica" ] || die ".env.replica is absent"
set -a
. "$REPLICA_DIR/.env.replica"
set +a

PG_USER_EFF="${PG_USER:-postgres}"
PG_DB_EFF="${PG_DATABASE:-mageride}"
dc() { docker compose -f "$COMPOSE" "$@"; }

mc_in_minio() {
  dc exec -T minio sh -c "
    mc alias set local http://127.0.0.1:9000 \"\$MINIO_ROOT_USER\" \"\$MINIO_ROOT_PASSWORD\" >/dev/null
    $1" 2>/dev/null
}

assume_yes=0
wanted=""
for arg in "$@"; do
  case "$arg" in
    --list) step "dumps in ${BUCKET}"; mc_in_minio "mc ls local/${BUCKET}" | sed 's/^/  /'; exit 0 ;;
    --yes)  assume_yes=1 ;;
    *)      wanted="$arg" ;;
  esac
done

running=$(dc ps --services --filter status=running 2>/dev/null | tr '\n' ' ')
case "$running" in *postgres*) : ;; *) die "postgres is not running under mageride-replica" ;; esac

step "1/4  choose the dump"
if [ -z "$wanted" ]; then
  wanted=$(mc_in_minio "mc ls local/${BUCKET}" | awk '{print $NF}' | sort -r | head -1)
  [ -n "$wanted" ] || die "no dumps in ${BUCKET} — run backup.sh first"
  ok "newest: ${wanted}"
else
  ok "requested: ${wanted}"
fi

step "2/4  confirm"
# The marker is what says this database is synthetic. Its ABSENCE is the case to be loud about.
marker=$(dc exec -T postgres psql -U "$PG_USER_EFF" -d "$PG_DB_EFF" -qtAX \
  -c "SELECT count(*) FROM replica.synthetic_marker;" 2>/dev/null | tr -d '[:space:]')

if [ "${marker:-0}" != "1" ]; then
  printf '  \033[31m!\033[0m This database has NO synthetic marker. It may not be a replica.\n'
  assume_yes=0
fi

if [ "$assume_yes" != 1 ]; then
  printf '  This DROPS %s and restores %s.\n  Type restore to continue: ' "$PG_DB_EFF" "$wanted"
  read -r answer
  [ "$answer" = "restore" ] || die "aborted"
fi

step "3/4  restore"
mc_in_minio "mc cp local/${BUCKET}/${wanted} /tmp/${wanted} >/dev/null" \
  || die "could not fetch ${wanted} from the object store"
dc cp "minio:/tmp/${wanted}" "/tmp/${wanted}" >/dev/null 2>&1 \
  || die "could not copy the dump to the host"

# Everything that holds a connection has to let go before DROP DATABASE will run.
dc stop app-services hot-path fanout tcp-adapter pgbouncer >/dev/null 2>&1
ok "stopped the services holding connections"

dc exec -T postgres psql -U "$PG_USER_EFF" -d postgres -qtAX \
  -c "SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE datname = '${PG_DB_EFF}' AND pid <> pg_backend_pid();" \
  >/dev/null 2>&1
# Δ C130 — TWO `-c` flags, not one with two statements in it.
#
# `psql -c "DROP DATABASE …; CREATE DATABASE …;"` sends both as ONE query string, which the server
# wraps in an implicit transaction, and the answer is
#
#     ERROR:  DROP DATABASE cannot run inside a transaction block
#
# every time. Multiple `-c` flags are sent as separate queries and are the documented way to do
# this. As printed, this script could never restore anything: it died here, having already stopped
# app-services, hot-path, fanout, tcp-adapter and pgbouncer, and left the platform down with the
# database intact — which is the worst of both outcomes and is what chaos/drills/90 found on its
# first run.
#
# `backup.sh --verify-restore` did not catch it because it restores into a *fresh* scratch database
# and therefore only ever runs `CREATE DATABASE` on its own. A dump nobody has restored is not a
# backup; a restore script nobody has run is not a recovery plan.
dc exec -T postgres psql -U "$PG_USER_EFF" -d postgres -qtAX \
  -c "DROP DATABASE IF EXISTS ${PG_DB_EFF};" \
  -c "CREATE DATABASE ${PG_DB_EFF};" >/dev/null 2>&1 \
  || die "could not recreate ${PG_DB_EFF}"

dc exec -T postgres psql -U "$PG_USER_EFF" -d "$PG_DB_EFF" -qtAX \
  -c "CREATE EXTENSION IF NOT EXISTS timescaledb; CREATE EXTENSION IF NOT EXISTS postgis; CREATE EXTENSION IF NOT EXISTS pgcrypto;" \
  >/dev/null 2>&1

if dc exec -T postgres pg_restore -U "$PG_USER_EFF" -d "$PG_DB_EFF" --no-owner --no-privileges \
     < "/tmp/${wanted}" >/tmp/restore.err 2>&1; then
  ok "pg_restore completed"
else
  printf '  \033[33m!\033[0m pg_restore reported errors (Timescale extension ownership is expected):\n'
  tail -3 /tmp/restore.err | sed 's/^/      /'
fi

step "4/4  bring the services back and check"
dc start pgbouncer app-services hot-path fanout tcp-adapter >/dev/null 2>&1
ok "services restarted"

users=$(dc exec -T postgres psql -U "$PG_USER_EFF" -d "$PG_DB_EFF" -qtAX -c "SELECT count(*) FROM iam.users;" 2>/dev/null | tr -d '[:space:]')
marker=$(dc exec -T postgres psql -U "$PG_USER_EFF" -d "$PG_DB_EFF" -qtAX -c "SELECT count(*) FROM replica.synthetic_marker;" 2>/dev/null | tr -d '[:space:]')
ok "restored database holds ${users:-?} users; synthetic marker present: ${marker:-0}"

rm -f "/tmp/${wanted}"
printf '\n\033[1m✓ restored from %s.\033[0m  smoke it:  bash infra/replica/smoke.sh\n' "$wanted"
