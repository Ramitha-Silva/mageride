# dispatch-svc (C023 ws-dispatch-stub) — Mode C presence and the offer loop

Stack: .NET 10 Minimal API + Dapper over Npgsql + StackExchange.Redis + Confluent.Kafka +
`pocketken.H3`. References `MageRide.Shared` (C002).

**Verify:** `dotnet test backend/src/Dispatch.Api.Tests -c Release`

## What this slice is

Presence plus a single-candidate offer loop: a driver goes online, the nearest available driver
is picked out of the Redis GEO index, one offer is written with a 15 s TTL and a DB backstop, and
`offer.created` is committed to `dispatch.outbox`. Everything here matches
`backend/contracts/dispatch.yaml`, which wins over this file and over the code.

| Endpoint | Spec |
|---|---|
| `POST /v1/standby/online` | D3' dispatch-svc route table, US-6A.1 |
| `POST /v1/standby/offline` | D3' dispatch-svc route table |

There is no third route. The offer loop is driven by `ride.events`, not by HTTP.

**Not here, on purpose.** Weighted scoring, the Driver Level system, the Job Board and its
intents, scheduled rides, Directional Travel (DT-01..DT-08), the D-08 wallet gate, the
`reputation-svc` block gate, `safety.blocked_drivers`, package-size compatibility (P-11) and the
no-show report are **C034/C035/C036**. The US-6A.11 120-second `ExpiredNoDriver` global timeout is
C034's too — no route exists to write that ride state, so this slice stops the cascade at
`Dispatch:MaxOfferRounds` and leaves the ride in `Matching`. The FCM/SignalR push that turns
`offer.created` into a phone buzzing is **C024/C051**; this service's job ends at the committed
outbox row. All are left out rather than stubbed — a gate that always passes reads like a gate
that works.

## Rules that are load-bearing

- **dispatch-svc never writes `rides.state`** (R-01; ADD §11.12 "ride-svc is the sole writer").
  The three moves it drives are commands on `/v1/internal/rides/{id}/matching`, `/offer` and
  `/offer/expire` — C022 added the first two, C023 the third, and all three are micro-change-sets
  against D3'. ADD §11.11's diagram draws dispatch running the `UPDATE` itself; sole-writer wins.
- **The H3 cell is never a distance bound** (R-06, and this component's DoD). `H3Grid` produces
  the *set of Redis keys to read* — a res-5 `gridDisk(2)` spans roughly 40 km — and stops there.
  `CandidateRepository.NarrowAsync` is what decides who is actually near, with
  `ST_DWithin(geo, pickup, radius)` on `dispatch.driver_presence` (ADD §6, D-06). The Redis step
  reads whole cell sets and applies **no** radius, deliberately: a `GEOSEARCH BYRADIUS` there
  would make the mandatory post-filter look optional.
- **R-10 is both mechanisms, and neither is optional.** The Lua
  `SET lock:driver-offer:{driverId} NX PX` is the fast path so a driver app never sees a claimed
  offer; `ux_offers_driver_live` (the partial unique index on `dispatch.offers`) is what survives a
  Redis flush. `ReservationTests` deletes the lock, puts the driver back in the index and proves
  the index still refuses a second live offer.
- **The reservation happens before ride-svc is called, and the backstop is armed with it.** If the
  process dies in between, the sweep finds a timer for an offer the ride never got, is answered
  410, settles the row and frees the driver. The other order would leave a ride `Offered` with
  nothing watching the deadline.
- **The deadline is ride-svc's, everywhere.** `dispatch.offers.expires_at`, the `rides.timers`
  fire time and the Redis `PEXPIRE` are all realigned to the instant `POST /offer` returned,
  because it is ride-svc's `offer_expires_at > now()` that decides an accept. A sweep that runs
  early is answered `409` and reschedules rather than taking the window away.
- **Expiry has two triggers and one implementation.** The durable `rides.timers` row (R-04) is the
  guarantee; the `offer:{rideId}` key expiring (D-07) is an accelerator. Both go through
  `IOfferTimerRepository`'s lease, so they cannot double-fire, and both end in
  `DispatchService.ExpireAsync`.
- **Timers are leased, not marked fired on claim.** One `UPDATE … WHERE id IN (SELECT …
  FOR UPDATE SKIP LOCKED)` pushes `fire_at` out by `Dispatch:TimerLease`. A held row lock would
  deadlock against the expiry's own writes on another connection; an immediate `fired_at` would let
  a worker that died mid-expiry take the ride's only backstop with it.
- **`offer.declined` and `offer.expired` name neither the driver nor the offer.** ride-svc clears
  `offered_driver_id` and `current_offer_id` before building the envelope, so the release is keyed
  by ride and resolved against `dispatch.offers`. Recorded as a contract gap in the C023 handoff.
- **The consumer keeps no dedupe table.** D6' §2.3 is at-least-once and says consumers key on
  `eventId`; every action here is idempotent by construction instead — deterministic
  `Idempotency-Key`s on the ride-svc commands, and conditional `UPDATE`s guarded on the status they
  expect. C034 should revisit if it adds an action that is not naturally idempotent.

## Configuration

`Dispatch:RideServiceInternalKey` must equal ride-svc's `Ride:InternalApiKey`. Unset means every
offer is answered 404 and **no driver is ever asked** — which from the outside looks like "nobody
is online", so the service says so once, loudly, at start-up.

`Dispatch:SearchRadiusM` (default **5000**) is the exact post-filter's radius. **No spec pins
it** — D5' §3.1 writes `ST_DWithin(d.geo, pickup, searchRadius)` and never gives the value. 5 km
sits inside the res-5 ring(2) reach, so the pre-filter stays genuinely coarse.

`Dispatch:OfferTtl` (default 15 s, D5' §3.5/US-6A.3) is what dispatch *asks* for; ride-svc stamps
the deadline. `Dispatch:MaxOfferRounds` (default 8) bounds the cascade.

`Dispatch:ExpiryWorkerEnabled`, `Dispatch:ConsumerEnabled` and
`Dispatch:KeyspaceNotificationsEnabled` each gate one hosted service. `Dispatch:AlgorithmVersion`
is **0** and means "not the D5' §3.3 weighted algorithm" — C034 lands version 1.

**Redis needs `notify-keyspace-events Ex`** for the D-07 accelerator;
`infra/docker-compose.dev.slim.yml` and the TestKit fixture both set it on the server's command
line. `Dispatch:ConfigureKeyspaceNotifications` (default off) makes the service try `CONFIG SET`
itself, which needs an admin connection the kernel deliberately does not open.

`Outbox:*` defaults to `dispatch` / `dispatch_outbox` / `dispatch.events` (set in
`DispatchApplication`, overridable). `CommandLog:Schema` defaults to `dispatch`.

`Jwt:Issuer` must match what iam-svc signs with or every request is a 401.

## Schema this service added

`db/migrations/0710` — `dispatch.command_log`, the third per-service command log (iam 0104,
registry 0307). The file header says why; recorded as a micro-change-set in the C023 handoff.
`migrate-verify.sh` now expects **13** dispatch tables, not 12.

## The one thing that is not dispatch's table

`rides.timers` rows of kind `offer_expiry` are written by this service. The table belongs to
ride-svc by name; the job belongs to dispatch by every spec that names an owner (ADD §6 gives
dispatch-svc "Quartz.NET (scheduled rides **+ offer backstop**)", D5' §3.5 puts the durable
backstop under *Offer TTL & cascade*, and this component's deliverable list says so outright).
Nothing here touches `rides.rides`. If the schemas are ever split across databases the timer has
to move to `dispatch.timers` (migration 0708 already exists for the DT-04 case).
