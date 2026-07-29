# dispatch-svc (C023 ws-dispatch-stub, C034 core, C035 scheduling + levels) — Mode C presence, the offer loop, the Job Board

Stack: .NET 10 Minimal API + Dapper over Npgsql + StackExchange.Redis + Confluent.Kafka +
MQTTnet + a `reputation.v1` gRPC client + `pocketken.H3`. References `MageRide.Shared` (C002).

**Verify:** `dotnet test backend/src/Dispatch.Api.Tests -c Release`

## What this service is

The Mode C dispatcher: presence driven by live position events, candidate generation (H3 res-5
pre-filter then the mandatory exact `ST_DWithin` post-filter), D5' §3.2's hard eligibility gates,
§3.3's versioned weighted scoring, the R-10 reservation pair, the 15 s offer cascade and the
US-6A.11 120-second global timeout. Everything here matches `backend/contracts/dispatch.yaml`,
which wins over this file and over the code.

| Endpoint | Spec |
|---|---|
| `POST /v1/standby/online` | D3' dispatch-svc route table, US-6A.1 |
| `POST /v1/standby/offline` | D3' dispatch-svc route table |
| `POST /v1/rides/schedule` · `DELETE /v1/rides/schedule/{id}` | D3' Δ 2026-06-28 AL-36, US-6A.4 |
| `GET /v1/rides/job-board` · `POST /v1/rides/job-board/{id}/intent` | D-06, US-6A.5 |
| `GET /v1/rides/scheduled/{driverId}` | US-6A.15 |
| `GET /v1/drivers/{id}/level` · `/stats` | US-6A.6, US-6A.14 |
| `POST /v1/internal/drivers/{id}/no-show` | US-6A.7 |
| `PUT /v1/admin/drivers/level-config` | US-14.12 |
| `GET`/`POST /v1/internal/passengers/{id}/penalties[/settle]` | **Δ C035** — D5' §7.1's read and write halves |

The offer loop itself is still driven by `ride.events`, `telemetry.normalized` and `veh/+/status`,
not by HTTP. None of the routes above places an offer.

**Not here, on purpose.** Directional Travel (DT-01..DT-08) and
`PUT /v1/admin/dispatch/directional-config` are **C036**; the FCM/SignalR push that turns
`offer.created` into a phone buzzing is **C024/C051**; the money that settles a penalty is
**fare-svc's** — this service records the debt and exposes it, and writes no ledger entry. The
Driver Level's *level-down-on-reports* rule and the admin appeal restore are **reputation-svc's**
(C033). All are left out rather than stubbed — a gate that always passes reads like a gate that
works.

## Rules that are load-bearing

- **dispatch-svc never writes `rides.state`** (R-01; ADD §11.12 "ride-svc is the sole writer").
  The four moves it drives are commands on `/v1/internal/rides/{id}/matching`, `/offer`,
  `/offer/expire` and `/system-cancel`. ADD §11.11's diagram draws dispatch running the `UPDATE`
  itself; sole-writer wins.
- **The H3 cell is never a distance bound** (R-06). `H3Grid` produces the *set of Redis keys to
  read* — a res-5 `gridDisk(2)` spans roughly 40 km — and stops there. `CandidateRepository`
  is what decides who is near, with `ST_DWithin(geo, pickup, radius)` on
  `dispatch.driver_presence`. The Redis step applies **no** radius, deliberately: a
  `GEOSEARCH BYRADIUS` there would make the mandatory post-filter look optional.
- **The gates run before the score, and both are recorded.** D5' §3.2 is titled "run BEFORE
  scoring". Five gates are predicates on rows the post-filter query already joins (AVAILABLE, the
  tier, GPS freshness, `registry.vehicles.dispatch_state` for E-03, `safety.blocked_drivers` for
  US-12.10); two need another service (`reputation.block_state` over gRPC, the D-08 wallet
  balance); P-11's package-size table is a pure function. **An excluded candidate still gets a
  `candidate_scores` row** with `rejectedBy` naming the gate — R-11's audit is asked "why did this
  driver *not* get the ride" more often than the converse.
- **The score is reproducible from its own row** (R-11, and this component's DoD). The breakdown
  carries the three normalised terms, the three weights that were live, the raw distance and the
  half-life — so a decision survives an admin retuning the weights, and
  `dispatch_algorithm_version` says which formula those numbers belong to. **Version 1 is D5'
  §3.3's weighted algorithm**; version 0 is C023's nearest-only ordering and still means that.
- **R-12 Phase 1 is sequential.** Exactly one offer goes out per round. Walking down the ranked
  list is not a second offer — it is finding the top-1 *reservable* driver.
  `Dispatch:BatchMatchingEnabled` exists so the decision is visible in configuration and is off;
  nothing is wired behind it.
- **R-10 is both mechanisms, and neither is optional.** The Lua
  `SET lock:driver-offer:{driverId} NX PX` is the fast path; `ux_offers_driver_live` is what
  survives a Redis flush. **Migration 0712 gave the index the `released_at IS NULL` predicate it
  was missing** — as printed, an ACCEPTED row stayed live for ever and a driver could be offered
  exactly one ride in their lifetime. `ReturnToPoolAsync` now does all three of ADD §11.12's
  duties: release the offer row, drop the Redis lock, put the driver back in the GEO index.
- **The reservation happens before ride-svc is called, and the backstop is armed with it.** If the
  process dies in between, the sweep finds a timer for an offer the ride never got, is answered
  410, settles the row and frees the driver. The other order would leave a ride `Offered` with
  nothing watching the deadline.
- **The deadline is ride-svc's, everywhere.** `dispatch.offers.expires_at`, the `rides.timers`
  fire time and the Redis `PEXPIRE` are all realigned to the instant `POST /offer` returned.
- **Two clocks, two tables.** `rides.timers.offer_expiry` is R-04's per-offer backstop
  (`OfferExpiryWorker`). `dispatch.timers` holds the two whose subject is the ride or the driver —
  US-6A.11's 120 s cascade deadline and R-15's release grace (`DispatchTimerWorker`). Both lease
  with `FOR UPDATE SKIP LOCKED` and push `fire_at` out rather than marking fired on claim.
- **The global deadline waits for a live offer.** §11.12's `ExpiredNoDriver` cell resolves from
  `Matching` alone, and the one candidate the cascade found should get their 15 seconds; the timer
  reschedules itself to just past the offer's deadline and comes back.
- **An empty round is not the end of the ride.** It leaves the ride in `Matching`, because a driver
  who comes online inside the remaining window is still a candidate. Only the 120 s deadline or
  `Dispatch:MaxOfferRounds` ends it, both through `system-cancel` with `no_driver_found`.
- **A last will starts a clock; it does not release anything.** An `offline` on
  `veh/{vehicleId}/status` arms `offer_release_grace`; an `online` retires it; only the grace
  expiring releases the offer and takes the driver offline. **An ON_RIDE driver is skipped** —
  §11.12 gives ride-svc four graces on an accepted ride, and two services acting on one fact would
  race. Repeating `offline` does not extend the grace (`ux_dispatch_timers_driver_live`).
- **R-15 is the only caller that may revoke an offer inside its window.** `POST /offer/expire`
  gained a `reason` (Δ C034): `deadline` keeps R-04's `offer_expires_at <= now()` guard,
  `driver_unreachable` drops it, because the grace has already proved the driver cannot accept.
  The audit records which (`OFFER_EXPIRED` / `DRIVER_UNREACHABLE`).
- **Presence is kept alive by position events, not by the driver re-posting.** `telemetry.normalized`
  refreshes `last_seen_at` on every sample and moves `geo` only past
  `Dispatch:PositionMoveThresholdM` — the freshness gate is about liveness, and a driver waiting at
  a rank is the candidate this service most wants to keep. The consumer reads from **Latest**
  (a presence index is current state); `ride.events` reads from Earliest (a booking committed while
  the service was down still has to be dispatched).
- **`driver:availability:{driverId}`'s `level` and `walletOk` are written and never read.** They
  are ADD §9.4's documented shape and would be hours stale by the time a candidate build saw them;
  the live values come from reputation-svc and from `wallet:bal:{driverId}`.
- **The reputation gate fails open, and says so on the row.** A reputation outage that excluded
  every driver would take the platform down for a signal that removes a handful of them. An
  unanswered candidate carries `blockState: UNKNOWN` into the audit, which is a different fact from
  `OK`.
- **The wallet gate refuses rather than guesses.** D-08 verbatim: first trip of the Colombo day is
  free and the balance is not consulted; from the second, `walletBalance ≥ dailyFee` or the
  candidate is dropped. `tripsToday` is counted from `dispatch.offers` — this service's own record
  of the same fact D5' §2.2 describes — so the first-trip half survives a billing outage and only
  the balance is ever unconfirmable. A tier with no `billing.plans` row is refused from the second
  trip, because migration 1901 leaves `truck` / `mini_truck` unseeded on purpose.
- **The consumer keeps no dedupe table.** D6' §2.3 is at-least-once; every action is idempotent by
  construction instead — deterministic `Idempotency-Key`s, conditional `UPDATE`s guarded on the
  status they expect, and partial unique indexes for the arming.

## Scheduling, the Job Board and the Driver Level (C035)

- **The booking table is its own timer.** No `dispatch.timers` row is armed for T-30 and none
  could be: that table's `ride_id` has a foreign key onto `rides.rides`, and at T-30 the ride does
  not exist — creating it is the job. `ix_sched_due` (0704) is a partial index on
  `pickup_time WHERE status = 'SCHEDULED'`, which *is* "the next thing to fire", and the status
  column is the claim (`FOR UPDATE SKIP LOCKED`).
- **The sweep materialises and stops; the event dispatches.** `POST /v1/internal/rides/scheduled`
  (Δ C035 on ride-svc) turns the booking into a ride and emits `ride.requested` in its own
  transaction; the ordinary consumer runs the ordinary round. Dispatching from the sweep as well
  would give one ride two racing first rounds. It is idempotent because the **scheduled-ride id is
  the `clientRequestId`** — R-18's `ux_rides_idem` is what makes a retried sweep find the ride its
  first attempt created.
- **A scheduled round is intent-only, and every round of it.** `DispatchService.DispatchAsync`
  looks the booking up by ride id and, when it finds one, the raw candidate set is
  `dispatch.job_board_intents` instead of the H3 ring, at the 30 km board radius instead of the
  5 km on-demand one. A decline re-runs the same branch, so the cascade walks the intent list.
  **A scheduled ride nobody posted intent on is never offered to the open pool** — D5' §3.7 names
  intent-posters and nobody else, and it ends in `ExpiredNoDriver` at the ordinary 120 s deadline.
  See the C035 handoff: that is the spec read literally, and it is a product question.
- **§3.7 is a different rule from §3.3, not a re-weighting of it.** The Job Board dispatch orders
  by distance and breaks ties on the higher level; the weighted score is still computed and stored,
  and `breakdown.ordering = "job-board-proximity"` says which rule produced the `rank`. Folding it
  into the weights would put a distant Level-3 driver ahead of a near Level-2 one, which is exactly
  what "closest … by Level" does not say.
- **Two services write `dispatch.driver_levels`, and one lock keeps it safe.** reputation-svc owns
  every rule driven by its counters (three reports → −1 and the delisting, the appeal restore);
  this service owns the level-*up* from `trips.ratings` — which C033's own CLAUDE.md hands over —
  and the US-6A.7 no-show decrement D3' files here. Both sides take `SELECT … FOR UPDATE` on the
  row first, and this side takes only that row, so it holds a suffix of C033's documented
  block-state → counters → level order and no cycle is possible.
- **The level engine is a recompute, not a queue.** Points are summed from `trips.ratings` and
  compared against `points_awarded_total` (migration 0713); only the delta is applied. Running it
  twice, on two replicas, or after a crash awards nothing twice — which a rating-event consumer
  could only manage if delivery were exactly-once, and D6' §2.3 says it is not. Only 4★ and 5★
  count, at their own star value (D5' §4.2), and only `subject_kind = 'ride'` ratings: this level
  gates Mode C, and a Mode A/B session rating is trip-state-svc's plane.
- **A level once earned is not un-earned by the evidence disappearing.** A rating total that goes
  *down* (a PDPA erasure) moves the watermark and leaves the level alone; §4.2's level-down list is
  three reports and a no-show.
- **The no-show insert is the claim.** `ux_no_show_driver_ride` (0713) is what makes US-6A.7 one
  decrement per missed ride however many times the report arrives — the level row is locked
  *before* the audit insert, so the two are one atomic act. A report with no `rideId` cannot be
  deduplicated and is counted as given; the index is partial for that reason.
- **`level_config.cancellation_penalty_points` is stored and never read.** `dispatch.yaml`'s
  `LevelConfig` names the knob so the admin surface has to round-trip it, but §11.12 gives a driver
  cancellation a reputation hit and a brief delist — both reputation-svc's, both already applied
  there — and no spec gives it a level or a point cost. Applying one off
  `reputation.driver_cancelled` would also be the one write in this service a redelivery could
  double. Raised in the C035 handoff.
- **The penalty ledger is recorded here and settled by fare-svc.** `cancellation.penalty.accrued`
  becomes a `dispatch.cancellation_penalties` row; `GET`/`POST /v1/internal/passengers/{id}/
  penalties[/settle]` are how fare-svc reads the debt before pricing the next trip and marks it
  paid after posting the ledger entries. **No ledger entry is written here** (D-09). All three
  §11.12 bases are recorded, not only the Rs 50 — the table is the passenger's whole outstanding
  balance, which is what US-6A.10b's "clear outstanding balance" is evaluated against — and `basis`
  tells fare-svc that a `full_fare` amount is the *quoted* fare to be re-metered.
- **`ux_penalty_apply` guards nothing; the conditional `UPDATE` does.** D5' §7.1 names
  `UNIQUE(penalty_id, applied_ride_id)`, and because `id` is the primary key that pair is unique by
  construction (0706's own header says so). What actually prevents a double-apply is that the
  settle statement claims `FOR UPDATE SKIP LOCKED` and updates only rows still `OUTSTANDING`, so a
  retry and a later trip both settle nothing and report zero — the same answer as "nothing owed",
  deliberately, because an amount reported twice is an amount charged twice. The accrual side is
  guarded by `ux_penalty_accrual(original_ride_id, basis)` (0713).

## Configuration

`Dispatch:RideServiceInternalKey` must equal ride-svc's `Ride:InternalApiKey`. Unset means every
offer is answered 404 and **no driver is ever asked** — which from the outside looks like "nobody is
online", so the service says so once, loudly, at start-up. It says the same about every other gate
that is switched off; `DispatchApplication.WarnAboutGatesThatCannotClose` is the whole list.

| Setting | Default | Where it comes from |
|---|---|---|
| `OfferTtl` | 15 s | D5' §3.5 / US-6A.3 — what dispatch asks for; ride-svc stamps the deadline |
| `GlobalTimeout` | 120 s | D5' §3.5 / US-6A.11. **ADD §11.12 says 60 s** — see the C034 handoff |
| `MaxOfferRounds` | 8 | §11.12's "N rounds"; **no spec gives N** |
| `SearchRadiusM` | 5 000 | **no spec pins it** — D5' §3.1 writes `searchRadius` and never gives a value |
| `H3Resolution` / `H3RingK` | 5 / 2 | ADD §9.4, D5' §3.1's `ring(1..2)` |
| `PresenceTtl` | 60 s | R-08 / ADD §9.4 — the availability hash's TTL |
| `ExpectedPositionInterval` × `PositionFreshnessFactor` | 60 s × 2 | D5' §3.2's `2×expectedInterval`. **§5.1 and §5.2 give two different intervals for the same driver** — see the handoff |
| `PositionMoveThresholdM` | 25 | D5' §5.2's `Δpos < 25 m` coalescing |
| `AlgorithmVersion` | 1 | R-11. 1 = the D5' §3.3 weighted formula |
| `Weights:Distance/Level/Category` | 0.60 / 0.25 / 0.15 | **no spec gives the values** — argued at their declaration |
| `DistanceHalfLifeM` | 1 000 | **no spec** — the normaliser D5' §3.3's `normalize(1/d)` omits |
| `BatchMatchingEnabled` | off | R-12 Phase 2; nothing is wired behind it |
| `ReputationGrpcAddress` / `ReputationInternalKey` | `http://reputation-svc:5005` | D7' §4.2, C033 |
| `ReputationTimeout` / `ReputationCacheTtl` | 2 s / 5 s | the cache matches C033's own `BlockStatusCacheTtl` |
| `WalletCacheTtl` | 5 s | D-08 / D5' §9.2 |
| `OfferReleaseGrace` | 5 s | R-15. **No spec pins it** — argued against the 15 s offer window |
| `TimerPollInterval` / `TimerBatchSize` / `TimerLease` | 500 ms / 100 / 30 s | R-04's "≤1 s after expiry" |
| `MqttServiceName` | `dispatch` | mints `svc-dispatch`, which `acl.conf` grants `veh/#` |
| `JobBoardRadiusM` | 30 000 | D-06 / `GET /v1/rides/job-board?radius=30km` — the one radius a spec pins |
| `ScheduledLeadTime` | 30 min | D5' §3.7 "goes live 30 min prior" |
| `ScheduledMinimumLead` / `ScheduledMaximumLead` | 30 min / 30 d | **no spec** — the floor is the lead time; the ceiling is how long a tariff version lasts |
| `ScheduledDispatchGrace` | 30 min | **no spec** — how long a booking that will not materialise is retried before it is abandoned |
| `ScheduledPollInterval` / `ScheduledBatchSize` | 30 s / 50 | a booking is placed half an hour early; this is not the R-04 backstop |
| `LevelSweepInterval` / `LevelSweepBatchSize` | 1 min / 200 | 500 points is a hundred five-star rides — a level is a slow fact |
| `InternalApiKey` | *(unset)* | D3' §0's mTLS family, interim shared secret. **Unset ⇒ `/v1/internal/**` is not mapped** |

Each of `ExpiryWorkerEnabled`, `DispatchTimerWorkerEnabled`, `ConsumerEnabled`,
`PositionConsumerEnabled`, `KeyspaceNotificationsEnabled`, `LastWillEnabled`,
`ScheduledWorkerEnabled`, `LevelWorkerEnabled`, `ReputationGateEnabled` and `WalletGateEnabled`
gates one thing. `LastWillEnabled` is **off** by default because it is the only part of this
service that needs a broker; everything else is on.

**Redis needs `notify-keyspace-events Ex`** for the D-07 accelerator;
`infra/docker-compose.dev.slim.yml` and the TestKit fixture both set it on the server's command
line. `Dispatch:ConfigureKeyspaceNotifications` (default off) makes the service try `CONFIG SET`
itself, which needs an admin connection the kernel deliberately does not open.

`Outbox:*` defaults to `dispatch` / `dispatch_outbox` / `dispatch.events` (set in
`DispatchApplication`, overridable). `CommandLog:Schema` defaults to `dispatch`.

`Jwt:Issuer` must match what iam-svc signs with or every request is a 401.

## Schema this service added

`db/migrations/0710` (C023) — `dispatch.command_log`. **`0711`** (C034) — `dispatch.timers` gains
`ride_id`, a nullable `driver_id`, a `payload` and the two live-timer partial unique indexes.
**`0712`** (C034) — `dispatch.offers.released_at` and the widened `ux_offers_driver_live`.
**`0713`** (C035) — `scheduled_rides.payment_method` + `ux_sched_ride`,
`driver_levels.points_awarded_total`, `ux_no_show_driver_ride`, `cancellation_penalties.basis` +
`ux_penalty_accrual`, and the singleton `dispatch.level_config`. 0713 is the only one that adds a
table, so `migrate-verify.sh` now expects **14** dispatch tables, not 13. Every file header argues
its change and every one is a micro-change-set in the C034 or C035 handoff.

## The one thing that is not dispatch's table

`rides.timers` rows of kind `offer_expiry` are written by this service. The table belongs to
ride-svc by name; the job belongs to dispatch by every spec that names an owner (ADD §6 gives
dispatch-svc "Quartz.NET (scheduled rides **+ offer backstop**)", D5' §3.5 puts the durable
backstop under *Offer TTL & cascade*). Nothing here touches `rides.rides`. If the schemas are ever
split across databases the timer has to move to `dispatch.timers`, which since 0711 has the
`ride_id` and `payload` columns it would need.
