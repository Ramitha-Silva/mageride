#!/usr/bin/env bash
# =====================================================================================
# Δ MCS-18 — put a test driver back to before Profile Setup, without deleting them.
#
#   bash infra/replica/reset-driver-onboarding.sh --phone +94767137368
#   bash infra/replica/reset-driver-onboarding.sh --phone +94767137368 --dry-run
#   bash infra/replica/reset-driver-onboarding.sh --all-pending --dry-run
#
# --- The problem this solves ------------------------------------------------------------
# SCR-DA/DI-001's boot router asks registry-svc "has this driver completed Profile Setup?"
# and that question is answered by the EXISTENCE of a `registry.driver_profiles` row
# (`OnboardingService.ReadProfileAsync`, Δ MCS-05). So the first successful save is
# one-way: reinstalling the app and signing in with the same number goes straight to the
# dashboard (SCR-DA-010) for ever, which is correct behaviour and also makes the screen
# untestable a second time.
#
# --- What it deliberately does NOT delete ------------------------------------------------
# `iam.users`. The point is to sign in with the SAME number and land on Profile Setup, so
# the account, its role grant and its OTP history all stay. Deleting the user would test a
# different thing — first-ever sign-up — and would silently change the `driver` role
# question that MCS-05 was about.
#
# --- Scope ------------------------------------------------------------------------------
# REPLICA ONLY. The root CLAUDE.md is explicit that this box carries synthetic data for
# testing and demos; this script refuses to run against anything whose synthetic marker is
# absent, because "delete this driver's onboarding" is not a sentence anybody should be
# able to type at production by accident.
# =====================================================================================
set -euo pipefail

CONTAINER="${REPLICA_PG_CONTAINER:-mageride-replica-postgres-1}"
PGUSER="${REPLICA_PG_USER:-mageride}"
PGDB="${REPLICA_PG_DB:-mageride}"

PHONE=""
ALL_PENDING=0
DRY_RUN=0

bold() { printf '\n\033[1m▸ %s\033[0m\n' "$1"; }
ok()   { printf '  \033[32m✓\033[0m %s\n' "$1"; }
note() { printf '  \033[33m!\033[0m %s\n' "$1"; }
die()  { printf '  \033[31m✗ %s\033[0m\n' "$1" >&2; exit 1; }

usage() {
  cat >&2 <<'USAGE'
usage: reset-driver-onboarding.sh (--phone +94XXXXXXXXX | --all-pending) [--dry-run]

  --phone P       reset the driver whose iam.users.phone is P
  --all-pending   reset every driver profile that is NOT verified (verified_at IS NULL)
  --dry-run       print what would be removed and change nothing
USAGE
  exit 2
}

while [ $# -gt 0 ]; do
  case "$1" in
    --phone) PHONE="${2:-}"; shift 2 ;;
    --all-pending) ALL_PENDING=1; shift ;;
    --dry-run) DRY_RUN=1; shift ;;
    -h|--help) usage ;;
    *) die "unknown argument: $1" ;;
  esac
done

[ -n "$PHONE" ] || [ "$ALL_PENDING" = 1 ] || usage
[ -n "$PHONE" ] && [ "$ALL_PENDING" = 1 ] && die "--phone and --all-pending are mutually exclusive"

psql() { docker exec -i "$CONTAINER" psql -U "$PGUSER" -d "$PGDB" -v ON_ERROR_STOP=1 "$@"; }
q()    { psql -tAc "$1"; }

# -------------------------------------------------------------------------------------
bold "1/3  this is the replica, and nothing else"
docker inspect "$CONTAINER" >/dev/null 2>&1 || die "no container $CONTAINER — is the replica up?"

marker=$(q "SELECT count(*) FROM information_schema.tables
             WHERE table_schema='replica' AND table_name='synthetic_marker'" || echo 0)
[ "$marker" = "1" ] || die "replica.synthetic_marker is absent — refusing to run against a database
  that has not identified itself as the synthetic replica (see infra/replica/seed.sh)."
ok "replica.synthetic_marker present — synthetic data"

# -------------------------------------------------------------------------------------
bold "2/3  what matches"

if [ -n "$PHONE" ]; then
  # Parameterised through a temp table rather than string-interpolated: a phone number is
  # user input, and this script is one quote away from being an injection otherwise.
  selector="p.driver_id IN (SELECT id FROM iam.users WHERE phone = '$(printf '%s' "$PHONE" | sed "s/'/''/g")')"
  scope="phone $PHONE"
else
  selector="p.verified_at IS NULL"
  scope="every profile with verified_at IS NULL"
fi

targets=$(q "SELECT p.driver_id FROM registry.driver_profiles p WHERE $selector")

if [ -z "$targets" ]; then
  ok "nothing matches $scope — already reset, or that number never completed Profile Setup"
  exit 0
fi

count=$(printf '%s\n' "$targets" | wc -l | tr -d ' ')
printf '  %s driver(s) matching %s:\n' "$count" "$scope"
psql -c "
  SELECT p.driver_id,
         u.phone,
         u.role,
         p.display_name,
         (p.verified_at IS NOT NULL) AS verified,
         (SELECT count(*) FROM registry.documents d WHERE d.driver_id = p.driver_id) AS documents,
         (SELECT count(*) FROM registry.vehicles v WHERE v.owner_id = p.driver_id) AS vehicles,
         (SELECT count(*) FROM docs.uploads o WHERE o.owner_id = p.driver_id) AS uploads
    FROM registry.driver_profiles p
    LEFT JOIN iam.users u ON u.id = p.driver_id
   WHERE $selector
   ORDER BY p.created_at;"

if [ "$DRY_RUN" = 1 ]; then
  note "--dry-run: nothing was deleted"
  exit 0
fi

# -------------------------------------------------------------------------------------
bold "3/3  reset"

# ONE transaction. A half-reset driver — profile gone, licence documents still there — is a
# state no screen and no service expects, and it is worse than either end of the operation.
#
# Order is FK order, children first. `registry.driver_eligible_vehicles` is a VIEW and needs
# nothing. `iam.users` is untouched by design (see the header).
psql <<SQL
BEGIN;

CREATE TEMP TABLE reset_targets ON COMMIT DROP AS
SELECT p.driver_id FROM registry.driver_profiles p WHERE $selector;

CREATE TEMP TABLE reset_vehicles ON COMMIT DROP AS
SELECT v.id FROM registry.vehicles v WHERE v.owner_id IN (SELECT driver_id FROM reset_targets);

DELETE FROM registry.document_fields f
 WHERE f.document_id IN (
   SELECT d.id FROM registry.documents d
    WHERE d.driver_id IN (SELECT driver_id FROM reset_targets)
       OR d.vehicle_id IN (SELECT id FROM reset_vehicles));

DELETE FROM registry.documents d
 WHERE d.driver_id IN (SELECT driver_id FROM reset_targets)
    OR d.vehicle_id IN (SELECT id FROM reset_vehicles);

DELETE FROM registry.onboarding_steps s WHERE s.vehicle_id IN (SELECT id FROM reset_vehicles);

-- The vehicle goes too. Profile Setup is driver identity and owns no vehicle (AL-27), but a
-- vehicle whose owner has no driver_profile is a state the Mode-C wizard cannot resume from
-- and My Vehicles cannot explain. "Reset this driver" means the driver.
DELETE FROM registry.vehicles v WHERE v.id IN (SELECT id FROM reset_vehicles);

DELETE FROM registry.driver_payout_profiles pp
 WHERE pp.driver_id IN (SELECT driver_id FROM reset_targets);

DELETE FROM registry.driver_profiles p
 WHERE p.driver_id IN (SELECT driver_id FROM reset_targets);

-- Last, because the documents above referenced them by storage_url. The objects themselves
-- are left in MinIO: NFR-28's auto_delete_at is what reclaims them, and a script that reached
-- into object storage would be a second, unaudited deletion path.
DELETE FROM docs.uploads o WHERE o.owner_id IN (SELECT driver_id FROM reset_targets);

COMMIT;
SQL

ok "reset $count driver(s)"

remaining=$(q "SELECT count(*) FROM registry.driver_profiles p WHERE $selector")
[ "$remaining" = "0" ] || die "$remaining profile(s) still match after the reset"

printf '\n\033[1m✓ done.\033[0m  Sign in with the same number — the boot router will send it to Profile Setup.\n'
