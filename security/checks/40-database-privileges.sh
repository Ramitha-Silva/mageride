#!/usr/bin/env bash
# =====================================================================================
# 40 — the database role the platform actually connects as (ADD §12.6 insider DB access, D-35)
#
# This is C127 finding 01, kept as a check because the finding is a DEPLOYMENT state and not a code
# state. Migration 2001 creates the roles; nothing forces a deployment to use them, and until it
# does two shipped controls are inert:
#
#   · audit.events accepts UPDATE, DELETE and TRUNCATE, so D-35's immutable log is not immutable.
#     1305's own comment predicted this exactly: "Real immutability is the deployment's job."
#   · every row-level-security policy is bypassed, because a table owner bypasses RLS without
#     FORCE and a superuser bypasses it unconditionally.
#
# Read-only. Every query below is a catalog read or a `has_table_privilege` call; none writes, and
# none reads a business row.
# =====================================================================================

# shellcheck shell=bash

step "40. the connecting database role (ADD §12.6, D-35)"

if ! replica_db_available; then
  skip_ "no replica Postgres to ask — this is a property of a DEPLOYMENT, not of the repository"
  note "Bring the replica up (infra/replica/deploy.sh) and re-run. Until then, whether audit.events"
  note "is actually append-only and whether RLS actually applies are both unknown, not known-good."
  return 0 2>/dev/null || exit 0
fi

connecting_user=$(psql_replica "SELECT current_user")
is_super=$(psql_replica "SELECT usesuper FROM pg_user WHERE usename = current_user")

ok "the replica answers as '${connecting_user}'"

# -------------------------------------------------------------------------------------
# 40.1 — not a superuser
# -------------------------------------------------------------------------------------
if [ "$is_super" = "t" ]; then
  bad "the application connects as a SUPERUSER ('${connecting_user}'). A superuser bypasses every
      row-level-security policy unconditionally and can rewrite audit.events. Cut over to
      mageride_app — docs/runbooks/database-roles.md §3, one environment variable."
else
  ok "'${connecting_user}' is not a superuser"
fi

# -------------------------------------------------------------------------------------
# 40.2 — audit.events is append-only for the connecting role
# -------------------------------------------------------------------------------------
# `-At` prints a boolean as t/f; the concatenation below therefore reads 'fff' when all three
# are refused. Casting to ::text would give 'falsefalsefalse' and the comparison would never match
# — which would make this check silently unfailable.
audit_write=$(psql_replica "
  SELECT has_table_privilege(current_user,'audit.events','UPDATE')::int::text
      || has_table_privilege(current_user,'audit.events','DELETE')::int::text
      || has_table_privilege(current_user,'audit.events','TRUNCATE')::int::text")

if [ "$audit_write" = "000" ]; then
  ok "audit.events is append-only for the connecting role (D-35)"
else
  bad "audit.events is MUTABLE by the connecting role (update/delete/truncate = ${audit_write}, 1 = granted).
      D-35's immutable admin log can be edited by the same credential that writes it, so the log
      cannot evidence anything it is the only record of."
fi

audit_insert=$(psql_replica "SELECT has_table_privilege(current_user,'audit.events','INSERT')")
[ "$audit_insert" = "t" ] \
  && ok "…and still writable, so the interceptor can record a mutation" \
  || bad "the connecting role cannot INSERT into audit.events; every audited mutation will 500"

# -------------------------------------------------------------------------------------
# 40.3 — row-level security is actually in force
#
# `row_security_active` is the question that matters: not "does the table have RLS" but "is it
# being applied to me". Nine tables carry policies (1806, 1807) and every one of them answered
# false before C127.
# -------------------------------------------------------------------------------------
rls_total=$(psql_replica "SELECT count(*) FROM pg_class WHERE relrowsecurity AND relkind='r'")
rls_inactive=$(psql_replica "
  SELECT count(*) FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
   WHERE c.relrowsecurity AND c.relkind='r'
     AND NOT row_security_active((n.nspname||'.'||c.relname)::regclass)")

if [ "${rls_total:-0}" -eq 0 ]; then
  bad "no table has row-level security enabled; ADD §12.6 requires it for insider DB access"
elif [ "${rls_inactive:-0}" -eq 0 ]; then
  ok "row-level security is in force on all ${rls_total} policy-bearing tables"
else
  bad "${rls_inactive} of ${rls_total} policy-bearing tables do NOT apply their policy to the
      connecting role. Every fleet-scoping policy the platform ships is doing nothing."
  psql_replica "
    SELECT n.nspname||'.'||c.relname||'  (owner '||pg_get_userbyid(c.relowner)||', force='||c.relforcerowsecurity||')'
      FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
     WHERE c.relrowsecurity AND c.relkind='r'
       AND NOT row_security_active((n.nspname||'.'||c.relname)::regclass)
     ORDER BY 1" | while IFS= read -r line; do note "$line"; done
fi

forced=$(psql_replica "SELECT count(*) FROM pg_class WHERE relrowsecurity AND relforcerowsecurity AND relkind='r'")
if [ "${forced:-0}" -eq "${rls_total:-0}" ] && [ "${rls_total:-0}" -gt 0 ]; then
  ok "…and FORCE is set on all ${forced}, so the policy holds for the table owner too"
else
  bad "only ${forced:-0} of ${rls_total:-0} policy-bearing tables set FORCE ROW LEVEL SECURITY.
      Without it the owner bypasses the policy — apply migration 2001."
fi

# -------------------------------------------------------------------------------------
# 40.4 — the least-privilege roles exist to cut over TO
# -------------------------------------------------------------------------------------
roles=$(psql_replica "SELECT string_agg(rolname, ', ' ORDER BY rolname) FROM pg_roles WHERE rolname LIKE 'mageride\_%'")
missing_roles=()
for role in mageride_app mageride_migrate mageride_readonly; do
  case "$roles" in *"$role"*) ;; *) missing_roles+=("$role") ;; esac
done

if [ ${#missing_roles[@]} -eq 0 ]; then
  ok "the least-privilege roles exist (${roles})"
else
  bad "migration 2001 has not been applied here — missing: ${missing_roles[*]}"
fi
