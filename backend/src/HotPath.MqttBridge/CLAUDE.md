# mqtt-bridge-svc (C024 ws-realtime-pipeline) — EMQX to Redpanda

Stack: .NET 10 + MQTTnet 5 + Confluent.Kafka. References `MageRide.Shared` (C002).
No database, no Redis, no HTTP surface beyond the two health probes.

**Verify:** `dotnet test backend/src/HotPath.Tests -c Release`

## What this service is

One job: hold the E-08 shared subscription `$share/posGroup/veh/+/pos/live` (and the parallel
`$share/posReplayGroup/veh/+/pos/replay`), key each payload by the vehicleId in its topic, produce
it to `telemetry.raw`, acknowledge. Contracts: `backend/contracts/realtime/mqtt-topics.md` §1/§4,
D6' §3.3, ADD §7.3.

## Rules that are load-bearing

- **The `$share/` prefix is the component.** Drop it and nothing errors: every replica receives
  every message and `telemetry.raw` carries one copy per replica. `MqttTopics.SharedPositionLive`
  builds the filter from a group name so a configuration string cannot lose it, and
  `MqttBridgeTests.Two_replicas_share_the_subscription_with_no_duplicate_ingest` counts what
  actually landed on the topic rather than what the bridge thinks it forwarded.
- **Live and replay are separate groups.** R-09 keeps a reconnect storm's backlog off the live
  path. Same-group would let a fleet's replay share the delivery budget with the samples saying
  where vehicles are right now. The *priority* half of R-09 — live preempting replay 4:1 — is
  **not** implemented; it needs broker-side priority the C009 configuration does not set.
- **The partition key comes from the topic, never the payload.** EMQX authenticated the topic
  (`acl.conf` binds a device to `veh/${username}/*`; `emqx.conf` binds `${username}` to the token's
  `vehicleId` claim). The payload is whatever the device chose to write. Keying on it would let a
  compromised handset write into another vehicle's partition.
- **It decodes nothing.** The payload crosses as opaque bytes. A bridge that parsed payloads would
  drop a sample it merely failed to understand, before anyone could see it on `telemetry.raw` and
  find out why. Normalisation, the `seq` dedupe and anti-spoof are position-processor-svc's.
- **Acknowledgement is manual and follows the produce** (`args.AutoAcknowledge = false`). MQTTnet
  acks on handler return, which would make EMQX → Redpanda at-most-once. A payload that cannot be
  produced is left unacknowledged and EMQX redispatches it to another group member when this
  session ends. There is no in-process retry and no `telemetry.raw.dlq` (D6' §2.3) — that is C039.
- **The credential is a `svc-` principal.** `acl.conf` grants `$share/#` and the `veh/#` wildcard to
  `^svc-` and nothing else. `MqttSessionTokenIssuer.IssueForService` adds the prefix, so a caller
  cannot mint one under a vehicle-shaped username.

## Configuration

`Mqtt:Host` / `Mqtt:Port` / `Mqtt:UseTls` — the broker. In-cluster components use the plaintext
1883 listener, which is never published outside the docker network.

`Mqtt:SessionTokenSecret` **must equal EMQX's `EMQX_AUTHENTICATION__1__SECRET`** or every CONNECT
is refused. Development form only: D6' §3.2 mints RS256 in provisioning-svc (C030) and EMQX
validates against its JWKS with D-21's 15-minute cache — the commented block already in
`infra/deploy/emqx/emqx.conf`.

`MqttBridge:LiveShareGroup` / `MqttBridge:ReplayShareGroup` — the group names, not filters. Any
names work as long as every replica agrees; the two must differ.

`MqttBridge:ServiceName` (default `mqtt-bridge`) — bare name; the `svc-` prefix is added.
`MqttBridge:Enabled` gates the worker. `MqttBridge:ConsumeReplay` gates the backlog subscription.
`MqttBridge:ReconnectDelayMin`/`Max` are R-09's jittered 1–60 s backoff.

## Not here

The D-17 rate ceiling (EMQX's listener and rule engine enforce it; the `mqtt.rate_violation` audit
event is C038/C125), the LWT consumers (R-15/T-04 — trip-state, dispatch and fleet-health each
consume `veh/+/status`), the DLQ, and the Dockerfile. `infra/docker-compose.dev.yml` expects a
combined `backend/src/HotPath/Dockerfile` covering mqtt-bridge + position-processor +
persistence-writer + fleet-health; that container is assembled by C038–C044 and the path does not
match this project's name yet.
