# SignalR hub contract — `/hubs/live`

**Owner:** `fanout-svc` (C041) · **Consumers:** passenger and driver apps (C013/C017), Passenger
Web subview (C117) · **Contract tests:** C118

**Sources.** `specs/D3_mageride_api_contracts.md` §3.1 · `specs/D6_mageride_integration.md` §5 ·
ADD R-06, R-08, R-09, D-22, D-23, P-13, E-01.

OpenAPI cannot express a bidirectional hub, so this file is the contract for the socket surface
the way the `*.yaml` files in this directory are the contract for the HTTP surface. **Method
names, argument shapes and event payloads below are normative** — C013 generates its client
against them and C118 asserts them.

---

## 1. Connection

| Property | Value |
|---|---|
| Endpoint | `/hubs/live` |
| Transport | WebSockets, with SSE and long-polling as SignalR's own fallbacks |
| Auth | Access JWT in the **`access_token` query parameter** — the SignalR convention, because a browser `WebSocket` cannot set an `Authorization` header |
| Protocol | JSON hub protocol; property names are camelCase, matching the REST surface |
| Backplane | Redis (MVP) → Redpanda (scale, beyond 5 pods) |
| Keep-alive | 15 s |
| Server timeout | 30 s |
| Reconnect | Automatic, jittered backoff |

The token is an ordinary 30-minute RS256 access token (D-29) — **not** the MQTT session JWT,
which is a separate credential with a different lifetime and audience (E-02). A connection whose
token expires mid-stream is closed; the client reconnects with a refreshed token.

### 1.1 Reconnection and state recovery (R-08 functional analog)

On reconnect the client:

1. rejoins its geocell groups with `JoinGeocells`, and
2. **resyncs from `GET /v1/nearby`** (query-svc, served from Redis GEO) rather than waiting for
   the next per-cell batch.

That two-step is why `/v1/nearby` exists at all: the socket carries deltas, the REST read carries
the snapshot. A client that only reconnects, without resyncing, will show a stale map until the
next batch tick.

### 1.2 Reconnect-storm controls (R-09)

Mobile clients use jittered exponential backoff (1–60 s, ±25%). The same ±25% symmetric band the
shared kernel generates by hand for Polly (C002 decision 5) applies here — a decorrelated curve
would let a large fleet re-synchronise into a second thundering herd.

---

## 2. Client → Server

| Method | Args | Effect |
|---|---|---|
| `JoinGeocells(cells: string[])` | H3 **resolution-7** cell ids | Subscribe to live vehicle frames for those cells. A 3 km passenger view is res-7 self + `ring(2)` = **19 cells** (R-06). **Mode B entitlement is checked at join** (D-23) — a cell join never grants visibility of a vehicle the caller is not entitled to see. |
| `LeaveGeocells(cells: string[])` | — | Unsubscribe. **30 s hysteresis on boundary churn** — a client oscillating across a cell edge does not thrash group membership. |
| `SubscribeRide(rideId: string)` | ULID | Live driver position for the caller's own ride (US-6A.12). Rejected unless the caller is a participant. |
| `SubscribeLocRequest(requestId: string)` | ULID | Booker awaits the rider's confirmation (P-13). |

### 2.1 Groups

| Group | Membership |
|---|---|
| `cell:{h3Res7}` | Any authenticated client that joined the cell, filtered by entitlement |
| `ride:{rideId}` | The ride's passenger, its driver, and — for a proxy booking — the booker |
| `booker:{bookerId}:loc-req:{requestId}` | The booker who issued the location request (P-13) |

---

## 3. Server → Client

| Event | Payload | When |
|---|---|---|
| `VehiclePositions` | `[{ vehicleId, lat, lng, heading, speed, type, mode }]` | Per-cell batch, every **2–8 s** (US-7.3). Batched, not per-fix — a per-fix fan-out would be 5 msg/s per vehicle. |
| `VehicleRemoved` | `{ vehicleId, reason: "stale" \| "offline" \| "engaged" }` | US-7.16/7.17. `engaged` = a Mode C vehicle went on hire and left the public groups. |
| `RideStateChanged` | `{ rideId, state, version, driver?, etaSeconds? }` | Every ride-aggregate transition (ADD Appendix B.2). `state` is one of the 18 `RideState` values; `version` is the same optimistic-concurrency counter the REST responses carry. |
| `DriverPosition` | `{ rideId, lat, lng, heading }` | Assigned-ride live position (US-6A.12), to `ride:{rideId}` only. |
| `LocationRequestResolved` | `{ requestId, state: "Confirmed" \| "Declined" \| "Expired", geo? }` | The proxy round-trip resolving (P-02, P-13). `geo` is present only for `Confirmed`. |
| `ShareRevoked` | `{ vehicleId }` | Mode B unsubscribe (D-22). |
| `PackageStatus` | `{ rideId, status: "PickedUp" \| "InTransit" \| "Delivered" }` | US-20.7. |

Payload field names and value sets match the REST contracts exactly: `RideState` from
`_shared.yaml`, vehicle types from `VehicleType`, `mode` from `OperatingMode`. A client can share
one set of models between the socket and the API (C012).

---

## 4. Visibility and entitlement (D-22, D-23)

Public geocell groups fan out:

- **Mode A** — bus and train, always;
- **Mode B** — only to entitled passengers;
- **Mode C** — only while *not* on active hire. Once a ride is accepted the vehicle is removed
  from the public groups with `VehicleRemoved{reason:"engaged"}` and appears only in
  `ride:{rideId}`.

Stale and offline vehicles are dropped (US-7.17).

**Entitlement cache.** Mode B entitlement is a Redis `share:{userId}` SET, invalidated by pub/sub
(D-23), and it is checked **on group join**, not per frame.

**Revocation is directed and immediate.** A `share.revoked` event triggers a targeted
`RemoveFromGroupAsync` for the affected passenger in **under 200 ms** (D-22) — the platform does
not wait for the passenger's next cell crossing to stop showing them a vehicle they no longer have
access to. `ShareRevoked` is delivered to that passenger so the client can drop the marker rather
than let it go stale.

---

## 5. Proxy round-trip (P-13)

1. The booker calls `POST /v1/location-requests` (ride-svc).
2. The booker's client subscribes `booker:{bookerId}:loc-req:{requestId}` with
   `SubscribeLocRequest`.
3. The rider confirms — in-app via `POST /v1/location-requests/{requestId}/confirm`, or with no app
   at all via `POST /public/track/{token}/pickup/confirm` (AL-45).
4. ride-svc writes the outbox row; fanout-svc publishes `LocationRequestResolved` to that group.
5. Expiry (300 s) and decline are pushed on the same channel.

**There is no polling anywhere in this flow.** `GET /v1/location-requests/{requestId}` exists for
reconnect and support diagnosis only.

---

## 6. What is *not* on this hub

- **Device position ingest.** Devices publish over MQTT (`realtime/mqtt-topics.md`); they never
  connect to SignalR. §3.3 of D3' resolves this explicitly: passenger realtime-out is SignalR,
  device ingest is MQTT, and there is no MQTT-as-realtime-out.
- **Background delivery.** A backgrounded app receives FCM/APNs pushes, not socket frames:
  `RIDE_OFFER` (high-priority/silent, E-01), `DRIVER_ASSIGNED`, `DRIVER_ARRIVED`,
  `RIDE_CANCELLED`, `PAYMENT_CONFIRMED`, `SCHEDULED_REMINDER`, `DIRECTIONAL_EXPIRING` (10 min,
  DT-08/US-10.14), `LOW_BALANCE`, `location_request`, `package_*`, `SOS_*`. See
  `notification.yaml`.
- **The Passenger Web subview.** SCR-WT pages use the **SSE** stream
  `GET /public/track/{token}/live` (public-bff), not SignalR — the token is the credential there
  and there is no JWT to put in `access_token`.
