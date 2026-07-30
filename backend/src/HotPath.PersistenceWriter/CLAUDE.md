# persistence-writer-svc (C040) — telemetry.normalized to the system of record

Stack: .NET 10 + Confluent.Kafka + Npgsql (binary `COPY`) + Dapper. References `MageRide.Shared`
(C002). **No Redis, deliberately** — see the fences below.

**Verify:** `dotnet test backend/src/HotPath.Tests -c Release --filter Category=PersistenceWriter`

## What this service is

The durable write path, off the hot path. Three things, all driven by events:

| Input | Output | Spec |
|---|---|---|
| `telemetry.normalized` | `telemetry.positions`, batched `COPY` (1k rows / 500 ms / partition) | ADD §9.5 item 5, T-06 |
| `telemetry.normalized` | `trips.position_samples`, one row per session per minute | ADD §9.2 |
| `trip.events` → `session.ended` | `trips.session_summaries` — start, end, distance, polyline | ADD §9.2 |
| a row Postgres refuses | `telemetry.normalized.dlq` | D6' §2.3 |

## The two fences, and how each is held structurally

- **Raw high-frequency positions go to `telemetry.positions` only. Never to
  `trips.position_samples`.** §21 and ADD §9.2 both say it. What lands in the operational table is
  one representative fix per session per minute — twelve fixes of a Mode A minute become twelve
  hypertable rows and one operational row, which is what
  `A_minute_of_Mode_A_fixes_becomes_one_operational_sample_and_many_hypertable_rows` asserts.
- **A slow or failed write must not affect the live map — degrade by buffering and alerting.**
  Held by construction rather than by care: this service sets `UseRedis = false`, so it does not
  register a Redis client and *cannot* touch `geo:live`, `veh:meta` or the R-08 pool. It is also its
  own consumer group on `telemetry.normalized`, so falling behind moves nobody else's offsets. The
  buffer of record is **Redpanda** (seven-day retention, D6' §2.1), not this process: when a flush
  fails the offsets stay uncommitted and, at `MaxBufferedRows`, the loop stops consuming entirely —
  an unbounded in-memory queue would turn a database outage into an OOM kill, and the restart would
  replay from the same offset anyway.

## Rules that are load-bearing

- **`COPY` into a temp table, then `INSERT … ON CONFLICT DO NOTHING`.** The ADD asks for `COPY` and
  separately requires replay idempotency on the vehicle's sequence, and `COPY` has no conflict
  handling at all — one duplicate raises and takes the whole batch with it. The two-step satisfies
  both: the binary import moves rows at `COPY` speed, one set-based insert applies the unique index.
- **The conflict target is three columns and that is not a choice.** TimescaleDB rejects a unique
  index that omits a partitioning column, so the specs' `(vehicle_id, seq)` cannot exist and
  `ux_positions_vehicle_seq` is `(vehicle_id, seq, sample_ts)` (C006 note (a)). A re-sent buffered
  sample carries the GNSS timestamp it was captured with, so the tuple still collides — which is the
  case T-05/R-17 exists for.
- **The staging table is created inside the transaction, `ON COMMIT DROP`.** A session-scoped table
  created once per connection is faster and breaks the moment this runs behind PgBouncer in
  transaction mode (ADD §9.3), where consecutive transactions are not promised the same backend. One
  `CREATE TEMP TABLE` per half-second is not worth a correctness footgun.
- **Offsets are committed after the database transaction, never before.** That is the entire
  durability story: a process killed mid-batch has committed nothing, so the batch is redelivered,
  and the two unique indexes make the redelivery a no-op.
  `Killing_the_writer_mid_batch_loses_no_rows_and_duplicates_none` kills a real process mid-backlog
  and asserts every row arrives exactly once.
- **A failing flush retries in place, forever, with backoff.** Nothing is lost to an outage however
  long it lasts. `TelemetryFlushFailures` is the alert; the fence above is what makes it survivable.
- **`received_ts` is written explicitly.** It is `NOT NULL DEFAULT now()` and a `COPY` supplies no
  defaults, so leaving it out would record a replay lag of zero for every row — the one thing the
  column exists to measure.
- **The 1/min row is stamped at its minute boundary, and that is what makes it idempotent.**
  Delivery is at-least-once, so this code sees the same minute again after any rebalance or restart.
  Truncating to the bucket turns "have I written this minute?" into a unique-index question
  (`ux_possample_session_minute`, 0506) needing no per-vehicle memory — which is the state that would
  otherwise reset exactly when the platform is least stable. **The first fix of a minute wins**, not
  the last: decidable on arrival, so a batch straddling a boundary holds no bucket open.
- **A fix captured before its session started is persisted and not attributed.** A vehicle idling at
  the depot before the driver pressed Start Journey is real telemetry; putting it in the journey
  would put the depot in the polyline and in the distance.
- **Both vehicle lookups cache their misses.** Every Mode C vehicle publishes on this topic and none
  has a tracking session (R-01); most vehicles belong to no fleet. Without negative caching the
  write path would issue a query per vehicle per batch for an answer that is reliably "nothing" —
  the per-row database work `COPY` exists to avoid.
- **`fleetId` is denormalised here** (`mqtt-topics.md` §6: "C040 must populate it"), from
  `registry.fleet_vehicles`. A sample that already carries one keeps it — the publisher knew, and
  re-resolving would let a stale cache overwrite a fresh fact. Without this the fleet-scoped view
  (1804) returns nothing to the fleet that owns the vehicle.
- **A poison row is isolated, not guessed at.** A batch that fails on SQLSTATE class 22 (data) or 23
  (integrity) will fail identically on every retry, so it is re-offered row by row to find which
  rows are bad; those go to the DLQ with the reason and the sample attached, and the rest are
  written. **Everything else is transient and is retried** — a dropped connection, a deadlock, a
  full disk, a chunk being compressed. Committing past one of those would silently lose telemetry
  the hypertable is the system of record for.
- **`Earliest`, unlike C039 reading the same topic.** A writer that was down for ten minutes must
  persist the ten minutes it missed; position-processor maintaining a current-state index must not
  replay them. Two consumers, two groups, opposite answers, on purpose.
- **Not the kernel's `KafkaTopicConsumer` for the batch path.** That one commits per message, which
  is right for a ride command and would be a broker round trip per position here. The batching loop
  is local to this service and is the right thing to promote once a second service needs it.
- **The trip summary is computed from full-resolution rows, not from the 1/min samples.** A minute of
  city driving is not a straight line: chaining sixty-second chords across a route with turns in it
  loses a third of the distance or more. The raw rows are indexed for exactly this read
  (`ix_positions_vehicle_ts`; ADD §9.5 item 6 names "trip linestring for trip Y" as a raw-chunk
  query) and are always present when the event arrives — a session ends the same day it started and
  raw chunks live 30 days. The 1/min fallback is for a replayed event and **labels itself**
  `operational` so a reader can see the distance is a lower bound.
- **The summary is bounded by the session's window, not by `trip_id`.** `telemetry.positions.trip_id`
  is set only if the publishing device chose to (`mqtt-topics.md` §2.1), and nothing makes a tracker
  do it — a summary keyed on it would be empty for most fleets.
- **The distance is measured before the line is simplified.** Simplifying first would quietly shorten
  every journey by the tolerance's worth of detail, and the distance is the one number in a summary
  somebody might be paid against.
- **The summary is upserted, because a session can end twice.** US-5.10 restarts an auto-ended
  session in place, keeping its id, so `session.ended` is not once-per-session. A `session.ended`
  whose session is `ACTIVE` again is **committed** (the next end does the work); one whose session
  row is not visible yet is **retried** (a summary lost to that race is a journey with no record).
  Two outcomes that a single "null means no" would have conflated.
- **Every switch-off is announced at start-up.** A writer that is not writing looks exactly like a
  platform with no vehicles on it: ingest flows, the live map works, nothing errors, and the system
  of record quietly has nothing in it. `WarnAboutWhatIsNotBeingWritten` is the whole list.

## Schema this service added

`db/migrations/0506__trips_operational_sampling.sql`. Both objects are micro-change-sets in the C040
handoff.

| Object | Why |
|---|---|
| `ux_possample_session_minute` on `trips.position_samples(session_id, sample_ts)` | 0503 gives the table only a generated-identity PK, so an at-least-once consumer appended a duplicate row on every rebalance |
| `trips.session_summaries` | ADD §9.2 promises a durable "trip summary (start, end, distance, polyline)" and **no DDL source prints a table for it**; §9.5 item 2's continuous aggregates cannot answer it, being bucketed by time and blind to sessions |

`migrate-verify.sh` now expects **7** trips tables, not 6.

## Not here, and named rather than stubbed

- **The continuous aggregates, the compression policy and the retention policy** (ADD §9.5 items
  2–4). All three are declarative and already landed by C006's migrations 1802–1803 — TimescaleDB's
  own scheduler runs them, and a service that duplicated any of it would be a second opinion about
  the same data.
- **`telemetry.raw.dlq`** (D6' §2.3). This service owns `telemetry.normalized.dlq` — its own input
  topic — and nothing else. The raw one is a claim against the kernel's shared
  `KafkaTopicConsumer`, which every consumer on the platform uses, and belongs to whoever changes
  that. See the C040 handoff.
- **The read path over any of this** (C042 query-svc), **fleet-health rollups** (C044), and the
  Dockerfile — `infra/docker-compose.dev.yml` still expects a combined
  `backend/src/HotPath/Dockerfile` covering bridge + processor + persistence-writer + fleet-health.

## Configuration

D7' §4.2 gives this service two settings under a `Timescale` prefix — `Timescale__BatchRows`=1000 and
`Timescale__FlushMs`=500. Both are honoured as aliases; everything is bound under
`PersistenceWriter` so one section holds it all. Micro-change-set in the handoff.

| Setting | Default | Where it comes from |
|---|---|---|
| `BatchRows` | 1 000 | ADD §9.5 item 5, D7' §4.2 `Timescale__BatchRows` |
| `FlushInterval` | 500 ms | ADD §9.5 item 5, D7' §4.2 `Timescale__FlushMs` |
| `MaxBufferedRows` | 10 000 | **no spec** — the ceiling that makes the Redpanda backlog the buffer |
| `RetryDelay` / `MaxRetryDelay` | 250 ms / 10 s | **no spec** |
| `DeadLetterEnabled` | on | D6' §2.3. Off ⇒ a poison row stalls its partition instead: loud rather than lossy |
| `OperationalSamplingEnabled` | on | ADD §9.2 |
| `SamplePeriod` | 1 min | ADD §9.2's "1/min sampled". **Changing it changes the meaning of stored rows** |
| `SessionCacheTtl` | 30 s | **no spec** — bounds how late a new session's first sample can be |
| `FleetCacheTtl` | 10 min | **no spec** — fleet membership is an admin action measured in months |
| `LookupCacheCapacity` | 50 000 | **no spec** |
| `SummariesEnabled` | on | ADD §9.2 |
| `PolylineToleranceM` | 25 | **no spec** — matches D5' §5.2's own `Δpos < 25 m` coalescing threshold |
| `AllowOperationalGeometryFallback` | on | for a summary computed after ADD §9.5 item 4 dropped the raw chunks |

`ConnectionStrings:Postgres` is required. `Kafka:BootstrapServers` is required. There is no
`ConnectionStrings:Redis` and there must not be.
