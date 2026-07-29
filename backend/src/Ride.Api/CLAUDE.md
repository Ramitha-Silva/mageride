# ride-svc (C022 happy path, C023 Δ, C032 core, C037 proxy + package) — Mode C ride aggregate

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
| `POST /v1/internal/rides/scheduled` | **Δ C035** — see below |
| `POST /v1/rides/{id}/package/pickup-otp` · `/delivery-otp` | **Δ C037** — P-07, ADD §11.16 |
| `POST /v1/rides/{id}/package/proof-photo` | **Δ C037** — P-10 |
| `POST /v1/rides/{id}/cod-collected` | **Δ C037** — P-08 |
| `POST /v1/location-requests` · `GET /{id}` · `/confirm` · `/decline` | **Δ C037** — P-02, P-13, §11.15 |
| `POST /v1/internal/location-requests/{id}/confirm` · `/decline` | **Δ C037** — AL-45's web path |

**Not here, on purpose.** `/dispute` is **C049/C050** (it opens a support ticket, which is
support-svc's). `GET /v1/rides/history` is **C048**. Both are left unmapped rather than stubbed.

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
- **A scheduled ride is booked elsewhere and materialised here** (Δ C035). `scheduledAt` on
  `POST /v1/rides/request` is still refused: advance bookings live in `dispatch.scheduled_rides`
  and `POST /v1/rides/schedule` on dispatch-svc is what creates one (AL-36). At T-30 min dispatch
  calls `POST /v1/internal/rides/scheduled`, which is the fourth command of the same family and
  exists for the same reason as the other three — `dispatch.offers.ride_id` has a foreign key onto
  `rides.rides`, so the offer cannot exist before the ride, and R-01 says dispatch may not create
  it. It is **idempotent because the scheduled-ride id is the `clientRequestId`**: `ux_rides_idem`
  turns a retried sweep into a replay rather than a second booking. It takes no `fareEstimateToken`
  — a quote from when the passenger booked is not the price of a ride 30 minutes from now (D5'
  §1.4) — so the ride carries no `fare_estimate_minor` and fare-svc meters it. The booked pickup
  time is not echoed onto the ride either: `rides.rides` has no `scheduled_at` column and
  `dispatch.scheduled_rides.pickup_time` is where it lives. Both are C035 handoff notes.
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
- **ride-svc owns five of the eight `rides.timers` kinds.** `arrival_grace`, `no_show`,
  `payment_pending`, `offline_grace` and — Δ C037 — `cod_uncollected`. `offer_expiry` is
  dispatch-svc's (ADD §6 gives it "Quartz.NET (scheduled rides **+ offer backstop**)", and C023
  built it). The remaining two are armed by **nobody**, each for its own reason:
  `location_request_expiry` cannot be a row at all and `otp_attempt_window` has no duration in any
  spec — both argued at `RideTimerKinds`. Every query in `RideTimerRepository` is scoped by kind,
  which is what lets two services share one table with no coordination protocol.
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

## Proxy booking and package delivery (Δ C037)

- **One machine, three kinds.** ADD Appendix B.2 invariant 6 is honoured literally: proxy and
  package add **no states**. The pickup OTP takes the same `Accepted|DriverArrived → InProgress`
  edge `start` does and the delivery OTP the same `InProgress → Completed → PaymentPending` pair
  `complete` does. A gate decides *whether* the ride may move, never *where* to.
- **Both events at every gate.** `package.picked_up` co-fires with `ride.started` and
  `package.delivered` with `ride.completed`. Spelling a package's completion only the new way would
  leave dispatch-svc — which releases the driver on `ride.completed` — holding a ghost-busy driver.
- **`passenger_id` is the booking account on all three kinds.** D4' annotates `booker_id`
  "= passenger unless proxy", but the column is `NOT NULL REFERENCES iam.users(id)` and P-03's whole
  point is that a proxy rider may have no account — so that reading is unsatisfiable for the case it
  was written for. R-18's idempotency key, AL-16, `ux_rides_open_passenger` and the money all belong
  to the account that booked; `rider_id` names the rider when there is one to name.
- **A correct OTP never spends an attempt, and neither does a malformed one.** The gate is two
  guarded `UPDATE`s — one that matches the digest and moves the ride, one that charges the budget
  and applies only when the digest does *not* match — so no attempt can be counted twice and none
  can be counted for a code that was never a guess. The attempt that **exhausts** the budget raises
  `package.otp_locked`; the next one is `423`. A wrong code is committed, deliberately: rolling it
  back would make the budget unenforceable.
- **The delivery code is minted at pickup, not read back.** D5' §11 has both codes generated at
  booking and ADD §11.16 sends the delivery one to the recipient at *pickup* — and a plaintext the
  server did not keep cannot be sent an hour later. A code exists from booking (so
  `ck_rides_package_complete` holds at `INSERT`) and the code actually sent is minted at the moment
  of sending, replacing its digest in the same statement that takes the pickup gate. It lives in the
  clear for one hop instead of for the whole booking.
- **Photo proof completes the delivery.** ADD §11.16 draws the photograph and the delivery OTP as
  alternatives into `Completed`; a route that only filed the picture would leave the parcel
  delivered and the ride running. The file is written **before** the transaction, so a ride that
  moved leaves an orphan file rather than a completion with no proof behind it.
- **`cod-collected` is the settlement, and that is not an R-05 exception.** The three gateway
  terminals are states fare-svc *observes*; cash in a driver's hand is observable by nobody, and
  D5' §6 draws `PaymentPending --> CashOnDeliveryCollected` as an edge of the ride machine. It emits
  P-08's `payment.cod_collected` **and** the ordinary `ride.settled`, so every terminal reaches a
  consumer in one payload shape.
- **P-14 is a matrix row, not a code path.** `cod_uncollected` is armed at the *pickup* of a COD
  parcel (the clock is about money in transit, and the delivery may itself be what never happens),
  survives every lifecycle move like `offline_grace`, and is retired by the terminal the driver's
  tap produces. The tap and the clock race; whichever lands first leaves the other nothing to do.
- **A decline transmits nothing, and the code has no way to.** The route takes no body, the
  statement has no `resolved_geo` in its `SET` list, and the event payload's `geo` is filled from a
  column that is NULL by construction. P-02's fence is three properties of the code rather than one
  reviewer's care.
- **`RiderNotRegistered` is live, not terminal.** ADD §11.15 ends the round-trip there; AL-45 is
  later and wins — the rider is SMSed a `pickup_confirm` link, answers through public-bff, and the
  request runs down the same 300 s clock. US-8.19's booker fallback is what happens if nobody
  answers, not the only path.
- **The 300 s deadline is the request row, because it cannot be a timer.** ADD §11.15 asks for
  `rides.timers kind='location_request_expiry'`, but that table's `ride_id` is `NOT NULL` with a
  foreign key onto `rides.rides` and the request is issued *before* the ride — which is why
  `rides.location_requests.ride_id` is nullable in the first place. `issued_at + ttl_seconds` is the
  durable deadline, swept over `ix_location_requests_due` in the same worker pass.
- **The P-12 limit is Postgres, not Redis.** ride-svc holds no Redis connection (`UseRedis = false`,
  which is what makes R-04's "independently of any Redis TTL" structural). `ix_location_requests_booker`
  exists in migration 0606 for exactly this count, and counting inside the transaction that inserts
  the next row means a request that rolled back never spent a token. It is checked **before** the
  iam-svc lookup, so a booker out of requests cannot keep using the registration oracle for free.
- **The lookup is a call, not a join.** ride-svc reads `iam.users` directly for
  `counterpartyPhone` — the same read-only cross-context read `DriverSummaryRepository` makes — but
  "is this number registered" is a *registration oracle*, and iam-svc answers it behind
  `iam.phone_lookups`, which records who asked. An outage is `503`: guessing "unregistered" SMSes a
  stranger and guessing "registered" pushes into the void.
- **AL-48 and P-03 conflict in exactly one cell, and P-03 wins.** A proxy ride whose rider is
  unregistered has no number to hand the driver. The field is absent — never the booker's, which
  P-05 forbids outright.

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

`Ride:PhoneHashKey` and `Ride:OtpPepper` are **required outside Development** and resolved during
`RideApplication.Build`, so a deployment that forgets one is a failed start rather than a 500 on
somebody's booking. Neither is rotatable in place: a new phone key partitions the existing digests
rather than re-keying them, and a new OTP pepper makes every package booked before the change
undeliverable by code (photo proof is what an in-flight delivery falls back to).

`Ride:IamBaseUrl` **unset means proxy booking and the whole `/v1/location-requests` family answer
`503 dependency-unavailable`** — the routes stay mapped, because a null object that answered
"unregistered" would SMS a stranger every time. `Ride:IamInternalApiKey` must equal iam-svc's
`Auth:InternalApiKey` or every lookup is a 404.

| Setting | Default | Where it comes from |
|---|---|---|
| `MaxOtpAttempts` | 5 | P-07 — "max 5 attempts each → admin queue" |
| `LocationRequestTtl` | 5 min | P-02 / ADD §11.15; the contract pins `ttl` at `const: 300` |
| `LocationRequestsPerHour` / `PerDay` | 5 / 30 | P-12 |
| `CodUncollectedGrace` | 24 h | P-14 / D5' §8.3 |
| `ProofPhotoRoot` | *(temp dir)* | **not object storage** — D-36's bucket, when a client exists |
| `ProofPhotoMaxBytes` | 8 MiB | **no spec pins it** — argued at its declaration |
| `IamTimeout` | 3 s | it sits in front of a booker tapping a button |

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

**Δ C037 adds eight.** `package.picked_up` · `package.delivered` · `package.otp_locked` ·
`payment.cod_collected` · `location.request.issued` · `.confirmed` · `.declined` · `.expired`.
ADD §11.16 names the two package moves and P-08 names the COD one; `package.otp_locked` is coined
here for the admin queue and raised in the handoff. The four `location.request.*` events are keyed
by **`requestId`, not by a ride** — the round-trip happens before a ride exists, which is the point
of it — and ride under `ride.events` because this service has one outbox and D6' §2.1 gives it one
topic. A consumer keyed on rides ignores them by `eventType`, which dispatch-svc's handler already
does for everything it does not recognise.

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

`db/migrations/0609` (C037) — `recipient_name` / `recipient_phone` on `rides.rides` plus
`ck_rides_package_recipient`, and `ix_location_requests_due` on `rides.location_requests`. Both are
micro-change-sets and the file header argues each. The recipient's number is stored **in the clear**,
unlike `rider_phone_hash`: P-03 hashes the unregistered proxy rider because nothing ever has to dial
them, and AL-21 must SMS the recipient while AL-33 must let the driver ring them. Everything else
C037 needed — the two OTP hashes, the attempt counters, `rides.location_requests`,
`rides.proof_artifacts`, `safety.location_request_audit` — was already landed by C004/C005.
