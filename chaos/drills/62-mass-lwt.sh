#!/usr/bin/env bash
# =====================================================================================
# 62 — mass-lwt.  A fleet of drivers loses coverage at once (R-15, R-16, DT-04).
#
# R-15: "EMQX LWT not wired to dispatch → `veh/{vehicleId}/status=offline` event → dispatch-svc
# releases active offer / starts grace timer per ride state."
# R-16: "Per-state grace windows (offline-after-accept 60 s, after-arrive 120 s, in-progress
# 5 min, at-payment 10 min) in config."
#
# The drill has two halves, and the report keeps them apart because they fail independently:
#
#   BROKER  — does EMQX publish the wills at all? The generator's own watcher counts them.
#   PLATFORM— does dispatch-svc act on them? `dispatch.timers` of kind `offer_release_grace`
#             is the whole observable, and its absence has two very different causes.
#
# One of the aborted sessions is the FIXTURE driver, holding a live offer, so the platform half
# is asked about a vehicle the dispatcher has something to release.
# =====================================================================================

drill_begin "62" "Mass driver offline (EMQX last will)" \
  "R-15 (ADD §6, §11.12) · R-16 grace windows · T-04 retained LWT · DT-04" \
  "a fleet of MQTT sessions dropped without DISCONNECT, plus the fixture driver's live offer" \
  "the sessions are gone by construction; the fixture ride is cancelled and the driver taken off standby"

command -v k6 >/dev/null 2>&1 || {
  bad "drill 62 could not run: k6 is not on PATH"
  drill_end 30
  return 0 2>/dev/null || true
}

FLEET="${CHAOS_LWT_FLEET:-150}"

# -------------------------------------------------------------------------------------
# Is the consumer even switched on? Asked first, because it decides how to read everything below.
# -------------------------------------------------------------------------------------
last_will_enabled=$(dc exec -T app-services printenv Dispatch__LastWillEnabled 2>/dev/null | tr -d '\r')
[ -n "$last_will_enabled" ] || last_will_enabled="unset (defaults to false)"
note "Dispatch__LastWillEnabled = ${last_will_enabled}"

# -------------------------------------------------------------------------------------
# A live offer to release
# -------------------------------------------------------------------------------------
if make_live_offer 0; then
  arm_rollback "release_fixture 0"
  ok "fixture driver ${FIXTURE_DRIVER:0:8}… holds a live offer on ride ${FIXTURE_RIDE:0:8}… (vehicle ${FIXTURE_VEHICLE:0:8}…)"
  real_vehicle="$FIXTURE_VEHICLE"
else
  warn "no live offer could be made; the R-15 half of this drill will be about an idle driver"
  real_vehicle=""
fi

grace_before=$(psql_one "SELECT count(*) FROM dispatch.timers WHERE kind = 'offer_release_grace';")

mkdir -p "${CHAOS_DIR}/out"
summary="${CHAOS_DIR}/out/lwt.json"

lwt_started=$(now_ms)
k6 run --quiet "${CHAOS_DIR}/k6/lwt.js" \
  -e CHAOS_FLEET="$FLEET" -e CHAOS_HOLD=8 -e CHAOS_WATCH=25 \
  -e CHAOS_REAL_VEHICLE="$real_vehicle" \
  --summary-export="$summary" >/dev/null 2>&1
lwt_ms=$(since_ms "$lwt_started")

read_metric() { jq -r "${1} // 0" "$summary" 2>/dev/null | head -1; }

connected=$(read_metric '.metrics.lwt_connected.count')
aborted=$(read_metric '.metrics.lwt_aborted.count')
observed=$(read_metric '.metrics.lwt_wills_observed.count')
real_observed=$(read_metric '.metrics.lwt_real_will_observed.count')
will_med=$(read_metric '.metrics.lwt_will_latency_ms.med')
will_max=$(read_metric '.metrics.lwt_will_latency_ms.max')

note "${connected} sessions connected with a will, ${aborted} dropped without DISCONNECT, \
${observed} wills seen on veh/+/status (med ${will_med} ms after the abort, max ${will_max} ms)"

degraded_table_open
degraded_row "EMQX (broker half)" \
  "${observed}/${aborted} \`offline\` wills published, med ${will_med} ms after the socket died" \
  "\"EMQX LWT … \`veh/{vehicleId}/status=offline\`\" (R-15, T-04)"

# -------------------------------------------------------------------------------------
# Broker half
# -------------------------------------------------------------------------------------
if [ "${aborted:-0}" -gt 0 ] && [ "${observed:-0}" -ge "${aborted:-1}" ]; then
  ok "the broker half HELD: every one of ${aborted} dropped sessions produced a retained \
\`offline\` on its own status topic, median ${will_med} ms after the socket died"
elif [ "${observed:-0}" -gt 0 ]; then
  bad "only ${observed} of ${aborted} wills were published"
  finding MED "EMQX published ${observed} wills for ${aborted} sessions dropped without a \
DISCONNECT. A will that does not fire is a driver the platform still believes is online — R-15's \
grace timer never starts and the offer runs its full 15 s on somebody who is not there."
else
  bad "no wills were observed at all"
  finding HIGH "EMQX published no last will for ${aborted} sessions dropped without a DISCONNECT. \
R-15's entire mechanism depends on it, and so does T-04's stalled-tracker detection."
fi

# -------------------------------------------------------------------------------------
# Platform half — the one R-15 is actually about
# -------------------------------------------------------------------------------------
sleep 3
grace_after=$(psql_one "SELECT count(*) FROM dispatch.timers WHERE kind = 'offer_release_grace';")
grace_new=$(( ${grace_after:-0} - ${grace_before:-0} ))

degraded_row "dispatch-svc (platform half)" \
  "${grace_new} new \`offer_release_grace\` timers; \`Dispatch__LastWillEnabled\` = ${last_will_enabled}" \
  "\"dispatch-svc releases active offer / starts grace timer per ride state\" (R-15)"

if [ "$grace_new" -ge 1 ]; then
  ok "R-15 HELD: ${grace_new} offer_release_grace timer(s) armed from the wills"

  if [ -n "$real_vehicle" ]; then
    armed=$(psql_one "SELECT count(*) FROM dispatch.timers
                       WHERE driver_id = '${FIXTURE_DRIVER}' AND kind = 'offer_release_grace';")
    expect_at_least "the fixture driver's own offer got a grace timer" "$armed" 1

    # R-16's window. `Dispatch:OfferReleaseGrace` defaults to 5 s and dispatch-svc's CLAUDE.md
    # records that no spec pins it — the number is argued against the 15 s offer window.
    grace_sec=$(psql_one "SELECT round(extract(epoch FROM (fire_at - now())))
                            FROM dispatch.timers
                           WHERE driver_id = '${FIXTURE_DRIVER}' AND kind = 'offer_release_grace'
                           ORDER BY fire_at DESC LIMIT 1;")
    note "the grace fires in ${grace_sec} s (Dispatch:OfferReleaseGrace default 5 s; R-16's table \
gives 60 s / 120 s / 5 min / 10 min for the post-ACCEPT states, which are ride-svc's)"

    if released_ms=$(wait_for 40 1 offer_released "$FIXTURE_RIDE"); then
      ok "the offer was released $(human_ms "$released_ms") after the will — the driver is back in \
the pool and the ride is re-offerable"
    else
      finding MED "The grace timer was armed and the offer was not released within 40 s. R-15's \
purpose is that a ride does not spend its whole 15 s window on a driver who cannot answer."
    fi
  fi
else
  case "$last_will_enabled" in
    true|True|TRUE)
      finding HIGH "R-15 is switched on and does nothing. \`Dispatch__LastWillEnabled=${last_will_enabled}\`, \
EMQX published ${observed} \`offline\` wills, and no \`offer_release_grace\` timer was armed. A \
driver who loses coverage mid-offer holds it for the full 15 s and the ride waits on somebody who \
is not there." ;;
    *)
      finding HIGH "**R-15 is not wired on this deployment.** \`Dispatch__LastWillEnabled\` is \
${last_will_enabled}, so \`VehicleStatusWorker\` never subscribes to \`veh/+/status\` and the \
${observed} wills EMQX published for this drill's ${aborted} dropped sessions were delivered to \
nobody. dispatch-svc says so itself at start-up — \"Dispatch:LastWillEnabled is off, so R-15's \
EMQX last will is not consumed. A driver whose session drops mid-offer holds it until the 15 s \
window expires instead of the 00:00:05 grace\" — and the setting appears in no environment file: \
it is not in \`infra/env/.env.app.example\`, not in \`.env.replica.example\` and not in \
\`infra/k8s/\`, so **production would deploy with it off as well**. The default is \`false\` in \
\`DispatchOptions\` because dispatch-svc's CLAUDE.md notes it is the only part of the service \
needing a broker; every environment that has one should be setting it. **Three more mechanisms \
lose their input with it**: DT-04's directional filter is never cleared by a driver going offline, \
T-04's stalled-tracker detection loses its signal, and **R-16's four post-accept grace windows \
(offline-after-accept 60 s, after-arrive 120 s, in-progress 5 min, at-payment 10 min, ADD §11.12) \
are ride-svc's response to the same \`veh/{id}/status=offline\` fact — so a driver who loses \
coverage mid-ride starts no grace at all.** R-16 could not be drilled for that reason and is \
recorded as untested rather than as passing." ;;
  esac
fi

degraded_table_close

report ""
report "| Mass last will | Measured |"
report "|---|---|"
report "| Sessions dropped without DISCONNECT | ${aborted} of ${connected} connected |"
report "| Wills EMQX published on \`veh/+/status\` | ${observed} (med ${will_med} ms, max ${will_max} ms after the socket died) |"
report "| \`dispatch.timers\` \`offer_release_grace\` armed | ${grace_new} |"
report "| \`Dispatch__LastWillEnabled\` | \`${last_will_enabled}\` |"
report "| Generator wall clock | $(human_ms "$lwt_ms") |"
report ""

# The retained wills are real state left on the broker: `retain_available = true` and every one of
# these topics now holds `offline` for a vehicle that does not exist. Cleared with a zero-length
# retained publish, which is MQTT's own way of deleting one.
note "clearing ${observed} retained will payloads from the broker"
dc exec -T emqx /opt/emqx/bin/emqx ctl retainer clean 'veh/c0a0c0a0-+/status' >/dev/null 2>&1 || true

drill_end 60
