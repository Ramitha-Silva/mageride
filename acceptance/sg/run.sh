#!/usr/bin/env bash
# =====================================================================================
# acceptance/sg/run.sh — C131's verify command.
#
#   bash acceptance/sg/run.sh --report acceptance/sg/out/report.md
#   bash acceptance/sg/run.sh --report ... --calls 500 --seconds 60
#   bash acceptance/sg/run.sh --rehearse --report acceptance/sg/out/rehearsal.md
#   bash acceptance/sg/run.sh --selftest-only
#
# WHAT THE EXIT CODE MEANS
# ------------------------------------------------------------------------------------
#   0  every acceptance figure the definition of done names was measured in-region.
#   1  the run happened and something it measured missed its target. The numbers stand;
#      a failing threshold is a result, not a reason to soften a threshold (C129's rule).
#   2  the run could NOT happen — the region, the Colombo clients or the devices are not
#      there. Nothing is measured and nothing is reported as measured. `--report` still
#      writes a document, and that document says what is missing.
#      This is `infra/replica/gtfs-day0-verify.sh`'s shape, for C126's reason: a component
#      blocked on something outside the repository states the blockage in its own exit code
#      rather than in a paragraph somebody can skip.
#   3  the instruments are unsound (`selftest.py` failed). Nothing was attempted.
#
# WHY THERE IS NO WAY TO GET AN ACCEPTANCE FIGURE OUT OF THE EU REPLICA
# ------------------------------------------------------------------------------------
# `lib/region.sh` is consulted before any probe runs and its verdict is carried into every
# artefact this script writes. A run that does not clear it is stamped NOT EVIDENCE in the
# report's title, in every JSON payload's `evidence` field, and in the exit code. There is
# deliberately no flag that overrides it — see that file's header.
# =====================================================================================
set -uo pipefail

cd "$(dirname -- "${BASH_SOURCE[0]}")" || exit 2
SG_DIR="$PWD"
cd ../.. || exit 2
REPO_ROOT="$PWD"

ENV_FILE="$SG_DIR/env.json"
OUT_DIR="$SG_DIR/out"
REPORT=""
CALLS=50
SECONDS_STREAM=30
ROUNDS=20
REHEARSE=0
SELFTEST_ONLY=0
DOWNLINK_MODE="platform"

REGION_RTT_MS=""
EVIDENCE="not-evidence"
BLOCKERS=()
RESULTS=()

step()      { printf '\n\033[1m▸ %s\033[0m\n' "$*"; }
ok()        { printf '  \033[32m✓\033[0m %s\n' "$*"; }
note_fail() { printf '  \033[31m✗\033[0m %s\n' "$*"; }
note()      { printf '  \033[33m!\033[0m %s\n' "$*"; }
blocker()   { BLOCKERS+=("$1"); note_fail "$1"; }

usage() { sed -n '2,30p' "$0"; exit 0; }

while [ $# -gt 0 ]; do
  case "$1" in
    --report)        REPORT="$2"; shift 2 ;;
    --env)           ENV_FILE="$2"; shift 2 ;;
    --calls)         CALLS="$2"; shift 2 ;;
    --seconds)       SECONDS_STREAM="$2"; shift 2 ;;
    --rounds)        ROUNDS="$2"; shift 2 ;;
    --downlink)      DOWNLINK_MODE="$2"; shift 2 ;;
    --rehearse)      REHEARSE=1; shift ;;
    --selftest-only) SELFTEST_ONLY=1; shift ;;
    -h|--help)       usage ;;
    *) printf 'unknown argument: %s\n' "$1" >&2; exit 2 ;;
  esac
done

mkdir -p "$OUT_DIR"
[ -n "$REPORT" ] && mkdir -p "$(dirname -- "$REPORT")"

# =====================================================================================
# 1. The instruments, before anything else.
# =====================================================================================
step "Instrument self-test"

if python3 "$SG_DIR/selftest.py" > "$OUT_DIR/selftest.log" 2>&1; then
  ok "$(grep -oE '[0-9]+ checks passed' "$OUT_DIR/selftest.log" | head -1) — G.107, RFC 3550, RFC 5389/5766 and D6' §4.1"
else
  note_fail "the measurement code does not pass its own tests; see $OUT_DIR/selftest.log"
  tail -20 "$OUT_DIR/selftest.log"
  exit 3
fi

[ "$SELFTEST_ONLY" -eq 1 ] && exit 0

# =====================================================================================
# 2. The environment descriptor.
# =====================================================================================
step "Environment"

if [ ! -f "$ENV_FILE" ]; then
  blocker "no $ENV_FILE — run 'bash acceptance/sg/configure.sh' against the Singapore region first"
else
  ok "read $(basename "$ENV_FILE")"
fi

jqr() { [ -f "$ENV_FILE" ] && jq -r "$1 // empty" "$ENV_FILE" 2>/dev/null || true; }

REGION="$(jqr '.region')"
CLIENT_LOCATION="$(jqr '.tracker.clientLocation')"
TURN_HOST="$(jqr '.turn.host')"
TURN_PORT="$(jqr '.turn.port')"
TRACKER_HOST="$(jqr '.tracker.host')"
PLATFORM_BASE="$(jqr '.platform.baseUrl')"

[ -z "$TURN_HOST" ]    && blocker "env.json names no TURN host — the LiveKit/coturn media plane is not deployed in Singapore"
[ -z "$TRACKER_HOST" ] && blocker "env.json names no tracker ingest host — there is no Singapore ingest to round-trip against"
[ -z "$PLATFORM_BASE" ] && blocker "env.json names no platform baseUrl — the AL-48 fallback cannot be driven"
[ -z "$CLIENT_LOCATION" ] && blocker "env.json declares no Colombo-side client — a Sri Lanka to Singapore RTT needs a Sri Lankan origin"

# =====================================================================================
# 3. The region fence.
# =====================================================================================
# shellcheck source=lib/region.sh
. "$SG_DIR/lib/region.sh"

REGION_OK=1

if [ "$REHEARSE" -eq 1 ]; then
  step "Region fence"
  note "--rehearse: the fence is NOT applied and NOTHING here is acceptance evidence."
  note "This mode exists so the in-region run is not the first time this code executes."
  EVIDENCE="rehearsal"
  REGION_OK=0
elif [ ${#BLOCKERS[@]} -eq 0 ]; then
  if region_assert_singapore "$TURN_HOST" "${TURN_PORT:-3478}" "$REGION" "$CLIENT_LOCATION"; then
    EVIDENCE="acceptance"
    REGION_OK=0
    ok "the fence is cleared — figures from this run are acceptance evidence"
  else
    blocker "the region fence was not cleared; no figure from this run is acceptance evidence"
  fi
else
  step "Region fence"
  note_fail "not attempted — the environment is incomplete (see above)"
fi

# =====================================================================================
# 4. The probes.
# =====================================================================================
run_probe() {
  local name="$1"; shift
  local code=0

  # `$?` after an `if` block is the block's status, not the command's — a probe that exited 2
  # would be reported as "(exit 0)", which is exactly the kind of thing that makes a failing
  # acceptance run look like a passing one. Capture it at the command.
  "$@" > "$OUT_DIR/$name.log" 2>&1 || code=$?

  if [ "$code" -eq 0 ]; then
    ok "$name"
    RESULTS+=("$name=pass")
  else
    note_fail "$name (exit $code) — see $OUT_DIR/$name.log"
    RESULTS+=("$name=fail")
  fi

  return "$code"
}

if [ ${#BLOCKERS[@]} -eq 0 ]; then
  step "VoIP — media quality at concurrency"
  run_probe voip-media python3 "$SG_DIR/voip/media_probe.py" \
    --env "$ENV_FILE" --calls "$CALLS" --seconds "$SECONDS_STREAM" \
    --out "$OUT_DIR/voip-media.json"

  step "VoIP — AL-48 direct-dial fallback"
  run_probe voip-fallback-available python3 "$SG_DIR/voip/fallback_probe.py" \
    --env "$ENV_FILE" --expect available --out "$OUT_DIR/voip-fallback-available.json"

  note "the forced-failure half needs LiveKit taken down in-region; see README.md §'Forcing the failure'"
  if [ "$(jqr '.voip.forcedFailureArranged')" = "true" ]; then
    run_probe voip-fallback-unavailable python3 "$SG_DIR/voip/fallback_probe.py" \
      --env "$ENV_FILE" --expect unavailable --out "$OUT_DIR/voip-fallback-unavailable.json"
  else
    blocker "env.json does not record voip.forcedFailureArranged — the forced-failure half of DoD 3 was not driven"
  fi

  step "Tracker — round trip and downlink"
  run_probe tracker-rtt python3 "$SG_DIR/tracker/rtt_probe.py" \
    --env "$ENV_FILE" --rounds "$ROUNDS" --downlink "$DOWNLINK_MODE" \
    --out "$OUT_DIR/tracker-rtt.json"

  step "Server-side view"
  run_probe collect bash "$SG_DIR/collect.sh" --env "$ENV_FILE" --out "$OUT_DIR/server-side.json"
else
  step "Probes"
  note_fail "not run — see the blockers above. Nothing was measured."
fi

# =====================================================================================
# 5. The report.
# =====================================================================================
write_report() {
  local path="$1"

  {
    if [ "$EVIDENCE" = "acceptance" ]; then
      printf '# C131 — Singapore acceptance run\n\n'
      printf '**Evidence.** The region fence was cleared: minimum TCP RTT %s ms from a client declaring itself at `%s`, against a target that is not the replica.\n' \
        "${REGION_RTT_MS:-?}" "$CLIENT_LOCATION"
    elif [ "$EVIDENCE" = "rehearsal" ]; then
      printf '# C131 — REHEARSAL, NOT EVIDENCE\n\n'
      printf '> This run was made with `--rehearse`. The region fence was **not applied**.\n'
      printf '> Every figure below describes whatever host answered, which on this box is the\n'
      printf '> Contabo EU replica. **EU numbers are not acceptance evidence** (C131 fence 1).\n'
    else
      printf '# C131 — NOT RUN, NOT EVIDENCE\n\n'
      printf '> The acceptance run could not happen. Nothing below was measured.\n'
    fi

    printf '\nGenerated by `acceptance/sg/run.sh`. Region declared: `%s`. Client: `%s`.\n' \
      "${REGION:-none}" "${CLIENT_LOCATION:-none}"

    if [ ${#BLOCKERS[@]} -gt 0 ]; then
      printf '\n## What is missing\n\n'
      for item in "${BLOCKERS[@]}"; do
        printf -- '- %s\n' "$item"
      done
      printf '\nSee `acceptance/sg/report.md` for what each blocker needs and who owns it.\n'
    fi

    if [ ${#RESULTS[@]} -gt 0 ]; then
      printf '\n## Probes\n\n| Probe | Result | Raw |\n|---|---|---|\n'
      for item in "${RESULTS[@]}"; do
        printf '| `%s` | %s | `acceptance/sg/out/%s.json` |\n' \
          "${item%%=*}" "${item##*=}" "${item%%=*}"
      done
    fi

    for artefact in voip-media tracker-rtt; do
      [ -f "$OUT_DIR/$artefact.json" ] || continue
      printf '\n## %s\n\n```json\n' "$artefact"
      jq '.summary // .measurements // .' "$OUT_DIR/$artefact.json" 2>/dev/null | head -60
      printf '```\n'
    done

    printf '\n## How to read these figures\n\n'
    printf -- '- **Region-specific and non-transferable:** every VoIP media figure and every tracker\n'
    printf -- '  RTT. They describe a Colombo-to-Singapore path and mean nothing about any other pair.\n'
    printf -- '- **Transferable:** the AL-48 fallback result. It is a contract property of voip-svc,\n'
    printf -- '  not of the region.\n'
    printf -- '- **Modelled, not measured:** the jitter-buffer term inside every MOS, and the codec\n'
    printf -- '  impairment (G.711+PLC parameters; G.113 publishes no Opus row). Both are stated at\n'
    printf -- '  their term in the JSON so a reader can substitute their own.\n'
  } > "$path"

  ok "wrote $path"
}

step "Report"

# Not `[ -n "$REPORT" ] && write_report … || note …` — in that form a write_report that returned
# non-zero would ALSO print the "no --report given" line, which is a report that was attempted and
# failed being announced as one that was never asked for.
if [ -n "$REPORT" ]; then
  write_report "$REPORT"
else
  note "no --report given; JSON is in $OUT_DIR"
fi

# =====================================================================================
# 6. The verdict.
# =====================================================================================
step "Verdict"

if [ ${#BLOCKERS[@]} -gt 0 ]; then
  note_fail "${#BLOCKERS[@]} blocker(s). No acceptance figure was produced."
  printf '\n'
  for item in "${BLOCKERS[@]}"; do printf '    - %s\n' "$item"; done

  if [ "$EVIDENCE" = "rehearsal" ]; then
    printf '\n  This was a rehearsal, which never produces acceptance evidence in any case.\n'
  else
    printf '\n  C131 cannot be closed from this host. See acceptance/sg/report.md.\n'
  fi
  exit 2
fi

failed=0
for item in "${RESULTS[@]}"; do [ "${item##*=}" = "fail" ] && failed=$((failed + 1)); done

if [ "$failed" -gt 0 ]; then
  note_fail "$failed probe(s) missed their target. The figures stand and are in $OUT_DIR."
  exit 1
fi

ok "every probe met its target"
[ "$EVIDENCE" = "rehearsal" ] && { note "rehearsal only — not acceptance evidence"; exit 2; }
exit 0
