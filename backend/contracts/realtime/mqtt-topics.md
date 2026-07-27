# MQTT topic contract — EMQX device plane

**Owner:** `mqtt-bridge-svc` (C038) and `tcp-adapter` (C043) · **Publishers:** driver apps
(HiveMQ/CocoaMQTT), MQTT-native hardware trackers, protocol adapters · **Consumers:**
`position-processor-svc` (C039), `persistence-writer-svc` (C040), `trip-state-svc`, `dispatch-svc`,
`fleet-health-svc` · **Contract tests:** C118

**Sources.** `specs/D3_mageride_api_contracts.md` §3.2 · `specs/D6_mageride_integration.md` §3, §4
and §2.2 · ADD §7.2/§7.7, T-01…T-12, R-09, R-15, R-17, D-17, D-21, E-02, E-08.

This is the device-ingest half of the real-time plane. **Devices never connect to SignalR and the
platform never fans out to passengers over MQTT** — D3' §3.3 resolves that split explicitly.

---

## 1. Topic tree and ACL

| Topic | Direction | QoS | Retain | Payload | ACL |
|---|---|---|---|---|---|
| `veh/{vehicleId}/pos/live` | device → broker | 1 | last | CBOR `PositionSample` | device **PUB own only** |
| `veh/{vehicleId}/pos/replay` | device → broker | 1 | no | CBOR backlog, monotonic `seq` | device PUB own; rate-limited |
| `veh/{vehicleId}/cmd` | broker → device | 1 | no | `{cmd, args, expiresAt}` | device **SUB own only** |
| `veh/{vehicleId}/status` | broker (LWT) | 1 | **yes** | `online` \| `offline` | system |
| `fleet/{operatorId}/+/pos/live` | broker → consumer | 1 | — | wildcard | operator-scoped SUB, row-level-security equivalent |
| `sys/diag/{vehicleId}` | device → broker | 0 | no | diagnostics | device PUB own |

**ACL binding.** EMQX binds `{vehicleId}` from the device's JWT or X.509 claim, so a device
physically cannot publish under another vehicle's topic — the authorisation is in the credential,
not in the payload.

**`status` is retained** so a consumer that subscribes after a device went offline still learns it
is offline. `pos/live` retains the last sample for the same reason: a fresh subscriber gets a
position immediately rather than waiting for the next cadence tick.

---

## 2. Payloads

### 2.1 `PositionSample` — `pos/live` and `pos/replay`

CBOR on the wire; JSON shown (D6' §2.2). This is the same shape `position-processor-svc` republishes
onto the `telemetry.normalized` Redpanda topic.

```jsonc
{ "vehicleId": "uuid",
  "sampleTs":  "2026-06-13T10:15:30Z",   // GNSS capture instant — NOT the receive time
  "receivedTs":"2026-06-13T10:15:31Z",
  "seq":       84213,                    // monotonic per vehicle; the replay dedupe key (R-17/T-05)
  "lat": 6.9271, "lng": 79.8612,
  "speedMps": 11.8, "headingDeg": 270,
  "accuracyM": 7.5, "hdop": 0.9, "satCount": 11,
  "source": 1,                           // 0=mobile, 1=gt06, 2=jt808, 3=h02, 4=nmea-mqtt
  "mode": "C", "vehicleType": "three_wheeler",
  "fleetId": null,                       // denormalised at write time — see §6
  "tripId": "uuid?" }                    // Mode A/B only
```

**Validation at the sink.** `telemetry.positions` (C006) enforces latitude and longitude ranges,
`seq >= 0` and `source BETWEEN 0 AND 4` as plain CHECK constraints. A cheap tracker reporting
`0`/`999` degrees is a bug **C039 must filter before the batch arrives** — the constraints make it
loud instead of silently poisoning a rollup.

### 2.2 `cmd` — downlink

```jsonc
{ "cmd": "setPosRate", "args": { "seconds": 1 }, "expiresAt": "2026-06-13T10:20:00Z" }
```

Supported commands: `setPosRate`, `pingNow`, `reboot`, `setGeofence`, `revokeCredential`.

**`expiresAt` is honoured on reconnect** — an expired command is *not* delivered to a device that
comes back later. A `reboot` queued an hour ago is not something a returning vehicle should obey.

For a legacy TCP device the adapter translates this envelope into the protocol-native command frame
over the open socket (T-05, US-3.17).

### 2.3 `status` — LWT

Payload is the literal string `online` or `offline`.

---

## 3. Authentication

| Client | Credential |
|---|---|
| Driver app | **MQTT session JWT**, minted by `POST /v1/auth/mqtt-token` (iam.yaml) |
| MQTT-native tracker | X.509 client certificate, 90-day TTL, minted by provisioning-svc |
| Legacy TCP tracker | Signed PSK + IMEI-HMAC, validated by the adapter |
| mqtt-bridge / adapters | mTLS bridge user |

**The MQTT session JWT is decoupled from the API access token (E-02).** Its TTL is
`max(active ride + 2 h, 4 h)` and it is bound to `(vehicleId, deviceId, rideId?)`, so a mid-trip API
token refresh that fails in poor coverage does not silently stop position publishing. The API access
token stays 30 minutes.

EMQX validates it against a **locally cached JWKS with a 15-minute TTL plus a just-in-time lookup on
miss** (D-21) — without the cache, a fleet reconnecting together would stampede the JWKS endpoint at
exactly the worst moment.

**Revocation is sub-second (T-12).** EMQX's dynamic ACL and the TCP adapters read the same
provisioning-svc Redis lookup with pub/sub invalidation; the adapter re-validates every 5 minutes on
long-lived sockets and **force-closes a matching socket within 1 s** of a revoke.

---

## 4. Rate limits (D-17, E-08)

| Limit | Where | Effect on breach |
|---|---|---|
| **5 msg/s per `vehicleId`** on `veh/+/pos/live` | EMQX rule engine | Suppressed + `mqtt.rate_violation` → `audit.events` |
| **10 msg/s per 10 s** | position-processor, second line | Drop + flag |
| **20 samples/s per device** on `pos/replay` | EMQX + adapter | Throttled |
| **500 connections/s per listener**, plus a per-ASN cap | EMQX | Connection refused |

The 5/s ceiling is sized to accommodate the 1-second near-geofence cadence plus retries — it is a
misbehaviour ceiling, not the expected rate. Normal cadence is adaptive (US-5.5).

**Consumption is a shared subscription.** `mqtt-bridge-svc` subscribes
`$share/posGroup/veh/+/pos/live` (and a parallel group for `/pos/replay`), so N replicas
load-balance with **exactly-once dispatch and no duplicate ingest** (E-08); Redpanda offsets are
committed per partition and EMQX redistributes on replica loss.

---

## 5. Replay and dedupe (R-17, T-05)

An offline tracker buffers to flash — a 50,000-sample ring — and bursts to `pos/replay` on
reconnect, on a **separate consumer group** so a backlog never delays live traffic.

Three layers of dedupe, in order:

1. **position-processor** keeps `veh:seq:{vehicleId}` in Redis and discards `seq <= last_seen`.
2. **On reconnect the client drains live for 2 seconds before unlocking replay**, and live preempts
   replay 4:1 (R-09) — a returning vehicle's current position is more valuable than its history.
3. **The database** rejects an exact duplicate:
   `ux_positions_vehicle_seq (vehicle_id, seq, sample_ts)`.

> **C040 must write `ON CONFLICT (vehicle_id, seq, sample_ts) DO NOTHING` — not a two-column
> conflict target.** TimescaleDB rejects a unique index that omits a partitioning column, so the
> two-column `(vehicle_id, seq)` index both DDL sources print cannot be created (C006 note (a)).
> The three-column index still rejects the case R-17/T-05 exists for, because a re-sent buffered
> sample carries the GNSS timestamp it was captured with. It does **not** reject a same-`seq` sample
> bearing a *different* timestamp — which is why layers 1 and 2 above are not optional.

---

## 6. Last will, offline and mode routing

**LWT (R-15, T-04).** EMQX publishes the retained `veh/{vehicleId}/status = offline` last will.
Three services consume it:

| Consumer | Action |
|---|---|
| `trip-state-svc` | Auto-end the Mode A/B session (`POST /v1/internal/sessions/{sessionId}/auto-end`, reason `mqtt_offline`) |
| `dispatch-svc` | Release the active offer and start the grace window (R-15); **clear the active Directional filter** (DT-04) |
| `fleet-health-svc` | Update the health rollup |

**The TCP adapter emulates LWT on socket half-close** by publishing the same retained
`status=offline` (T-04) — a legacy device that simply loses its socket is indistinguishable, to
every consumer, from an MQTT device whose will fired.

**Mode routing (T-11).**

- **Mode A bus:** the tracker is authoritative and broadcasts irrespective of any driver app.
- **Mode C:** dispatch reads the tracker when one is bound; a tracker offline for more than 30 s
  falls back to phone GPS, or marks the vehicle unavailable.
- **One publisher at a time.** `POST /v1/trackers/{imei}/switch-source` (provisioning.yaml) is what
  chooses between phone and hardware — the position stream never interleaves two clocks (US-3.6).

**Fleet scoping.** `fleetId` is denormalised onto the sample at write time so a fleet-scoped read
needs no join. **C040 must populate it.** A vehicle that changes fleet keeps its old rows under the
old fleet, which is correct for an audit trail and is what the fleet-scoped view returns (C006
decision 8).

---

## 7. Protocol adapters (T-01)

`tcp-adapter` is one StatefulSet per protocol family and has **no HTTP surface**.

| Family | Transport | Port | Devices | Adapter |
|---|---|---|---|---|
| GT06 / GT06N | TCP binary | 5023 | Concox GT06, TK103, ST-901 | `adapter-gt06` |
| JT/T 808 | TCP binary | 5024 | Chinese / SL-import trackers | `adapter-jt808` |
| H02 / H02X | TCP ASCII | 5025 | Older bus trackers | `adapter-h02` |
| Generic NMEA | UDP | 5026 | Low-cost asset trackers | `adapter-nmea-udp` |
| NMEA over MQTT | MQTT native | 8883 | Teltonika / Queclink (new firmware) | **none — direct to EMQX** |

Each adapter: terminate the socket → **validate the IMEI via
`GET /v1/internal/trackers/{imei}/validate`** (provisioning.yaml, mTLS) → decode the binary frame
into a canonical `PositionSample` → publish to `veh/{vehicleId}/pos/live` (or `/pos/replay`) as the
mTLS bridge user.

**IMEI resolution (T-03).** Redis `imei:{imei}` with a 24-hour TTL in front of
`prov.tracker_bindings`, invalidated by `tracker.bound` / `tracker.unbound`.

**Anti-clone (T-08).** Two devices presenting the same IMEI within 24 hours put **both** bindings
into `QUARANTINED` and neither keeps publishing until an admin resolves it. The binding endpoint
answers `409 imei-duplicate`.

Naming: `tcp-adapter` is canonical; `tracker-adapter-svc` (ADD §6) is an alias only — planner
finding 5.

---

## 8. Downstream — where a sample goes next

```
device ──MQTT──> EMQX ──$share──> mqtt-bridge-svc ──> Redpanda `telemetry.raw`
                                                            │
                                              position-processor-svc
                                        (dedupe by seq, normalise, Kalman)
                                                            │
                                                  Redpanda `telemetry.normalized`
                                                            │
                        ┌───────────────────┬───────────────┴──────────────┬─────────────────┐
              persistence-writer      trip-state-svc              fleet-health-svc      fanout-svc
              (COPY → telemetry.       (auto-end timers)          (health rollup)    (SignalR batches)
               positions hypertable)
```

Redpanda's **default partition key is `vehicleId`**, so per-vehicle ordering holds end to end
(D6' §2.1). Poison and retry-exhausted messages land in `<topic>.dlq` with
`{originalOffset, error, attempts}` — nothing is silently dropped (D6' §2.3).
