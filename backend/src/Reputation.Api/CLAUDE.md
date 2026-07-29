# reputation-svc (C033) — counters, block state, anti-collusion

Stack: .NET 10 Minimal API + **gRPC** + Dapper over Npgsql + StackExchange.Redis +
Confluent.Kafka. References `MageRide.Shared` (C002).

**Verify:** `dotnet test backend/src/Reputation.Api.Tests -c Release`

## What this service is

The single home for cancellation, no-show and vehicle-report counters with rolling-window
semantics (D-04), the effective block state dispatch-svc gates every candidate on, the Driver
Level System's level-down and appeal-restore rules (D5' §4.2), and the E-07 anti-collusion
detector. Everything here matches `backend/contracts/reputation.yaml` and
`backend/contracts/proto/reputation.v1.proto`, which win over this file and over the code.

| Surface | Spec |
|---|---|
| gRPC `reputation.v1.Reputation` — `GetBlockStatus`, `GetDriverLevel`, `ReportCancellation`, `ReportNoShow`, `ReportVehicle` | D3' reputation-svc, D-04 |
| `GET /v1/admin/reputation/flags` | D3' route table, E-07 |
| `POST /v1/admin/drivers/{driverId}/level/restore` | D3' route table, US-6A.8 |
| `POST /v1/admin/reputation/flags/{flagId}/resolve` | **Δ C033** — `FraudFlag.status` had no route |
| `GET /v1/admin/reputation/users/{userId}` | **Δ C033** — what an override is applied to |
| `PUT /v1/admin/reputation/users/{userId}/block-state` | **Δ C033** — the manual override deliverable |
| `POST /v1/internal/reputation/observations` | **Δ C033** — the E-07 IP/ASN input; nothing produces it yet |
| consumes `ride.events` | D6' §2.1 |
| produces `reputation.events` — `fraud.suspected`, `reputation.block_state_changed` | **Δ C033** — no topic in §2.1 |

**Not here, on purpose.** `safety.vehicle_reports` and the decision to confirm or dismiss a
report are **safety-svc's** (C044) — this service is *told* a report was CONFIRMED and tallies
it. Rating collection and the level-*up* points (D5' §4.1, `trips.ratings`) belong to whoever
writes ratings; only the level-*down* rules and the appeal restore are here. The Rs 50
settlement is fare-svc's (D5' §7.1). Nothing here cancels a ride or suspends a driver.

## Rules that are load-bearing

- **Counters live here and nowhere else** (the component's first fence). Other services read
  `block_status` over gRPC and keep no tallies. ride-svc's `IBookingEligibility` (C032) is the
  interim this replaces — its own CLAUDE.md says so, and swapping it for a gRPC read is a
  ride-svc change, not this one.
- **This service publishes state; it does not act on it** (the second fence). A flag never
  blocks anybody — ADD §12.6 reserves the auto-suspend for a Tier-2 admin decision — and a block
  state excludes a driver only because *dispatch-svc* reads it (D5' §3.2).
- **The rules decide, not the caller.** `ReputationRules` is a pure function of the counters, the
  clock and the configured thresholds; a caller reports what happened and never what it should
  mean. Same shape as ride-svc's cancellation matrix, for the same reason.
- **Every intake is one transaction**: the `reputation.intake_log` claim, the counter update, the
  block-state upsert, any level decrement, the audit row and the `reputation.outbox` event. A
  counter that moved without the event announcing it is a driver excluded by a fact nobody can
  find (R-13).
- **Counting is exactly-once by ledger, not by convention.** D6' §2.3 is at-least-once and a gRPC
  retry has the same shape; `reputation.intake_log`'s primary key is the claim. Both intake paths
  — the topic and the five D3' RPCs — go through it, so a platform that later wires the gRPC
  reports as well counts each fact once and not twice.
- **The live intake is `ride.events`, not the gRPC reports.** D3' declares
  `ReportCancellation`/`ReportNoShow` with ride-svc as the caller, and ride-svc (C032) calls
  nothing — it publishes, which is also what CLAUDE.md's universal outbox rule requires. Both are
  implemented; only one currently fires.
- **`cancellations_continuous` is a run, not a window.** D5' §7.2 gives it exactly one reset
  condition — any completed ride — and a time-based reset would let a passenger wait out a
  strike. `reports_total` and `no_shows` *are* window-scoped (D-04); `window_reset_at` holds the
  start of the current window.
- **A completed ride produces two facts, one per side.** §7.2 names no role and both runs have to
  reset; the pair is also the E-07 pair-frequency detector's input, which is why both rows carry
  the ride id. The dedupe key therefore carries the subject as well as the event id.
- **A derived state follows its counters; only an event-imposed one is sticky.**
  `BlockReasons.SurvivesRecompute` is the whole rule: §11.12's brief delist has no threshold to
  fall back below, so it survives its time box; `cancellations_disabled` and `reports_delist` are
  derived, and protecting them would mean a completed ride could not re-enable a booking-disabled
  passenger.
- **A time box lapses on read, and the sweep only makes it durable.** `GetBlockStatus` applies
  `expires_at` itself, so a driver whose delist ended is dispatchable the moment they are asked
  about, whether or not `BlockStateExpiryWorker` has run. What the sweep adds is the row, the
  forgiveness of the counter that caused it (a served strike is served) and the
  `reputation.block_state_changed` event.
- **Reinstatement forgives the counters.** `PUT …/block-state` with `OK` clears every counter and
  returns the user to automatic control. Leaving them at three would restore access for exactly
  as long as it took to recompute.
- **One lock order: block state, then counters, then level.** `IBlockStateRepository.LockAsync`
  materialises an `OK` row before locking, because a `SELECT … FOR UPDATE` that matches nothing
  takes no lock — without it two concurrent first facts for one user deadlock against each other.
  The expiry sweep can only discover users through `block_states`, which is what fixes the order.
- **reputation-svc is the sole writer of `dispatch.driver_levels`.** The table is in another
  schema because that is where D4' §6 prints it; every rule that *changes* a level is D5' §4.2's
  and therefore this service's, and D3' puts both `GetDriverLevel` and the appeal restore here. A
  second `reputation.driver_levels` would be two tables for one fact. Raised as a
  micro-change-set in the C033 handoff.
- **`iam.devices` is read and only ever read.** Two accounts on one `device_key` is E-07's
  device-binding cross-check and is not visible anywhere else; the key itself is never published
  on a flag.
- **"Exactly once per detection window" is an index, not a scheduler.**
  `ux_fraud_flags_window(kind, subject_id, related_id, window_key) NULLS NOT DISTINCT` is what
  bounds the queue, so the detector's interval is a latency choice and running it more often
  raises no more flags.
- **gRPC needs a port of its own.** Cleartext HTTP has no ALPN, so Kestrel cannot negotiate
  HTTP/1.1 and HTTP/2 on one socket — an endpoint serving the admin routes answers a gRPC preface
  with `GOAWAY HTTP_1_1_REQUIRED`. This is the one service that binds its own listeners.
- **The gRPC service is `AllowAnonymous` and idempotency-exempt.** The caller is a service with no
  bearer, and a gRPC call is an HTTP POST that cannot carry `Idempotency-Key` — the kernel would
  answer `400`, which reaches the client as an unreadable "Bad gRPC response". The RPCs carry
  their own dedupe key instead.

## Configuration

`Reputation:InternalApiKey` **unset means `/v1/internal/reputation/**` is not mapped and the gRPC
service accepts any in-cluster caller** — tolerable on one dev host and nowhere else, so it is
logged loudly. D3' §0 puts both on mTLS and the gateway refuses the internal prefix at the edge
(C008); the shared secret is the interim until C042.

`Reputation:ConsumerEnabled` off means **nothing counts anything** — no cancellation is tallied,
no threshold is reached, every block status answers OK forever. Also logged loudly, because from
the outside the gate works, it just always opens.

| Setting | Default | Where it comes from |
|---|---|---|
| `CounterWindow` | 30 d | D-04 rolling reset; the only window any spec gives (E-07) |
| `CancellationDisableThreshold` | 3 | US-6A.10b / AL-16 / D5' §7.2 |
| `ReportDelistThreshold` | 3 | US-12.6 / D5' §4.2 |
| `CancellationWarnThreshold` / `ReportWarnThreshold` / `NoShowWarnThreshold` | 2 / 2 / 3 | **no spec produces WARN** — one short of the block |
| `ReportDelistDuration` | 7 d | D5' §4.2 says "temporary"; **no number** |
| `DriverCancelDelistDuration` | 30 min | §11.12 says "brief"; **no number** |
| `BookingDisableCooldown` | 24 h | AL-16's "configurable cooldown"; **no number** |
| `LevelUpThreshold` | 500 | D5' §4.2 |
| `BlockStatusCacheTtl` | 5 s | matches D-08's wallet gate on the same hot path |
| `GrpcListenPort` / `HttpListenPort` | 5005 / 5000 | D7' §4.2; 5000 is `gateway-routes.json`'s cluster address. 0 = ephemeral |
| `Collusion.PairRideThreshold` / `PairWindow` | 8 / 30 d | E-07's "> N rides / 30 d"; **no N** |
| `Collusion.DeviceSharingThreshold` | 2 | AL-08 binds one install to one session per app |
| `Collusion.NetworkClusterThreshold` / `NetworkWindow` | 4 / 7 d | loosest of the three — shared NAT is normal here |
| `Collusion.DetectionWindow` | 1 d | the `window_key` bucket; a persisting pattern re-raises daily |
| `NetworkObservationRetention` | 90 d | PDPA (E-06) |
| `ConsumerEnabled` / `ExpiryWorkerEnabled` / `DetectorEnabled` | on | each gates one hosted service |

`Jwt:Issuer` must match what iam-svc signs with; this service holds no signing key.

## Events on `reputation.events`

`fraud.suspected` · `reputation.block_state_changed`. Both keyed by **userId** — a block state is
a fact about a person, and only the user key keeps two consequences for one person in order.
**Neither the topic nor either name is in D6' §2.1**; see the C033 handoff.

## Schema this service added

`db/migrations/0803` — `reputation.intake_log`, `reputation.outbox`, `reputation.command_log`.
`0804` — block-state provenance (`source`, `reason`, `set_by`) and the fraud-flag lifecycle
(`status`, `window_key`, `subject_type`, the resolution columns, `ux_fraud_flags_window`).
`0805` — `reputation.network_observations`. Every one is argued in its file header and recorded
as a micro-change-set; `migrate-verify.sh` now expects **7** reputation tables, not 3.
