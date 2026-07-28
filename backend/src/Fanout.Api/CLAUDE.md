# fanout-svc (C024 ws-realtime-pipeline) — the `/hubs/live` SignalR hub

Stack: .NET 10 + ASP.NET Core SignalR + StackExchange.Redis. References `MageRide.Shared` (C002).
No database, no Kafka. Listens on **5001** in the compose stack (D7' §2.1); the gateway proxies
`/hubs/{**remainder}` to it with HTTP/1.1 pinned and a 30-minute activity timeout (C008).

**Verify:** `dotnet test backend/src/HotPath.Tests -c Release`

`backend/contracts/realtime/signalr-hub.md` is normative for this surface the way the `*.yaml`
files are for the REST one, and it wins over this file and over the code.

## What this slice implements

| Method | State |
|---|---|
| `JoinGeocells(cells[])` | Yes — res-7 ids, validated |
| `LeaveGeocells(cells[])` | Yes — 30 s hysteresis |
| `SubscribeRide(rideId)` | **C041** |
| `SubscribeLocRequest(requestId)` | **C041** |

| Event | State |
|---|---|
| `VehiclePositions` | Yes — per-cell batch |
| `VehicleRemoved`, `RideStateChanged`, `DriverPosition`, `LocationRequestResolved`, `ShareRevoked`, `PackageStatus` | **C041** |

`SubscribeRide` and `SubscribeLocRequest` are absent rather than stubbed. Both are "rejected unless
the caller is a participant", and a version that joined the group without checking would be a
working subscription to somebody else's ride — a hole that reads, from the client, exactly like the
finished feature.

## Rules that are load-bearing

- **There is no SignalR backplane, and adding one for the cell batches would be a bug.** Every
  replica reads the `cell:{h3index}` streams it has members in and pushes to its own local group,
  so coverage is already complete. A backplane would re-broadcast each replica's send to every
  other replica and a passenger would receive one copy of every frame per replica in the
  deployment. D6' §5's backplane earns its place for the **directed** sends C041 owes —
  `ShareRevoked`'s targeted `RemoveFromGroupAsync` under 200 ms (D-22), `RideStateChanged`,
  `DriverPosition` — where the replica holding a connection is unknown. **C041 must add it for
  those and keep the per-cell batches off it.**
- **A cell's read position is fixed at join, not on the pump's first tick.** Resolving it on the
  first tick loses every position written between the join and that tick: the tick advances past
  those entries and sends nothing, because a batch with no frames is not a batch. With a 2 s
  interval that is a 2 s hole at exactly the moment a passenger opens the map. A cell another
  connection already holds is never re-anchored — that would skip entries the existing subscriber
  has not been sent.
- **`$` is never used as a stream position.** A non-blocking `XREAD` from `$` resolves to the
  stream's last id and therefore always returns nothing: a pump that appears to run and never
  delivers.
- **Batched, never per fix.** One `VehiclePositions` per cell per tick carrying the **newest frame
  per vehicle**. A vehicle that reported four times inside a window is in one place now, and
  replaying its history makes the marker jitter backwards. Per-fix fan-out is the O(passengers ×
  vehicles) cost ADD §7.4 exists to avoid.
- **Cells are validated as res-7.** `JoinGeocells` takes an array off the wire, and a bad value's
  consequence is silence — an unparseable or wrong-resolution id becomes a group name nothing
  publishes to. The hub answers with a `HubException` naming the resolution, because the superseded
  "res-8 + ring(1)" figure is still in circulation (R-06).
- **The credential is the ordinary 30-minute API access token (D-29), in the `access_token` query
  parameter** — SignalR's convention, and unavoidable because a browser `WebSocket` cannot set an
  `Authorization` header. It is **never** the MQTT session JWT, which is a separate credential with
  a different lifetime and audience (E-02). The query hook is scoped to `/hubs/live`: anywhere else,
  a token in a URL is a token in a proxy log.
- **A disconnect releases cells immediately; only `LeaveGeocells` waits.** The socket is gone, so
  there is no membership to preserve and holding one would keep this replica polling for nobody.

## Not here

**The D-22/D-23 visibility filters.** Public geocell groups should carry Mode A always, Mode B only
to entitled passengers (the `share:{userId}` SET, pub/sub-invalidated), and Mode C only while not on
active hire. **None of that is implemented** — this slice fans out every vehicle
position-processor-svc indexed. That is the documented state of the walking skeleton, and it is why
nothing here claims to implement D-22.

## Configuration

`Fanout:BatchInterval` (2 s — the floor of `signalr-hub.md` §3's 2–8 s band, because the C024 SLO
is under 5 s p95), `:MaxEntriesPerCellPerTick`, `:LeaveHysteresis` (30 s, ADD §7.4 step 6),
`:MaxCellsPerConnection` (128 — above both the 19-cell 3 km view and the 37-cell intercity one),
`:PumpEnabled`.

`Fanout:JoinSeedFrames` (32) replays the tail of each joined cell to **the joining connection
only**. It is a stand-in: `signalr-hub.md` §1.1 makes `GET /v1/nearby` (query-svc, C042) the real
snapshot path, and until that exists a passenger who opens the map sees nothing until each nearby
vehicle's next sample. **C041/C042 should remove it** once `/v1/nearby` lands.

`Jwt:*` as every service. `ConnectionStrings:Redis` is the whole of this service's state.
