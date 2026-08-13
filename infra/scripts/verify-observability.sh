#!/usr/bin/env bash
# =====================================================================================
# C119 verify — proves every item in the observability-stack definition of done.
#
#   bash infra/scripts/verify-observability.sh
#
# Definition of done (build/prompts/C119.md):
#   1. every service reports the golden signals and appears on a dashboard
#   2. a simulated stuck ride fires the correct business alert within its window
#   3. SOS latency and position end-to-end latency are measurable against their SLOs
#   4. each alert links to a runbook with a first action
#   + the deliverables: OTLP wiring verified end to end from a service to Tempo
#
# --- HOW IT IS STRUCTURED --------------------------------------------------------------
# Phases 1-5 are STATIC: no containers, no network, a couple of seconds. They are the ones
# that catch a rule that could never fire, a metric name that does not exist, an alert with
# no runbook. Phase 6 onwards brings the stack up.
#
# Phase 3 is where the definition of done's "simulated stuck ride" actually runs —
# `promtool test rules` feeds each §13.3.1 gauge the series a stuck ride would produce and
# asserts the right alert is silent before its window and firing after it.
#
# --- WHAT IT NEEDS ----------------------------------------------------------------------
# docker, jq, python3. The slim dev stack does NOT have to be running: a scrape target that
# is absent is a target that is down, which is the correct reading. It IS needed for the
# infrastructure-target checks, which are skipped (not failed) when it is not up.
#
# Env:
#   VERIFY_OBS_KEEP=1     leave the observability stack running afterwards
#   VERIFY_OBS_REUSE=1    do not bring it down/up; assert against what is running
#   VERIFY_OBS_STATIC=1   phases 1-5 only, no containers at all
#   VERIFY_OBS_NO_BUILD=1 skip the api-gateway build in phase 8 if the image is absent
# =====================================================================================
set -Eeuo pipefail

REPO_ROOT="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/../.." && pwd)"
OBS_DIR="$REPO_ROOT/infra/observability"
OBS="$OBS_DIR/docker-compose.yml"
RULES_DIR="$OBS_DIR/prometheus/rules"
TESTS_DIR="$OBS_DIR/prometheus/tests"
DASH_DIR="$OBS_DIR/grafana/dashboards"
RUNBOOKS="$REPO_ROOT/docs/runbooks"
NETWORK="mageride_mr"

PROM_IMAGE="prom/prometheus:v2.55.1"
AM_IMAGE="prom/alertmanager:v0.27.0"

PROM_URL="http://127.0.0.1:${PROMETHEUS_PORT:-9090}"
GRAFANA_URL="http://127.0.0.1:${GRAFANA_PORT:-3000}"
TEMPO_URL="http://127.0.0.1:${TEMPO_PORT:-3200}"
LOKI_URL="http://127.0.0.1:${LOKI_PORT:-3100}"
AM_URL="http://127.0.0.1:${ALERTMANAGER_PORT:-9093}"
OTLP_HTTP="http://127.0.0.1:${OTLP_HTTP_PORT:-4318}"

FAILURES=0
CHECKS=0
SKIPS=0

RED=''; GREEN=''; YELLOW=''; BLUE=''; RESET=''
if [[ -t 1 ]]; then RED=$'\033[31m'; GREEN=$'\033[32m'; YELLOW=$'\033[33m'; BLUE=$'\033[36m'; RESET=$'\033[0m'; fi

step() { printf '\n%s==> %s%s\n' "$YELLOW" "$1" "$RESET"; }
die()  { printf '%serror: %s%s\n' "$RED" "$1" "$RESET" >&2; exit 1; }

check() { # check <description> <expected> <actual>
  CHECKS=$((CHECKS + 1))
  if [[ "$2" == "$3" ]]; then
    printf '  %sok%s   %s\n' "$GREEN" "$RESET" "$1"
  else
    printf '  %sFAIL%s %s\n       expected: %s\n       actual:   %s\n' \
      "$RED" "$RESET" "$1" "$2" "$3"
    FAILURES=$((FAILURES + 1))
  fi
}

skip() { SKIPS=$((SKIPS + 1)); printf '  %sskip%s %s (%s)\n' "$BLUE" "$RESET" "$1" "$2"; }

cleanup() {
  if [[ "${VERIFY_OBS_KEEP:-}" == "1" || "${VERIFY_OBS_REUSE:-}" == "1" || "${VERIFY_OBS_STATIC:-}" == "1" ]]; then
    return
  fi
  docker rm -f c119-otlp-probe >/dev/null 2>&1 || true
  # NEVER --remove-orphans. It operates on the compose PROJECT, and this stack shares the
  # `mageride` project with the dev stacks by design — so it would take Postgres, Redis,
  # Redpanda, EMQX and MinIO down with it. It did exactly that once.
  docker compose -f "$OBS" down >/dev/null 2>&1 || true
}
trap cleanup EXIT

command -v docker  >/dev/null 2>&1 || die "docker is required"
command -v jq      >/dev/null 2>&1 || die "jq is required"
command -v python3 >/dev/null 2>&1 || die "python3 is required"

# promtool and amtool ship inside the images the stack already pins, so the verify needs no
# host-side Prometheus install and can never drift from the version that runs the rules.
promtool() { docker run --rm --entrypoint /bin/promtool -v "$OBS_DIR:/w" -w /w "$PROM_IMAGE" "$@"; }
amtool()   { docker run --rm --entrypoint /bin/amtool   -v "$OBS_DIR:/w" -w /w "$AM_IMAGE"   "$@"; }

promq() { # promq <query> -> the first sample's value, or "" if none
  curl -sf --get "$PROM_URL/api/v1/query" --data-urlencode "query=$1" 2>/dev/null \
    | jq -r '.data.result[0].value[1] // ""' 2>/dev/null || echo ""
}

# =====================================================================================
step "1. Configuration parses"
# =====================================================================================

if docker compose -f "$OBS" config >/dev/null 2>&1; then s=ok; else s=fail; fi
check "infra/observability/docker-compose.yml parses" "ok" "$s"

# The same trap infra/CLAUDE.md records for the dev stacks: compose interpolates env_file
# values, and an unset variable silently becomes an empty string in the container.
warnings=$(docker compose -f "$OBS" config 2>&1 >/dev/null | grep -c 'variable is not set' || true)
check "no unset-variable warnings" "0" "$warnings"

if promtool check config --syntax-only /w/prometheus/prometheus.yml >/dev/null 2>&1; then s=ok; else s=fail; fi
check "prometheus.yml is valid" "ok" "$s"

if amtool check-config /w/alertmanager/alertmanager.yml >/dev/null 2>&1; then s=ok; else s=fail; fi
check "alertmanager.yml is valid" "ok" "$s"

rule_files=("$RULES_DIR"/*.yml)
check "rule files present" "true" "$([[ ${#rule_files[@]} -ge 4 ]] && echo true || echo false)"

for f in "${rule_files[@]}"; do
  if promtool check rules "/w/prometheus/rules/$(basename "$f")" >/dev/null 2>&1; then s=ok; else s=fail; fi
  check "$(basename "$f") is a valid rule file" "ok" "$s"
done

# =====================================================================================
step "2. Every ADD §13.3 / §13.3.1 / §13.4 row has a rule"
# =====================================================================================

alerts_json=$(python3 - "$RULES_DIR" <<'PY'
import sys, os, re, json
# The rule files are YAML with no anchors or tags; a targeted parse avoids making PyYAML a
# host dependency for a verify script that otherwise needs nothing but docker.
alerts = []
for name in sorted(os.listdir(sys.argv[1])):
    if not name.endswith(".yml"):
        continue
    text = open(os.path.join(sys.argv[1], name), encoding="utf-8").read()
    blocks = re.split(r"\n\s*-\s*alert:\s*", text)[1:]
    for block in blocks:
        alert = block.split("\n", 1)[0].strip()
        runbook = re.search(r"runbook_url:\s*(\S+)", block)
        summary = re.search(r"summary:\s*", block)
        severity = re.search(r"severity:\s*(\S+)", block)
        alerts.append({
            "name": alert,
            "file": name,
            "runbook": runbook.group(1) if runbook else None,
            "has_summary": summary is not None,
            "severity": severity.group(1) if severity else None,
        })
print(json.dumps(alerts))
PY
)

alert_names=$(echo "$alerts_json" | jq -r '.[].name')
alert_count=$(echo "$alerts_json" | jq 'length')
printf '     %s alert rules found\n' "$alert_count"

has_alert() { echo "$alert_names" | grep -qx "$1" && echo yes || echo no; }

# --- ADD §13.3.1, all eight rows (R-20, the C119 fence) -------------------------------
for a in RideStuckMatching RideStuckOffered RideStuckAccepted RideStuckDriverArrived \
         RideStuckInProgress RideStuckPaymentPending PaymentsOverpaidBacklog \
         ExpiredDocumentsStillDispatching; do
  check "§13.3.1 row is alertable: $a" "yes" "$(has_alert "$a")"
done

# --- ADD §13.3, every SLO with a burn-rate or threshold alert -------------------------
for a in PositionE2ELatencyBudgetBurning SosDispatchLatencyBreached \
         PaymentCallbackResolveBudgetBurning VoipCallSetupSlow \
         TrackingPlaneAvailabilityBudgetBurning ApiAvailabilityBudgetBurning \
         WebSocketConnectFailureRateHigh OfferPushLatencyBudgetBurning \
         AcceptResolutionLatencyBudgetBurning; do
  check "§13.3 SLO has an alert: $a" "yes" "$(has_alert "$a")"
done

# --- ADD §13.4, all seven bullets ------------------------------------------------------
for a in ConsumerLagSecondsHigh EmqxAuthFailureRateHigh RedisEvictions \
         PostgresReplicationLag RideTimerBacklogHigh OutboxDispatchLagHigh; do
  check "§13.4 bullet has an alert: $a" "yes" "$(has_alert "$a")"
done
# Bullet 7 is "stuck_state_* per §13.3.1", already covered above.

# --- The SLO targets are the spec's, not softer ones (the C119 fence) -----------------
check "position SLO uses the 5 s p95 bucket" "yes" \
  "$(grep -q 'le="5000"' "$RULES_DIR/recording.slo.yml" && echo yes || echo no)"
check "position SLO burn rate is 14x" "yes" \
  "$(grep -q '(14 \* 0.01)' "$RULES_DIR/alerts.slo.yml" && echo yes || echo no)"
check "position p99 target is 8 s" "yes" \
  "$(grep -q 'mageride:position_e2e:p99 > 8' "$RULES_DIR/alerts.slo.yml" && echo yes || echo no)"
check "SOS p99 target is 5 s and pages on one window" "yes" \
  "$(grep -A2 'alert: SosDispatchLatencyBreached' "$RULES_DIR/alerts.slo.yml" \
     | grep -q 'mageride:sos_dispatch:p99 > 5' && echo yes || echo no)"
check "SOS alert has no smoothing window" "yes" \
  "$(grep -A3 'alert: SosDispatchLatencyBreached' "$RULES_DIR/alerts.slo.yml" \
     | grep -q 'for: 0s' && echo yes || echo no)"
check "consumer lag threshold is 5 s" "yes" \
  "$(grep -q 'mageride:consumer_lag:seconds > 5' "$RULES_DIR/alerts.infrastructure.yml" && echo yes || echo no)"
check "consumer lag page threshold is 100k (D7' §12)" "yes" \
  "$(grep -q 'mageride:consumer_lag:messages > 100000' "$RULES_DIR/alerts.infrastructure.yml" && echo yes || echo no)"
check "timer backlog threshold is 100" "yes" \
  "$(grep -q 'mageride_rides_timer_backlog) > 100' "$RULES_DIR/alerts.infrastructure.yml" && echo yes || echo no)"
check "outbox p95 threshold is 1 s" "yes" \
  "$(grep -A6 'alert: OutboxDispatchLagHigh' "$RULES_DIR/alerts.infrastructure.yml" \
     | grep -q '> 1000' && echo yes || echo no)"
check "EMQX auth failure threshold is 1%" "yes" \
  "$(grep -q '> 0.01' "$RULES_DIR/alerts.infrastructure.yml" && echo yes || echo no)"
check "Redis eviction threshold is zero" "yes" \
  "$(grep -q 'redis_evicted_keys_total\[5m\])) > 0' "$RULES_DIR/alerts.infrastructure.yml" && echo yes || echo no)"
check "Postgres replication threshold is 30 s" "yes" \
  "$(grep -q 'pg_replication_is_replica == 1) > 30' "$RULES_DIR/alerts.infrastructure.yml" && echo yes || echo no)"

# =====================================================================================
step "3. A simulated stuck ride fires the correct alert (definition of done)"
# =====================================================================================

test_files=("$TESTS_DIR"/*.test.yml)
check "rule unit tests present" "true" "$([[ ${#test_files[@]} -ge 3 ]] && echo true || echo false)"

for f in "${test_files[@]}"; do
  name=$(basename "$f")
  if out=$(promtool test rules "/w/prometheus/tests/$name" 2>&1); then s=ok; else s=fail; fi
  check "promtool test rules $name" "ok" "$s"
  [[ "$s" == "fail" ]] && printf '%s\n' "$out" | sed 's/^/       /'
done

# The definition-of-done item names the stuck ride specifically, so assert the case exists
# rather than trusting that the file above happens to contain it.
check "the stuck-ride simulation covers all six ride states" "6" \
  "$(grep -oE 'mageride_rides_stuck\{state="[A-Za-z]+"' "$TESTS_DIR/stuck-state.test.yml" \
     | sort -u | wc -l | tr -d ' ')"

# =====================================================================================
step "4. Every alert links to a runbook, and every runbook has a first action"
# =====================================================================================

missing_runbook=$(echo "$alerts_json" | jq -r '.[] | select(.runbook == null) | .name')
check "every alert has a runbook_url" "" "$missing_runbook"

missing_summary=$(echo "$alerts_json" | jq -r '.[] | select(.has_summary == false) | .name')
check "every alert has a summary" "" "$missing_summary"

missing_severity=$(echo "$alerts_json" | jq -r '.[] | select(.severity == null) | .name')
check "every alert has a severity" "" "$missing_severity"

broken=""
for url in $(echo "$alerts_json" | jq -r '.[].runbook' | sort -u); do
  file="${url##*/}"
  [[ -f "$RUNBOOKS/$file" ]] || broken="$broken $file"
done
check "every runbook_url names a file that exists" "" "$broken"

no_first_action=""
for f in "$RUNBOOKS"/*.md; do
  [[ "$(basename "$f")" == "README.md" ]] && continue
  grep -q '^## First action' "$f" || no_first_action="$no_first_action $(basename "$f")"
done
check "every runbook opens with a First action" "" "$no_first_action"

# Δ C124: runbooks that are DELIVERY procedures rather than alert responses. No alert points at
# them and none should — nothing pages when somebody needs to roll a deploy back or rotate the JWT
# signing key — but they belong beside the alert runbooks, because every alert runbook above can end
# in one of them and an on-call person looks in one directory.
#
# A named list rather than a pattern: the check's teeth are that a NEW alert runbook nobody linked
# is a failure, and a pattern like `deploy*` would let one hide.
# C132 added `oncall.md`, which is the one runbook that is ABOUT the alerts rather than behind
# one: the rota, the three PagerDuty services and their escalation, and the status page. No alert
# can point at it and none should.
#
# `chaos-drills.md`, `gtfs-day0-load.md` and `database-roles.md` are also not alert-driven and are
# NOT added here — they are C126's, C127's and C130's, they have been failing this check and the
# First-action check since they landed, and quietly half-fixing another component's regression is
# how the other half stops being visible. Recorded in the C132 handoff.
NOT_ALERT_DRIVEN="deploy.md rollback.md secret-rotation.md replica-operations.md oncall.md"

orphaned=""
for f in "$RUNBOOKS"/*.md; do
  base=$(basename "$f")
  [[ "$base" == "README.md" ]] && continue
  [[ " $NOT_ALERT_DRIVEN " == *" $base "* ]] && continue
  echo "$alerts_json" | jq -r '.[].runbook' | grep -q "/$base$" || orphaned="$orphaned $base"
done
check "no runbook is orphaned (nothing links to it)" "" "$orphaned"

# =====================================================================================
step "5. Every metric a rule or dashboard names is one the platform emits"
# =====================================================================================
#
# The single highest-value static check here. A rule against a metric that does not exist
# is a rule that can never fire, and nothing else in this script would notice — Prometheus
# loads it happily and evaluates it to an empty vector for ever.
#
# The inventory is DERIVED from the C# source rather than checked in, so it cannot go
# stale: MageRideDiagnostics and AdapterDiagnostics declare the instrument name and unit,
# and the same mangling MageRide.Shared.Tests/Observability/PrometheusExpositionTests pins
# is applied here (dot to underscore, `_total` on counters, the unit spelled out, a
# brace-wrapped annotation unit dropped).

inventory=$(python3 - "$REPO_ROOT" <<'PY'
import sys, os, re

root = sys.argv[1]
sources = [
    "backend/src/MageRide.Shared/Observability/MageRideDiagnostics.cs",
    "backend/src/TcpAdapter/Observability/AdapterDiagnostics.cs",
    "backend/src/ApiGateway/Observability/GatewayDiagnostics.cs",
]

UNITS = {"ms": "milliseconds", "s": "seconds", "By": "bytes"}
names = set()

for rel in sources:
    path = os.path.join(root, rel)
    if not os.path.exists(path):
        continue
    text = open(path, encoding="utf-8").read()

    # Counter<long>/Histogram<double> factory calls: name, unit, description.
    for kind, name, unit in re.findall(
            r'Create(Counter|Histogram|ObservableGauge)<[^>]+>\(\s*"([^"]+)"\s*,\s*"([^"]*)"', text):
        metric = name.replace(".", "_")
        if unit and not unit.startswith("{"):
            metric += "_" + UNITS.get(unit, unit)
        if kind == "Counter":
            metric += "_total"
        names.add(metric)

    # Gauge names declared as `const string ...Gauge = "mageride.x.y";` — the instrument is
    # created by whichever service owns the data.
    for name in re.findall(r'const string \w*Gauge\w* = "([^"]+)"', text):
        names.add(name.replace(".", "_"))

print("\n".join(sorted(names)))
PY
)

inventory_count=$(echo "$inventory" | grep -c . || true)
printf '     %s mageride_* metrics declared in the C# sources\n' "$inventory_count"
check "the metric inventory is non-empty" "true" "$([[ $inventory_count -gt 30 ]] && echo true || echo false)"

# --- The four SLOs ADD §13.3 defines and no service instruments yet -------------------
# Their rules are ARMED AND SILENT on purpose: an SLO with no rule is an SLO everybody
# forgets, and a rule with no data is a "No data" panel somebody has to explain — the
# second is the better failure. Each is named in the C119 handoff with the service that
# owns it.
#
# This list is self-cleaning: the loop below fails if an entry has since been implemented,
# which is what forces it to be removed rather than left to rot.
PLANNED=(
  mageride_dispatch_offer_push_latency_milliseconds        # §13.3 row 8, dispatch-svc
  mageride_ride_accept_resolution_latency_milliseconds     # §13.3 row 9, ride-svc
  mageride_payments_resolve_latency_milliseconds           # §13.3 row 3, fare-svc
  mageride_voip_call_setup_latency_milliseconds            # §13.3 row 4, voip-svc + LiveKit
)

stale=""
for m in "${PLANNED[@]}"; do
  echo "$inventory" | grep -qx "$m" && stale="$stale $m"
done
check "the not-yet-instrumented list has no entries that now exist" "" "$stale"

# Every `mageride_*` series named in a rule or a dashboard, with the Prometheus histogram
# and counter suffixes stripped back to the family name.
referenced=$(grep -rhoE 'mageride_[a-z0-9_]+' "$RULES_DIR" "$DASH_DIR" \
  | sed -E 's/_(bucket|count|sum)$//' \
  | sort -u)

unknown=""
for m in $referenced; do
  echo "$inventory" | grep -qx "$m" && continue
  printf '%s\n' "${PLANNED[@]}" | grep -qx "$m" && continue
  unknown="$unknown $m"
done
check "every mageride_* series named in a rule or dashboard is declared in code" "" "$unknown"

# And the reverse for the two the definition of done names by hand: they must exist in the
# code AND be read by a rule, or "measurable against their SLOs" is not true.
for m in mageride_positions_e2e_latency_milliseconds mageride_sos_dispatch_latency_milliseconds; do
  check "$m is declared in code" "yes" "$(echo "$inventory" | grep -qx "$m" && echo yes || echo no)"
  check "$m is read by an SLO rule" "yes" \
    "$(grep -rq "$m" "$RULES_DIR/recording.slo.yml" && echo yes || echo no)"
done

# =====================================================================================
step "6. Every service is scraped and appears on a dashboard"
# =====================================================================================

# The scrape TARGET hostnames, not the `service:` labels: `docker-compose.dev.yml`'s
# composition host is called `fanout` and is labelled `service: fanout-svc`, which is
# correct (the label names the service, the target names the container) and would make a
# comparison against labels report a false miss.
scrape_targets=$(grep -oE '"[a-z0-9-]+:[0-9]+"' "$OBS_DIR/prometheus/prometheus.yml" \
  | tr -d '"' | cut -d: -f1 | sort -u)

# The services the platform actually deploys, taken from the compose files rather than from
# a list in this script — a new service added to a compose file and not to prometheus.yml is
# a service nobody is watching, and that is the failure this check exists for.
compose_services=$(python3 - "$REPO_ROOT" <<'PY'
import sys, os, re
root = sys.argv[1]
found = set()
for rel in ["infra/docker-compose.skeleton.yml", "infra/docker-compose.dev.yml"]:
    path = os.path.join(root, rel)
    if not os.path.exists(path):
        continue
    for line in open(path, encoding="utf-8"):
        m = re.match(r"^  ([a-z0-9][a-z0-9-]*):\s*$", line)
        if m:
            found.add(m.group(1))
# Infrastructure and one-shots are scraped through exporters or not at all.
found -= {"postgres", "pgbouncer", "redis", "redpanda", "redpanda-init", "emqx", "minio",
          "minio-init", "migrate", "haproxy", "livekit", "coturn", "tcp-adapter"}
print("\n".join(sorted(found)))
PY
)

unscraped=""
for s in $compose_services; do
  echo "$scrape_targets" | grep -qx "$s" || unscraped="$unscraped $s"
done
check "every app container in a compose file is a scrape target" "" "$unscraped"

# tcp-adapter is the deliberate exception and it must stay deliberate: runtime:10.0-alpine
# with no HTTP surface (D7' §5.1 gives it a TCP-socket liveness probe), so its meter reaches
# Prometheus only by being pushed to the collector first.
check "the collector's pushed-metrics job exists (tcp-adapter's only path)" "yes" \
  "$(grep -q 'job_name: otel-forwarded' "$OBS_DIR/prometheus/prometheus.yml" && echo yes || echo no)"

dash_count=$(find "$DASH_DIR" -name '*.json' | wc -l | tr -d ' ')
check "dashboards present" "true" "$([[ $dash_count -ge 8 ]] && echo true || echo false)"

bad_json=""
uids=""
for f in "$DASH_DIR"/*.json; do
  if uid=$(jq -er '.uid' "$f" 2>/dev/null); then
    uids="$uids $uid"
  else
    bad_json="$bad_json $(basename "$f")"
  fi
done
check "every dashboard is valid JSON with a uid" "" "$bad_json"
check "dashboard uids are unique" "$(echo $uids | tr ' ' '\n' | wc -l | tr -d ' ')" \
  "$(echo $uids | tr ' ' '\n' | sort -u | wc -l | tr -d ' ')"

# ADD §13.2's golden-signal table, row by row: each plane must have a board.
for uid in mageride-platform mageride-slo mageride-stuck-states mageride-position-plane \
           mageride-fanout mageride-emqx mageride-stream mageride-redis mageride-postgres; do
  check "ADD §13.2 plane has a dashboard: $uid" "yes" \
    "$(echo "$uids" | tr ' ' '\n' | grep -qx "$uid" && echo yes || echo no)"
done

# The overview board is what makes "every service appears on a dashboard" true: it is
# driven by a template variable over the scrape targets rather than by a hand-written list,
# so a service added to prometheus.yml appears without editing any JSON.
check "the overview board enumerates services from the scrape targets" "yes" \
  "$(grep -q 'label_values(up{job=~' "$DASH_DIR/platform-overview.json" && echo yes || echo no)"

if [[ "${VERIFY_OBS_STATIC:-}" == "1" ]]; then
  printf '\n%sStatic phases only (VERIFY_OBS_STATIC=1).%s\n' "$YELLOW" "$RESET"
else

# =====================================================================================
step "7. The stack comes up and loads what it was given"
# =====================================================================================

if [[ "${VERIFY_OBS_REUSE:-}" != "1" ]]; then
  docker compose -f "$OBS" up -d >/dev/null 2>&1 || die "the observability stack failed to start"
fi

# The shared helper, not a copy of it: `docker compose ps --format json` emits NDJSON on
# some versions and a single JSON array on others, and wait-healthy.sh is where that is
# normalised. A hand-rolled `jq -rs` slurp errors on the array shape — swallowed by
# `2>/dev/null`, which would make this check pass vacuously and let phases 8-9 race a stack
# that is not up.
#
# The collector and redis-exporter declare no healthcheck (both images are the binary with
# no shell), so they are proved by their scrape target being up instead.
if bash "$REPO_ROOT/infra/scripts/wait-healthy.sh" "$OBS" 180 >/dev/null 2>&1; then s=ok; else s=fail; fi
check "every observability container with a healthcheck is healthy" "ok" "$s"

curl -sf "$PROM_URL/-/healthy" >/dev/null 2>&1 && s=ok || s=fail
check "Prometheus is answering" "ok" "$s"

rules=$(curl -sf "$PROM_URL/api/v1/rules" 2>/dev/null || echo '{}')
group_errors=$(echo "$rules" | jq -r '[.data.groups[]? | select(.lastError != null and .lastError != "")] | length' 2>/dev/null || echo unknown)
check "no rule group failed to load" "0" "$group_errors"

loaded_alerts=$(echo "$rules" | jq '[.data.groups[]?.rules[]? | select(.type=="alerting")] | length' 2>/dev/null || echo 0)
check "Prometheus loaded every alert rule on disk" "$alert_count" "$loaded_alerts"

loaded_recording=$(echo "$rules" | jq '[.data.groups[]?.rules[]? | select(.type=="recording")] | length' 2>/dev/null || echo 0)
check "Prometheus loaded the recording rules" "true" "$([[ $loaded_recording -ge 20 ]] && echo true || echo false)"

am_status=$(curl -sf "$AM_URL/api/v2/status" 2>/dev/null | jq -r '.cluster.status // "unknown"' 2>/dev/null || echo unknown)
check "Alertmanager is ready" "ready" "$am_status"

ds=$(curl -sf "$GRAFANA_URL/api/datasources" 2>/dev/null | jq -r '[.[].uid] | sort | join(",")' 2>/dev/null || echo "")
check "Grafana provisioned the three datasources" "loki,prometheus,tempo" "$ds"

provisioned=$(curl -sf "$GRAFANA_URL/api/search?type=dash-db" 2>/dev/null | jq 'length' 2>/dev/null || echo 0)
check "Grafana provisioned every dashboard" "$dash_count" "$provisioned"

# A dashboard that provisioned but references a datasource uid that does not exist is a
# board of empty panels, and Grafana reports it as loaded.
bad_ds=$(curl -sf "$GRAFANA_URL/api/search?type=dash-db" 2>/dev/null \
  | jq -r '.[].uid' 2>/dev/null \
  | while read -r u; do
      curl -sf "$GRAFANA_URL/api/dashboards/uid/$u" 2>/dev/null \
        | jq -r --arg u "$u" '[.. | objects | select(has("datasource")) | .datasource | select(type=="object") | .uid]
                              | unique | map(select(. != "prometheus" and . != "loki" and . != "tempo" and (startswith("$") | not)))
                              | if length > 0 then $u else empty end' 2>/dev/null
    done | tr '\n' ' ' | sed 's/ *$//')
check "no dashboard references an unknown datasource" "" "$bad_ds"

tempo_ready=$(curl -sf "$TEMPO_URL/ready" 2>/dev/null | tr -d '\n' || echo "")
check "Tempo is ready" "ready" "$tempo_ready"

loki_ready=$(curl -sf "$LOKI_URL/ready" 2>/dev/null | tr -d '\n' || echo "")
check "Loki is ready" "ready" "$loki_ready"

# =====================================================================================
step "8. Infrastructure targets (ADD §13.2's golden signals)"
# =====================================================================================

if ! docker ps --format '{{.Names}}' | grep -q '^mageride-postgres-1$'; then
  skip "infrastructure scrape targets" "the slim dev stack is not running"
  skip "EMQX / Redpanda / Redis / Postgres golden signals" "the slim dev stack is not running"
else
  # Two scrape intervals, so a target that only just came up is not reported as down.
  sleep 35

  for job in emqx redpanda redis postgres blackbox-http blackbox-tcp; do
    up=$(promq "max(up{job=\"$job\"})")
    check "scrape target up: $job" "1" "${up:-missing}"
  done

  # The §13.2 rows, one metric each — the ones the alert rules are written against.
  declare -A GOLDEN=(
    [emqx_connections_count]="EMQX connections"
    [emqx_authentication_failure]="EMQX auth failures"
    [emqx_messages_dropped]="EMQX dropped"
    [redpanda_kafka_max_offset]="Redpanda offsets (the lag numerator)"
    [redpanda_kafka_records_produced_total]="Redpanda produce rate"
    [redpanda_cluster_unavailable_partitions]="Redpanda partition health"
    [redis_evicted_keys_total]="Redis evictions"
    [redis_memory_used_bytes]="Redis memory"
    [pg_stat_activity_count]="Postgres connections"
    [pg_replication_lag_seconds]="Postgres replication lag"
    [probe_success]="synthetic probes"
  )
  for metric in "${!GOLDEN[@]}"; do
    v=$(promq "count($metric)")
    check "§13.2 signal present: ${GOLDEN[$metric]}" "true" "$([[ -n "$v" && "$v" != "0" ]] && echo true || echo false)"
  done
fi

# =====================================================================================
step "9. OTLP end to end — a real service's trace reaches Tempo"
# =====================================================================================
#
# Two proofs, and both are needed. The first pushes OTLP straight at the collector, which
# isolates the collector -> Tempo -> query path. The second runs an actual MageRide service
# with `Otel__Endpoint` pointed at the collector, which is the wiring D7' §4.1 describes and
# the only thing that proves the kernel's exporter is configured correctly.

trace_id=$(openssl rand -hex 16)
span_id=$(openssl rand -hex 8)
now_ns=$(date +%s%N)

payload=$(python3 - "$trace_id" "$span_id" "$now_ns" <<'PY'
import json, sys
trace_id, span_id, now = sys.argv[1], sys.argv[2], int(sys.argv[3])
print(json.dumps({"resourceSpans": [{
    "resource": {"attributes": [
        {"key": "service.name", "value": {"stringValue": "c119-verify"}},
        {"key": "deployment.environment", "value": {"stringValue": "dev"}}]},
    "scopeSpans": [{"scope": {"name": "MageRide"}, "spans": [{
        "traceId": trace_id, "spanId": span_id, "name": "c119-otlp-probe",
        "kind": 1, "startTimeUnixNano": str(now), "endTimeUnixNano": str(now + 1_000_000)}]}]}]}))
PY
)

http=$(curl -s -o /dev/null -w '%{http_code}' -X POST "$OTLP_HTTP/v1/traces" \
  -H 'Content-Type: application/json' -d "$payload" 2>/dev/null || echo 000)
check "the collector accepts an OTLP/HTTP trace" "200" "$http"

# Tempo's ingester holds a trace briefly before it is queryable.
found=no
for _ in $(seq 1 20); do
  if curl -sf "$TEMPO_URL/api/traces/$trace_id" 2>/dev/null | jq -e '.batches | length > 0' >/dev/null 2>&1; then
    found=yes; break
  fi
  sleep 3
done
check "the trace is queryable in Tempo" "yes" "$found"

log_payload=$(python3 - "$trace_id" "$now_ns" <<'PY'
import json, sys
trace_id, now = sys.argv[1], int(sys.argv[2])
print(json.dumps({"resourceLogs": [{
    "resource": {"attributes": [{"key": "service.name", "value": {"stringValue": "c119-verify"}}]},
    "scopeLogs": [{"logRecords": [{
        "timeUnixNano": str(now), "severityText": "INFO", "traceId": trace_id,
        "body": {"stringValue": "c119 otlp log probe"}}]}]}]}))
PY
)
http=$(curl -s -o /dev/null -w '%{http_code}' -X POST "$OTLP_HTTP/v1/logs" \
  -H 'Content-Type: application/json' -d "$log_payload" 2>/dev/null || echo 000)
check "the collector accepts an OTLP/HTTP log record" "200" "$http"

found=no
for _ in $(seq 1 10); do
  if curl -sf --get "$LOKI_URL/loki/api/v1/query_range" \
       --data-urlencode 'query={service_name="c119-verify"}' 2>/dev/null \
       | jq -e '.data.result | length > 0' >/dev/null 2>&1; then
    found=yes; break
  fi
  sleep 3
done
check "the log record is queryable in Loki" "yes" "$found"

# --- A real service, exporting through the kernel's own OpenTelemetry wiring ----------
# api-gateway is the cheapest genuine MageRide service to stand up: Redis is its only
# dependency (C008), so this proves `AddMageRideTelemetry` end to end without the database,
# the broker or the rest of the platform.

if ! docker ps --format '{{.Names}}' | grep -q '^mageride-redis-1$'; then
  skip "a real service's trace reaching Tempo" "the slim dev stack is not running"
elif ! docker image inspect mageride/api-gateway:dev >/dev/null 2>&1 && [[ "${VERIFY_OBS_NO_BUILD:-}" == "1" ]]; then
  skip "a real service's trace reaching Tempo" "mageride/api-gateway:dev absent and VERIFY_OBS_NO_BUILD=1"
else
  if ! docker image inspect mageride/api-gateway:dev >/dev/null 2>&1; then
    printf '     building mageride/api-gateway:dev (first run only)...\n'
    docker build -q -f "$REPO_ROOT/infra/docker/Dockerfile.service" \
      --build-arg SERVICE=ApiGateway -t mageride/api-gateway:dev "$REPO_ROOT" >/dev/null \
      || die "could not build mageride/api-gateway:dev"
  fi

  docker rm -f c119-otlp-probe >/dev/null 2>&1 || true
  docker run -d --rm --name c119-otlp-probe --network "$NETWORK" \
    -p 127.0.0.1:15990:5000 \
    -e ASPNETCORE_ENVIRONMENT=Development \
    -e ConnectionStrings__Redis=redis:6379 \
    -e Jwt__JwksUrl=http://127.0.0.1:9/jwks \
    -e Jwt__Issuer=https://iam.mageride.lk \
    -e Otel__ServiceName=c119-service-probe \
    -e Otel__PrometheusEnabled=true \
    -e Otel__TraceSampleRatio=1.0 \
    -e Otel__Endpoint=http://otel-collector:4317 \
    mageride/api-gateway:dev >/dev/null 2>&1 || die "could not start the service probe"

  ready=no
  for _ in $(seq 1 30); do
    if curl -sf http://127.0.0.1:15990/health/live >/dev/null 2>&1; then ready=yes; break; fi
    sleep 2
  done
  check "the service probe started" "yes" "$ready"

  if [[ "$ready" == "yes" ]]; then
    # A request the tracing filter does not exclude — /health/* and /metrics are filtered
    # out of traces on purpose, so probing those would prove nothing.
    for _ in $(seq 1 5); do curl -s -o /dev/null http://127.0.0.1:15990/v1/rides/c119 || true; done

    # The kernel's Prometheus endpoint carries the golden signals.
    #
    # Scraped in a bounded retry rather than once: `http_server_request_duration_seconds` is
    # recorded when a request FINISHES and the exporter builds its exposition on demand, so
    # the first scrape after start-up can legitimately arrive before either is ready. A
    # single attempt made this a flake.
    scrape=""
    for _ in $(seq 1 15); do
      scrape=$(curl -sf http://127.0.0.1:15990/metrics 2>/dev/null || echo "")
      if grep -q '^# TYPE http_server_request_duration_seconds histogram' <<<"$scrape" \
         && grep -q '^target_info{' <<<"$scrape"; then
        break
      fi
      curl -s -o /dev/null http://127.0.0.1:15990/v1/rides/c119 || true
      sleep 2
    done

    check "the service exposes http_server_request_duration_seconds" "yes" \
      "$(grep -q '^# TYPE http_server_request_duration_seconds histogram' <<<"$scrape" && echo yes || echo no)"

    # Asserted on the parsed value, not with a grep for the expected string, so a mismatch
    # says WHICH name the service published — the difference between "the OTel resource is
    # missing" and "Otel__ServiceName was not honoured".
    probe_service=$(grep -m1 '^target_info{' <<<"$scrape" \
      | grep -oE 'service_name="[^"]*"' | cut -d'"' -f2 || echo "")
    check "the service publishes its OTel resource on target_info" "c119-service-probe" "${probe_service:-<no target_info>}"

    # And the same request, as a span, in Tempo — through the collector.
    found=no
    for _ in $(seq 1 25); do
      hits=$(curl -sf --get "$TEMPO_URL/api/search" \
               --data-urlencode 'q={resource.service.name="c119-service-probe"}' \
               --data-urlencode 'limit=5' 2>/dev/null \
             | jq -r '.traces | length' 2>/dev/null || echo 0)
      if [[ "${hits:-0}" -gt 0 ]]; then found=yes; break; fi
      sleep 4
    done
    check "a real service's trace reached Tempo over OTLP" "yes" "$found"
  fi

  docker rm -f c119-otlp-probe >/dev/null 2>&1 || true
fi

fi  # VERIFY_OBS_STATIC

# =====================================================================================
printf '\n%s==> %s checks, %s failures, %s skipped%s\n' \
  "$YELLOW" "$CHECKS" "$FAILURES" "$SKIPS" "$RESET"

if [[ "${VERIFY_OBS_KEEP:-}" == "1" || "${VERIFY_OBS_REUSE:-}" == "1" ]]; then
  printf '%sStack left running — Grafana on %s, Prometheus on %s.%s\n' \
    "$YELLOW" "$GRAFANA_URL" "$PROM_URL" "$RESET"
fi

if (( FAILURES > 0 )); then
  printf '%sC119 verify FAILED%s\n' "$RED" "$RESET"
  exit 1
fi

printf '%sC119 verify passed%s\n' "$GREEN" "$RESET"
