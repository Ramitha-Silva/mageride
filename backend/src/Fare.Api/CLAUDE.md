# fare-svc (C049 fare-svc-core + C050 fare-svc-payments)

Stack: .NET 10 Minimal API + Dapper over Npgsql. References `MageRide.Shared` (C002).
**No Redis, no Kafka, no outbox** — see "Rules that are load-bearing" for why each is absent.

**Verify:** `dotnet test backend/src/Fare.Api.Tests -c Release`

`backend/contracts/fare.yaml` is normative for this surface and wins over this file and over the
code.

## What this service is

**What a Mode C ride costs, and how it is paid.** C049 is the price: the `fares.tariffs` rate card,
the peak and night windows, the upfront estimate and the token that binds it, the E-04 Kalman filter
the final distance is measured with, the D-05 cross-trip cancellation settlement, and the
`Initiated` `fares.ride_payments` row a completed ride produces. C050 is everything after
`Initiated`: D-10's persisted state machine and every settlement path.

| Endpoint | Auth | Spec |
|---|---|---|
| `GET /v1/fare/estimate` | Bearer | US-8.9, D5' §1.1/§1.2, AL-19 |
| `POST /v1/fare/calculate` | internal | E-04, D5' §1.2/§7.1 — ride-svc's completion hop |
| `POST /v1/fare/pay` | Bearer | **Δ C050** — D-10, US-8.11, P-04, D-11 |
| `GET /v1/fare/pay/{id}/status` | Bearer | **Δ C050** — US-8.15 |
| `POST /v1/fare/pay/{id}/fallback-cash` | Bearer | **Δ C050** — US-8.15 |
| `POST /v1/fare/pay/scan-driver-qr` | Bearer | **Δ C050** — AL-22 |
| `POST /v1/fare/pay/driver-qr/claim` · `/confirm` · `/dispute` | Bearer | **Δ C050** — AL-47, US-26.1 |
| `POST /v1/fare/pay/onepay/webhook` · `/lankaqr/confirm` | HMAC | **Δ C050** — R-19, D6' §7.1/§7.2 |
| `POST /v1/admin/fare/refund` | finance · admin | **Δ C050** — E-05 |

| Table | Read | Written |
|---|---|---|
| `fares.tariffs` · `peak_windows` | every estimate and every settlement | **admin-bff** (C065) — see below |
| `fares.ride_payments` | the ride's existing fare | **this service** — C049 opens it, C050 drives it |
| `fares.refunds` | how much has already gone back | **this service** — and it *is* the Finance queue |
| `fares.driver_earnings` | — | **this service**, on every R-05 terminal |
| `support.tickets` | — | **support-svc** (C053) — one AL-47 dispute row written here |
| `registry.driver_payouts` | D-11's merchant binding | **registry-svc** — read-only here |
| `fares.command_log` | the kernel's replay | this service |
| `rides.rides` · `transitions` | what to price, and the window to measure | **ride-svc** (R-01) — read-only here |
| `telemetry.positions` | the E-04 track | **the ingest plane** — read-only here |
| `billing.journal_*` | — | **wallet-svc** (D-09) — never touched here |
| `dispatch.cancellation_penalties` | — | **dispatch-svc** — reached over HTTP, never read directly |

## The fences, and how each is held structurally

- **Mode C tiers expose PRICE ONLY before a driver is matched — no ETA, no distance (AL-19).**
  Neither response carries an arrival time and this service computes none: there is no field for
  one, no clock arithmetic anywhere in the pricing path, and the `durationSec` the contract lets a
  caller send is accepted and never read. The D5' §1.1 tariff has no time component at all, so a
  fare that grew one would be a different pricing model rather than a bug.
- **Mode B has no per-trip fare. Mode A has no fare at all.** `bus` and `train` are refused by
  `FareEstimator` before a tariff is looked up, and `fares.tariffs` is seeded with the six Mode C
  passenger tiers and nothing else. Mode B's money is `subscription.*` (Epic 23, C048) and
  `billing.monthly_subscriptions` (C047); neither is reachable from this service.
- **Driver earning posts only once the payment reaches a terminal state (R-05).** Every path that
  closes a payment goes through one method — `PaymentSettlementService` — and the earning is written
  inside the same transaction as the terminal state, so a payment cannot be terminal without its
  earning and no handler can post one early. `Disputed` closes a payment and earns nothing: it is a
  terminal of the *ride* and not of the money.
- **Driver-QR payments have NO gateway callback (AL-47).** They settle by attestation and behave
  like cash. `DriverQrService` contains no ledger call at all, and the test asserts the *absence* of
  a request against a wallet stub that is running and reachable — the fence is proved, not assumed.
- **Gateway-verified `Succeeded` is OnePay-only (D-10).** Only the OnePay route is wired to a
  gateway that can produce one; the fence lives in which secret verifies which route, and LankaQR's
  confirm is the acquirer reporting a transfer that already landed.
- **P-04: cash is always paid by the rider, LankaQR/OnePay always by the booker.** Resolved from the
  method chosen *at payment*, not at booking — so a passenger who booked cash and pays by card moves
  the charge to the booker, which is what the rule is for.

## Rules that are load-bearing

### The payment machine (Δ C050)

- **The machine is data, not control flow.** `PaymentStateMachine` is D5' §8.1's diagram as one
  table; every handler names *what happened* and applies what it is given, and none of them writes a
  state name. `The_machine_is_exactly_the_diagram` compares the table with a hand transcription of
  the spec and fails **both ways**, so an invented transition is as loud as a missing one.
- **Every move is a guarded `UPDATE … WHERE state = @From`.** That is the whole concurrency
  argument: a retrying webhook and a passenger tapping "pay cash" both resolve the same transition,
  the database picks the winner, and the loser gets no row and is answered a conflict rather than
  overwriting a settlement.
- **A settled payment and a wrong-moment one are different refusals.** `409
  payment-already-settled` versus an ordinary conflict — collapsing them would tell a passenger
  their card had been charged when it had not.
- **R-19's idempotency is the UNIQUE index plus a quiet no-op.** A redelivered callback either
  collides on `provider_transaction_id` or finds the payment past the state it was resolving from;
  both answer `200`, because anything else makes the gateway retry for ever. Nothing counts
  deliveries and there is no second dedupe table.
- **A late `Succeeded` after cash is an overpayment, and the ride is not touched.** §11.14 says it
  outright — "UPDATE rides SET state='Disputed' is NOT done". The cash settlement stands, the
  payment becomes `Overpaid`, and a `overpaid_reversal` row lands on the Finance queue.
- **`fares.refunds` *is* the Finance queue.** `ix_refunds_open` (1003) is a partial index over the
  unsettled statuses ordered by `requested_at` and its own comment names it SCR-AP-009. So a refund
  becoming visible to Finance is a row landing there — not an event, and not a screen this service
  owns.
- **A retry is a new row, never a mutation.** `provider_transaction_id` is UNIQUE and must stay
  one-to-one with a gateway call; reusing the row would give two attempts one reference and make
  D-10's chain unreconstructable. The close and the successor are one transaction, because a
  `Retried` row with no successor is a payment nothing can settle.
- **`FellBackToCash` is the cash terminal, not only the fallback.** D5' §8.1 names cash as the
  default method and the payment CHECK has no separate "cash settled" value — `CashSettled` is a
  *ride* state, and ride-svc maps `FellBackToCash` onto it. A ride paid in cash and one that fell
  back reach the same payment state by design; `method` is what tells them apart. Raised as a naming
  observation in the C050 handoff.
- **A refund never un-earns a driver.** The rollup is a read model of what was collected; putting a
  reversal through it would make yesterday's Earnings screen change overnight. Finance reconciles
  against the ledger, which is where the reversal lives.
- **The earning is inside the transaction; the two outward hops are after it.** ride-svc's settle and
  wallet-svc's tip are other services' transactions and cannot be rolled back with ours, so they run
  post-commit and are each idempotent by construction. A failed hop is loud and does **not** fail the
  caller — the passenger has paid, and a 500 would invite a retry that finds the payment terminal.

### The fare (C049)

- **Money never touches a floating-point type.** The distance is the one genuine real number, and
  `FareFormula.MetresOf` converts it to whole metres at the boundary; every arithmetic step
  downstream is `long`. `A_distance_that_is_inexact_in_binary_still_prices_exactly` is the test —
  1.0 + 0.1 + 0.2 km still costs exactly Rs 124.00.
- **One `round` per product, away from zero** (D5' §1.3). In integer arithmetic that is
  `(a * b + half) / divisor`, which is exact. Away from zero rather than banker's because every
  amount is non-negative and a passenger reading "Rs 480" should not need to know which way 0.5
  fell.
- **Peak and night stack additively on the base, and are one product.**
  `round(base * (peak + night) / 100)`, so a trip that is both is base × 35% — never
  base × 1.20 × 1.15, which is Rs 5.40 more on a Rs 180 fare. The seeded windows never overlap, but
  an admin may make them, and the formula answers the same way either way.
- **The window decides *whether*; the tariff decides *how much*.** D5' §1.1 is explicit
  (`tariff.peak_surcharge_pct`), so `fares.peak_windows.multiplier_pct` is **not read**. It is
  seeded to the same 20/15 the tariffs carry, so the two agree today. Raised as a spec gap in the
  C049 handoff.
- **A window may wrap midnight, and the night one does.** Migration 1001 declines to CHECK the
  ordering for exactly this reason. `end < start` is not a bad row — it is 22:00–05:00 — and a naive
  range test makes the night surcharge unreachable rather than merely wrong. Windows are half-open,
  `[start, end)`, or 09:00 would be both peak and not depending on which row was read first.
- **The tariff is resolved at an instant, never "the current one".** The estimate resolves at the
  moment of quoting and the settlement at the moment the ride was **requested**, so a rate published
  while somebody is in the car cannot re-price their journey. That is the whole reason 1001 versions
  the table by `effective_from`, and `A_rate_published_mid_journey_does_not_reprice_the_journey` is
  the test.
- **A vehicle type with no configured tariff is refused, never guessed.** §20 seeds no rate for
  `truck` or `mini_truck` — Epic 20 configures delivery rates before such a vehicle can be booked —
  and the C022 stub's invented numbers for them were the first thing this component deleted.
  `422 route-unavailable` is an answer an admin can act on; an invented rate bills somebody a number
  nobody chose.
- **A ride is priced once, and the guard is an index.** `ux_ride_payments_first_attempt`
  (migration 1006), partial on `attempt_no = 1`. An application-side `SELECT … FOR UPDATE` was tried
  first and does not work: a lock on a row that does not exist yet locks nothing, so six concurrent
  completions all read empty and all insert. Caught by
  `Concurrent_completions_leave_one_payment`. A plain UNIQUE on `ride_id` would have been wrong too
  — D-10's retry chain deliberately puts several attempts on one ride.
- **The distance falls back to the estimate, not to zero** (D5' §1.2's
  `distance_calculation_failed`). A ride whose tracker was silent is charged the number the
  passenger was shown; re-pricing it at a measured 0 km would hand them the first-km charge for a
  journey across the city.
- **Positions before `InProgress` are the drive to the pickup and are not charged.** The travel
  window comes from `rides.transitions`, which is immutable (0602 has no UPDATE path), so it cannot
  drift after the fact. There is no column that holds it — the audit trail is where "when did this
  ride actually start moving" lives.
- **Raw samples, not the continuous aggregate.** `telemetry.positions_1m` is one point per minute —
  what query-svc draws a trip line from — and chaining sixty-second chords across a route with turns
  loses a third of the distance. The fare is charged on this number.
- **E-04 is three rules and they remove different errors.** Rejection drops fixes too uncertain to
  inform anything and single-sample teleports, which add *kilometres*. The filter smooths the
  metre-scale wobble of a moving vehicle. The movement gate refuses to accumulate a step the
  position uncertainty cannot tell from standing still — which is what a vehicle at a red light does
  for ninety seconds at a time, and is where most of the inflation is. Any one alone leaves most of
  it in place.
- **The movement gate reads the measurement accuracy, not the filter's posterior.** The posterior
  shrinks as the filter converges — under a metre after a minute of standing still — so a gate built
  from it reopens for exactly the stationary vehicle it exists to hold shut. The question the gate
  asks is "could these two fixes be the same place?", and that is answered by how well the receiver
  knew where it was.
- **`Fare:Kalman:ProcessNoise` is swept, not argued.** It trades two failures that pull opposite
  ways, and `A_route_with_right_angle_turns_keeps_its_length` is the test that stops it being tuned
  down until the stationary case passes and every real journey quietly under-charges.
- **D-05 settles first and adds what comes back.** dispatch-svc's settle route is idempotent on
  `(penalty_id, applied_ride_id)`, so a retry collects nothing and cannot charge the same Rs 50
  twice — whereas reading the debt, pricing it, and settling afterwards would re-charge it on every
  retry that failed in between. C035 decision (9), followed verbatim.
- **The next trip's driver is a pass-through, not the beneficiary** (AL-16). The passenger pays the
  Rs 50 to whoever drove them this time — it is inside the fare — and the platform moves it from
  that driver's wallet to the driver who was stood up. Net zero for the pass-through, which is what
  makes it safe to add to a fare they collect in cash.
- **The penalty's ledger key is the business fact, spelled exactly.** `penalty_id || ':' || ride_id`
  — D5' §7.1 verbatim, 1101's column comment, and 1005's header. It is a cross-service contract, and
  a well-meaning reformat would silently start paying the penalty twice.
- **The ledger legs are posted after the commit, and that is the safe order.** A wallet posting is
  another service's transaction and cannot be rolled back with ours; each leg is keyed by the
  business fact, so a retry replays rather than double-posts.
- **A dependency being unwell degrades the fare; it does not refuse it.** dispatch-svc answering 500
  means the trip's own fare is still correct and the debt stays outstanding for the next completed
  trip. Refusing here would leave a driver unable to finish a ride because a *different* service was
  down.
- **No Redis.** The tariff table is single-digit rows behind an index, and a cache would cost an
  invalidation protocol with admin-bff whose whole point is that a published rate takes effect.
  `effective_from` versioning exists precisely so nothing has to be invalidated.
- **No Kafka and no outbox.** The event a fare produces is R-05's terminal, and that is published by
  **ride-svc** through `POST /v1/internal/rides/{id}/payment-settled`, which C050 calls. An event
  emitted here would describe a settlement this component does not make. C050 revisits it.
- **Every switch-off is announced at start-up**, and one combination is called out as worse than
  either extreme: `DispatchBaseUrl` set with `WalletBaseUrl` unset collects a penalty into the fare
  and cannot forward it. Set both or neither.

## Schema this service added

| Object | Why |
|---|---|
| `fares.command_log` (1005) | R-14 needs a replay log per bounded context and D4' §5 prints DDL for `rides.command_log` only — the ninth time this has been raised (C020, C021, C030, C033, C034, C045, C046, C047). `billing.command_log` is **not** reused: it is wallet-svc's and its primary key is the bare idempotency key |
| `ux_ride_payments_first_attempt` (1006) | "A ride is priced once" had no index. §9 could not express it as a plain UNIQUE on `ride_id`, because D-10's retry chain puts several attempts on one ride — partial on `attempt_no = 1` is the invariant, exactly |

`migrate-verify.sh` now expects **6** fares tables, not 5, and carries a C049 section: the replay
log's shape, the one-fare-per-ride index proved by rejection *and* by the retry it still admits, the
tariff versioning, the absent delivery rates and the midnight-wrapping night window. The existing
R-19 check gained an explicit `attempt_no = 2` so it still tests the `provider_transaction_id`
UNIQUE rather than the new index.

## Not here, and named rather than stubbed

- **The COD state sync.** `CashOnDelivery` and `CashOnDeliveryCollected` are in the machine and no
  endpoint drives them: ride-svc's `POST /v1/rides/{id}/cod-collected` (C037) settles the *ride*
  directly and never tells fare-svc, so a COD payment row stays `Initiated`. Closing it needs either
  a `ride.events` consumer here or a call from there, and neither is invented. Named in the C050
  handoff.
- **The pushes.** The AL-47 +5-minute nudge, US-8.15's `PAYMENT_CONFIRMED` and §11.14's "Refund
  processed" are notification-svc's (C051/C052), which does not exist and has no outbound queue
  table to write to. `QrNudgeSweeper` identifies who should be nudged and logs it; everything the
  notification needs is on the row it finds.
- **The acquirer's reverse API.** E-05's refund writes the workflow row and posts the ledger leg;
  actually calling OnePay's reverse endpoint and recording `provider_refund_id` is the gateway half,
  and `ux_refunds_provider_ref` is already waiting for it.
- **`PUT /v1/admin/fares/tariffs`.** admin-bff's (C065) — `admin-bff.yaml` declares it there, not in
  `fare.yaml`. What this component owns is the model it writes through: `ITariffRepository.PublishAsync`
  inserts a whole rate card at one `effectiveFrom` and replaces the windows in the same transaction.
- **The routed distance.** ADD §7.6 puts OSRM/Valhalla in Phase 3. Until then an estimate is a
  straight line × `Fare:RouteDetourFactor`, the same method and the same 1.3 query-svc's ETA uses —
  two services must not approximate one road network with two constants. Phase 3 replaces
  `FarePricingService.RouteDistanceKm` and `KalmanTrack` together.
- **The service-area polygon.** `config.operating_cities` (migration 0201) is the real answer;
  `FareEstimator` still carries the C022 bounding box, which catches a caller on the wrong continent
  and no more. Re-raised in the handoff.

## Configuration

Every knob is documented at its declaration in `FareOptions` and `KalmanTrackOptions`.
`Fare:EstimateTokenKey` and `Fare:EstimateTokenTtl` are **not** there — they belong to
`MageRide.Shared.Fares.FareEstimateTokenOptions`, because ride-svc binds the same section to verify
what this service signs. One section, two readers, one key; without it every booking is a
`400 invalid-fare-token`.

| Setting | Default | Where it comes from |
|---|---|---|
| `InternalApiKey` | unset | **unset ⇒ `POST /v1/fare/calculate` is not mapped** and every completed ride stalls in `PaymentPending` |
| `RouteDetourFactor` | 1.3 | **no spec** — an interim for Phase 3 routing; matches `Query:EtaDetourFactor` deliberately |
| `Kalman:ProcessNoise` | 0.05 | **no spec** — swept against the stationary-drift and lost-corner failures |
| `Kalman:MovementGateSigma` · `MaxAccuracyM` · `MaxSpeedMps` | 1.0 · 50 m · 55 m/s | **no spec** — each argued at its declaration |
| `MaxTrackSamples` | 20 000 | **no spec** — a bound, not a working limit |
| `PenaltySettlementEnabled` · `DispatchBaseUrl` · `DispatchInternalApiKey` | on · unset · unset | **unset ⇒ no D-05 penalty is ever collected** |
| `WalletBaseUrl` · `WalletInternalApiKey` | unset | **unset with dispatch set ⇒ the penalty is collected and cannot be paid out.** Set both or neither |
| `InternalTimeout` | 2 s | D6' §8.3's internal hop |
| `RideBaseUrl` · `RideInternalApiKey` | unset | **Δ C050 — unset ⇒ no ride ever leaves `PaymentPending`.** R-05's only hop |
| `OnepaySurchargeBps` | 500 | US-8.11's +5%, in basis points so it stays an integer. OnePay only |
| `OnepayBaseUrl` · `OnepayApiKey` | unset | **unset ⇒ `onepay` answers 503** and the app offers another rail |
| `OnepayWebhookSecret` · `LankaQrWebhookSecret` | unset | **unset ⇒ that callback refuses every delivery.** No accept-unsigned mode |
| `LankaQrMerchantId` · `LankaQrDeepLinkTemplate` | unset | AL-15's "Pay" deep link; unset ⇒ `lankaqr` answers 503. **No spec prints the scheme** |
| `QrNudgeEnabled` · `QrNudgeAfter` · `QrNudgeInterval` | on · 5 min · 1 min | AL-47 / US-26.1. **The sweep runs; the push is C052's** |

`ConnectionStrings:Postgres` and `Jwt:*` are required. `CommandLog:*` defaults to `fares` /
`command_log` with no aggregate-id column (set in `FareApplication`, overridable). There is no
`ConnectionStrings:Redis`, no `Kafka:BootstrapServers` and no `Outbox:*`, and there must not be —
see `FareApplication` for why each is off.
