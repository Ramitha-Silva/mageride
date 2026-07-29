# dispatch-svc (C023 ws-dispatch-stub, C034 core) — Mode C presence and the offer loop

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

There is still no third route. The offer loop is driven by `ride.events`, `telemetry.normalized`
and `veh/+/status`, not by HTTP.

**Not here, on purpose.** Directional Travel (DT-01..DT-08) is **C036**; scheduled rides, the Job
Board and its intents, the Driver Level *engine*, `dispatch.no_show_events` and the Rs 50
cancellation-penalty records are **C035**; the FCM/SignalR push that turns `offer.created` into a
phone buzzing is **C024/C051**. The Driver Level is *read* here (it is a scoring term) and written
by reputation-svc, which C033's fence makes its sole writer. All are left out rather than stubbed —
a gate that always passes reads like a gate that works.

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

Each of `ExpiryWorkerEnabled`, `DispatchTimerWorkerEnabled`, `ConsumerEnabled`,
`PositionConsumerEnabled`, `KeyspaceNotificationsEnabled`, `LastWillEnabled`,
`ReputationGateEnabled` and `WalletGateEnabled` gates one thing. `LastWillEnabled` is **off** by
default because it is the only part of this service that needs a broker; everything else is on.

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
**`0712`** (C034) — `dispatch.offers.released_at` and the widened `ux_offers_driver_live`. Neither
adds a table, so `migrate-verify.sh` still expects **13** dispatch tables. Both file headers argue
the change; both are micro-change-sets in the C034 handoff.

## The one thing that is not dispatch's table

`rides.timers` rows of kind `offer_expiry` are written by this service. The table belongs to
ride-svc by name; the job belongs to dispatch by every spec that names an owner (ADD §6 gives
dispatch-svc "Quartz.NET (scheduled rides **+ offer backstop**)", D5' §3.5 puts the durable
backstop under *Offer TTL & cascade*). Nothing here touches `rides.rides`. If the schemas are ever
split across databases the timer has to move to `dispatch.timers`, which since 0711 has the
`ride_id` and `payload` columns it would need.
