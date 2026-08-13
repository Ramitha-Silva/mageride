#!/usr/bin/env bash
# =====================================================================================
# 30 — redpanda-loss.  The event backbone stops, and the outbox is asked to do its job.
#
# ADD §14.1's row for this is written as consumer lag, not broker loss:
#
#   | Stream consumer lag | Position updates delayed
#   |                     | Payload includes `data_age` field; app shows "updating..." indicator |
#
# Broker loss is the extreme of the same thing, and it is the case the transactional outbox exists
# for (D6' §2.4): a booking commits to Postgres whether or not anything can be published, and the
# `ride.requested` event waits in `rides.outbox` until it can. This drill checks all three claims —
# that bookings still commit, that nothing is lost, and that the data-age signal the app is
# supposed to show actually exists.
# =====================================================================================

drill_begin "30" "Event backbone down" \
  "ADD §14.1 (stream consumer lag) · D6' §2.4 transactional outbox · ADD §7.6 (single broker, RF=1)" \
  "Redpanda — every outbox dispatcher's producer and every consumer group" \
  "docker compose start redpanda"

undispatched_before=$(psql_one "SELECT count(*) FROM rides.outbox WHERE dispatched_at IS NULL;")
failures_before=$(metric "$(metrics_of app-services 5000)" mageride_outbox_publish_failures_total)
dispatched_before=$(metric "$(metrics_of app-services 5000)" mageride_outbox_dispatched_total)

arm_rollback "dc start redpanda"

stopped_at=$(now_ms)
dc stop redpanda >/dev/null 2>&1
ok "redpanda stopped after $(human_ms "$(since_ms "$stopped_at")")"
sleep 3

degraded_table_open

# -------------------------------------------------------------------------------------
# The outbox's whole reason for existing
# -------------------------------------------------------------------------------------
driver_online 0 >/dev/null 2>&1
arm_rollback "driver_offline 0"

book_started=$(now_ms)
ride=$(request_ride 0 2>/dev/null)
book_ms=$(since_ms "$book_started")

if [ -n "$ride" ]; then
  arm_rollback "cancel_ride '${ride}' 0"
  ok "D6' §2.4 HELD: a booking still commits with the broker down (${book_ms} ms, ride ${ride:0:8}…)"
  degraded_row "Booking (\`POST /v1/rides/request\`)" "accepted in ${book_ms} ms" \
    "implied by the transactional outbox — the commit does not depend on the broker"

  # The event must be WAITING, not gone. That is the difference between an outbox and a log line.
  sleep 2
  held=$(psql_one "SELECT count(*) FROM rides.outbox WHERE aggregate_id = '${ride}' AND dispatched_at IS NULL;")
  expect_at_least "the ride.requested event is held undispatched in rides.outbox" "$held" 1

  # And the ride cannot progress, because dispatch-svc learns about it from `ride.events`.
  state=$(ride_state "$ride" 0)
  degraded_row "…and its dispatch" "state=${state}" \
    "not described — §14.1's row is about *delay*, not about a stopped broker"
  expect_one_of "the ride is held at the booking state, not failed" "$state" "Requested" "Matching"
else
  bad "the booking was refused with the broker down (${book_ms} ms)"
  finding HIGH "A Redpanda outage refuses bookings. The transactional outbox (D6' §2.4, and this \
platform's stated reason for having no direct HTTP calls between services for state changes) \
exists precisely so that a commit does not depend on the broker; if \`POST /v1/rides/request\` \
fails while Redpanda is down, the write path is publishing inline somewhere."
fi

nearby=$(probe_nearby)
degraded_row "Live map (\`GET /v1/nearby\`)" "\`${nearby}\` (code, limitedLive, vehicles)" \
  "\"Position updates delayed\""

sos_degraded_row "$(probe_sos 0)"

# -------------------------------------------------------------------------------------
# The `data_age` signal §14.1 promises the app will show
# -------------------------------------------------------------------------------------
# This is the claim that can be checked without any fault at all, which is why it is checked here
# rather than inferred: under consumer lag the positions on the map are old, and §14.1 says the
# payload carries their age so the client can render "updating…".
map_fields=$(edge_get_as "$(env_json '.passengers[0].bearer')" \
  "/v1/nearby?lat=6.9271&lng=79.8612&radiusM=5000" | jq -r '(.vehicles[0] // {}) | keys | join(",")' 2>/dev/null)
top_fields=$(edge_get_as "$(env_json '.passengers[0].bearer')" \
  "/v1/nearby?lat=6.9271&lng=79.8612&radiusM=5000" | jq -r 'keys | join(",")' 2>/dev/null)

degraded_row "Position payload age" "top-level: \`${top_fields:-?}\`; per vehicle: \`${map_fields:-none returned}\`" \
  "\"Payload includes \`data_age\` field; app shows 'updating...' indicator\""

case "${top_fields},${map_fields}" in
  *data_age*|*dataAge*) ok "the position payload carries a data-age field" ;;
  *)
    finding HIGH "ADD §14.1's only client-visible signal for stream lag does not exist. The row \
promises \"Payload includes \`data_age\` field; app shows 'updating...' indicator\" and no such \
field is on any surface: \`NearbyResponse\` is \`{vehicles, asOf, limitedLive}\` and \
\`VehicleFrame\` (\`LiveHubContract.cs\`, the SignalR frame) is \
\`{VehicleId, Lat, Lng, Heading, Speed, Type, Mode}\` — neither carries a sample timestamp. \
\`asOf\` is when query-svc ANSWERED, not when the position was taken, so under lag the map \
reports a fresh timestamp over stale markers and a client has nothing to compute an age from. \
The degradation is real and invisible: the passenger sees a driver frozen in the road with a \
current clock beside them."
    ;;
esac

degraded_table_close

# -------------------------------------------------------------------------------------
# Recovery — nothing lost, and how long the backlog takes to drain
# -------------------------------------------------------------------------------------
undispatched_peak=$(psql_one "SELECT count(*) FROM rides.outbox WHERE dispatched_at IS NULL;")

# How long the outage takes to become VISIBLE, which is not the same as how long it takes to
# happen. `alerts.infrastructure.yml` pages on `rate(mageride_outbox_publish_failures_total) > 0`,
# and that counter cannot move until librdkafka gives up on the message: `Kafka:MessageTimeoutMs`
# is 15 s and `KafkaEventPublisher` awaits each delivery in turn. Until then the drain loop is
# parked inside one `ProduceAsync` and every gauge an operator has says the platform is fine.
# The first version of this drill read the counter eight seconds after the booking and failed its
# own assertion for exactly this reason.
if visible_ms=$(wait_for 45 2 outbox_failures_advanced "$failures_before"); then
  ok "the outbox reported a publish failure $(human_ms "$visible_ms") after the booking — \
Kafka:MessageTimeoutMs is 15 s and that is the floor on how fast an operator can be told"
else
  finding MED "A total broker outage produces no \`mageride_outbox_publish_failures\` inside 45 s, \
so \`alerts.infrastructure.yml\`'s OutboxPublishFailures rule (\`rate(...) > 0\`) does not fire. \
The other outbox alert is OutboxLag, whose expression is a p95 over \
\`mageride_outbox_dispatch_latency_milliseconds_bucket\` — a histogram that only takes an \
observation when a row IS dispatched, so a stopped backbone makes it go quiet rather than tall. \
Between them the two rules cover a slow outbox and not a stopped one."
fi

failures_during=$(metric "$(metrics_of app-services 5000)" mageride_outbox_publish_failures_total)
note "rides.outbox held ${undispatched_peak} undispatched rows at the peak (was ${undispatched_before}); \
mageride.outbox.publish_failures advanced by $((failures_during - failures_before))"

outage_ms=$(since_ms "$stopped_at")
recovery_started=$(now_ms)
dc start redpanda >/dev/null 2>&1

if rp_ms=$(wait_for 180 3 service_healthy redpanda); then
  ok "redpanda healthy again $(human_ms "$rp_ms") after start"
else
  bad "redpanda did not report healthy within 180 s"
fi

if drained_ms=$(wait_for 120 2 outbox_drained rides); then
  ok "the outbox drained completely $(human_ms "$drained_ms") after the broker returned — \
$(( undispatched_peak - undispatched_before )) held events, none lost"
else
  still=$(psql_one "SELECT count(*) FROM rides.outbox WHERE dispatched_at IS NULL;")
  bad "the outbox still holds ${still} undispatched rows two minutes after the broker returned"
  finding HIGH "The outbox did not drain after Redpanda came back: ${still} rows are still \
undispatched. At-least-once delivery is the platform's stated contract (D6' §2.3) and an outbox \
that stops retrying makes it at-most-once."
fi

dispatched_after=$(metric "$(metrics_of app-services 5000)" mageride_outbox_dispatched_total)
note "mageride.outbox.dispatched advanced by $((dispatched_after - dispatched_before)) across the drill"

report ""
report "| Redpanda outage | Measured |"
report "|---|---|"
report "| Outage length | $(human_ms "$outage_ms") |"
report "| Undispatched outbox rows at peak | ${undispatched_peak} (baseline ${undispatched_before}) |"
report "| Broker healthy again | $(human_ms "${rp_ms:-0}") |"
report "| Outbox fully drained | $(human_ms "${drained_ms:-0}") after the broker returned |"
report ""

# The ride booked during the outage has to make it out the other side under its own steam. That is
# the whole promise: the event was late, not lost.
if [ -n "$ride" ]; then
  if recovered_state_ms=$(wait_for 60 2 ride_dispatched "$ride"); then
    ok "the ride booked during the outage reached $(ride_state "$ride" 0) \
$(human_ms "$recovered_state_ms") after the broker returned"
  else
    finding MED "The ride booked while Redpanda was down was still \`$(ride_state "$ride" 0)\` a \
minute after the broker returned, with its outbox row dispatched. The event was replayed onto \
\`ride.events\`; whether dispatch-svc acted on it depends on where its consumer group's offset \
was, and \`RideEventConsumer\` reads from Earliest for exactly this reason. Worth a look before \
the same thing is claimed for a production outage."
  fi
fi

drill_end 180
