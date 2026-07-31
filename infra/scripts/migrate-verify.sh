#!/usr/bin/env bash
# =====================================================================================
# C003 / C004 / C005 / C006 verify — apply db/migrations to a throwaway PostgreSQL 16 +
# TimescaleDB + PostGIS container, twice, and assert the objects each definition of done names.
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

# Every schema whose tables have been landed so far (C003 + C004 + C005 + C006). The
# platform-wide rules — TIMESTAMPTZ only, a tz_at companion per business DATE, set_updated_at
# on every mutable table — are asserted across all of them at once, so a later component
# cannot quietly opt out by adding a schema.
OWNED_SCHEMAS="'iam','registry','prov','config','trips','rides','dispatch','reputation',
               'safety','fares','billing','subscription','comms','docs','support','content',
               'audit','pdpa','spatial','transit','transit_staging','analytics','telemetry'"

# The fleet-scoped login role the C006 checks connect as. Created after the migrations run
# (1804 creates the mageride_fleet_reader group role it is a member of).
FLEET_ROLE="verify_fleet"

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

psql_fleet() { # psql_fleet <fleet_id | ""> <sql> -> last line, whitespace trimmed
  local fleet="$1" sql="$2" prelude=""
  [[ -n "$fleet" ]] && prelude="SET app.fleet_id = '$fleet'; "
  docker exec "$CONTAINER" psql -U "$FLEET_ROLE" -d "$PGDATABASE_VALUE" \
    -tAqc "$prelude$sql" 2>/dev/null | tail -1 | tr -d '[:space:]'
}

check_fleet_eq() { # check_fleet_eq <label> <expected> <fleet_id | ""> <sql>
  local label="$1" expected="$2" fleet="$3" sql="$4" actual
  CHECKS=$((CHECKS + 1))
  actual="$(psql_fleet "$fleet" "$sql")"
  if [[ "$actual" == "$expected" ]]; then
    printf '  %s✓%s %s\n' "$GREEN" "$RESET" "$label"
  else
    printf '  %s✗%s %s (expected %s, got %s)\n' "$RED" "$RESET" "$label" "$expected" "${actual:-<empty>}"
    FAILURES=$((FAILURES + 1))
  fi
}

check_fleet_denied() { # check_fleet_denied <label> <sql the fleet role must not be allowed to run>
  local label="$1" sql="$2"
  CHECKS=$((CHECKS + 1))
  if docker exec "$CONTAINER" psql -U "$FLEET_ROLE" -d "$PGDATABASE_VALUE" \
       -v ON_ERROR_STOP=1 -qc "$sql" >/dev/null 2>&1; then
    printf '  %s✗%s %s (the read was allowed)\n' "$RED" "$RESET" "$label"
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
# 9 from C003 + iam.command_log, which C020 added because D3' §0 mandates a per-service
# idempotency log and D4' §5 prints one only for rides (C020 handoff micro-change-set);
# + iam.user_credentials and iam.federated_identities, which C026 added because AL-07 gives the
# two portals password / Google / Apple sign-in and D4' §1 stores no verifier and no provider
# binding (C026 handoff micro-change-set);
# + iam.phone_lookups, which C027 added because P-03's registration oracle
# (GET /v1/users/lookup) answers a question about a person who never signed up and no spec gives
# it a record — hashed, never the number (C027 handoff micro-change-set).
check_eq "13 iam tables" "13" \
  "SELECT count(*) FROM information_schema.tables WHERE table_schema='iam' AND table_type='BASE TABLE';"
# 12 from C003 + registry.command_log, added by C021 for the same reason iam.command_log was
# (C021 handoff micro-change-set); + registry.outbox, added by C028 because `share.revoked` (D-22)
# has a producer and a consumer in the specs and neither a topic nor a table
# (C028 handoff micro-change-set); + registry.document_notices, added by C029 because E-03 names
# four distinct notices per document and registry.documents.status can only remember one
# (C029 handoff micro-change-set).
check_eq "15 registry tables" "15" \
  "SELECT count(*) FROM information_schema.tables WHERE table_schema='registry' AND table_type='BASE TABLE';"
# 2 from server_db_schema.md §3 (tracker_bindings, device_certs) + 5 added by C030, each a
# micro-change-set raised in its handoff: prov.command_log (D3' §0 mandates a per-service
# idempotency log and D4' prints one for rides only), prov.outbox (tracker.bound/tracker.unbound
# have a producer and a consumer in D6' §4.3 and no topic or table), prov.imei_sightings (T-08 is a
# 24 h window and nothing recorded a presentation to measure it against), and
# prov.bulk_jobs + prov.bulk_job_rows (D3' specifies the T-09 endpoint completely and D4' has no
# table for any of it).
check_eq "7 prov tables" "7" \
  "SELECT count(*) FROM information_schema.tables WHERE table_schema='prov' AND table_type='BASE TABLE';"
check_eq "1 config table" "1" \
  "SELECT count(*) FROM information_schema.tables WHERE table_schema='config' AND table_type='BASE TABLE';"

step "Tables owned by C004"
# relispartition filters out the trips.position_samples monthly partitions, which are tables
# in their own right but not schema objects anyone declared.
# 4 from server_db_schema.md §4 (sessions, events, ratings, position_samples) + 2 added by C031,
# both micro-change-sets raised in its handoff: trips.command_log (D3' §0 mandates a per-service
# idempotency log and D4' prints one for rides only — the fourth service to need it) and
# trips.outbox (D6' §2.1 names trip.events and trip-state-svc as its producer, and neither D4' §4
# nor server_db_schema.md §4 gives that producer a transactional table to write into).
# + 1 added by C040 (0506): trips.session_summaries. ADD §9.2 promises a durable "trip summary
# (start, end, distance, polyline)" and no DDL source prints a table for it; §9.5 item 2's
# continuous aggregates cannot answer it, being bucketed by time and blind to sessions.
check_eq "7 trips tables" "7" \
  "SELECT count(*) FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
    WHERE n.nspname='trips' AND c.relkind IN ('r','p') AND NOT c.relispartition;"
check_eq "7 rides tables" "7" \
  "SELECT count(*) FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
    WHERE n.nspname='rides' AND c.relkind IN ('r','p') AND NOT c.relispartition;"
# 14, not C004's 12: 0710 added dispatch.command_log — the third per-service command log
# (iam 0104, registry 0307), for the reason recorded in db/CLAUDE.md — and 0713 added
# dispatch.level_config, the singleton PUT /v1/admin/drivers/level-config writes (US-14.12).
check_eq "14 dispatch tables" "14" \
  "SELECT count(*) FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
    WHERE n.nspname='dispatch' AND c.relkind IN ('r','p') AND NOT c.relispartition;"
# 7, not C004's 3: 0803 added reputation.intake_log / outbox / command_log and 0805 added
# reputation.network_observations, for the reasons recorded in each file header and in the
# C033 handoff.
check_eq "7 reputation tables" "7" \
  "SELECT count(*) FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
    WHERE n.nspname='reputation' AND c.relkind IN ('r','p') AND NOT c.relispartition;"

step "AL-08 — single active device per app"
check_eq "ux_sessions_active_app is a unique partial index on (user_id, app)" "1" \
  "SELECT count(*) FROM pg_indexes
    WHERE schemaname='iam' AND indexname='ux_sessions_active_app'
      AND indexdef LIKE 'CREATE UNIQUE INDEX%(user_id, app)%WHERE (revoked_at IS NULL)%';"

step "AL-07 / AL-37 — portal sign-in surfaces (C026, 0107)"
# The apps' two surfaces plus the two portals. Widened by 0107 so a browser sign-in has a legal
# row at all; ux_sessions_active_app then also gives one live portal session per person, which is
# the "session binding" AL-37 keeps as a compensating control.
check_eq "sessions.app admits both apps and both portals" "1" \
  "SELECT count(*) FROM pg_constraint
    WHERE conrelid='iam.sessions'::regclass AND conname='ck_sessions_app'
      AND pg_get_constraintdef(oid) LIKE '%passenger%driver%admin%fleet%';"
check_eq "devices.platform admits web" "1" \
  "SELECT count(*) FROM pg_constraint
    WHERE conrelid='iam.devices'::regclass AND conname='ck_devices_platform'
      AND pg_get_constraintdef(oid) LIKE '%android%ios%web%';"
check_eq "one MageRide account per federated (provider, subject)" "1" \
  "SELECT count(*) FROM pg_indexes
    WHERE schemaname='iam' AND indexname='ux_federated_provider_subject'
      AND indexdef LIKE 'CREATE UNIQUE INDEX%(provider, subject)%';"
# AL-37 removed the MFA/TOTP step and replaced it with a lock-out. There is no iam.user_mfa and
# there must never be one — the counter that replaced it lives on the credential it counts.
check_eq "no MFA table exists anywhere (AL-37)" "0" \
  "SELECT count(*) FROM information_schema.tables
    WHERE table_schema='iam' AND table_name IN ('user_mfa','mfa_enrolments','user_totp');"
check_eq "the lock-out counter is durable, not cached" "2" \
  "SELECT count(*) FROM information_schema.columns
    WHERE table_schema='iam' AND table_name='user_credentials'
      AND column_name IN ('failed_attempts','locked_until');"

step "P-03 — the phone-lookup log stores a hash, never a number (C027, 0108)"
# BYTEA on purpose: a TEXT column is one careless INSERT away from holding the number it exists
# to avoid holding.
check_eq "phone_lookups.phone_hash is bytea" "1" \
  "SELECT count(*) FROM information_schema.columns
    WHERE table_schema='iam' AND table_name='phone_lookups'
      AND column_name='phone_hash' AND data_type='bytea';"
check_eq "phone_lookups has no clear-text phone column" "0" \
  "SELECT count(*) FROM information_schema.columns
    WHERE table_schema='iam' AND table_name='phone_lookups' AND column_name IN ('phone','msisdn');"
# No ON DELETE CASCADE from iam.users: the audit row must outlive the account it may name.
check_eq "phone_lookups.user_id nulls rather than cascades" "1" \
  "SELECT count(*) FROM pg_constraint
    WHERE conrelid='iam.phone_lookups'::regclass AND contype='f' AND confdeltype='n';"

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
# E-03's four notices per document (C029, migration 0312). The primary key is the idempotency:
# a nightly job that runs twice must not push the same reminder twice.
check_eq "registry.document_notices threshold domain" "1" \
  "SELECT count(*) FROM pg_constraint
    WHERE conrelid='registry.document_notices'::regclass AND contype='c'
      AND pg_get_constraintdef(oid) LIKE '%30%7%1%0%';"
check_eq "registry.document_notices is keyed per (document, threshold)" "document_id,threshold_days" \
  "SELECT string_agg(a.attname, ',' ORDER BY k.ord)
     FROM pg_constraint c
     JOIN LATERAL unnest(c.conkey) WITH ORDINALITY AS k(attnum, ord) ON true
     JOIN pg_attribute a ON a.attrelid = c.conrelid AND a.attnum = k.attnum
    WHERE c.conrelid='registry.document_notices'::regclass AND c.contype='p';"

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
    WHERE table_schema IN ($OWNED_SCHEMAS)
      AND data_type = 'timestamp without time zone';"
# dispatch.directional_filters.used_date was the first business-date column in the schema; any
# later one that appears without its Asia/Colombo tz_at audit companion fails here (D-38).
#
# transit.gtfs_feed_versions is the one documented exemption: service_start / service_end are
# read out of an uploaded GTFS feed rather than computed in Asia/Colombo, so there is no
# derivation instant for a companion to record.
check_eq "every DATE column has a tz_at companion" "0" \
  "SELECT count(*) FROM information_schema.columns c
    WHERE c.table_schema IN ($OWNED_SCHEMAS)
      AND c.data_type = 'date'
      AND NOT (c.table_schema = 'transit' AND c.table_name = 'gtfs_feed_versions')
      AND NOT EXISTS (SELECT 1 FROM information_schema.columns t
                       WHERE t.table_schema = c.table_schema AND t.table_name = c.table_name
                         AND t.column_name LIKE '%tz\_at');"

step "§0.2 — set_updated_at attached to every mutable table"
check_eq "no updated_at column is left without its trigger" "0" \
  "SELECT count(*) FROM information_schema.columns c
    WHERE c.table_schema IN ($OWNED_SCHEMAS)
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

# P-03: an unregistered answer names nobody (ck_phone_lookups_identity, C027/0108). Asserted here
# rather than with the rest of the 0108 checks because it needs a real account to try to name —
# otherwise the foreign key could reject the row and the CHECK would go untested.
check_rejects "an unregistered lookup cannot name an account (P-03)" \
  "INSERT INTO iam.phone_lookups(phone_hash, registered, user_id)
     VALUES (decode('00','hex'), false, '11111111-1111-1111-1111-111111111111');"

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

step "US-9.6 — one selected vehicle per driver (C021)"
psql_run "INSERT INTO iam.users(id, phone, role) VALUES
            ('55555555-5555-5555-5555-555555555555','+94770000555','driver');
          INSERT INTO registry.vehicles(id, owner_id, registration_number, vehicle_type, mode, driver_name)
            VALUES ('66666666-6666-6666-6666-666666666666','55555555-5555-5555-5555-555555555555',
                    'WP-QQ-5555','three_wheeler','C','Other Driver');
          INSERT INTO registry.driver_profiles(driver_id, display_name) VALUES
            ('11111111-1111-1111-1111-111111111111','Test Driver'),
            ('55555555-5555-5555-5555-555555555555','Other Driver');" >/dev/null \
  || die "could not seed the selection fixtures."

# 0308 made "a driver may only select a vehicle they own" a composite foreign key. 0311 relaxed
# it to a plain one, because US-13.9 gives an *assigned* non-owner the right to select a fleet
# vehicle and the composite key rejected exactly that. What the schema still guarantees is below;
# who may select what is registry.driver_eligible_vehicles, enforced by registry-svc (C028).
check_rejects "a selection must name a real vehicle (fk_driver_profiles_active_vehicle_id)" \
  "UPDATE registry.driver_profiles
      SET active_vehicle_id='99999999-9999-9999-9999-999999999999', active_vehicle_selected_at=now()
    WHERE driver_id='11111111-1111-1111-1111-111111111111';"
check_rejects "a selected vehicle with no selection instant is rejected (US-9.7)" \
  "UPDATE registry.driver_profiles
      SET active_vehicle_id='66666666-6666-6666-6666-666666666666'
    WHERE driver_id='55555555-5555-5555-5555-555555555555';"

CHECKS=$((CHECKS + 1))
if psql_run "UPDATE registry.driver_profiles
                SET active_vehicle_id='66666666-6666-6666-6666-666666666666', active_vehicle_selected_at=now()
              WHERE driver_id='55555555-5555-5555-5555-555555555555';" >/dev/null 2>&1 \
   && [[ "$(psql_q "SELECT count(*) FROM registry.driver_profiles
                     WHERE driver_id='55555555-5555-5555-5555-555555555555'
                       AND active_vehicle_id IS NOT NULL;")" == "1" ]]; then
  printf '  %s✓%s a driver may select their own vehicle, and only one (US-9.6)\n' "$GREEN" "$RESET"
else
  printf '  %s✗%s selecting an owned vehicle failed, or more than one selection survived\n' "$RED" "$RESET"
  FAILURES=$((FAILURES + 1))
fi

# US-13.9 / C028 (0310, 0311): the projection is what answers "which vehicles may this driver
# operate", and an assignment puts a vehicle the driver does not own into their list.
psql_run "INSERT INTO registry.fleets(id, owner_id, name)
            VALUES ('47474747-4747-4747-4747-474747474747','55555555-5555-5555-5555-555555555555','Assign Fleet');
          INSERT INTO registry.fleet_assignments(fleet_id, vehicle_id, driver_id)
            VALUES ('47474747-4747-4747-4747-474747474747',
                    '66666666-6666-6666-6666-666666666666',
                    '11111111-1111-1111-1111-111111111111');" >/dev/null \
  || die "could not seed the fleet assignment fixture."

check_eq "an assigned driver appears in the eligibility projection as 'assigned' (US-13.9)" "assigned" \
  "SELECT source FROM registry.driver_eligible_vehicles
    WHERE driver_id='11111111-1111-1111-1111-111111111111'
      AND vehicle_id='66666666-6666-6666-6666-666666666666';"
check_eq "the owner of that same vehicle appears as 'owned'" "owned" \
  "SELECT source FROM registry.driver_eligible_vehicles
    WHERE driver_id='55555555-5555-5555-5555-555555555555'
      AND vehicle_id='66666666-6666-6666-6666-666666666666';"
# A PENDING vehicle is entitled-to and not go-live eligible: the projection reports both facts so
# dispatch can still answer `vehicle-not-approved` rather than `vehicle-not-found`.
check_eq "a PENDING vehicle is listed but not go-live eligible (US-9.6, AL-30)" "f" \
  "SELECT is_go_live_eligible FROM registry.driver_eligible_vehicles
    WHERE driver_id='55555555-5555-5555-5555-555555555555'
      AND vehicle_id='66666666-6666-6666-6666-666666666666';"
# US-13.8: revoking the assignment takes the entitlement away the moment it is written.
psql_run "UPDATE registry.fleet_assignments SET revoked_at = now()
           WHERE driver_id='11111111-1111-1111-1111-111111111111';" >/dev/null \
  || die "could not revoke the fixture assignment."
check_eq "a revoked assignment leaves the projection at once (US-13.8)" "0" \
  "SELECT count(*) FROM registry.driver_eligible_vehicles
    WHERE driver_id='11111111-1111-1111-1111-111111111111'
      AND vehicle_id='66666666-6666-6666-6666-666666666666';"

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

# C040 (0506). The 1/min sample had no key, so an at-least-once consumer appended a duplicate
# row on every rebalance; the writer stores each row at its minute boundary and this index is
# what makes that idempotent without per-vehicle state.
check_eq "one operational sample per session per minute (0506)" "1" \
  "SELECT count(*) FROM pg_indexes
    WHERE schemaname='trips' AND indexname='ux_possample_session_minute'
      AND indexdef LIKE 'CREATE UNIQUE INDEX%(session_id, sample_ts)%';"

step "ADD §9.2 — the trip summary (C040, 0506)"
check_eq "trips.session_summaries exists" "1" \
  "SELECT count(*) FROM information_schema.tables
    WHERE table_schema='trips' AND table_name='session_summaries';"
# ADD §9.2 names exactly these four. A summary with a distance and no line, or a line and no
# ends, is not the artefact the sentence promises.
for col in start_geo end_geo distance_m polyline; do
  check_eq "session_summaries.$col" "1" \
    "SELECT count(*) FROM information_schema.columns
      WHERE table_schema='trips' AND table_name='session_summaries' AND column_name='$col';"
done
check_eq "geometry_source records which relation the distance came from" "1" \
  "SELECT count(*) FROM pg_constraint
    WHERE conrelid='trips.session_summaries'::regclass
      AND conname='ck_summaries_geometry_source';"
# R-01 again: a summary is a Mode A/B journey. A Mode C ride is rides.rides and is priced,
# not summarised from a tracking session.
check_rejects "a Mode C trip summary is rejected (R-01)" \
  "INSERT INTO trips.session_summaries(session_id, vehicle_id, driver_id, mode, started_at, ended_at)
     VALUES (gen_random_uuid(), gen_random_uuid(), gen_random_uuid(), 'C', now(), now());"
check_rejects "a negative summary distance is rejected" \
  "INSERT INTO trips.session_summaries(session_id, vehicle_id, driver_id, mode, started_at, ended_at, distance_m)
     VALUES (gen_random_uuid(), gen_random_uuid(), gen_random_uuid(), 'B', now(), now(), -1);"

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

# C037 / migration 0609. The P-02 sweep scans the requests still open, and AL-45 makes
# RiderNotRegistered one of them — a request answered by SMS runs down the same 300 s clock.
check_eq "ix_location_requests_due covers both live request states (P-02/AL-45)" \
  "Pending,RiderNotRegistered" \
  "SELECT string_agg(m[1], ',' ORDER BY m[1] COLLATE \"C\")
     FROM pg_indexes i, LATERAL regexp_matches(i.indexdef, '''([A-Za-z]+)''', 'g') AS m
    WHERE i.schemaname='rides' AND i.indexname='ix_location_requests_due';"

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
# 0713: ux_penalty_apply cannot reject a redelivered accrual — `id` is the primary key, so the
# pair is unique by construction. This one is the guard that actually holds (C035 handoff).
check_eq "ux_penalty_accrual is unique on (original_ride_id, basis)" "1" \
  "SELECT count(*) FROM pg_indexes
    WHERE schemaname='dispatch' AND indexname='ux_penalty_accrual'
      AND indexdef LIKE 'CREATE UNIQUE INDEX%(original_ride_id, basis)%';"

step "US-6A.6/6A.7/US-14.12 — Driver Level System (0713)"
check_eq "driver_levels start at 3 with a 500-point threshold" "3|500" \
  "SELECT (SELECT column_default FROM information_schema.columns
             WHERE table_schema='dispatch' AND table_name='driver_levels' AND column_name='level')
         ||'|'||
         (SELECT column_default FROM information_schema.columns
             WHERE table_schema='dispatch' AND table_name='driver_levels' AND column_name='level_up_threshold');"
check_eq "the level-up engine has its idempotency watermark" "1" \
  "SELECT count(*) FROM information_schema.columns
    WHERE table_schema='dispatch' AND table_name='driver_levels' AND column_name='points_awarded_total';"
check_eq "ux_no_show_driver_ride is unique partial on (driver_id, ride_id)" "1" \
  "SELECT count(*) FROM pg_indexes
    WHERE schemaname='dispatch' AND indexname='ux_no_show_driver_ride'
      AND indexdef LIKE 'CREATE UNIQUE INDEX%(driver_id, ride_id)%WHERE (ride_id IS NOT NULL)%';"
check_eq "level_config seeded with one row" "1" "SELECT count(*) FROM dispatch.level_config;"
check_eq "level_config defaults are the D5 §4.2 values" "500|2" \
  "SELECT level_up_threshold||'|'||job_board_min_level FROM dispatch.level_config WHERE id = 1;"

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

# C037 / migration 0609: AL-21 notifies the recipient and AL-33 dials them, so a parcel with
# nobody to deliver it to is a delivery that cannot be completed.
check_rejects "a package ride with no recipient is rejected (AL-21/AL-33)" \
  "INSERT INTO rides.rides(passenger_id, client_request_id, booker_id, vehicle_type, pickup_geo, dropoff_geo,
                           kind, package_size, pickup_otp_hash, delivery_otp_hash)
     VALUES ('$PAX_2','c0000005-0000-0000-0000-000000000008','$PAX_2','mini_truck', $COLOMBO, $KANDY,
             2, 'M', decode('00','hex'), decode('01','hex'));"

CHECKS=$((CHECKS + 1))
if psql_run "INSERT INTO rides.rides(passenger_id, client_request_id, booker_id, vehicle_type, pickup_geo,
                                     dropoff_geo, kind, package_size, pickup_otp_hash, delivery_otp_hash,
                                     recipient_name, recipient_phone, payment_method)
               VALUES ('$PAX_2','c0000005-0000-0000-0000-000000000007','$PAX_2','mini_truck', $COLOMBO,
                       $KANDY, 2, 'M', decode('00','hex'), decode('01','hex'),
                       'Kamala', '+94771234567', 'cod');" >/dev/null 2>&1; then
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
# C005 — safety / fares / billing / subscription / comms / docs / support / content /
#        audit / pdpa / spatial / transit / analytics
# ---------------------------------------------------------------------------------------
step "Tables owned by C005"
# 7 safety, not C005's 5: `safety.outbox` (the admin live feed D3' asks for and signalr-hub.md has
# no group for) and `safety.command_log` (R-14, the eleventh) are C052's, both added by 0905.
check_eq "7 safety tables" "7" \
  "SELECT count(*) FROM information_schema.tables WHERE table_schema='safety' AND table_type='BASE TABLE';"
# 6, not C005's 5: `fares.command_log` is C049's (1005) — R-14 needs a replay log per bounded
# context and D4' §5 prints DDL for `rides.command_log` only.
check_eq "5 fares tables from C005 + 1 from C049" "6" \
  "SELECT count(*) FROM information_schema.tables WHERE table_schema='fares' AND table_type='BASE TABLE';"
# 15, not C005's 12: billing.topups, billing.outbox and billing.command_log are C046's (1107).
check_eq "12 billing tables from C005 + 3 from C046" "15" \
  "SELECT count(*) FROM information_schema.tables WHERE table_schema='billing' AND table_type='BASE TABLE';"
# 6, not C005's 4: `subscription.command_log` is C047's (1203) — R-14 needs a replay log per
# bounded context and D4' §5 prints DDL for `rides.command_log` only — and `subscription.outbox`
# is C048's (1204), because BR-23.11's unsubscribe has to publish `share.revoked` inside the
# transaction that mutes the grant and D6' §2.1 gives this service no topic and D4' §18b no table.
check_eq "4 subscription tables from C005 + 1 from C047 + 1 from C048" "6" \
  "SELECT count(*) FROM information_schema.tables WHERE table_schema='subscription' AND table_type='BASE TABLE';"
# 5 content, not C005's 3: `content.onboarding_slides` (AL-28's carousel copy, which is
# content-svc's content) and `content.command_log` (R-14, one per bounded context) are C045's, both
# added by 1307 in the same schema.
# 5 comms, not C005's 3: `comms.notifications` (D5' §14.4's outbound queue, which no spec declares)
# and `comms.command_log` (R-14, the tenth of them) are C051's, both added by 1308.
# 3 support, not C005's 1: `support.ticket_events` (the thread the definition of done makes a
# ticket's status transitions "visible to the user" through — §13 holds one `admin_response`, so a
# second reply overwrites the first) and `support.command_log` (R-14, the twelfth) are C053's, both
# added by 1309.
check_eq "5 comms, 2 docs, 3 support, 5 content, 1 audit, 2 pdpa, 3 spatial tables" "21" \
  "SELECT count(*) FROM information_schema.tables
    WHERE table_schema IN ('comms','docs','support','content','audit','pdpa','spatial')
      AND table_type='BASE TABLE';"
check_eq "6 transit + 5 transit_staging + 1 analytics tables" "12" \
  "SELECT count(*) FROM information_schema.tables
    WHERE table_schema IN ('transit','transit_staging','analytics') AND table_type='BASE TABLE';"
check_eq "billing.bank_transfer_topups does not exist (AL-05)" "0" \
  "SELECT count(*) FROM information_schema.tables
    WHERE table_schema='billing' AND table_name='bank_transfer_topups';"

step "Deferred FKs from C003 and C004 are now closed"
check_eq "trips.sessions.route_id references spatial.routes (C004 note (d))" "1" \
  "SELECT count(*) FROM pg_constraint
    WHERE conrelid='trips.sessions'::regclass AND conname='fk_sessions_route' AND contype='f';"
check_eq "fleet_payout_profiles both upload columns reference docs.uploads (AL-49)" "2" \
  "SELECT count(*) FROM pg_constraint
    WHERE conrelid='registry.fleet_payout_profiles'::regclass AND contype='f'
      AND confrelid='docs.uploads'::regclass;"

step "§0 Money — integer minor units, non-negative, LKR"
check_eq "every *_minor column is an integer type" "0" \
  "SELECT count(*) FROM information_schema.columns
    WHERE table_schema IN ($OWNED_SCHEMAS)
      AND column_name LIKE '%\_minor'
      AND data_type NOT IN ('integer','bigint');"
# §0 exempts exactly the ledger balances and postings: those are signed BIGINT because the
# suspense account and the debit leg of every entry are negative by construction. Every
# other *_minor column in the platform must carry a >= 0 (or > 0) CHECK.
check_eq "every non-ledger *_minor column has a non-negative CHECK" "0" \
  "SELECT count(*) FROM information_schema.columns c
    WHERE c.table_schema IN ($OWNED_SCHEMAS)
      AND c.column_name LIKE '%\_minor'
      AND (c.table_schema||'.'||c.table_name||'.'||c.column_name) NOT IN (
            'billing.accounts.balance_minor',
            'billing.journal_postings.amount_minor',
            'billing.wallets.balance_minor',
            'billing.wallet_transactions.amount_minor',
            'billing.wallet_transactions.balance_after_minor')
      AND NOT EXISTS (
        SELECT 1 FROM pg_constraint k
         WHERE k.conrelid = format('%I.%I', c.table_schema, c.table_name)::regclass
           AND k.contype = 'c'
           AND pg_get_constraintdef(k.oid) ~ ('\\m' || c.column_name || '\\M[[:space:]]*>=?[[:space:]]*0'));"
check_eq "every currency column defaults to LKR" "0" \
  "SELECT count(*) FROM information_schema.columns
    WHERE table_schema IN ($OWNED_SCHEMAS) AND column_name = 'currency'
      AND (column_default IS NULL OR column_default NOT LIKE '%LKR%');"

step "D-09 — the double-entry ledger"
check_eq "journal_entries.idempotency_key is UNIQUE" "1" \
  "SELECT count(*) FROM pg_constraint
    WHERE conrelid='billing.journal_entries'::regclass AND contype='u'
      AND pg_get_constraintdef(oid) LIKE '%(idempotency_key)%';"
check_eq "journal.kind has no reseller_commission (AL-01)" "0" \
  "SELECT count(*) FROM pg_constraint
    WHERE conrelid='billing.journal_entries'::regclass AND contype='c'
      AND pg_get_constraintdef(oid) LIKE '%reseller%';"
check_eq "accounts.owner_type has no 'reseller' (AL-01)" "0" \
  "SELECT count(*) FROM pg_constraint
    WHERE conrelid='billing.accounts'::regclass AND contype='c'
      AND pg_get_constraintdef(oid) LIKE '%reseller%';"
check_eq "accounts.owner_type is exactly the four AL-01/AL-03 values" "driver,fleet,platform,suspense" \
  "SELECT string_agg(m[1], ',' ORDER BY m[1] COLLATE \"C\")
     FROM pg_constraint c, LATERAL regexp_matches(pg_get_constraintdef(c.oid), '''([a-z_]+)''', 'g') AS m
    WHERE c.conrelid='billing.accounts'::regclass AND c.conname='ck_accounts_owner_type';"
check_eq "trg_balanced is a DEFERRABLE INITIALLY DEFERRED constraint trigger" "1" \
  "SELECT count(*) FROM pg_trigger
    WHERE tgrelid='billing.journal_postings'::regclass AND tgname='trg_balanced'
      AND tgconstraint <> 0 AND tgdeferrable AND tginitdeferred;"

step "R-19 / AL-47 — payment idempotency and driver-QR attestation"
check_eq "ride_payments.provider_transaction_id is UNIQUE" "1" \
  "SELECT count(*) FROM pg_constraint
    WHERE conrelid='fares.ride_payments'::regclass AND contype='u'
      AND pg_get_constraintdef(oid) LIKE '%(provider_transaction_id)%';"
check_eq "ride_payments.state carries both AL-47 attestation states and PartiallyRefunded" "1" \
  "SELECT count(*) FROM pg_constraint
    WHERE conrelid='fares.ride_payments'::regclass AND conname='ck_ride_payments_state'
      AND pg_get_constraintdef(oid) LIKE '%QrClaimedByPassenger%'
      AND pg_get_constraintdef(oid) LIKE '%DriverConfirmedQR%'
      AND pg_get_constraintdef(oid) LIKE '%PartiallyRefunded%';"
check_eq "ride_payments.method is exactly the five AL-22 values" "cash,cod,lankaqr,onepay,scan_driver_qr" \
  "SELECT string_agg(m[1], ',' ORDER BY m[1] COLLATE \"C\")
     FROM pg_constraint c, LATERAL regexp_matches(pg_get_constraintdef(c.oid), '''([a-z_]+)''', 'g') AS m
    WHERE c.conrelid='fares.ride_payments'::regclass AND c.conname='ck_ride_payments_method';"
check_eq "qr_claim_artifact_id references rides.proof_artifacts" "1" \
  "SELECT count(*) FROM pg_constraint
    WHERE conrelid='fares.ride_payments'::regclass AND contype='f'
      AND confrelid='rides.proof_artifacts'::regclass;"

step "AL-54 — GTFS feed lifecycle"
check_eq "ux_gtfs_feed_one_active is a unique partial index on status='active'" "1" \
  "SELECT count(*) FROM pg_indexes
    WHERE schemaname='transit' AND indexname='ux_gtfs_feed_one_active'
      AND indexdef LIKE 'CREATE UNIQUE INDEX%WHERE (status = ''active''::text)%';"
check_eq "gtfs_feed_versions.uploaded_by references iam.users (not the phantom user_id)" "1" \
  "SELECT count(*) FROM pg_constraint
    WHERE conrelid='transit.gtfs_feed_versions'::regclass AND contype='f'
      AND confrelid='iam.users'::regclass;"
# The activation swap renames staging into transit.* wholesale, so any column drift between
# the two sides would corrupt the live feed rather than fail loudly.
check_eq "transit_staging mirrors the five gtfs_* tables column-for-column" "0" \
  "SELECT count(*) FROM (
     SELECT table_name, column_name, data_type FROM information_schema.columns
      WHERE table_schema='transit'
        AND table_name IN ('gtfs_routes','gtfs_trips','gtfs_stops','gtfs_stop_times','gtfs_shapes')
     EXCEPT
     SELECT table_name, column_name, data_type FROM information_schema.columns
      WHERE table_schema='transit_staging') d;"
check_eq "no transit_staging FK points at a live transit table" "0" \
  "SELECT count(*) FROM pg_constraint k
     JOIN pg_class c ON c.oid = k.conrelid
     JOIN pg_namespace n ON n.oid = c.relnamespace
     JOIN pg_class f ON f.oid = k.confrelid
     JOIN pg_namespace fn ON fn.oid = f.relnamespace
    WHERE n.nspname='transit_staging' AND k.contype='f' AND fn.nspname <> 'transit_staging';"

step "Seed data (§20)"
check_eq "six Mode C fare tiers seeded" "6" "SELECT count(*) FROM fares.tariffs;"
check_eq "every Mode-C-bookable type has a tariff (AL-09)" \
  "flex,mini_van,motorbike,sedan,three_wheeler,van" \
  "SELECT string_agg(vehicle_type, ',' ORDER BY vehicle_type COLLATE \"C\") FROM fares.tariffs;"
check_eq "eight daily-fee plan rows across seven rate tiers" "8|7" \
  "SELECT count(*)||'|'||count(DISTINCT daily_fee_minor) FROM billing.plans;"
check_eq "Mode A is free" "0" \
  "SELECT count(*) FROM billing.plans WHERE mode='A' AND daily_fee_minor <> 0;"
check_eq "three peak/night windows seeded" "3" "SELECT count(*) FROM fares.peak_windows;"
check_eq "five voucher denominations seeded (US-9.19)" "5" \
  "SELECT count(*) FROM billing.voucher_discount_tiers;"
check_eq "Rs 1,000 voucher carries the spec's worked 10% rate" "1000" \
  "SELECT discount_bps FROM billing.voucher_discount_tiers WHERE denomination_minor = 100000;"
check_eq "two platform ledger accounts seeded" "2" \
  "SELECT count(*) FROM billing.accounts WHERE owner_id IS NULL;"
check_eq "every seeded notification template exists in all three languages (D-26)" "0" \
  "SELECT count(*) FROM (
     SELECT template_key FROM content.notification_templates
      GROUP BY template_key HAVING count(DISTINCT language) <> 3) t;"
# 1902's four (the keys the specs name by string) plus 1904's eighteen: the rest of D5' §14.4's
# table, seeded beside the service that resolves them (C051).
check_eq "twenty-two notification template keys seeded" "22" \
  "SELECT count(DISTINCT template_key) FROM content.notification_templates;"
check_eq "the E-01 fallback SMS interpolates the two values offer.created carries" "1" \
  "SELECT count(*) FROM content.notification_templates
    WHERE template_key='ride_offer_sms' AND language='en'
      AND body LIKE '%{{fare}}%' AND body LIKE '%{{distance}}%';"
check_eq "every language of a key interpolates the same placeholders (D-26)" "0" \
  "SELECT count(*) FROM (
     SELECT template_key
       FROM (SELECT template_key, language,
                    (SELECT count(*) FROM regexp_matches(body, '\\{\\{[a-z]+\\}\\}', 'g')) AS n
                FROM content.notification_templates) c
      GROUP BY template_key HAVING count(DISTINCT n) <> 1) t;"
check_eq "every FAQ category exists in all three languages (US-16.1, D-26)" "0" \
  "SELECT count(*) FROM (
     SELECT category FROM content.faq_articles
      GROUP BY category HAVING count(DISTINCT language) <> 3) t;"
check_eq "the Sinhala wallet FAQ survived three migration passes intact" "1" \
  "SELECT count(*) FROM content.faq_articles
    WHERE language='si' AND category='wallet' AND title LIKE 'මගේ පසුම්බියට%';"

# ---------------------------------------------------------------------------------------
step "C005 constraints actually bite"

ACC_PLATFORM="$(psql_q "SELECT id FROM billing.accounts WHERE owner_type='platform';")"
ACC_DRIVER='c0000007-0000-0000-0000-000000000001'
ENTRY_BAD='c0000008-0000-0000-0000-000000000001'
ENTRY_OK='c0000008-0000-0000-0000-000000000002'
RIDE_1='c0000004-0000-0000-0000-000000000001'

psql_run "INSERT INTO billing.accounts(id, owner_type, owner_id, currency)
            VALUES ('$ACC_DRIVER','driver','$DRV_A','LKR');" >/dev/null \
  || die "could not seed the ledger fixture account."

# The trigger is DEFERRABLE INITIALLY DEFERRED, so it fires at COMMIT. psql -c runs the
# whole batch in one implicit transaction, which is exactly the shape a real two-leg write
# has: the single posting below is only detectable once the transaction tries to commit.
check_rejects "an unbalanced journal entry is rejected at COMMIT (D-09)" \
  "INSERT INTO billing.journal_entries(id, kind, idempotency_key)
     VALUES ('$ENTRY_BAD','adjustment','verify:unbalanced');
   INSERT INTO billing.journal_postings(entry_id, account_id, amount_minor)
     VALUES ('$ENTRY_BAD','$ACC_PLATFORM',100);"

CHECKS=$((CHECKS + 1))
if psql_run "INSERT INTO billing.journal_entries(id, kind, idempotency_key)
               VALUES ('$ENTRY_OK','topup','verify:balanced');
             INSERT INTO billing.journal_postings(entry_id, account_id, amount_minor)
               VALUES ('$ENTRY_OK','$ACC_PLATFORM',-50000),
                      ('$ENTRY_OK','$ACC_DRIVER', 50000);" >/dev/null 2>&1; then
  printf '  %s✓%s a two-leg entry summing to zero is accepted (D-09)\n' "$GREEN" "$RESET"
else
  printf '  %s✗%s a balanced two-leg entry was rejected\n' "$RED" "$RESET"
  FAILURES=$((FAILURES + 1))
fi

check_rejects "deleting one leg of a balanced entry is rejected (D-09)" \
  "DELETE FROM billing.journal_postings
    WHERE entry_id='$ENTRY_OK' AND amount_minor = 50000;"

check_rejects "a duplicate ledger idempotency_key is rejected (D-09)" \
  "INSERT INTO billing.journal_entries(kind, idempotency_key)
     VALUES ('topup','verify:balanced');"

check_rejects "a second 'platform' ledger account is rejected" \
  "INSERT INTO billing.accounts(owner_type, currency) VALUES ('platform','LKR');"

psql_run "INSERT INTO fares.ride_payments(ride_id, method, amount_minor, provider_transaction_id)
            VALUES ('$RIDE_1','onepay',150000,'OP-VERIFY-0001');" >/dev/null \
  || die "could not insert the fixture ride payment."

# attempt_no = 2 so this is still a test of the provider_transaction_id UNIQUE and not of
# ux_ride_payments_first_attempt (C049's 1006), which would otherwise reject the row first and
# leave this check passing for the wrong reason.
check_rejects "a replayed gateway callback id is rejected (R-19)" \
  "INSERT INTO fares.ride_payments(ride_id, method, amount_minor, attempt_no, provider_transaction_id)
     VALUES ('$RIDE_1','onepay',150000,2,'OP-VERIFY-0001');"

check_rejects "a daily fee waived as the first trip cannot carry an amount (D-13)" \
  "INSERT INTO billing.daily_fee_charges(driver_id, vehicle_id, amount_minor, status)
     VALUES ('$DRV_A','$VEH_A',10000,'WAIVED_FIRST_TRIP');"

CHECKS=$((CHECKS + 1))
if psql_run "INSERT INTO billing.daily_fee_charges(driver_id, vehicle_id, amount_minor)
               VALUES ('$DRV_A','$VEH_A',10000)
             ON CONFLICT (driver_id, vehicle_id, fee_date) DO NOTHING;
             INSERT INTO billing.daily_fee_charges(driver_id, vehicle_id, amount_minor)
               VALUES ('$DRV_A','$VEH_A',10000)
             ON CONFLICT (driver_id, vehicle_id, fee_date) DO NOTHING;" >/dev/null 2>&1 \
   && [[ "$(psql_q "SELECT count(*) FROM billing.daily_fee_charges WHERE driver_id='$DRV_A';")" == "1" ]]; then
  printf '  %s✓%s charging the daily fee twice in one Colombo day is a no-op (D-13)\n' "$GREEN" "$RESET"
else
  printf '  %s✗%s the daily fee is not idempotent per (driver, vehicle, Colombo date)\n' "$RED" "$RESET"
  FAILURES=$((FAILURES + 1))
fi

check_eq "fee_date landed on the Asia/Colombo business date (D-38)" "t" \
  "SELECT fee_date = (now() AT TIME ZONE 'Asia/Colombo')::date
     FROM billing.daily_fee_charges WHERE driver_id='$DRV_A';"

check_rejects "a voucher that credits less than its face value is rejected (US-9.19)" \
  "INSERT INTO billing.voucher_purchases(buyer_id, denomination_minor, discount_bps_applied,
                                          paid_minor, credited_minor)
     VALUES ('$DRV_A',100000,1000,90000,90000);"

check_rejects "a credit transfer to yourself is rejected (AL-01)" \
  "INSERT INTO billing.credit_transfers(sender_driver_id, recipient_driver_id, amount_minor)
     VALUES ('$DRV_A','$DRV_A',50000);"

check_rejects "a monthly subscription period that is not the 1st is rejected (D-38)" \
  "INSERT INTO billing.monthly_subscriptions(vehicle_id, period_month)
     VALUES ('$VEH_A', DATE '2026-08-15');"

check_rejects "a pickup_confirm token with no location request is rejected (AL-44)" \
  "INSERT INTO safety.trip_share_tokens(token, scope, expires_at)
     VALUES ('tok-verify-1','pickup_confirm', now() + interval '5 minutes');"

CHECKS=$((CHECKS + 1))
if psql_run "INSERT INTO safety.trip_share_tokens(token, trip_id, scope, expires_at)
               VALUES ('tok-verify-2','$RIDE_1','proxy_rider', now() + interval '1 hour');" >/dev/null 2>&1; then
  printf '  %s✓%s a proxy_rider token against a trip is accepted (AL-44)\n' "$GREEN" "$RESET"
else
  printf '  %s✗%s a valid proxy_rider token was rejected\n' "$RED" "$RESET"
  FAILURES=$((FAILURES + 1))
fi

check_rejects "an SOS with neither a user nor a share token is rejected (AL-44)" \
  "INSERT INTO safety.sos_events(role, lat, lng) VALUES ('passenger',6.9271,79.8612);"

CHECKS=$((CHECKS + 1))
if psql_run "INSERT INTO safety.sos_events(role, lat, lng, source, share_token)
               VALUES ('passenger',6.9271,79.8612,'web','tok-verify-2');" >/dev/null 2>&1; then
  printf '  %s✓%s a web SOS identified only by share token is accepted (US-25.5)\n' "$GREEN" "$RESET"
else
  printf '  %s✗%s a token-only web SOS was rejected\n' "$RED" "$RESET"
  FAILURES=$((FAILURES + 1))
fi

check_rejects "a passenger blocking themselves is rejected" \
  "INSERT INTO safety.blocked_drivers(passenger_id, driver_id) VALUES ('$PAX_1','$PAX_1');"

check_rejects "a masked call type is rejected — masking was removed (AL-48)" \
  "INSERT INTO comms.call_log(caller_id, callee_role, call_type)
     VALUES ('$PAX_1','driver','normal_masked');"

check_rejects "a broadcast missing a language is rejected (D-26)" \
  "INSERT INTO content.broadcasts(message_by_lang)
     VALUES ('{\"en\":\"Service update\",\"si\":\"සේවා යාවත්කාලීන\"}'::jsonb);"

psql_run "INSERT INTO registry.vehicles(id, owner_id, registration_number, vehicle_type, mode,
                                        driver_name, mode_b_billing)
            VALUES ('c0000003-0000-0000-0000-000000000003','$DRV_B','WP-BUS-0001','bus','B',
                    'Driver B','paid');
          INSERT INTO subscription.access_requests(vehicle_id, passenger_id)
            VALUES ('c0000003-0000-0000-0000-000000000003','$PAX_1');" >/dev/null \
  || die "could not seed the subscription fixtures."

check_rejects "a second open access request for the same (vehicle, passenger) is rejected (AL-23)" \
  "INSERT INTO subscription.access_requests(vehicle_id, passenger_id)
     VALUES ('c0000003-0000-0000-0000-000000000003','$PAX_1');"

check_rejects "a pending access request cannot claim a decision maker" \
  "INSERT INTO subscription.access_requests(vehicle_id, passenger_id, decided_by)
     VALUES ('c0000003-0000-0000-0000-000000000003','$PAX_2','$DRV_B');"

psql_run "INSERT INTO subscription.grants(id, vehicle_id, passenger_id)
            VALUES ('c0000009-0000-0000-0000-000000000001',
                    'c0000003-0000-0000-0000-000000000003','$PAX_1');" >/dev/null \
  || die "could not insert the fixture grant."

check_rejects "a second live grant for the same (vehicle, passenger) is rejected (US-4.11)" \
  "INSERT INTO subscription.grants(vehicle_id, passenger_id)
     VALUES ('c0000003-0000-0000-0000-000000000003','$PAX_1');"

CHECKS=$((CHECKS + 1))
if psql_run "UPDATE subscription.grants
                SET status='unsubscribed', unsubscribed_at=now()
              WHERE id='c0000009-0000-0000-0000-000000000001';
             INSERT INTO subscription.grants(vehicle_id, passenger_id)
               VALUES ('c0000003-0000-0000-0000-000000000003','$PAX_1');" >/dev/null 2>&1; then
  printf '  %s✗%s an unsubscribed grant freed its slot before the owner deleted it\n' "$RED" "$RESET"
  FAILURES=$((FAILURES + 1))
else
  printf '  %s✓%s an unsubscribed grant stays MUTED until the fleet owner deletes it (US-4.12)\n' "$GREEN" "$RESET"
fi

check_rejects "a paid subscription with no fare is rejected (AL-24)" \
  "INSERT INTO subscription.subscriptions(grant_id, vehicle_id, passenger_id, billing, join_day)
     VALUES ('c0000009-0000-0000-0000-000000000001','c0000003-0000-0000-0000-000000000003',
             '$PAX_1','paid',5);"

check_rejects "a join_anniversary cycle with no join day is rejected" \
  "INSERT INTO subscription.subscriptions(grant_id, vehicle_id, passenger_id, billing)
     VALUES ('c0000009-0000-0000-0000-000000000001','c0000003-0000-0000-0000-000000000003',
             '$PAX_1','free');"

psql_run "INSERT INTO transit.gtfs_feed_versions(file_name, file_size_bytes, sha256, storage_key,
                                                  uploaded_by, status, activated_at)
            VALUES ('day0.zip', 1024, 'sha-verify-0001', 's3://gtfs/day0.zip', '$PAX_1',
                    'active', now());" >/dev/null \
  || die "could not insert the fixture GTFS feed version."

check_rejects "a second active GTFS feed version is rejected (AL-54 / BR-32.2)" \
  "INSERT INTO transit.gtfs_feed_versions(file_name, file_size_bytes, sha256, storage_key,
                                          uploaded_by, status, activated_at)
     VALUES ('day1.zip', 2048, 'sha-verify-0002', 's3://gtfs/day1.zip', '$PAX_1',
             'active', now());"

check_rejects "re-uploading an identical GTFS feed is rejected (US-28.1)" \
  "INSERT INTO transit.gtfs_feed_versions(file_name, file_size_bytes, sha256, storage_key, uploaded_by)
     VALUES ('day0-again.zip', 1024, 'sha-verify-0001', 's3://gtfs/day0b.zip', '$PAX_1');"

CHECKS=$((CHECKS + 1))
if psql_run "UPDATE transit.gtfs_feed_versions SET status='archived', archived_at=now()
              WHERE sha256='sha-verify-0001';
             INSERT INTO transit.gtfs_feed_versions(file_name, file_size_bytes, sha256, storage_key,
                                                    uploaded_by, status, activated_at)
               VALUES ('day1.zip', 2048, 'sha-verify-0002', 's3://gtfs/day1.zip', '$PAX_1',
                       'active', now());" >/dev/null 2>&1; then
  printf '  %s✓%s archiving the active feed lets the next one activate (BR-32.3)\n' "$GREEN" "$RESET"
else
  printf '  %s✗%s a new feed could not activate after the previous was archived\n' "$RED" "$RESET"
  FAILURES=$((FAILURES + 1))
fi

check_rejects "a PDPA FulfilledHold with no stated reason is rejected (E-06)" \
  "INSERT INTO pdpa.requests(user_id, kind, status) VALUES ('$PAX_1','erasure','FulfilledHold');"

psql_run "INSERT INTO pdpa.requests(user_id, kind) VALUES ('$PAX_1','export');" >/dev/null \
  || die "could not insert the fixture PDPA request."

check_eq "a PDPA request defaults to the 30-day statutory deadline (E-06)" "30" \
  "SELECT EXTRACT(DAY FROM (due_by - requested_at))::int FROM pdpa.requests
    WHERE user_id='$PAX_1' LIMIT 1;"

# ---------------------------------------------------------------------------------------
# C006 — telemetry (TimescaleDB hypertable, rollups, policies, fleet scoping)
# ---------------------------------------------------------------------------------------
step "Objects owned by C006"
# 1 from C006 (telemetry.positions) + 3 added by C044 (1805): device_health, fleet_health_alerts
# and the outbox. US-3.13's four states are a per-device question a bucketed continuous aggregate
# cannot answer; see 1805's header.
check_eq "4 telemetry tables — 1 hypertable + 3 from C044" "4" \
  "SELECT count(*) FROM information_schema.tables
    WHERE table_schema='telemetry' AND table_type='BASE TABLE';"
check_eq "8 telemetry views — 4 rollups + 4 fleet-scoped" "8" \
  "SELECT count(*) FROM information_schema.views WHERE table_schema='telemetry';"

step "T-06 / ADD §9.5 — the hypertable"
check_eq "telemetry.positions is a hypertable" "1" \
  "SELECT count(*) FROM timescaledb_information.hypertables
    WHERE hypertable_schema='telemetry' AND hypertable_name='positions';"
check_eq "time dimension is sample_ts on 1-day chunks" "sample_ts|1day" \
  "SELECT column_name||'|'||time_interval FROM timescaledb_information.dimensions
    WHERE hypertable_schema='telemetry' AND hypertable_name='positions' AND dimension_type='Time';"
check_eq "space dimension is vehicle_id across 16 partitions" "vehicle_id|16" \
  "SELECT column_name||'|'||num_partitions FROM timescaledb_information.dimensions
    WHERE hypertable_schema='telemetry' AND hypertable_name='positions' AND dimension_type='Space';"
# The specs print UNIQUE (vehicle_id, seq); TimescaleDB requires every partitioning column in
# a unique index, so sample_ts is in the key. See 1801's header and build/progress.md.
check_eq "ux_positions_vehicle_seq is UNIQUE and carries the partitioning column" "1" \
  "SELECT count(*) FROM pg_indexes
    WHERE schemaname='telemetry' AND indexname='ux_positions_vehicle_seq'
      AND indexdef LIKE 'CREATE UNIQUE INDEX%(vehicle_id, seq, sample_ts)%';"
check_eq "ix_positions_fleet_ts is partial on fleet_id IS NOT NULL" "1" \
  "SELECT count(*) FROM pg_indexes
    WHERE schemaname='telemetry' AND indexname='ix_positions_fleet_ts'
      AND indexdef LIKE '%WHERE (fleet_id IS NOT NULL)%';"
check_eq "the per-vehicle and per-trip read indexes exist (ADD §9.5 item 6)" "2" \
  "SELECT count(*) FROM pg_indexes WHERE schemaname='telemetry'
     AND indexname IN ('ix_positions_vehicle_ts','ix_positions_trip_ts');"

step "T-06 — continuous aggregates (1m / 5m / 1h + fleet health)"
check_eq "four continuous aggregates registered" \
  "fleet_health_5m,positions_1h,positions_1m,positions_5m" \
  "SELECT string_agg(view_name, ',' ORDER BY view_name COLLATE \"C\")
     FROM timescaledb_information.continuous_aggregates WHERE view_schema='telemetry';"
check_eq "every rollup reads live rows as well as materialised ones" "0" \
  "SELECT count(*) FROM timescaledb_information.continuous_aggregates
    WHERE view_schema='telemetry' AND materialized_only;"
check_eq "four refresh policies scheduled" "4" \
  "SELECT count(*) FROM timescaledb_information.jobs
    WHERE hypertable_schema='telemetry' AND proc_name='policy_refresh_continuous_aggregate';"
check_eq "positions_1m keeps the spec's 3-hour / 1-minute refresh window" "03:00:00|00:01:00" \
  "SELECT (config->>'start_offset')||'|'||(config->>'end_offset')
     FROM timescaledb_information.jobs
    WHERE hypertable_name='positions_1m' AND proc_name='policy_refresh_continuous_aggregate';"

step "ADD §9.5 items 3 and 4 — compression and retention"
check_eq "compression is segmented by vehicle_id" "vehicle_id" \
  "SELECT attname FROM timescaledb_information.compression_settings
    WHERE hypertable_schema='telemetry' AND hypertable_name='positions'
      AND segmentby_column_index IS NOT NULL;"
check_eq "chunks compress after 7 days" "7days" \
  "SELECT config->>'compress_after' FROM timescaledb_information.jobs
    WHERE hypertable_name='positions' AND proc_name='policy_compression';"
check_eq "raw telemetry is dropped after 30 days" "30days" \
  "SELECT config->>'drop_after' FROM timescaledb_information.jobs
    WHERE hypertable_name='positions' AND proc_name='policy_retention';"
check_eq "all four aggregates are retained 12 months" "4" \
  "SELECT count(*) FROM timescaledb_information.jobs
    WHERE hypertable_schema='telemetry' AND proc_name='policy_retention'
      AND hypertable_name <> 'positions' AND config->>'drop_after' = '1 year';"

# ---------------------------------------------------------------------------------------
step "C006 constraints actually bite"

FLEET_1='e0000001-0000-0000-0000-000000000001'
FLEET_2='e0000001-0000-0000-0000-000000000002'
VEH_T1='e0000002-0000-0000-0000-000000000001'
VEH_T2='e0000002-0000-0000-0000-000000000002'
VEH_T3='e0000002-0000-0000-0000-000000000003'
VEH_T4='e0000002-0000-0000-0000-000000000004'
# Two hours back, so the samples sit inside every refresh window and well inside retention.
TS_BASE="date_trunc('minute', now() - interval '2 hours')"

psql_run "INSERT INTO telemetry.positions(vehicle_id, sample_ts, seq, lat, lng, speed_mps, source, fleet_id)
            VALUES ('$VEH_T1', $TS_BASE,                        1, 6.9271, 79.8612, 8.0, 1, '$FLEET_1'),
                   ('$VEH_T1', $TS_BASE + interval '10 seconds', 2, 6.9272, 79.8613, 12.0, 1, '$FLEET_1'),
                   ('$VEH_T1', $TS_BASE + interval '20 seconds', 3, 6.9273, 79.8614, 10.0, 1, '$FLEET_1'),
                   ('$VEH_T2', $TS_BASE,                        1, 7.2906, 80.6337, 5.0, 2, '$FLEET_1'),
                   ('$VEH_T3', $TS_BASE,                        1, 6.0535, 80.2210, 9.0, 3, '$FLEET_2'),
                   ('$VEH_T4', $TS_BASE,                        1, 6.9271, 79.8612, 0.0, 0, NULL);" >/dev/null \
  || die "could not seed the telemetry fixtures."

check_eq "writing a sample creates a chunk (ADD §9.5 item 1)" "t" \
  "SELECT count(*) > 0 FROM timescaledb_information.chunks
    WHERE hypertable_schema='telemetry' AND hypertable_name='positions';"

check_rejects "a replayed (vehicle_id, seq) sample is rejected (T-05/R-17)" \
  "INSERT INTO telemetry.positions(vehicle_id, sample_ts, seq, lat, lng, source, fleet_id)
     VALUES ('$VEH_T1', $TS_BASE, 1, 6.9271, 79.8612, 1, '$FLEET_1');"

CHECKS=$((CHECKS + 1))
if psql_run "INSERT INTO telemetry.positions(vehicle_id, sample_ts, seq, lat, lng, source, fleet_id)
               VALUES ('$VEH_T2', $TS_BASE + interval '30 seconds', 1, 7.2907, 80.6338, 2, '$FLEET_1');" >/dev/null 2>&1; then
  printf '  %s✓%s seq is monotonic per vehicle, not global — another vehicle may reuse it (T-05)\n' "$GREEN" "$RESET"
else
  printf '  %s✗%s a second vehicle could not reuse a sequence number\n' "$RED" "$RESET"
  FAILURES=$((FAILURES + 1))
fi

check_rejects "a sample outside the latitude range is rejected" \
  "INSERT INTO telemetry.positions(vehicle_id, sample_ts, seq, lat, lng, source)
     VALUES ('$VEH_T4', $TS_BASE + interval '1 minute', 99, 999.0, 79.8612, 0);"

check_rejects "an unknown tracker protocol is rejected (server_db_schema §18)" \
  "INSERT INTO telemetry.positions(vehicle_id, sample_ts, seq, lat, lng, source)
     VALUES ('$VEH_T4', $TS_BASE + interval '1 minute', 98, 6.9271, 79.8612, 9);"

step "T-06 — the aggregates refresh and answer queries"
# refresh_continuous_aggregate is a procedure and cannot run inside a transaction block, so
# each CALL is its own statement.
for CAGG in positions_1m positions_5m positions_1h fleet_health_5m; do
  psql_run "CALL refresh_continuous_aggregate('telemetry.$CAGG', now() - interval '1 day', now() - interval '1 hour');" >/dev/null \
    || die "could not refresh telemetry.$CAGG."
done

check_eq "all four aggregates materialised at least one chunk" "4" \
  "SELECT count(*) FROM timescaledb_information.continuous_aggregates ca
    WHERE ca.view_schema='telemetry'
      AND EXISTS (SELECT 1 FROM timescaledb_information.chunks c
                   WHERE c.hypertable_schema = ca.materialization_hypertable_schema
                     AND c.hypertable_name   = ca.materialization_hypertable_name);"
check_eq "positions_1m rolled the three samples into one bucket" "3|12" \
  "SELECT samples||'|'||max_speed::int FROM telemetry.positions_1m
    WHERE vehicle_id='$VEH_T1';"
check_eq "positions_1m carries the last fix in the bucket" "6.9273|79.8614" \
  "SELECT round(last_lat::numeric,4)||'|'||round(last_lng::numeric,4)
     FROM telemetry.positions_1m WHERE vehicle_id='$VEH_T1';"
check_eq "positions_5m and positions_1h agree on the sample count" "3|3" \
  "SELECT (SELECT samples FROM telemetry.positions_5m WHERE vehicle_id='$VEH_T1')||'|'||
          (SELECT samples FROM telemetry.positions_1h WHERE vehicle_id='$VEH_T1');"
check_eq "fleet_health_5m counts two distinct vehicles for fleet 1 (US-3.13)" "2|5" \
  "SELECT active_vehicles||'|'||samples FROM telemetry.fleet_health_5m
    WHERE fleet_id='$FLEET_1';"
check_eq "fleet_health_5m ignores vehicles that belong to no fleet" "0" \
  "SELECT count(*) FROM telemetry.fleet_health_5m WHERE fleet_id IS NULL;"

step "ADD §9.5 item 8 — a fleet reader sees only its own telemetry"
psql_run "CREATE ROLE $FLEET_ROLE LOGIN;
          GRANT mageride_fleet_reader TO $FLEET_ROLE;" >/dev/null \
  || die "could not create the fleet-scoped verify role."

RAW_CHUNK="$(psql_q "SELECT format('%I.%I', chunk_schema, chunk_name)
                       FROM timescaledb_information.chunks
                      WHERE hypertable_schema='telemetry' AND hypertable_name='positions' LIMIT 1;")"
[[ -n "$RAW_CHUNK" ]] || die "no telemetry.positions chunk to test chunk-level access against."

check_fleet_denied "the fleet role cannot read telemetry.positions directly" \
  "SELECT count(*) FROM telemetry.positions;"
# A grant on the hypertable propagates to its chunks, so the chunk is the obvious way around a
# view. It has to be denied too, or the whole fleet fence is decorative.
check_fleet_denied "the fleet role cannot reach around the view into a chunk ($RAW_CHUNK)" \
  "SELECT count(*) FROM $RAW_CHUNK;"
check_fleet_denied "the fleet role cannot read the vehicle-keyed rollups" \
  "SELECT count(*) FROM telemetry.positions_1m;"

check_fleet_eq "an unscoped session sees no telemetry at all (fail closed)" "0" "" \
  "SELECT count(*) FROM telemetry.positions_fleet;"
check_fleet_eq "fleet 1 sees exactly its own five samples" "5" "$FLEET_1" \
  "SELECT count(*) FROM telemetry.positions_fleet;"
check_fleet_eq "fleet 1 sees exactly its own two vehicles" "$VEH_T1,$VEH_T2" "$FLEET_1" \
  "SELECT string_agg(DISTINCT vehicle_id::text, ',' ORDER BY vehicle_id::text) FROM telemetry.positions_fleet;"
check_fleet_eq "fleet 2's vehicle is invisible to fleet 1 (cross-fleet read blocked)" "0" "$FLEET_1" \
  "SELECT count(*) FROM telemetry.positions_fleet WHERE vehicle_id='$VEH_T3';"
check_fleet_eq "fleet 2 sees only its own sample" "$VEH_T3" "$FLEET_2" \
  "SELECT string_agg(DISTINCT vehicle_id::text, ',') FROM telemetry.positions_fleet;"
check_fleet_eq "a vehicle owned by no fleet is invisible to every fleet" "0" "$FLEET_2" \
  "SELECT count(*) FROM telemetry.positions_fleet WHERE vehicle_id='$VEH_T4';"
check_fleet_eq "the fleet health rollup is scoped the same way" "$FLEET_1" "$FLEET_1" \
  "SELECT string_agg(DISTINCT fleet_id::text, ',') FROM telemetry.fleet_health_5m_fleet;"

# ---------------------------------------------------------------------------------------
# C044 — telemetry: per-device health, the fleet threshold alert, this plane's outbox (1805)
# ---------------------------------------------------------------------------------------
step "Objects owned by C044 (US-3.13, US-3.16)"
for t in device_health fleet_health_alerts outbox; do
  check_eq "telemetry.$t exists" "1" \
    "SELECT count(*) FROM information_schema.tables
      WHERE table_schema='telemetry' AND table_name='$t';"
done
check_eq "the four-state classifier is one IMMUTABLE SQL function" "sql|i" \
  "SELECT l.lanname||'|'||p.provolatile::text FROM pg_proc p
     JOIN pg_language l ON l.oid = p.prolang
     JOIN pg_namespace n ON n.oid = p.pronamespace
    WHERE n.nspname='telemetry' AND p.proname='device_health_state';"

step "US-3.13 — the state ladder is decided in the database"
# The thresholds are parameters, not literals: Health:StaleAfter / Health:OfflineAfter. `at` is a
# parameter too, which is what makes the function immutable and the same expression usable by both
# the dashboard read and fleet-health-svc's transition sweep.
ladder() { # ladder <last_ping_at> <last_status> <last_status_at> [binding_state]
  printf "SELECT telemetry.device_health_state('%s', NULL, %s, %s, %s, interval '5 minutes', interval '30 minutes', '2026-07-30T09:00:00Z'::timestamptz);" \
    "${4:-ACTIVE}" "$1" "$2" "$3"
}

check_eq "a ping 1 min ago is ONLINE" "ONLINE" \
  "$(ladder "'2026-07-30T08:59:00Z'::timestamptz" NULL NULL)"
check_eq "no ping > 5 min is STALE" "STALE" \
  "$(ladder "'2026-07-30T08:54:00Z'::timestamptz" NULL NULL)"
check_eq "no ping > 30 min is OFFLINE" "OFFLINE" \
  "$(ladder "'2026-07-30T08:29:00Z'::timestamptz" NULL NULL)"
check_eq "a tracker that never reported is OFFLINE, not ONLINE" "OFFLINE" \
  "$(ladder NULL NULL NULL)"
# R-15/T-04: the broker has said the session is gone, so it cannot be ONLINE — but US-3.13 defines
# OFFLINE as thirty minutes of silence and a bus in a tunnel is not a device failure.
check_eq "a last will after the last ping is STALE, not OFFLINE" "STALE" \
  "$(ladder "'2026-07-30T08:59:00Z'::timestamptz" "'offline'" "'2026-07-30T08:59:30Z'::timestamptz")"
check_eq "a ping after the last will clears it with no 'online' message" "ONLINE" \
  "$(ladder "'2026-07-30T08:59:30Z'::timestamptz" "'offline'" "'2026-07-30T08:59:00Z'::timestamptz")"
# US-3.8 against T-08: a revoked credential is retired, a quarantine is held pending US-3.4's admin
# decision and may return, so only the first is DECOMMISSIONED.
check_eq "a REVOKED binding is DECOMMISSIONED even while it is publishing" "DECOMMISSIONED" \
  "$(ladder "'2026-07-30T09:00:00Z'::timestamptz" NULL NULL REVOKED)"
check_eq "a QUARANTINED binding is not decommissioned" "ONLINE" \
  "$(ladder "'2026-07-30T09:00:00Z'::timestamptz" NULL NULL QUARANTINED)"

step "US-3.16 — one alert per fleet per window"
psql_run "INSERT INTO telemetry.device_health(vehicle_id, fleet_id, imei, last_ping_at)
            VALUES ('$VEH_T1','$FLEET_1','864000000000001', now());
          INSERT INTO telemetry.fleet_health_alerts
            (fleet_id, bucket, window_minutes, expected_vehicles, reporting_vehicles,
             offline_vehicles, offline_pct, threshold_pct)
            VALUES ('44444444-4444-4444-4444-444444444444','2026-07-30T09:00:00Z',5,100,90,10,10.00,10.00);" \
  >/dev/null || die "could not seed the C044 fixtures."

check_rejects "a second alert for the same (fleet, window) is rejected" \
  "INSERT INTO telemetry.fleet_health_alerts
     (fleet_id, bucket, window_minutes, expected_vehicles, reporting_vehicles,
      offline_vehicles, offline_pct, threshold_pct)
     VALUES ('44444444-4444-4444-4444-444444444444','2026-07-30T09:00:00Z',5,100,90,10,10.00,10.00);"
check_rejects "an alert claiming more offline than the fleet holds is rejected" \
  "INSERT INTO telemetry.fleet_health_alerts
     (fleet_id, bucket, window_minutes, expected_vehicles, reporting_vehicles,
      offline_vehicles, offline_pct, threshold_pct)
     VALUES ('44444444-4444-4444-4444-444444444444','2026-07-30T09:05:00Z',5,10,0,11,110.00,10.00);"
check_rejects "a battery percentage outside 0-100 is rejected" \
  "UPDATE telemetry.device_health SET battery_pct = 255 WHERE vehicle_id='$VEH_T1';"
check_rejects "an unknown health state is rejected" \
  "UPDATE telemetry.device_health SET observed_state = 'DEGRADED' WHERE vehicle_id='$VEH_T1';"
check_rejects "an unknown presence payload is rejected" \
  "UPDATE telemetry.device_health SET last_status = 'maybe' WHERE vehicle_id='$VEH_T1';"

step "ADD §7.7.7 — a fleet operator sees only its own devices and alerts"
check_fleet_denied "the fleet role cannot read telemetry.device_health directly" \
  "SELECT count(*) FROM telemetry.device_health;"
check_fleet_denied "the fleet role cannot read telemetry.fleet_health_alerts directly" \
  "SELECT count(*) FROM telemetry.fleet_health_alerts;"
check_fleet_eq "an unscoped session sees no device health at all (fail closed)" "0" "" \
  "SELECT count(*) FROM telemetry.device_health_fleet;"
check_fleet_eq "fleet 1 sees its own device" "1" "$FLEET_1" \
  "SELECT count(*) FROM telemetry.device_health_fleet;"
check_fleet_eq "fleet 2 sees none of fleet 1's devices" "0" "$FLEET_2" \
  "SELECT count(*) FROM telemetry.device_health_fleet;"
check_fleet_eq "the alerted fleet sees its own alert" "1" "44444444-4444-4444-4444-444444444444" \
  "SELECT count(*) FROM telemetry.fleet_health_alerts_fleet;"
check_fleet_eq "another fleet sees none of it" "0" "$FLEET_1" \
  "SELECT count(*) FROM telemetry.fleet_health_alerts_fleet;"

# ---------------------------------------------------------------------------------------
# C045 — content: the publishing workflow, the broadcast window, the carousel (1307, 1903)
# ---------------------------------------------------------------------------------------
step "Objects owned by C045 (D-26, AL-28)"
check_eq "content.onboarding_slides exists" "1" \
  "SELECT count(*) FROM information_schema.tables
    WHERE table_schema='content' AND table_name='onboarding_slides';"
check_eq "the trilingual template rule is a DEFERRED constraint trigger" "1" \
  "SELECT count(*) FROM pg_trigger
    WHERE tgname='trg_notification_templates_trilingual' AND tgdeferrable AND tginitdeferred;"
for c in status approved_at created_by; do
  check_eq "content.notification_templates.$c exists" "1" \
    "SELECT count(*) FROM information_schema.columns
      WHERE table_schema='content' AND table_name='notification_templates' AND column_name='$c';"
done
check_eq "content.broadcasts carries the other end of the window" "2" \
  "SELECT count(*) FROM information_schema.columns
    WHERE table_schema='content' AND table_name='broadcasts'
      AND column_name IN ('ends_at','created_by');"
check_eq "content.command_log exists (R-14, one per bounded context)" "1" \
  "SELECT count(*) FROM information_schema.tables
    WHERE table_schema='content' AND table_name='command_log';"
check_eq "content.command_log has no aggregate-id column" "0" \
  "SELECT count(*) FROM information_schema.columns
    WHERE table_schema='content' AND table_name='command_log'
      AND column_name IN ('ride_id','broadcast_id');"
check_eq "the JSONB trilingual test is one IMMUTABLE SQL function" "sql|i" \
  "SELECT l.lanname||'|'||p.provolatile::text FROM pg_proc p
     JOIN pg_language l ON l.oid = p.prolang
     JOIN pg_namespace n ON n.oid = p.pronamespace
    WHERE n.nspname='content' AND p.proname='is_trilingual_text';"

step "AL-28 — the first-run carousel is seeded, three slides per audience"
check_eq "3 driver + 3 passenger slides" "driver=3,passenger=3" \
  "SELECT string_agg(audience||'='||n, ',' ORDER BY audience) FROM (
     SELECT audience, count(*)::text AS n FROM content.onboarding_slides
      WHERE is_active GROUP BY audience) s;"
check_eq "every slide carries a headline and a body in all three languages (D-26)" "0" \
  "SELECT count(*) FROM content.onboarding_slides
    WHERE NOT (title_by_lang ?& array['si','ta','en'])
       OR NOT (body_by_lang ?& array['si','ta','en']);"
check_eq "the Sinhala driver wallet slide survived three migration passes intact" "1" \
  "SELECT count(*) FROM content.onboarding_slides
    WHERE audience='driver' AND slot=3 AND title_by_lang->>'si' LIKE 'එක් පසුම්බියක්%';"
check_rejects "a fourth slide cannot take an occupied slot" \
  "INSERT INTO content.onboarding_slides(audience, slot, illustration_ref, title_by_lang, body_by_lang)
     VALUES ('driver', 1, 'x', '{\"si\":\"a\",\"ta\":\"b\",\"en\":\"c\"}'::jsonb,
                             '{\"si\":\"a\",\"ta\":\"b\",\"en\":\"c\"}'::jsonb);"
check_rejects "a slide missing Tamil is rejected (D-26)" \
  "INSERT INTO content.onboarding_slides(audience, slot, illustration_ref, title_by_lang, body_by_lang)
     VALUES ('passenger', 9, 'x', '{\"si\":\"a\",\"en\":\"c\"}'::jsonb,
                                  '{\"si\":\"a\",\"ta\":\"b\",\"en\":\"c\"}'::jsonb);"
# `?&` (C005's form) admits these three: the keys are present and the *values* are not strings a
# reader can show anybody. content.is_trilingual_text is what closes it.
check_rejects "a slide with a blank language is rejected, not just a missing key" \
  "INSERT INTO content.onboarding_slides(audience, slot, illustration_ref, title_by_lang, body_by_lang)
     VALUES ('passenger', 9, 'x', '{\"si\":\"  \",\"ta\":\"b\",\"en\":\"c\"}'::jsonb,
                                  '{\"si\":\"a\",\"ta\":\"b\",\"en\":\"c\"}'::jsonb);"
check_rejects "a slide with a null language is rejected" \
  "INSERT INTO content.onboarding_slides(audience, slot, illustration_ref, title_by_lang, body_by_lang)
     VALUES ('passenger', 9, 'x', '{\"si\":null,\"ta\":\"b\",\"en\":\"c\"}'::jsonb,
                                  '{\"si\":\"a\",\"ta\":\"b\",\"en\":\"c\"}'::jsonb);"
check_rejects "a slide whose language is a number is rejected" \
  "INSERT INTO content.onboarding_slides(audience, slot, illustration_ref, title_by_lang, body_by_lang)
     VALUES ('passenger', 9, 'x', '{\"si\":1,\"ta\":\"b\",\"en\":\"c\"}'::jsonb,
                                  '{\"si\":\"a\",\"ta\":\"b\",\"en\":\"c\"}'::jsonb);"
check_rejects "a broadcast with a blank language is rejected (ck_broadcasts_trilingual_strict)" \
  "INSERT INTO content.broadcasts(message_by_lang)
     VALUES ('{\"si\":\"\",\"ta\":\"b\",\"en\":\"c\"}'::jsonb);"

step "C045 constraints actually bite"
check_eq "every seeded template version is published with an approval timestamp" "0" \
  "SELECT count(*) FROM content.notification_templates
    WHERE status <> 'published' OR approved_at IS NULL;"
# The fence, at COMMIT: two of three languages is not a template. psql -c runs one implicit
# transaction, so the deferred trigger fires before it returns.
check_rejects "a template published in two languages is rejected at COMMIT (D-26)" \
  "INSERT INTO content.notification_templates(template_key, language, body)
     VALUES ('verify_partial','en','Two of three'), ('verify_partial','si','තුනෙන් දෙක');"
check_eq "and it left nothing behind" "0" \
  "SELECT count(*) FROM content.notification_templates WHERE template_key='verify_partial';"
psql_run "INSERT INTO content.notification_templates(template_key, language, body) VALUES
            ('verify_full','en','All three'), ('verify_full','si','තුනම'), ('verify_full','ta','மூன்றும்');" \
  >/dev/null || die "a trilingual template insert was rejected; the deferred trigger is too strict."
check_eq "all three languages together are accepted" "3" \
  "SELECT count(*) FROM content.notification_templates WHERE template_key='verify_full';"
check_rejects "dropping one language of a published version is rejected (D-26)" \
  "DELETE FROM content.notification_templates WHERE template_key='verify_full' AND language='ta';"
psql_run "DELETE FROM content.notification_templates WHERE template_key='verify_full';" >/dev/null \
  || die "withdrawing a whole template version was rejected; the trigger must allow 0 languages."
check_eq "withdrawing the whole version is allowed" "0" \
  "SELECT count(*) FROM content.notification_templates WHERE template_key='verify_full';"
# The hole a NEW-only trigger would leave: move one language's row to a fresh version and fill that
# version up, and the version it came *from* is left with two languages. Both pairs are checked.
psql_run "INSERT INTO content.notification_templates(template_key, language, body) VALUES
            ('verify_move','en','a'), ('verify_move','si','b'), ('verify_move','ta','c');" >/dev/null \
  || die "could not seed the version-move fixture."
check_rejects "moving one language to another version cannot leave the old version partial (D-26)" \
  "UPDATE content.notification_templates SET version = 2
     WHERE template_key='verify_move' AND language='ta';
   INSERT INTO content.notification_templates(template_key, language, body, version) VALUES
     ('verify_move','si','b2',2), ('verify_move','en','a2',2);"
check_eq "and the original version still has all three languages" "3" \
  "SELECT count(*) FROM content.notification_templates
    WHERE template_key='verify_move' AND version = 1;"
psql_run "DELETE FROM content.notification_templates WHERE template_key='verify_move';" >/dev/null \
  || die "could not clean up the version-move fixture."

check_rejects "a published version with no approval timestamp is rejected" \
  "INSERT INTO content.notification_templates(template_key, language, body, status, approved_at)
     VALUES ('verify_unapproved','en','x','published',NULL);"
check_rejects "an unknown template status is rejected" \
  "INSERT INTO content.notification_templates(template_key, language, body, status)
     VALUES ('verify_status','en','x','live');"
check_rejects "a broadcast whose window ends before it starts is rejected" \
  "INSERT INTO content.broadcasts(message_by_lang, scheduled_at, ends_at)
     VALUES ('{\"si\":\"a\",\"ta\":\"b\",\"en\":\"c\"}'::jsonb,
             '2026-08-01T00:00:00Z','2026-07-01T00:00:00Z');"

# ---------------------------------------------------------------------------------------
# C046 — billing: top-up sessions, this plane's outbox, the replay log (1107)
# ---------------------------------------------------------------------------------------
step "Objects owned by C046 (D-09, D-12, R-19, AL-05)"
for t in topups outbox command_log; do
  check_eq "billing.$t exists" "1" \
    "SELECT count(*) FROM information_schema.tables
      WHERE table_schema='billing' AND table_name='$t';"
done
check_eq "billing.command_log has no aggregate-id column" "0" \
  "SELECT count(*) FROM information_schema.columns
    WHERE table_schema='billing' AND table_name='command_log'
      AND column_name IN ('ride_id','topup_id');"
check_eq "R-19: provider_transaction_id is unique where present" "1" \
  "SELECT count(*) FROM pg_indexes
    WHERE schemaname='billing' AND indexname='ux_topups_provider_txn';"

step "AL-05 — bank transfer is not a top-up method, anywhere"
check_eq "no billing table mentions bank transfer" "0" \
  "SELECT count(*) FROM information_schema.tables
    WHERE table_schema='billing' AND table_name LIKE '%bank%';"
check_eq "no billing column mentions bank transfer" "0" \
  "SELECT count(*) FROM information_schema.columns
    WHERE table_schema='billing' AND column_name LIKE '%bank%';"
check_rejects "a bank-transfer top-up method is rejected by the database (AL-05)" \
  "INSERT INTO billing.topups(driver_id, account_id, method, amount_minor)
     SELECT '$DRV_B', a.id, 'bank_transfer', 100000
       FROM billing.accounts a WHERE a.owner_type='platform' LIMIT 1;"

step "C046 constraints actually bite"
psql_run "INSERT INTO billing.accounts(owner_type, owner_id, currency) VALUES ('driver','$DRV_B','LKR')
            ON CONFLICT DO NOTHING;" >/dev/null || die "could not seed the C046 wallet fixture."
TOPUP_ACCOUNT="$(psql_q "SELECT id FROM billing.accounts WHERE owner_type='driver' AND owner_id='$DRV_B';")"
check_rejects "a Pending top-up cannot carry a ledger entry" \
  "INSERT INTO billing.topups(driver_id, account_id, method, amount_minor, journal_entry_id)
     SELECT '$DRV_B','$TOPUP_ACCOUNT','onepay',100000, e.id
       FROM billing.journal_entries e LIMIT 1;"
check_rejects "a settled top-up must carry a settlement instant" \
  "INSERT INTO billing.topups(driver_id, account_id, method, amount_minor, state)
     VALUES ('$DRV_B','$TOPUP_ACCOUNT','onepay',100000,'Succeeded');"
check_rejects "a zero-amount top-up is rejected" \
  "INSERT INTO billing.topups(driver_id, account_id, method, amount_minor)
     VALUES ('$DRV_B','$TOPUP_ACCOUNT','onepay',0);"
psql_run "INSERT INTO billing.topups(id, driver_id, account_id, method, amount_minor, provider_transaction_id)
            VALUES ('c0000046-0000-0000-0000-000000000001','$DRV_B','$TOPUP_ACCOUNT','onepay',100000,'onepay-verify-1');" \
  >/dev/null || die "could not seed the R-19 top-up fixture."
check_rejects "a second top-up cannot claim the same provider_transaction_id (R-19)" \
  "INSERT INTO billing.topups(driver_id, account_id, method, amount_minor, provider_transaction_id)
     VALUES ('$DRV_B','$TOPUP_ACCOUNT','lankaqr',100000,'onepay-verify-1');"
psql_run "DELETE FROM billing.topups WHERE provider_transaction_id='onepay-verify-1';" >/dev/null \
  || die "could not clean up the R-19 top-up fixture."

# ---------------------------------------------------------------------------------------
# C047 — subscription: the R-14 replay log (1203), plus the two fences the database holds for
#        this component that no earlier section asserts
# ---------------------------------------------------------------------------------------
step "Objects owned by C047 (D-13, AL-03, AL-09)"
check_eq "subscription.command_log exists" "1" \
  "SELECT count(*) FROM information_schema.tables
    WHERE table_schema='subscription' AND table_name='command_log';"
check_eq "subscription.command_log has no aggregate-id column" "0" \
  "SELECT count(*) FROM information_schema.columns
    WHERE table_schema='subscription' AND table_name='command_log'
      AND column_name IN ('ride_id','ticket_id');"
# The replay log is per bounded context precisely so two services' keys cannot collide: a client's
# key against subscription-svc must not replay wallet-svc's stored response.
check_eq "subscription.command_log is keyed on the idempotency key alone" "1" \
  "SELECT count(*) FROM information_schema.key_column_usage k
     JOIN information_schema.table_constraints c USING (constraint_schema, constraint_name)
    WHERE c.table_schema='subscription' AND c.table_name='command_log'
      AND c.constraint_type='PRIMARY KEY' AND k.column_name='idempotency_key';"

# §20 seeds no rate for the package-delivery types on purpose: a delivery vehicle cannot go online
# until Finance decides what it costs, and subscription-svc answers 404 rather than inventing one.
check_eq "no daily-fee rate is seeded for truck or mini_truck (§20, AL-09)" "0" \
  "SELECT count(*) FROM billing.plans WHERE vehicle_type IN ('truck','mini_truck');"

step "AL-03 — the Mode B monthly platform charge, and its first free month"
check_rejects "a FREE month cannot carry an amount" \
  "INSERT INTO billing.monthly_subscriptions(vehicle_id, period_month, amount_minor, status)
     VALUES ('$VEH_A','2026-07-01', 30000, 'FREE');"
# The platform's Mode B fee is ledgered through the consolidated invoice; the per-vehicle row is
# deliberately unledgered, which is what keeps subscription-svc's charge (C047) distinct from both
# fleet-billing-svc's posting (C060) and C048's pass-through, which is never ledgered at all (§18b).
check_eq "billing.monthly_subscriptions has no journal_entry_id" "0" \
  "SELECT count(*) FROM information_schema.columns
    WHERE table_schema='billing' AND table_name='monthly_subscriptions'
      AND column_name='journal_entry_id';"
check_eq "billing.fleet_invoices does have one" "1" \
  "SELECT count(*) FROM information_schema.columns
    WHERE table_schema='billing' AND table_name='fleet_invoices'
      AND column_name='journal_entry_id';"
check_eq "'daily_fee' is a journal kind wallet-svc will accept" "1" \
  "SELECT count(*) FROM pg_constraint
    WHERE conrelid='billing.journal_entries'::regclass AND contype='c'
      AND pg_get_constraintdef(oid) LIKE '%daily_fee%';"

# ---------------------------------------------------------------------------------------
# C049 — fares: the replay log (1005) and the one-fare-per-ride invariant (1006)
# ---------------------------------------------------------------------------------------
step "Objects owned by C049 (E-04, D-05, D-10, AL-19)"
check_eq "fares.command_log exists" "1" \
  "SELECT count(*) FROM information_schema.tables
    WHERE table_schema='fares' AND table_name='command_log';"
check_eq "fares.command_log has no aggregate-id column" "0" \
  "SELECT count(*) FROM information_schema.columns
    WHERE table_schema='fares' AND table_name='command_log' AND column_name IN ('ride_id','payment_id');"

# A ride is priced once. The index is partial on attempt_no = 1 because D-10's retry chain
# deliberately puts several attempts on one ride — a plain UNIQUE on ride_id would forbid the retry
# the payment machine depends on.
check_eq "ux_ride_payments_first_attempt is partial on the first attempt" "1" \
  "SELECT count(*) FROM pg_indexes
    WHERE schemaname='fares' AND indexname='ux_ride_payments_first_attempt'
      AND indexdef LIKE '%attempt_no = 1%';"

# $RIDE_1 already carries the attempt-1 payment the C005 section inserted, so the invariant is
# tested against a row that is already there rather than against one this section seeds.
check_rejects "a ride cannot be priced twice (ux_ride_payments_first_attempt)" \
  "INSERT INTO fares.ride_payments(ride_id, method, amount_minor)
     VALUES ('$RIDE_1','cash',50000);"
# …but the D-10 retry chain still works, because a retry is attempt 2 and outside the predicate.
check_eq "a retry attempt is still allowed on the same ride" "1" \
  "WITH retry AS (
     INSERT INTO fares.ride_payments(ride_id, method, amount_minor, attempt_no)
     VALUES ('$RIDE_1','cash',50000,2)
     RETURNING 1)
   SELECT count(*) FROM retry;"

# AL-19 / D5' §1.1: the rate card is versioned, never mutated, so a completed ride stays
# reconcilable against the rate that priced it.
check_eq "fares.tariffs is versioned by effective_from" "1" \
  "SELECT count(*) FROM pg_constraint
    WHERE conrelid='fares.tariffs'::regclass AND conname='ux_tariffs_type_effective';"
check_rejects "one vehicle type cannot have two tariffs at one instant" \
  "INSERT INTO fares.tariffs(vehicle_type, first_km_minor, per_km_minor, effective_from)
     VALUES ('sedan', 99999, 99999, 'epoch'::timestamptz);"
# §20 seeds no delivery rate on purpose: Epic 20 configures them before such a vehicle is booked.
check_eq "no Mode C tariff is seeded for truck or mini_truck (§20, Epic 20)" "0" \
  "SELECT count(*) FROM fares.tariffs WHERE vehicle_type IN ('truck','mini_truck');"
# The night window wraps midnight, which is why 1001 declines to CHECK the ordering.
check_eq "the seeded night window wraps midnight" "1" \
  "SELECT count(*) FROM fares.peak_windows WHERE kind='night' AND end_local < start_local;"

# ---------------------------------------------------------------------------------------
# C048 — subscription: the Epic 23 outbox (1204) and the §18b fences the database holds
# ---------------------------------------------------------------------------------------
step "Objects owned by C048 (AL-23, AL-24, AL-25, AL-49, §18b)"
check_eq "subscription.outbox exists" "1" \
  "SELECT count(*) FROM information_schema.tables
    WHERE table_schema='subscription' AND table_name='outbox';"
# Same shape as registry.outbox (0309) because it publishes onto the same topic with the same
# partition key: fanout-svc's consumer cannot tell the two producers apart, and must not have to.
check_eq "subscription.outbox is shaped like registry.outbox" "5" \
  "SELECT count(*) FROM information_schema.columns
    WHERE table_schema='subscription' AND table_name='outbox'
      AND column_name IN ('aggregate_id','event_type','payload','created_at','dispatched_at');"

# THE FENCE. §18b: this money is a pass-through to the fleet owner. There is no column here that
# could hold a posting id, so no amount of future code can net it against the platform's own fee.
check_eq "subscription.payments has no journal_entry_id and no commission column" "0" \
  "SELECT count(*) FROM information_schema.columns
    WHERE table_schema='subscription' AND table_name='payments'
      AND column_name IN ('journal_entry_id','commission_minor','platform_fee_minor');"

# AL-25: an unsubscribed grant keeps the (vehicle, passenger) slot until the OWNER deletes it,
# which is what makes the roster row survive as MUTED and what a rejoin reuses.
check_eq "ux_grant_active is partial on deleted_at, not on status" "1" \
  "SELECT count(*) FROM pg_indexes
    WHERE schemaname='subscription' AND indexname='ux_grant_active'
      AND indexdef LIKE '%deleted_at IS NULL%' AND indexdef NOT LIKE '%status%';"

psql_run "INSERT INTO subscription.grants(id, vehicle_id, passenger_id)
            VALUES ('c0000048-0000-0000-0000-000000000001','$VEH_A','$PAX_1');" >/dev/null \
  || die "could not seed the Epic 23 grant fixture."
check_rejects "a second live grant cannot be issued for the same (vehicle, passenger)" \
  "INSERT INTO subscription.grants(vehicle_id, passenger_id) VALUES ('$VEH_A','$PAX_1');"

# A Paid subscription with no fare would bill nothing for ever, so the accept refuses the vehicle
# rather than writing one — and the database refuses it too.
check_rejects "a paid subscription cannot exist without a monthly fare (ck_subscriptions_fare)" \
  "INSERT INTO subscription.subscriptions(grant_id, vehicle_id, passenger_id, billing, join_day)
     VALUES ('c0000048-0000-0000-0000-000000000001','$VEH_A','$PAX_1','paid', 5);"
check_rejects "a join_anniversary cycle cannot exist without the day it anniversaries on" \
  "INSERT INTO subscription.subscriptions(grant_id, vehicle_id, passenger_id, billing, cycle)
     VALUES ('c0000048-0000-0000-0000-000000000001','$VEH_A','$PAX_1','free','join_anniversary');"

psql_run "INSERT INTO subscription.subscriptions
            (id, grant_id, vehicle_id, passenger_id, billing, monthly_fare_minor, cycle, join_day, next_due)
          VALUES ('c0000048-0000-0000-0000-000000000002','c0000048-0000-0000-0000-000000000001',
                  '$VEH_A','$PAX_1','paid',250000,'join_anniversary',5,'2026-07-06');" >/dev/null \
  || die "could not seed the Epic 23 subscription fixture."

check_rejects "a payment period must be the first of a month (D-38)" \
  "INSERT INTO subscription.payments(subscription_id, vehicle_id, passenger_id, period_month,
                                     amount_minor, method)
     VALUES ('c0000048-0000-0000-0000-000000000002','$VEH_A','$PAX_1','2026-07-06',250000,'cash');"
check_rejects "a paid payment must say when it was paid (ck_subscription_payments_paid_at)" \
  "INSERT INTO subscription.payments(subscription_id, vehicle_id, passenger_id, period_month,
                                     amount_minor, method, status)
     VALUES ('c0000048-0000-0000-0000-000000000002','$VEH_A','$PAX_1','2026-07-01',250000,'cash','paid');"

psql_run "INSERT INTO subscription.payments(subscription_id, vehicle_id, passenger_id, period_month,
                                            amount_minor, method)
            VALUES ('c0000048-0000-0000-0000-000000000002','$VEH_A','$PAX_1','2026-07-01',250000,'onepay');" \
  >/dev/null || die "could not seed the Epic 23 payment fixture."
check_rejects "a month cannot carry two live payments (ux_subpay_period)" \
  "INSERT INTO subscription.payments(subscription_id, vehicle_id, passenger_id, period_month,
                                     amount_minor, method)
     VALUES ('c0000048-0000-0000-0000-000000000002','$VEH_A','$PAX_1','2026-07-01',250000,'cash');"
# …but a failed attempt is outside the partial index, so the passenger can try again.
psql_run "UPDATE subscription.payments SET status='failed'
           WHERE subscription_id='c0000048-0000-0000-0000-000000000002';" >/dev/null \
  || die "could not fail the Epic 23 payment fixture."
check_eq "a failed attempt does not block a retry for the same month" "1" \
  "WITH retry AS (
     INSERT INTO subscription.payments(subscription_id, vehicle_id, passenger_id, period_month,
                                       amount_minor, method)
     VALUES ('c0000048-0000-0000-0000-000000000002','$VEH_A','$PAX_1','2026-07-01',250000,'cash')
     RETURNING 1)
   SELECT count(*) FROM retry;"

psql_run "DELETE FROM subscription.payments
           WHERE subscription_id='c0000048-0000-0000-0000-000000000002';
          DELETE FROM subscription.grants WHERE id='c0000048-0000-0000-0000-000000000001';" >/dev/null \
  || die "could not clean up the Epic 23 fixtures."

# ---------------------------------------------------------------------------------------
# C052 — safety: the outbox, the replay log, and the four columns C005 could not know about (0905)
#
# The checks below are about the two facts the columns exist to make recordable: the D-33 interval
# (`ts` -> `dispatched_at`, which is the SLO) and who confirmed a report (the evidence behind a
# delisting somebody will appeal).
# ---------------------------------------------------------------------------------------
step "Objects owned by C052 (D-33, D-34, US-12.5/12.6)"

check_eq "safety.outbox exists" "1" \
  "SELECT count(*) FROM information_schema.tables
    WHERE table_schema='safety' AND table_name='outbox';"
check_eq "safety.command_log exists (R-14, the eleventh bounded context)" "1" \
  "SELECT count(*) FROM information_schema.tables
    WHERE table_schema='safety' AND table_name='command_log';"
check_eq "safety.sos_events records when the alert actually went out" "1" \
  "SELECT count(*) FROM information_schema.columns
    WHERE table_schema='safety' AND table_name='sos_events' AND column_name='dispatched_at';"
check_eq "a vehicle report names the driver it counts against" "1" \
  "SELECT count(*) FROM information_schema.columns
    WHERE table_schema='safety' AND table_name='vehicle_reports' AND column_name='driver_id';"
check_eq "the US-12.6 tally has a partial index on CONFIRMED" "1" \
  "SELECT count(*) FROM pg_indexes
    WHERE schemaname='safety' AND indexname='ix_vreports_confirmed'
      AND indexdef LIKE '%CONFIRMED%';"
check_eq "issuing a D-34 link can find a live token per (trip, scope)" "1" \
  "SELECT count(*) FROM pg_indexes
    WHERE schemaname='safety' AND indexname='ix_trip_share_tokens_trip_scope';"

check_rejects "an unknown SMS outcome is refused (ck_sos_events_sms_status)" \
  "INSERT INTO safety.sos_events(user_id, role, lat, lng, sms_status)
     VALUES ('$PAX_1','passenger',6.9271,79.8612,'Delivered');"
check_rejects "a resolved report must say when (ck_vehicle_reports_resolution)" \
  "INSERT INTO safety.vehicle_reports(reporter_id, vehicle_id, reason, status)
     VALUES ('$PAX_1','$VEH_A','reason','CONFIRMED');"
check_eq "a pending report needs no resolution timestamp" "1" \
  "WITH filed AS (
     INSERT INTO safety.vehicle_reports(reporter_id, vehicle_id, reason)
     VALUES ('$PAX_1','$VEH_A','verify c052 pending') RETURNING 1)
   SELECT count(*) FROM filed;"

# A passenger who taps Report twice on one trip has one complaint — and three taps must not be the
# three confirmations that delist a vehicle.
REPORT_RIDE='c0000052-0000-0000-0000-000000000001'
psql_run "INSERT INTO safety.vehicle_reports(reporter_id, vehicle_id, ride_id, reason)
            VALUES ('$PAX_1','$VEH_A','$REPORT_RIDE','verify c052 first');" >/dev/null \
  || die "could not seed the C052 report fixture."
check_rejects "one passenger files one report per ride (ux_vreports_reporter_ride)" \
  "INSERT INTO safety.vehicle_reports(reporter_id, vehicle_id, ride_id, reason)
     VALUES ('$PAX_1','$VEH_A','$REPORT_RIDE','verify c052 second');"
# …and a report with no ride has no natural key, so a second one is admitted (the command log is
# what dedupes those).
check_eq "a report with no ride is not caught by the partial index" "1" \
  "WITH again AS (
     INSERT INTO safety.vehicle_reports(reporter_id, vehicle_id, reason)
     VALUES ('$PAX_1','$VEH_A','verify c052 pending again') RETURNING 1)
   SELECT count(*) FROM again;"

psql_run "DELETE FROM safety.vehicle_reports WHERE reason LIKE 'verify c052%';" >/dev/null \
  || die "could not clean up the C052 report fixture."

# ---------------------------------------------------------------------------------------
# C051 — comms: the outbound notification queue (1308)
#
# The queue is what makes D-27's backoff survive a restart and E-01's "3 s no-ack → SMS fallback"
# exactly once, so the checks below are about the two guards rather than about the columns: the
# dedupe claim that turns at-least-once event delivery into one message, and the CHECK that refuses
# a row nothing could be delivered to.
# ---------------------------------------------------------------------------------------
step "Objects owned by C051 (D5' §14.4, E-01, D-27)"

check_eq "comms.notifications exists" "1" \
  "SELECT count(*) FROM information_schema.tables
    WHERE table_schema='comms' AND table_name='notifications';"
check_eq "comms.command_log exists (R-14, the tenth bounded context)" "1" \
  "SELECT count(*) FROM information_schema.tables
    WHERE table_schema='comms' AND table_name='command_log';"
check_eq "ux_notifications_dedupe is the producer's claim" "1" \
  "SELECT count(*) FROM pg_indexes
    WHERE schemaname='comms' AND indexname='ux_notifications_dedupe';"
check_eq "the E-01 ack sweep has its partial index" "1" \
  "SELECT count(*) FROM pg_indexes
    WHERE schemaname='comms' AND indexname='ix_notifications_ack_due'
      AND indexdef LIKE '%acked_at IS NULL%';"
check_eq "the delivery queue is indexed on due rows only" "1" \
  "SELECT count(*) FROM pg_indexes
    WHERE schemaname='comms' AND indexname='ix_notifications_due'
      AND indexdef LIKE '%Pending%';"
check_eq "comms.notification_tokens carries the AL-08 install (1308)" "1" \
  "SELECT count(*) FROM information_schema.columns
    WHERE table_schema='comms' AND table_name='notification_tokens' AND column_name='device_id';"
check_eq "one live handle per install (ux_notif_tokens_device)" "1" \
  "SELECT count(*) FROM pg_indexes
    WHERE schemaname='comms' AND indexname='ux_notif_tokens_device';"

psql_run "INSERT INTO comms.notifications(dedupe_key, notification_type, channel, recipient_phone, language)
          VALUES ('verify:c051:one','SOS_TRIGGERED','sms','+94770000001','en');" >/dev/null \
  || die "could not seed the C051 notification fixture."
check_rejects "a producer's claim is unique (ux_notifications_dedupe)" \
  "INSERT INTO comms.notifications(dedupe_key, notification_type, channel, recipient_phone, language)
     VALUES ('verify:c051:one','SOS_TRIGGERED','sms','+94770000002','en');"
check_rejects "an SMS with no destination is refused (ck_notifications_sms_destination)" \
  "INSERT INTO comms.notifications(dedupe_key, notification_type, channel, recipient_user_id, language)
     VALUES ('verify:c051:two','SOS_TRIGGERED','sms',NULL,'en');"
check_rejects "a row nothing can be addressed to is refused (ck_notifications_addressable)" \
  "INSERT INTO comms.notifications(dedupe_key, notification_type, channel, language)
     VALUES ('verify:c051:three','DRIVER_ASSIGNED','push','en');"
check_rejects "an unknown status is refused (ck_notifications_status)" \
  "INSERT INTO comms.notifications(dedupe_key, notification_type, channel, recipient_phone, status, language)
     VALUES ('verify:c051:four','SOS_TRIGGERED','sms','+94770000003','Delivered','en');"
check_rejects "a body in a fourth language is refused (ck_notifications_language)" \
  "INSERT INTO comms.notifications(dedupe_key, notification_type, channel, recipient_phone, language)
     VALUES ('verify:c051:five','SOS_TRIGGERED','sms','+94770000004','hi');"

psql_run "DELETE FROM comms.notifications WHERE dedupe_key LIKE 'verify:c051:%';" >/dev/null \
  || die "could not clean up the C051 notification fixture."

# ---------------------------------------------------------------------------------------
# C053 — support: the ticket thread, the agent's handling columns, the replay log (1309)
#
# §13 gives support.tickets one `admin_response TEXT` — a queue that can remember one sentence and
# cannot say who wrote it, when, or what the status was before they did. The checks below are about
# the three facts these objects exist to make recordable: the conversation the complainant reads,
# who is working the ticket, and when it was answered.
# ---------------------------------------------------------------------------------------
step "Objects owned by C053 (Epic 16, US-9.23, US-14.13)"

check_eq "support.ticket_events exists (the thread)" "1" \
  "SELECT count(*) FROM information_schema.tables
    WHERE table_schema='support' AND table_name='ticket_events';"
check_eq "support.command_log exists (R-14, the twelfth bounded context)" "1" \
  "SELECT count(*) FROM information_schema.tables
    WHERE table_schema='support' AND table_name='command_log';"
check_eq "a ticket can be assigned, resolved and evidenced (5 columns)" "5" \
  "SELECT count(*) FROM information_schema.columns
    WHERE table_schema='support' AND table_name='tickets'
      AND column_name IN ('assigned_to','assigned_at','resolved_at','resolved_by','screenshot_upload_id');"
# The definition of done: the screenshot is linked by id, not by public URL. A foreign key onto
# docs.uploads is what makes that structural — the bytes are on SSE-KMS storage (D-36) and the
# ticket cannot hold anything but a pointer to the row that knows where.
check_eq "the screenshot link is a FK onto docs.uploads, not a URL" "1" \
  "SELECT count(*) FROM pg_constraint
    WHERE conrelid='support.tickets'::regclass AND contype='f'
      AND confrelid='docs.uploads'::regclass;"
check_eq "the agent queue can be paged by status (ix_tickets_status_created)" "1" \
  "SELECT count(*) FROM pg_indexes
    WHERE schemaname='support' AND indexname='ix_tickets_status_created';"
check_eq "a thread is read in order (ix_ticket_events_ticket)" "1" \
  "SELECT count(*) FROM pg_indexes
    WHERE schemaname='support' AND indexname='ix_ticket_events_ticket';"

psql_run "INSERT INTO iam.users (id, phone, role)
          VALUES ('c0000053-0000-0000-0000-000000000001','+94770000531','passenger')
          ON CONFLICT (id) DO NOTHING;
          INSERT INTO support.tickets (id, user_id, category, description)
          VALUES ('c0000053-0000-0000-0000-000000000002','c0000053-0000-0000-0000-000000000001',
                  'daily_fee_refund','verify: charged in error');" >/dev/null \
  || die "could not seed the C053 ticket fixture."

check_rejects "an unknown thread event kind is refused (ck_ticket_events_kind)" \
  "INSERT INTO support.ticket_events (ticket_id, kind)
     VALUES ('c0000053-0000-0000-0000-000000000002','escalated');"
# ck_tickets_resolution is NOT VALID (resolved_at is new and pre-existing rows have none), so this
# proves the constraint bites on a NEW write — which is the half that closes the hole.
check_rejects "a RESOLVED ticket with no resolved_at is refused (ck_tickets_resolution)" \
  "UPDATE support.tickets SET status='RESOLVED'
     WHERE id='c0000053-0000-0000-0000-000000000002';"
check_rejects "a resolved_at on an OPEN ticket is refused (ck_tickets_resolution)" \
  "UPDATE support.tickets SET resolved_at=now()
     WHERE id='c0000053-0000-0000-0000-000000000002';"
check_rejects "a ticket cannot point at an upload that does not exist" \
  "UPDATE support.tickets SET screenshot_upload_id='c0000053-0000-0000-0000-0000000000ff'
     WHERE id='c0000053-0000-0000-0000-000000000002';"

psql_run "DELETE FROM support.ticket_events
           WHERE ticket_id='c0000053-0000-0000-0000-000000000002';
          DELETE FROM support.tickets WHERE id='c0000053-0000-0000-0000-000000000002';
          DELETE FROM iam.users WHERE id='c0000053-0000-0000-0000-000000000001';" >/dev/null \
  || die "could not clean up the C053 ticket fixture."

# ---------------------------------------------------------------------------------------
# C054 — docs: the ADD §12.5 document-processing log on docs.extractions (1310)
#
# §12.5 asks for "hash + policy version + redaction-pass version stored per extraction" and 1301
# gives it one BOOLEAN. The checks below are about the two things those columns exist to make
# answerable: WHICH file was processed under WHICH redaction policy (after NFR-28 has deleted the
# file itself), and the D-36 invariant that an image only ever left the perimeter redacted.
# ---------------------------------------------------------------------------------------
step "Objects owned by C054 (D-36, ADD §12.5, D6' §7.5)"

check_eq "docs.extractions carries the ADD §12.5 processing log (7 columns)" "7" \
  "SELECT count(*) FROM information_schema.columns
    WHERE table_schema='docs' AND table_name='extractions'
      AND column_name IN ('raw_sha256','redacted_sha256','redaction_policy_version',
                          'redaction_pass_version','faces_blurred','identifiers_masked','engine');"

check_eq "the D6' §7.5 fallback is countable (ix_extractions_fallback)" "1" \
  "SELECT count(*) FROM pg_indexes
    WHERE schemaname='docs' AND indexname='ix_extractions_fallback';"

psql_run "INSERT INTO iam.users (id, phone, role)
            VALUES ('c0000054-0000-0000-0000-000000000001','+94770000541','driver');
          INSERT INTO docs.uploads (id, owner_id, storage_url, kind)
            VALUES ('c0000054-0000-0000-0000-000000000002',
                    'c0000054-0000-0000-0000-000000000001','licence.png','driving_license');" >/dev/null \
  || die "could not seed the C054 extraction fixture."

# The D-36 invariant, in the last place able to refuse to record its violation: a row saying the
# external model ran on an image the pre-pass never touched describes the one thing that must never
# have happened. NOT VALID, so this proves it bites on a NEW write — the half that closes the hole.
check_rejects "an unredacted Gemini extraction is refused (ck_extractions_gemini_is_redacted)" \
  "INSERT INTO docs.extractions (upload_id, doc_type, status, redaction_applied, engine)
     VALUES ('c0000054-0000-0000-0000-000000000002','driving_license','EXTRACTED',false,'gemini');"

check_rejects "an unknown extraction engine is refused (ck_extractions_engine)" \
  "INSERT INTO docs.extractions (upload_id, doc_type, status, engine)
     VALUES ('c0000054-0000-0000-0000-000000000002','driving_license','EXTRACTED','azure');"

# The on-prem path is the one that legitimately sends nothing, so it must be recordable.
psql_run "INSERT INTO docs.extractions (upload_id, doc_type, status, redaction_applied, engine,
                                        raw_sha256, redaction_policy_version, redaction_pass_version,
                                        faces_blurred, identifiers_masked)
            VALUES ('c0000054-0000-0000-0000-000000000002','driving_license','MANUAL_REVIEW',false,
                    'tesseract', repeat('a',64), 'd36.1', 'c054.1', 1, 2);" >/dev/null \
  || die "a Tesseract-only extraction (redaction_applied=false, engine=tesseract) was refused."

psql_run "DELETE FROM docs.extractions WHERE upload_id='c0000054-0000-0000-0000-000000000002';
          DELETE FROM docs.uploads WHERE id='c0000054-0000-0000-0000-000000000002';
          DELETE FROM iam.users WHERE id='c0000054-0000-0000-0000-000000000001';" >/dev/null \
  || die "could not clean up the C054 extraction fixture."

# ---------------------------------------------------------------------------------------
# C055 — comms: one open room per ride, and a vocabulary for how a call ended (1311)
#
# 1302 landed both tables in their final post-AL-48 shape and neither had a writer until voip-svc.
# The checks below are about the two facts the service otherwise cannot state: that a ride's one
# LiveKit room has one open session (D3' gives a ride one room and BOTH parties start a call into
# it), and that a call which never connected can be told from one that did — which is what ADD §14's
# direct-dial fallback and ADD §16's call-setup SLO are both measured on.
# ---------------------------------------------------------------------------------------
step "Objects owned by C055 (D-24, D6' §6, AL-48)"

check_eq "one open LiveKit session per room (ux_voip_sessions_open_room)" "1" \
  "SELECT count(*) FROM pg_indexes
    WHERE schemaname='comms' AND indexname='ux_voip_sessions_open_room';"

check_eq "the VoIP-failure rate is countable (ix_call_log_voip_failed)" "1" \
  "SELECT count(*) FROM pg_indexes
    WHERE schemaname='comms' AND indexname='ix_call_log_voip_failed';"

psql_run "INSERT INTO iam.users (id, phone, role)
            VALUES ('c0000055-0000-0000-0000-000000000001','+94770000551','passenger');
          INSERT INTO rides.rides
            (id, passenger_id, client_request_id, booker_id, vehicle_type, pickup_geo, dropoff_geo, state)
            VALUES ('c0000055-0000-0000-0000-000000000002',
                    'c0000055-0000-0000-0000-000000000001','c0000055-0000-0000-0000-000000000003',
                    'c0000055-0000-0000-0000-000000000001','three_wheeler',
                    ST_SetSRID(ST_MakePoint(79.861, 6.927), 4326)::geography,
                    ST_SetSRID(ST_MakePoint(79.877, 6.901), 4326)::geography, 'InProgress');
          INSERT INTO comms.voip_sessions (ride_id, livekit_room)
            VALUES ('c0000055-0000-0000-0000-000000000002','ride_c0000055');" >/dev/null \
  || die "could not seed the C055 call fixture."

check_rejects "a second open session for one room is refused (ux_voip_sessions_open_room)" \
  "INSERT INTO comms.voip_sessions (ride_id, livekit_room)
     VALUES ('c0000055-0000-0000-0000-000000000002','ride_c0000055');"

# AL-48 withdrew number masking. The two values it left are the only two the log admits, and the
# CHECK is where a component implementing from a pre-AL-48 section finds that out.
check_rejects "the withdrawn masked call type is refused (ck_call_log_call_type)" \
  "INSERT INTO comms.call_log (ride_id, caller_id, callee_role, call_type)
     VALUES ('c0000055-0000-0000-0000-000000000002','c0000055-0000-0000-0000-000000000001',
             'driver','normal_masked');"

check_rejects "an outcome no spec names is refused (ck_call_log_outcome)" \
  "INSERT INTO comms.call_log (ride_id, caller_id, callee_role, call_type, outcome, ended_at)
     VALUES ('c0000055-0000-0000-0000-000000000002','c0000055-0000-0000-0000-000000000001',
             'driver','free_voip','went_to_voicemail', now());"

# An outcome describes a call that finished. Without the pairing a row can claim voip_failed and
# still be open — which is exactly the row the SLO query counts.
check_rejects "an outcome without an end is refused (ck_call_log_ended)" \
  "INSERT INTO comms.call_log (ride_id, caller_id, callee_role, call_type, outcome)
     VALUES ('c0000055-0000-0000-0000-000000000002','c0000055-0000-0000-0000-000000000001',
             'driver','free_voip','completed');"

psql_run "INSERT INTO comms.call_log (ride_id, caller_id, callee_role, call_type, outcome, ended_at)
            VALUES ('c0000055-0000-0000-0000-000000000002','c0000055-0000-0000-0000-000000000001',
                    'driver','direct_dial','completed', now());" >/dev/null \
  || die "a client-reported direct_dial tap (AL-48) was refused."

psql_run "DELETE FROM comms.call_log WHERE ride_id='c0000055-0000-0000-0000-000000000002';
          DELETE FROM comms.voip_sessions WHERE ride_id='c0000055-0000-0000-0000-000000000002';
          DELETE FROM rides.rides WHERE id='c0000055-0000-0000-0000-000000000002';
          DELETE FROM iam.users WHERE id='c0000055-0000-0000-0000-000000000001';" >/dev/null \
  || die "could not clean up the C055 call fixture."

# ---------------------------------------------------------------------------------------
# C056 — transit: the destination a bus is signed for (1406)
#
# Both D3' and D5' BR-23.2 put the headsign on every option and §18c gives transit.gtfs_trips five
# columns, none of which can hold it. It is what tells the two directions of one route apart on a
# card: "138 to Kottawa" and "138 to Pettah" share a route_short_name AND a route_long_name.
#
# The staging mirror matters as much as the live column here: 1404 builds
# transit_staging.gtfs_trips with CREATE TABLE IF NOT EXISTS ... LIKE, so a column added later is
# only picked up on a database where staging does not exist yet. On every other one the two sides
# diverge — and activation is ALTER TABLE ... SET SCHEMA, which needs them shape-identical.
# ---------------------------------------------------------------------------------------
step "Objects owned by C056 (AL-18, BR-23.2)"

check_eq "transit.gtfs_trips carries trip_headsign" "1" \
  "SELECT count(*) FROM information_schema.columns
    WHERE table_schema='transit' AND table_name='gtfs_trips' AND column_name='trip_headsign';"

check_eq "transit_staging.gtfs_trips carries it too (the swap needs both sides identical)" "1" \
  "SELECT count(*) FROM information_schema.columns
    WHERE table_schema='transit_staging' AND table_name='gtfs_trips' AND column_name='trip_headsign';"

# AL-49 BR-31.1: the pay sheet's payTo reads the single verified row, and the table is versioned so
# an owner's later edit lands beside it rather than on top of it.
check_eq "an org has at most one verified payout profile" "1" \
  "SELECT count(*) FROM pg_indexes
    WHERE schemaname='registry' AND indexname='ux_payout_profile_verified'
      AND indexdef LIKE '%verified%';"

# ---------------------------------------------------------------------------------------
printf '\n'
if (( FAILURES == 0 )); then
  printf '%s%d/%d checks passed — migrations apply cleanly, twice.%s\n' "$GREEN" "$CHECKS" "$CHECKS" "$RESET"
  exit 0
fi

printf '%s%d of %d checks failed.%s\n' "$RED" "$FAILURES" "$CHECKS" "$RESET"
exit 1
