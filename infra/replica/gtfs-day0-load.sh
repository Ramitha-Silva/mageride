#!/usr/bin/env bash
# =====================================================================================
# infra/replica/gtfs-day0-load.sh — the day-0 GTFS operation, driven against the running replica.
#
#   bash infra/replica/gtfs-day0-load.sh --feed <national-feed.zip> [--previous <prior-release.zip>]
#
# C126. This is the operation; `gtfs-day0-verify.sh` is the check. It drives the SCR-AP-016 calls in
# the order the screen makes them (D1 §Admin → SCR-AP-016), through the edge, as the Admin Portal
# does — upload → poll validation → preview → activate → history — and records the two things only
# the run itself can know: the pre-first-import empty state, and how long each swap took.
#
# ------------------------------------------------------------------------------------
# THE FEED IS NOT IN THIS REPOSITORY, AND MUST NOT BE
# ------------------------------------------------------------------------------------
# AL-56: the GTFS dataset — the day-0 national feed and every later refresh — is an EXTERNALLY
# PROVIDED file. There is no in-house sourcing or authoring workstream, this script will not
# manufacture one, and a feed it built itself would turn every check below into a check of its own
# fixture. Put the provider's zip somewhere readable and name it with --feed.
#
# ------------------------------------------------------------------------------------
# WHY --previous EXISTS
# ------------------------------------------------------------------------------------
# The rollback rehearsal needs a version to roll back TO, and that version must be `archived` —
# which means it must have been active. The upload dedupes on the file's own sha256 (BR-32.1), so
# the same zip cannot become a second version and a feed cannot be rolled back to itself. Given the
# previous national release, the sequence becomes the real one:
#
#   empty → activate previous → activate current (day-0) → roll back to previous → restore current
#
# Without --previous everything except the rehearsal runs, and the rehearsal is reported as NOT
# REHEARSED rather than quietly skipped.
#
# ------------------------------------------------------------------------------------
# WHAT IT WILL NOT DO
# ------------------------------------------------------------------------------------
# It never edits, repairs, filters or generates feed content, and it reads nothing out of the zip
# beyond its first two bytes. Server-side validation (BR-32.1) is the only quality gate MageRide
# enforces (AL-56); a feed that fails it is fixed at the provider and re-uploaded.
# =====================================================================================
set -uo pipefail

# shellcheck source=infra/replica/gtfs-lib.sh
. "$(dirname -- "${BASH_SOURCE[0]}")/gtfs-lib.sh"

FEED=""
PREVIOUS=""
SKIP_ROLLBACK=0
OBSERVE_ONLY=0
VALIDATION_TIMEOUT="${GTFS_VALIDATION_TIMEOUT:-1800}"
RELOAD_BOUND="${GTFS_RELOAD_BOUND:-60}"

while [ $# -gt 0 ]; do
  case "$1" in
    --feed)          FEED="${2:-}"; shift 2 ;;
    --previous)      PREVIOUS="${2:-}"; shift 2 ;;
    --skip-rollback) SKIP_ROLLBACK=1; shift ;;
    # Steps 0–2 only: provision the operator, prove the surface answers, and record the
    # pre-first-import empty state. It exists because that state is destroyed by the first
    # activation and the feed may arrive days later — the record has to be made while it is true,
    # not when it is convenient.
    --observe-empty-state) OBSERVE_ONLY=1; shift ;;
    -h|--help)       sed -n '2,38p' "${BASH_SOURCE[0]}"; exit 0 ;;
    *)               die "unknown argument: $1" ;;
  esac
done

FEED="${FEED:-${GTFS_FEED_ZIP:-}}"
PREVIOUS="${PREVIOUS:-${GTFS_PREVIOUS_ZIP:-}}"

# The conventional drop point, so the runbook can name a path rather than a variable.
if [ -z "$FEED" ] && [ -d "$REPLICA_DIR/gtfs" ]; then
  # `<name>-previous.zip` is the rollback target by convention, so a two-file drop needs no flags.
  for drop in "$REPLICA_DIR"/gtfs/*.zip; do
    [ -f "$drop" ] || continue
    case "$drop" in
      *-previous.zip) [ -n "$PREVIOUS" ] || PREVIOUS="$drop" ;;
      *)              [ -n "$FEED" ]     || FEED="$drop" ;;
    esac
  done
fi

# =====================================================================================
step "0. preflight"
# =====================================================================================
if [ -z "$FEED" ] && [ "$OBSERVE_ONLY" = "1" ]; then
  ok "--observe-empty-state: no feed needed, and none will be uploaded"
elif [ -z "$FEED" ]; then
  cat >&2 <<'MISSING'
  There is no GTFS feed to load, and this script will not create one.

  The feed is an externally provided file (AL-56) — the day-0 national release and every refresh
  alike. Obtain it from the provider, then either:

      mkdir -p infra/replica/gtfs
      cp <provider-file>.zip           infra/replica/gtfs/
      cp <prior-release>.zip           infra/replica/gtfs/national-previous.zip   # for the rollback rehearsal
      bash infra/replica/gtfs-day0-load.sh

  or name the files directly:

      bash infra/replica/gtfs-day0-load.sh --feed /path/to/feed.zip [--previous /path/to/prior.zip]

  docs/runbooks/gtfs-day0-load.md §1 covers obtaining it and what to check before uploading.

  If the file has not arrived yet, record the day-0 baseline now — it is destroyed by the first
  activation and cannot be reconstructed afterwards:

      bash infra/replica/gtfs-day0-load.sh --observe-empty-state
MISSING
  exit 2
fi

if [ -n "$FEED" ]; then
  [ -f "$FEED" ] || die "no such file: $FEED"
  [ -z "$PREVIOUS" ] || [ -f "$PREVIOUS" ] || die "no such file: $PREVIOUS"

  # A zip, checked by its magic bytes rather than its extension. Deliberately the ONLY thing read
  # out of the file here — see "what it will not do" above.
  candidates=("$FEED")
  [ -n "$PREVIOUS" ] && candidates+=("$PREVIOUS")
  for candidate in "${candidates[@]}"; do
    if [ "$(head -c 2 "$candidate")" = "PK" ]; then
      ok "$(basename "$candidate") is a zip ($(du -h "$candidate" | cut -f1), sha256 $(sha256sum "$candidate" | cut -c1-12)…)"
    else
      die "$candidate does not start with a zip signature"
    fi
  done

  # Two names for one file is the easy mistake, and it fails twenty minutes later in a way that
  # reads like a rollback bug: the upload dedupes on content (BR-32.1), so the second file becomes
  # the same version, the "rollback" target is the live feed, and the swap it is asked for is one
  # transit-svc correctly refuses as `feed-already-active`.
  if [ -n "$PREVIOUS" ] &&
     [ "$(sha256sum "$FEED" | cut -d' ' -f1)" = "$(sha256sum "$PREVIOUS" | cut -d' ' -f1)" ]; then
    die "--feed and --previous are the same file (identical sha256). The rollback rehearsal needs the
      PREVIOUS national release: a feed cannot be rolled back to itself."
  fi
fi

running=$(docker compose -f "$COMPOSE" ps --services --filter status=running 2>/dev/null | wc -l)
[ "$running" -gt 0 ] || die "nothing is running under mageride-replica — bring it up with deploy.sh"
ok "${running} replica services running"

# transit-svc must be *reachable*, not merely deployed. Unauthenticated on purpose: a 401 proves the
# gateway routed to transit-svc and its own deny-by-default refused us (AL-06); a 502 proves the
# route table points at nothing, which is the C125 class of failure.
api GET /v1/admin/transit/gtfs/versions
case "$API_STATUS" in
  401|403) ok "the edge routes /v1/admin/transit/** into transit-svc (${API_STATUS} unauthenticated)" ;;
  502|503|504) die "the edge answered ${API_STATUS} for the GTFS admin surface — no transit-svc behind the gateway" ;;
  404) die "404 for /v1/admin/transit/gtfs/versions — the gateway has no route for it (gateway-routes.json)" ;;
  *) warn "unauthenticated GET …/versions answered ${API_STATUS}, which is not the 401 AL-06 implies" ;;
esac

journal_init
if [ -n "$FEED" ]; then
  journal_set_str '.feed.path' "$(basename "$FEED")"
  journal_set_str '.feed.sha256' "$(sha256sum "$FEED" | cut -d' ' -f1)"
  journal_set '.feed.bytes' "$(stat -c %s "$FEED")"
fi
if [ -n "$PREVIOUS" ]; then
  journal_set_str '.previousFeed.path' "$(basename "$PREVIOUS")"
  journal_set_str '.previousFeed.sha256' "$(sha256sum "$PREVIOUS" | cut -d' ' -f1)"
fi

# =====================================================================================
step "1. the operator (AL-06 — Admin or Super Admin only)"
# =====================================================================================
require_token
ok "signed in as ${GTFS_ADMIN_EMAIL} (${GTFS_ADMIN_ID})"
journal_set_str '.operator.email' "$GTFS_ADMIN_EMAIL"
journal_set_str '.operator.userId' "$GTFS_ADMIN_ID"

api GET "/v1/admin/transit/gtfs/versions?limit=50"
versions_before="$API_BODY"
[ "$API_STATUS" = "200" ] || die "GET …/versions returned ${API_STATUS} for an admin: $(printf '%s' "$versions_before" | head -c 300)"
ok "the version history reads: $(printf '%s' "$versions_before" | jq -r '.items | length') row(s)"

# =====================================================================================
step "2. the pre-first-import empty state (AL-55, D2 SCR-AP-016 'Empty state')"
# =====================================================================================
# Observed and recorded BEFORE anything is activated, because it stops existing at that moment and
# is a definition-of-done item in its own right. AL-55 is what makes it worth recording: after day-0
# an empty answer from /v1/transit/options means "no bus serves this corridor", and only `coverage`
# distinguishes that from "there is no feed" — so this is the one chance to see the second value on
# a real deployment.
counts_now=$(live_counts_json)
versions_count=$(printf '%s' "$counts_now" | jq -r '.versions')
first_corridor=$(jq -c '.corridors[0]' "$CORRIDORS")

if [ -n "$(journal_get '.emptyState.observedAt')" ]; then
  ok "already recorded on $(journal_get '.emptyState.observedAt') — kept, not overwritten"
elif [ "$versions_count" != "0" ]; then
  bad "a feed version already exists (${versions_count} row(s)) and no empty state was ever recorded.
      The pre-first-import state cannot be reconstructed. To record it, start from a clean database:
      infra/replica/down.sh --volumes  &&  deploy.sh  &&  this script."
else
  options_for \
    "$(printf '%s' "$first_corridor" | jq -r '.from.lat')" "$(printf '%s' "$first_corridor" | jq -r '.from.lng')" \
    "$(printf '%s' "$first_corridor" | jq -r '.to.lat')"   "$(printf '%s' "$first_corridor" | jq -r '.to.lng')"
  probe="$API_BODY"

  coverage=$(printf '%s' "$probe" | jq -r '.coverage // "unreadable"')
  options=$(printf '%s' "$probe" | jq -r '.options | length' 2>/dev/null) || options=0
  case "$options" in ''|*[!0-9]*) options=0 ;; esac

  journal_set_str '.emptyState.observedAt' "$(now_iso)"
  journal_set '.emptyState.liveCounts' "$counts_now"
  journal_set_str '.emptyState.coverage' "$coverage"
  journal_set_str '.emptyState.probeCorridor' "$(printf '%s' "$first_corridor" | jq -r '.id')"
  journal_set '.emptyState.probeOptions' "$options"
  journal_set '.emptyState.historyRows' "$(printf '%s' "$versions_before" | jq '.items | length')"

  if [ "$coverage" = "no_feed" ]; then
    ok "coverage=no_feed with ${options} options — AL-55's safety net, seen once, on purpose"
  else
    bad "the empty replica answered coverage=${coverage}; expected no_feed"
  fi
  ok "live tables empty: $(printf '%s' "$counts_now" | jq -r '"routes=\(.routes) stops=\(.stops) trips=\(.trips) stop_times=\(.stop_times) shapes=\(.shapes)"')"
  ok "the history table is empty — SCR-AP-016's day-0 empty state"
fi

if [ "$OBSERVE_ONLY" = "1" ]; then
  echo
  echo "==============================================================================="
  printf '%d passed, %d failed, %d skipped\n' "$pass" "$fail" "$skip"
  echo "the baseline is recorded in ${JOURNAL} and will not be overwritten."
  echo "when the provider's file arrives:"
  echo "    bash infra/replica/gtfs-day0-load.sh --feed <feed.zip> --previous <prior.zip>"
  [ "$fail" -eq 0 ] || exit 1
  exit 0
fi

# =====================================================================================
# The pipeline, as functions, because the rehearsal walks it more than once.
#
# Each returns through a named global rather than stdout: the transcript ok/bad/note lines go to
# stdout, so a function whose *value* was also on stdout would hand its caller the transcript.
# =====================================================================================
UPLOAD_VERSION=""
VALIDATION_BODY=""
ACTIVATE_SECONDS=""
CACHE_SECONDS=""

# `upload <zip>` → UPLOAD_VERSION. POST …/uploads (US-28.1).
# A 409 `feed-duplicate` is a success for this script's purpose: BR-32.1's sha256 refusal names the
# version the first attempt created, which is the version a re-run wants to carry on with.
upload() {
  local zip="$1" body code version

  api POST /v1/admin/transit/gtfs/uploads \
    -H "Idempotency-Key: $(idem_key)" \
    -F "file=@${zip};type=application/zip"
  body="$API_BODY"
  code="$API_STATUS"

  case "$code" in
    202)
      version=$(printf '%s' "$body" | jq -r '.feedVersionId')
      note "uploaded as ${version} (202 — validation queued, not run)"
      ;;
    409)
      if [ "$(problem_code "$body")" = "feed-duplicate" ]; then
        version=$(printf '%s' "$body" | jq -r '.feedVersionId')
        note "this exact file is already version ${version} (sha256 dedupe, BR-32.1) — reused"
      else
        die "upload refused: $(printf '%s' "$body" | head -c 300)"
      fi
      ;;
    413) die "the upload was refused as too large; the ceiling is Transit__Gtfs__MaxUploadBytes (BR-32.1: 200 MB)" ;;
    *)   die "upload returned ${code}: $(printf '%s' "$body" | head -c 400)" ;;
  esac

  [ -n "$version" ] && [ "$version" != "null" ] || die "no feedVersionId came back from the upload"
  UPLOAD_VERSION="$version"
}

# `await_validation <versionId>` → VALIDATION_BODY. The status stepper (Uploaded → Validating →
# Validated / Failed), polled at the 2 s cadence SCR-AP-016 itself polls at.
await_validation() {
  local version="$1" started body status waited=0

  started=$(now_epoch)

  while :; do
    api GET "/v1/admin/transit/gtfs/uploads/${version}"
    body="$API_BODY"
    [ "$API_STATUS" = "200" ] || die "GET …/uploads/${version} returned ${API_STATUS}"

    status=$(printf '%s' "$body" | jq -r '.status')

    case "$status" in
      validated|active|archived|failed) break ;;
      uploaded|validating) : ;;
      *) die "unknown feed status '${status}'" ;;
    esac

    waited=$(printf '%.0f' "$(elapsed_since "$started")")
    if [ "$waited" -ge "$VALIDATION_TIMEOUT" ]; then
      die "validation was still '${status}' after ${waited}s. Transit__Gtfs__ValidationEnabled must be
      true and the validation worker must be running; raise GTFS_VALIDATION_TIMEOUT for a very large
      feed, and read the app-services log before you do."
    fi

    # Only on a terminal: a \r progress line in a captured log is 64 spaces and a mystery.
    [ -t 1 ] && printf '\r    validating… %ss (status %s)   ' "$waited" "$status"
    sleep 2
  done

  [ -t 1 ] && printf '\r%*s\r' 64 ''
  journal_set ".validation.\"${version}\".elapsedSec" "$(elapsed_since "$started")"
  journal_set ".validation.\"${version}\".body" "$body"
  VALIDATION_BODY="$body"
}

# `preview <status-body>` — the preview card: per-file counts, feed_info version, service window,
# warnings (US-28.1/28.2). Printing it IS the operator's review step, and it is the last thing that
# happens before a swap every passenger sees. Non-zero when the feed failed validation.
preview() {
  local body="$1" warnings errors

  note "feed_info version : $(printf '%s' "$body" | jq -r '.feedInfoVersion // "(none in the file)"')"
  note "service window    : $(printf '%s' "$body" | jq -r '(.serviceStart // "?") + " → " + (.serviceEnd // "?")')"
  note "counts            : $(printf '%s' "$body" | jq -r '.counts | to_entries | map("\(.key)=\(.value)") | join("  ")')"

  warnings=$(printf '%s' "$body" | jq -r '.warnings | length')
  errors=$(printf '%s' "$body" | jq -r '.errorSummary | length')

  if [ "$warnings" != "0" ]; then
    warn "${warnings} warning(s) — these never block activation (BR-32.1):"
    printf '%s' "$body" | jq -r '.warnings[] | "      · " + .'
  fi

  if [ "$(printf '%s' "$body" | jq -r '.status')" = "failed" ]; then
    bad "validation FAILED. First ${errors} error(s):"
    printf '%s' "$body" | jq -r '.errorSummary[] | "      · " + .'
    note "the full row-level report: GET /v1/admin/transit/gtfs/uploads/<id>/report?format=csv"
    note "AL-56: the feed is fixed at the provider and re-uploaded. Nothing here edits a feed."
    return 1
  fi

  return 0
}

# `activate <versionId> <label>` → ACTIVATE_SECONDS. The atomic swap (US-28.2), and a rollback,
# which is the same call (BR-32.3).
activate() {
  local version="$1" label="$2" started body code outgoing problem still

  outgoing=$(active_version_id)
  started=$(now_epoch)

  api POST "/v1/admin/transit/gtfs/uploads/${version}/activate" \
    -H "Idempotency-Key: $(idem_key)" -H 'Content-Length: 0'
  body="$API_BODY"
  code="$API_STATUS"
  ACTIVATE_SECONDS=$(elapsed_since "$started")

  if [ "$code" != "200" ]; then
    problem=$(problem_code "$body")

    if [ "$problem" = "feed-already-active" ]; then
      note "${version} is already the active feed — nothing swapped"
      journal_set_str ".activation.\"${version}\".note" "already active"
      ACTIVATE_SECONDS=0
      return 0
    fi

    # The fence, asserted at the only moment it can be: a failed activation must leave the previous
    # feed live and untouched.
    still=$(active_version_id)
    if [ "$still" = "$outgoing" ]; then
      bad "activating ${label} failed (${code} ${problem}) — and the previous feed '${outgoing}' is still live, which is the required behaviour (BR-32.2)"
    else
      bad "activating ${label} failed (${code} ${problem}) AND the active feed changed from '${outgoing}' to '${still}'. That breaks BR-32.2."
    fi

    die "activation refused: $(printf '%s' "$body" | head -c 400)" 1
  fi

  journal_set ".activation.\"${version}\".elapsedSec" "$ACTIVATE_SECONDS"
  journal_set_str ".activation.\"${version}\".committedAt" "$(now_iso)"
  journal_set_str ".activation.\"${version}\".label" "$label"
  journal_set_str ".activation.\"${version}\".outgoing" "${outgoing:-}"

  ok "activated ${label} in ${ACTIVATE_SECONDS}s (status $(printf '%s' "$body" | jq -r '.status'))"
}

# `await_cache <versionId> <corridor-json> <since-epoch>` → CACHE_SECONDS. The ≤ 60 s bound
# (US-28.2, D6' I-32.1), measured from the moment the swap committed — which is when the NOTIFY is
# delivered, because it is issued inside the swap transaction.
#
# Two signals, because neither alone is sufficient. The log line names the version the CACHE loaded,
# which the wire does not carry: `feedVersion` is `feed_info.version` and two releases of one
# national feed may share it. `coverage` on the wire is what a passenger actually experiences, and
# is the only signal that the FIRST activation reached them.
await_cache() {
  local version="$1" corridor="$2" started="$3" waited coverage seen probe waited_int

  while :; do
    waited_int=$(printf '%.0f' "$(elapsed_since "$started")")
    seen=$(cache_loaded_since "$((waited_int + 60))s" | grep -F "$version" | tail -1)

    options_for \
      "$(printf '%s' "$corridor" | jq -r '.from.lat')" "$(printf '%s' "$corridor" | jq -r '.from.lng')" \
      "$(printf '%s' "$corridor" | jq -r '.to.lat')"   "$(printf '%s' "$corridor" | jq -r '.to.lng')"
    probe="$API_BODY"
    coverage=$(printf '%s' "$probe" | jq -r '.coverage // "unreadable"')

    if [ -n "$seen" ] && [ "$coverage" = "active" ]; then
      [ -t 1 ] && printf '\r%*s\r' 64 ''
      CACHE_SECONDS=$(elapsed_since "$started")
      journal_set ".cacheReload.\"${version}\".elapsedSec" "$CACHE_SECONDS"
      journal_set_str ".cacheReload.\"${version}\".evidence" "$(printf '%s' "$seen" | tr -s ' ' | tail -c 200)"
      journal_set '.cacheReload.boundSec' "$RELOAD_BOUND"
      return 0
    fi

    if [ "$waited_int" -ge "$RELOAD_BOUND" ]; then
      [ -t 1 ] && printf '\r%*s\r' 64 ''
      CACHE_SECONDS="$waited_int"
      journal_set ".cacheReload.\"${version}\".elapsedSec" "$CACHE_SECONDS"
      journal_set_str ".cacheReload.\"${version}\".evidence" "not observed within the bound"
      bad "the cache had not published ${version} after ${waited_int}s; US-28.2's bound is ${RELOAD_BOUND}s.
      LISTEN transit_feed_activated fires the reload and Transit__FeedPollInterval (30 s) is the
      safety net — check both, and the app-services log for 'Reloading the GTFS feed failed'."
      return 1
    fi

    [ -t 1 ] && printf '\r    waiting for the cache… %ss (coverage %s)   ' "$waited_int" "$coverage"
    sleep 3
  done
}

# `corridors <versionId>` — the sample set (C126's definition of done), recorded answer by answer.
corridors() {
  local version="$1" total=0 with_direct=0 with_hint=0 shape_ok=0 shape_bad=0
  local tolerance bbox results="[]" corridor

  tolerance=$(jq -r '.shapeToleranceM' "$CORRIDORS")
  bbox=$(jq -r '"\(._bbox.minLat),\(._bbox.maxLat),\(._bbox.minLng),\(._bbox.maxLng)"' "$CORRIDORS")

  while IFS= read -r corridor; do
    total=$((total+1))

    local id label from_lat from_lng to_lat to_lng probe coverage direct names hinted got
    local leg_shapes shape verdict leg_verdicts="[]"

    id=$(printf '%s' "$corridor" | jq -r '.id')
    label=$(printf '%s' "$corridor" | jq -r '.label')
    from_lat=$(printf '%s' "$corridor" | jq -r '.from.lat'); from_lng=$(printf '%s' "$corridor" | jq -r '.from.lng')
    to_lat=$(printf '%s' "$corridor" | jq -r '.to.lat');     to_lng=$(printf '%s' "$corridor" | jq -r '.to.lng')

    options_for "$from_lat" "$from_lng" "$to_lat" "$to_lng"
    probe="$API_BODY"
    if [ "$API_STATUS" != "200" ]; then
      bad "${label}: /v1/transit/options answered ${API_STATUS}"
      continue
    fi

    coverage=$(printf '%s' "$probe" | jq -r '.coverage')
    direct=$(printf '%s' "$probe" | jq '[.options[] | select(.kind | ascii_downcase == "direct")] | length')
    names=$(printf '%s' "$probe" | jq -r '[.options[] | select(.kind | ascii_downcase == "direct") | .legs[0].routeShortName] | unique | join(", ")')

    if [ "$coverage" != "active" ]; then
      bad "${label}: coverage=${coverage} after activation"
    elif [ "$direct" -lt 1 ]; then
      bad "${label}: no DIRECT option. Either the feed does not serve this corridor, or its halts are
      further from the sample points than the deployed halt radius (Transit__HaltRadiusM, 400 m)."
    else
      with_direct=$((with_direct+1))
      ok "${label}: ${direct} direct route(s) — ${names}"
    fi

    # The hinted route numbers: reported, never required. See _softChecks in gtfs-corridors.json.
    hinted=$(printf '%s' "$corridor" | jq -r '.expectRoutes | join(", ")')
    got=$(printf '%s' "$probe" | jq -c '[.options[] | .legs[] | .routeShortName]')
    if printf '%s' "$corridor" | jq -e --argjson got "$got" \
         '[.expectRoutes[] | select(. as $e | $got | index($e) != null)] | length > 0' >/dev/null 2>&1; then
      with_hint=$((with_hint+1))
    else
      warn "${label}: none of the hinted routes (${hinted}) appear. Route numbering is the feed's to
      state, not ours to require (AL-56) — but ask the provider about this one."
    fi

    # Every direct leg's shape, judged against the corridor that produced it.
    leg_shapes=$(printf '%s' "$probe" | jq -r '.options[] | select(.kind | ascii_downcase == "direct") | .legs[0].shape // ""')
    while IFS= read -r shape; do
      [ -z "$shape" ] && continue
      if verdict=$(python3 "$SHAPE_CHECK" --polyline "$shape" \
                     --near "${from_lat},${from_lng}" --near "${to_lat},${to_lng}" \
                     --tolerance-m "$tolerance" --bbox "$bbox"); then
        shape_ok=$((shape_ok+1))
      else
        shape_bad=$((shape_bad+1))
        bad "${label}: a direct leg's shape is wrong — $(printf '%s' "$verdict" | jq -r '.failures | join("; ")')"
      fi
      leg_verdicts=$(printf '%s' "$leg_verdicts" | jq -c --argjson v "$verdict" '. + [$v]')
    done <<< "$leg_shapes"

    if [ "$direct" -gt 0 ] && [ -z "$(printf '%s' "$leg_shapes" | tr -d ' \n')" ]; then
      if [ "$(pg_scalar 'SELECT count(*) FROM transit.gtfs_shapes;')" = "0" ]; then
        warn "${label}: no leg carries a shape, and the feed has no shapes.txt rows at all — SCR-PA-009
      will draw no route line. That is the feed's shape to provide, not ours (AL-56)."
      else
        bad "${label}: no direct leg carries a shape, although the feed does have shape rows"
      fi
    fi

    results=$(printf '%s' "$results" | jq -c \
      --arg id "$id" --arg label "$label" --arg coverage "$coverage" --arg names "$names" \
      --argjson direct "$direct" --argjson shapes "$leg_verdicts" \
      '. + [{id: $id, label: $label, coverage: $coverage, directOptions: $direct, routeShortNames: $names, shapes: $shapes}]')
  done <<< "$(jq -c '.corridors[]' "$CORRIDORS")"

  journal_set ".corridors.\"${version}\"" \
    "$(jq -nc --arg at "$(now_iso)" --argjson r "$results" --argjson t "$total" --argjson d "$with_direct" \
       --argjson h "$with_hint" --argjson so "$shape_ok" --argjson sb "$shape_bad" \
       '{checkedAt: $at, total: $t, withDirect: $d, withHintedRoute: $h, shapesOk: $so, shapesBad: $sb, results: $r}')"

  note "corridor sample: ${with_direct}/${total} returned direct routes, ${with_hint}/${total} named a hinted route, ${shape_ok} shape(s) correct, ${shape_bad} wrong"
}

# =====================================================================================
# The sequence.
# =====================================================================================
previous_version=""

if [ -n "$PREVIOUS" ]; then
  step "3. the previous release — loaded first, so the rehearsal has somewhere to roll back to"
  upload "$PREVIOUS"; previous_version="$UPLOAD_VERSION"
  await_validation "$previous_version"
  preview "$VALIDATION_BODY" || die "the previous release does not validate, so it cannot be a rollback target" 1
  ok "the previous release ${previous_version} validated"
  prev_from=$(now_epoch)
  activate "$previous_version" "the previous release"
  await_cache "$previous_version" "$first_corridor" "$prev_from" || true
  journal_set_str '.versions.previous' "$previous_version"
else
  step "3. the previous release — not supplied"
  skip_ "no --previous: the rollback rehearsal has no archived version to roll back to, and is
      reported as NOT REHEARSED below"
fi

# -------------------------------------------------------------------------------------
step "4. the day-0 feed — upload and validate (US-28.1)"
# -------------------------------------------------------------------------------------
upload "$FEED"; version="$UPLOAD_VERSION"
journal_set_str '.versions.current' "$version"
await_validation "$version"
status_body="$VALIDATION_BODY"
ok "validation finished in $(journal_get ".validation.\"${version}\".elapsedSec")s with status $(printf '%s' "$status_body" | jq -r '.status')"

# -------------------------------------------------------------------------------------
step "5. preview — what the operator reviews before the swap (US-28.2)"
# -------------------------------------------------------------------------------------
if ! preview "$status_body"; then
  printf '\n%d passed, %d failed, %d skipped — the feed did not validate, so nothing was activated.\n' \
    "$pass" "$fail" "$skip"
  exit 1
fi
ok "the feed is validated and activatable"

# -------------------------------------------------------------------------------------
step "6. activate — the atomic swap and the ≤ ${RELOAD_BOUND} s cache reload (US-28.2)"
# -------------------------------------------------------------------------------------
reload_from=$(now_epoch)
activate "$version" "the day-0 national feed"
if await_cache "$version" "$first_corridor" "$reload_from"; then
  ok "the cache published ${version} ${CACHE_SECONDS}s after the swap committed (bound ${RELOAD_BOUND}s)"
fi

active_now=$(active_version_id)
if [ "$active_now" = "$version" ]; then
  ok "transit.gtfs_feed_versions: ${version} is the one active row"
else
  bad "the ledger's active row is '${active_now}', not ${version}"
fi

after=$(live_counts_json)
journal_set '.postActivation.liveCounts' "$after"
ok "live tables: $(printf '%s' "$after" | jq -r '"routes=\(.routes) stops=\(.stops) trips=\(.trips) stop_times=\(.stop_times) shapes=\(.shapes)"')"

staging=$(pg_scalar "SELECT count(*) FROM transit_staging.gtfs_routes;")
note "transit_staging.gtfs_routes holds ${staging} row(s) — the outgoing dataset, left where the three-way rename put it"

# -------------------------------------------------------------------------------------
step "7. post-activation verification — the corridor sample set"
# -------------------------------------------------------------------------------------
corridors "$version"

# -------------------------------------------------------------------------------------
step "8. a refused activation leaves the live feed alone (BR-32.2)"
# -------------------------------------------------------------------------------------
# A version id that cannot exist, so the refusal is genuine and no real feed is put at risk. This is
# the one negative case that can be exercised against a live replica without damaging a dataset.
ghost="00000000-0000-0000-0000-0000000000ff"
api POST "/v1/admin/transit/gtfs/uploads/${ghost}/activate" \
  -H "Idempotency-Key: $(idem_key)" -H 'Content-Length: 0' >/dev/null
refused="$API_STATUS"
still=$(active_version_id)
if [ "$refused" != "200" ] && [ "$still" = "$version" ]; then
  ok "activating a nonexistent version answered ${refused} and left ${version} live"
  journal_set_str '.refusalProbe.status' "$refused"
  journal_set_str '.refusalProbe.activeAfter' "$still"
else
  bad "the refusal probe answered ${refused} and the active feed is now '${still}'"
fi

# -------------------------------------------------------------------------------------
step "9. rollback rehearsal (US-28.3, BR-32.3)"
# -------------------------------------------------------------------------------------
if [ -z "$previous_version" ]; then
  skip_ "NOT REHEARSED: no previous release was supplied. C126's definition of done is not met until
      this runs — docs/runbooks/gtfs-day0-load.md §6 is what to run when the file arrives."
  journal_set_str '.rollback.status' "not-rehearsed"
elif [ "$SKIP_ROLLBACK" = "1" ]; then
  skip_ "NOT REHEARSED: --skip-rollback"
  journal_set_str '.rollback.status' "skipped"
else
  note "rolling back to ${previous_version} — the same call the history row's Re-activate makes"
  rb_from=$(now_epoch)
  activate "$previous_version" "the previous release (rollback)"
  rb_activate="$ACTIVATE_SECONDS"
  await_cache "$previous_version" "$first_corridor" "$rb_from" || true
  rb_reload="$CACHE_SECONDS"

  rolled=$(active_version_id)
  archived=$(pg_scalar "SELECT status FROM transit.gtfs_feed_versions WHERE feed_version_id = '${version}';")

  if [ "$rolled" = "$previous_version" ] && [ "$archived" = "archived" ]; then
    ok "the swap reversed: ${previous_version} is active and the day-0 feed is ${archived}"
  else
    bad "after the rollback the active row is '${rolled}' and the day-0 feed is '${archived}'"
  fi

  # And back, so the replica is left in its day-0 state rather than on an old feed.
  note "restoring the day-0 feed"
  re_from=$(now_epoch)
  activate "$version" "the day-0 national feed (restore)"
  re_activate="$ACTIVATE_SECONDS"
  await_cache "$version" "$first_corridor" "$re_from" || true
  re_reload="$CACHE_SECONDS"

  restored=$(active_version_id)
  if [ "$restored" = "$version" ]; then
    ok "the day-0 feed is live again"
  else
    bad "the restore left '${restored}' active, not ${version}"
  fi

  journal_set_str '.rollback.status' "rehearsed"
  journal_set_str '.rollback.to' "$previous_version"
  journal_set '.rollback.activateSec' "$rb_activate"
  journal_set '.rollback.reloadSec' "$rb_reload"
  journal_set '.rollback.restoreActivateSec' "$re_activate"
  journal_set '.rollback.restoreReloadSec' "$re_reload"
  journal_set_str '.rollback.rehearsedAt' "$(now_iso)"
fi

# -------------------------------------------------------------------------------------
step "10. the history table, as SCR-AP-016 shows it (US-28.3)"
# -------------------------------------------------------------------------------------
api GET "/v1/admin/transit/gtfs/versions?limit=50"
history="$API_BODY"
printf '%s' "$history" | jq -r '.items[] | [(.status | ascii_upcase), .feedVersionId, .fileName, (.feedInfoVersion // "-")] | @tsv' \
  | while IFS=$'\t' read -r s i f v; do printf '    %-9s %s  %s  (%s)\n' "$s" "$i" "$f" "$v"; done
journal_set '.history' "$(printf '%s' "$history" | jq -c '.items')"

active_rows=$(pg_scalar "SELECT count(*) FROM transit.gtfs_feed_versions WHERE status = 'active';")
if [ "$active_rows" = "1" ]; then
  ok "exactly one active version, which is what ux_gtfs_feed_one_active enforces"
else
  bad "${active_rows} row(s) are 'active' — the partial unique index should have made that impossible"
fi

# `gtfs_feed` is GtfsAuditActions.FeedEntity — the entity_type every GTFS lifecycle fact carries.
audits=$(pg_scalar "SELECT count(*) FROM audit.events WHERE entity_type = 'gtfs_feed';")
ok "${audits} audit event(s) recorded for this surface (D-35): $(pg_scalar "SELECT string_agg(DISTINCT action, ', ') FROM audit.events WHERE entity_type = 'gtfs_feed';")"
journal_set '.auditEvents' "${audits:-0}"

journal_set_str '.completedAt' "$(now_iso)"

# =====================================================================================
echo
echo "==============================================================================="
printf '%d passed, %d failed, %d skipped\n' "$pass" "$fail" "$skip"
echo "journal: ${JOURNAL}"
echo "now check it:  bash infra/replica/gtfs-day0-verify.sh"
[ "$fail" -eq 0 ] || exit 1
exit 0
