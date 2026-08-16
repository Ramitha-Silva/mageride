#!/usr/bin/env bash
# =====================================================================================
# chaos/run-drills.sh — break the replica on purpose, one documented failure at a time, and
# write down what actually happened.
#
#   bash chaos/run-drills.sh --env replica --report chaos/out/report.md
#   bash chaos/run-drills.sh --env replica --list
#   bash chaos/run-drills.sh --env replica --only 10,50 --report chaos/out/report.md
#   bash chaos/run-drills.sh --env replica --report chaos/out/report.md --no-dr
#
# Prerequisites: the replica is up (`bash infra/replica/deploy.sh`) and the fixture exists
# (`bash chaos/configure.sh` — the bearers live 30 minutes).
#
# ------------------------------------------------------------------------------------
# `--env replica` IS A FENCE, NOT A DEFAULT
# ------------------------------------------------------------------------------------
# There is no other value. This suite runs `redis-cli FLUSHALL`, `docker stop postgres`,
# `docker network disconnect` and, in the DR drill, `DROP DATABASE` — against a stack whose data
# is synthetic by construction (`infra/replica/seed.sql`) and whose loss costs a re-seed. Naming
# the environment is how the operator says out loud which box they are about to break, and the
# only name that is accepted is the one where breaking it is free.
#
# Production is DOKS in Singapore and is reached by no code path in this directory.
#
# ------------------------------------------------------------------------------------
# WHAT THE EXIT CODE MEANS
# ------------------------------------------------------------------------------------
#   0  every drill ran, every fault was rolled back, the stack is healthy. FINDINGS MAY EXIST —
#      they are the deliverable, and the summary and the report name them.
#   1  a drill could not run, an assertion about the drill's own mechanics failed, or the stack
#      did not come back inside a drill's recovery budget.
#   2  bad usage, or this is not the replica.
#
# It is deliberately not a verdict on the platform: a drill that proves a documented degradation
# never happens has succeeded at its job. chaos/report.md is where that is read.
# =====================================================================================
set -uo pipefail

cd "$(dirname -- "${BASH_SOURCE[0]}")" || exit 2
CHAOS_DIR="$PWD"
cd .. || exit 2
REPO_ROOT="$PWD"

COMPOSE="infra/replica/docker-compose.light-replica.yml"
PROJECT="mageride-replica"
ENV_FILE="$REPO_ROOT/infra/replica/.env.replica"

ENVIRONMENT=""
REPORT_OUT=""
ONLY=""
SKIP=""
RUN_DR=1
LIST_ONLY=0

while [ $# -gt 0 ]; do
  case "$1" in
    --env)    ENVIRONMENT="$2"; shift 2 ;;
    --report) REPORT_OUT="$2"; shift 2 ;;
    --only)   ONLY="$2"; shift 2 ;;
    --skip)   SKIP="$2"; shift 2 ;;
    --no-dr)  RUN_DR=0; shift ;;
    --list)   LIST_ONLY=1; shift ;;
    -h|--help) sed -n '2,35p' "$0" | sed 's/^# \{0,1\}//'; exit 0 ;;
    *) echo "unknown argument: $1" >&2; exit 2 ;;
  esac
done

case "$ENVIRONMENT" in
  replica) ;;
  "") echo "  --env is required. The only accepted value is 'replica'." >&2; exit 2 ;;
  *)  echo "  --env ${ENVIRONMENT} is refused. This suite stops containers, flushes Redis and drops
      the database; the only environment where that is free is the lightweight production
      replica, and production is DOKS in Singapore." >&2; exit 2 ;;
esac

export CHAOS_DIR REPO_ROOT COMPOSE PROJECT REPORT_OUT

# -------------------------------------------------------------------------------------
# The drill registry. Order matters: the cheap, surgical faults run first so a run that is going
# to fail fails before it has stopped Postgres, and the DR drill runs last because it is the one
# that replaces the database under everything the earlier drills established.
# -------------------------------------------------------------------------------------
DRILLS=(
  "10 redis-flush           Redis keyspace lost mid-offer (R-04 durable backstop)"
  "11 redis-loss            Redis unreachable (ADD §14.1 live-map degradation)"
  "20 postgres-loss         Postgres primary down (ADD §14.1 tracking continues)"
  "30 redpanda-loss         Event backbone down (outbox holds, consumers lag)"
  "40 emqx-loss             MQTT broker down (ADD §14.1 reconnect within 5 s)"
  "50 outbox-stall          Outbox dispatcher stalled (E-09)"
  "60 reconnect-storm       R-09 connection-rate control under a storm"
  "61 replay-flood          R-09 live/replay split under a replay flood"
  "62 mass-lwt              R-15/R-16 mass driver offline via EMQX last will"
  "63 network-partition     app-services severed from the internal network"
  "70 wallet-degraded       D-08 dispatch with the wallet balance unknowable"
  "90 dr-restore            ADD §15 restore from backup, against RPO 5 min / RTO 30 min"
)

if [ "$LIST_ONLY" = 1 ]; then
  printf '\n\033[1mchaos drills\033[0m\n\n'
  for entry in "${DRILLS[@]}"; do printf '  %s\n' "$entry"; done
  printf '\n  --only takes the two-digit ids, comma separated.\n\n'
  exit 0
fi

# -------------------------------------------------------------------------------------
# Pre-flight
# -------------------------------------------------------------------------------------
for tool in docker jq curl python3 openssl uuidgen; do
  command -v "$tool" >/dev/null 2>&1 || { echo "  $tool is required and is not on PATH" >&2; exit 2; }
done

[ -f "$ENV_FILE" ] || { echo "  .env.replica is absent — run infra/replica/deploy.sh first" >&2; exit 2; }
set -a
# shellcheck disable=SC1090
. "$ENV_FILE"
set +a

EDGE="https://127.0.0.1:${HAPROXY_HTTPS_PORT:-443}"
HOSTHDR="${REPLICA_HOSTNAME:-replica.mageride.lk}"
export EDGE HOSTHDR

# shellcheck source=lib/common.sh
. "$CHAOS_DIR/lib/common.sh"
# shellcheck source=lib/drill.sh
. "$CHAOS_DIR/lib/drill.sh"
# shellcheck source=lib/fixture.sh
. "$CHAOS_DIR/lib/fixture.sh"

trap chaos_panic EXIT INT TERM

if [ -n "$REPORT_OUT" ]; then
  case "$REPORT_OUT" in /*) ;; *) REPORT_OUT="$REPO_ROOT/$REPORT_OUT" ;; esac
  mkdir -p "$(dirname "$REPORT_OUT")"
  : > "$REPORT_OUT"
fi

RUN_STARTED=$(now_ms)
RUN_STAMP="$(date -u +%Y-%m-%dT%H:%M:%SZ)"

# -------------------------------------------------------------------------------------
# The report's front matter, written before anything is checked or broken, so that a run which
# dies in its own pre-flight still leaves a file saying which box, when, and against what commit.
# It has to be FIRST: the pre-flight's steady-state probes and the D-33 baseline both `report`,
# and on the first version they landed above the `# C130` heading.
# -------------------------------------------------------------------------------------
report "# C130 — chaos drill run"
report ""
report "| | |"
report "|---|---|"
report "| **Run** | ${RUN_STAMP} |"
report "| **Environment** | lightweight production replica (\`${PROJECT}\`), ${HOSTHDR} |"
report "| **Host** | $(uname -sr), $(nproc) vCPU, $(free -g | awk '/^Mem:/{print $2}') GB |"
report "| **Commit** | $(git -C "$REPO_ROOT" rev-parse --short HEAD 2>/dev/null || echo 'not a git tree') |"
report "| **Suite** | \`chaos/run-drills.sh --env replica\` |"
report ""
report "Every drill below states its blast radius and its rollback before it injects anything;"
report "the rollback is armed first and runs on any exit path. **▲ FINDING** rows are the"
report "deliverable — a documented degradation that does not happen, a document that does not"
report "describe what happens, or a recovery that did not fit its budget."
report ""
report "## 00 — pre-flight"
report ""

step "chaos — the replica, broken on purpose"

# The same third refusal seed.sh, load/configure.sh and chaos/configure.sh make. Checked again
# HERE rather than trusted from configure.sh: an hour may have passed and the database that
# answers on this socket is not necessarily the one that was there then.
running=$(dc ps --services --filter status=running 2>/dev/null | tr '\n' ' ')
case "$running" in
  *postgres*) ;;
  *) die "nothing is running under the ${PROJECT} project — bring it up with
      bash infra/replica/deploy.sh" ;;
esac

marker=$(psql_one "SELECT count(*) FROM replica.synthetic_marker WHERE marker = 'mageride-replica-synthetic';")
[ "$marker" = "1" ] || die "replica.synthetic_marker is absent. This database was not created by
      infra/replica/seed.sql, and this suite is about to flush, stop and drop things."
ok "replica.synthetic_marker is present — synthetic data only"

if elapsed=$(wait_for 180 5 stack_healthy); then
  ok "every container is healthy (waited $(human_ms "$elapsed"))"
else
  die "the stack is not healthy before a single fault has been injected. A chaos run against an
      already-degraded plane measures nothing:
$(dc ps --format '  {{.Service}} {{.Health}}' 2>/dev/null | grep -v healthy)"
fi

require_fixture
ok "chaos/env.json carries live bearers"

steady_state "steady state, before" || die "the platform is not serving; nothing here is worth breaking yet"

# D-33's baseline, taken with nothing broken, because every drill below compares against it. It is
# a real `safety.sos_events` row and a real dual-gateway attempt — an SOS that were mocked here
# would be measuring the mock.
CHAOS_DRILL_ID="00 pre-flight"
sos_baseline=$(probe_sos 0)
CHAOS_SOS_BASELINE_MS=$(printf '%s' "$sos_baseline" | awk '{print $1}')
CHAOS_SOS_BASELINE=$(printf '%s' "$sos_baseline" | awk '{print $3}')
note "SOS baseline: HTTP $(printf '%s' "$sos_baseline" | awk '{print $2}') in ${CHAOS_SOS_BASELINE_MS} ms, smsStatus=${CHAOS_SOS_BASELINE}"

case "$CHAOS_SOS_BASELINE" in
  Dispatched) ok "D-33's SMS path works on this deployment before anything is broken" ;;
  Failed)
    finding HIGH "**Before any fault is injected**, an SOS on this deployment is recorded and \
never sent: \`POST /v1/sos\` answers 200 with \`smsStatus=Failed\`, which the contract defines as \
\"every gateway refused; the admin console has the alert and nobody has been SMSed\". Both D-33 \
gateways are absent here — \`Sms__SecondaryGateway\` is empty in \`.env.app.example\` and \
notification-svc's log transport is switched off by \
\`Notification__AllowLogTransportOutsideDevelopment=false\` — so the dual path D-33 exists to \
provide is a single path that is not connected. iam-svc's OTPs are unaffected and reach the dev \
sender, which is why every other suite on this replica passes over the top of it. The five-second \
SLO is met (${CHAOS_SOS_BASELINE_MS} ms) and measures the time to give up." ;;
  *) warn "SOS baseline was ${CHAOS_SOS_BASELINE}; the drills will compare against it as-is" ;;
esac

# -------------------------------------------------------------------------------------
# Run them
# -------------------------------------------------------------------------------------
wanted() {
  local id="$1"
  case ",${SKIP}," in *",${id},"*) return 1 ;; esac
  [ -z "$ONLY" ] && return 0
  case ",${ONLY}," in *",${id},"*) return 0 ;; esac
  return 1
}

for entry in "${DRILLS[@]}"; do
  id="${entry%% *}"
  rest="${entry#* }"
  name="${rest%% *}"

  wanted "$id" || continue
  if [ "$id" = "90" ] && [ "$RUN_DR" = 0 ]; then
    printf '\n\033[2m════ 90  dr-restore — skipped (--no-dr)\033[0m\n'
    report ""
    report "## 90 — dr-restore"
    report ""
    report "Skipped: \`--no-dr\`. The DR drill replaces the database and is the one drill that"
    report "cannot be run beside anything else."
    continue
  fi

  file="$CHAOS_DIR/drills/${id}-${name}.sh"
  [ -f "$file" ] || { bad "drill ${id} has no file at ${file}"; continue; }

  # Sourced, not executed: the drills share the counters, the fixture globals and the one
  # rollback trap. A subshell would take its rollbacks — and its findings — to the grave.
  # shellcheck disable=SC1090
  . "$file"

  # One drill's leaked ride is the next drill's booking refusal: `ux_rides_open_passenger` allows
  # one non-terminal ride per passenger, so a fixture that is not put back looks exactly like a
  # platform refusing to book under fault. Cleared between drills rather than trusted to each
  # drill's own rollback, because the rollback is the thing that may have been interrupted.
  release_all_fixture_rides
  leaked=$(open_fixture_rides)
  [ "${leaked:-0}" = "0" ] || warn "drill ${id} left ${leaked} open fixture ride(s) behind"
done

# -------------------------------------------------------------------------------------
# Summary
# -------------------------------------------------------------------------------------
trap - EXIT INT TERM

TOTAL_MS=$(since_ms "$RUN_STARTED")

step "after the run"
steady_state "steady state, after" || bad "the platform is not back to its pre-run steady state"

printf '\n\033[1m════════════════════════════════════════════════════════\033[0m\n'
printf '  drills run        %d\n' "$CHAOS_DRILLS_RUN"
printf '  assertions        \033[32m%d passed\033[0m  \033[31m%d failed\033[0m\n' "$CHAOS_PASS" "$CHAOS_FAIL"
printf '  findings          \033[35m%d\033[0m\n' "$CHAOS_FINDINGS"
printf '  wall clock        %s\n' "$(human_ms "$TOTAL_MS")"
[ -n "$REPORT_OUT" ] && printf '  report            %s\n' "${REPORT_OUT#"$REPO_ROOT"/}"

if [ -n "$CHAOS_HIGH_FINDINGS" ]; then
  printf '\n\033[1;35m  HIGH findings — these are the run\047s news:\033[0m\n%s' "$CHAOS_HIGH_FINDINGS"
fi

report ""
report "---"
report ""
report "## Summary"
report ""
report "| | |"
report "|---|---|"
report "| Drills run | ${CHAOS_DRILLS_RUN} |"
report "| Assertions | ${CHAOS_PASS} passed, ${CHAOS_FAIL} failed |"
report "| Findings | ${CHAOS_FINDINGS} |"
report "| Wall clock | $(human_ms "$TOTAL_MS") |"
report ""

if [ "$CHAOS_FAIL" -gt 0 ] || [ "$CHAOS_DRILLS_FAILED" -gt 0 ]; then
  printf '\n\033[31m✗ %d assertion(s) failed and %d drill(s) did not recover.\033[0m\n' \
    "$CHAOS_FAIL" "$CHAOS_DRILLS_FAILED"
  exit 1
fi

printf '\n\033[32m✓ every drill ran, every fault was rolled back, the stack is healthy.\033[0m\n'
[ "$CHAOS_FINDINGS" -gt 0 ] && printf '\033[35m  %d finding(s) — read %s\033[0m\n' \
  "$CHAOS_FINDINGS" "${REPORT_OUT:-the output above}"
exit 0
