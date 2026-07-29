# mqtt-bridge-svc (C024 skeleton → C038 production form) — EMQX to Redpanda

Stack: .NET 10 + MQTTnet 5 + Confluent.Kafka + StackExchange.Redis. References `MageRide.Shared`
(C002). No database, no HTTP surface beyond the two health probes.

**Verify:** `dotnet test backend/src/HotPath.Tests -c Release --filter Category=MqttBridge`

## What this service is

Hold the E-08 shared subscriptions `$share/posGroup/veh/+/pos/live` and
`$share/posReplayGroup/veh/+/pos/replay`, key each payload by the vehicleId in its topic, produce it
to `telemetry.raw`, acknowledge. On top of that: T-05's 20 samples/s/device limit on the backlog,
D-17's `mqtt.rate_violation` onto `audit.events`, and a drain on shutdown so a rolling deploy does
not duplicate ingest. Contracts: `backend/contracts/realtime/mqtt-topics.md` §1/§4/§5, D6' §3.3,
ADD §7.3/§7.5.2/§7.5.3.

## Rules that are load-bearing

- **The `$share/` prefix is the component.** Drop it and nothing errors: every replica receives
  every message and `telemetry.raw` carries one copy per replica. `MqttTopics.SharedPositionLive`
  builds the filter from a group name so a configuration string cannot lose it, and the E-08 test
  counts what actually landed on the topic rather than what the bridge thinks it forwarded.
- **Live and replay get a session each, not one session with two filters.** The share groups alone
  do not deliver R-09, because MQTT's inflight window is per session: one session holding both
  filters would let 32 unacknowledged backlog samples, each waiting on a T-05 token, stall live
  delivery on the same socket. Two sessions have two windows and the backlog can only starve itself.
  `MqttBridgeRateTests.A_backlog_flood_does_not_delay_live_samples` is the assertion.
- **The partition key comes from the topic, never the payload.** EMQX authenticated the topic
  (`acl.conf` binds a device to `veh/${username}/*`; `emqx.conf` binds `${username}` to the token's
  `vehicleId` claim). The payload is whatever the device chose to write. Keying on it would let a
  compromised handset write into another vehicle's partition.
- **It decodes nothing.** The payload crosses as opaque bytes — but it is **copied on the receive
  loop** (`BridgedMessage`), because MQTTnet reuses its packet buffer as soon as the handler returns
  and neither path forwards synchronously. Normalisation, the `seq` dedupe and anti-spoof are
  position-processor-svc's.
- **Acknowledgement follows the produce, and nothing else** (`args.AutoAcknowledge = false`).
  MQTTnet acks on handler return, which would make EMQX → Redpanda at-most-once. The PUBACK goes
  out only after a delivery report names a partition and an offset. A payload that cannot be
  produced is left unacknowledged and EMQX redispatches it to another group member when this
  session ends. No in-process retry and no `telemetry.raw.dlq` (D6' §2.3) — that is C039.
- **The forward is started synchronously and completed asynchronously.** `TelemetryForwarder.Forward`
  hands the record to librdkafka before it returns and waits for the delivery report on a
  continuation, so several produces are in flight at once. Awaiting each in turn caps a replica at
  one broker round trip per sample — a few hundred samples/s against ADD §7.6's 1 200/s sustained
  and 6 000/s burst. Ordering survives because the enqueue is synchronous and in call order and
  `EnableIdempotence` will not reorder a retry.
- **Stopping is unsubscribe → drain → disconnect.** Anything else turns a rollout into duplicate
  ingest: a payload produced but unacknowledged when the socket drops comes back to another replica.
- **The credential is a `svc-` principal.** `acl.conf` grants `$share/#` and the `veh/#` wildcard to
  `^svc-` and nothing else. `MqttSessionTokenIssuer.IssueForService` adds the prefix.

## The two rate limits

Both counters are **in Redis, not in the replica**. A shared subscription hands each replica a
random slice of one device's stream, so an in-process counter would let N replicas pass N times the
limit — and for D-17 no replica would ever see the rate the vehicle is actually publishing at.

| Limit | Where | On breach |
|---|---|---|
| **20 samples/s/device** on `pos/replay` (T-05) | `ReplayPacer` + `ReplayThrottle`, one lane per device | **Waits.** A backlog is a vehicle's history; the wait reaches EMQX as unacknowledged QoS 1 filling the inflight window, which is ADD §7.5.2's "server-issued back-pressure token" |
| **5 msg/s per vehicleId** on `pos/live` (D-17) | `PublishRateMonitor` | **Nothing is dropped.** One `mqtt.rate_violation` on `audit.events` per vehicle per cooldown, cluster-wide |

**The bridge does not enforce D-17; it reports it.** Enforcement is `emqx.conf`'s
`messages_rate = "5/s"`. The bridge is nevertheless the only place the ceiling can be *measured*:
D6' §3.3 asks the EMQX rule engine for it with a `TUMBLINGWINDOW` aggregate the spec itself calls
illustrative (open-source EMQX 5.8 has no windowed aggregation), the listener limiter emits no
event, and that limiter is **per connection** while D-17 is written **per `vehicleId`** — a device
opening four sessions under one vehicle credential passes the broker four times over and publishes
at 20 msg/s. That is the case the test exercises.

**Both fail open.** Redis unreachable ⇒ the sample is forwarded and the bridge reports unready.
Losing telemetry to a cache outage is worse than losing a limit the broker still half-enforces.

## Configuration

`Mqtt:Host` / `Mqtt:Port` / `Mqtt:UseTls` — the broker. In-cluster components use the plaintext
1883 listener, never published outside the docker network.

`Mqtt:SessionTokenSecret` **must equal EMQX's `EMQX_AUTHENTICATION__1__SECRET`** or every CONNECT
is refused. Development form only: D6' §3.2 mints RS256 in provisioning-svc (C030) and EMQX
validates against its JWKS with D-21's 15-minute cache — the commented block already in
`infra/deploy/emqx/emqx.conf`.

`ConnectionStrings:Redis` — **required as of C038** (both rate counters).

`MqttBridge:LiveShareGroup` / `ReplayShareGroup` — group names, not filters. Any names work as long
as every replica agrees; the two must differ. `MqttBridge:ServiceName` (default `mqtt-bridge`) is a
bare name; the `svc-` prefix is added. `MqttBridge:Enabled` gates the worker, `ConsumeReplay` the
backlog session, `ThrottleReplay` the T-05 bucket, `MonitorPublishRate` the D-17 counter.
`ReconnectDelayMin`/`Max` are R-09's jittered 1–60 s backoff. Every C038 knob is documented in
`infra/env/.env.app.example`.

## Broker settings this service depends on

`infra/deploy/emqx/emqx.conf` is C009's file, but two settings in it are this component's contract:

- **`mqtt.shared_subscription_strategy = sticky`** — set by C038. EMQX 5.8 defaults to
  `round_robin`, which picks the next group member for *every* message: two replicas then take one
  vehicle's samples alternately and race each other to the producer, and the per-vehicle ordering
  ADD §7.3 and D6' §2.1 promise end to end becomes a coin toss. A Redpanda key keeps a partition
  ordered; it cannot reorder what arrived scrambled. `sticky` binds a publishing session to one
  member, so load balancing is per device — which is how a fleet actually distributes.
  `MqttBridgeTests.Per_vehicle_ordering_holds_across_replicas` fails without it.
- **`listeners.*.messages_rate = "5/s"`** — D-17's enforcement, and the reason a test cannot push
  20 samples/s down one connection. Measured: 4.9/s on a single socket, no burst.

## Known gaps

- **"Commit Redpanda offsets per partition" (ADD §7.3) is not a thing a producer does.** Committing
  is what a consumer group does with offsets it has read. What is real, and what carries the
  guarantee behind the sentence, is that the bridge learns *where the broker put a record* before
  it acknowledges: `PartitionOffsetLog` records it off the delivery report and publishes it as
  `mageride.mqtt.bridge.partition_offset`. Raised as a spec finding in the C038 handoff.
- **"Live preempts replay 4:1" (R-09/D6' §3.5) is not implemented as a ratio.** Connection
  isolation plus the 20/s cap is what keeps the backlog off the live path, and it is enough for the
  DoD; a literal 4:1 needs broker-side priority the C009 configuration does not set.
- **`telemetry.raw.dlq`** (D6' §2.3) — C039.

## Not here

The LWT consumers (R-15/T-04 — trip-state, dispatch and fleet-health each consume `veh/+/status`),
the DLQ, and the Dockerfile. `infra/docker-compose.dev.yml` expects a combined
`backend/src/HotPath/Dockerfile` covering mqtt-bridge + position-processor + persistence-writer +
fleet-health; that container is assembled by C039–C044 and the path does not match this project's
name yet.
