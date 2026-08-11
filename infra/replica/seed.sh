#!/usr/bin/env bash
# =====================================================================================
# infra/replica/seed.sh — put synthetic data in the replica, and refuse to do it anywhere else.
#
#   bash infra/replica/seed.sh            # assert the reference data, then seed the actors
#   bash infra/replica/seed.sh --verify   # assert only; change nothing
#
# ------------------------------------------------------------------------------------
# THE REFUSAL IS THE FEATURE
# ------------------------------------------------------------------------------------
# The one thing this script must never do is write into a database holding real riders' phone numbers.
# It checks three things before it writes, and any of them failing stops it:
#
#   1. The compose project is `mageride-replica`. Not the dev project, not a bare Postgres.
#   2. Either replica.synthetic_marker exists (we seeded this database before) OR the tables this
#      script writes to are EMPTY of anything but migration-seeded reference rows. A database with
#      unexplained users in it is not one to add synthetic drivers to.
#   3. The reference data the migrations own is present. Absent tariffs means the migrations did not
#      run, which means this is not the database anybody thinks it is.
# =====================================================================================
set -uo pipefail

cd "$(dirname -- "${BASH_SOURCE[0]}")" || exit 2
REPLICA_DIR="$PWD"
cd ../.. || exit 2

COMPOSE="infra/replica/docker-compose.light-replica.yml"
PROJECT="mageride-replica"

verify_only=0
[ "${1:-}" = "--verify" ] && verify_only=1

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

# Every statement goes through `docker compose exec postgres psql`, never a published port: the
# replica publishes no database port at all, which is the point.
psql_q() {
  docker compose -f "$COMPOSE" exec -T postgres \
    psql -U "$PG_USER_EFF" -d "$PG_DB_EFF" -v ON_ERROR_STOP=1 -qtAX -c "$1" 2>&1
}

# -------------------------------------------------------------------------------------
step "1/4  is this the replica?"
# -------------------------------------------------------------------------------------
running=$(docker compose -f "$COMPOSE" ps --services --filter status=running 2>/dev/null | tr '\n' ' ')
case "$running" in
  *postgres*) ok "the $PROJECT project is up and postgres is running" ;;
  *) die "postgres is not running under the $PROJECT project. This script talks to that container
      and nothing else — it has no way to reach, and must never reach, another database." ;;
esac

# -------------------------------------------------------------------------------------
step "2/4  is it safe to write here?"
# -------------------------------------------------------------------------------------
marker=$(psql_q "SELECT count(*) FROM replica.synthetic_marker WHERE marker = '${REPLICA_SYNTHETIC_MARKER:-mageride-replica-synthetic}';" || echo "0")

if [ "${marker//[^0-9]/}" = "1" ]; then
  ok "the synthetic marker is present — this database was seeded by this script before"
else
  # No marker. Then every table this script writes to has to be empty, or we are looking at a
  # database somebody else filled.
  users=$(psql_q "SELECT count(*) FROM iam.users;")
  vehicles=$(psql_q "SELECT count(*) FROM registry.vehicles;")

  case "$users$vehicles" in
    *ERROR*|*error*|"") die "could not read iam.users / registry.vehicles — have the migrations run?
      $users $vehicles" ;;
  esac

  if [ "${users//[^0-9]/}" != "0" ] || [ "${vehicles//[^0-9]/}" != "0" ]; then
    die "no synthetic marker, but this database already holds ${users} user(s) and ${vehicles}
      vehicle(s). REFUSING TO WRITE. If this really is a throwaway replica, the marker is created by
      seed.sql on a clean database — bring the stack down with --volumes and deploy again."
  fi

  ok "no marker and no pre-existing users or vehicles — a clean database, safe to seed"
fi

# -------------------------------------------------------------------------------------
step "3/4  the reference data the MIGRATIONS own"
# -------------------------------------------------------------------------------------
# Asserted, never inserted. 0201 and 1901-1906 own these; a seeder that re-inserted them would
# double the tariff table, and a replica that prices rides twice is worse than one that cannot.
reference_missing=0
check_ref() {
  local table="$1" why="$2" count
  count=$(psql_q "SELECT count(*) FROM ${table};")
  case "$count" in
    *ERROR*|*error*|"") printf '  \033[31m✗\033[0m %s does not exist — the migrations did not run\n' "$table" >&2
                        reference_missing=1; return ;;
  esac
  if [ "${count//[^0-9]/}" = "0" ]; then
    printf '  \033[31m✗\033[0m %s is EMPTY — %s\n' "$table" "$why" >&2
    reference_missing=1
  else
    ok "${table}: ${count} row(s)"
  fi
}

check_ref "config.operating_cities"        "no city means no dispatch anywhere (0201)"
check_ref "fares.tariffs"                  "every ride would price at nothing (1901)"
check_ref "fares.peak_windows"             "the surcharge windows are gone (1901)"
check_ref "billing.plans"                  "the daily-fee tiers are gone (1901, D-13)"
check_ref "billing.voucher_discount_tiers" "bulk vouchers have no discount table (1901, AL-01)"
check_ref "content.faq_articles"           "the in-app FAQ is empty (1902)"

[ "$reference_missing" = 0 ] || die "reference data is missing — this is not a migrated database"

if [ "$verify_only" = 1 ]; then
  printf '\n\033[1m✓ verify only — nothing was written.\033[0m\n'
  exit 0
fi

# -------------------------------------------------------------------------------------
step "4/4  seeding the actors"
# -------------------------------------------------------------------------------------
# seed.sql is idempotent (ON CONFLICT DO NOTHING and NOT EXISTS throughout), so a second run is a
# no-op rather than a doubled fleet.
if docker compose -f "$COMPOSE" exec -T postgres \
     psql -U "$PG_USER_EFF" -d "$PG_DB_EFF" -v ON_ERROR_STOP=1 -q < "$REPLICA_DIR/seed.sql"; then
  ok "seed.sql applied"
else
  die "seed.sql failed — see the error above. Nothing was committed: the file is one transaction."
fi

step "what is in there now"
psql_q "
SELECT 'passengers', count(*)::text FROM iam.users WHERE role = 'passenger'
UNION ALL SELECT 'drivers',    count(*)::text FROM iam.users WHERE role = 'driver'
UNION ALL SELECT 'fleet owners', count(*)::text FROM iam.users WHERE role = 'fleet_owner'
UNION ALL SELECT 'vehicles (APPROVED)', count(*)::text FROM registry.vehicles WHERE status = 'APPROVED'
UNION ALL SELECT 'vehicle types', count(DISTINCT vehicle_type)::text FROM registry.vehicles
UNION ALL SELECT 'trip sessions', count(*)::text FROM trips.sessions
ORDER BY 1;" | sed 's/|/: /' | sed 's/^/  /'

printf '\n\033[1m✓ seeded.\033[0m  golden paths:  bash infra/replica/smoke.sh\n'
