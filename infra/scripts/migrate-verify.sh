#!/usr/bin/env bash
# =====================================================================================
# C003 verify — apply db/migrations to a throwaway PostgreSQL 16 + TimescaleDB + PostGIS
# container, twice, and assert the objects the definition of done names.
#
#   bash infra/scripts/migrate-verify.sh
#
# The container is removed on exit, including on failure. Nothing on the host is touched
# and the lightweight production replica is never started (root CLAUDE.md "Build Host").
#
# Env:
#   MIGRATE_VERIFY_IMAGE   Postgres image (default timescale/timescaledb-ha:pg16)
#   MIGRATE_VERIFY_KEEP=1  Leave the container running for inspection
# =====================================================================================
set -Eeuo pipefail

REPO_ROOT="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/../.." && pwd)"
IMAGE="${MIGRATE_VERIFY_IMAGE:-timescale/timescaledb-ha:pg16}"
CONTAINER="mageride-migrate-verify-$$"
PGPASSWORD_VALUE="verify"
PGDATABASE_VALUE="mageride_verify"
PROJECT="$REPO_ROOT/backend/src/MageRide.Migrations/MageRide.Migrations.csproj"

FAILURES=0
CHECKS=0

RED=''; GREEN=''; YELLOW=''; RESET=''
if [[ -t 1 ]]; then RED=$'\033[31m'; GREEN=$'\033[32m'; YELLOW=$'\033[33m'; RESET=$'\033[0m'; fi

step()  { printf '\n%s==> %s%s\n' "$YELLOW" "$1" "$RESET"; }
die()   { printf '%serror: %s%s\n' "$RED" "$1" "$RESET" >&2; exit 1; }

cleanup() {
  if [[ "${MIGRATE_VERIFY_KEEP:-}" == "1" ]]; then
    printf '\n%sContainer %s left running (MIGRATE_VERIFY_KEEP=1).%s\n' "$YELLOW" "$CONTAINER" "$RESET"
    return
  fi
  docker rm -f "$CONTAINER" >/dev/null 2>&1 || true
}
trap cleanup EXIT

psql_q() { # psql_q <sql> -> single value, whitespace trimmed
  docker exec -e PGPASSWORD="$PGPASSWORD_VALUE" "$CONTAINER" \
    psql -U postgres -d "$PGDATABASE_VALUE" -tAqc "$1" 2>/dev/null | tr -d '[:space:]'
}

psql_run() { # psql_run <sql> -> raw output, non-fatal
  docker exec -e PGPASSWORD="$PGPASSWORD_VALUE" "$CONTAINER" \
    psql -U postgres -d "$PGDATABASE_VALUE" -v ON_ERROR_STOP=1 -qc "$1" 2>&1
}

check_eq() { # check_eq <label> <expected> <sql>
  local label="$1" expected="$2" sql="$3" actual
  CHECKS=$((CHECKS + 1))
  actual="$(psql_q "$sql")"
  if [[ "$actual" == "$expected" ]]; then
    printf '  %s✓%s %s\n' "$GREEN" "$RESET" "$label"
  else
    printf '  %s✗%s %s (expected %s, got %s)\n' "$RED" "$RESET" "$label" "$expected" "${actual:-<empty>}"
    FAILURES=$((FAILURES + 1))
  fi
}

check_rejects() { # check_rejects <label> <sql that must fail>
  local label="$1" sql="$2"
  CHECKS=$((CHECKS + 1))
  if psql_run "$sql" >/dev/null 2>&1; then
    printf '  %s✗%s %s (the statement was accepted but should have been rejected)\n' "$RED" "$RESET" "$label"
    FAILURES=$((FAILURES + 1))
  else
    printf '  %s✓%s %s\n' "$GREEN" "$RESET" "$label"
  fi
}

# ---------------------------------------------------------------------------------------
command -v docker >/dev/null 2>&1 || die "docker is required to verify the migrations."
docker info >/dev/null 2>&1 || die "the docker daemon is not reachable."

step "Building the migration runner"
dotnet build "$PROJECT" -c Release --nologo -v quiet
MIGRATE_DLL="$REPO_ROOT/backend/src/MageRide.Migrations/bin/Release/net10.0/MageRide.Migrations.dll"
[[ -f "$MIGRATE_DLL" ]] || die "build produced no $MIGRATE_DLL"

step "Starting throwaway $IMAGE"
docker run -d --name "$CONTAINER" \
  -e POSTGRES_PASSWORD="$PGPASSWORD_VALUE" \
  -e POSTGRES_DB="$PGDATABASE_VALUE" \
  -p 127.0.0.1:0:5432 \
  "$IMAGE" >/dev/null

HOST_PORT="$(docker port "$CONTAINER" 5432/tcp | head -1 | sed 's/.*://')"
[[ -n "$HOST_PORT" ]] || die "could not determine the published port for $CONTAINER"
CONN="Host=127.0.0.1;Port=$HOST_PORT;Database=$PGDATABASE_VALUE;Username=postgres;Password=$PGPASSWORD_VALUE"
echo "    listening on 127.0.0.1:$HOST_PORT"

for _ in $(seq 1 60); do
  docker exec "$CONTAINER" pg_isready -U postgres -d "$PGDATABASE_VALUE" >/dev/null 2>&1 && break
  sleep 1
done
docker exec "$CONTAINER" pg_isready -U postgres -d "$PGDATABASE_VALUE" >/dev/null 2>&1 \
  || die "PostgreSQL did not become ready."

# ---------------------------------------------------------------------------------------
step "Pass 1 — apply to an empty database"
dotnet "$MIGRATE_DLL" --connection "$CONN" --wait 60 \
  || die "pass 1 failed: the migrations do not apply to an empty database."

SCRIPT_COUNT="$(find "$REPO_ROOT/db/migrations" -maxdepth 1 -name '*.sql' | wc -l | tr -d ' ')"
check_eq "all $SCRIPT_COUNT scripts are journalled" "$SCRIPT_COUNT" \
  "SELECT count(*) FROM public.schema_versions;"

step "Pass 2 — re-apply with the journal (must be a no-op)"
PASS2="$(dotnet "$MIGRATE_DLL" --connection "$CONN" --wait 10)" \
  || die "pass 2 failed: re-running the migrations errored."
echo "$PASS2"
CHECKS=$((CHECKS + 1))
if grep -qi "Applied 0 script" <<<"$PASS2"; then
  printf '  %s✓%s the journal suppressed every script on the second run\n' "$GREEN" "$RESET"
else
  printf '  %s✗%s expected the second run to apply 0 scripts\n' "$RED" "$RESET"
  FAILURES=$((FAILURES + 1))
fi

step "Pass 3 — re-apply ignoring the journal (proves the DDL itself is idempotent)"
dotnet "$MIGRATE_DLL" --connection "$CONN" --wait 10 --ignore-journal \
  || die "pass 3 failed: the scripts are not idempotent when re-executed."

# ---------------------------------------------------------------------------------------
step "Extensions and schemas"
check_eq "postgis, timescaledb, pgcrypto and citext installed" "4" \
  "SELECT count(*) FROM pg_extension WHERE extname IN ('postgis','timescaledb','pgcrypto','citext');"
check_eq "all 23 schemas created" "23" \
  "SELECT count(*) FROM information_schema.schemata WHERE schema_name IN (
     'iam','registry','prov','trips','rides','dispatch','reputation','safety','fares','billing',
     'comms','docs','support','content','audit','pdpa','spatial','telemetry','config',
     'subscription','transit','analytics','transit_staging');"

step "Tables owned by C003"
check_eq "9 iam tables" "9" \
  "SELECT count(*) FROM information_schema.tables WHERE table_schema='iam' AND table_type='BASE TABLE';"
check_eq "12 registry tables" "12" \
  "SELECT count(*) FROM information_schema.tables WHERE table_schema='registry' AND table_type='BASE TABLE';"
check_eq "2 prov tables" "2" \
  "SELECT count(*) FROM information_schema.tables WHERE table_schema='prov' AND table_type='BASE TABLE';"
check_eq "1 config table" "1" \
  "SELECT count(*) FROM information_schema.tables WHERE table_schema='config' AND table_type='BASE TABLE';"
check_eq "C004/C005/C006 schemas left empty" "0" \
  "SELECT count(*) FROM information_schema.tables
    WHERE table_schema IN ('trips','rides','dispatch','reputation','safety','fares','billing','comms',
                           'docs','support','content','audit','pdpa','spatial','telemetry',
                           'subscription','transit','analytics','transit_staging');"

step "AL-08 — single active device per app"
check_eq "ux_sessions_active_app is a unique partial index on (user_id, app)" "1" \
  "SELECT count(*) FROM pg_indexes
    WHERE schemaname='iam' AND indexname='ux_sessions_active_app'
      AND indexdef LIKE 'CREATE UNIQUE INDEX%(user_id, app)%WHERE (revoked_at IS NULL)%';"

step "AL-09 / D-37 — vehicle type and registration uniqueness"
check_eq "vehicle_type CHECK lists the 10 canonical types" "1" \
  "SELECT count(*) FROM pg_constraint
    WHERE conrelid='registry.vehicles'::regclass AND contype='c'
      AND pg_get_constraintdef(oid) LIKE '%motorbike%three_wheeler%flex%sedan%mini_van%van%truck%mini_truck%bus%train%';"
check_eq "vehicle_type CHECK has no 'car'" "0" \
  "SELECT count(*) FROM pg_constraint
    WHERE conrelid='registry.vehicles'::regclass AND contype='c'
      AND pg_get_constraintdef(oid) ~ '''car''';"
check_eq "ux_vehicles_regno_active is partial on PENDING/APPROVED" "1" \
  "SELECT count(*) FROM pg_indexes
    WHERE schemaname='registry' AND indexname='ux_vehicles_regno_active'
      AND indexdef LIKE 'CREATE UNIQUE INDEX%WHERE (status = ANY%';"

step "AL-50 — fleet-owned documents"
check_eq "registry.documents.driver_id is nullable" "YES" \
  "SELECT is_nullable FROM information_schema.columns
    WHERE table_schema='registry' AND table_name='documents' AND column_name='driver_id';"
check_eq "registry.documents.fleet_id exists" "1" \
  "SELECT count(*) FROM information_schema.columns
    WHERE table_schema='registry' AND table_name='documents' AND column_name='fleet_id';"
check_eq "ck_documents_owner requires exactly one owner" "1" \
  "SELECT count(*) FROM pg_constraint
    WHERE conrelid='registry.documents'::regclass AND conname='ck_documents_owner'
      AND pg_get_constraintdef(oid) LIKE '%num_nonnulls(driver_id, fleet_id) = 1%';"
# Postgres rewrites `kind IN (...)` to `= ANY (ARRAY[...])`, so match each value rather
# than a fixed ordering.
check_eq "documents.kind covers all five slots incl. revenue_license" "1" \
  "SELECT count(*) FROM pg_constraint
    WHERE conrelid='registry.documents'::regclass AND contype='c'
      AND pg_get_constraintdef(oid) LIKE '%driving_license%'
      AND pg_get_constraintdef(oid) LIKE '%registration%'
      AND pg_get_constraintdef(oid) LIKE '%insurance%'
      AND pg_get_constraintdef(oid) LIKE '%revenue_license%'
      AND pg_get_constraintdef(oid) LIKE '%permit%';"

step "AL-29 / AL-30 — per-field verification and the onboarding state machine"
check_eq "registry.document_fields verify_status domain" "1" \
  "SELECT count(*) FROM pg_constraint
    WHERE conrelid='registry.document_fields'::regclass AND contype='c'
      AND pg_get_constraintdef(oid) LIKE '%auto_verified%pending%confirmed%';"
check_eq "registry.document_fields source domain" "1" \
  "SELECT count(*) FROM pg_constraint
    WHERE conrelid='registry.document_fields'::regclass AND contype='c'
      AND pg_get_constraintdef(oid) LIKE '%''ai''%''manual''%';"
check_eq "registry.onboarding_steps step domain" "1" \
  "SELECT count(*) FROM pg_constraint
    WHERE conrelid='registry.onboarding_steps'::regclass AND contype='c'
      AND pg_get_constraintdef(oid) LIKE '%details%insurance%revenue%photos%';"
check_eq "registry.onboarding_steps status domain" "1" \
  "SELECT count(*) FROM pg_constraint
    WHERE conrelid='registry.onboarding_steps'::regclass AND contype='c'
      AND pg_get_constraintdef(oid) LIKE '%pending_input%verified%pending_review%';"
check_eq "registry.vehicles.onboarding_status exists" "1" \
  "SELECT count(*) FROM information_schema.columns
    WHERE table_schema='registry' AND table_name='vehicles' AND column_name='onboarding_status';"

step "T-08 / T-02 — tracker anti-clone and rotation"
check_eq "ux_tracker_imei_active is unique on ACTIVE only" "1" \
  "SELECT count(*) FROM pg_indexes
    WHERE schemaname='prov' AND indexname='ux_tracker_imei_active'
      AND indexdef LIKE 'CREATE UNIQUE INDEX%WHERE (state = ''ACTIVE''::text)%';"
check_eq "prov.tracker_bindings.rotates_at is NOT NULL" "NO" \
  "SELECT is_nullable FROM information_schema.columns
    WHERE table_schema='prov' AND table_name='tracker_bindings' AND column_name='rotates_at';"

step "D-38 — temporal columns"
check_eq "no 'timestamp without time zone' columns" "0" \
  "SELECT count(*) FROM information_schema.columns
    WHERE table_schema IN ('iam','registry','prov','config')
      AND data_type = 'timestamp without time zone';"
# C003 owns no business-date column yet; the guard is here so the first one that appears
# without its Asia/Colombo tz_at audit companion fails the verify (D-38).
check_eq "every DATE column has a tz_at companion" "0" \
  "SELECT count(*) FROM information_schema.columns c
    WHERE c.table_schema IN ('iam','registry','prov','config') AND c.data_type = 'date'
      AND NOT EXISTS (SELECT 1 FROM information_schema.columns t
                       WHERE t.table_schema = c.table_schema AND t.table_name = c.table_name
                         AND t.column_name LIKE '%tz_at');"

step "§0.2 — set_updated_at attached to every mutable table"
check_eq "no updated_at column is left without its trigger" "0" \
  "SELECT count(*) FROM information_schema.columns c
    WHERE c.table_schema IN ('iam','registry','prov','config') AND c.column_name = 'updated_at'
      AND NOT EXISTS (
        SELECT 1 FROM pg_trigger tg
         WHERE tg.tgrelid = format('%I.%I', c.table_schema, c.table_name)::regclass
           AND NOT tg.tgisinternal
           AND tg.tgfoid = 'public.set_updated_at'::regproc);"

step "Seed data (§20)"
check_eq "nine canonical roles seeded" "9" "SELECT count(*) FROM iam.roles;"
check_eq "six internal roles flagged" "6" "SELECT count(*) FROM iam.roles WHERE is_internal;"
check_eq "three launch cities seeded" "3" "SELECT count(*) FROM config.operating_cities;"
check_eq "Colombo is the first city and keeps its Sinhala label" "කොළඹ" \
  "SELECT name_si FROM config.operating_cities ORDER BY sort_order LIMIT 1;"

step "Constraints actually bite"
psql_run "INSERT INTO iam.users(id, phone, role) VALUES
            ('11111111-1111-1111-1111-111111111111','+94770000001','driver');
          INSERT INTO iam.devices(id, user_id, platform) VALUES
            ('22222222-2222-2222-2222-222222222222','11111111-1111-1111-1111-111111111111','android'),
            ('22222222-2222-2222-2222-222222222223','11111111-1111-1111-1111-111111111111','ios');
          INSERT INTO iam.sessions(user_id, device_id, app) VALUES
            ('11111111-1111-1111-1111-111111111111','22222222-2222-2222-2222-222222222222','driver');" >/dev/null \
  || die "could not seed the constraint fixtures."

check_rejects "a second live session for the same (user, app) is rejected (AL-08)" \
  "INSERT INTO iam.sessions(user_id, device_id, app) VALUES
     ('11111111-1111-1111-1111-111111111111','22222222-2222-2222-2222-222222222223','driver');"

CHECKS=$((CHECKS + 1))
if psql_run "INSERT INTO iam.sessions(user_id, device_id, app) VALUES
               ('11111111-1111-1111-1111-111111111111','22222222-2222-2222-2222-222222222223','passenger');" >/dev/null 2>&1; then
  printf '  %s✓%s the same user may hold a driver and a passenger session at once (AL-08)\n' "$GREEN" "$RESET"
else
  printf '  %s✗%s a passenger session alongside a driver session was rejected\n' "$RED" "$RESET"
  FAILURES=$((FAILURES + 1))
fi

check_rejects "vehicle_type 'car' is rejected (AL-09)" \
  "INSERT INTO registry.vehicles(owner_id, registration_number, vehicle_type, mode, driver_name)
     VALUES ('11111111-1111-1111-1111-111111111111','CAR-0001','car','C','Test');"

psql_run "INSERT INTO registry.vehicles(id, owner_id, registration_number, vehicle_type, mode, driver_name)
            VALUES ('33333333-3333-3333-3333-333333333333','11111111-1111-1111-1111-111111111111',
                    'WP-CAB-1234','three_wheeler','C','Test Driver');" >/dev/null \
  || die "could not insert the fixture vehicle."

check_rejects "a duplicate plate on a live vehicle is rejected (D-37)" \
  "INSERT INTO registry.vehicles(owner_id, registration_number, vehicle_type, mode, driver_name)
     VALUES ('11111111-1111-1111-1111-111111111111','WP-CAB-1234','sedan','C','Other Driver');"

CHECKS=$((CHECKS + 1))
if psql_run "UPDATE registry.vehicles SET status='REJECTED' WHERE id='33333333-3333-3333-3333-333333333333';
             INSERT INTO registry.vehicles(owner_id, registration_number, vehicle_type, mode, driver_name)
               VALUES ('11111111-1111-1111-1111-111111111111','WP-CAB-1234','sedan','C','Other Driver');" >/dev/null 2>&1; then
  printf '  %s✓%s a plate frees up once the old registration is REJECTED (D-37)\n' "$GREEN" "$RESET"
else
  printf '  %s✗%s a REJECTED registration still blocks its plate\n' "$RED" "$RESET"
  FAILURES=$((FAILURES + 1))
fi

check_rejects "a document with neither owner is rejected (AL-50)" \
  "INSERT INTO registry.documents(kind, file_url) VALUES ('insurance','s3://x');"
# Committed on its own: psql -c runs a multi-statement batch in one implicit transaction,
# so creating the fleet inside a statement that is expected to fail would roll it back too.
psql_run "INSERT INTO registry.fleets(id, owner_id, name)
            VALUES ('44444444-4444-4444-4444-444444444444','11111111-1111-1111-1111-111111111111','Test Fleet');" >/dev/null \
  || die "could not insert the fixture fleet."

check_rejects "a document with both owners is rejected (AL-50)" \
  "INSERT INTO registry.documents(driver_id, fleet_id, kind, file_url)
     VALUES ('11111111-1111-1111-1111-111111111111','44444444-4444-4444-4444-444444444444','insurance','s3://x');"

CHECKS=$((CHECKS + 1))
if psql_run "INSERT INTO registry.documents(fleet_id, kind, file_url)
               VALUES ('44444444-4444-4444-4444-444444444444','revenue_license','s3://y');" >/dev/null 2>&1; then
  printf '  %s✓%s a fleet may own a revenue_license document with no driver (AL-50)\n' "$GREEN" "$RESET"
else
  printf '  %s✗%s a fleet-owned revenue_license document was rejected\n' "$RED" "$RESET"
  FAILURES=$((FAILURES + 1))
fi

CHECKS=$((CHECKS + 1))
BEFORE="$(psql_q "SELECT updated_at FROM iam.users WHERE id='11111111-1111-1111-1111-111111111111';")"
sleep 1
psql_run "UPDATE iam.users SET first_name='Nimal' WHERE id='11111111-1111-1111-1111-111111111111';" >/dev/null
AFTER="$(psql_q "SELECT updated_at FROM iam.users WHERE id='11111111-1111-1111-1111-111111111111';")"
if [[ "$BEFORE" != "$AFTER" ]]; then
  printf '  %s✓%s set_updated_at stamps updated_at on UPDATE (§0.2)\n' "$GREEN" "$RESET"
else
  printf '  %s✗%s updated_at did not move on UPDATE\n' "$RED" "$RESET"
  FAILURES=$((FAILURES + 1))
fi

# ---------------------------------------------------------------------------------------
printf '\n'
if (( FAILURES == 0 )); then
  printf '%s%d/%d checks passed — migrations apply cleanly, twice.%s\n' "$GREEN" "$CHECKS" "$CHECKS" "$RESET"
  exit 0
fi

printf '%s%d of %d checks failed.%s\n' "$RED" "$FAILURES" "$CHECKS" "$RESET"
exit 1
