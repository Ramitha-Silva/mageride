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
| `SubscribeRide(rideId: string)` | UUID | Live driver position and state for the caller's own ride (US-6A.12). **Rejected unless the caller is a participant** — checked against fanout-svc's projection of `ride.events`, and refused for a ride it has never seen. |
| `SubscribeLocRequest(requestId: string)` | UUID | Booker awaits the rider's confirmation (P-13). The group is named from the caller's own token, so a caller who is not the booker joins a group nothing publishes to. |

D3' §3.1 and D6' §5.1 both write these two arguments as ULIDs; `rides.rides.id` and
`rides.location_requests.request_id` are `UUID` columns and every REST response renders them as such,
so the wire form is a UUID string. Recorded as a micro-change-set in the C041 handoff.

### 2.1 Groups

| Group | Membership |
|---|---|
| `cell:{h3Res7}` | Any authenticated client that joined the cell. Carries **public** vehicles only — see §4 |
| `vehicle:{vehicleId}` | The passengers entitled to a Mode B vehicle (D-23), and that vehicle's own driver (AL-31). **Joined by the server, never asked for** |
| `ride:{rideId}` | The ride's passenger, its driver, its proxy **rider**, and — for a proxy booking — the booker |
| `booker:{bookerId}:loc-req:{requestId}` | The booker who issued the location request (P-13) |

**`vehicle:{vehicleId}` is a C041 addition to D6' §5.1's table**, and it is the only shape in which
§5.1 and §5.2 are both satisfiable. §5.2 says a public geocell group fans out "Mode A + entitled
Mode B", which cannot be true of a *group*: a cell group has one membership and one message, so a
Mode B frame put on it reaches every passenger in the cell, entitled or not. ADD §11.10's remedy —
remove the revoked passenger from the geocell group — would also stop them seeing the buses, which
is visibility §5.2 grants unconditionally. Splitting the private vehicles onto a group of their own
makes both lines hold literally: entitlement is still checked at join and never per frame, and D-22's
revocation is still one directed `RemoveFromGroupAsync` that now removes exactly what was granted.

**There is no `SubscribeVehicle` method.** Every membership of that group is derived from server-side
state — the `share:{userId}` SET for a Mode B watcher, registry-svc's `lock:driver:{driverId}`
go-live selection for the driver home map — so there is no request a client could make that the
server would not have to overrule. AL-31 is enforced by what the server joins: a driver is put in
exactly one vehicle group, their own, whatever the app asks for.

The **proxy rider** is likewise a C041 addition. P-01 makes booker and rider two different people and
the rider is the one actually in the car; the original line names the booker and omits them.

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

Where one vehicle's position may go:

| Mode | State | Audience |
|---|---|---|
| **A** — bus, train | any | `cell:{h3Res7}` — public, always |
| **B** — private shared | any | `vehicle:{vehicleId}` only, i.e. its entitled passengers |
| **C** — on demand | idle | `cell:{h3Res7}` — public |
| **C** | on active hire | `ride:{rideId}` only, as `DriverPosition`. Also `vehicle:{vehicleId}`, which for a Mode C vehicle is its own driver (AL-31) |
| any | stale or offline | nobody (US-7.17) |

The filter splits in two, and only one half is per passenger. **Stale, offline and on-hire are facts
about the vehicle**, identical for every subscriber, so they are decided once per frame and the batch
stays a batch — ADD §7.4's O(updates × subscribers-per-cell) cost model is untouched. **Entitlement is
a fact about the pair**, and it is settled at group join: an entitled passenger is a member of
`vehicle:{vehicleId}` and everybody else is not, so no frame is ever tested against a passenger.

A vehicle that leaves the public map is announced with `VehicleRemoved` and a reason, not by going
quiet: batches carry only what moved, so a client that inferred removal from absence would erase
every stationary vehicle on every tick.

**Freshness.** No specification pins the window. D5' §5.4, ADD §6 and US-7.17 all say "older than the
freshness window"; `Fanout:FreshnessWindow` defaults to **60 s**, matching the R-08 presence TTL, so
a vehicle leaves the passenger's map at the same moment its driver leaves the dispatch pool. The same
rule drops a frame that *arrives* older than the window, which is what keeps a reconnecting device's
`veh/{id}/pos/replay` backlog off the live map — those samples reach the same cell stream as live
ones.

**Entitlement cache.** Mode B entitlement is a Redis `share:{userId}` SET, invalidated by
`share.granted`/`share.revoked` on `registry.events` (D-23), and it is checked **on group join**, not
per frame. A miss means "not entitled" — a cold cache costs an entitled passenger their Mode B
vehicles until their next grant event, which is a degradation they can see and report, where the
opposite default would be a disclosure nobody can.

**Revocation is directed and immediate.** A `share.revoked` event triggers a targeted
`RemoveFromGroupAsync` for the affected passenger in **under 200 ms** (D-22) — the platform does
not wait for the passenger's next cell crossing to stop showing them a vehicle they no longer have
access to. `ShareRevoked` is delivered to that passenger so the client can drop the marker rather
than let it go stale.

### 4.1 Backplane

D6' §5's "Redis (MVP) → Redpanda (scale)" applies to the **directed** sends only — `ShareRevoked`,
`RideStateChanged`, `LocationRequestResolved`, `PackageStatus` — whose target connection may be on
any replica. They travel a Redis pub/sub channel (`fanout:control`) that each replica applies to its
own connections, so every client is served exactly once.

**The per-cell batches must never go through a backplane.** Every replica reads the `cell:{h3index}`
streams it has members in and pushes to its own local group, so coverage is already complete;
SignalR's `AddStackExchangeRedis()` would re-broadcast each replica's send to every other replica and
a passenger would receive one copy of every frame per replica in the deployment.

Sticky sessions are still required at the edge — a WebSocket is a single long-lived connection, and
SignalR's SSE and long-polling fallbacks route several requests to one connection id.

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
