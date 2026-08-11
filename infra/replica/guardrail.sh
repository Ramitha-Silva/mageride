#!/usr/bin/env bash
# =====================================================================================
# infra/replica/guardrail.sh — what has to be true before the replica is allowed to start,
# and what has to stay true while it runs.
#
#   bash infra/replica/guardrail.sh              # the pre-flight checks
#   bash infra/replica/guardrail.sh --running    # also compare live usage against the budget
#
# WHY THIS IS NOT ADVISORY. The root CLAUDE.md: this repository is built ON the box that hosts the
# replica, and ~19 GB of replica plus a `dotnet build` does not fit in 24 GB. What happens when they
# overlap is not a slow build — it is the OOM killer choosing a victim, and the victim is whichever
# process asked for memory last. A half-killed Postgres in a stack somebody is demoing is a worse
# outcome than a refused deploy, so `deploy.sh` calls this first and stops if it fails.
#
# THE BUDGET IS READ FROM THE SPEC, NOT WRITTEN HERE. specs/lightweight-production-replica.md has a
# resource table; this script parses it. Three reasons: a number copied here is a number that drifts,
# the prompt's DoD says "~18.9 GB" while the spec's own totals are 16.7 GB (core 11) and 19.7 GB
# (core + voip + both portals + monitoring) so there is no single figure to copy anyway, and a
# guardrail that disagrees with the spec is worse than none because it will be silenced.
# =====================================================================================
set -uo pipefail

cd "$(dirname -- "${BASH_SOURCE[0]}")/../.." || exit 2

SPEC="specs/lightweight-production-replica.md"
COMPOSE="infra/replica/docker-compose.light-replica.yml"

failures=0
warnings=0
pass=0

ok()   { pass=$((pass + 1));      printf '  \033[32m✓\033[0m %s\n' "$*"; }
bad()  { failures=$((failures+1)); printf '  \033[31m✗\033[0m %s\n' "$*" >&2; }
warn() { warnings=$((warnings+1)); printf '  \033[33m!\033[0m %s\n' "$*"; }
head_(){ printf '\n\033[1m%s\033[0m\n' "$*"; }

check_running=0
[ "${1:-}" = "--running" ] && check_running=1

# The compose file marks four values `${VAR:?}`, so EVERY `docker compose` call in this script needs
# them — including `ps`. Without this the --running check reported "nothing is running under the
# mageride-replica project" while all eleven containers were healthy: a false negative in the one
# check whose job is to notice the stack outgrowing its budget.
if [ -f infra/replica/.env.replica ]; then
  set -a
  # shellcheck disable=SC1091
  . infra/replica/.env.replica
  set +a
fi

# -------------------------------------------------------------------------------------
head_ "1. is a heavy build running?"
# -------------------------------------------------------------------------------------
# The exact fence the root CLAUDE.md draws. `pgrep -f` on the command line rather than on the
# process name: `dotnet` is the process name for the build, the test host, and every service in the
# stack, so matching the name would refuse to start the replica because the replica is running.
# Our own process tree is excluded. `pgrep -f` matches a COMMAND LINE, so the shell that invoked
# this script trips every pattern the moment the invoking command mentions one — including
# `bash -c "... dotnet build ..."`, a CI step, or an editor. The first version did exactly that and
# reported a heavy build that was its own parent. A guardrail that cries wolf gets silenced, which is
# the one outcome this file's header says it must avoid.
ancestors=" $$ "
walk=$$
while [ "$walk" -gt 1 ]; do
  walk=$(ps -o ppid= -p "$walk" 2>/dev/null | tr -d ' ')
  [ -z "$walk" ] && break
  ancestors="${ancestors}${walk} "
done

heavy=""
while IFS= read -r line; do
  [ -z "$line" ] && continue
  pid=${line%% *}
  # Skip ourselves, our ancestors, and any child we spawned to do the matching.
  case "$ancestors" in *" $pid "*) continue ;; esac
  [ "$pid" = "$$" ] && continue
  heavy="${heavy}${line}\n"
done < <(
  pgrep -af 'dotnet (build|test|publish|restore)' 2>/dev/null
  pgrep -af 'docker (build|buildx build)|buildkitd' 2>/dev/null
  pgrep -af 'gradlew|gradle-launcher|GradleDaemon' 2>/dev/null
  pgrep -af 'npm run build|next build' 2>/dev/null
)

if [ -n "$heavy" ]; then
  bad "a heavy build is running — the replica and a build do not fit in this box together:"
  printf '%b' "$heavy" | sed 's/^/      /' >&2
  echo "      wait for it, or stop it, then run this again." >&2
else
  ok "no dotnet/docker/gradle/next build in flight"
fi

# -------------------------------------------------------------------------------------
head_ "2. the budget, read from the spec's own resource table"
# -------------------------------------------------------------------------------------
if [ ! -f "$SPEC" ]; then
  bad "$SPEC not found — cannot derive a budget, and will not invent one"
  core_mib=0
else
  # budget.py parses the spec's Resource Summary section. Scoped to that section deliberately: the
  # first version read every table in the file and invented a 12th core container from
  # "| Geocoding (Nominatim) | Dedicated 8 GB Postgres |" in the comparison table at the top, which
  # put the budget at 24.6 GiB against a 24 GiB box and refused a deploy that fits.
  totals_json=$(python3 infra/replica/budget.py totals 2>&1)

  if ! printf '%s' "$totals_json" | python3 -c "import json,sys; json.load(sys.stdin)" 2>/dev/null; then
    bad "could not read the budget out of $SPEC:"
    printf '%s\n' "$totals_json" | tail -3 | sed 's/^/      /' >&2
    core_mib=0
  else
    core_mib=$(printf '%s' "$totals_json" | python3 -c "import json,sys; print(json.load(sys.stdin)['core_mib'])")
    core_count=$(printf '%s' "$totals_json" | python3 -c "import json,sys; print(len(json.load(sys.stdin)['core_containers']))")
    elsewhere=$(printf '%s' "$totals_json" | python3 -c "import json,sys; print(' '.join(json.load(sys.stdin)['elsewhere']) or 'none')")

    ok "the spec budgets ${core_mib} MiB for ${core_count} core containers ($(awk "BEGIN{printf \"%.2f\", ${core_mib}/1024}") GiB)"
    ok "hosted elsewhere and therefore not counted here: ${elsewhere}"

    # Every limit in the compose file must equal the spec's row. A container given more than the spec
    # budgeted is how a stack that "fits" stops fitting.
    drift_out=$(python3 infra/replica/budget.py drift "$COMPOSE" 2>&1)
    drift_status=$?

    real_drift=$(printf '%s\n' "$drift_out" | grep -v '^DEVIATION ' | grep -v '^$' || true)
    deviations=$(printf '%s\n' "$drift_out" | grep '^DEVIATION ' || true)

    if [ "$drift_status" -ne 0 ]; then
      bad "the compose file could not be checked against the spec:"
      printf '%s\n' "$drift_out" | tail -3 | sed 's/^/      /' >&2
    elif [ -n "$real_drift" ]; then
      bad "the compose file and the spec disagree:"
      printf '%s\n' "$real_drift" | sed 's/^/      /' >&2
    else
      ok "every container's limit matches the spec, or is a declared deviation"
    fi

    # Reported every run, never silent: an accepted deviation nobody sees again is drift.
    if [ -n "$deviations" ]; then
      while IFS= read -r line; do
        [ -n "$line" ] && warn "${line#DEVIATION }"
      done <<< "$deviations"
    fi
  fi
fi

# -------------------------------------------------------------------------------------
head_ "3. does this box have room for it?"
# -------------------------------------------------------------------------------------
total_mib=$(free -m | awk '/^Mem:/ {print $2}')
avail_mib=$(free -m | awk '/^Mem:/ {print $7}')
need_mib=${core_mib:-0}

# 2 GiB for the OS, the Docker daemon and page cache. The spec's own arithmetic leaves ~4.3 GB of
# headroom on a 24 GB box; this is the floor below which the OOM killer becomes likely rather than
# possible.
reserve_mib=2048

ok "host: ${total_mib} MiB total, ${avail_mib} MiB available"

if [ "$need_mib" -gt 0 ]; then
  if [ $((need_mib + reserve_mib)) -gt "$total_mib" ]; then
    bad "the replica needs ${need_mib} MiB + ${reserve_mib} MiB reserve, and this box has ${total_mib} MiB"
  else
    ok "budget + reserve fits: $((need_mib + reserve_mib)) MiB of ${total_mib} MiB"
  fi

  if [ "$avail_mib" -lt "$need_mib" ]; then
    warn "only ${avail_mib} MiB is available right now. Nothing else is stopping the deploy, but the
      stack will start into a box that is already using $((total_mib - avail_mib)) MiB."
  fi
fi

# -------------------------------------------------------------------------------------
head_ "4. what the replica needs on disk before it starts"
# -------------------------------------------------------------------------------------
if [ -f infra/deploy/device-ca/certs/ca_chain.crt ]; then
  ok "the device CA chain exists (EMQX reads it as its 8883 cacertfile at listener start)"
else
  # Not a failure: deploy.sh generates it. A failure here would make the first deploy impossible.
  warn "infra/deploy/device-ca/certs/ca_chain.crt is absent — deploy.sh will generate it
      (infra/scripts/ensure-device-ca.sh). EMQX cannot start its TLS listener without it."
fi

if [ -f infra/deploy/certs/replica.pem ]; then
  # Named explicitly rather than globbed: the dev stack's mageride-dev.pem sitting in the same
  # directory would satisfy a glob and is not the file haproxy.replica.cfg names.
  ok "infra/deploy/certs/replica.pem exists (the file haproxy.replica.cfg binds)"
else
  warn "no certificate in infra/deploy/certs — deploy.sh generates a self-signed one. It is a
      REPLICA certificate: nothing trusts it, and nothing should."
fi

free_gib=$(df -BG --output=avail / | tail -1 | tr -dc '0-9')
if [ "${free_gib:-0}" -lt 20 ]; then
  bad "only ${free_gib} GiB free on / — the images alone are ~4 GiB and Postgres grows"
else
  ok "${free_gib} GiB free on /"
fi

# -------------------------------------------------------------------------------------
if [ "$check_running" = 1 ]; then
  head_ "5. live usage against the budget"

  if ! docker compose -f "$COMPOSE" ps --quiet 2>/dev/null | grep -q .; then
    warn "nothing is running under the mageride-replica project — skipping the live comparison"
  else
    live=$(docker stats --no-stream --format '{{.Name}} {{.MemUsage}}' 2>/dev/null | grep '^mageride-replica' || true)
    if [ -z "$live" ]; then
      warn "docker stats returned nothing"
    else
      printf '%s\n' "$live" | sed 's/^/      /'
      used_mib=$(printf '%s' "$live" | awk '{print $2}' | python3 -c "
import sys
total = 0.0
for line in sys.stdin:
    v = line.strip()
    if not v: continue
    unit = v[-3:].upper()
    number = float(v[:-3])
    total += number * {'MIB': 1, 'GIB': 1024, 'KIB': 1/1024}.get(unit, 0)
print(int(total))
")
      if [ "${used_mib:-0}" -gt "${core_mib:-0}" ]; then
        bad "live usage ${used_mib} MiB exceeds the spec's budget ${core_mib} MiB"
      else
        ok "live usage ${used_mib} MiB is within the spec's ${core_mib} MiB"
      fi
    fi
  fi
fi

# -------------------------------------------------------------------------------------
echo
echo "==============================================================================="
printf '%d passed, %d failed, %d warning(s)\n' "$pass" "$failures" "$warnings"
[ "$failures" -ne 0 ] && exit 1
exit 0
