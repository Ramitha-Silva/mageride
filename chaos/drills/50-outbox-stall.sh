#!/usr/bin/env bash
# =====================================================================================
# 50 — outbox-stall.  The dispatcher stops draining while everything else stays up.
#
# E-09 is the tightest latency claim in the ADD:
#
#   | E-09 | Outbox poll latency adds ~250 ms to offer | Postgres LISTEN/NOTIFY wakeup … on
#   |      |                                           | `outbox.events`; offer push median < 50 ms |
#
# Drill 30 stopped the broker, which stalls the outbox from the far side and takes the whole event
# plane with it. This one stalls the DISPATCHER alone, with Postgres, Redis, Redpanda and every
# service healthy — the failure mode where one background worker is wedged and nothing else is,
# which is what `docs/runbooks/outbox-lag.md` exists for and the one an operator is least likely
# to recognise.
#
# ------------------------------------------------------------------------------------
# THE LEVER IS THE DISPATCHER'S OWN LEADER ELECTION
# ------------------------------------------------------------------------------------
# `OutboxDispatcher.DrainOnceAsync` opens with `pg_try_advisory_xact_lock(key)` where the key is
# FNV-1a of `"{schema}.{table}"` — "a transaction-scoped advisory lock elects one drainer at a
# time, which is what preserves the per-aggregate ordering consumers depend on". Holding that same
# key from an outside SESSION makes every drain attempt return "another replica is draining" and
# publish nothing, for ever, with no error anywhere.
#
# It is the cleanest fault in this suite: exact blast radius (one service's outbox), no container
# touched, no data destroyed, and instant rollback. It also SELF-HEALS — the holder is a `psql`
# running `pg_sleep`, so the lock is released by the session ending even if this script is killed.
# =====================================================================================

drill_begin "50" "Outbox dispatcher stalled" \
  "E-09 (ADD §6, §9.4, §13.4 bullet 6) · D6' §2.4 · docs/runbooks/outbox-lag.md" \
  "ride-svc's outbox drain loop only — nothing is stopped, restarted or deleted" \
  "release the advisory lock (and the holder expires on its own after ${CHAOS_STALL_SECONDS:-70} s)"

# FNV-1a of "rides.outbox", as `OutboxDispatcher.AdvisoryLockKey` computes it. Recomputed here in
# python rather than hard-coded, so a change to the schema or table name in `Ride__Outbox__*`
# cannot leave this drill silently locking a key nothing uses and reporting that the outbox
# survived a stall it never had.
OUTBOX_SCHEMA="${OUTBOX_SCHEMA:-rides}"
LOCK_KEY=$(python3 -c "
h = 14695981039346656037
for b in '${OUTBOX_SCHEMA}.outbox'.encode():
    h ^= b
    h = (h * 1099511628211) & 0xFFFFFFFFFFFFFFFF
print(h - (1 << 64) if h >= (1 << 63) else h)")
note "advisory key for ${OUTBOX_SCHEMA}.outbox is ${LOCK_KEY}"

STALL_SECONDS="${CHAOS_STALL_SECONDS:-70}"

# -------------------------------------------------------------------------------------
# 0. Control — E-09's own number, with nothing stalled
# -------------------------------------------------------------------------------------
driver_online 0 >/dev/null 2>&1
arm_rollback "driver_offline 0"

control_ride=$(request_ride 0 2>/dev/null)
if [ -n "$control_ride" ]; then
  arm_rollback "cancel_ride '${control_ride}' 0"
  wait_for 20 0.2 outbox_drained "$OUTBOX_SCHEMA" >/dev/null
  control_hop=$(psql_one "SELECT round(extract(epoch FROM (dispatched_at - created_at)) * 1000)
                            FROM ${OUTBOX_SCHEMA}.outbox WHERE aggregate_id = '${control_ride}'
                           ORDER BY id LIMIT 1;")
  note "control: ride.requested took ${control_hop} ms from commit to dispatched (E-09 asks for < 50 ms median)"
  cancel_ride "$control_ride" 0
fi

# -------------------------------------------------------------------------------------
# 1. The fault
# -------------------------------------------------------------------------------------
# Backgrounded, and it releases itself. `arm_rollback` kills it early on every exit path; the
# `pg_sleep` is the belt to that braces, because a stalled outbox left behind by a crashed drill
# would be an event plane that stops with every container healthy.
dc exec -T postgres psql -U "${PG_USER:-mageride}" -d "${PG_DATABASE:-mageride}" -qtAX \
  -c "SELECT pg_advisory_lock(${LOCK_KEY});" -c "SELECT pg_sleep(${STALL_SECONDS});" \
  >/dev/null 2>&1 &
HOLDER_PID=$!
arm_rollback "kill ${HOLDER_PID} 2>/dev/null; dc exec -T postgres psql -U '${PG_USER:-mageride}' -d '${PG_DATABASE:-mageride}' -qtAX -c \"SELECT pg_advisory_unlock_all();\""

sleep 2

held=$(psql_one "BEGIN; SELECT pg_try_advisory_xact_lock(${LOCK_KEY}); ROLLBACK;")
expect "the drain lock is held against the dispatcher" "$held" "f"

stalled_at=$(now_ms)
undispatched_before=$(undispatched "$OUTBOX_SCHEMA")

# -------------------------------------------------------------------------------------
# 2. What a stalled dispatcher looks like from every angle an operator has
# -------------------------------------------------------------------------------------
degraded_table_open

ride=$(request_ride 0 2>/dev/null)

if [ -n "$ride" ]; then
  arm_rollback "cancel_ride '${ride}' 0"
  ok "the booking still commits: ride ${ride:0:8}… (the outbox is a background worker, not the write path)"
  degraded_row "Booking (\`POST /v1/rides/request\`)" "accepted, 202" "not described"

  sleep 5

  waiting=$(psql_one "SELECT count(*) FROM ${OUTBOX_SCHEMA}.outbox WHERE aggregate_id = '${ride}' AND dispatched_at IS NULL;")
  expect_at_least "its ride.requested is stuck in the outbox" "$waiting" 1

  state=$(ride_state "$ride" 0)
  degraded_row "The ride itself" "state=\`${state}\` five seconds after booking" \
    "E-09 budgets the outbox hop at < 50 ms median"

  # This is the failure's whole shape: nothing is down, nothing is erroring, and the ride never
  # moves. `Requested` is the state ride-svc writes on commit; `Matching` is what dispatch-svc's
  # consumer produces after the event is published.
  if [ "$state" = "Requested" ]; then
    ok "the ride is frozen at Requested — the event never reached dispatch-svc, and nothing failed"
  else
    warn "the ride reached ${state} with the drain lock held — another replica drained it?"
  fi

  nearby=$(probe_nearby)
  degraded_row "Live map (\`GET /v1/nearby\`)" "\`${nearby}\`" "unaffected — the position plane has its own path"
  expect "the position plane is untouched by a stalled ride outbox" \
    "$(printf '%s' "$nearby" | awk '{print $1}')" "200"

  health=$(dc exec -T app-services sh -c "curl -fsS -o /dev/null -w '%{http_code}' http://127.0.0.1:5000/health/ready" 2>/dev/null | tr -d '\r')
  degraded_row "\`/health/ready\`" "HTTP ${health:-unreadable}" "not described"

  if [ "$health" = "200" ]; then
    finding MED "A wedged outbox dispatcher is invisible to every liveness signal the platform \
has. With ride-svc's drain lock held: \`/health/ready\` answers 200, every container is healthy, \
no log line is written, \`mageride_outbox_publish_failures\` does not move (nothing threw — the \
drain returns 0 rows because it lost the leader election), and \
\`mageride_outbox_dispatch_latency\` goes QUIET rather than tall, because a histogram only takes \
an observation when a row IS dispatched. \`alerts.infrastructure.yml\`'s OutboxLag rule is a p95 \
over that histogram's buckets, so it cannot fire on this. The one signal that does move is the \
count of undispatched rows, and nothing exports it. Every ride booked in the meantime sits in \
\`Requested\` and is never offered to anybody."
  fi

  # ADD §13.3.1 gives a ride 60 s in a pre-match state before R-20 calls it stuck. That IS the
  # backstop for this failure, and it is worth knowing whether it fires — it is the difference
  # between a silent stall and one the business dashboards eventually notice.
  stuck_before=$(metric "$(metrics_of app-services 5000)" mageride_rides_stuck_detected_total)
else
  bad "the booking was refused with only the outbox dispatcher stalled"
fi

sos_degraded_row "$(probe_sos 0)"
degraded_table_close

# -------------------------------------------------------------------------------------
# 3. Release, and measure how fast E-09's LISTEN/NOTIFY path catches up
# -------------------------------------------------------------------------------------
stall_ms=$(since_ms "$stalled_at")
released_at=$(now_ms)
kill "$HOLDER_PID" 2>/dev/null
# The kill ends `docker compose exec`'s local process; the psql inside the container is what holds
# the lock, so it is unlocked explicitly as well. `pg_advisory_unlock_all()` runs in a NEW session
# and cannot touch another session's locks, so the terminate is what actually does it.
psql_q "SELECT pg_terminate_backend(pid) FROM pg_stat_activity
         WHERE query LIKE '%pg_sleep(${STALL_SECONDS})%' AND pid <> pg_backend_pid();" >/dev/null 2>&1

if drained_ms=$(wait_for 60 0.5 outbox_drained "$OUTBOX_SCHEMA"); then
  ok "the outbox drained $(human_ms "$drained_ms") after the lock was released — \
$(( $(undispatched "$OUTBOX_SCHEMA") + 0 )) rows left, nothing lost across a $(human_ms "$stall_ms") stall"
else
  bad "the outbox still held $(undispatched "$OUTBOX_SCHEMA") rows a minute after the lock was released"
  finding HIGH "The outbox did not resume after its drain lock was released. \
\`OutboxDispatcher\`'s wake-up is a LISTEN/NOTIFY latch plus a \`PollInterval\` safety net of 5 s; \
if neither fires after a stall, a transient lock contention becomes a permanent stop."
fi

if [ -n "$ride" ]; then
  hop=$(psql_one "SELECT round(extract(epoch FROM (dispatched_at - created_at)) * 1000)
                    FROM ${OUTBOX_SCHEMA}.outbox WHERE aggregate_id = '${ride}' ORDER BY id LIMIT 1;")
  note "the stalled event's total outbox hop was ${hop} ms (control ${control_hop:-?} ms, E-09 asks < 50 ms median)"

  if resumed_ms=$(wait_for 60 1 ride_dispatched "$ride"); then
    ok "the frozen ride reached $(ride_state "$ride" 0) $(human_ms "$resumed_ms") after the outbox resumed"
  else
    finding MED "The ride booked during the stall was still \`$(ride_state "$ride" 0)\` a minute \
after its outbox row was dispatched. The event was published; whether it produced an offer depends \
on the 120 s US-6A.11 deadline having already passed, which for a stall longer than that it will \
have. A stall of more than two minutes therefore costs every ride booked inside it, and the \
passenger is told \`ExpiredNoDriver\` — indistinguishable from no driver being available."
  fi

  stuck_after=$(metric "$(metrics_of app-services 5000)" mageride_rides_stuck_detected_total)
  if [ "$((stuck_after - stuck_before))" -ge 1 ]; then
    ok "R-20's stuck-state observer noticed: mageride.rides.stuck_detected advanced by $((stuck_after - stuck_before))"
  else
    note "mageride.rides.stuck_detected did not move — the stall (${stall_ms} ms) was shorter than \
ADD §13.3.1's 60 s budget for a pre-match state, which is the correct answer"
  fi
fi

report ""
report "| Outbox stall | Measured |"
report "|---|---|"
report "| Stall length | $(human_ms "$stall_ms") |"
report "| Control hop (commit → dispatched) | ${control_hop:-not measured} ms |"
report "| Stalled hop | ${hop:-not measured} ms |"
report "| Drain after release | $(human_ms "${drained_ms:-0}") |"
report "| E-09's budget | offer push median < 50 ms |"
report ""

drill_end 90
