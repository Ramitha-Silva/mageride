#!/usr/bin/env bash
# =====================================================================================
# Seed the walking skeleton's driver and vehicle (C021).
#
#   bash infra/scripts/seed-skeleton.sh                      # slim dev stack on loopback
#   DATABASE_URL=postgres://… bash infra/scripts/seed-skeleton.sh
#
# Applies db/seed/skeleton.sql: one driver account with the `driver` role and one APPROVED
# Mode C three-wheeler, already selected as that driver's live publisher (US-9.6).
#
# NOT a migration and never run by the migrate container — the seed invents an account and
# approves a vehicle with no insurance document (AL-10 is bypassed; see the file header).
# Re-runnable: applying it twice changes nothing.
#
# The driver signs in through iam-svc with the ordinary OTP flow on +94770000001; the dev
# SMS sender (Sms:Provider=dev) writes the code to the iam-svc log.
# =====================================================================================
set -Eeuo pipefail

REPO_ROOT="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/../.." && pwd)"
SEED_FILE="$REPO_ROOT/db/seed/skeleton.sql"

# Matches the slim dev stack's published Postgres (infra/docker-compose.dev.slim.yml).
DATABASE_URL="${DATABASE_URL:-postgres://postgres:mageride_dev@127.0.0.1:5432/mageride}"

# The image the whole platform standardises on, and the one already cached on the build host.
PSQL_IMAGE="${PSQL_IMAGE:-timescale/timescaledb-ha:pg16}"

RED=''; GREEN=''; YELLOW=''; RESET=''
if [[ -t 1 ]]; then RED=$'\033[31m'; GREEN=$'\033[32m'; YELLOW=$'\033[33m'; RESET=$'\033[0m'; fi

die() { printf '%serror: %s%s\n' "$RED" "$1" "$RESET" >&2; exit 1; }

[[ -f "$SEED_FILE" ]] || die "missing $SEED_FILE"

# --- How to reach psql -----------------------------------------------------------------
# The build host has no postgresql-client, and installing one to run a seed would be a
# strange thing to require of a repo that already pulls this image for every migration.
# --network host so a loopback DATABASE_URL means the same thing inside the container.
if command -v psql >/dev/null 2>&1; then
  run_psql() { psql "$DATABASE_URL" --quiet --no-psqlrc --set ON_ERROR_STOP=1 "$@"; }
  SEED_ARG="$SEED_FILE"
elif command -v docker >/dev/null 2>&1 && docker info >/dev/null 2>&1; then
  printf '%snote:%s psql is not installed; running it from %s\n' "$YELLOW" "$RESET" "$PSQL_IMAGE" >&2
  run_psql() {
    docker run --rm --network host \
      --volume "$SEED_FILE:/seed/skeleton.sql:ro" \
      --entrypoint psql "$PSQL_IMAGE" \
      "$DATABASE_URL" --quiet --no-psqlrc --set ON_ERROR_STOP=1 "$@"
  }
  SEED_ARG="/seed/skeleton.sql"
else
  die "neither psql nor a reachable docker daemon is available"
fi

printf '\n%s==> seeding the walking skeleton%s\n' "$YELLOW" "$RESET"

# ON_ERROR_STOP so the trailing assertion block in the seed actually fails the script.
run_psql --file "$SEED_ARG" \
  || die "seed failed. Has \`bash infra/scripts/dev-up.sh\` run and the migrate job completed?"

run_psql --tuples-only --no-align --command "
  SELECT format('  driver  %s  %s', u.id, u.phone) FROM iam.users u
   WHERE u.id = '00000000-0000-4000-8000-00000000d001'
  UNION ALL
  SELECT format('  vehicle %s  %s  %s  %s', v.id, v.registration_number, v.vehicle_type, v.status)
    FROM registry.vehicles v WHERE v.id = '00000000-0000-4000-8000-00000000c001';"

printf '%s✓%s skeleton driver seeded with one selected, approved Mode C three-wheeler\n' "$GREEN" "$RESET"
