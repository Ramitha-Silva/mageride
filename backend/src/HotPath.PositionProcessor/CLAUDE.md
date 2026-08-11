# position-processor-svc (C024 skeleton → C039 production form) — telemetry.raw to the live indexes

Stack: .NET 10 + Confluent.Kafka + StackExchange.Redis. References `MageRide.Shared` (C002).
No database: the Timescale write path is persistence-writer-svc's (C040), which ADD §9.5 batches
through `COPY` precisely so the hot path never holds a database connection.

**Verify:** `dotnet test backend/src/HotPath.Tests -c Release --filter Category=PositionProcessor`

## What this service is

Consume `telemetry.raw`, decode the device payload, **refuse what cannot be true** (D-18/T-07) and
what is publishing too fast (D-17), discard replays on the `seq` watermark, and write the live state
the map and the dispatcher are made of — then republish the normalised sample onto
`telemetry.normalized`. Contracts: `backend/contracts/realtime/mqtt-topics.md` §2.1/§4/§5,
D5' §5.3/§13.1, D6' §2.2, ADD §7.4, §9.4, §12.6.

| Key | Type | Why | Writer |
|---|---|---|---|
| `geo:live` | GEO | Every active vehicle's last position (ADD §9.4) | this |
| `veh:meta:{vehicleId}` | HASH | Type, mode, cell, and the last accepted fix the D-18 filter measures against | this |
| `cell:{h3index}` | STREAM | The res-7 fan-out buffer fanout-svc reads (ADD §7.4 steps 1–3) | this |
| `veh:seq:{vehicleId}` | STRING | The R-17/T-05 replay watermark | this |
| `rate:pos-ingest:{vehicleId}:{window}` | STRING | D-17's second-line window | this |
| `geo:drivers:available:{type}:{res5cell}` | GEO | R-08 candidate index — *position* half | this |
| `driver:availability:{driverId}` | HASH | R-08 presence — *position* half (`lastSeen`, `cell`, the 60 s TTL) | this |
| `driver:availability:{driverId}` | HASH | R-08 presence — *phase* half (`state`, the tier, creation and deletion) | **dispatch-svc** |
| `veh:driver:{vehicleId}` | STRING | The reverse binding this service resolves a driver through | **dispatch-svc** |

## The order of the pipeline, and why it is that order

decode → well-formed → **D-17 rate** → read last accepted → **D-18/T-07 plausibility** → **R-08
pool** → `seq` watermark + live indexes → `telemetry.normalized`.

- **The rate check is before the anti-spoof filter and after the parse.** It counts what a vehicle
  actually published, so it has to see samples the filter would reject — a spoofer flooding the
  platform is the case D-17 exists for. After the parse, because a payload that is not a position is
  not this vehicle publishing positions too fast.
- **The anti-spoof filter is before the watermark.** A refused sample must not advance `veh:seq`:
  the watermark means "the newest sample we accepted", and letting a rejected one move it would make
  every genuine sample behind it look like a replay — one spoofed frame would take a vehicle off the
  map until its `seq` caught up. It must also not become the `veh:meta` position the *next* sample is
  measured against, or a spoofer could walk a vehicle across the island one refused jump at a time.
  `PositionGateTests` asserts both.

## Rules that are load-bearing

- **Anti-spoof thresholds are per vehicle type, from config — never hardcoded constants** (fence 1).
  `PositionProcessor:MaxSpeedKph:{type}` is seeded with ADD §12.6's table and bound from
  configuration, so retuning a tier after a month of false positives is a setting and not a build.
  Nothing compares against a literal at the comparison site.
- **Samples with accuracy worse than 200 m are discarded, not smoothed** (fence 2). Nothing here
  clamps, averages or corrects. A 500 m error circle is not a worse position, it is a different
  building; a Kalman pass over the *accepted* track is C040's if it is ever wanted.
- **`MinStepInterval` is a clamp, not a skip.** An implied speed over a very short gap is the fix's
  error circle rather than motion, and most trackers stamp `sampleTs` to the whole second — so a
  burst arrives with no gap at all. Judging at the floor keeps a teleport catchable; *skipping* would
  hand a spoofer the gate by publishing two samples with one timestamp.
- **A backlog skips the step gates and nothing else.** `stream=replay` samples are a vehicle's own
  history arriving late, so implied speed, the monotonic clock and the R-08 heartbeat do not apply
  to them; accuracy and satellite count still do, because a bad fix was bad when it was captured.
  T-05's `seq` watermark is what filters a replay. **An unstamped record is treated as live** —
  reading it as a backlog would silently switch the gates off for it.
- **The monotonic GNSS clock is hardware only** (D5' §13.1's "hardware **additionally**"). A
  handset's clock is the user's to set and Android will move it mid-track; `seq` is what orders a
  handset's samples (R-17).
- **The fan-out grid is res 7 and the dispatch grid is res 5, and both are constants.** R-06
  corrected an earlier "res-8 + ring(1)" claim that is still in circulation. A client — or a
  service — computing cells at another resolution joins or writes keys nothing reads, and the
  symptom is an empty map rather than an error, so every side takes its resolution from
  `MageRide.Shared.Geo.GeoCells`.
- **The topic's vehicle wins over the payload's.** EMQX bound the topic to the device's credential;
  the payload is self-asserted. A sample whose `vehicleId` disagrees with its topic is rebound and
  logged. Trusting the payload would undo the ACL.
- **`seq <= watermark` is discarded, and the watermark advance is atomic.** Ordering per vehicle is
  already guaranteed (`telemetry.raw` is keyed by vehicleId, so one consumer owns a vehicle) — the
  Lua compare-and-set exists because that guarantee lapses for seconds during a group rebalance, and
  a lost update there would let a replayed sample overwrite a live one.
- **`seq` has SECOND resolution, so one sample per vehicle per second is a ceiling.** tcp-adapter sets
  `seq = CapturedAt.ToUnixTimeMilliseconds()` and all four families of D6' §4.1 stamp to the whole
  second, so every seq ends in `000`. **Two genuinely distinct fixes captured inside one second carry
  the same seq, and the watermark cannot tell the second one from a replay — it is discarded, counted
  `replayed`, and does not even become the position the next sample is measured against.**
  **This is a genuine gap, not a documented ceiling.** AL-12's fastest *scheduled* cadence is 1 call/s
  (the near-pickup burst), which lands every sample in its own second and is safe — but that same
  entry is "bounded by the 5 msg/s/vehicle broker ceiling (§12.4)", and the D-17 table below sets this
  service's own line at 10 msg/s over 10 s. So both rate limits deliberately tolerate a vehicle
  publishing several samples a second, while the watermark keeps one per second and counts the rest as
  replays. Nothing sizes the ceilings to what the storage path can actually keep. **Read it together
  with the `MinStepInterval` rule below, which on its own reads as though a same-second burst
  survives.** It does not, and the order above is why: the plausibility gate genuinely does judge such
  a burst rather than skipping it — that rule is accurate — but the seq watermark runs *after* the
  gate, so everything the gate carefully let through except the second's first sample is discarded
  immediately afterwards. Closing it means giving seq resolution the timestamp does not have, and
  `TcpAdapter/CLAUDE.md` records why the device frame counter was rejected — a spec question, not an
  edit here.
- **A bad sample is dropped, never retried.** Redelivering an unparseable payload produces the same
  nothing forever, and one misbehaving handset must not stall the partition every other vehicle in
  its shard shares. Drops are counted by reason (`undecodable`, `malformed`, `rate_limited`,
  `implausible`, `replayed`) and plausibility refusals again by *check* and tier, which is the number
  an operator retuning ADD §12.6's table actually needs.
- **`AutoOffsetReset.Latest`, alone on the platform** (with dispatch-svc's position consumer).
  dispatch-svc reads `ride.events` from the earliest offset because a booking committed while it was
  down still has to be dispatched. A position is not like that: this is a *current-state* index, and
  replaying ten minutes of stale samples would push every one of them to passengers as current,
  oldest last. History is Timescale's (T-06). `PositionProcessor:StartFromEarliest` reverses it for a
  deliberate replay — the test harness is the only thing that sets it.
- **Redis is the live index, not the record.** Losing the whole keyspace costs the live map until
  each vehicle's next sample, costs the candidate pool one round, and costs history nothing. Which is
  why every failure in the R-08 path is swallowed and counted rather than propagated.
- **Every gate fails open when switched off, and says so at start-up.** An open anti-spoof gate looks
  exactly like a working one from the outside: positions flow, the map is populated, nothing errors.
  `WarnAboutGatesThatCannotClose` is the whole list.

## R-08, and why two services write one hash

**ADD §9.4 gives `driver:availability:{driverId}` and `geo:drivers:available:*` to
position-processor-svc, and a position sample carries no driver.** The telemetry plane is keyed by
`vehicleId` end to end because EMQX authenticates a *vehicle* (`mqtt-topics.md` §1); the dispatch
plane is keyed by `driverId` because a ride is offered to a person. That is why C024 left the
heartbeat unwritten and C034 landed a version of it in dispatch-svc, where the pair already lives.

C039 resolves it by splitting on **what decides the fact**, not on which key it lives in:

| Fact | Decided by | Written by |
|---|---|---|
| Is this driver in the pool at all, and under which tier | a phase transition — go-online, offer, accept, offline | dispatch-svc |
| Which res-5 cell they are discoverable from | a position | **this service** |
| Whether they are still there (the 60 s TTL, `lastSeen`) | a position | **this service** |
| Recovery when the hash lapsed but the durable row survives | `dispatch.driver_presence` | dispatch-svc |

`veh:driver:{vehicleId}` is the binding that makes the split possible: dispatch-svc writes it at the
one moment the (driver, vehicle) pair is established and deletes it when the driver goes off duty.

**This service tracks; it never declares.** It does not create an availability hash, and it never
adds a driver the hash does not already say is `AVAILABLE`. So the two writers cannot disagree — one
says *who* is in the pool, the other says *where*. A hash resurrected here with one field and no TTL
would read to every later caller as "this driver is online, position unknown", which is why the
reconciliation is one Lua script with the existence check inside it.

**`MetaFields.PoolCell` exists because a GEO set has no TTL and the availability hash does.** When
the hash expires there is nothing left anywhere naming the cell key that still holds the driver, so
the membership would leak for ever. The vehicle's meta hash remembers `{driverId}|{cellKey}` and
outlives the 60 s window; the next sample is what undoes it. That field is not in ADD §9.4's shape —
micro-change-set in the C039 handoff, along with `veh:driver:{vehicleId}` itself.

## The two D-17 lines

| Line | Where | Scope | On breach |
|---|---|---|---|
| **5 msg/s** | EMQX `listeners.*.messages_rate` | per **connection** | publisher paused by the broker |
| **5 msg/s** | mqtt-bridge-svc `PublishRateMonitor` | per **vehicle** | `mqtt.rate_violation`; **nothing dropped** |
| **10 msg/s over 10 s** | this service, `IngestRateGuard` | per **vehicle** | **dropped** + `mqtt.rate_violation` |

The bridge does not drop because a position dropped there is one anti-spoof never gets to look at.
By the time a sample reaches here anti-spoof has looked at it, and a vehicle still doubling the
broker's ceiling is publishing from several sessions under one credential — the case
`mqtt-topics.md` §4 says the first line cannot see. Both lines write the **same** `audit.events`
action, because it is the only one any spec spells for the MQTT plane; `detectedBy` and `line` are
what tell them apart. The debounce keys differ so the first line firing cannot silence the second.

The counter is in Redis, keyed by vehicle: `telemetry.raw` is partitioned by `vehicleId` so one
consumer owns a vehicle and an in-process counter would *almost* work — but the assignment moves on
every rebalance, which is when a flooding device is most likely to be the cause. Fixed windows, not
sliding: a sliding window costs a sorted set per vehicle on the hot path, and the cost of a fixed one
is that a burst straddling a boundary passes at up to twice the rate for one window — which for a
misbehaviour ceiling already at twice the broker's is not worth the write.

## Not here, and named rather than stubbed

- **The per-device fraud score** D5' §13.1 counts refused samples toward. Nothing owns it — the
  `PositionsImplausible{check,vehicle_type}` counter and a warning-level log line are the whole trail
  today. Raised in the C039 handoff.
- **`telemetry.raw.dlq`** (D6' §2.3). `KafkaTopicConsumer` still commits past a poison message and
  stalls the partition on a retryable failure, which is loud rather than lossy.
- **T-08 anti-cloning** (two devices, one IMEI, 24 h ⇒ both quarantined) — provisioning-svc's
  (C030/C043); **T-11 mode eligibility** (tracker online ≤ 30 s AND driver-app online) — dispatch's
  gate; **the Timescale write** (C040); **US-7.17's stale-vehicle removal from `geo:live`** (C041);
  **the LWT consumers** (R-15/T-04).

## Configuration

Every knob is documented at its declaration in `PositionProcessorOptions` and in
`infra/env/.env.app.example`. The ones that are not obvious:

| Setting | Default | Where it comes from |
|---|---|---|
| `MaxSpeedKph:{type}` | ADD §12.6's seven | **`truck`, `mini_truck`, `train` are missing from the spec's table** |
| `DefaultMaxSpeedKph` | 200 | the most permissive value *in* that table — a tier no spec priced is never refused by an invented number |
| `MaxJumpSpeedKph` | 3 600 | ADD §12.6's "jump < 1 km/s" |
| `MaxAccuracyM` | 200 | D-18 |
| `MinStepInterval` | 1 s | **no spec** — D5' §5.2/AL-12's fastest cadence is 1 sample/s |
| `MinSatellites` | 4 | **no spec gives a number**; 4 is what a 3-D fix needs |
| `RequireSatelliteCount` | off | a GT06 frame carries none, and it is the largest tracker family |
| `MaxClockSkewAhead` | 5 min | **no spec** — without it one 2099 frame is a permanent T-07 watermark |
| `RateCeilingPerSecond` × `RateCheckWindow` | 10/s × 10 s | D5' §5.3, `mqtt-topics.md` §4 |
| `DriverAvailabilityTtl` | 60 s | R-08 / ADD §9.4. **Must equal `Dispatch:PresenceTtl`** |
| `SeqWatermarkTtl` | 24 h | **no spec pins it** (C024) |
| `VehicleMetaTtl` | 10 min | also the D-18 filter's horizon — a vehicle silent longer has no step to measure |
| `CellStreamMaxLength` / `CellStreamTtl` | 1 000 / 1 h | a fan-out buffer, not a record |

`PlausibilityEnabled`, `RateCheckEnabled`, `AvailabilityIndexEnabled`, `PublishNormalized`,
`StartFromEarliest` and `Enabled` each gate one thing; all but `StartFromEarliest` are on.
