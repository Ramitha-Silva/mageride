# transit-svc (C056, routing half) — GTFS Mode A discovery and the paste-link resolver

Stack: .NET 10 Minimal API + Dapper over Npgsql. References `MageRide.Shared` (C002).
**No Redis, no Kafka, no outbox, no command log** — see `TransitApplication` for why each is off.
The only thing that reaches this service asynchronously is Postgres' own `LISTEN/NOTIFY`.

**Verify:** `dotnet test backend/src/Transit.Api.Tests -c Release`

`backend/contracts/transit.yaml` is normative for this surface and wins over this file and over the
code.

## What this service is (and what is C057's)

| Endpoint | Auth | Spec |
|---|---|---|
| `GET /v1/transit/options` | Bearer | D3' transit-svc, AL-18, BR-23.2 |
| `GET /v1/transit/routes/{routeId}` | Bearer | D3' transit-svc |
| `GET /v1/geo/parse-maps-link` | Bearer | AL-20, BR-23.4 |

**`/v1/admin/transit/gtfs/**` is C057's** (AL-54, SCR-AP-016) and is deliberately unmapped here.
This component is the *consumer* of what activation publishes; `contracts/transit.yaml` carries both
halves because they are one service.

| Table | Read | Written |
|---|---|---|
| `transit.gtfs_routes` · `_trips` · `_stops` · `_stop_times` · `_shapes` | once per activation | **C057's importer** |
| `transit.gtfs_feed_versions` | which feed is active | **C057** |

## The three fences, and how each is held structurally

- **No Google APIs, anywhere.** `/v1/geo/parse-maps-link` follows a redirect and reads a coordinate
  out of the URL it lands on. There is one named `HttpClient` in the whole service and it can only
  reach `Transit:MapsLink:AllowedHosts` — no Maps SDK, no Places call, no API key.
- **AL-17: a destination is a geo-location.** Held by an absence of capability rather than a filter:
  every parameter on this surface is a coordinate, so a passenger who types "138" has nowhere to
  send it. `There_is_no_way_to_ask_for_a_route_number_as_a_destination` asserts it against the
  running route table, so a route added later fails the suite.
  (`{routeId}` on the detail route is the opposite direction — an option already chosen.)
- **AL-55: no-coverage is a safety net, not the launch state.** So it is a *different answer on the
  wire*, not an empty list: `coverage: no_feed` versus `coverage: active`. Without the
  discriminator a feed gap is indistinguishable from a corridor no bus serves, and SCR-PA-009 has
  to render them differently.

## Rules that are load-bearing

- **The feed is loaded once per activation and answered from memory.** BR-23.2 asks for *all*
  direct routes between two points; computed per request that is a self-join over half a million
  `gtfs_stop_times` rows on a screen the passenger is watching. What is held is **patterns** —
  distinct stop sequences — indexed by the halts they call at, so a nearby halt is a dictionary
  lookup.
- **A pattern is a distinct stop sequence, not a trip and not one representative per direction.** A
  route's thousands of trips collapse to a handful of sequences, and the question BR-23.2 asks is
  about the sequence. Taking only the longest per direction would be smaller and wrong in both
  directions: it would claim a direct route for a corridor only the full-length working covers, and
  still miss a short-turn that reaches somewhere the long one does not.
- **"Direct" is exactly BR-23.2's sentence** — one route's sequence covers a halt near the origin
  *before* a halt near the destination. The **before** is the whole rule: a route that passes both
  halts in the other order is not a way to get there, and a matcher that intersected sets would say
  it was.
- **All direct *routes*, but one option per route.** Three workings over the same corridor are one
  answer to a passenger; the shortest ride on the route is the one offered.
- **Ordering is fewest stops, then shortest walk — and deliberately not "soonest departure".**
  BR-23.2 asks for "fewest stops/soonest departure" and this build cannot honour the second half:
  **`server_db_schema` §18c mirrors five GTFS tables and none of them is `calendar`/`calendar_dates`**,
  so a trip's departure time is readable but whether that trip runs *today* is not. Ordering on a
  departure nothing can validate would put a Sunday-only working at the top of a Tuesday list. The
  durations that *are* reported come from the pattern's own arrival offsets, which are
  service-day independent. Raised as a gap; C057 is where the calendar would land.
- **One transfer, not two.** BR-23.2 says "≥ 1 transfer" and lists them below direct options.
  Two-transfer search over a national feed is a different algorithm (RAPTOR or a transfer graph) and
  a different latency budget — named in the handoff rather than half-built.
- **A transfer is at one halt, so nothing is walked between legs.** A transfer across two nearby
  halts is a different option shape and a different promise about the interchange.
- **A loop route is boarded at the first occurrence of a halt.** Taking the later one would invent a
  ride around the whole loop that nobody would choose.
- **The halt radius is a setting because it decides whether a corridor has a route at all.** 400 m
  (BR-23.2, D6' I-32.1), bounded — a 5 km "walk" would return every route in the city as reachable.
- **The cache is swapped, never mutated**, matching AL-54's own shape on the database side: a reload
  builds a whole new `GtfsFeed` and one reference assignment publishes it, so a request that started
  under the old feed finishes under the old feed.
- **A reload that fails leaves the previous feed published.** Yesterday's routes beat no routes, and
  the poll comes back.
- **`LISTEN` is the trigger; the poll is why the 60 s bound is a guarantee.** A notification reaches
  sessions connected at the moment it fires — a reconnect window, a dropped connection, PgBouncer in
  transaction mode — so a service that only listened would serve the previous feed indefinitely with
  nothing to say it had. The connection is `OpenDirectAsync` for the same reason the kernel's outbox
  dispatcher's is.
- **The stop lookup is a linear scan and that is deliberate.** ~7 600 halts, a haversine each: tens
  of microseconds, no index to maintain, no rebuild on reload, and no second structure that can
  disagree with the stop list.
- **The `data=` pin beats the `@` viewport on a `/place/` URL.** They differ, sometimes by hundreds
  of metres, because the viewport is framed around the label rather than centred on it — taking the
  viewport drops the passenger's marker down the street from the place they shared.
- **The allowlist is the only host rule, and it is re-checked at every redirect hop.** The *first*
  URL is the one an attacker cannot choose the destination of; the redirect target is. An earlier
  revision also kept a hardcoded "is this a shortener" list and the two could disagree — a host an
  operator allowed was refused by a constant nobody could see. One list, one decision, and
  `AllowAutoRedirect` is off so the chain is walked rather than reported after the fact.
- **Matching is exact-or-subdomain, never a string suffix.** `evilgoo.gl` ends with `goo.gl` and is
  somebody else's domain entirely.
- **An unreadable link is `422`, a missing one is `400`.** Different failures: one the client fixes
  by sending a url, the other by picking on the map (BR-23.4's Error state).
- **The 3 s budget covers the retry.** The sheet says "Reading link…" for three seconds and then
  offers the map; a per-attempt budget would make the worst case twice what the user was promised.
- **Every switch-off is announced at start-up**, and here for AL-55's reason: a service with no feed
  answers every corridor the same way a service *with* a feed answers a corridor no bus serves.

## Schema this service added

`db/migrations/1406__transit_trip_headsign.sql`, a micro-change-set recorded in the C056 handoff.

| Object | Why |
|---|---|
| `transit.gtfs_trips.trip_headsign` | D3' and D5' BR-23.2 both put the headsign on every option and §18c's five columns cannot hold it. It is what tells the two directions of one route apart on a card — "138 to Kottawa" and "138 to Pettah" share a `route_short_name` *and* a `route_long_name`. NULL until C057's importer maps `trips.txt`; the service falls back to `route_long_name` |
| the same column on `transit_staging.gtfs_trips` | 1404 builds the mirror with `CREATE TABLE IF NOT EXISTS … LIKE`, so a later column is only picked up where staging does not exist yet. On every other database the two sides diverge — and activation is `ALTER TABLE … SET SCHEMA`, which needs them shape-identical |

## Contract changes this component made

| Change | Why |
|---|---|
| `coverage` on the options response (`active` \| `no_feed`) | an empty list meant two different things, and AL-55 makes one of them a safety net SCR-PA-009 renders differently |
| `feedVersion` typed `[string, 'null']` | it is absent precisely when `coverage` is `no_feed`, and the client has to be able to read that |

## Promoted into the kernel

`EncodedPolyline` moved from `Query.Api/Geo/` to `MageRide.Shared.Geo` unchanged. It is a **wire
format two services must agree on** — query-svc encodes a trip's track, this service encodes a GTFS
shape, and a client decodes both — so a second copy would let them drift on precision. The same move
C042 made with `VehicleVisibilityRules` and C052 with `LiteralKeyDictionaryConverter`.

## Not here, and named rather than stubbed

- **The GTFS Dataset Manager.** C057's (AL-54): upload, validate, activate, roll back. This service
  reads what it publishes and holds no route that can write a GTFS table.
- **GTFS-RT live ETAs.** Phase 3 (D6' I-32.2). The durations reported here are scheduled in-vehicle
  times from the feed.
- **The service calendar.** `calendar`/`calendar_dates` have nowhere to live (see the ordering rule).
  Until they do, this service cannot say whether a trip runs today — which is why nothing here
  claims a departure time.
- **Two-transfer itineraries**, and **walking legs between halts**. Both are a different algorithm.
- **`/v1/transport-options`.** query-svc's (C042), which composes private tiers with a call to this
  service. The two overlap and C042's handoff says so.
- **Nominatim geocoding and `/v1/geo/search`.** query-svc's (D-15). This service resolves a pasted
  link to a coordinate and does not turn it into an address — BR-23.4's Resolved state gets its
  address from `/v1/geo/reverse`.
- **The Dockerfile.** `infra/docker-compose.dev.yml` already carries a `transit-svc` cluster
  destination pointing at the combined `app-services` container.

## Configuration

Every knob is documented at its declaration in `TransitOptions` and in `infra/env/.env.app.example`.

| Setting | Default | Where it comes from |
|---|---|---|
| `HaltRadiusM` | 400 | BR-23.2 / D6' I-32.1, "admin halt-radius, default 400 m" |
| `MaxHaltsPerEnd` | 12 | **no spec** — a dense interchange, and a bound on the transfer search |
| `MaxOptions` | 50 | **no spec** — direct options are never dropped for a transfer |
| `TransferOptionsEnabled` · `MaxTransferOptions` | on · 10 | BR-23.2's "listed below direct" |
| `FeedCacheEnabled` | on | **off ⇒ every corridor answers `no_feed`** |
| `FeedChannel` | `transit_feed_activated` | D6' I-32.1 names it |
| `FeedPollInterval` | 30 s | **no spec** — the safety net that makes the ≤ 60 s bound a guarantee |
| `MapsLink:Timeout` · `Retries` | 3 s · 1 | BR-23.4, and the timeout covers the retry |
| `MapsLink:MaxRedirects` | 4 | **no spec** — a shortener uses one; four covers an interstitial |
| `MapsLink:AllowedHosts` | the five Google hosts | **the whole security story of the resolver** — validated non-empty at start-up |

`ConnectionStrings:Postgres` and `Jwt:*` are required. There is no `ConnectionStrings:Redis`, no
`Kafka:BootstrapServers` and no `Outbox:*`, and there must not be.
