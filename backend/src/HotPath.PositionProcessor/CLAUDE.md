# position-processor-svc (C024 ws-realtime-pipeline) — telemetry.raw to the live indexes

Stack: .NET 10 + Confluent.Kafka + StackExchange.Redis. References `MageRide.Shared` (C002).
No database: the Timescale write path is persistence-writer-svc's (C040), which ADD §9.5 batches
through `COPY` precisely so the hot path never holds a database connection.

**Verify:** `dotnet test backend/src/HotPath.Tests -c Release`

## What this service is

Consume `telemetry.raw`, decode the device payload, discard replays on the `seq` watermark, and
write the three things the live map is made of — then republish the normalised sample onto
`telemetry.normalized`. Contracts: `backend/contracts/realtime/mqtt-topics.md` §2.1/§5, D6' §2.2,
ADD §7.4, §9.4.

| Key | Type | Why |
|---|---|---|
| `geo:live` | GEO | Every active vehicle's last position (ADD §9.4) |
| `veh:meta:{vehicleId}` | HASH | Type, mode, cell — what a map marker needs without a registry lookup |
| `cell:{h3index}` | STREAM | The res-7 fan-out buffer fanout-svc reads (ADD §7.4 steps 1–3) |
| `veh:seq:{vehicleId}` | STRING | The R-17/T-05 replay watermark |

## Rules that are load-bearing

- **The fan-out grid is res 7, and it is a constant.** R-06 corrected an earlier "res-8 + ring(1)"
  claim that is still in circulation. A client computing cells at another resolution joins groups
  nothing publishes to, and the symptom is an empty map rather than an error, so both sides take
  the resolution from `MageRide.Shared.Geo.GeoCells`.
- **The topic's vehicle wins over the payload's.** EMQX bound the topic to the device's credential;
  the payload is self-asserted. A sample whose `vehicleId` disagrees with its topic is rebound and
  logged. Trusting the payload would undo the ACL.
- **`seq <= watermark` is discarded, and the watermark advance is atomic.** Ordering per vehicle is
  already guaranteed (`telemetry.raw` is keyed by vehicleId, so one consumer owns a vehicle) — the
  Lua compare-and-set exists because that guarantee lapses for seconds during a group rebalance,
  and a lost update there would let a replayed sample overwrite a live one.
- **A bad sample is dropped, never retried.** Redelivering an unparseable payload produces the same
  nothing forever, and one misbehaving handset must not stall the partition every other vehicle in
  its shard shares. Drops are counted by reason (`undecodable`, `malformed`, `replayed`).
- **`AutoOffsetReset.Latest`, alone on the platform.** dispatch-svc reads `ride.events` from the
  earliest offset because a booking committed while it was down still has to be dispatched. A
  position is not like that: this is a *current-state* index, and replaying ten minutes of stale
  samples would push every one of them to passengers as current, oldest last. History is
  Timescale's (T-06). `PositionProcessor:StartFromEarliest` reverses it for a deliberate replay —
  the test harness is the only thing that sets it.
- **Redis is the live index, not the record.** Losing the whole keyspace costs the live map until
  each vehicle's next sample and costs history nothing.

## Not here, and named rather than stubbed

- **The `driver:availability:{driverId}` heartbeat** R-08 gives this service (C039). It is left
  alone rather than half-refreshed: dispatch-svc's GPS-freshness gate reads the durable
  `dispatch.driver_presence` row precisely because nothing refreshes that hash yet (C023 decision
  10). A sample also carries no driverId, so writing it would need a registry lookup this component
  has no business doing on the hot path.
- **Anti-spoof and the Kalman pass** (C039/C040), the **second-line 10 msg/s ceiling** (D-17), the
  **Timescale write** (C040), the **LWT consumers** (R-15/T-04) and **`telemetry.raw.dlq`**
  (D6' §2.3).

## Configuration

`PositionProcessor:ConsumerGroup`, `:SeqWatermarkTtl` (24 h — **no spec pins it**),
`:CellStreamMaxLength` / `:CellStreamTtl` (a fan-out buffer, expired when no vehicle has been in
the cell for an hour), `:PublishNormalized`, `:StartFromEarliest` (leave off), `:Enabled`.
