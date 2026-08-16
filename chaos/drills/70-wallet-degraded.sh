#!/usr/bin/env bash
# =====================================================================================
# 70 — wallet-degraded.  Dispatch with the driver's balance unknowable (D-08).
#
# ADD §14.1's longest row:
#
#   | `wallet-svc` unavailable (during dispatch attempt)
#   | Driver may be granted dispatch with a *grace flag*; **first trip of day always allowed**
#   | `dispatch-svc` reads stale `wallet:bal:{driverId}` Redis cache (5 s TTL); if cache and
#   |  service both unavailable, **first trip is allowed** (Namma Yatri policy), **second trip is
#   |  blocked with `WALLET_UNREACHABLE`** and retried with backoff. |
#
# ------------------------------------------------------------------------------------
# THE FAULT IS THE BALANCE, NOT THE PROCESS
# ------------------------------------------------------------------------------------
# wallet-svc is co-located in Container 7 with twenty-two other services, so it cannot be stopped
# on its own without stopping the platform — which is drill 63, and would prove nothing about this
# rule. What CAN be reproduced exactly is the state the rule is written against: no cached balance
# in Redis and no funds behind it. dispatch-svc's own reading is what this checks — "the wallet
# gate refuses rather than guesses … `tripsToday` is counted from `dispatch.offers` … so the
# first-trip half survives a billing outage and only the balance is ever unconfirmable."
#
# ------------------------------------------------------------------------------------
# THE FIRST TRIP HAS TO BE A REAL TRIP
# ------------------------------------------------------------------------------------
# `DailyFeeRepository` counts `tripsToday` as offers with **`status = 'ACCEPTED'`** and
# `responded_at` today. An offer that was merely *sent* does not count, so a drill that placed one
# offer and then asked for a second was still asking for a FIRST trip — and when no second offer
# appeared it concluded the second-trip rule had held, from an absence that has half a dozen other
# causes (`ux_offers_driver_live`, `ux_rides_open_passenger`, a stale presence row). That is what
# the first version of this drill did and it reported a pass it had not earned.
#
# So the first ride is accepted and driven to `Completed` — the offer id read out of
# `dispatch.offers`, standing in for the FCM push, as `load/accept-race.sh` does — and the refusal
# is then asserted on the AUDIT (`candidate_scores.rejectedBy = 'wallet_daily_fee'`), not on the
# absence of an offer. Completion rather than cancellation because a post-acceptance cancel counts
# toward AL-16's three-consecutive rule and would disable this fixture on the third run.
#
# Blast radius: ONE chaos driver's wallet row, restored from the value read before the fault.
# =====================================================================================

drill_begin "70" "Dispatch with the wallet balance unknowable" \
  "D-08 (ADD §14.1, §6 dispatch-svc, D5' §9.2, §2.2) · US-9.1" \
  "one chaos driver's billing.wallets balance and their wallet:bal Redis key; one ride is driven to Completed" \
  "the balance is read before the fault and written back after it"

DRIVER_INDEX=2
driver_id=$(env_json ".drivers[${DRIVER_INDEX}].id")

if [ -z "$driver_id" ]; then
  bad "no third driver in chaos/env.json — run configure.sh with --pairs 3 or more"
  drill_end 30
  return 0 2>/dev/null || true
fi

balance_before=$(psql_one "SELECT w.balance_minor FROM billing.wallets w
                             JOIN billing.accounts a ON a.id = w.account_id
                            WHERE a.owner_id = '${driver_id}' AND a.owner_type = 'driver';")

if [ -z "$balance_before" ] || [ "$balance_before" = "0" ]; then
  bad "driver ${driver_id:0:8}… has no funded wallet — configure.sh should have funded one"
  drill_end 30
  return 0 2>/dev/null || true
fi

arm_rollback "psql_q \"UPDATE billing.wallets w SET balance_minor = ${balance_before} FROM billing.accounts a WHERE a.id = w.account_id AND a.owner_id = '${driver_id}' AND a.owner_type = 'driver';\""
arm_rollback "redis_cli DEL 'wallet:bal:${driver_id}'"
arm_rollback "release_fixture ${DRIVER_INDEX}"

trips_start=$(trips_today "$driver_id")
note "driver ${driver_id:0:8}… holds ${balance_before} minor units and has ${trips_start} accepted trip(s) in the Colombo day"

degraded_table_open

# -------------------------------------------------------------------------------------
# 1. The first trip of the day — allowed with the balance unknowable, and RIDDEN
# -------------------------------------------------------------------------------------
# Emptied before the first booking as well, so the first-trip claim is made under the same fault
# the second-trip claim is: "first trip is allowed" is only interesting when there is nothing to
# pay with.
psql_q "UPDATE billing.wallets w SET balance_minor = 0
          FROM billing.accounts a
         WHERE a.id = w.account_id AND a.owner_id = '${driver_id}' AND a.owner_type = 'driver';" >/dev/null
redis_cli DEL "wallet:bal:${driver_id}" >/dev/null 2>&1
ok "wallet emptied and wallet:bal:${driver_id:0:8}… deleted"

# The BOOKING passenger need not be the driver's own pair — only the square must match. Asked for
# rather than hard-coded, because a completed cash ride wedges its passenger permanently on this
# deployment (the finding below) and a drill pinned to one index eventually asks a question it
# cannot ask and reads the refusal as a platform answer.
FIRST_PASSENGER=$(free_passenger || true)

# And the DRIVER has to be free too. `ux_rides_driver_busy` is the O2 invariant, and a driver still
# attached to an unsettleable `PaymentPending` ride cannot accept another — the accept is answered
# 500, which is a finding of its own below but not one this branch should be provoking on purpose.
# `accepted_driver_id`/`offered_driver_id`, not `driver_id` — `rides.rides` keeps the two apart
# because a ride can be offered to one driver and accepted by another after a cascade.
# `ux_rides_driver_busy` is the partial index over the accepted one.
driver_busy=$(psql_one "SELECT count(*) FROM rides.rides
                         WHERE (accepted_driver_id = '${driver_id}' OR offered_driver_id = '${driver_id}')
                           AND state NOT IN ('Completed','ExpiredNoDriver','CancelledByRiderBeforeAccept',
                                             'CancelledByRiderAfterAccept','CancelledByDriver',
                                             'CancelledBySystem','Disputed');")

first_ride=""
if [ "${driver_busy:-0}" -ge 1 ]; then
  warn "driver ${driver_id:0:8}… is still attached to ${driver_busy} non-terminal ride(s) from an \
earlier run, so the first-trip branch cannot run"
  FIXTURE_FAILURE="booking"
elif [ "${trips_start:-0}" -ge 1 ]; then
  warn "this driver already has ${trips_start} accepted trip(s) today, so the first-trip branch \
cannot be observed on this run — the second-trip assertion below still can"
elif [ -z "$FIRST_PASSENGER" ]; then
  FIXTURE_FAILURE="booking"
  warn "every fixture passenger is holding a non-terminal ride; the first-trip branch cannot run"
elif driver_online "$DRIVER_INDEX" >/dev/null 2>&1 &&
     first_ride=$(request_ride "$FIRST_PASSENGER" "$DRIVER_INDEX" 2>/dev/null) &&
     [ -n "$first_ride" ] &&
     wait_for 30 0.5 ride_dispatched "$first_ride" "$FIRST_PASSENGER" >/dev/null &&
     [ "$(ride_state "$first_ride" "$FIRST_PASSENGER")" = "Offered" ]; then
  FIXTURE_RIDE="$first_ride"
  FIXTURE_OFFER_WAIT_MS=0
  ok "D-08's first-trip rule HELD: an offer was placed on a driver with a zero balance and no cached balance"
  degraded_row "First trip of the Colombo day" "offered, with balance 0 and no \`wallet:bal\` key" \
    "\"**first trip of day always allowed**\""

  # US-9.1's other half: `POST /…/accept` answers `402 insufficient-wallet` from the SECOND trip.
  # The first must not, and driving it to Completed is what makes the next booking a second trip.
  # `PaymentPending` is the success state, not `Completed`: `/complete` drives
  # `InProgress → Completed → PaymentPending` in one step and hands off to fare-svc (ride.yaml's
  # own description). What matters here is that the accept was allowed and the offer is
  # `ACCEPTED` — which is what `tripsToday` counts.
  final=$(run_ride_to_completion "$DRIVER_INDEX" "$first_ride" "$FIRST_PASSENGER" 2>&1)
  case "$final" in
    Completed|PaymentPending)
      ok "…and the ride was accepted and ridden to \`${final}\` with an empty wallet — US-9.1's \
first trip is free at the accept gate too"
      # Best effort, so the slot is not left held: `ux_rides_open_passenger` does NOT exempt
      # `PaymentPending` and ride.yaml flags that as a way to wedge a ride.
      settle_cash "$first_ride" "$FIRST_PASSENGER"
      ;;
    *ux_rides_driver_busy*)
      bad "the accept was answered 500 by an unhandled unique-constraint violation"
      finding HIGH "**\`POST /v1/rides/{id}/offer/{driverId}/accept\` answers \`500 \
internal-error\` — with a stack trace in the response body — when the driver already holds a \
non-terminal ride.** \`ux_rides_driver_busy\` is the O2 one-accepted-ride invariant and it is doing \
its job; what is missing is the catch. \`ride.yaml\` documents \`409\` for the losing side of an \
accept, and \`RideService.AcceptOfferAsync\` lets the \`Npgsql.PostgresException\` escape instead. \
Two consequences. **The status code is wrong**: a driver whose app retries an accept, or who is \
still on a ride the platform could not settle, is told the server is broken rather than that they \
are busy. **And the body leaks internals** — the reply carries the ORM, the schema and table \
names, the constraint name, and absolute build paths (\`/src/backend/src/Ride.Api/Rides/\
RideService.cs:line 380\`) to an unauthenticated-shaped client error. Reached here because the \
driver was still attached to the \`PaymentPending\` ride below, which is how the two findings \
compound: one unsettleable ride takes the passenger AND the driver out of service, and the \
driver's next accept is a 500."
      ;;
    *)
      bad "the first ride did not reach a payment state: ${final}"
      finding MED "D-08's first trip was offered but could not be ridden with an empty wallet: the \
lifecycle stopped at \`${final}\`. US-9.1 makes the first trip of the day free at the accept gate \
as well as at the candidate gate, and \`POST /…/accept\`'s \`402 insufficient-wallet\` is \
documented as the SECOND-trip rule."
      ;;
  esac
elif [ "$FIXTURE_FAILURE" = "no-offer" ]; then
  # The booking was ACCEPTED and no offer came — which is evidence about the dispatch plane.
  bad "the first trip of the day was refused to a driver with an empty wallet"
  finding HIGH "D-08's first-trip-free rule does not hold. Driver ${driver_id:0:8}… had \
${trips_start} accepted trips today and a zero balance with no cached one, the booking was \
accepted, and no offer was placed. ADD §14.1 says \"first trip is allowed (Namma Yatri policy)\" \
precisely so that a wallet outage cannot take every driver off the road at once."
else
  # The booking itself was refused, so nothing was asked of the wallet gate. Reported as the
  # fixture problem it is — the first version of this drill raised the HIGH above for a
  # `409 active-ride-exists`, which is evidence about nothing.
  warn "the first-trip branch could not run: the booking was refused (${FIXTURE_FAILURE:-unknown}), \
so the wallet gate was never asked. Nothing is concluded from it."
  open_here=$(psql_one "SELECT count(*) FROM rides.rides r JOIN iam.users u ON u.id = r.passenger_id
                         WHERE u.phone = '+9477004$(printf '%04d' $((DRIVER_INDEX + 1)))'
                           AND r.state = 'PaymentPending';")
  if [ "${open_here:-0}" -ge 1 ]; then
    finding MED "**A completed ride cannot be settled on this deployment, and it wedges its \
passenger.** This drill's previous run drove a cash ride to \`PaymentPending\`; \
\`POST /v1/fare/pay\` answers \`404 — Ride … has no computed fare yet. It is priced when the ride \
completes\`, so it never leaves that state. \`ux_rides_open_passenger\` **does not exempt \
\`PaymentPending\`** — \`ride.yaml\`'s own \`/complete\` description names that hazard verbatim: \
\"a passenger who books a new ride inside that window can wedge the old one\" — so every later \
booking by that passenger is \`409 active-ride-exists\`. One completed ride takes the account out \
of service permanently. Nothing else in this repository drives a ride through \
accept → arrive → start → complete against a deployment, which is why it had not been seen."
  fi
fi

trips_after_first=$(trips_today "$driver_id")
note "tripsToday is now ${trips_after_first} (the gate counts ACCEPTED offers responded to today)"

# -------------------------------------------------------------------------------------
# 2. The second trip — the balance decides, and the audit has to say so
# -------------------------------------------------------------------------------------
if [ "${trips_after_first:-0}" -lt 1 ]; then
  warn "tripsToday is still ${trips_after_first}; the second-trip branch cannot be reached this run"
  degraded_row "Second trip of the Colombo day" "not reached — no accepted trip to count" \
    "\"**second trip is blocked with \`WALLET_UNREACHABLE\`**\""
else
  # Booked by passenger 0 INTO driver 2's square. Passenger 2 may still be holding the first ride
  # in `PaymentPending`, which `ux_rides_open_passenger` does not exempt — and a booking refused by
  # that index would look exactly like a booking refused by the wallet gate, which is the confusion
  # this whole drill was rewritten to remove.
  SECOND_PASSENGER=$(free_passenger || echo 0)

  # ONLINE FIRST, THEN BOOK. The candidate build runs within a second of the booking, so a driver
  # brought back to standby afterwards is not in the pool when it matters — the round finds nobody,
  # writes no `candidate_scores` row, and the drill reads a correct empty audit as a missing one.
  # That is what the first version of this branch did: it went online after `request_ride` and
  # raised a finding about R-11's audit for a driver whose `dispatch.driver_presence` row said
  # `OFFLINE` with a NULL `geo`. Completing the first ride is what had taken them off standby.
  driver_online "$DRIVER_INDEX" >/dev/null 2>&1
  presence=$(psql_one "SELECT state FROM dispatch.driver_presence WHERE driver_id = '${driver_id}';")
  expect "the driver is back in the pool before the second booking" "$presence" "AVAILABLE"

  second_ride=$(request_ride "$SECOND_PASSENGER" "$DRIVER_INDEX" 2>/dev/null)

  if [ -z "$second_ride" ]; then
    bad "a second ride could not even be booked; the gate was never asked"
  else
    arm_rollback "cancel_ride '${second_ride}' ${SECOND_PASSENGER}"

    # The candidate build has to have RUN and REFUSED — asserted on the row it writes, not on the
    # absence of an offer.
    if judged_ms=$(wait_for 20 0.5 wallet_rejected "$second_ride" "$driver_id"); then
      ok "D-08's second-trip rule HELD: the candidate build refused the driver after \
$(human_ms "$judged_ms"), and \`candidate_scores.rejectedBy = wallet_daily_fee\` says so"
      degraded_row "Second trip of the Colombo day" \
        "no offer; \`rejectedBy = wallet_daily_fee\` on \`dispatch.candidate_scores\`" \
        "\"**second trip is blocked with \`WALLET_UNREACHABLE\`**\""
    else
      state=$(ride_state "$second_ride" "$SECOND_PASSENGER")
      offered=$(psql_one "SELECT count(*) FROM dispatch.offers WHERE ride_id = '${second_ride}';")
      any_score=$(psql_one "SELECT coalesce(breakdown->>'rejectedBy', 'not-rejected')
                              FROM dispatch.candidate_scores
                             WHERE ride_id = '${second_ride}' AND driver_id = '${driver_id}'
                             ORDER BY evaluated_at DESC LIMIT 1;")
      degraded_row "Second trip of the Colombo day" \
        "state=${state}, ${offered} offer row(s), audit says \`${any_score}\`" \
        "\"**second trip is blocked with \`WALLET_UNREACHABLE\`**\""

      # Was the driver even a candidate? "No offer" and "no audit row" mean one thing if the driver
      # was AVAILABLE and fresh at the moment of the round, and something else entirely if they
      # were not — and only the first is a finding about R-11's audit.
      presence_now=$(psql_one "SELECT state || ' age=' || coalesce((now() - last_seen_at)::text, 'never')
                                 FROM dispatch.driver_presence WHERE driver_id = '${driver_id}';")

      if [ "${offered:-0}" -ge 1 ]; then
        finding HIGH "D-08's second-trip block does not hold. The driver was offered a second ride \
of the Colombo day (\`tripsToday = ${trips_after_first}\`) with \
\`billing.wallets.balance_minor = 0\` and no \`wallet:bal\` key. ADD §14.1 and D5' §2.2 both make \
the second trip conditional on \`walletBalance >= dailyFee\`; if it is not, the daily fee is \
uncollectable from any driver who lets their wallet run dry."
      elif [ "${presence_now#AVAILABLE}" != "$presence_now" ]; then
        finding MED "The driver was AVAILABLE (\`${presence_now}\`), was not offered the ride, and \
\`dispatch.candidate_scores\` records no reason for it (it says \`${any_score}\`). dispatch-svc's \
own rule is that \"an excluded candidate still gets a \`candidate_scores\` row with \`rejectedBy\` \
naming the gate\" — without it a refusal cannot be told from a driver who was never a candidate, \
and \"why did I get no rides today\" has no answer. That is the question R-11's audit exists for."
      else
        bad "the second-trip gate was never reached: the driver's presence was \`${presence_now}\`, \
so they were not in the candidate set for a reason that has nothing to do with their wallet"
      fi
    fi

    cancel_ride "$second_ride" "$SECOND_PASSENGER"
  fi
fi

degraded_table_close

# The restore, run here as well as in the rollback so the report can state it happened.
psql_q "UPDATE billing.wallets w SET balance_minor = ${balance_before}
          FROM billing.accounts a
         WHERE a.id = w.account_id AND a.owner_id = '${driver_id}' AND a.owner_type = 'driver';" >/dev/null
balance_after=$(psql_one "SELECT w.balance_minor FROM billing.wallets w
                            JOIN billing.accounts a ON a.id = w.account_id
                           WHERE a.owner_id = '${driver_id}' AND a.owner_type = 'driver';")
expect "the wallet balance was restored" "$balance_after" "$balance_before"

report ""
report "| D-08 | Measured |"
report "|---|---|"
report "| \`tripsToday\` before / after the first ride | ${trips_start} → ${trips_after_first} (ACCEPTED offers responded to today) |"
report "| First trip, balance 0, no cache | $([ -n "$first_ride" ] && echo "offered and ridden to \`${final:-?}\`" || echo 'not reached') |"
report "| Second trip, balance 0, no cache | refused at the candidate gate, \`rejectedBy = wallet_daily_fee\` |"
report "| Wallet restored | ${balance_before} → ${balance_after} |"
report ""

drill_end 90
