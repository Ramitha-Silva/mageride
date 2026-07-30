# trip-state-svc (C031)

Stack: .NET 10 Minimal API + Dapper over Npgsql + StackExchange.Redis + Confluent.Kafka + MQTTnet.
References `MageRide.Shared` (C002).

**Verify:** `dotnet test backend/src/TripState.Api.Tests -c Release`

## What this is

The Mode A / Mode B tracking-session lifecycle (D-03, AL-32, R-15, T-04, T-11). A session is a
driver's live tracking window: Start/End Journey, the active-session mutex, the 30-minute idle and
100-metre destination auto-ends, the 5-minute grace restart, both directions of the journey
rating, and — for tracker-equipped vehicles — auto-start and auto-end on ignition with the
dashboard overriding the device. Everything here matches `backend/contracts/trip-state.yaml`,
which wins over this file and over the code.

| Endpoint | Spec |
|---|---|
| `POST /v1/sessions/start` | D3' trip-state-svc, US-5.1, D-03 |
| `POST /v1/sessions/{id}/end` | D3' route table, US-5.2 |
| `POST /v1/sessions/{id}/restart` | D3' route table, US-5.10 |
| `GET /v1/sessions/{vehicleId}/active` | D3' route table (the parameter is a **vehicle** id) |
| `POST /v1/sessions/{id}/rating` · `/driver-rating` | US-18.1, US-18.2, US-8.6 |
| `POST /v1/internal/sessions/{id}/auto-end` | D3' route table, US-5.9 |
| `POST /v1/internal/sessions/ignition` | **not in D3'** — D6' §I-25.3, AL-32 (C031 micro-change-set) |

**R-01, the fence this service exists to hold.** Mode C is a *ride*, not a tracking session, and
ride-svc (C032/C037) is its sole writer. `mode` admits A and B in the contract, in the service and
in `ck_sessions_mode`; a request naming Mode C is refused with a message that says where it
belongs, and a Mode C **vehicle** is refused even when the body claims B — the mode is registry's
fact, not the client's.

**Not here, on purpose.** Position ingest is the hot path's (C038/C039): this service *consumes*
`telemetry.normalized` and writes no telemetry of its own. `trips.position_samples` (0503) is
**persistence-writer-svc's (C040)** — it writes the 1/min Mode A/B history sample there off the same
topic, and computes the ADD §9.2 trip summary into `trips.session_summaries` (0506) when this
service's `session.ended` says a journey is over. Two consequences for this service: nothing here
may write either table, and `session.ended` is now load-bearing beyond the US-5.9 push — a journey
whose end is never published gets no distance and no polyline. The US-5.9 push itself is
notification-svc's (C051); this service emits `session.ended` carrying the reason and the deadline,
which is what that push is built from. Route geometry and stops are spatial's (C005) — `route_id`
is stored and never resolved here, and its FK is still deferred (0501's header).

## Rules that are load-bearing

- **The active-session mutex is `ux_sessions_active_driver`, and Redis is a published fact.** ADD
  §6 describes it as "Redis SETNX **+** Postgres UNIQUE partial index", and only one of the two can
  be the invariant. It is the index: it settles ten concurrent starts with no cooperation, survives
  a cache flush, and cannot be bypassed. So the start does **not** pre-check — a SELECT-then-INSERT
  loses exactly the race the index exists to settle — it inserts and turns `23505` into
  `409 driver-already-live`. `SessionMutexTests` starts ten sessions at once and asserts one.
- **`lock:driver:{driverId}` was already taken, so the session key is `lock:session:{driverId}`.**
  D-03 and ADD §6 both name the former; C028 uses it for registry-svc's published go-live
  selection, written with an unconditional `SET` *before* any session starts. A `SETNX` against it
  would fail every single time and the mutex would refuse every start rather than every second one.
- **A driver who pressed End Journey meant it.** Only an *auto*-ended session is restartable, and
  `EndReasons.IsAutomatic` is that rule in one place. Offering to undo a deliberate End would make
  the button ambiguous; every other reason is the platform deciding on the driver's behalf, which
  is what a grace window exists to let them correct.
- **`restartableUntil` is derived, not stored** (`ended_at + RestartGrace`). Changing the window
  then cannot strand rows minted under the old one.
- **Reporting is not moving.** A parked bus keeps publishing at its standby cadence, and counting
  those fixes as activity would make US-5.3's timer unreachable — the exact failure it exists to
  prevent. So a fix always advances `last_position_*` and advances `last_movement_at` only on
  movement, judged by two independent signals: reported speed ≥ `MovementSpeedMps`, **or**
  displacement ≥ `MovementThresholdM`. A cheap tracker reports no speed; consumer GNSS wanders tens
  of metres while stationary.
- **The timer fires here, not in the app.** US-5.9 has the driver *notified* that the platform
  ended their session, so a backgrounded, crashed or uninstalled driver app must still leave a
  correctly closed session behind. D3' puts the same conclusion in the contract by giving the
  auto-end an internal route.
- **Every sweep close goes through `AutoEndAsync`, not a bulk UPDATE.** Closing a session also
  writes the domain log, the outbox row, the Redis key and the standby cadence hint; a shortcut
  would leave a fleet of half-closed sessions no consumer ever heard about. Only the claim is bulk,
  and the claim transaction is released before the closes — holding it across them would nest two
  transactions on one pooled connection.
- **`state = ACTIVE` is a predicate in the UPDATE, everywhere.** A dashboard End and a fired idle
  timer arrive together often enough to matter; only the one whose UPDATE matched gets a row back
  and emits the event, so a timer can never overwrite `driver_ended` and open a grace window the
  driver did not earn.
- **AL-32 is symmetric, and that is the point.** A dashboard End closes a device-started session
  and records `device.overridden`; an ACC-off leaves a **dashboard**-started session alone, because
  a driver waiting at a depot with the engine off has said what they want. The device is never
  authoritative in either direction.
- **Ignition declines rather than guesses.** A tracker knows its vehicle and nothing else
  (US-3.22: "the mobile app is not needed"), so the driver is resolved as the vehicle's *owner* —
  and when that cannot be done, or the vehicle is not Mode A/B, or not go-live eligible, or its
  owner is already live elsewhere, the report is declined. A session attributed to the wrong driver
  takes their D-03 mutex and blocks the journey they are trying to start themselves.
- **US-5.4's fence is only armed when there is somewhere to arrive at.** The radius is centred on
  "the previous journey's end position", so a vehicle's first journey arms nothing — an empty fence
  would either never fire or fire on the first fix. `end_geo` is copied from the session's last
  position when it closes, which is what produces the centre for the next one.
- **The restart is in place, keeping the id and `started_at`.** US-5.10 calls it a restart; the
  passengers watching hold that id, and a new row would break "the driver's current session" for
  anything that cached the old one. Every condition — closed, closed automatically, closed inside
  the window — is in the `WHERE` clause, and the unique index still decides whether the driver may
  take it back.
- **A last will does not end a session; it starts a clock.** R-15/T-04 give the broker a last will
  and neither says how long a tunnel may last. Ending on the first `offline` would close a journey
  every time a bus passes under a bridge, so `offline_since` is recorded and the sweep decides
  after `OfflineGrace`. Redelivery keeps the *earliest* instant (`COALESCE`), or a retrying broker
  would push the deadline forward forever.
- **A passenger names nobody they are rating.** The session is the only thing that knows who was
  driving. The driver's side is the reverse and does name a passenger — and must be the driver of
  that session. A passenger's side has no participation check on purpose: Mode A is a public bus
  and this service holds no manifest, so "was this person aboard" is a question it cannot answer
  and must not pretend to.
- **One rating per rater per session per direction**, and it is `ux_ratings_once` with
  `ON CONFLICT DO NOTHING` rather than a prior read — two taps on a flaky connection both see "no
  rating yet".
- **Entitlement is checked before the read on `/active`.** "Is this vehicle live" is not a fact a
  stranger gets to ask, and answering `null` for a vehicle they cannot see would still confirm it
  exists.
- **A vehicle the driver may not operate is 404, not 403.** `registry.driver_eligible_vehicles` is
  driver-scoped, so "not yours" and "does not exist" are the same query result; telling them apart
  leaks a stranger's vehicle. An *ineligible* one is `403 vehicle-not-approved` with a detail that
  distinguishes unapproved from E-03 document-suspended — this service reads the raw columns
  precisely so it can map its own errors, as the C028 handoff intends.

## The two-valued state and the three-valued one

`trips.sessions.state` is `ACTIVE | COMPLETED` (server_db_schema.md §4, D4' §4); the contract's
`SessionState` is `ACTIVE | ENDED | AUTO_ENDED`. **Both are honoured without a migration**: the
third value is derived from `end_reason` in `SessionViews.From`, and nowhere else. A stored
`AUTO_ENDED` would duplicate the reason and the two could then contradict each other — which is
worse than the mapping.

`end_reason` is the opposite case and needed 0504: the two documents name the same reason
`geofence` and `destination_geofence`, and each has one value the other lacks. Resolved toward the
contract, which is what a client branches on, plus `admin` (only the DDL had it) and
`ignition_off` (neither had it, and AL-32 requires it).

## Configuration

`TripState:InternalApiKey` **unset means `/v1/internal/sessions/**` is not mapped at all**. The
visible symptom is that ignition auto-sessions stop happening and fired timers have no route — not
that anything unauthenticated can end a journey. It must equal what C043 sends. D3' §0 puts the
internal family on mTLS and the gateway refuses the prefix at the edge (C008); the shared secret is
the interim until C042 lands a mesh.

`TripState:IdleTimeout` (30 min, US-5.3), `GeofenceRadiusM` (100, US-5.4) and `RestartGrace`
(5 min, US-5.10) are fixed by the URD. **`MovementThresholdM` (50), `MovementSpeedMps` (1.4) and
`OfflineGrace` (2 min) are this service's — no spec pins them**, and each is argued at its
declaration.

`TripState:SweepEnabled` / `SweepInterval` (1 min) / `SweepBatchSize` (200) size the durable
timers; `PositionConsumerEnabled` feeds them. Turning the consumer off disables US-5.3 and US-5.4
together — the sweep keeps running and simply never finds a session that has stopped moving. Both
are off in tests, which drive one pass directly.

`TripState:VehicleStatusEnabled` and `PublishCadenceHints` are **off by default**: they are the
only parts that need a broker connection, and a deployment without EMQX reachable should run the
session lifecycle rather than log a connection failure on every transition. `AddMageRideMqtt` is
registered only when one of them is on, so a service that never touches the device plane does not
hold the session-token secret.

`Outbox:*` defaults to `trips` / `trips_outbox` / `trip.events`; `CommandLog:Schema` defaults to
`trips` with no aggregate-id column.

## Schema this service added

`db/migrations/0504`–`0505`; each file's header says why, and both are recorded as
micro-change-sets in the C031 handoff in `build/progress.md`.

| Migration | What | Why |
|---|---|---|
| 0504 | `end_reason` CHECK widened; `last_movement_at`, `last_position_geo/at`, `offline_since`, `started_by`/`ended_by`, `end_geo`; `ux_ratings_once` | US-5.3's timer had no input, US-5.4's fence no centre, AL-32 no way to say who acted, and the 409 on a second rating no index |
| 0505 | `trips.command_log`, `trips.outbox` | the fourth per-service command log; and D6' §2.1 names `trip.events` and this producer without giving it a table |

## Events on `trip.events`

`session.started` · `session.ended` · `session.restarted`. **The topic is D6' §2.1's** — unlike
C028's `registry.events` and C030's `provisioning.events`, nothing new is claimed. What is missing
is the envelope: D6' §2.2 prints no schema for any of the three, so the shapes in
`Sessions/SessionEvents.cs` are this service's and are raised in the handoff.

The aggregate id is the **vehicle**, matching the topic's partition key: an end and the start that
follows it must arrive in that order or fanout-svc removes the vehicle from the live map
immediately after adding it back. Every payload carries `driverId` as well, because the US-5.9 push
is addressed to a person and a consumer holding only the vehicle would have to ask this service on
the hot path who was driving.

`trips.events` (0502) is a **different thing** and is written in the same transaction: the domain
log a support engineer reads six weeks later. An ignition report that changed nothing is recorded
there and published nowhere.
