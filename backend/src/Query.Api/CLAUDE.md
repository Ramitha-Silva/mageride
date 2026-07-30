# query-svc (C042) — the read side of both planes

Stack: .NET 10 Minimal API + Dapper over Npgsql + StackExchange.Redis + Grpc.AspNetCore.
References `MageRide.Shared` (C002). **No Kafka, no outbox, no command log, and no writes at all** —
every route is a `GET` and everything this service knows it read from state somebody else owns.

**Verify:** `dotnet test backend/src/Query.Api.Tests -c Release`

`backend/contracts/query.yaml` and `backend/contracts/proto/query.v1.proto` are normative for this
surface and win over this file and over the code.

## What this service is

| Endpoint | Spec |
|---|---|
| `GET /v1/nearby` | D3' query-svc, US-7.1/7.4/7.7/7.11/7.12/7.16/7.17, MAP-03/06 |
| `GET /v1/routes/{routeNumber}/buses` | D3' route table, US-7.9 |
| `GET /v1/transport-options` | D3' route table, US-7.15, AL-17/AL-19 |
| `GET /v1/trips/{userId}` · `/{tripId}` | US-8.7 |
| `GET /v1/earnings/{driverId}` · `/sessions` | US-9.22, R-05, D-13 |
| `GET /v1/geo/search` · `/reverse` | D-14, D-15, D6' §7.6, BR-23.1, AL-17 |
| `query.v1.Query` (gRPC, internal) | ADD §6, D3' §0 — **the block is a C042 micro-change-set** |

| Key it reads | Written by |
|---|---|
| `geo:live` (GEO), `veh:meta:{vehicleId}` (HASH) | position-processor-svc (C039) |
| `veh:engaged:{vehicleId}`, `veh:offline:{vehicleId}`, `share:{userId}` | fanout-svc (C041) |
| `geo:fwd:*`, `geo:rev:*` | **this** — the only thing it writes, and it is a cache |

Postgres: `registry.vehicles`, `rides.rides`, `rides.transitions`, `fares.ride_payments`,
`trips.sessions`, `trips.session_summaries`, `trips.ratings`, `telemetry.positions_1m`,
`billing.daily_fee_charges`, `dispatch.cancellation_penalties`, `iam.saved_addresses`,
`spatial.routes`. All read-only.

## Rules that are load-bearing

- **The visibility rules are one function in the kernel, not two implementations.**
  `MageRide.Shared.Realtime.VehicleVisibilityRules.Classify` is fanout-svc's and this service's alike
  — promoted out of `Fanout.Api/Visibility` by this component, which is what C041's handoff asked for
  by name. `signalr-hub.md` §1.1 makes `GET /v1/nearby` the snapshot and resync path for the very map
  `/hubs/live` streams, so a second copy would let the poll and the stream disagree about who may be
  seen, and the disagreement surfaces as a passenger watching an engaged taxi for one poll interval —
  D-22's disclosure with a delay on it.
- **`geo:live` is a superset of the live fleet, so the exact post-filter is not optional.** `GEOADD`
  replaces a member's position and *nothing ever removes one*: a GEO set has no per-member TTL, C039
  does not `GEOREM`, and C041's stale sweep works on the cell streams instead. A `GEOSEARCH` therefore
  returns vehicles that stopped reporting last year, at the place they stopped. Every candidate is
  re-read from `veh:meta` — which *does* expire — and re-measured with a haversine against the position
  read back. A candidate with no hash is not "somewhere approximate", it is unknown, and it is dropped.
- **The search radius is inflated 1 % and then narrowed exactly.** Redis GEO is geohash-based and its
  own documentation admits up to 0.5 % error at the boundary. Both directions matter: the exact pass
  drops a vehicle Redis included from just outside, and the inflation is what stops it missing one
  Redis excluded from just inside.
- **A vehicle whose mode or type is missing is not drawn, even though the registry could supply it.**
  fanout-svc holds no database and drops such a frame; taking the more generous path here would put a
  marker on the map that the socket then never moves — a frozen vehicle a passenger walks towards. The
  two planes fail the same way on purpose. (`type` is separately required because MAP-03 draws a marker
  *by* type, colour and rail icon both.)
- **US-7.16 has two halves and both are implemented.** An engaged Mode C vehicle is off the public
  answer, and *on* the answer for the passenger whose ride engaged it. Membership is decided by
  `rides.rides`: `veh:engaged:{vehicleId}` names the ride, and the database says whether the caller is
  a party to it (passenger, booker or registered rider — P-01/P-03 make those three different
  accounts). **Nothing here re-derives which states count as engaged** — the key's presence already
  answered that, and a second copy of ride-svc's eighteen-state machine is how the two would drift.
- **The registry is read only for vehicles whose identity may be disclosed.** US-7.4 gives the details
  popup to Mode A and Mode B alone — "standby on-demand vehicles do not show info when tapped" — and
  US-7.12 gives the plate and the driver's name to the accepted ride. So an idle Mode C taxi's
  registration is never fetched at all: the privacy rule is the shape of the data access rather than a
  field-stripping step that could be forgotten.
- **A `{userId}` in a path is checked against the token, never trusted**, in one place
  (`SubjectScope.Require`) because the rule has to be identical on all four routes that carry one. The
  six back-office roles pass (US-24.9/24.10's read-only tabs); the `PII_READ` audit for that is
  admin-bff's (D-35), not this service's.
- **A trip that is not the caller's is 404, not 403.** The scoping is inside the query, so "does not
  exist" and "is not yours" are the same result — telling them apart is a membership oracle over other
  people's journeys. trip-state-svc's `/active` is under the same rule.
- **AL-17 is held by an absence of capability, not by a filter.** "Destination search returns geocoded
  places and saved/recent only. No route-number rows" — and the search path has no query that can
  reach `spatial.routes`, `transit.gtfs_routes` or anything else holding a route. `IPlaceRepository`
  reaches two tables; `IGeocoder` reaches an OSM *place* index. A passenger typing "138" gets whatever
  Nominatim thinks "138" is and cannot get a bus route, because no code exists that could return one.
  A filter would be a line somebody could delete. `PlaceSearchTests` seeds a real route numbered 138
  *with an active bus on it* and asserts search still cannot produce it.
- **A Redis failure degrades to `limitedLive`, not to a 500.** ADD §12's resilience table specifies it:
  "Redis failure … query-svc returns `limited_live` flag". A passenger during a cache outage gets an
  empty map that says it is incomplete, which is recoverable; a 500 is a screen they cannot use. The
  flag is **always serialised** — a client that could not tell "no vehicles nearby" from "we do not
  know" would render an outage as a quiet afternoon.
- **Read-after-write is one read, and it is decided by the read's shape.** ADD §9.3: replicas, with
  read-after-write "only where required". Required exactly once: `GET /v1/trips/{userId}/{tripId}`,
  opened from the receipt screen seconds after ride-svc marked the ride terminal, where lag does not
  stale the answer but *inverts* it into a 404 on a trip the passenger has just finished. Everything
  else is a list or an aggregate, where a row missing from the top of a page appears on the next pull.
- **`etaSeconds` is a straight line with a detour factor, and it says so.** ADD §7.6 puts routing in
  **Phase 3**, so there is no road network to measure against; C041 had already deferred the field
  here once and deferring again would mean nothing ever populates it. Every assumption is a setting
  rather than a constant — the detour factor, the per-type average speeds, the cap — so it can be
  retuned against observed arrivals and deleted when OSRM lands. Speed comes from the vehicle when the
  vehicle is moving and from its type when it is not, because a taxi at a light reports ~0 m/s and
  dividing by that gives hours.
- **The per-type ETA speeds are urban averages *including stops*, not ADD §12.6's anti-spoof ceilings.**
  Those are the speeds above which a fix is a lie — three to five times what a vehicle averages across
  a city — and using them would understate every arrival by a factor of four.
- **Modes and types are normalised separately.** Canonical vehicle types are lower-case with
  underscores (AL-09); operating modes are the upper-case letters A/B/C (D5' §2). One shared
  `ToLowerInvariant` silently turns `modes=C` into a filter that matches nothing — an empty map with no
  error anywhere, which is exactly the failure shape this service is written against. It was a real bug
  for one test run; `NormaliseType`/`NormaliseMode` are the fix and a test pins both.
- **A radius above the contract's ceiling is a 400, not a silent clamp.** A client asking for 50 km and
  receiving 20 km worth of vehicles would conclude the country is empty.
- **The R-05 earning gate is read off the *ride*, not the payment.** D5' §8.1's terminal set is three
  `rides.rides.state` values (plus AL-47's driver-QR, which lands in the same place). Gating on the
  payment row would count a `Succeeded` attempt on a ride later disputed, and would have to reason
  about the D-10 retry chain to avoid counting one fare three times. A ride has one state.
- **Gross excludes the OnePay surcharge** (US-8.11 — the passenger's gateway cost, which the gateway
  keeps), and **a cash ride with no payment row still counts as a trip**: a driver whose day was all
  cash must not read "0 trips".
- **The daily fee and the D-05 penalty are on the summary and not on a per-ride row.** A daily fee is a
  fact about a *day* (D-13 charges it once, before the second trip) and the penalty is a fact about a
  cancellation on somebody else's journey; splitting either across the rides of a day would make every
  row's net wrong in a different way. D3' marks both optional on `SessionEarning` and required on
  `EarningsSummary`, which is the same conclusion.
- **`fares.driver_earnings` is deliberately not read.** Migration 1004 creates it as "the read model
  behind the driver Earnings screen" and **nothing writes it** — its writer is fare-svc's R-05 earning
  post (C049/C050). Reading an unwritten rollup would answer every dashboard with zeros while the
  payment rows behind it hold real money: the failure that looks exactly like a working screen.
- **Trip detail returns the stored polyline and never rebuilds one from raw rows.** Mode A/B: the
  `trips.session_summaries.polyline` geography column persistence-writer-svc wrote on `session.ended`
  (ADD §9.2). Mode C: the `telemetry.positions_1m` continuous aggregate, which is the read path ADD
  §9.5 item 2 prescribes ("hits aggregates, not raw rows") and which migration 1802 landed *naming this
  component*. `geometrySource` says which and at what grain.
- **A Mode C `distanceKm` is omitted rather than derived from a minute-grain line.** Chaining
  sixty-second chords across a route with turns loses a third of the distance or more — C040's own note
  on the same trade-off — and that number is the one on the receipt. A figure a third short of the fare
  is worse than no figure.
- **A session has no fare, and that is `null` rather than `0`.** Mode A is free to ride and Mode B is a
  monthly subscription paid to the fleet owner (BR-23.8/23.9). Zero would claim the journey cost
  nothing, which is a different statement from "journeys are not priced".
- **Cursors are keyset on `(timestamp, id)`.** Two trips can start in the same microsecond — a fleet's
  morning departure does exactly that — and a cursor on the timestamp alone would skip rows or repeat
  them. Unsigned, and it does not matter: the query is scoped by the token's subject and the cursor
  contributes only an ordering bound. An unparseable cursor is the first page, not a 400 — a client
  that upgraded across a format change should see the top of the list, not an error it cannot clear.
- **Nothing here computes a GTFS route or a fare.** `/v1/transport-options` delegates to transit-svc
  (C061, AL-18) and fare-svc; either computed here would be a second opinion about somebody else's
  number, and the symptom is a price on the options screen that differs from the confirm screen.
  Private tiers are constructed **without** an ETA or a distance, in one place, because AL-19/BR-23.3
  make a pre-match tier price-only.
- **There is no Google Places call and there must never be one.** D3' makes every map endpoint
  `[REPLACE]` as a hard rule. `NominatimClient` has exactly one downstream and no fallback provider — a
  fallback is how "no Google Maps SDK" becomes "no Google Maps SDK except when the self-hosted one is
  slow".
- **The gRPC surface delegates to the same services the HTTP routes use**, so the visibility filter,
  the R-05 gate and the polyline's provenance each have one implementation. `viewer_user_id` is
  **required**: two of the four visibility rules are per viewer, and answering a call that names nobody
  with the public map is how a back-office screen ends up showing an engaged taxi.
- **Every switch-off is announced at start-up.** An open filter looks exactly like a working one from
  the outside — vehicles appear, positions move, nothing errors — and the difference only surfaces when
  somebody sees a vehicle, a plate or a journey that is not theirs.
  `WarnAboutWhatIsNotBeingEnforced` names each with the specific disclosure it causes, the same rule
  position-processor-svc and fanout-svc are written under.

## Contract changes this component made

`query.yaml`, all recorded in the C042 handoff:

| Change | Why |
|---|---|
| `limitedLive` on both snapshot responses | ADD §12 specifies the flag by name and D3' prints no field for it |
| `TripDetail.geometrySource` | a full-resolution Mode A/B track and a 1/min Mode C line are not the same artefact |
| `GeocodedPlace.label` | without it a client cannot render US-7.13's Home/Work shortcuts |
| `503` on `/v1/geo/reverse` and `/v1/transport-options` | the real failure mode of a proxy to a self-hosted dependency |
| `fromLat`/`fromLng` required on `/v1/transport-options` | the platform holds no last-known position for a *passenger*; `geo:live` is keyed by vehicle |
| `x-grpc-service` + `proto/query.v1.proto` | ADD §6 gives this service gRPC and neither spec prints a block |

`/v1/transport-options` is kept rather than folded into C061's `/v1/transit/options`, because
`shared/kmp`'s generated client already calls it (C012) — but the two overlap and the handoff says so.

## Not here, and named rather than stubbed

- **The Mode C track's producer.** E-04's Kalman-filtered path is computed by fare-svc for the distance
  the fare is charged on and **not persisted anywhere**: ADD §9.2's stored summary is per *session* and
  `ck_summaries_mode` admits only A and B. Until C049 stores it, a ride's line is the 1/min aggregate
  and its distance is absent. No table was added for a writer that does not exist yet.
- **Passenger ridership on Mode A/B.** `trips.sessions` links to a *driver* and nothing else — nobody is
  ticketed on a bus — so "my trips" is a passenger's Mode C rides and a driver's rides plus sessions.
  The only join the schema has.
- **Tiles.** Cloudflare R2 + Worker over HTTP range requests at `https://tiles.mageride.lk/sl.pmtiles`
  (D-14, MAP-09). **Not an app API** and not this service's.
- **GTFS routing** (transit-svc, C061), **the fare tariff** (fare-svc), **`/v1/geo/parse-maps-link`**
  (transit-svc, AL-20 — the gateway already routes it there), and the token-scoped public read
  (public-bff, C065).
- **Row-level security on `fleet_id`.** ADD §9.5 item 8 makes the fleet-scoped telemetry view an RLS
  policy (migration 1804) "without application-side filtering risk"; nothing here sets a fleet role,
  because no endpoint here is fleet-scoped. fleet-svc (C059) is where that lands.
- **The Dockerfile.** `infra/docker-compose.dev.yml` already carries a `query-svc` cluster destination
  pointing at the combined `app-services` container.

## Configuration

Every knob is documented at its declaration in `QueryOptions` and in `infra/env/.env.app.example`. The
ones that are not obvious:

| Setting | Default | Where it comes from |
|---|---|---|
| `FreshnessWindow` | 60 s | **no spec pins it** — must equal `Fanout:FreshnessWindow` |
| `DefaultRadiusM` / `MaxRadiusM` | 3 000 / 20 000 | D3' `GET /v1/nearby` |
| `MaxVehicles` | 500 | **no spec** — a bound needed because `geo:live` never shrinks |
| `EtaDetourFactor` | 1.3 | **no spec** — an interim until ADD §7.6's Phase 3 router |
| `EtaSpeedKph:{type}` | urban averages | **no spec** — deliberately *not* ADD §12.6's ceilings |
| `EtaMinSpeedMps` / `MaxEta` | 2 m/s / 90 min | **no spec**; the floor stops a division by a stopped vehicle |
| `GeocodeCacheTtl` | 24 h | **no spec** — an order of magnitude inside D-15's weekly refresh |
| `ReverseCacheDecimals` | 4 | **no spec** — ~11 m, finer than GNSS error and coarse enough to hit |
| `SavedPlaceLimit` / `RecentPlaceLimit` | 5 / 5 | **no spec** — BR-23.1 names the sources, not the counts |
| `PrivateTiers` | the six Mode C passenger tiers | AL-09 minus the delivery and Mode A types |
| `ReplicaConnectionString` | unset | ADD §9.3. Unset is correct outside DOKS, just not scaled |
| `InternalApiKey` | unset | **unset leaves `query.v1.Query` unmapped**, not open |
| `HttpListenPort` / `GrpcListenPort` | 5000 / 5006 | **D7' §4.2 has no row for query-svc.** Two ports because cleartext has no ALPN; 5006 not 5005, which is reputation-svc's and shares the `app-services` container. 0 = ephemeral |
| `VisibilityEnabled` · `EntitlementEnabled` · `OwnRideEnabled` · `EtaEnabled` | on | each gates one rule; all four are announced when off |

`ConnectionStrings:Postgres`, `ConnectionStrings:Redis` and `Jwt:*` are required. There is no
`Kafka:BootstrapServers` and there must not be.
