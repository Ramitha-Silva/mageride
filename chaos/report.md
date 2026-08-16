# C130 — what breaking the replica on purpose found

Twelve drills against the lightweight production replica (single Contabo VPS, 8 vCPU / 24 GB,
eleven containers on one bridge network), each one injecting a failure ADD §14.1 or §15 documents
and comparing what happened with what the document says. `chaos/README.md` is how to run them;
`chaos/out/report.md` is one run's machine-written record. This file is what it means.

**Most of the documented degradation holds.** R-04's durable backstop survives a `FLUSHALL`,
`limited_live` is raised exactly where ADD §12 says, "tracking continues" through a Postgres outage
is true, the transactional outbox loses nothing across a broker outage, R-09's live/replay split
costs the live lane nothing under a flood, and D-08's wallet rule holds on both halves. Those are
real results and they are recorded in §4.

**Five things are not what the documents say**, and three of them are invisible while they are
happening. They are the reason this component exists. **Two more came out of driving one ride all
the way to completion** — something nothing else in this repository does against a deployment (§3).

---

## 1. The five findings that change how this platform is operated

### 1.1 · SOS does not reach anybody, and a Redis outage refuses it outright  — HIGH

Two separate facts, found in the same probe.

**Before any fault is injected**, `POST /v1/sos` on this deployment answers
`200 {"smsStatus":"Failed"}`. `safety.yaml` defines that value as *"every gateway refused; the admin
console has the alert and nobody has been SMSed"*. D-33 requires a **dual** SMS path —
`Sms__SecondaryGateway` is empty in `.env.app.example`, and notification-svc's log transport is
switched off by `Notification__AllowLogTransportOutsideDevelopment=false` — so the two paths D-33
exists to provide are one path that is not connected. The 5-second SLO is met — 0.1–1.8 s across
runs — and what it measures is the time to give up. iam-svc's OTPs are unaffected (they go through
its own dev sender, which `Sms__AllowDevSenderOutsideDevelopment=true` permits), which is why every
other suite on this replica passes straight over the top of this.

**And under fault it is refused rather than degraded.** With Redis stopped, `POST /v1/sos` answers
**503 in 54–187 ms**; with Postgres stopped, **500 in 83–125 ms**. It survives a Redpanda outage, an
EMQX outage and a stalled outbox unchanged, at 122–682 ms.

ADD §14.1's degradation table **has no SOS row at all**. There is no documented behaviour for what
the one request with a person on the other end of it should do while the platform is degraded — no
queue, no retry, no store-and-forward. §13.3's `Sos:SloMs = 5000` is a latency budget for the happy
path and says nothing about availability.

> **Owner:** safety-svc / notification-svc configuration (the gateways) and a micro-change-set on
> ADD §14.1 (the missing row). The replica's own SMS configuration is C125's; the absence of a
> degradation rule is a spec gap.

### 1.2 · R-15 is not wired, in any environment  — HIGH

`Dispatch__LastWillEnabled` **appears in no configuration file anywhere in the repository** — not
`infra/env/.env.app.example`, not `.env.replica.example`, not `infra/replica/*.yml`, and not
`infra/k8s/`. `DispatchOptions.LastWillEnabled` defaults to `false`, so **production would deploy
with it off as well**.

The drill measured both halves separately, which is what makes this actionable:

| | Measured |
|---|---|
| **EMQX (broker half)** | 150 sockets dropped without a DISCONNECT → **150 retained `offline` payloads** on `veh/{vehicleId}/status`, median **918 ms** after the socket died. Exactly as R-15 and T-04 describe. |
| **dispatch-svc (platform half)** | **0** `offer_release_grace` timers armed. `VehicleStatusWorker` never subscribes. |

dispatch-svc says so itself, at start-up, in the replica's log right now:

> `Dispatch:LastWillEnabled is off, so R-15's EMQX last will is not consumed. A driver whose session
> drops mid-offer holds it until the 15 s window expires instead of the 00:00:05 grace.`

The cost is wider than the offer. Three more mechanisms take their input from the same
`veh/{vehicleId}/status=offline` fact:

- **DT-04** lists "going offline / EMQX LWT `status=offline`" as one of four paths that clear a
  Directional Travel filter. That path does not exist here, so a driver who loses coverage keeps a
  filter narrowing their candidacy until its Quartz expiry.
- **T-04's** stalled-tracker detection loses the same signal.
- **R-16's four post-accept grace windows** — offline-after-accept 60 s, after-arrive 120 s,
  in-progress 5 min, at-payment 10 min (ADD §11.12) — are ride-svc's response to a driver going
  offline *on an accepted ride*. With nothing consuming the will, a driver who loses coverage
  mid-ride starts no grace at all. **R-16 is therefore recorded as untested, not as passing**: it
  cannot be drilled until 1.2 is closed.

> **Owner:** infra — one line in `.env.app.example` and in the three `infra/k8s/overlays/*`. The
> code is present, correct and tested (`Dispatch.Api.Tests/Integration/LastWillTests.cs`); nothing
> turns it on.

### 1.3 · ADD §14.1's only client-visible degradation signal does not exist  — HIGH

The stream-lag row promises: *"Payload includes `data_age` field; app shows 'updating...'
indicator"*. No such field is on any surface.

- `NearbyResponse` is `{vehicles, asOf, limitedLive}`.
- `VehicleFrame` — the SignalR frame, `MageRide.Shared/Realtime/LiveHubContract.cs` — is
  `{VehicleId, Lat, Lng, Heading, Speed, Type, Mode}`.

Neither carries a sample timestamp, and `asOf` is **when query-svc answered**, not when the position
was taken. So under consumer lag the map renders stale markers with a current clock beside them, and
a client has nothing to compute an age from. The degradation is real and, to the passenger,
invisible: a driver frozen in the road who looks live.

> **Owner:** a micro-change-set — either `VehicleFrame`/`NearbyVehicleResponse` gain the sample
> timestamp they already have in `veh:meta`, or §14.1 drops a promise nothing implements. The data
> is one field away: `LiveVehicleIndex` already reads `sampleTs` and discards it.

### 1.4 · RPO is one backup interval, not five minutes  — HIGH

The DR drill wrote a probe row and booked a ride **after** the backup and **before** the disaster.
Neither survived the restore.

| | ADD §15 | Measured here |
|---|---|---|
| Mechanism | pgBackRest continuous WAL → S3, daily base backup | `pg_dump -Fc` into the replica's own MinIO |
| **RPO** | **5 min** | **everything since the last backup** — on the runbook's nightly `15 2 * * *` schedule, up to **24 hours** |
| **RTO** | **30 min** | **1 m 11 s** (1 m 11 s · 1 m 25 s · 1 m 11 s over three runs) ✅ |

The five minutes in §15 is a property of WAL shipping. There is no WAL archive on this deployment and
no pgBackRest, so no improvement in restore speed moves the number: a snapshot mechanism has no
point-in-time component at all. The replica is documented as a single-point-of-failure stack (§14's
MVP column; the compose file's own header), so this is a gap between §15's table and what every
environment short of production has — but **§15's table does not say which column it applies to**,
and the 5 minutes is what a reader takes away.

The RTO is met comfortably, and the figure does not extrapolate: it is a **1.5–1.8 MB** dump of a
13,000–17,000-row telemetry table, and `pg_restore` is linear in it. ADD §16.4 prices production's
telemetry hypertable at the whole launch write load.

> **Owner:** production infrastructure (pgBackRest on DOKS) or a micro-change-set on §15 stating
> which column its RPO belongs to.

### 1.5 · `infra/replica/restore.sh` could never restore  — HIGH, **fixed here**

The DR drill's first run failed at the drop:

```
ERROR:  DROP DATABASE cannot run inside a transaction block
```

`psql -c "DROP DATABASE IF EXISTS …; CREATE DATABASE …;"` sends both statements as **one** query
string, which the server wraps in an implicit transaction, and `DROP DATABASE` cannot run in one.
The script died there — having already stopped app-services, hot-path, fanout, tcp-adapter and
pgbouncer — and left **the platform down with the database intact**, the worst of both outcomes. Its
step 4, which restarts those services, never ran.

`backup.sh --verify-restore` did not catch it because it restores into a *fresh scratch* database
and therefore only ever runs `CREATE DATABASE` on its own. That script's own header says *"a dump
nobody has restored is not a backup"*; the corollary this found is that **a restore script nobody
has run is not a recovery plan**.

Fixed in `infra/replica/restore.sh` — two `-c` flags instead of one, which psql sends as separate
queries — and the drill then completed end to end. It is recorded here rather than fixed silently
because it is the only defect this component repaired, and because the DoD it blocked ("the DR
restore meets RPO and RTO with measured numbers") could not be met around it.

---

## 2. Findings that are about being able to *see* a failure

Three of the twelve failures produce **no signal an operator would act on**. That is the pattern
running through this report and it is worth stating on its own.

| Failure | What an operator sees while it is happening |
|---|---|
| **Outbox dispatcher wedged** (drill 50) | `/health/ready` 200 · every container healthy · no log line · `mageride_outbox_publish_failures` flat · `mageride_outbox_dispatch_latency` **quiet, not tall** — a histogram only takes an observation when a row *is* dispatched, so `OutboxLag`'s p95 cannot fire on a stopped drain. Every ride booked meanwhile sits in `Requested`. R-20's stuck-state observer is the only thing that eventually notices, after §13.3.1's 60 s. |
| **Redis flushed** (drill 10) | `GET /v1/nearby` answers `200 {limitedLive:false, vehicles:[]}` — Redis is *up*, so `LiveVehicleIndex`'s `RedisException`/`TimeoutException` branches never run. Indistinguishable from a city with no vehicles in it. |
| **app-services partitioned** (drill 63) | The container answers **its own** `/health/ready` with 200 while every socket to Postgres, Redis, Redpanda, EMQX and MinIO is black-holed, and Docker's health state does not change. D7' §5.1 makes readiness the signal an orchestrator routes on; on DOKS this pod keeps taking requests it cannot serve, and for a partition rather than a crash neither the liveness probe nor the endpoint controller will notice. |

Three further gaps of the same kind:

- **`mageride.rides.timers_fired` is declared and incremented by nothing.**
  `grep -rn RideTimersFired backend/src` returns its declaration in `MageRideDiagnostics` and
  nothing else, while **two Grafana panels chart it** — `business-stuck-states.json`
  ("fired/min · {{kind}}") and `money-and-safety.json` (`kind="payment_pending"` timers fired/h).
  Both are permanently empty. The neighbouring gauge is fine: `mageride_rides_timer_backlog` is
  published by ride-svc's `StuckStateObserver` and is what `alerts.infrastructure.yml`'s
  `RideTimerBacklogHigh` fires on — **so the backlog is visible and the drain is not.**
- **A total Redpanda outage produces no publish-failure signal for 15 seconds.**
  `mageride_outbox_publish_failures_total` cannot move until librdkafka gives up on the message, and
  `Kafka:MessageTimeoutMs` is 15 s with `KafkaEventPublisher` awaiting each delivery in turn. So
  `OutboxPublishFailing` (`rate(...) > 0`) has a 15-second floor, and `OutboxDispatchLagHigh` goes
  quiet rather than tall for the reason above. Between them the two rules cover a *slow* outbox and
  not a *stopped* one.
- **Telemetry stays dark for one to several minutes after a broker restart, and nothing reports it.**
  EMQX reported healthy **58–81 s** after `docker compose start` (its healthcheck has a 60-second
  `start_period`), a device could open a socket **25–43 ms** after that, and mqtt-bridge-svc's
  `$share/posGroup/…` subscription came back later still. Measured twice, deliberately, because the
  two numbers differ and the difference is the finding:

  | | Bridge re-subscribed |
  |---|---|
  | `docker compose restart emqx` (broker away ~5 s) | **71 s** |
  | `stop` → probes → `start` (broker away ~100 s) | **not within 4 minutes** |

  `MqttBridgeOptions.ReconnectDelayMin/Max` are **1 s and 60 s** with
  `exponential = min * 2^(attempt-1)` capped at the max (`MqttStreamSession.BackoffFor`), and the
  attempt counter does not reset when the broker becomes reachable — so a failed attempt made while
  EMQX is still starting pushes the next one a full minute out, and a longer outage compounds it.
  Through that whole window every container is healthy, the broker accepts publishes and PUBACKs
  them, and nothing reaches `telemetry.raw`. That is the same silent-loss shape as
  load/report.md's central finding, arrived at from a different direction.

---

## 3. Two defects found by driving one ride all the way through

Drill 70 needs a driver with **one accepted trip today** before D-08's second-trip rule applies at
all — `DailyFeeRepository` counts `tripsToday` as `dispatch.offers` rows with `status = 'ACCEPTED'`,
so an offer that merely went out does not count. Getting there meant driving a ride through
accept → arrive → start → complete against the deployment, which **nothing else in this repository
does**: `load/dispatch.js` cancels pre-acceptance by design, and `load/accept-race.sh` stops at the
accept. Two defects fell out of the four calls beyond it.

### 3.1 · A completed cash ride cannot be settled, and it wedges both parties — MED

`/complete` drives `InProgress → Completed → PaymentPending` and hands off to fare-svc. On this
deployment the hand-off does not land: `POST /v1/fare/pay {rideId, method:"cash"}` answers

> `404 — Ride … has no computed fare yet. It is priced when the ride completes.`

…for a ride that has completed, and `fares.ride_payments` is empty. All six `ride.events` including
`ride.completed` were published and dispatched, so the event left ride-svc; nothing computed a fare
from it. The ride never leaves `PaymentPending`, and **`ux_rides_open_passenger` does not exempt
that state** — a hazard `ride.yaml`'s own `/complete` description names verbatim: *"a passenger who
books a new ride inside that window can wedge the old one"*. Every later booking by that passenger
is `409 active-ride-exists`, and the driver is held by `ux_rides_driver_busy` in the same way. One
completed ride takes an account pair out of service permanently.

### 3.2 · The accept endpoint answers 500 with a stack trace when the driver is busy — HIGH

With the driver still attached to that ride, `POST /v1/rides/{id}/offer/{driverId}/accept` answers:

```
500 internal-error
Npgsql.PostgresException (0x80004005): 23505: duplicate key value violates unique constraint
"ux_rides_driver_busy"
   at MageRide.Ride.Rides.RideService.AcceptOfferAsync(…) in /src/backend/src/Ride.Api/Rides/RideService.cs:line 380
   …
```

The index is doing its job — it is the O2 one-accepted-ride invariant — and what is missing is the
catch. `ride.yaml` documents `409` for the losing side of an accept and `RideService.AcceptOfferAsync`
lets the `PostgresException` escape instead. Two things are wrong with that. **The status code**: a
driver whose app retries, or who is still on a ride the platform could not settle, is told the
server is broken rather than that they are busy — and 500 is the one code a client is expected to
retry into. **And the body**: the reply carries the ORM, the schema and table names, the constraint
name and absolute build paths, to an ordinary client error. That is the same class of disclosure
C127's ASVS review looks for, on a path its suite does not reach.

> **Owner:** ride-svc. One `catch (PostgresException { SqlState: "23505" })` mapping
> `ux_rides_driver_busy` to the documented 409, and a look at why `ride.completed` produces no fare.

---

## 4. What held

Recorded at the same weight as the findings, because a chaos report that only lists failures is not
a measurement.

| Claim | Spec | Measured |
|---|---|---|
| **The offer expiry survives a Redis flush** | R-04 — "the durable backstop fires within 1 s of expiry independently of any Redis TTL" | `FLUSHALL` mid-offer; `rides.timers` fired **760–949 ms** after the deadline across runs, against a **305–631 ms** control with Redis intact. The ride returned to `Matching` and the offer row settled `EXPIRED`. **Both inside the 1 s budget, and the gap between them is D-07's keyspace-notification accelerator — which is exactly what a Redis failure removes.** |
| **query-svc raises `limited_live`** | ADD §14.1 / §12 | Redis stopped → `200 {limitedLive:true}`, `mageride_query_nearby_limited_live_total` advanced, and the flag **cleared itself 196 ms** after Redis returned with no restart. |
| **Tracking continues through a Postgres outage** | ADD §14.1 | `GET /v1/nearby` served 200 from Redis throughout; the GT06 listener kept accepting. Registration and history refused in **~200 ms** — fast, which is what stops a database outage becoming an every-request outage. |
| **The transactional outbox loses nothing** | D6' §2.4 | Broker stopped → booking still committed (**541 ms**, 202), `ride.requested` held undispatched, ride parked in `Requested`. On recovery the outbox drained **842 ms** later and the held ride reached `Offered` **105 ms** after that. |
| **A reconnect storm costs the devices that stayed** | R-09 / ADD §7.5.3 | **1,200 of 1,200** sessions established, 0 refused; the incumbent publisher kept **every one** of its acknowledgements throughout. |
| **A replay flood does not drown live samples** | R-09 / ADD §7.5.1 | **132–137 samples/s** on `veh/+/pos/replay` across four runs; the live lane kept **every** acknowledgement in each. Delivery holds; **latency does not** — the median stays at 22–46 ms and the maximum reached **4.3 s**, so the split protects the samples and not their timeliness, and D-19's 5 s p95 has very little headroom left at 137 msg/s on the replay lane alone. |
| **EMQX publishes the wills** | R-15 / T-04 | 150 sockets dropped without DISCONNECT → **150** retained `offline` payloads, median **811 ms**, max **1,268 ms**. (Nothing consumes them — §1.2.) |
| **D-08's wallet rule, both halves** | D-08 / D5' §2.2 / US-9.1 | Balance zeroed and `wallet:bal` deleted: the **first** trip of the Colombo day was offered *and ridden* — accept → arrive → start → complete, so the accept gate's `402 insufficient-wallet` correctly did not apply to it. The **second** was refused **840 ms** after booking, with `candidate_scores.rejectedBy = wallet_daily_fee` naming the gate. |
| **A booking never depends on the broker** | infra/CLAUDE.md's fence | EMQX stopped → ride requested and offered normally. |
| **A partition heals without intervention** | no ADD row | app-services severed from the network, then reattached → the platform served again **1.5–3.4 s** later with **no restart**, and HAProxy followed the container. What the edge does *during* the partition is **not** stable: it answered `503 in 47 ms` on two runs and produced **no response inside 8 s** on a third. A client can retry the first and can only time out on the second, and ADD §14.1 has no row saying which it should be. |
| **RTO** | ADD §15 — 30 min | **1 m 11 s**, disaster to a passenger booking again — three runs at 1 m 11 s, 1 m 25 s, 1 m 11 s. |

---

## 5. Where the drills could not reach, and what that costs

Stated rather than left implicit, because a drill that could not reach a limit is not evidence the
limit is fine.

- **No Patroni, no second EMQX node, no Redpanda quorum.** ADD §14's MVP column says
  "Single + daily backup", "Single node (accepted risk)" and "Single Redpanda broker (RF=1)"; the
  replica's compose file opens by calling itself "a single-point-of-failure stack by design". Drills
  20, 30 and 40 measure the half of each §14.1 row a single-node stack can answer. §14.1's *user
  impact* column is testable everywhere; its *system behaviour* column is written for production and
  three of its rows cannot be exercised below it.
- **`max_conn_rate = "500/s"` was never approached.** The storm generator reached **~38
  connections/s** — each session is a TLS handshake plus a WebSocket upgrade plus a CONNECT, driven
  from the same eight vCPU as the broker. What the drill measured is what a storm *costs*, and that
  number is worth having on its own: **CONNACK latency was a median 5.4 s and a p95 of 8.4 s**
  during the storm, against **404 ms** at rest. A driver app whose reconnect timeout is under ten
  seconds would give up and re-queue, which is how a storm sustains itself. Where the broker's own
  limit binds is still unmeasured.
- **The per-ASN reconnect guardrail (R-09) cannot be observed here.** Every connection has one
  source address — the same deployment property that makes the gateway's per-caller rate limit a
  per-platform one (load/report.md's finding).
- **One measurement here was the generator's fault, and it is recorded rather than quietly fixed.**
  A run reported "the live lane lost 5 of 40 acknowledgements under the flood" and raised a HIGH
  finding that a replay flood drowns live samples. It did not: the control session closed its socket
  on the heels of its last publish, and five in-flight PUBACKs never landed — a tail-shaped loss the
  drill could not tell from the broker dropping samples. `chaos/k6/replay-flood.js` now stops
  publishing, drains for six seconds, and only then closes; four runs since have kept every
  acknowledgement. **A generator that can manufacture the failure it is looking for is worse than no
  measurement**, and this is the third time a control caught one in this component.

- **The replay throttle never engaged.** `mageride.mqtt.bridge.replay_throttled` and `…replay_shed`
  did not move under a ~137/s flood, with `MqttBridge__ThrottleReplay=true` and
  `ReplaySamplesPerSecond=20` set on the container — because EMQX's `delivery.dropped.queue_full`
  moved by **4,543–4,892** over the same window. The samples never reached the bridge, so its throttle was
  never asked to do anything. That is load/report.md's ~10 msg/s ceiling seen from a second angle,
  and it means **R-09's replay throttle is still untested on this deployment**: it cannot be
  exercised until the ingest ceiling is raised.
- **Only the PostgreSQL row of ADD §15 was drilled.** Redis RDB, Redpanda tiered storage, Vault
  snapshots, etcd and Terraform have no implementation on this deployment to exercise.
- **HAProxy accepts MQTT into a dead broker** for as long as its health check takes to notice. The
  `check` is present on `server emqx emqx:8084`, but the `bind` is accepted by HAProxy itself before
  any backend is chosen, so a driver app gets an established socket and no CONNACK rather than the
  connection failure its reconnect backoff (ADD §7.5.3: "jittered exponential, 1 s–60 s") is written
  against. The 5 s in §14.1 assumes the LB has already taken the dead node out of rotation; nothing
  says how long that is allowed to take.

---

## 6. Findings, with an owner each

| # | Sev | Finding | Owner |
|---|---|---|---|
| 1 | **HIGH** | SOS is recorded and never sent on this deployment (`smsStatus=Failed` with nothing broken); both D-33 gateways absent (§1.1) | replica/production SMS configuration |
| 2 | **HIGH** | SOS is refused 503/500 under a Redis or Postgres outage; ADD §14.1 has **no SOS row** (§1.1) | micro-change-set on §14.1; safety-svc |
| 3 | **HIGH** | R-15 not wired anywhere: `Dispatch__LastWillEnabled` in no env file, no compose file, no k8s overlay; default `false` (§1.2) | infra (one line × four files) |
| 4 | **HIGH** | ADD §14.1's `data_age` field does not exist on any surface; stale maps carry a current `asOf` (§1.3) | micro-change-set: the field, or the promise |
| 5 | **HIGH** | RPO is one backup interval (up to 24 h), not §15's 5 min — no WAL archive, no pgBackRest (§1.4) | production infra, or §15's table |
| 6 | **HIGH** | `infra/replica/restore.sh` could never restore (`DROP DATABASE` in an implicit transaction) — **fixed in this component** (§1.5) | done; `backup.sh --verify-restore` should exercise the real path |
| 7 | **HIGH** | `POST /…/offer/{driverId}/accept` answers **500 with a stack trace, the constraint name and build paths** when `ux_rides_driver_busy` is violated; the contract documents 409 (§3.2) | ride-svc |
| 8 | MED | A flushed Redis is not an unreachable one, and the platform cannot tell a caller which it is | query-svc |
| 9 | MED | `mageride.rides.timers_fired` declared, incremented nowhere; two Grafana panels permanently empty | ride-svc / dispatch-svc |
| 10 | MED | `POST /v1/rides/request` refused outright during a Redis outage — §14.1 promises only a stale map | micro-change-set on §14.1 |
| 11 | MED | A wedged outbox dispatcher is invisible to every liveness signal; neither outbox alert can fire on it | observability (export the undispatched count) |
| 12 | MED | A stopped broker produces no publish-failure signal for 15 s (`Kafka:MessageTimeoutMs`) | observability / KafkaOptions |
| 13 | MED | mqtt-bridge-svc's re-subscription after a broker restart is unbounded by anything observable (71 s to > 4 min); telemetry dark, all containers healthy | hot-path (MQTTnet reconnect policy) |
| 14 | MED | A partitioned app-services reports itself READY; D7' §5.1 makes that the routing signal | MageRide.Shared health checks |
| 15 | MED | HAProxy accepts MQTT connections into a dead broker; the app's reconnect backoff never starts | infra (haproxy.replica.cfg) |
| 16 | MED | R-09's replay throttle is untested — the flood is discarded at EMQX before the bridge sees it | blocked on load/report.md's ingest ceiling |
| 17 | MED | A completed cash ride cannot be settled (`404 no computed fare yet`) and wedges its passenger *and* driver permanently (§3.1) | fare-svc / ride-svc |
| 18 | MED | CONNACK latency reaches a **5.4 s median / 8.4 s p95** under a storm the broker's own limiter never sees (~38/s against 500/s) — long enough for a client timeout to re-queue the connection | infra sizing; R-09's client backoff |
| — | — | **R-16's four post-accept grace windows could not be drilled at all** — the signal that starts them is finding #3's | blocked on #3 |

**Nothing here changed a service, a spec, a contract or a migration.** The one exception is
operational and is finding #6: `infra/replica/restore.sh`'s two-statement `psql -c`, without which
the DR drill — and the component's definition of done — could not run at all.
