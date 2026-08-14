#!/usr/bin/env bash
# =====================================================================================
# The pre-deploy migration gate (C124).
#
#   bash infra/scripts/migration-gate.sh --deployed sha-1a2b3c4 --target <full sha> \
#        --connection "Host=127.0.0.1;Port=5432;Database=mageride;Username=postgres;Password=..."
#
#   bash infra/scripts/migration-gate.sh --deployed sha-1a2b3c4 --target HEAD --list-only
#
# The C124 fence: "DB migrations are gated pre-deploy and must be backward-compatible with the
# running version." This script is the "pre-deploy" half — it runs BEFORE the promotion commit
# exists, so a failure means ArgoCD has nothing new to sync and the previous version keeps
# serving. The in-cluster half is ArgoCD sync wave 1
# (infra/k8s/base/jobs/migrate.yaml), which stops wave 2 from being applied.
#
# --- Why backward compatibility is not optional here ------------------------------------
# The rollout is `RollingUpdate` with `maxUnavailable: 0, maxSurge: 1` (D7' §7, and every
# generated Deployment). That means, by design, that OLD AND NEW PODS SERVE AT THE SAME TIME
# against ONE schema — for as long as the slowest rollout takes, which across thirty-one
# workloads is minutes. A migration that removes something the running version reads does not
# cause a failed deploy; it causes 500s from half the pods while the deploy reports progress.
#
# So the rule is expand/contract: a release may only ADD. Whatever the previous release read must
# still be there. Removing it is a second release, after nothing reads it any more.
#
# --- What this script checks, and what it cannot ---------------------------------------
# It checks three things:
#   1. no released script was modified or deleted (db/CLAUDE.md: "a released script is immutable,
#      corrections ship as a new file" — DbUp's journal makes an edited script a no-op on every
#      database that already ran it, so the change silently applies nowhere);
#   2. every NEW script is expand-only, by pattern, unless it carries an explicit marker;
#   3. the new scripts actually apply to a real Postgres 16 + TimescaleDB + PostGIS, and a second
#      pass applies nothing (the C003-C006 discipline).
#
# It cannot check that the previous release's CODE tolerates the new schema — that would need the
# old assemblies running against it. Pattern matching over DDL is a coarse instrument, and (2) is
# where a determined mistake gets through: `DO $$ ... $$` blocks, a `DROP` inside a function body,
# a rename expressed as add-plus-backfill-plus-drop across two files in one release. It catches
# the ordinary mistakes, which is most of them.
#
# --- On the 138 scripts that already exist ---------------------------------------------
# THE HISTORICAL SET DOES NOT PASS RULE (2), and it does not need to. Waves 0-4 built the schema
# against an empty database with nothing serving, so `1010__registry_retire_d11_merchant.sql`'s
# DROP TABLE and the `ALTER TABLE … ADD CONSTRAINT … CHECK` statements in the billing scripts were
# free at the time. The gate only ever scans the DELTA between the revision an environment is
# running and the one being promoted, and the first deploy into an environment has no deployed
# revision at all (`sha-0000000`), so it skips rule (2) entirely. Pointing `--deployed` at a
# pre-wave-5 commit by hand will report those findings; that is the tool being asked a question it
# is not for.
# =====================================================================================
set -euo pipefail

REPO="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
MIGRATIONS="db/migrations"
DEPLOYED=""
TARGET="HEAD"
CONNECTION=""
LIST_ONLY=0

# The escape hatch. A script that genuinely must break compatibility carries this line, and the
# reason is then in the diff, in the file, and in `git log -S` for ever:
#
#   -- mageride:expand-contract phase=contract reason=<why the last reader is gone>
#
# `phase=contract` is the vocabulary on purpose: it says out loud that this is the second half of
# a pair, and invites the reviewer to ask which release was the first half.
MARKER='mageride:expand-contract'

usage() {
  sed -n '3,10p' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//'
}

while [ $# -gt 0 ]; do
  case "$1" in
    --deployed)   DEPLOYED="${2:?--deployed needs a tag or sha}"; shift 2 ;;
    --target)     TARGET="${2:?--target needs a sha}"; shift 2 ;;
    --connection) CONNECTION="${2:?--connection needs a DSN}"; shift 2 ;;
    --list-only)  LIST_ONLY=1; shift ;;
    -h|--help)    usage; exit 0 ;;
    *) echo "error: unrecognised argument '$1'" >&2; usage >&2; exit 2 ;;
  esac
done

cd "$REPO"

fail=0
note()  { printf '\033[36m—\033[0m %s\n' "$*"; }
ok()    { printf '\033[32m✓\033[0m %s\n' "$*"; }
bad()   { printf '\033[31m✗\033[0m %s\n' "$*" >&2; fail=1; }

echo "=== MageRide migration gate ==================================================="

# -------------------------------------------------------------------------------------
# 1. Which commit is the environment running?
#
# The image tag is `sha-<7>`; git needs the commit. `sha-0000000` is the placeholder base carries,
# and it means this environment has never been promoted — there is no running version to be
# compatible WITH, so the compatibility rule is vacuous and only the apply check runs.
# -------------------------------------------------------------------------------------
BASE=""
case "$DEPLOYED" in
  ""|sha-0000000)
    note "no deployed revision (${DEPLOYED:-unset}) — nothing is running yet, so every script is new"
    ;;
  sha-*)
    short="${DEPLOYED#sha-}"
    if BASE="$(git rev-parse --verify --quiet "$short^{commit}")"; then
      note "deployed: $DEPLOYED ($BASE)"
    else
      # A force-push or a shallow clone. Refusing would block every deploy after a history
      # rewrite; proceeding silently would skip the gate. So: proceed, loudly.
      BASE=""
      echo "::warning::$DEPLOYED is not in this repository's history (force-push, or a shallow" \
           "clone — the workflow fetches depth 0). The backward-compatibility check needs the" \
           "deployed commit and is SKIPPED; the apply check still runs."
    fi
    ;;
  *)
    bad "--deployed must be a sha-xxxxxxx tag, got '$DEPLOYED'"
    ;;
esac

HEAD_SHA="$(git rev-parse --verify "$TARGET^{commit}")"
note "target:   $HEAD_SHA"

# -------------------------------------------------------------------------------------
# 2. What changed under db/migrations/?
# -------------------------------------------------------------------------------------
NEW=()
if [ -n "$BASE" ]; then
  while IFS=$'\t' read -r status path rest; do
    [ -n "${status:-}" ] || continue
    case "$status" in
      A) NEW+=("$path") ;;
      M)
        bad "$path was MODIFIED. A released migration is immutable (db/CLAUDE.md): DbUp records
    every script in public.schema_versions, so an edited script is a no-op on every database
    that already ran it — the change applies to new databases only and the two diverge
    silently. Ship the correction as a new NNNN__*.sql."
        ;;
      D)
        bad "$path was DELETED. Its journal row stays, so the object it created is still there on
    every existing database and gone from every new one."
        ;;
      R*)
        bad "$path was RENAMED (to ${rest:-?}). The journal keys on the FILE NAME, so a rename
    makes DbUp apply the same DDL a second time under the new name."
        ;;
      *) bad "unhandled git status '$status' for $path" ;;
    esac
  done < <(git diff --name-status "$BASE" "$HEAD_SHA" -- "$MIGRATIONS")
else
  # No baseline: everything is "new" for the purposes of the apply check, and the compatibility
  # scan has nothing to be compatible with.
  while IFS= read -r path; do NEW+=("$path"); done < <(git ls-tree -r --name-only "$HEAD_SHA" -- "$MIGRATIONS")
fi

if [ "${#NEW[@]}" -eq 0 ]; then
  ok "no new migrations between the deployed revision and the target"
else
  note "${#NEW[@]} new migration script(s):"
  for f in "${NEW[@]}"; do echo "      ${f#"$MIGRATIONS"/}"; done
fi

if [ "$LIST_ONLY" = 1 ]; then
  test "$fail" = 0 || exit 1
  exit 0
fi

# -------------------------------------------------------------------------------------
# 3. Is every new script expand-only?
#
# Each rule below is a thing that breaks a pod still running the PREVIOUS release, during the
# window where both are serving. The message says HOW, because "not backward compatible" on its
# own is not actionable at 2 a.m.
#
# The scan is statement-aware rather than a grep over the file, and that is the difference between
# a gate people use and one they disable. A `CHECK` constraint written inline in a
# `CREATE TABLE IF NOT EXISTS` is completely safe — the table is new, it has no rows, and no
# running code writes to it — while the same constraint added by `ALTER TABLE` is a full-table
# validation under an ACCESS EXCLUSIVE lock. A grep cannot tell those apart, so it would flag
# almost every one of this repository's 138 scripts and be turned off in a week.
#
# So: each file is split into statements, the set of objects the file CREATES is collected first,
# and a rule only fires when its target is something that already existed.
# -------------------------------------------------------------------------------------
if [ -n "$BASE" ] && [ "${#NEW[@]}" -gt 0 ]; then
  if ! MARKER="$MARKER" python3 - "${NEW[@]}" <<'PY'
import os, re, sys

MARKER = os.environ["MARKER"]

LINE_COMMENT = re.compile(r"--[^\n]*")
BLOCK_COMMENT = re.compile(r"/\*.*?\*/", re.DOTALL)
# `$$ … $$` and `$tag$ … $tag$`: a function body is not DDL against a table, and its semicolons
# would break statement splitting.
DOLLAR_BODY = re.compile(r"\$(\w*)\$.*?\$\1\$", re.DOTALL)

IDENT = r'(?:"[^"]+"|[A-Za-z_][A-Za-z0-9_]*)'
QUALIFIED = rf"(?:{IDENT}\.)?{IDENT}"


def norm(name: str) -> str:
    """`"Rides"."Command_Log"` and `rides.command_log` are the same object."""
    return name.replace('"', "").lower()


def created_objects(sql: str) -> set[str]:
    """Everything this file brings into existence, qualified and bare."""
    out: set[str] = set()
    patterns = [
        rf"\bCREATE\s+TABLE\s+(?:IF\s+NOT\s+EXISTS\s+)?({QUALIFIED})",
        rf"\bCREATE\s+(?:OR\s+REPLACE\s+)?(?:TEMP\s+|TEMPORARY\s+)?VIEW\s+(?:IF\s+NOT\s+EXISTS\s+)?({QUALIFIED})",
        rf"\bCREATE\s+MATERIALIZED\s+VIEW\s+(?:IF\s+NOT\s+EXISTS\s+)?({QUALIFIED})",
        rf"\bCREATE\s+SCHEMA\s+(?:IF\s+NOT\s+EXISTS\s+)?({QUALIFIED})",
        rf"\bCREATE\s+TYPE\s+({QUALIFIED})",
        rf"\bCREATE\s+(?:OR\s+REPLACE\s+)?FUNCTION\s+({QUALIFIED})",
        rf"\bCREATE\s+(?:OR\s+REPLACE\s+)?PROCEDURE\s+({QUALIFIED})",
    ]
    for pattern in patterns:
        for match in re.finditer(pattern, sql, re.IGNORECASE):
            name = norm(match.group(1))
            out.add(name)
            out.add(name.split(".")[-1])
    return out


def statements(sql: str) -> list[str]:
    return [s.strip() for s in sql.split(";") if s.strip()]


def target_of(statement: str, keyword: str) -> str | None:
    m = re.search(rf"\b{keyword}\s+(?:IF\s+EXISTS\s+)?(?:ONLY\s+)?({QUALIFIED})", statement, re.IGNORECASE)
    return norm(m.group(1)) if m else None


def pre_existing(name: str | None, created: set[str]) -> bool:
    """True when the statement's target was NOT created by this same file."""
    if name is None:
        return True
    return name not in created and name.split(".")[-1] not in created


# Hot tables: a lock on one of these is a lock on the ride path. The rule is narrowed to them
# because `CREATE INDEX CONCURRENTLY` cannot run inside a transaction and DbUp gives every script
# one (`WithTransactionPerScript`), so demanding it everywhere would demand something the runner
# cannot do. On a cold reference table the lock does not matter.
HOT_SCHEMAS = ("telemetry", "rides", "dispatch", "billing")

findings: list[tuple[str, str, str]] = []  # (file, label, why)


def scan(name: str, raw: str) -> None:
    sql = DOLLAR_BODY.sub(" ", BLOCK_COMMENT.sub(" ", LINE_COMMENT.sub(" ", raw)))
    sql = re.sub(r"\s+", " ", sql)
    created = created_objects(sql)

    def flag(label: str, why: str) -> None:
        findings.append((name, label, why))

    for st in statements(sql):
        upper = st.upper()

        # --- always a break, whatever it targets ------------------------------------
        if re.search(r"\bDROP\s+TABLE\b", st, re.IGNORECASE):
            table = target_of(st, "DROP TABLE")
            if pre_existing(table, created):
                flag("DROP TABLE", f"`{table}` — every SELECT the running version makes against it fails for the whole rollout.")
        if re.search(r"\bDROP\s+COLUMN\b", st, re.IGNORECASE):
            table = target_of(st, "ALTER TABLE")
            if pre_existing(table, created):
                flag("DROP COLUMN", f"on `{table}` — Dapper maps by column name, so the running version's SELECT list breaks.")
        if re.search(rf"\bALTER\s+COLUMN\s+{IDENT}\s+(?:SET\s+DATA\s+)?TYPE\b", st, re.IGNORECASE):
            table = target_of(st, "ALTER TABLE")
            if pre_existing(table, created):
                flag("column TYPE change", f"on `{table}` — the running version's parameter binding and its reads both assume the old type.")
        if re.search(r"\bRENAME\s+(TO|COLUMN|CONSTRAINT)\b", st, re.IGNORECASE):
            table = target_of(st, "ALTER TABLE")
            if pre_existing(table, created):
                flag("RENAME", f"on `{table}` — the old name disappears atomically. Add the new one, backfill, and drop in a later release.")
        # A TRUNCATE STATEMENT, not the word. `TRUNCATE` is also a PostgreSQL privilege, so a
        # substring match — which this was until 2026-08-14 — reads
        #
        #     REVOKE UPDATE, DELETE, TRUNCATE ON audit.events FROM mageride_app, …
        #
        # as data removal. That line is migration 2001's, it is C127-01's remediation, and its
        # whole purpose is to make `audit.events` append-only: the gate was blocking delivery of
        # the statement that takes the privilege AWAY. It is also the only occurrence of the word
        # in any migration, so this check had never matched anything else.
        #
        # `^TRUNCATE` or `TRUNCATE TABLE|ONLY` covers every form the statement takes
        # (`TRUNCATE t`, `TRUNCATE TABLE t`, `TRUNCATE ONLY t`) and none of the privilege's.
        # Anchored like every other check in this block; this was the one that was not.
        if re.search(r"^\s*TRUNCATE\b|\bTRUNCATE\s+(?:TABLE|ONLY)\b", st, re.IGNORECASE):
            flag("TRUNCATE", "this is a migration, not a fixture — every environment applies it, production included. Data removal belongs in a reviewed one-off.")

        # --- a break only against something that already existed ---------------------
        if re.search(r"\bDROP\s+(VIEW|MATERIALIZED\s+VIEW|TYPE|FUNCTION|SCHEMA|PROCEDURE)\b", st, re.IGNORECASE):
            m = re.search(rf"\bDROP\s+(?:MATERIALIZED\s+VIEW|VIEW|TYPE|FUNCTION|SCHEMA|PROCEDURE)\s+(?:IF\s+EXISTS\s+)?({QUALIFIED})", st, re.IGNORECASE)
            obj = norm(m.group(1)) if m else None
            # `DROP VIEW IF EXISTS x; CREATE VIEW x …` is a redefinition, not a removal — and it is
            # how this repository replaces a view whose column list changed, because
            # `CREATE OR REPLACE VIEW` cannot do that.
            if pre_existing(obj, created):
                flag("DROP of a view, type, function or schema", f"`{obj}` is dropped and not recreated here — the running version may still resolve it.")

        if re.search(r"\bSET\s+NOT\s+NULL\b", st, re.IGNORECASE):
            table = target_of(st, "ALTER TABLE")
            if pre_existing(table, created):
                flag("SET NOT NULL", f"on `{table}` — the running version still inserts rows without that column, and every such INSERT now fails.")

        if re.search(r"\bADD\s+COLUMN\b", st, re.IGNORECASE):
            table = target_of(st, "ALTER TABLE")
            if pre_existing(table, created):
                for clause in re.finditer(
                    rf"\bADD\s+COLUMN\s+(?:IF\s+NOT\s+EXISTS\s+)?({IDENT})([^,]*)", st, re.IGNORECASE
                ):
                    body = clause.group(2)
                    if re.search(r"\bNOT\s+NULL\b", body, re.IGNORECASE) and not re.search(
                        r"\bDEFAULT\b", body, re.IGNORECASE
                    ):
                        flag(
                            "ADD COLUMN NOT NULL with no DEFAULT",
                            f"`{norm(clause.group(1))}` on `{table}` — the running version's INSERTs omit it and fail. "
                            "Give it a DEFAULT, or add it nullable and tighten in a later release.",
                        )

        if re.search(r"\bADD\s+(?:CONSTRAINT\b[^;]*?)?\bCHECK\b", st, re.IGNORECASE) and upper.startswith("ALTER TABLE"):
            table = target_of(st, "ALTER TABLE")
            if pre_existing(table, created) and not re.search(r"\bNOT\s+VALID\b", st, re.IGNORECASE):
                flag(
                    "CHECK constraint without NOT VALID",
                    f"on `{table}` — it validates every existing row under an ACCESS EXCLUSIVE lock AND rejects "
                    "writes the running version still makes. Add it NOT VALID here and VALIDATE in a later release.",
                )

        if re.search(r"\bADD\s+(?:CONSTRAINT\b[^;]*?)?\bFOREIGN\s+KEY\b", st, re.IGNORECASE) and upper.startswith("ALTER TABLE"):
            table = target_of(st, "ALTER TABLE")
            if pre_existing(table, created) and not re.search(r"\bNOT\s+VALID\b", st, re.IGNORECASE):
                flag(
                    "FOREIGN KEY without NOT VALID",
                    f"on `{table}` — same lock as above, and it rejects the running version's writes.",
                )

        if re.search(r"\bCREATE\s+(?:UNIQUE\s+)?INDEX\b", st, re.IGNORECASE):
            m = re.search(rf"\bON\s+(?:ONLY\s+)?({QUALIFIED})", st, re.IGNORECASE)
            table = norm(m.group(1)) if m else None
            hot = table is not None and table.split(".")[0] in HOT_SCHEMAS
            if hot and pre_existing(table, created) and not re.search(r"\bCONCURRENTLY\b", st, re.IGNORECASE):
                flag(
                    "CREATE INDEX without CONCURRENTLY on a hot table",
                    f"on `{table}` — it holds a write lock for the whole build. DbUp wraps each script in a "
                    "transaction and CONCURRENTLY cannot run in one, so this needs its own script marked "
                    f"`-- {MARKER}` with the lock window stated.",
                )


exit_code = 0
for path in sys.argv[1:]:
    try:
        raw = open(path, encoding="utf-8").read()
    except FileNotFoundError:
        continue
    name = path.split("/")[-1]

    marked = re.search(rf"--\s*{re.escape(MARKER)}[^\n]*", raw, re.IGNORECASE)
    if marked:
        print(f"::warning::{name} declares itself NOT backward compatible: {marked.group(0).strip()}")
        print(f"\033[36m—\033[0m {name} — marked {MARKER}, rules not enforced")
        continue

    before = len(findings)
    scan(name, raw)
    if len(findings) == before:
        print(f"\033[32m✓\033[0m {name} — expand-only")

for name, label, why in findings:
    print(f"\033[31m✗\033[0m {name}: {label} — {why}", file=sys.stderr)
    exit_code = 1

if exit_code:
    print(
        f"\n    Every finding above is a change that breaks the version still serving during a\n"
        f"    RollingUpdate. If the previous release genuinely no longer touches it, mark the file:\n"
        f"      -- {MARKER} phase=contract reason=<which release stopped reading it>\n",
        file=sys.stderr,
    )

sys.exit(exit_code)
PY
  then
    fail=1
  fi
fi

# -------------------------------------------------------------------------------------
# 4. Do they apply, and is a second pass a no-op?
#
# The C003-C006 discipline. `infra/scripts/migrate-verify.sh` is the fuller version (three passes
# and 187 schema assertions) and starts its own container; this runs against a database the caller
# provides, because in CI that is a service container and here it is whatever the operator points
# at. What both prove is the same thing: the scripts are idempotent, so the wave-1 Job in the
# cluster can be re-run without consequence.
# -------------------------------------------------------------------------------------
if [ -z "$CONNECTION" ]; then
  note "no --connection given — the apply check is SKIPPED (pass one for the full gate)"
else
  note "applying to the throwaway database"
  if ! dotnet run --project backend/src/MageRide.Migrations -c Release -- \
        --connection "$CONNECTION" --wait 120; then
    bad "the migrations do not apply to an empty Postgres 16. This is a red deploy, not a red test."
  else
    ok "first pass applied"
    pending="$(dotnet run --project backend/src/MageRide.Migrations -c Release -- \
                 --connection "$CONNECTION" --what-if 2>&1 || true)"
    if printf '%s' "$pending" | grep -q "No scripts to execute"; then
      ok "second pass is a no-op"
    else
      bad "the second pass still has work to do — a script is not re-runnable:
$(printf '%s' "$pending" | sed 's/^/    /')"
    fi
  fi
fi

echo "==============================================================================="
if [ "$fail" = 0 ]; then
  ok "gate passed — the deploy may proceed"
  exit 0
fi
echo "gate FAILED. Nothing is promoted, so the previous version keeps serving." >&2
exit 1
