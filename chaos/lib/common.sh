#!/usr/bin/env bash
# =====================================================================================
# chaos/lib/common.sh — the things every drill needs: how to reach the replica, how to read
# what it did, and how to say what happened.
#
# Sourced, never executed. `run-drills.sh` sources it once and exports nothing a drill has to
# re-derive.
# =====================================================================================

# -------------------------------------------------------------------------------------
# Where we are
# -------------------------------------------------------------------------------------
CHAOS_DIR="${CHAOS_DIR:?common.sh must be sourced by run-drills.sh}"
REPO_ROOT="${REPO_ROOT:?}"
COMPOSE="${COMPOSE:-infra/replica/docker-compose.light-replica.yml}"
PROJECT="${PROJECT:-mageride-replica}"

# -------------------------------------------------------------------------------------
# Output. Two streams on purpose: the terminal gets colour, the report gets markdown.
# -------------------------------------------------------------------------------------
CHAOS_PASS=0
CHAOS_FAIL=0
CHAOS_FINDINGS=0

# The report is assembled in a file rather than a variable: a drill that kills the box mid-run
# should still leave everything it had already observed on disk.
REPORT_OUT="${REPORT_OUT:-}"

# A markdown table cannot have a bullet in the middle of it. `degraded_table_open` sets this flag
# and everything that is NOT a table row is held back until `degraded_table_close`, because a
# finding raised while the §14.1 comparison is being written would otherwise split one table into
# two and orphan the header — which is exactly what drill 10's `limitedLive` finding did.
CHAOS_TABLE_OPEN=0
CHAOS_DEFERRED=""

report() {
  [ -n "$REPORT_OUT" ] || return 0
  if [ "$CHAOS_TABLE_OPEN" = "1" ]; then
    CHAOS_DEFERRED="${CHAOS_DEFERRED}$*
"
  else
    printf '%s\n' "$*" >> "$REPORT_OUT"
  fi
  return 0
}

# Always written, never deferred — the table's own rows.
report_raw() { [ -n "$REPORT_OUT" ] && printf '%s\n' "$*" >> "$REPORT_OUT"; return 0; }

step()  { printf '\n\033[1m%s\033[0m\n' "$*"; }
ok()    { CHAOS_PASS=$((CHAOS_PASS + 1)); printf '  \033[32m✓\033[0m %s\n' "$*"; report "- ✅ $*"; }
bad()   { CHAOS_FAIL=$((CHAOS_FAIL + 1)); printf '  \033[31m✗\033[0m %s\n' "$*" >&2; report "- ❌ $*"; }
note()  { printf '    %s\n' "$*"; report "- $*"; }
warn()  { printf '  \033[33m!\033[0m %s\n' "$*"; report "- ⚠️ $*"; }
die()   { printf '  \033[31m✗\033[0m %s\n' "$*" >&2; exit 1; }

# A FINDING is the deliverable of this component, not an error. It is what the drill saw that the
# documented behaviour does not describe — recorded loudly, counted separately, and never allowed
# to be mistaken for a drill that failed to run.
#
#   finding HIGH "..."   the documented behaviour does not happen and something is lost by it
#   finding MED  "..."   the behaviour differs from the document, or the document is wrong
#   finding LOW  "..."   true, worth writing down, nobody is paged for it
#
# The severity is a WORD rather than a number because it is written into the report and read by a
# person. `run-drills.sh` counts them and prints the HIGH ones again in its summary; it does not
# turn them into an exit code — see chaos/README.md on why the exit code is a statement about the
# suite and not a verdict on the platform.
CHAOS_HIGH_FINDINGS=""
finding() {
  local severity="$1"; shift
  CHAOS_FINDINGS=$((CHAOS_FINDINGS + 1))
  printf '  \033[35m▲ %s\033[0m %s\n' "$severity" "$*"
  report "- **▲ ${severity}** — $*"
  [ "$severity" = "HIGH" ] && CHAOS_HIGH_FINDINGS="${CHAOS_HIGH_FINDINGS}  ▲ [${CHAOS_DRILL_ID:-?}] $*
"
  return 0
}

# -------------------------------------------------------------------------------------
# The replica
# -------------------------------------------------------------------------------------
dc() { docker compose -f "$COMPOSE" "$@"; }

psql_q() {
  dc exec -T postgres psql -U "${PG_USER:-mageride}" -d "${PG_DATABASE:-mageride}" -qtAX -c "$1" 2>&1
}

# Trims psql's trailing whitespace and CR so `[ "$x" = "1" ]` works.
psql_one() { psql_q "$1" | head -1 | tr -d ' \r'; }

redis_cli() { dc exec -T redis redis-cli "$@" 2>&1; }
rpk()       { dc exec -T redpanda rpk "$@" 2>&1; }
emqx_ctl()  { dc exec -T emqx /opt/emqx/bin/emqx ctl "$@" 2>&1; }

# `curl` is in app-services' image and `wget` is in the alpine runtime ones; both are tried
# because the two Dockerfiles do not carry the same tools. Scraped from INSIDE the network: no
# service but the edge publishes a port, which is Container 1's whole point.
metrics_of() {
  local service="$1" port="$2"
  dc exec -T "$service" sh -c \
    "curl -fsS http://127.0.0.1:${port}/metrics 2>/dev/null || wget -qO- http://127.0.0.1:${port}/metrics 2>/dev/null" 2>/dev/null
}

# `metric <scrape> <name>` — the summed value of every labelled series of one counter.
#
# Summed, not `head -1`: OpenTelemetry's Prometheus exporter writes one line per label set, so
# `mageride_positions_dropped_total` alone is six series and reading the first one silently
# reports the count for whichever `reason` sorted first.
metric() {
  printf '%s\n' "$1" | awk -v want="$2" '
    $0 ~ "^"want"([{ ]|$)" { for (i = NF; i >= 1; i--) if ($i ~ /^[0-9.eE+-]+$/) { total += $i; break } }
    END { printf "%d", total + 0 }'
}

# -------------------------------------------------------------------------------------
# Time. Every measurement in this suite is a wall-clock delta on the host, in milliseconds,
# taken the same way, so two drills' numbers are comparable.
# -------------------------------------------------------------------------------------
now_ms() { date +%s%3N; }

since_ms() { echo $(( $(now_ms) - $1 )); }

# `human_ms <ms>` — 850 ms / 12.4 s / 3 m 05 s, because an RTO written in milliseconds is not
# something anyone can compare against "30 minutes" at a glance.
human_ms() {
  local ms="$1"
  if [ "$ms" -lt 1000 ]; then printf '%d ms' "$ms"
  elif [ "$ms" -lt 60000 ]; then printf '%.1f s' "$(echo "$ms" | awk '{print $1/1000}')"
  else printf '%d m %02d s' $((ms / 60000)) $(((ms % 60000) / 1000))
  fi
}

# -------------------------------------------------------------------------------------
# Waiting for things
# -------------------------------------------------------------------------------------

# `wait_for <timeout-seconds> <interval-seconds> <predicate...>` — returns 0 and echoes the
# elapsed milliseconds as soon as the predicate succeeds, 1 (and the elapsed) on timeout.
#
# The elapsed time is the RETURN VALUE of this function, because in every drill here the answer
# to "did it recover" is less interesting than "how long did it take", and a helper that only
# answered the first would have every caller time it again with a different clock.
wait_for() {
  local timeout="$1" interval="$2"; shift 2
  local started deadline
  started=$(now_ms)
  deadline=$(( started + timeout * 1000 ))

  while [ "$(now_ms)" -lt "$deadline" ]; do
    if "$@" >/dev/null 2>&1; then
      since_ms "$started"
      return 0
    fi
    sleep "$interval"
  done

  since_ms "$started"
  return 1
}

# Every container the compose file declares as a core service is running AND healthy.
# `migrate`, `redpanda-init` and `minio-init` are one-shots and are excluded by construction:
# `--filter status=running` never lists them once they have exited 0.
stack_healthy() {
  local unhealthy
  unhealthy=$(dc ps --format '{{.Service}} {{.Health}}' 2>/dev/null \
              | awk '$2 != "healthy" && $2 != "" {print $1}' | tr '\n' ' ')
  [ -z "${unhealthy// /}" ]
}

# One service, by compose name.
service_healthy() {
  local health
  health=$(dc ps --format '{{.Service}} {{.Health}}' 2>/dev/null | awk -v s="$1" '$1 == s {print $2}')
  [ "$health" = "healthy" ]
}

# -------------------------------------------------------------------------------------
# The edge — every probe in this suite goes through HAProxy on 443, for smoke.sh's reason:
# talking to app-services:5000 would skip TLS termination, the forwarded headers, the /health
# and /v1/internal denials and the vhost routing, which is most of what the edge exists to do.
# -------------------------------------------------------------------------------------
edge_curl() {
  # -k because the replica's certificate is self-signed BY DESIGN; trusting it on this box would
  # make the suite pass for a reason a phone never enjoys.
  curl -sS -k --max-time "${CHAOS_HTTP_TIMEOUT:-20}" -H "Host: ${HOSTHDR}" "$@"
}

# The HTTP status, or `000` when curl never got one.
#
# The `|| echo 000` fallback that used to be here produced `000000` on a timeout: curl writes
# `%{http_code}` — which is already `000` when there was no response — AND exits non-zero, so both
# halves fired. Every `case "$code" in 000)` branch in the drills then missed, and drill 63 read a
# hung edge as "answered 000000" instead of raising its finding. One capture, one normalisation.
edge_code() {
  local path="$1"; shift
  local code
  code=$(edge_curl -o /dev/null -w '%{http_code}' "$@" "${EDGE}${path}" 2>/dev/null)
  case "$code" in
    [1-5][0-9][0-9]) printf '%s' "$code" ;;
    *) printf '000' ;;
  esac
}

edge_body() {
  local path="$1"; shift
  edge_curl "$@" "${EDGE}${path}" 2>/dev/null || true
}

# An authenticated GET, as a passenger's app makes it.
edge_get_as() {
  local bearer="$1" path="$2"
  edge_curl -H "Authorization: Bearer ${bearer}" "${EDGE}${path}" 2>/dev/null || true
}

edge_post_as() {
  local bearer="$1" path="$2" body="${3:-}"
  local args=(-X POST -H "Authorization: Bearer ${bearer}" -H 'Content-Type: application/json'
              -H "Idempotency-Key: $(openssl rand -hex 16)")
  [ -n "$body" ] && args+=(-d "$body")
  edge_curl "${args[@]}" "${EDGE}${path}" 2>/dev/null || true
}

# -------------------------------------------------------------------------------------
# chaos/env.json — the fixture credentials, written by configure.sh
# -------------------------------------------------------------------------------------
ENV_JSON="${CHAOS_DIR}/env.json"

env_json() { jq -r "$1 // empty" "$ENV_JSON" 2>/dev/null; }

require_fixture() {
  [ -f "$ENV_JSON" ] || die "chaos/env.json is absent — run \`bash chaos/configure.sh\` first"

  local bearer
  bearer=$(env_json '.passengers[0].bearer')
  [ -n "$bearer" ] || die "chaos/env.json carries no passenger bearer — re-run configure.sh"

  # A 30-minute access token (D-29). An expired one answers 401 on every probe in this suite and
  # the drills would read as a platform that refuses everything under fault — which is exactly
  # the conclusion a chaos report must never reach for the wrong reason.
  local code
  code=$(edge_curl -o /dev/null -w '%{http_code}' -H "Authorization: Bearer ${bearer}" \
         "${EDGE}/v1/rides/active" 2>/dev/null || echo 000)
  case "$code" in
    401|403) die "the fixture bearers have expired (GET /v1/rides/active answered ${code}).
      They live 30 minutes — re-run \`bash chaos/configure.sh\`." ;;
    000) die "the edge did not answer at ${EDGE}. Is the replica up?" ;;
    *) ;;
  esac
}
