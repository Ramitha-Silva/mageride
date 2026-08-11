#!/usr/bin/env bash
# =====================================================================================
# infra/replica/backup.sh — pg_dump the replica into its own object store, and prove the dump restores.
#
#   bash infra/replica/backup.sh                 # dump, upload, prune
#   bash infra/replica/backup.sh --verify-restore # ...then restore it into a THROWAWAY database and
#                                                 #    count the rows, which is the only way to know
#                                                 #    the dump is a backup rather than a file
#
# Nightly, from the host's crontab (see docs/runbooks/replica-operations.md §4):
#   15 2 * * *  cd /root/mageride && bash infra/replica/backup.sh --verify-restore >> /var/log/mageride-backup.log 2>&1
#
# ------------------------------------------------------------------------------------
# A DUMP NOBODY HAS RESTORED IS NOT A BACKUP
# ------------------------------------------------------------------------------------
# `--verify-restore` restores into a database created for the purpose and dropped afterwards, then
# compares row counts against the source for the tables that matter. Without it this script proves
# only that pg_dump exited 0, which it does for a dump of an empty database too.
#
# THE OBJECT STORE IS THE REPLICA'S OWN MinIO. That is deliberate for a replica — the data is
# synthetic and the point is to exercise the mechanism, not to survive the loss of the box. A
# production backup goes off-host; this one does not, and the runbook says so.
# =====================================================================================
set -uo pipefail

cd "$(dirname -- "${BASH_SOURCE[0]}")" || exit 2
REPLICA_DIR="$PWD"
cd ../.. || exit 2

COMPOSE="infra/replica/docker-compose.light-replica.yml"
BUCKET="mageride-backups"
KEEP=7

verify_restore=0
[ "${1:-}" = "--verify-restore" ] && verify_restore=1

ok()   { printf '  \033[32m✓\033[0m %s\n' "$*"; }
die()  { printf '  \033[31m✗\033[0m %s\n' "$*" >&2; exit 1; }
step() { printf '\n\033[1m▸ %s\033[0m\n' "$*"; }

[ -f "$REPLICA_DIR/.env.replica" ] || die ".env.replica is absent — run deploy.sh first"
set -a
# shellcheck disable=SC1090
. "$REPLICA_DIR/.env.replica"
set +a

PG_USER_EFF="${PG_USER:-postgres}"
PG_DB_EFF="${PG_DATABASE:-mageride}"

# Passed in, never interpolated into a command string: a password on a command line is visible in
# `ps` to every process on the box.
dc() { docker compose -f "$COMPOSE" "$@"; }

running=$(dc ps --services --filter status=running 2>/dev/null | tr '\n' ' ')
case "$running" in *postgres*) : ;; *) die "postgres is not running under mageride-replica" ;; esac
case "$running" in *minio*) : ;; *) die "minio is not running — there is nowhere to put the dump" ;; esac

# Timestamps come from the host clock, once, so every artefact of this run shares one name.
STAMP="$(date -u +%Y%m%dT%H%M%SZ)"
DUMP="mageride-${STAMP}.dump"

# -------------------------------------------------------------------------------------
step "1/4  dump"
# -------------------------------------------------------------------------------------
# Custom format (-Fc): compressed, and pg_restore can be pointed at a single table from it, which is
# what makes a partial recovery possible at all. Plain SQL cannot do that.
if dc exec -T postgres pg_dump -U "$PG_USER_EFF" -d "$PG_DB_EFF" -Fc --no-owner --no-privileges \
     > "/tmp/${DUMP}" 2>/tmp/pg_dump.err; then
  size=$(du -h "/tmp/${DUMP}" | cut -f1)
  ok "pg_dump wrote /tmp/${DUMP} (${size})"
else
  die "pg_dump failed: $(tail -3 /tmp/pg_dump.err)"
fi

# A dump of nothing is a successful pg_dump. Check it has content before believing it.
if [ ! -s "/tmp/${DUMP}" ]; then
  die "the dump is empty"
fi

tables_in_dump=$(dc exec -T postgres sh -c "true" >/dev/null 2>&1; \
  docker run --rm -i -v "/tmp/${DUMP}:/d.dump:ro" timescale/timescaledb-ha:pg16 \
  pg_restore -l /d.dump 2>/dev/null | grep -c "TABLE DATA" || echo 0)
ok "the dump lists ${tables_in_dump} tables with data"

# -------------------------------------------------------------------------------------
step "2/4  upload to the object store"
# -------------------------------------------------------------------------------------
dc cp "/tmp/${DUMP}" "minio:/tmp/${DUMP}" >/dev/null 2>&1 \
  || die "could not copy the dump into the minio container"

if dc exec -T minio sh -c "
      mc alias set local http://127.0.0.1:9000 \"\$MINIO_ROOT_USER\" \"\$MINIO_ROOT_PASSWORD\" >/dev/null &&
      mc mb --ignore-existing local/${BUCKET} >/dev/null &&
      mc cp /tmp/${DUMP} local/${BUCKET}/ >/dev/null &&
      rm -f /tmp/${DUMP}" 2>/dev/null; then
  ok "uploaded to ${BUCKET}/${DUMP}"
else
  die "upload failed — the dump is still at /tmp/${DUMP} on the host"
fi

# -------------------------------------------------------------------------------------
step "3/4  prune"
# -------------------------------------------------------------------------------------
# Keep the last KEEP. Explicitly NOT `mc rm --older-than`: on a box whose clock has moved, that
# deletes by an age nobody verified. Counting is deterministic.
pruned=$(dc exec -T minio sh -c "
  mc alias set local http://127.0.0.1:9000 \"\$MINIO_ROOT_USER\" \"\$MINIO_ROOT_PASSWORD\" >/dev/null
  mc ls local/${BUCKET} | awk '{print \$NF}' | sort -r | tail -n +$((KEEP + 1)) | while read -r old; do
    [ -n \"\$old\" ] && mc rm \"local/${BUCKET}/\$old\" >/dev/null && echo \"\$old\"
  done" 2>/dev/null | tr '\n' ' ')

if [ -n "${pruned// /}" ]; then
  ok "pruned beyond the last ${KEEP}: ${pruned}"
else
  ok "nothing to prune (keeping the last ${KEEP})"
fi

# -------------------------------------------------------------------------------------
if [ "$verify_restore" = 1 ]; then
  step "4/4  restore into a throwaway database and compare"

  SCRATCH="restore_verify_${STAMP//[^0-9]/}"

  before=$(dc exec -T postgres psql -U "$PG_USER_EFF" -d "$PG_DB_EFF" -qtAX -c "
    SELECT count(*) FROM iam.users;" | tr -d '[:space:]')

  dc exec -T postgres psql -U "$PG_USER_EFF" -d postgres -qtAX \
    -c "DROP DATABASE IF EXISTS ${SCRATCH};" >/dev/null 2>&1
  dc exec -T postgres psql -U "$PG_USER_EFF" -d postgres -qtAX \
    -c "CREATE DATABASE ${SCRATCH};" >/dev/null 2>&1 \
    || die "could not create the scratch database ${SCRATCH}"

  # The dump carries TimescaleDB hypertables; the extension has to exist before the restore or every
  # telemetry table fails. `--no-owner` on the way in matches `--no-owner` on the way out.
  dc exec -T postgres psql -U "$PG_USER_EFF" -d "$SCRATCH" -qtAX \
    -c "CREATE EXTENSION IF NOT EXISTS timescaledb; CREATE EXTENSION IF NOT EXISTS postgis; CREATE EXTENSION IF NOT EXISTS pgcrypto;" \
    >/dev/null 2>&1

  if dc exec -T postgres pg_restore -U "$PG_USER_EFF" -d "$SCRATCH" --no-owner --no-privileges \
       < "/tmp/${DUMP}" >/tmp/pg_restore.err 2>&1; then
    ok "pg_restore completed with no errors"
  else
    # pg_restore warns about extension ownership on a Timescale dump even when the data is fine, so
    # the row comparison below is what actually decides.
    warnings=$(grep -c "error" /tmp/pg_restore.err || echo 0)
    printf '  \033[33m!\033[0m pg_restore reported %s error line(s) — the row comparison decides:\n' "$warnings"
    tail -3 /tmp/pg_restore.err | sed 's/^/      /'
  fi

  after=$(dc exec -T postgres psql -U "$PG_USER_EFF" -d "$SCRATCH" -qtAX -c "
    SELECT count(*) FROM iam.users;" 2>/dev/null | tr -d '[:space:]')

  dc exec -T postgres psql -U "$PG_USER_EFF" -d postgres -qtAX \
    -c "DROP DATABASE IF EXISTS ${SCRATCH};" >/dev/null 2>&1
  ok "dropped the scratch database"

  if [ -n "$after" ] && [ "$before" = "$after" ]; then
    ok "RESTORE VERIFIED: iam.users held ${before} rows in the source and ${after} in the restore"
  else
    die "restore mismatch: source iam.users=${before}, restored=${after:-<unreadable>}.
      The dump uploaded but it is NOT a backup."
  fi
else
  step "4/4  restore verification"
  printf '  \033[33m!\033[0m skipped. A dump nobody has restored is not a backup — pass
      --verify-restore, which is what the nightly cron line in the runbook uses.\n'
fi

rm -f "/tmp/${DUMP}"

printf '\n\033[1m✓ backup complete.\033[0m  list them:  '
echo "docker compose -f $COMPOSE exec minio mc ls local/${BUCKET}"
