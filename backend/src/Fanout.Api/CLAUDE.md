# fanout-svc (C024 ws-realtime-pipeline + C041 fanout-svc) — the `/hubs/live` SignalR hub

Stack: .NET 10 + ASP.NET Core SignalR + StackExchange.Redis + Confluent.Kafka + MQTTnet.
References `MageRide.Shared` (C002). No database. Listens on **5001** in the compose stack
(D7' §2.1); the gateway proxies `/hubs/{**remainder}` to it with HTTP/1.1 pinned and a 30-minute
activity timeout (C008).

**Verify:** `dotnet test backend/src/Fanout.Api.Tests -c Release`
(the end-to-end EMQX→SignalR SLO stays in `HotPath.Tests`, where the bridge and the processor are.)

`backend/contracts/realtime/signalr-hub.md` is normative for this surface the way the `*.yaml`
files are for the REST one, and it wins over this file and over the code.

## What this service is

Everything a passenger, a driver or a booker sees move in real time, and — the part that is actually
hard — **who is allowed to see it**. D6' §5.2's visibility table, D-22's directed revocation, D-23's
entitlement cache, US-7.16's engagement hiding, US-7.17's stale and last-will suppression, AL-31's
own-vehicle driver map and P-13's proxy round-trip.

| Surface | State |
|---|---|
| `JoinGeocells` / `LeaveGeocells` | res-7 ids, validated; 30 s hysteresis |
| `SubscribeRide` | participant-checked against the `ride.events` projection |
| `SubscribeLocRequest` | group named from the caller's own token |
| `VehiclePositions` · `VehicleRemoved` · `RideStateChanged` · `DriverPosition` · `LocationRequestResolved` · `ShareRevoked` · `PackageStatus` | all seven |

| Redis key | Written by | For |
|---|---|---|
| `cell:{h3index}` | position-processor-svc | the res-7 fan-out buffer this service reads |
| `veh:meta:{vehicleId}` | position-processor-svc | the per-vehicle current position the ride and Mode B streams are drawn from |
| `share:{userId}` | **this** (from `registry.events`) | D-23's entitlement SET |
| `veh:engaged:{vehicleId}` | **this** (from `ride.events`) | US-7.16's active hire |
| `veh:offline:{vehicleId}` | **this** (from the EMQX last will) | US-7.17's `offline` half |
| `fanout:ride:{rideId}` | **this** (from `ride.events`) | who may join `ride:{rideId}` |
| `fanout:control` | **this** | D6' §5's Redis backplane, for directed sends only |
| `lock:driver:{driverId}` | registry-svc | AL-31 — which vehicle is the driver's own |

Only `share:{userId}` is in ADD §9.4. The other three are micro-change-sets in the C041 handoff and
each is argued at its declaration in `RedisKeys`.

## The four groups, and why there are four

| Group | Carries | Membership decided by |
|---|---|---|
| `cell:{h3index}` | Mode A always, idle Mode C | the client, via `JoinGeocells` |
| `vehicle:{vehicleId}` | that one vehicle | **the server**: `share:{userId}`, or `lock:driver:{driverId}` |
| `ride:{rideId}` | `DriverPosition`, `RideStateChanged`, `PackageStatus` | the participant check |
| `booker:{bookerId}:loc-req:{requestId}` | `LocationRequestResolved` | the caller's own token |

**`vehicle:{vehicleId}` is not in D6' §5.1's table, and it is the only shape in which §5.1 and §5.2
are both satisfiable.** §5.2 says a public geocell group fans out "Mode A + entitled Mode B", which
cannot be true of a *group*: a cell group has one membership and one message, so a Mode B frame put
on it reaches every passenger in the cell. ADD §11.10's remedy — remove the revoked passenger from
the geocell group — would also stop them seeing the buses, which §5.2 grants unconditionally.
Splitting the private vehicles onto their own group makes both lines hold literally, and makes the
D-22 revocation remove exactly what was granted.

**There is no `SubscribeVehicle`.** Every membership of that group is derived from server-side state,
so there is no request a client could make that the server would not have to overrule — a method
taking a vehicle id would be a method whose whole body is "ignore the argument". It is also what
makes AL-31 structural: a driver is joined to exactly one vehicle group, their own.

## Rules that are load-bearing

- **The per-cell batches must never go through a SignalR backplane, and `AddStackExchangeRedis()` is
  not called.** Every replica reads the cell streams it has members in and pushes to its own local
  group, so coverage is already complete; a backplane would re-broadcast each replica's send and a
  passenger would get one copy of every frame per replica in the deployment. The **directed** sends
  — `ShareRevoked`, `RideStateChanged`, `LocationRequestResolved`, `PackageStatus` — do have to cross
  replicas, and they travel `fanout:control`, where each replica applies the signal to its own
  connections and nobody else's. That is D6' §5's "Redis backplane (MVP)"; the Redpanda form at scale
  is the same fan-out with a consumer group per replica rather than per service.
- **The filter splits in two, and only one half is per passenger.** Stale, offline and on-hire are
  facts about the *vehicle* and identical for every subscriber, so they are decided once per frame
  and the batch stays a batch. Entitlement is a fact about the *pair*, and D6' §5.2 settles it at
  group join — which is why it costs nothing per frame.
- **A vehicle leaving the map is announced, never implied.** Batches carry only what moved, so a
  client that inferred removal from a batch not mentioning a vehicle would erase every stationary
  one on every tick. `VehicleRemoved` is sent once per transition with the contract's reason;
  `VehicleStreamPump` and `CellStreamPump` each remember what they have published so the "once" is
  real.
- **US-7.17 is detected two ways and they are not redundant.** The last will is immediate and the
  freshness sweep is the backstop, a minute behind it. A deployment with no broker reachable still
  removes the vehicle; a broker that fires removes it before the passenger walks towards it.
- **The offline mark is an instant, not a flag.** A device whose session died and whose app restarted
  publishing may never send an `online`, so visibility compares `veh:offline` against the sample's
  own `sampleTs`. A fresher sample is what brings it back, with nothing else needed.
- **A frame that arrives older than the freshness window is dropped, not drawn.** A reconnecting
  device's `veh/{id}/pos/replay` backlog reaches the same cell stream as live traffic
  (position-processor-svc writes both through one path) and the only thing that tells them apart is
  the capture instant. Without the check, an hour of history arrives as an hour of current positions.
- **`Completed` and `PaymentPending` release the vehicle.** The passenger is out of the car and
  dispatch-svc released the driver on `ride.completed`; keeping it hidden until the money settled
  would take an available driver off the map for as long as a card authorisation takes. Engagement is
  `Accepted | DriverArrived | InProgress` and everything else releases — including states this
  service has never heard of, because the alternative is a terminal list that has to be kept in step
  with ride-svc's eighteen.
- **Engagement is read off the ride's `state`, not off the event type.** A replica that starts mid-life
  and a topic read from an offset both classify correctly from the first message they see.
- **`SubscribeRide` refuses a ride this service has never seen.** A gap in the projection means
  fanout-svc does not know who the parties are, which is not the same as knowing the caller is one of
  them. The unknown-ride and not-yours messages are deliberately identical — telling a caller that a
  ride exists but is not theirs is a membership oracle over other people's journeys.
- **The ride and Mode B streams are read from `veh:meta`, not from the cell streams.** A cell stream
  only reaches a replica with a subscriber in that cell, and none of these audiences is subscribed to
  a place: a long ride leaves the nineteen cells the app joined within minutes, and a Mode B watcher
  follows a school van across a city. Driving them from cell membership would stop the position for a
  reason the user cannot see.
- **A cell's read position is fixed at join, not on the pump's first tick.** Resolving it on the first
  tick loses every position written between the join and that tick: the tick advances past those
  entries and sends nothing, because a batch with no frames is not a batch. A cell another connection
  already holds is never re-anchored — that would skip entries the existing subscriber has not seen.
- **`$` is never used as a stream position.** A non-blocking `XREAD` from `$` resolves to the
  stream's last id and therefore always returns nothing: a pump that appears to run and never
  delivers.
- **Batched, never per fix.** One `VehiclePositions` per cell per tick carrying the newest frame per
  vehicle. A vehicle that reported four times inside a window is in one place now, and replaying its
  history makes the marker jitter backwards.
- **Cells are validated as res-7.** A bad value's consequence is silence — an unparseable or
  wrong-resolution id becomes a group name nothing publishes to — so the hub answers with a
  `HubException` naming the resolution (R-06's superseded "res-8 + ring(1)" is still in circulation).
- **A share event that names no passenger is skipped, not stalled.** D6' §5.1's own `{vehicleId}`
  payload is one this service cannot act on; stalling the partition over it would stop every later
  revocation behind it, turning one unusable message into an unbounded visibility leak.
- **The credential is the ordinary 30-minute API access token (D-29), in the `access_token` query
  parameter** — SignalR's convention, and unavoidable because a browser `WebSocket` cannot set an
  `Authorization` header. It is **never** the MQTT session JWT (E-02). The query hook is scoped to
  `/hubs/live`: anywhere else, a token in a URL is a token in a proxy log.
- **`Clients.User(...)` needs `SubjectUserIdProvider`.** SignalR's default reads
  `ClaimTypes.NameIdentifier`, which the kernel deliberately does not map. Without it the directed
  sends address nobody — silently, because a send to an unknown user is not an error.
- **A disconnect releases everything immediately; only `LeaveGeocells` waits.** The socket is gone, so
  there is no membership to preserve and holding one would keep this replica polling for nobody.
- **Every filter that can be switched off is named at start-up.** An open filter looks exactly like a
  working one from the outside: positions flow, the map is populated, nothing errors, and the
  difference only surfaces when somebody sees a vehicle they should not.

## Known gaps

- **`share:{userId}` has no rebuild path.** This service is its only writer and builds it from
  `registry.events`; a fresh consumer group replays the topic, which covers a new deployment, but a
  Redis flush after that leaves entitled passengers with no Mode B visibility until their next grant
  event. Failing closed is the right direction, and the durable fix is a read-through against
  registry-svc — C048's surface. Raised in the C041 handoff.
- **`etaSeconds` is never sent on `RideStateChanged`.** D3' marks it optional and the estimate is
  query-svc's (C042), computed from the route; inventing one here would put two different numbers in
  front of one passenger.
- **`Fanout:JoinSeedFrames` is still a stand-in.** `signalr-hub.md` §1.1 makes `GET /v1/nearby`
  (query-svc, C042) the real snapshot path. **C042 should remove it** once that lands.

## Configuration

Every knob is documented at its declaration in `FanoutOptions` and in `infra/env/.env.app.example`.
The ones that are not obvious:

| Setting | Default | Where it comes from |
|---|---|---|
| `BatchInterval` | 2 s | floor of `signalr-hub.md` §3's 2–8 s band; the SLO is 5 s p95 |
| `FreshnessWindow` | 60 s | **no spec pins it** — matches `Dispatch:PresenceTtl` so the map and the pool agree |
| `MaxCellsPerConnection` | 128 | above both the 19-cell 3 km view and the 37-cell intercity one |
| `MaxVehicleSubscriptions` | 64 | a bound on what the *server* reads, not on what a client asks |
| `RideProjectionTtl` | 24 h | must outlive a live ride by a reconnect; R-20's SLOs are minutes |
| `EngagementTtl` | 12 h | a backstop for a terminal event never seen; errs long on purpose |
| `JoinSeedFrames` | 32 | the C042 stand-in |
| `LeaveHysteresis` | 30 s | ADD §7.4 step 6 |
| `EventsEnabled` · `ControlPlaneEnabled` · `PresenceEnabled` · `PumpEnabled` | on | each gates one filter; all four warn when off |
| `ConsumerGroup` | `fanout-svc` | D6' §2, "consumer group per service" |

`Jwt:*` as every service. `Kafka:BootstrapServers` and `Mqtt:*` are needed only while
`EventsEnabled` / `PresenceEnabled` are on. `ConnectionStrings:Redis` is the whole of this service's
state.
