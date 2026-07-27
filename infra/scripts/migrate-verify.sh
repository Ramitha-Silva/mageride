#!/usr/bin/env bash
# =====================================================================================
# C003 / C004 verify — apply db/migrations to a throwaway PostgreSQL 16 + TimescaleDB +
# PostGIS container, twice, and assert the objects each definition of done names.
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

step "Tables owned by C004"
# relispartition filters out the trips.position_samples monthly partitions, which are tables
# in their own right but not schema objects anyone declared.
check_eq "4 trips tables" "4" \
  "SELECT count(*) FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
    WHERE n.nspname='trips' AND c.relkind IN ('r','p') AND NOT c.relispartition;"
check_eq "7 rides tables" "7" \
  "SELECT count(*) FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
    WHERE n.nspname='rides' AND c.relkind IN ('r','p') AND NOT c.relispartition;"
check_eq "12 dispatch tables" "12" \
  "SELECT count(*) FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
    WHERE n.nspname='dispatch' AND c.relkind IN ('r','p') AND NOT c.relispartition;"
check_eq "3 reputation tables" "3" \
  "SELECT count(*) FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
    WHERE n.nspname='reputation' AND c.relkind IN ('r','p') AND NOT c.relispartition;"
check_eq "C005/C006 schemas left empty" "0" \
  "SELECT count(*) FROM information_schema.tables
    WHERE table_schema IN ('safety','fares','billing','comms',
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
    WHERE table_schema IN ('iam','registry','prov','config','trips','rides','dispatch','reputation')
      AND data_type = 'timestamp without time zone';"
# dispatch.directional_filters.used_date is the first business-date column in the schema; any
# later one that appears without its Asia/Colombo tz_at audit companion fails here (D-38).
check_eq "every DATE column has a tz_at companion" "0" \
  "SELECT count(*) FROM information_schema.columns c
    WHERE c.table_schema IN ('iam','registry','prov','config','trips','rides','dispatch','reputation')
      AND c.data_type = 'date'
      AND NOT EXISTS (SELECT 1 FROM information_schema.columns t
                       WHERE t.table_schema = c.table_schema AND t.table_name = c.table_name
                         AND t.column_name LIKE '%tz\_at');"

step "§0.2 — set_updated_at attached to every mutable table"
check_eq "no updated_at column is left without its trigger" "0" \
  "SELECT count(*) FROM information_schema.columns c
    WHERE c.table_schema IN ('iam','registry','prov','config','trips','rides','dispatch','reputation')
      AND c.column_name = 'updated_at'
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
# C004 — trips / rides / dispatch / reputation
# ---------------------------------------------------------------------------------------
step "R-01 — the trips / rides fence"
check_eq "trips.sessions.mode admits A and B but not C" "1" \
  "SELECT count(*) FROM pg_constraint
    WHERE conrelid='trips.sessions'::regclass AND conname='ck_sessions_mode'
      AND pg_get_constraintdef(oid) LIKE '%''A''%'
      AND pg_get_constraintdef(oid) LIKE '%''B''%'
      AND pg_get_constraintdef(oid) NOT LIKE '%''C''%';"
check_eq "ux_sessions_active_driver is unique partial on driver_id WHERE ACTIVE (D-03)" "1" \
  "SELECT count(*) FROM pg_indexes
    WHERE schemaname='trips' AND indexname='ux_sessions_active_driver'
      AND indexdef LIKE 'CREATE UNIQUE INDEX%(driver_id)%WHERE (state = ''ACTIVE''::text)%';"

step "§9.2 — trips.position_samples monthly partitions"
check_eq "position_samples is RANGE partitioned on sample_ts" "sample_ts" \
  "SELECT a.attname FROM pg_partitioned_table p
      JOIN pg_class c ON c.oid = p.partrelid
      JOIN pg_attribute a ON a.attrelid = c.oid AND a.attnum = p.partattrs[0]
    WHERE c.oid = 'trips.position_samples'::regclass;"
# relkind='r' matters: each partition also propagates a PK index and ix_possample_session,
# and those share the partition's name prefix.
check_eq "14 monthly partitions created (last month + 13)" "14" \
  "SELECT count(*) FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
    WHERE n.nspname='trips' AND c.relkind='r' AND c.relispartition
      AND c.relname LIKE 'position\_samples\_%';"
check_eq "there is no DEFAULT partition" "0" \
  "SELECT count(*) FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
    WHERE n.nspname='trips' AND c.relkind='r' AND c.relispartition
      AND pg_get_expr(c.relpartbound, c.oid) = 'DEFAULT';"

step "R-01 / Appendix B.2 — the 18 ride states, exactly"
check_eq "rides.rides state CHECK matches D5 §6 / ADD Appendix B.2" \
  "Accepted,CancelledByDriver,CancelledByRiderAfterAccept,CancelledByRiderBeforeAccept,CashOnDeliveryCollected,CashSettled,Completed,Disputed,DriverArrived,ExpiredNoDriver,InProgress,Matching,NoShowDriver,NoShowRider,Offered,Paid,PaymentPending,Requested" \
  "SELECT string_agg(m[1], ',' ORDER BY m[1] COLLATE \"C\")
     FROM pg_constraint c,
          LATERAL regexp_matches(pg_get_constraintdef(c.oid), '''([A-Za-z]+)''', 'g') AS m
    WHERE c.conrelid='rides.rides'::regclass AND c.conname='ck_rides_state';"

step "R-18 / O2 — rides.rides uniqueness"
check_eq "ux_rides_idem is unique on (passenger_id, client_request_id)" "1" \
  "SELECT count(*) FROM pg_indexes
    WHERE schemaname='rides' AND indexname='ux_rides_idem'
      AND indexdef LIKE 'CREATE UNIQUE INDEX%(passenger_id, client_request_id)%';"
check_eq "ux_rides_open_passenger exempts exactly the terminal set" \
  "CancelledByDriver,CancelledByRiderAfterAccept,CancelledByRiderBeforeAccept,CashOnDeliveryCollected,CashSettled,Completed,Disputed,ExpiredNoDriver,NoShowDriver,NoShowRider,Paid" \
  "SELECT string_agg(m[1], ',' ORDER BY m[1] COLLATE \"C\")
     FROM pg_indexes i, LATERAL regexp_matches(i.indexdef, '''([A-Za-z]+)''', 'g') AS m
    WHERE i.schemaname='rides' AND i.indexname='ux_rides_open_passenger';"
check_eq "ux_rides_driver_busy covers exactly the four busy states (O2/R-10)" \
  "Accepted,DriverArrived,InProgress,PaymentPending" \
  "SELECT string_agg(m[1], ',' ORDER BY m[1] COLLATE \"C\")
     FROM pg_indexes i, LATERAL regexp_matches(i.indexdef, '''([A-Za-z]+)''', 'g') AS m
    WHERE i.schemaname='rides' AND i.indexname='ux_rides_driver_busy';"

step "R-10 — one live offer per driver"
check_eq "ux_offers_driver_live is unique partial on driver_id WHERE OFFERED/ACCEPTED" \
  "ACCEPTED,OFFERED" \
  "SELECT string_agg(m[1], ',' ORDER BY m[1] COLLATE \"C\")
     FROM pg_indexes i, LATERAL regexp_matches(i.indexdef, '''([A-Z]+)''', 'g') AS m
    WHERE i.schemaname='dispatch' AND i.indexname='ux_offers_driver_live';"
check_eq "ux_offers_driver_live is UNIQUE on (driver_id)" "1" \
  "SELECT count(*) FROM pg_indexes
    WHERE schemaname='dispatch' AND indexname='ux_offers_driver_live'
      AND indexdef LIKE 'CREATE UNIQUE INDEX%(driver_id)%';"

step "R-14 — command log stores a replayable response"
check_eq "response_body is json, not jsonb (C002 micro-change-set (a))" "json" \
  "SELECT data_type FROM information_schema.columns
    WHERE table_schema='rides' AND table_name='command_log' AND column_name='response_body';"
check_eq "response_content_type exists" "1" \
  "SELECT count(*) FROM information_schema.columns
    WHERE table_schema='rides' AND table_name='command_log' AND column_name='response_content_type';"

step "R-13 / E-09 — both outboxes carry the dispatcher's column set"
check_eq "rides.outbox and dispatch.outbox both expose the six drain columns" "2" \
  "SELECT count(*) FROM (
     SELECT table_schema FROM information_schema.columns
      WHERE table_name='outbox' AND table_schema IN ('rides','dispatch')
        AND column_name IN ('id','aggregate_id','event_type','payload','created_at','dispatched_at')
      GROUP BY table_schema HAVING count(*) = 6) t;"

step "D-05 — cancellation penalty idempotency"
check_eq "ux_penalty_apply is unique on (id, applied_ride_id)" "1" \
  "SELECT count(*) FROM pg_indexes
    WHERE schemaname='dispatch' AND indexname='ux_penalty_apply'
      AND indexdef LIKE 'CREATE UNIQUE INDEX%(id, applied_ride_id)%';"
check_eq "the default penalty is Rs 50 in minor units" "5000" \
  "SELECT column_default FROM information_schema.columns
    WHERE table_schema='dispatch' AND table_name='cancellation_penalties'
      AND column_name='amount_minor';"

step "DT-03 — directional filters"
check_eq "ux_directional_active is unique partial on driver_id WHERE cleared_at IS NULL" "1" \
  "SELECT count(*) FROM pg_indexes
    WHERE schemaname='dispatch' AND indexname='ux_directional_active'
      AND indexdef LIKE 'CREATE UNIQUE INDEX%(driver_id)%WHERE (cleared_at IS NULL)%';"
check_eq "used_date defaults to the Asia/Colombo business date (D-38)" "1" \
  "SELECT count(*) FROM information_schema.columns
    WHERE table_schema='dispatch' AND table_name='directional_filters' AND column_name='used_date'
      AND column_default LIKE '%Asia/Colombo%';"
check_eq "directional_config seeded with one row" "1" "SELECT count(*) FROM dispatch.directional_config;"
check_eq "directional_config defaults are the D5 §12.1 values" "45|2000|250|2|7200" \
  "SELECT theta_max_deg||'|'||detour_max_m||'|'||progress_min_m||'|'||max_uses_per_day||'|'||max_duration_sec
     FROM dispatch.directional_config WHERE id = 1;"

step "AL-47 — proof artifacts carry the driver-QR receipt kind"
check_eq "proof_artifacts.kind covers all four kinds" "1" \
  "SELECT count(*) FROM pg_constraint
    WHERE conrelid='rides.proof_artifacts'::regclass AND conname='ck_proof_artifacts_kind'
      AND pg_get_constraintdef(oid) LIKE '%delivery_photo%'
      AND pg_get_constraintdef(oid) LIKE '%signature%'
      AND pg_get_constraintdef(oid) LIKE '%pickup_photo%'
      AND pg_get_constraintdef(oid) LIKE '%qr_receipt%';"

# ---------------------------------------------------------------------------------------
step "C004 constraints actually bite"

DRV_A='c0000001-0000-0000-0000-000000000001'
DRV_B='c0000001-0000-0000-0000-000000000002'
PAX_1='c0000002-0000-0000-0000-000000000001'
PAX_2='c0000002-0000-0000-0000-000000000002'
VEH_A='c0000003-0000-0000-0000-000000000001'
VEH_B='c0000003-0000-0000-0000-000000000002'
COLOMBO="ST_SetSRID(ST_MakePoint(79.8612,6.9271),4326)::geography"
KANDY="ST_SetSRID(ST_MakePoint(80.6337,7.2906),4326)::geography"

psql_run "INSERT INTO iam.users(id, phone, role) VALUES
            ('$DRV_A','+94770100001','driver'), ('$DRV_B','+94770100002','driver'),
            ('$PAX_1','+94770200001','passenger'), ('$PAX_2','+94770200002','passenger');
          INSERT INTO registry.vehicles(id, owner_id, registration_number, vehicle_type, mode, driver_name)
            VALUES ('$VEH_A','$DRV_A','WP-TUK-0001','three_wheeler','C','Driver A'),
                   ('$VEH_B','$DRV_B','WP-TUK-0002','three_wheeler','C','Driver B');" >/dev/null \
  || die "could not seed the C004 constraint fixtures."

check_rejects "a Mode C tracking session is rejected — Mode C is rides.rides (R-01)" \
  "INSERT INTO trips.sessions(vehicle_id, driver_id, mode) VALUES ('$VEH_A','$DRV_A','C');"

psql_run "INSERT INTO trips.sessions(vehicle_id, driver_id, mode) VALUES ('$VEH_A','$DRV_A','B');" >/dev/null \
  || die "could not insert the fixture tracking session."

check_rejects "a driver cannot hold two ACTIVE tracking sessions (D-03)" \
  "INSERT INTO trips.sessions(vehicle_id, driver_id, mode) VALUES ('$VEH_B','$DRV_A','B');"

psql_run "INSERT INTO rides.rides(id, passenger_id, client_request_id, booker_id, vehicle_type,
                                  pickup_geo, dropoff_geo)
            VALUES ('c0000004-0000-0000-0000-000000000001','$PAX_1',
                    'c0000005-0000-0000-0000-000000000001','$PAX_1','three_wheeler',
                    $COLOMBO, $KANDY);
          UPDATE rides.rides SET state='CashSettled', terminal_at=now()
            WHERE id='c0000004-0000-0000-0000-000000000001';" >/dev/null \
  || die "could not insert the fixture ride."

# The ride above is terminal, so only ux_rides_idem can reject this — an open-ride collision
# would otherwise mask the R-18 check.
check_rejects "the same (passenger, clientRequestId) cannot book twice (R-18)" \
  "INSERT INTO rides.rides(passenger_id, client_request_id, booker_id, vehicle_type, pickup_geo, dropoff_geo)
     VALUES ('$PAX_1','c0000005-0000-0000-0000-000000000001','$PAX_1','three_wheeler', $COLOMBO, $KANDY);"

psql_run "INSERT INTO rides.rides(id, passenger_id, client_request_id, booker_id, vehicle_type,
                                  pickup_geo, dropoff_geo, state, accepted_driver_id, accepted_vehicle_id)
            VALUES ('c0000004-0000-0000-0000-000000000002','$PAX_1',
                    'c0000005-0000-0000-0000-000000000002','$PAX_1','three_wheeler',
                    $COLOMBO, $KANDY, 'Accepted','$DRV_A','$VEH_A');" >/dev/null \
  || die "could not insert the accepted fixture ride."

check_rejects "a passenger cannot hold two open rides" \
  "INSERT INTO rides.rides(passenger_id, client_request_id, booker_id, vehicle_type, pickup_geo, dropoff_geo)
     VALUES ('$PAX_1','c0000005-0000-0000-0000-000000000003','$PAX_1','three_wheeler', $COLOMBO, $KANDY);"

check_rejects "a driver cannot be on two rides at once (O2 / R-10)" \
  "INSERT INTO rides.rides(passenger_id, client_request_id, booker_id, vehicle_type, pickup_geo, dropoff_geo,
                           state, accepted_driver_id)
     VALUES ('$PAX_2','c0000005-0000-0000-0000-000000000004','$PAX_2','three_wheeler', $COLOMBO, $KANDY,
             'Accepted','$DRV_A');"

check_rejects "a package ride without size and both OTP hashes is rejected (P-06/P-07)" \
  "INSERT INTO rides.rides(passenger_id, client_request_id, booker_id, vehicle_type, pickup_geo, dropoff_geo, kind)
     VALUES ('$PAX_2','c0000005-0000-0000-0000-000000000005','$PAX_2','mini_truck', $COLOMBO, $KANDY, 2);"

check_rejects "a proxy ride with no way to identify the rider is rejected (P-01/P-03)" \
  "INSERT INTO rides.rides(passenger_id, client_request_id, booker_id, vehicle_type, pickup_geo, dropoff_geo,
                           kind, is_proxy)
     VALUES ('$PAX_2','c0000005-0000-0000-0000-000000000006','$PAX_2','three_wheeler', $COLOMBO, $KANDY, 1, true);"

CHECKS=$((CHECKS + 1))
if psql_run "INSERT INTO rides.rides(passenger_id, client_request_id, booker_id, vehicle_type, pickup_geo,
                                     dropoff_geo, kind, package_size, pickup_otp_hash, delivery_otp_hash,
                                     payment_method)
               VALUES ('$PAX_2','c0000005-0000-0000-0000-000000000007','$PAX_2','mini_truck', $COLOMBO,
                       $KANDY, 2, 'M', decode('00','hex'), decode('01','hex'), 'cod');" >/dev/null 2>&1; then
  printf '  %s✓%s a complete package ride with COD is accepted (P-06/P-08)\n' "$GREEN" "$RESET"
else
  printf '  %s✗%s a complete package ride with COD was rejected\n' "$RED" "$RESET"
  FAILURES=$((FAILURES + 1))
fi

psql_run "INSERT INTO dispatch.offers(id, ride_id, driver_id, expires_at)
            VALUES ('c0000006-0000-0000-0000-000000000001','c0000004-0000-0000-0000-000000000002',
                    '$DRV_A', now() + interval '15 seconds');" >/dev/null \
  || die "could not insert the fixture offer."

check_rejects "a driver cannot hold two live offers (R-10)" \
  "INSERT INTO dispatch.offers(ride_id, driver_id, expires_at)
     VALUES ('c0000004-0000-0000-0000-000000000001','$DRV_A', now() + interval '15 seconds');"

CHECKS=$((CHECKS + 1))
if psql_run "UPDATE dispatch.offers SET status='DECLINED', responded_at=now()
               WHERE id='c0000006-0000-0000-0000-000000000001';
             INSERT INTO dispatch.offers(ride_id, driver_id, expires_at)
               VALUES ('c0000004-0000-0000-0000-000000000001','$DRV_A', now() + interval '15 seconds');" >/dev/null 2>&1; then
  printf '  %s✓%s a DECLINED offer releases the driver for the next one (D5 §7)\n' "$GREEN" "$RESET"
else
  printf '  %s✗%s a DECLINED offer still blocks the driver\n' "$RED" "$RESET"
  FAILURES=$((FAILURES + 1))
fi

psql_run "INSERT INTO dispatch.directional_filters(driver_id, destination_geo, expires_at)
            VALUES ('$DRV_A', $KANDY, now() + interval '2 hours');" >/dev/null \
  || die "could not insert the fixture directional filter."

check_rejects "a driver cannot hold two active directional filters (DT-03)" \
  "INSERT INTO dispatch.directional_filters(driver_id, destination_geo, expires_at)
     VALUES ('$DRV_A', $COLOMBO, now() + interval '2 hours');"

check_eq "used_date landed on the Asia/Colombo business date (D-38)" "t" \
  "SELECT used_date = (now() AT TIME ZONE 'Asia/Colombo')::date
     FROM dispatch.directional_filters WHERE driver_id = '$DRV_A';"

CHECKS=$((CHECKS + 1))
if psql_run "UPDATE dispatch.directional_filters SET cleared_at=now(), cleared_reason='manual'
               WHERE driver_id='$DRV_A';
             INSERT INTO dispatch.directional_filters(driver_id, destination_geo, expires_at)
               VALUES ('$DRV_A', $COLOMBO, now() + interval '2 hours');" >/dev/null 2>&1; then
  printf '  %s✓%s a cleared filter frees the driver but keeps its used_date row (DT-03)\n' "$GREEN" "$RESET"
else
  printf '  %s✗%s a cleared directional filter still blocks a new one\n' "$RED" "$RESET"
  FAILURES=$((FAILURES + 1))
fi
check_eq "both activations count toward the daily limit (DT-03)" "2" \
  "SELECT count(*) FROM dispatch.directional_filters
    WHERE driver_id='$DRV_A' AND used_date = (now() AT TIME ZONE 'Asia/Colombo')::date;"

check_rejects "a second dispatch.directional_config row is rejected (DT-02)" \
  "INSERT INTO dispatch.directional_config(id) VALUES (2);"

psql_run "INSERT INTO trips.position_samples(session_id, vehicle_id, geo, sample_ts)
            SELECT id, vehicle_id, $COLOMBO, now() FROM trips.sessions LIMIT 1;" >/dev/null \
  || die "could not insert the fixture position sample."

check_eq "a sample routes into this month's Asia/Colombo partition (§9.2)" "1" \
  "SELECT count(*) FROM trips.position_samples p
    WHERE p.tableoid::regclass::text =
          'trips.position_samples_' || to_char((now() AT TIME ZONE 'Asia/Colombo'), 'YYYY_MM');"

check_rejects "a rating outside 1–5 stars is rejected" \
  "INSERT INTO trips.ratings(subject_kind, subject_id, rater_id, ratee_id, stars, direction)
     VALUES ('ride','c0000004-0000-0000-0000-000000000001','$PAX_1','$DRV_A', 6, 'passenger_to_driver');"

check_rejects "a driver level outside 1–3 is rejected (US-6A.6)" \
  "INSERT INTO dispatch.driver_levels(driver_id, level) VALUES ('$DRV_B', 4);"

check_rejects "an unknown block state is rejected (D-04)" \
  "INSERT INTO reputation.block_states(user_id, state) VALUES ('$PAX_1','SHADOWBANNED');"

# ---------------------------------------------------------------------------------------
printf '\n'
if (( FAILURES == 0 )); then
  printf '%s%d/%d checks passed — migrations apply cleanly, twice.%s\n' "$GREEN" "$CHECKS" "$CHECKS" "$RESET"
  exit 0
fi

printf '%s%d of %d checks failed.%s\n' "$RED" "$FAILURES" "$CHECKS" "$RESET"
exit 1
