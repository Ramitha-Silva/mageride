#!/usr/bin/env bash
# =====================================================================================
# chaos/lib/drill.sh — the shape every drill has, and the rollback that is armed before the
# fault rather than written after it.
#
# ------------------------------------------------------------------------------------
# THE ROLLBACK IS ARMED FIRST, ALWAYS
# ------------------------------------------------------------------------------------
# A chaos suite whose recovery step is the last line of the script recovers nothing when the
# line above it fails, and a `docker stop postgres` that is never undone is not a drill — it is
# an outage somebody has to notice. So `arm_rollback` registers the undo BEFORE the fault is
# injected and a single trap runs the whole stack in reverse on ANY exit path: a failed
# assertion, a Ctrl-C, a `set -e` abort, or the terminal going away.
#
# It follows that every rollback here must be idempotent — `drill_end` runs it on the happy path
# too, and the trap may run it again.
#
# ------------------------------------------------------------------------------------
# A DRILL THAT CANNOT BE RECOVERED FROM WITHIN RTO IS A FINDING, NOT A FOOTNOTE
# ------------------------------------------------------------------------------------
# `drill_end` does not merely undo the fault; it waits for the stack to be healthy again and
# records how long that took. That number is the drill's own RTO and is reported whether or not
# anybody asked, because "we broke it and it came back" is a claim about a duration.
# =====================================================================================

# The rollback stack. One string per registered undo, most recent last.
CHAOS_ROLLBACKS=()
CHAOS_DRILL_ID=""
CHAOS_DRILL_STARTED=0
CHAOS_DRILLS_RUN=0
CHAOS_DRILLS_FAILED=0

# `arm_rollback "<shell command>"` — run last-in-first-out by run_rollbacks.
arm_rollback() { CHAOS_ROLLBACKS+=("$1"); }

run_rollbacks() {
  local i
  for (( i = ${#CHAOS_ROLLBACKS[@]} - 1; i >= 0; i-- )); do
    eval "${CHAOS_ROLLBACKS[$i]}" >/dev/null 2>&1 || true
  done
  CHAOS_ROLLBACKS=()
}

# The one trap. Installed by run-drills.sh, not here, so that sourcing this file has no effect.
chaos_panic() {
  local code=$?
  if [ ${#CHAOS_ROLLBACKS[@]} -gt 0 ]; then
    printf '\n\033[31m! interrupted inside drill %s — running %d rollback(s)\033[0m\n' \
      "${CHAOS_DRILL_ID:-?}" "${#CHAOS_ROLLBACKS[@]}" >&2
    run_rollbacks
    printf '\033[33m  rolled back. Check the stack:  docker compose -f %s ps\033[0m\n' "$COMPOSE" >&2
  fi
  exit $code
}

# -------------------------------------------------------------------------------------
# drill_begin <id> <title> <spec-ref> <blast-radius> <rollback-description>
# -------------------------------------------------------------------------------------
drill_begin() {
  CHAOS_DRILL_ID="$1"
  CHAOS_DRILL_STARTED=$(now_ms)
  CHAOS_DRILLS_RUN=$((CHAOS_DRILLS_RUN + 1))
  CHAOS_ROLLBACKS=()

  printf '\n\033[1;36m════ %s  %s\033[0m\n' "$1" "$2"
  printf '     \033[2mspec:\033[0m %s\n' "$3"
  printf '     \033[2mblast radius:\033[0m %s\n' "$4"
  printf '     \033[2mrollback:\033[0m %s\n' "$5"

  report ""
  report "## ${1} — ${2}"
  report ""
  report "| | |"
  report "|---|---|"
  report "| **Spec** | ${3} |"
  report "| **Blast radius** | ${4} |"
  report "| **Rollback** | ${5} |"
  report ""
}

# -------------------------------------------------------------------------------------
# drill_end — undo, wait for health, record the recovery time.
#
# `<seconds>` is how long recovery may take before the drill is failed rather than merely
# reported. It is per drill because a `docker stop postgres` and a `redis-cli FLUSHALL` do not
# come back on the same timescale, and one number for both would be either useless or a lie.
# -------------------------------------------------------------------------------------
drill_end() {
  local budget="${1:-120}"

  local recovery_started; recovery_started=$(now_ms)
  run_rollbacks

  local elapsed
  if elapsed=$(wait_for "$budget" 2 stack_healthy); then
    local total; total=$(since_ms "$CHAOS_DRILL_STARTED")
    ok "recovered: every container healthy $(human_ms "$elapsed") after the rollback began (drill total $(human_ms "$total"))"
  else
    CHAOS_DRILLS_FAILED=$((CHAOS_DRILLS_FAILED + 1))
    bad "NOT recovered within ${budget}s of the rollback. The stack is left as the drill found it:
      $(dc ps --format '{{.Service}} {{.Health}}' 2>/dev/null | awk '$2 != "healthy" && $2 != "" {print "        "$1" ("$2")"}')"
    finding HIGH "${CHAOS_DRILL_ID} could not be recovered inside its own budget (${budget}s). \
Per this component's fence that is a finding, not a footnote."
  fi

  CHAOS_DRILL_ID=""
  report ""
}

# -------------------------------------------------------------------------------------
# Assertions. Each one is a sentence about the documented behaviour, so a report reads as a
# comparison against ADD §14.1 rather than as a list of booleans.
# -------------------------------------------------------------------------------------

# `expect <description> <actual> <expected>`
expect() {
  if [ "$2" = "$3" ]; then ok "$1 — ${2}"
  else bad "$1 — expected ${3}, got ${2}"; fi
}

# `expect_one_of <description> <actual> <a> <b> ...`
expect_one_of() {
  local what="$1" actual="$2"; shift 2
  local candidate
  for candidate in "$@"; do
    if [ "$actual" = "$candidate" ]; then ok "${what} — ${actual}"; return 0; fi
  done
  bad "${what} — expected one of [$*], got ${actual}"
  return 1
}

# `expect_at_least <description> <actual> <floor>` — for counters, where the exact value depends
# on how much else the replica was doing.
expect_at_least() {
  if [ "${2:-0}" -ge "$3" ]; then ok "$1 — ${2} (≥ ${3})"
  else bad "$1 — ${2}, expected at least ${3}"; fi
}

# `degraded <what still works> <what is refused>` — ADD §14.1 is a table of exactly these two
# columns, so a drill records them in that shape and the report can be read against the spec
# line by line.
degraded_table_open() {
  report_raw ""
  report_raw "**Degradation observed** (against ADD §14.1's *User Impact* / *System Behaviour*):"
  report_raw ""
  report_raw "| Surface | Under fault | ADD §14.1 says |"
  report_raw "|---|---|---|"
  CHAOS_TABLE_OPEN=1
  CHAOS_DEFERRED=""
}

degraded_row() { report_raw "| ${1} | ${2} | ${3} |"; }

# Closes the table, then releases everything the drill said while it was open — the assertions and
# the findings, in the order they happened, below the table they belong to.
degraded_table_close() {
  CHAOS_TABLE_OPEN=0
  report_raw ""
  if [ -n "$CHAOS_DEFERRED" ]; then
    printf '%s' "$CHAOS_DEFERRED" >> "$REPORT_OUT"
    CHAOS_DEFERRED=""
    report_raw ""
  fi
}
