#!/usr/bin/env bash
# =====================================================================================
# security/run-asvs-checks.sh — the OWASP ASVS L2 review's non-xUnit half (C127).
#
#   bash security/run-asvs-checks.sh              # everything that can be answered here
#   bash security/run-asvs-checks.sh --strict     # a skipped check is a failure (release gate)
#   bash security/run-asvs-checks.sh 40           # one check file by its number
#
# C127's verify command is this AND the test project:
#
#   bash security/run-asvs-checks.sh && dotnet test tests/Security -c Release
#
# ------------------------------------------------------------------------------------
# WHY THERE ARE TWO HALVES, AND WHERE THE LINE IS
# ------------------------------------------------------------------------------------
# `tests/Security` composes all twenty-five services and reads what they DECLARE: every endpoint's
# authorization metadata, every service's bearer-validation parameters, the redaction guard's place
# in an HTTP client's handler chain. That is exhaustive, fast, and impossible to flake — and it can
# only ever see what is in the assemblies.
#
# This script asks the questions that are not in an assembly:
#
#   · is a secret in the repository, and are the rules that keep it out still there   (10)
#   · does the shipped configuration enforce D-30, D-31 and the mTLS-plane refusal    (20)
#   · does the RUNNING edge publish /metrics, refuse the internal plane, gate versions (30)
#   · does the platform connect to Postgres as a role that can rewrite the audit log   (40)
#
# 40 is the one worth reading twice. It is not a property of any file — every migration and every
# policy is correct — it is a property of the credential a deployment happens to hand the services,
# and both controls it defeats are invisible from inside the process.
#
# ------------------------------------------------------------------------------------
# THE FENCE
# ------------------------------------------------------------------------------------
# Live checks run against the REPLICA and never against production. The edge and the database are
# read out of `infra/replica/.env.replica`, or out of `MAGERIDE_LIVE_EDGE` when it is set; nothing
# here discovers a target by itself, and nothing here writes anything anywhere. A check with no
# target SKIPS and says what went unasked — an unanswerable question must not read as a green tick.
# =====================================================================================
set -uo pipefail

cd "$(dirname -- "${BASH_SOURCE[0]}")" || exit 2
SECURITY_DIR="$PWD"

# shellcheck source=security/asvs-lib.sh
. "$SECURITY_DIR/asvs-lib.sh"

STRICT=0
ONLY=""

for argument in "$@"; do
  case "$argument" in
    --strict) STRICT=1 ;;
    [0-9]*)   ONLY="$argument" ;;
    -h|--help)
      sed -n '2,12p' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//'
      exit 0 ;;
    *)
      echo "unknown argument: $argument" >&2
      exit 2 ;;
  esac
done

for tool in git python3 curl; do
  command -v "$tool" >/dev/null 2>&1 || { echo "$tool is required and is not on PATH" >&2; exit 2; }
done

printf '%sMageRide — OWASP ASVS L2 checks (C127)%s\n' "$C_BOLD" "$C_OFF"
note "repository $REPO_ROOT"
note "edge       ${EDGE:-(none — live checks will skip)}"

ran=0
for check in "$SECURITY_DIR"/checks/[0-9]*.sh; do
  [ -f "$check" ] || continue

  name="$(basename "$check")"
  [ -z "$ONLY" ] || case "$name" in "$ONLY"*) ;; *) continue ;; esac

  ran=$((ran+1))
  # shellcheck disable=SC1090
  . "$check"
done

if [ "$ran" -eq 0 ]; then
  echo "no check file matched '${ONLY}'" >&2
  exit 2
fi

# -------------------------------------------------------------------------------------
# The report
# -------------------------------------------------------------------------------------
step "summary"
printf '  %d passed · %d failed · %d warned · %d skipped\n' "$pass" "$fail" "$warned" "$skipped"

if [ "$skipped" -gt 0 ]; then
  echo
  printf '  %sNot asked%s — these are unknown, not known-good:\n' "$C_WARN" "$C_OFF"
  for entry in "${SKIPS[@]}"; do printf '    • %s\n' "$entry"; done
fi

if [ "$fail" -gt 0 ]; then
  echo
  printf '  %sFindings%s:\n' "$C_BAD" "$C_OFF"
  for entry in "${FAILURES[@]}"; do printf '    ✗ %s\n' "${entry%%$'\n'*}"; done
  echo
  echo "  Every finding is fixed or risk-accepted with an owner and a date — \"noted\" is not a"
  echo "  resolution (C127 fence). The ledger is security/remediation-backlog.md."
  exit 1
fi

if [ "$STRICT" -eq 1 ] && [ "$skipped" -gt 0 ]; then
  echo
  echo "  --strict: ${skipped} check(s) could not run, and a release gate cannot sign off on a"
  echo "  question nobody asked. Bring the replica up and re-run."
  exit 1
fi

echo
echo "  Every check that could run, passed."
exit 0
