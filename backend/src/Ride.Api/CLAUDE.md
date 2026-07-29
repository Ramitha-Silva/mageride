# ride-svc (C022 happy path, C023 Δ, C032 core) — Mode C ride aggregate

Stack: .NET 10 Minimal API + Dapper over Npgsql + MQTTnet. References `MageRide.Shared` (C002).

**Verify:** `dotnet test backend/src/Ride.Api.Tests -c Release`

## What this service is

The whole Mode C ride: the D5' §6 / ADD Appendix B.2 state machine, the §11.12 server-owned
cancellation and no-show matrix, the R-04 durable timers, the R-15/R-16 last-will graces, the R-05
payment terminals and the R-20 stuck-state SLOs. Everything here matches
`backend/contracts/ride.yaml`, which wins over this file and over the code.

| Endpoint | Spec |
|---|---|
| `POST /v1/rides/request` | D3' ride-svc, R-18, AL-16 |
| `GET /v1/rides/{id}` · `/state` | D3' route table |
| `GET /v1/rides/passenger/{id}/active` · `/driver/{id}/active` | D3' route table, R-18 |
| `POST /v1/rides/{id}/offer/{driverId}/accept` · `/decline` | ADD §11.11, §11.12 |
| `POST /v1/rides/{id}/arrive` · `/start` · `/complete` | D3' route table |
| `POST /v1/rides/{id}/cancel` | **Δ C032** — the §11.12 matrix |
| `POST /v1/internal/rides/{id}/system-cancel` · `/payment-settled` · `GET /saga-state` | **Δ C032** — D3' internal family |
| `POST /v1/internal/rides/{id}/matching` · `/offer` | **Δ C022** — see below |
| `POST /v1/internal/rides/{id}/offer/expire` | **Δ C023**, `reason` **Δ C034** — see below |

**Not here, on purpose.** Proxy booking and the P-02 location-request family, package delivery and
its two OTP gates are **C037**. `/dispute` is **C049/C050** (it opens a support ticket, which is
support-svc's). `GET /v1/rides/history` is **C048**. All are left unmapped rather than stubbed.

## Rules that are load-bearing

- **ride-svc is the sole writer of `rides.state`** (R-01, D5' §6). ADD §11.11's diagram draws
  dispatch-svc updating the row itself; §11.12 in the same document says sole-writer, and
  sole-writer wins. The moves other services drive are therefore commands on
  `/v1/internal/rides/**`, and `dispatch.offers` / `dispatch.candidate_scores` stay dispatch's.
- **The offer-expiry deadline decides, and `driver_unreachable` is its one exception** (Δ C034).
  `offer/expire` is bound to `offer_expires_at <= now()` evaluated by Postgres, so a sweeping node
  whose clock ran ahead cannot take an offer from a driver still inside the window. R-15 is the
  only caller allowed past it: dispatch-svc has watched the driver's EMQX session stay dead for a
  whole grace period, so there is no window left to protect. `RideOfferExpiryReasons` is a closed
  set rather than a boolean, so `rides.transitions.reason_code` records which of the two happened
  (`OFFER_EXPIRED` / `DRIVER_UNREACHABLE`). Both emit `offer.expired` — the consumer's reaction is
  identical.
- **The matrix decides, not the caller.** `POST /cancel` takes a `reason`, and the reason decides
  nothing: `RideCancellationMatrix` resolves (state × trigger) → (target, penalty, reputation hit),
  the trigger comes from which party is authenticated, and the guarded `UPDATE` is bound to the
  same state the matrix was resolved from. A ride that moved in between is answered `409`, never
  terminated under another row's rules. The client's reason is recorded and published because
  reputation-svc and support want it.
- **Every mutation is one transaction, and `RideStateWriter` is the only way through it**: the
  conditional `UPDATE`, the `rides.transitions` audit row (invariant 4), the ride's durable timers
  and the `rides.outbox` rows (R-13). Timers are armed *inside* the transaction that changes the
  state — a ride that reached `DriverArrived` and whose no-show timer was written afterwards would,
  on a crash between them, wait forever for a rider who never came.
- **No money moves here.** The Rs 50 (D-05), the Rs 100 no-show fee and the mid-trip full fare are
  *accrued* — stated on `cancellation.penalty.accrued` and settled by fare-svc against the
  passenger's next completed trip (D5' §7.1). ride-svc writes no ledger entry and no
  `dispatch.cancellation_penalties` row; those belong to other bounded contexts and the fence for
  this component is that cross-service state changes go through the outbox and nothing else.
- **The full fare travels as a rule, not a number.** A mid-trip cancel accrues the *quoted* fare
  because that is the only amount this service holds; `basis: full_fare` is what tells fare-svc to
  bill the metered distance instead. Same for the no-show's `base_fare_half` — the base fare is per
  tier (D5' §1.1) and lives in `fares.tariffs`.
- **ride-svc owns four of the eight `rides.timers` kinds.** `arrival_grace`, `no_show`,
  `payment_pending`, `offline_grace`. `offer_expiry` is dispatch-svc's (ADD §6 gives it "Quartz.NET
  (scheduled rides **+ offer backstop**)", and C023 built it); the other three are C037's. Every
  query in `RideTimerRepository` is scoped by kind, which is what lets two services share one table
  with no coordination protocol.
- **A lease-poll, not Quartz.** ADD §6 names "Quartz.NET clustered scheduler". What R-04 requires
  is that the durable row decides and that a fire lands within about a second on any replica;
  Quartz's contribution would be a job store holding one recurring trigger whose job is to scan
  `rides.timers`, and that scan is already multi-replica safe because it claims
  `FOR UPDATE SKIP LOCKED`. Clustering it would remove parallelism rather than add safety, for
  eleven `qrtz_*` tables no DDL spec declares. C023 reached the same conclusion for `offer_expiry`;
  running two different timer mechanisms over one table would be worse than either.
- **A last will starts a clock; it does not cancel a ride.** R-16's four windows (60 s after
  accept, 120 s after arrive, 5 min in progress, 10 min at payment) exist because a driver in an
  underpass has not abandoned anybody. An `offline` arms `offline_grace`, an `online` retires it,
  and only the timer expiring reaches the matrix. Redelivery cannot restart the clock —
  `ArmIfAbsentAsync` settles that in the `INSERT`, not with a prior read.
- **A grace re-plans itself when the ride moves.** A driver who goes dark while `Accepted` and then
  taps Arrive gets the 120-second window, computed from the *same* instant they went away — so
  moving the ride along is not a way to earn a fresh grace, and the re-plan terminates.
- **`payment_pending` moves nothing.** No row of §11.12 takes a ride out of `PaymentPending` on a
  timeout and R-05 reserves that door for fare-svc, so the timer produces the ADD §13.3.1 alert
  with a ride id on it and the `runbooks/ride-stuck.md` pointer, and nothing else.
- **R-05 has exactly one door.** `POST /internal/{id}/payment-settled` with a *terminal*
  `PaymentState`; anything still in flight is `400 illegal-transition`. `earningPayable` is true for
  the three terminals D5' §8.1 names and false for `Disputed`, which is a terminal of the ride and
  not of the money. A redelivered settlement that finds the ride already there is answered with the
  settled ride and writes no second event — fare-svc's delivery is at least once and R-14's replay
  only covers an identical header key.
- **AL-16 is derived, not stored** (`IBookingEligibility`). reputation-svc (C033) owns
  `cancellations_continuous` and its fence says counters live there and nowhere else; it does not
  exist yet, so ride-svc counts the run of `CancelledByRiderAfterAccept` at the head of the
  passenger's own history. That is not a second copy — the rides *are* the facts the counter would
  be computed from. When C033 lands, the interface takes a gRPC implementation and the query is
  deleted.
- **The accept is one conditional UPDATE and nothing else.** No advisory lock, no pre-flight
  `SELECT`, no application-side ordering — the database picks the winner, and there is deliberately
  no `offered_driver_id` predicate on it (that would turn a concurrent double-accept into two 403s).
- **`Completed` is not terminal.** `ux_rides_open_passenger` exempts it (C004 note (b)); the state
  machine does not. `complete` moves through it to `PaymentPending` in one transaction so the ride
  never rests there — and a passenger cannot book again until fare-svc settles, because
  `PaymentPending` is *not* exempt.
- **Idempotency is two independent keys.** The header is the kernel's (`rides.command_log`, R-14);
  `(passenger_id, client_request_id)` is `ux_rides_idem` (R-18) and survives a client that
  regenerated its header key.
- **`clientRequestId` may be a ULID.** ADD §11.13 has the apps generate them; the column is `UUID`.

## Configuration

`Ride:InternalApiKey` **unset means `/v1/internal/rides/**` is not mapped at all** — dispatch-svc
places no offers and fare-svc settles nothing, so every completed ride stalls in `PaymentPending`.
D3' §0 puts the internal family on mTLS and the gateway refuses the prefix at the edge (C008); the
shared secret is the interim until C042.

`Ride:OfferTtl` (15 s, D5' §3.5) is used when dispatch sends no `ttlSeconds`.

| Setting | Default | Where it comes from |
|---|---|---|
| `CancellationPenaltyMinor` | 5 000 | D-05 / US-6A.9 — Rs 50 |
| `NoShowPenaltyMinor` | 10 000 | §11.12 — "Rs 100 (configurable)" |
| `CancellationDisableThreshold` | 3 | US-6A.10b / AL-16 |
| `RiderNoShowGrace` | 5 min | D5' §7 |
| `PaymentPendingGrace` | 10 min | R-16 "at-payment", ADD §13.3.1 |
| `ArrivalGrace` | 15 min | **no spec pins it** — argued at its declaration |
| `TimersEnabled` / `TimerInterval` / `TimerBatchSize` / `TimerLease` | on / 1 s / 100 / 30 s | R-04's "≤1 s after expiry" |
| `VehicleStatusEnabled` / `MqttServiceName` | off / `ride` | R-15; off because it is the only part needing a broker |
| `StuckStateMetricsEnabled` | on | R-20 / ADD §13.3.1 |

`Fare:EstimateTokenKey` must be the same value fare-svc signs with, or every booking is a 400.
`Jwt:Issuer` must match what iam-svc signs with; ride-svc holds no signing key.

Redis is **off** (`UseRedis = false`). The `lock:driver-offer:{driverId}` fast path in ADD §11.11 is
dispatch-svc's reservation (D5' §3.6), the authoritative accept here is pure Postgres and so is
every timer — which makes R-04's "the backstop fires independently of any Redis TTL" structural
rather than merely tested: there is no cache in this process to flush.

## Events on `ride.events`

`ride.requested` · `offer.created` · `offer.declined` · `offer.expired` · `ride.accepted` ·
`ride.driver_arrived` · `ride.started` · `ride.completed` · `ride.cancelled` ·
`ride.expired_no_driver` · `ride.no_show_rider` · `ride.no_show_driver` · `ride.disputed` ·
`cancellation.penalty.accrued` · `reputation.driver_cancelled` · `ride.settled`.

D6' §2.2's `eventType` comment is a partial list; §11.12's Events column names six more and they are
used verbatim. **`ride.settled` is the one name no spec prints** and is this service's — raised in
the C032 handoff. Every one of them is keyed by `rideId` on the same topic, so a penalty can never
overtake the cancellation that caused it.

## Schema this service added

`db/migrations/0608` (C022) — `offered_driver_id`, `offered_vehicle_id`, `fare_estimate_minor`,
`fare_surcharge_minor`, `currency`. **C032 added no migration**: `rides.timers` (0605),
`rides.transitions` (0602) and `rides.rides.terminal_at` (0601) were already exactly what the
§11.12 matrix and the R-04 backstop need. `terminal_at` had no writer before this component; it
does now.
