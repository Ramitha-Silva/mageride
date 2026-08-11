#!/usr/bin/env bash
# =====================================================================================
# infra/replica/gtfs-day0-verify.sh — C126's definition of done, checked against the replica.
#
#   bash infra/replica/gtfs-day0-verify.sh
#
# `gtfs-day0-load.sh` performs the day-0 operation; this decides whether it happened and whether
# what it left behind is right. Read-only: it activates nothing, uploads nothing and writes nothing
# to the journal, so it can be re-run at any time — including months later, to ask whether the feed
# that is live is still the feed that was verified.
#
# ------------------------------------------------------------------------------------
# WHY IT READS A JOURNAL AS WELL AS THE DEPLOYMENT
# ------------------------------------------------------------------------------------
# Three of C126's five done-items are not observable after the fact. The pre-first-import empty
# state stops existing the moment a feed is activated. An activation's elapsed time and a
# rollback's are gone once they are over. Those three come from `gtfs-day0-journal.json`, written by
# the load script at the moment each happened.
#
# EVERYTHING ELSE IS RE-DERIVED LIVE — the active version, the row counts, the corridor answers, the
# shapes, the one-active-row invariant, the audit trail. A journal that claimed a corridor worked
# would not make this pass; the corridor is asked again, now, through the edge.
# =====================================================================================
set -uo pipefail

# shellcheck source=infra/replica/gtfs-lib.sh
. "$(dirname -- "${BASH_SOURCE[0]}")/gtfs-lib.sh"

RELOAD_BOUND="${GTFS_RELOAD_BOUND:-60}"

# =====================================================================================
step "0. the replica is up and the GTFS surface is reachable"
# =====================================================================================
running=$(docker compose -f "$COMPOSE" ps --services --filter status=running 2>/dev/null | wc -l)
if [ "$running" -eq 0 ]; then
  echo "  nothing is running under mageride-replica — bring it up with deploy.sh" >&2
  exit 2
fi
ok "${running} replica services running"

unhealthy=$(docker compose -f "$COMPOSE" ps --format '{{.Service}} {{.Health}}' 2>/dev/null \
            | awk '$2 != "healthy" && $2 != "" {print $1"("$2")"}' | tr '\n' ' ')
if [ -n "$unhealthy" ]; then
  bad "not healthy: ${unhealthy}"
else
  ok "every container with a healthcheck is healthy"
fi

# Deny-by-default, from the outside (AL-06, D2 SCR-AP-016: Admin and Super Admin only). Checked
# before a token is obtained, because afterwards the shape of the refusal is unobservable.
api GET /v1/admin/transit/gtfs/versions
case "$API_STATUS" in
  401|403) ok "the GTFS admin surface refuses an unauthenticated caller (${API_STATUS})" ;;
  200) bad "GET /v1/admin/transit/gtfs/versions answered 200 with NO credential — the feed history is public" ;;
  502|503|504) die "the edge answered ${API_STATUS} — no transit-svc behind the gateway" ;;
  404) die "404 — the gateway has no route for /v1/admin/transit/** (gateway-routes.json)" ;;
  *) warn "unauthenticated GET …/versions answered ${API_STATUS}" ;;
esac

if [ ! -f "$JOURNAL" ]; then
  echo >&2
  echo "  No day-0 journal at ${JOURNAL}." >&2
  echo "  The day-0 load has not run on this replica. Run it — the feed is an externally provided" >&2
  echo "  file, so obtain it first (docs/runbooks/gtfs-day0-load.md §1):" >&2
  echo >&2
  echo "      bash infra/replica/gtfs-day0-load.sh --feed <national-feed.zip> --previous <prior.zip>" >&2
  echo >&2
  exit 2
fi
ok "day-0 journal found, run started $(journal_get '.startedAt')"

require_token
ok "signed in as the day-0 operator ($(journal_get '.operator.email'))"

# =====================================================================================
step "1. the pre-first-import empty state was observed and documented (DoD 4, AL-55)"
# =====================================================================================
observed=$(journal_get '.emptyState.observedAt')
if [ -z "$observed" ]; then
  bad "no empty state was ever recorded. It is not reconstructable once a feed is active — the
      record has to be made on a database with no feed version in it."
else
  ok "observed at ${observed}, before any activation"

  coverage_then=$(journal_get '.emptyState.coverage')
  if [ "$coverage_then" = "no_feed" ]; then
    ok "it answered coverage=no_feed — AL-55's degradation, recorded as a state that was seen rather than assumed"
  else
    bad "the recorded empty state says coverage=${coverage_then}, not no_feed"
  fi

  empty_rows=$(journal_get '.emptyState.liveCounts.routes')
  empty_versions=$(journal_get '.emptyState.liveCounts.versions')
  if [ "${empty_rows:-x}" = "0" ] && [ "${empty_versions:-x}" = "0" ]; then
    ok "transit.gtfs_* and the version ledger were both empty when it was recorded"
  else
    bad "the recorded empty state is not empty: routes=${empty_rows}, versions=${empty_versions}"
  fi

  first_activation=$(journal_get '.versions.previous')
  [ -n "$first_activation" ] || first_activation=$(journal_get '.versions.current')
  activated_at=$(journal_get ".activation.\"${first_activation}\".committedAt")
  if [ -z "$activated_at" ] || [ "$observed" \< "$activated_at" ]; then
    ok "the observation precedes the first activation${activated_at:+ (${activated_at})}"
  else
    bad "the empty state was recorded at ${observed}, AFTER the first activation at ${activated_at}"
  fi
fi

# =====================================================================================
step "2. the full feed validated, and it is the file the provider sent (DoD 1, AL-56)"
# =====================================================================================
version=$(journal_get '.versions.current')
if [ -z "$version" ]; then
  echo >&2
  echo "  The journal names no day-0 feed version, so nothing has been activated on this replica." >&2
  echo "  The baseline above is recorded and will be kept; what is missing is the feed itself, which" >&2
  echo "  is an externally provided file (AL-56):" >&2
  echo >&2
  echo "      cp <current-release>.zip  infra/replica/gtfs/national.zip" >&2
  echo "      cp <prior-release>.zip    infra/replica/gtfs/national-previous.zip" >&2
  echo "      bash infra/replica/gtfs-day0-load.sh" >&2
  echo >&2
  echo "  docs/runbooks/gtfs-day0-load.md §1 is how to obtain it, §9 is the state of this replica." >&2
  echo >&2
  exit 2
fi

row=$(psql_q "
  SELECT json_build_object(
    'status', status, 'sha256', sha256, 'file', file_name, 'bytes', file_size_bytes,
    'feedInfoVersion', feed_info_version, 'serviceStart', service_start, 'serviceEnd', service_end,
    'errors', coalesce(jsonb_array_length(validation_report -> 'errors'), 0),
    'warnings', coalesce(jsonb_array_length(validation_report -> 'warnings'), 0),
    'activatedAt', activated_at, 'uploadedBy', uploaded_by)::text
  FROM transit.gtfs_feed_versions WHERE feed_version_id = '${version}';" | tr -d '\r' | tail -1)

case "$row" in
  '{'*) : ;;
  *) die "the journal's day-0 version ${version} is not in transit.gtfs_feed_versions: ${row}" ;;
esac

status=$(printf '%s' "$row" | jq -r '.status')
if [ "$status" = "active" ]; then
  ok "version ${version} is ACTIVE"
else
  bad "version ${version} is '${status}', not active — the day-0 feed is not live"
fi

if [ "$(printf '%s' "$row" | jq -r '.sha256')" = "$(journal_get '.feed.sha256')" ]; then
  ok "its sha256 is the sha256 of the file that was handed over — nothing edited the feed in flight (AL-56)"
else
  bad "the stored version's sha256 differs from the uploaded file's. Something re-packed the feed."
fi

errors=$(printf '%s' "$row" | jq -r '.errors')
if [ "$errors" = "0" ]; then
  ok "the validation report carries no errors ($(printf '%s' "$row" | jq -r '.warnings') warning(s), which never block — BR-32.1)"
elif [ "$status" = "active" ]; then
  bad "${errors} validation error(s) on the ACTIVE feed. Errors block activation, so this should have been impossible."
else
  bad "${errors} validation error(s), which is why this version is '${status}'. Errors block activation
      (BR-32.1); the row-level report is what the provider needs:
      GET /v1/admin/transit/gtfs/uploads/${version}/report?format=csv"
fi

note "feed_info version $(printf '%s' "$row" | jq -r '.feedInfoVersion // "(none)"'), service window $(printf '%s' "$row" | jq -r '(.serviceStart // "?") + " → " + (.serviceEnd // "?")')"

# The service window against today, because a feed that validated in April and expires in June is a
# perfectly valid feed and a broken deployment. BR-32.1 warns at < 30 days; this is the same
# question asked later, when the answer has changed.
service_end=$(printf '%s' "$row" | jq -r '.serviceEnd // ""')
if [ -n "$service_end" ]; then
  days_left=$(( ( $(date -u -d "$service_end" +%s) - $(date -u +%s) ) / 86400 ))
  if [ "$days_left" -lt 0 ]; then
    bad "the active feed's service window ENDED ${days_left#-} days ago. Every corridor now depends on
      stale schedules, and SCR-PA-009's safety net is what passengers are seeing."
  elif [ "$days_left" -lt 30 ]; then
    warn "the active feed's service window ends in ${days_left} days (BR-32.1 warns under 30) — ask the provider for the next release"
  else
    ok "the service window has ${days_left} days left"
  fi
fi

# =====================================================================================
step "3. exactly one active feed, and the live tables hold it (BR-32.2)"
# =====================================================================================
counts=$(live_counts_json)
active_rows=$(printf '%s' "$counts" | jq -r '.active')

if [ "$active_rows" = "1" ]; then
  ok "one row is active — ux_gtfs_feed_one_active, enforced by the index rather than by code"
else
  bad "${active_rows} rows are 'active'"
fi

for pair in routes:1 trips:1 stops:1 stop_times:1; do
  table="${pair%%:*}"; floor="${pair##*:}"
  value=$(printf '%s' "$counts" | jq -r ".${table}")
  if [ "${value:-0}" -ge "$floor" ] 2>/dev/null; then
    ok "transit.gtfs_${table}: ${value} row(s)"
  else
    bad "transit.gtfs_${table} is empty — the swap did not bring the dataset across"
  fi
done

shapes=$(printf '%s' "$counts" | jq -r '.shapes')
if [ "${shapes:-0}" -ge 1 ] 2>/dev/null; then
  ok "transit.gtfs_shapes: ${shapes} row(s)"
else
  warn "transit.gtfs_shapes is empty — the feed ships no shapes.txt, so no option can draw a route line (the feed's to provide, AL-56)"
fi

# The importer's row counts against the validator's, which counted the same files independently.
declared=$(psql_q "SELECT (counts ->> 'routes')::bigint FROM transit.gtfs_feed_versions WHERE feed_version_id = '${version}';" | tr -d ' \r' | tail -1)
live_routes=$(printf '%s' "$counts" | jq -r '.routes')
if [ -z "$declared" ]; then
  warn "the version carries no per-file counts, so there is nothing to compare the ${live_routes} live routes against"
elif [ "$declared" = "$live_routes" ]; then
  ok "the live route count equals what validation counted in the zip (${declared})"
else
  warn "validation counted ${declared} routes in the file and ${live_routes} are live. A feed whose
      routes.txt names routes no trip serves legitimately loads fewer; anything larger is a bug."
fi

# =====================================================================================
step "4. the cache reloaded inside the bound (DoD 2, US-28.2)"
# =====================================================================================
reload=$(journal_get ".cacheReload.\"${version}\".elapsedSec")
if [ -z "$reload" ]; then
  bad "no cache-reload timing was recorded for ${version}"
elif python3 -c "import sys; sys.exit(0 if float(sys.argv[1]) <= float(sys.argv[2]) else 1)" "$reload" "$RELOAD_BOUND"; then
  ok "the cache published the day-0 feed ${reload}s after the swap committed (bound ${RELOAD_BOUND}s)"
  note "evidence: $(journal_get ".cacheReload.\"${version}\".evidence")"
else
  bad "the cache took ${reload}s to publish the day-0 feed; US-28.2's bound is ${RELOAD_BOUND}s"
fi

activation_secs=$(journal_get ".activation.\"${version}\".elapsedSec")
[ -n "$activation_secs" ] && ok "the swap itself took ${activation_secs}s (a catalogue rename, so it does not grow with the feed)"

# And the cache is serving it NOW, which is a different claim from "it reloaded once".
options_for 6.9366 79.8524 6.8412 79.9647
probe="$API_BODY"
coverage=$(printf '%s' "$probe" | jq -r '.coverage // "unreadable"')
if [ "$coverage" = "active" ]; then
  ok "/v1/transit/options answers coverage=active right now"
else
  bad "/v1/transit/options answers coverage=${coverage} — the live cache is not serving a feed"
fi

# =====================================================================================
step "5. the corridor sample set, asked again now (DoD 2 and 3)"
# =====================================================================================
tolerance=$(jq -r '.shapeToleranceM' "$CORRIDORS")
bbox=$(jq -r '"\(._bbox.minLat),\(._bbox.maxLat),\(._bbox.minLng),\(._bbox.maxLng)"' "$CORRIDORS")
total=0; with_direct=0; with_hint=0; shapes_ok=0; shapes_bad=0

while IFS= read -r corridor; do
  total=$((total+1))

  label=$(printf '%s' "$corridor" | jq -r '.label')
  from_lat=$(printf '%s' "$corridor" | jq -r '.from.lat'); from_lng=$(printf '%s' "$corridor" | jq -r '.from.lng')
  to_lat=$(printf '%s' "$corridor" | jq -r '.to.lat');     to_lng=$(printf '%s' "$corridor" | jq -r '.to.lng')

  options_for "$from_lat" "$from_lng" "$to_lat" "$to_lng"
  answer="$API_BODY"
  if [ "$API_STATUS" != "200" ]; then
    bad "${label}: /v1/transit/options answered ${API_STATUS}"
    continue
  fi

  direct=$(printf '%s' "$answer" | jq '[.options[] | select(.kind | ascii_downcase == "direct")] | length')
  names=$(printf '%s' "$answer" | jq -r '[.options[] | select(.kind | ascii_downcase == "direct") | .legs[0].routeShortName] | unique | join(", ")')

  if [ "$direct" -ge 1 ]; then
    with_direct=$((with_direct+1))
    ok "${label}: ${direct} direct — ${names}"
  else
    bad "${label}: no direct route in the active feed"
  fi

  got=$(printf '%s' "$answer" | jq -c '[.options[] | .legs[] | .routeShortName]')
  if printf '%s' "$corridor" | jq -e --argjson got "$got" \
       '[.expectRoutes[] | select(. as $e | $got | index($e) != null)] | length > 0' >/dev/null 2>&1; then
    with_hint=$((with_hint+1))
  else
    warn "${label}: none of the hinted routes ($(printf '%s' "$corridor" | jq -r '.expectRoutes | join(", ")')) appear — a question for the provider, not a failure (AL-56)"
  fi

  while IFS= read -r shape; do
    [ -z "$shape" ] && continue
    if verdict=$(python3 "$SHAPE_CHECK" --polyline "$shape" \
                   --near "${from_lat},${from_lng}" --near "${to_lat},${to_lng}" \
                   --tolerance-m "$tolerance" --bbox "$bbox"); then
      shapes_ok=$((shapes_ok+1))
    else
      shapes_bad=$((shapes_bad+1))
      bad "${label}: shape rejected — $(printf '%s' "$verdict" | jq -r '.failures | join("; ")')"
    fi
  done <<< "$(printf '%s' "$answer" | jq -r '.options[] | select(.kind | ascii_downcase == "direct") | .legs[0].shape // ""')"
done <<< "$(jq -c '.corridors[]' "$CORRIDORS")"

if [ "$with_direct" = "$total" ]; then
  ok "all ${total} sampled corridors return a direct route"
else
  bad "${with_direct} of ${total} corridors return a direct route"
fi

if [ "$shapes_bad" = "0" ] && [ "$shapes_ok" -gt 0 ]; then
  ok "${shapes_ok} shape(s) decode, sit inside the Sri Lanka bounding box, and pass within ${tolerance} m of both ends of their corridor"
elif [ "$shapes_ok" = "0" ]; then
  bad "not one direct leg carried a checkable shape"
fi

if [ "$with_hint" != "$total" ]; then
  warn "${with_hint} of ${total} corridors named one of their hinted route numbers (soft check — see gtfs-corridors.json)"
fi

# =====================================================================================
step "6. the rollback was rehearsed and timed (DoD 3, US-28.3)"
# =====================================================================================
rollback=$(journal_get '.rollback.status')
case "$rollback" in
  rehearsed)
    ok "rehearsed at $(journal_get '.rollback.rehearsedAt'), rolling back to $(journal_get '.rollback.to')"
    note "rollback swap $(journal_get '.rollback.activateSec')s + cache $(journal_get '.rollback.reloadSec')s; restore $(journal_get '.rollback.restoreActivateSec')s + cache $(journal_get '.rollback.restoreReloadSec')s"

    target=$(journal_get '.rollback.to')
    target_status=$(pg_scalar "SELECT status FROM transit.gtfs_feed_versions WHERE feed_version_id = '${target}';")
    if [ "$target_status" = "archived" ]; then
      ok "the rollback target is archived again and can be rolled back to a second time"
    else
      warn "the rollback target is '${target_status}' — the replica was left on the previous release rather than the day-0 feed"
    fi

    reload_secs=$(journal_get '.rollback.reloadSec')
    if [ -n "$reload_secs" ] && python3 -c "import sys; sys.exit(0 if float(sys.argv[1]) <= float(sys.argv[2]) else 1)" "$reload_secs" "$RELOAD_BOUND"; then
      ok "the rolled-back feed reached passengers inside the same ${RELOAD_BOUND}s bound"
    else
      bad "the rollback's cache reload took ${reload_secs:-?}s, over the ${RELOAD_BOUND}s bound"
    fi
    ;;
  not-rehearsed)
    bad "NOT REHEARSED: no previous release was supplied, so nothing could be rolled back to.
      The upload dedupes on sha256, so the day-0 feed cannot be its own rollback target — this needs
      a second, genuinely different feed file. docs/runbooks/gtfs-day0-load.md §6."
    ;;
  skipped)
    bad "the rollback rehearsal was skipped with --skip-rollback. C126 requires it."
    ;;
  *)
    bad "no rollback rehearsal is recorded in the journal"
    ;;
esac

# The zip a rollback re-imports FROM has to still be there. Nothing deletes one (BR-32.3's
# 12-month retention is met by the absence of a delete path), but the storage root is a container
# path, and a path that is not on a volume is emptied by the next `docker compose up --build`.
storage_root=$(docker compose -f "$COMPOSE" exec -T app-services printenv Transit__Gtfs__StorageRoot 2>/dev/null | tr -d '\r')
if [ -n "$storage_root" ]; then
  # `find -type f`, not `ls`: the object store puts its own `gtfs/` prefix under the root, so the
  # zips are one level down and a directory listing counts one entry however many versions exist.
  stored=$(docker compose -f "$COMPOSE" exec -T app-services \
    sh -c "find '${storage_root}' -type f -name '*.zip' 2>/dev/null | wc -l" 2>/dev/null | tr -d ' \r')
  versions_total=$(printf '%s' "$counts" | jq -r '.versions')
  if [ "${stored:-0}" -ge "${versions_total:-1}" ] 2>/dev/null; then
    ok "${stored} stored zip(s) under ${storage_root} for ${versions_total} version(s) — every version can still be rolled back to"
  else
    bad "only ${stored} zip(s) are stored under ${storage_root} for ${versions_total} version(s). A version
      whose zip is gone cannot be rolled back to (BR-32.3) — check that the storage root is a volume."
  fi

  # AT OR UNDER a volume target, not equal to one: the mount is at /var/lib/mageride — the directory
  # Dockerfile.appservices creates as app:app, because a volume on a path the image does not have is
  # created root-owned and a non-root container cannot write it — while the storage root is the
  # `gtfs` subdirectory of it. Comparing for equality would report the durable mount as absent.
  # `.target as $t` and not a bare `.`: inside `$p | startswith(…)` the context is $p, so `. + "/"`
  # would compare the storage root against itself and every mount would look absent.
  if docker compose -f "$COMPOSE" config --format json 2>/dev/null \
     | jq -e --arg p "$storage_root" \
       '[.services."app-services".volumes // [] | .[].target as $t | select($p == $t or ($p | startswith($t + "/")))] | length > 0' \
       >/dev/null 2>&1; then
    ok "${storage_root} is on a mounted volume, so the stored zips survive a container rebuild"
  else
    bad "${storage_root} is on the container's writable layer, not a volume. The next rebuild of
      app-services deletes every stored feed zip, and with it every rollback target (BR-32.3)."
  fi
fi

# =====================================================================================
step "7. the fences (AL-55, AL-56, BR-32.2, D-35)"
# =====================================================================================
# AL-56: there is no route that can write a GTFS row. The superseded raw import must not be mapped —
# it is the one endpoint that would let a feed be assembled through the API instead of handed over.
api POST /v1/admin/transit/gtfs-import -H 'Content-Type: application/json' \
  -H "Idempotency-Key: $(idem_key)" -d '{}'
case "$API_STATUS" in
  404|405) ok "the superseded POST /v1/admin/transit/gtfs-import is not mapped (${API_STATUS}) — SCR-AP-016 is the only ingestion surface" ;;
  401|403) warn "POST …/gtfs-import answered ${API_STATUS}: it is refusing us, but it may still exist" ;;
  *) bad "POST /v1/admin/transit/gtfs-import answered ${API_STATUS} — a second ingestion path is live (AL-56)" ;;
esac

refusal=$(journal_get '.refusalProbe.status')
if [ -n "$refusal" ]; then
  if [ "$(journal_get '.refusalProbe.activeAfter')" = "$(journal_get '.versions.current')" ]; then
    ok "the recorded refusal probe (${refusal} on a nonexistent version) left the live feed untouched (BR-32.2)"
  else
    bad "the refusal probe changed the active feed"
  fi
fi

audits=$(pg_scalar "SELECT count(*) FROM audit.events WHERE entity_type = 'gtfs_feed';")
actions=$(pg_scalar "SELECT string_agg(DISTINCT action, ', ' ORDER BY action) FROM audit.events WHERE entity_type = 'gtfs_feed';")
if [ "${audits:-0}" -ge 3 ] 2>/dev/null; then
  ok "${audits} audit event(s) on this surface: ${actions} (D-35)"
else
  bad "only ${audits} audit event(s) for the GTFS lifecycle; upload, validation and activation each write one"
fi

activated_by=$(pg_scalar "SELECT count(*) FROM audit.events WHERE entity_type = 'gtfs_feed' AND action = 'GTFS_FEED_ACTIVATED' AND actor_id = '$(journal_get '.operator.userId')';")
if [ "${activated_by:-0}" -ge 1 ] 2>/dev/null; then
  ok "every activation is attributed to the operator who made it (${activated_by} by this one)"
else
  bad "no GTFS_FEED_ACTIVATED event names the day-0 operator as its actor"
fi

# =====================================================================================
echo
echo "==============================================================================="
printf '%d passed, %d failed, %d skipped\n' "$pass" "$fail" "$skip"
if [ "$fail" -ne 0 ]; then
  echo
  echo "the runbook for every one of these:  docs/runbooks/gtfs-day0-load.md"
  echo "the run's own record:                ${JOURNAL}"
  exit 1
fi
exit 0
