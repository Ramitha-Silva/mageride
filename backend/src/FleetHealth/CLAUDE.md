# fleet-health-svc (C044) — per-fleet tracker health and device-down alerting

Stack: .NET 10 Minimal API + Dapper over Npgsql + Confluent.Kafka + MQTTnet. References
`MageRide.Shared` (C002). **No Redis, deliberately** — see the fences below.

**Verify:** `dotnet test backend/src/FleetHealth.Tests -c Release`

`backend/contracts/fleet-health.yaml` is normative for this surface and wins over this file and over
the code.

## What this service is

Four inputs, one rollup, two outputs.

| Input | Transport | What it decides |
|---|---|---|
| `telemetry.normalized` | Redpanda | `last_ping_at` — the clock every state is measured from |
| `veh/{vehicleId}/status` | EMQX (retained LWT) | that a session died (R-15, T-04) |
| `sys/diag/{vehicleId}` | EMQX, QoS 0 | US-3.12's signal, battery and satellite count |
| `provisioning.events` | Redpanda | the IMEI, the fleet, and US-3.8's decommission |

| Output | Spec |
|---|---|
| `GET /v1/fleets/{fleetId}/health` | D3' fleet-svc route table, US-3.13 |
| `fleet.health_alert` on `fleet.events` | US-3.16, D7' §4.2 `Health__OfflinePct`/`WindowMin` |
| `telemetry.device_health` | the rollup itself (migration 1805) |
| `prov.tracker_bindings.last_seen_at` + 3 diagnostics columns | US-3.12; C030 hands them here |

## The three fences, and how each is held structurally

- **This service reads the stream and writes rollups. It does not mutate vehicles or sessions.**
  Nothing here writes `registry.vehicles`, `trips.sessions`, `rides.rides` or a tracker binding's
  `state`. The only column it writes outside `telemetry` is the four diagnostics fields on
  `prov.tracker_bindings` that C030's own CLAUDE.md hands to it — "the columns are read here and
  written there" — and those are pushed by a periodic set-based `UPDATE`, never per sample.
- **A slow or failed health write must not affect the live map or the system of record.** Held by
  construction: `UseRedis = false`, so this service does not register a Redis client and *cannot*
  touch `geo:live`, `veh:meta` or the R-08 pool; and it is its own consumer group on
  `telemetry.normalized`, so falling behind moves nobody else's offsets. When a flush fails the rows
  go back into the accumulator, the offsets stay uncommitted, and the buffer of record is Redpanda's
  seven-day retention (D6' §2.1).
- **The rollup is not a second opinion about `telemetry.fleet_health_5m`.** Migration 1802 owns the
  aggregate and its refresh policy and TimescaleDB's scheduler runs it. This service *reads* it, and
  the only thing it does to it is call its own `refresh_continuous_aggregate` over a closed window.

## Rules that are load-bearing

- **US-3.13's four states are one SQL expression, and there is no C# classifier.**
  `telemetry.device_health_state()` (1805) is called by the dashboard read *and* by the transition
  sweep, so the two cannot disagree — an operator seeing one thing while an alert fires on another is
  the failure that would be reported for years. `TrackerHealthStates` holds the vocabulary and the
  two spellings' mapping and nothing else.
- **The state is derived at read time; `observed_state` is only the sweep's record of a change.** So
  the dashboard is correct to the second between passes and correct with the sweep switched off. A
  materialised state would be stale for a sweep interval after every restart, which is exactly when
  somebody is looking at it.
- **The silence clock is the platform's, not the device's.** `last_ping_at` is the sample's
  `receivedTs`, stamped by mqtt-bridge-svc. A tracker whose GNSS clock is a year fast would otherwise
  be permanently Online and one a year slow permanently Offline — and C039 has a `MaxClockSkewAhead`
  gate because wrong clocks are common. For a T-05 backlog it is the instant the backlog arrived,
  which is when the device was demonstrably reachable.
- **`OfflineAfter` is thirty *minutes*, not D6' §4.5's thirty seconds.** That number is dispatch-svc's
  fallback threshold — a decision about one ride, on the hot path. This is an operator's dashboard,
  where a bus in a tunnel must not read as a device failure.
- **A last will takes a device out of `Online` and no further.** The broker has said the session is
  gone, so it cannot be Online however recent the last ping was; but US-3.13 defines Offline as thirty
  minutes of silence. A fresher ping clears it with no `online` message needed — the C041 rule that the
  will holds an *instant*, not a flag, because a device that crashed and restarted may never send one.
- **`REVOKED` is `Decommissioned`; `QUARANTINED` is not.** US-3.8 revokes credentials and no further
  ingest is possible. T-08 holds a binding pending the US-3.4 admin decision and it may return, so it
  reads as a device that is not reporting rather than one that has been retired.
- **Every write is a set-based statement and every conflicting column takes `GREATEST` or
  `COALESCE`.** Delivery is at-least-once (D6' §2.3) and per-vehicle ordering lapses for seconds
  during a rebalance, so an overtaken flush must not be able to move a clock backwards and a report
  carrying no battery must not erase the battery the last one gave. That is what lets the consumer
  commit its offsets after the flush and nothing else.
- **The ping path collapses to one row per vehicle before it touches the database.** T-10 sizes ingest
  at 20k msg/s and this service sees all of it; the fact has a five-minute grain, so a hundred samples
  from one bus in five seconds carry exactly one fact. `PingAccumulator` is that, and it stalls the
  consumer at its ceiling rather than growing without bound — C040's trade, made the same way.
- **Not the kernel's `KafkaTopicConsumer` for the ping path** (it commits per message, a broker round
  trip per position) **and exactly it for `provisioning.events`** (a handful of events a day, one
  upsert each, and a bind that happened while this service was down still has to be applied).
- **`Latest` on `telemetry.normalized`, like C039 and unlike C040.** This is a current-state rollup:
  replaying a day of samples writes values the next sample overwrites, and `GREATEST` means the replay
  cannot even make a device look fresher than it is. Work with no product.
- **The alert's numerator and denominator come from different places because neither source has
  both.** `reporting` is the closed `fleet_health_5m` bucket's distinct-vehicle count; `expected` is
  the fleet's `ACTIVE` tracker bindings. A vehicle that publishes nothing writes no row for the
  aggregate to count, so a missing tracker is invisible to it by construction. `reporting` is capped
  at `expected`: the aggregate also counts a fleet vehicle publishing from a phone (US-3.6) and one
  whose binding was revoked mid-window, either of which would otherwise make the offline count
  negative.
- **The threshold comparison is `>=`.** The deliverable writes "> 10 %" and the definition of done
  writes "a simulated 10 % fleet outage raises exactly one alert per window"; a strict comparison is
  silent for exactly the case the DoD names.
- **"Exactly one alert per window" is `ux_fleet_health_alert_window`, not a lock.** Every replica
  evaluates every window; the `INSERT … ON CONFLICT DO NOTHING … RETURNING` is the claim, and the
  replica whose insert returns no row writes no outbox event. That also makes a restart's re-evaluation
  free.
- **The alert is edge-triggered.** US-3.16 is "N % of my fleet *goes* offline within a 5-minute
  window" — a transition. Level-triggered, a fleet with a fifth of its vehicles parked for the season
  would alert every window for ever and be muted within a day, which is the same outcome as not
  alerting but harder to notice. `Health:AlertOnCrossingOnly` makes the choice visible.
- **The alert row and its outbox row commit together** (D6' §2.4, R-13). An alert that committed and
  then failed to publish would be an outage nobody was told about, behind a unique index that stops it
  ever being retried.
- **The event is a hand-off, not a notification.** `fleet.health_alert` carries
  `notificationType: FLEET_DEVICES_OFFLINE` and the numbers behind the decision, and no rendered text:
  the trilingual template, the channel and the recipient's preferences are notification-svc's (C051,
  D-26). The same split C036 makes for `directional.expiring`.
- **The endpoint's row filtering is a session GUC and a security-barrier view.** ADD §9.5 item 8 asks
  for the filter to live in the database "without application-side filtering risk" and ADD §7.7.7
  names this service as one of the two that apply it. `SET LOCAL app.fleet_id` (via `set_config(…,
  true)`, because ADD §9.3 puts this behind PgBouncer in transaction mode) plus
  `telemetry.device_health_fleet`, whose predicate is `telemetry.current_fleet_id()` — NULL when unset,
  so a dropped scope produces an empty dashboard and never another organisation's devices.
- **A fleet that is not the caller's is 403 and one that does not exist is 404.** Unusual for this
  platform, where "not yours" and "does not exist" are normally the same answer. It does not apply
  here: a fleet operator's own org id is in their token, so the only path they can construct is their
  own, and reaching the 404 at all takes one of AL-06's two platform roles. D3' declares both codes.
- **The bucket boundary is computed in .NET as well as in SQL, and a test pins the agreement.** The
  worker names the window it evaluates in a `WHERE bucket = …` predicate; if the two ever disagreed the
  predicate would match no row and every fleet would read as a total outage. Flooring on Unix seconds
  is only safe for widths that divide a day, which `TimeBuckets.Start` refuses to do otherwise.
- **No silent caps.** `items` is capped at `Health:MaxItems` and the response says `itemsTruncated`;
  the sweep logs when it filled its batch. The counts always cover the whole fleet.
- **Every switch-off is announced at start-up.** A fleet that is entirely `Offline` and a fleet whose
  ping consumer was never started look identical to an operator, which is exactly the failure this
  service exists to make visible. `WarnAboutWhatIsNotBeingWatched` is the whole list — the same rule
  position-processor-svc, persistence-writer-svc and query-svc are written under.

## Schema this service added

`db/migrations/1805__telemetry_device_health.sql`. Every object is a micro-change-set in the C044
handoff, because no DDL source prints any of it.

| Object | Why |
|---|---|
| `telemetry.device_health` | US-3.13 is a per-device question and a bucketed continuous aggregate is blind to which device contributed — it can say 90 of 100 reported and never which ten did not, nor tell a 6-minute silence from a 6-hour one |
| `telemetry.device_health_state()` | the ladder, as one expression both the read and the sweep call |
| `telemetry.fleet_health_alerts` | "exactly one alert per window" needs an index, not a lock |
| `telemetry.outbox` | `fleet.events` has a producer and a consumer in the specs and no table (same shape as 0309 / 0403) |
| `telemetry.device_health_fleet`, `telemetry.fleet_health_alerts_fleet` | ADD §7.7.7's fleet scoping, in 1804's `*_fleet` convention |

`migrate-verify.sh` now expects **4** telemetry tables and **8** telemetry views, not 1 and 6.

## Contract changes this component made

| Change | Why |
|---|---|
| `backend/contracts/fleet-health.yaml` **(new)** | D3' attributes the operation to fleet-health-svc in the very line that lists it under fleet-svc, and C008 resolves a cluster from the contract that declares an operation — declaring it in `fleet.yaml` routes it to a service that does not implement it. Same split as `POST /v1/fleets/{fleetId}/trackers/bulk` |
| `fleet.yaml` drops `/health` and `TrackerHealth` | moved, with a note in its header so nobody re-adds them from an earlier-dated spec line |
| `counts` / `percentages` / `TrackerState` | the shipped response was two counts and a boolean; US-3.13 asks for four states' counts **and percentages** |
| `window` / `alert` | US-3.16 had no read surface at all — `GET /v1/fleets/{id}/alerts` is the Phase 3 route-deviation family and its `kind` enum has no value for a device outage |
| `thresholds`, `itemsTruncated`, `TrackerHealth.state`/`since`/`batteryMv`/`signalStrength` | a portal should not hardcode US-3.13's five and thirty minutes, and a capped list must not read as a smaller fleet |
| `mqtt-topics.md` §2.4 | the topic tree gave `sys/diag` a name and no payload |
| `EventTopics.FleetEvents` (`fleet.events`, key fleetId) | **not** one of D6' §2.1's six; added to `bootstrap-topics.sh` and `slim-verify.sh` alongside `registry.events`, `provisioning.events` and `reputation.events` |

## Not here, and named rather than stubbed

- **US-3.14** — "a push notification when my tracker has been offline for more than 15 minutes during
  a session that was supposed to be active". The traceability matrix groups it with US-3.12/3.13 on
  this row and the deliverables do not name it, and there is a real reason to leave it: the trigger is
  a *session*, sessions are trip-state-svc's (C031), and that service already auto-ends a session
  `OfflineGrace` (2 min) after a last will and at a 30-minute idle timeout. A fifteen-minute alert
  sits inside both of those clocks, so whoever owns them has to reconcile the three. Raised in the
  handoff.
- **US-3.7's primary + redundant trackers.** `telemetry.device_health` is keyed by *vehicle*, so two
  trackers on one vehicle share one health row and the "promote the redundant after 60 s" rule has
  nowhere to live. Publisher selection is provisioning-svc's `switch-source` (US-3.6) and dispatch's
  authoritative-source choice (T-11); a per-binding health row would need the roster to say which is
  primary, and `prov.tracker_bindings` has no such column.
- **The per-device fraud score** (D5' §13.1) that C039's refusals count toward — still nobody's.
- **`telemetry.normalized.dlq`** — persistence-writer-svc owns that topic's DLQ (C040). This service
  commits past an unreadable payload, which is loud rather than lossy: position-processor already
  dropped the undecodable before republishing, so anything unreadable here is a producer that has
  changed shape.
- **The Dockerfile.** `infra/docker-compose.dev.yml` still expects a combined
  `backend/src/HotPath/Dockerfile` covering bridge + processor + persistence-writer + fleet-health.
  The gateway's `fleet-health-svc` cluster points at that container (`http://hot-path:5000/`), which
  is where D7' §2.1 puts this service — **and it is the first thing in that container with an HTTP
  surface**, so whoever builds the combined host has to expose Kestrel on 5000.
- **Row-level security on the *raw* hypertable.** Compression wins there (C006 decision); the
  `*_fleet` security-barrier views are the mechanism, and this component follows that convention
  rather than revisiting it.

## Configuration

Every knob is documented at its declaration in `FleetHealthOptions` and in
`infra/env/.env.app.example`. The ones that are not obvious:

| Setting | Default | Where it comes from |
|---|---|---|
| `OfflinePct` | 10 | D7' §4.2 `Health__OfflinePct`. Compared with **`>=`** |
| `WindowMin` | 5 | D7' §4.2 `Health__WindowMin`. **Must equal `fleet_health_5m`'s bucket width**; the service says so at start-up |
| `StaleAfter` / `OfflineAfter` | 5 min / 30 min | US-3.13 verbatim. **Not** D6' §4.5's 30 s, which is dispatch's |
| `FlushInterval` | 5 s | **no spec** — a thousandth of the stale window, so no transition is late because of it |
| `MaxBufferedDevices` | 200 000 | **no spec** — twice T-10's tracker population; the ceiling that makes Redpanda the buffer |
| `StartFromEarliest` | off | C039's argument on the same topic: a current-state rollup gains nothing from a replay |
| `DevicePlaneEnabled` | **off** | the only part that needs a broker; the states work without it |
| `MqttServiceName` | `fleet-health` | mints `svc-fleet-health`, which `acl.conf` already grants `veh/#` and `sys/#` |
| `SweepInterval` / `SweepBatchSize` | 1 min / 5 000 | **no spec** — a minute against a five-minute grain; the batch is a bound, not a throttle |
| `BindingSyncInterval` | 5 min | **no spec** — `prov.tracker_bindings` has an `updated_at` trigger, so every synced row is a real write |
| `AlertCheckInterval` | 1 min | **no spec** — deliberately not the window; 1802's policy has a 5-minute `end_offset` |
| `AlertOnCrossingOnly` | on | US-3.16's "goes offline". Off ⇒ level-triggered, still one alert per window |
| `MinFleetSize` | 1 | **no spec** — suppresses nothing by default; the knob makes the tiny-fleet question visible |
| `RefreshAggregateEnabled` | on | off ⇒ correct answers, from a raw-chunk scan |
| `MaxItems` | 5 000 | **no spec**; US-3.2's bulk ceiling, so the largest creatable fleet fits in one answer |

`ConnectionStrings:Postgres` and `Kafka:BootstrapServers` are required. `Jwt:Issuer` must match what
iam-svc signs with or every request is a 401. `Mqtt:SessionTokenSecret` is required **only** when
`Health:DevicePlaneEnabled` is on. There is no `ConnectionStrings:Redis` and there must not be.

`Outbox:*` defaults to `telemetry` / `telemetry_outbox` / `fleet.events` (set in
`FleetHealthApplication`, overridable). There is no `CommandLog:*` and no `telemetry.command_log`:
every route here is a `GET`, so there is no `Idempotency-Key` replay to log.
