#!/usr/bin/env bash
# =====================================================================================
# load/run.sh — the whole C129 suite, with the server side sampled around every profile.
#
#   bash load/run.sh                                  # smoke, sustained, fleet, dispatch
#   bash load/run.sh --profiles sustained,burst       # ingest profiles only
#   bash load/run.sh --profiles dispatch
#   bash load/run.sh --with-fanout                    # add the §16.3 subscriber-scale run
#
# Prerequisite: `bash load/configure.sh`. Output lands in load/out/ (gitignored); the figures
# that go into load/report.md are transcribed from it by hand, the way
# docs/runbooks/gtfs-day0-load.md transcribes a day-0 run.
#
# ------------------------------------------------------------------------------------
# A FAILING THRESHOLD IS A RESULT, NOT AN ERROR
# ------------------------------------------------------------------------------------
# `burst` exists to find out whether the replica can carry ADD §3.2's 15,000 msg/s, and the
# expected answer is no. k6 exits non-zero when a threshold breaks, so this script records each
# profile's exit code and carries on — and prints them all at the end. What it must never do is
# soften a threshold so that a scaled-down run reports a production target as met; that is the
# component's first fence.
# =====================================================================================
set -uo pipefail

cd "$(dirname -- "${BASH_SOURCE[0]}")" || exit 2
LOAD_DIR="$PWD"
cd .. || exit 2

OUT="$LOAD_DIR/out"
mkdir -p "$OUT"

PROFILES="smoke,sustained,fleet,dispatch"
WITH_FANOUT=0

while [ $# -gt 0 ]; do
  case "$1" in
    --profiles) PROFILES="$2"; shift 2 ;;
    --with-fanout) WITH_FANOUT=1; shift ;;
    *) echo "unknown argument: $1" >&2; exit 2 ;;
  esac
done

step() { printf '\n\033[1m▸ %s\033[0m\n' "$*"; }
ok()   { printf '  \033[32m✓\033[0m %s\n' "$*"; }
bad()  { printf '  \033[31m✗\033[0m %s\n' "$*"; }
note() { printf '    %s\n' "$*"; }

[ -f "$LOAD_DIR/env.json" ] || { echo "no load/env.json — run \`bash load/configure.sh\` first" >&2; exit 2; }

RESULTS=""

record() { RESULTS="${RESULTS}$1	$2
"; }

# -------------------------------------------------------------------------------------
step "0  the chain is alive"
# -------------------------------------------------------------------------------------
if k6 run --quiet "$LOAD_DIR/probe.js" 2>&1 | grep -q PUBACK; then
  ok "one sample crossed EMQX, the bridge, telemetry.raw, the processor and Redis"
else
  bad "the probe could not publish. Nothing below will mean anything — fix this first:"
  note "k6 run load/probe.js"
  exit 1
fi

# -------------------------------------------------------------------------------------
run_ingest() {
  local profile="$1"
  step "ingest — ${profile}"

  bash "$LOAD_DIR/collect.sh" open "ingest-${profile}"

  k6 run "$LOAD_DIR/ingest.js" -e "PROFILE=${profile}" \
    --summary-export="$OUT/ingest-${profile}.k6.json" 2>&1 | tail -30
  local code=${PIPESTATUS[0]}

  bash "$LOAD_DIR/collect.sh" close "ingest-${profile}"

  record "ingest/${profile}" "$code"
  [ "$code" -eq 0 ] && ok "thresholds held" || bad "a threshold broke (exit ${code}) — that IS the finding"
}

run_dispatch() {
  step "dispatch"

  bash "$LOAD_DIR/collect.sh" open dispatch
  k6 run "$LOAD_DIR/dispatch.js" --summary-export="$OUT/dispatch.k6.json" 2>&1 | tail -30
  local code=${PIPESTATUS[0]}
  bash "$LOAD_DIR/collect.sh" close dispatch

  record "dispatch" "$code"
  [ "$code" -eq 0 ] && ok "thresholds held" || bad "a threshold broke (exit ${code})"
}

run_fanout() {
  step "fan-out — ADD §16.3's subscriber shape"

  bash "$LOAD_DIR/collect.sh" open fanout
  k6 run "$LOAD_DIR/fanout.js" --summary-export="$OUT/fanout.k6.json" 2>&1 | tail -30
  local code=${PIPESTATUS[0]}
  bash "$LOAD_DIR/collect.sh" close fanout

  record "fanout" "$code"
}

IFS=',' read -ra WANTED <<< "$PROFILES"
for profile in "${WANTED[@]}"; do
  case "$profile" in
    dispatch) run_dispatch ;;
    fanout) run_fanout ;;
    *) run_ingest "$profile" ;;
  esac
done

[ "$WITH_FANOUT" = "1" ] && run_fanout

# -------------------------------------------------------------------------------------
step "the accept race (ADD §11.11)"
# -------------------------------------------------------------------------------------
bash "$LOAD_DIR/accept-race.sh" 2>&1 | tail -12
record "accept-race" "$?"

# -------------------------------------------------------------------------------------
step "results"
# -------------------------------------------------------------------------------------
printf '%s' "$RESULTS" | while IFS=$'\t' read -r name code; do
  [ -n "$name" ] || continue
  if [ "$code" = "0" ]; then
    ok "${name}"
  else
    bad "${name} (exit ${code})"
  fi
done

echo
echo "  raw output in load/out/. The transcribed figures — and what each one means against"
echo "  the ADD targets — belong in load/report.md."
