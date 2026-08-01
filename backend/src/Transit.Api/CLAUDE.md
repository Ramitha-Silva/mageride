# transit-svc (C056 routing, C057 GTFS lifecycle) — Mode A discovery, the paste-link resolver, and the GTFS Dataset Manager

Stack: .NET 10 Minimal API + Dapper over Npgsql. References `MageRide.Shared` (C002).
**No Redis, no Kafka, no outbox** — see `TransitApplication` for why each is off. The only thing
that reaches this service asynchronously is Postgres' own `LISTEN/NOTIFY`. **Δ C057: there is a
command log** (`transit.command_log`, migration 1407), because BR-32.2 makes activation idempotent
on `Idempotency-Key`.

**Verify:** `dotnet test backend/src/Transit.Api.Tests -c Release`
(C057's slice: `--filter Category=Gtfs`)

`backend/contracts/transit.yaml` is normative for this surface and wins over this file and over the
code.

## What this service is

| Endpoint | Auth | Spec |
|---|---|---|
| `GET /v1/transit/options` | Bearer | D3' transit-svc, AL-18, BR-23.2 |
| `GET /v1/transit/routes/{routeId}` | Bearer | D3' transit-svc |
| `GET /v1/geo/parse-maps-link` | Bearer | AL-20, BR-23.4 |
| **Δ C057** `POST /v1/admin/transit/gtfs/uploads` | Admin, Super Admin | AL-54, US-28.1, BR-32.1 |
| **Δ C057** `GET …/uploads/{feedVersionId}` · `/report` | Admin, Super Admin | US-28.1, BR-32.1 |
| **Δ C057** `POST …/uploads/{feedVersionId}/activate` | Admin, Super Admin | US-28.2, BR-32.2 |
| **Δ C057** `GET …/versions` · `/versions/{id}/download` | Admin, Super Admin | US-28.3, BR-32.3 |
| **Δ C057** `GET …/gtfs/objects/{feedVersionId}` | **signature** | Δ C057 — the 302's target |

The two halves are one service because they are two ends of one table: C057 writes
`transit.gtfs_*` and C056 reads it.

| Table | Read | Written |
|---|---|---|
| `transit.gtfs_routes` · `_trips` · `_stops` · `_stop_times` · `_shapes` | once per activation (C056) | the importer (C057), only ever via the swap |
| `transit_staging.gtfs_*` | — | the importer (C057) |
| `transit.gtfs_feed_versions` | which feed is active (C056) | the lifecycle (C057) |
| `transit.command_log` | — | the kernel's idempotency middleware |
| `audit.events` | — | every mutation on the admin surface (D-35) |

## The six fences, and how each is held structurally

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
- **AL-56: the feed is an externally provided file, launch and every refresh.** There is no
  authoring surface here and no route that can write a GTFS row — the importer's only input is a
  stored zip, and its only output is `transit_staging`. Server-side validation (BR-32.1) is the
  only quality gate MageRide enforces, which is why `GtfsValidator` is the largest thing in the
  component.
- **Activation is one transaction, and a failed one leaves the previous feed live.** Held by which
  tables each phase touches rather than by unwinding: the staging load writes only to
  `transit_staging.*`, and the swap — one `NpgsqlTransaction` — is the only thing that touches
  `transit.*`. `A_failed_activation_leaves_the_previous_feed_live_and_untouched` asserts it by
  losing the stored zip out from under a validated version.
- **Exactly one feed is `active`, and the partial unique index is what says so.** The application
  archives the incumbent first because `ux_gtfs_feed_one_active` is checked per statement, not
  because the application is the guard. Removing the ordering produces a constraint violation, not
  two live feeds.

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

### The GTFS lifecycle (Δ C057)

- **The swap is a three-way schema rename, not a delete-and-insert.** `ALTER TABLE … SET SCHEMA` is
  a catalogue update — it rewrites no row — so the live dataset is replaced in the time it takes to
  take the locks, whatever the feed's size. Emptying and refilling `transit.*` instead would leave
  it empty for the length of the load, which is the one state passengers must never see.
- **Foreign keys follow the tables, not the names.** A constraint is a reference to an OID, so the
  ex-staging tables keep referencing each other after they land in `transit`. That is what
  migration 1404 means by "pointing WITHIN `transit_staging`": a staging FK aimed at a live table
  would drag the live rows through the swap.
- **Index names are renamed back onto their own side inside the same transaction** (the C005
  decision `contracts/transit.yaml` records). Without it, `transit` ends up carrying
  `ix_staging_*`, and 1404's `CREATE INDEX IF NOT EXISTS ix_staging_…` then matches nothing and
  builds a second index on every migration re-run. Asserted after *two* activations, because a
  rename that only worked one way would pass after one.
- **One activation at a time, by session advisory lock — across both phases.** Two operators
  activating two feeds would otherwise both truncate and load one staging schema, and the swap
  would publish a dataset that is half of each. It is the second thing here to need
  `OpenDirectAsync`, for the same reason the `LISTEN` is: PgBouncer in transaction mode hands the
  session back between statements and the lock with it.
- **`NOTIFY` is issued inside the swap transaction**, so it is delivered exactly when — and only
  if — the swap commits (D6' I-32.1). The channel comes from the same `Transit:FeedChannel` the
  listener reads, so renaming it keeps both halves in step.
- **A rollback is an activation, and it must clear `archived_at`.** `ck_gtfs_feed_versions_activated`
  refuses an `active` row that still carries one, which is exactly the row a naive rollback writes.
  `migrate-verify.sh` asserts the constraint rejects it.
- **The upload dedupes on content, not on a header.** BR-32.1's sha256 refusal is stronger than
  `Idempotency-Key` — it catches a retry that regenerated its key, and the same file uploaded a
  month later by a different operator. That is also why this one POST is
  `AllowMissingIdempotencyKey`: the kernel's replay hashes and buffers the request body, and this
  body is up to 200 MB. The header is still **required**, enforced by the handler with the kernel's
  own rules, so a client cannot tell the two endpoints apart by what they accept.
- **Three guards for one 200 MB ceiling, because they catch different clients.** A declared
  `Content-Length` is refused before a byte is read; Kestrel's own limit is raised to exactly the
  ceiling plus the multipart envelope, which is what terminates a chunked upload; and the object
  store counts the file's own bytes. `MultipartReader`, not `ReadFormAsync` — the form reader
  buffers the whole body to a temp file before the handler sees anything, so a 200 MB feed would be
  written to disk twice and the 413 would arrive after both.
- **Errors block and warnings do not** (BR-32.1). That line is the line between "this dataset would
  break route matching" and "somebody should look at this": a stop 400 km out to sea is the first,
  a service window ending in three weeks is the second.
- **The verdict is a count, the report is a list.** A feed whose `stop_times.txt` names a
  nonexistent stop is wrong on every one of half a million rows; the report is capped and says how
  many were dropped, while `ErrorCount` — uncapped — is what decides `failed`.
- **A missing required file stops the pass.** Without `stops.txt` every `stop_times` row is also an
  `unknown_stop_id`, and the one finding that explains the feed would be buried under half a
  million consequences.
- **Validation compares stable ids against the *active* feed, and only when one is active.**
  BR-32.1 says "the currently active feed version"; comparing against live tables nobody is serving
  would warn that every id in an archived dataset had disappeared.
- **Anything the validator throws becomes a `failed` verdict, not a stuck row.** An operator
  watching SCR-AP-016's stepper needs an answer; leaving the feed at `validating` is a spinner that
  never resolves.
- **`calendar`/`calendar_dates` are validated but not stored.** BR-32.1 requires them and this
  service checks referential integrity and the service window against them, but §18c mirrors five
  tables and none is the calendar — so nothing can still say whether a trip runs *today*. C056's
  gap (b) is **escalated, not closed**: persisting them is a §18c change plus a C056 routing
  change, and inventing two tables no spec declares is not this component's call.
- **A signed URL is the credential on the download.** `…/download` answers a 302 and a browser
  following it does not carry the bearer that authorised it — which is why object storage answers
  downloads with presigned URLs. The signature is scoped to one feed version and one expiry.
- **Nothing deletes a stored zip.** BR-32.3's ≥ 12-month retention is met by the absence of a
  delete path: a rollback re-imports from the archived version's `storage_key`, so a collected zip
  is a version that can no longer be rolled back to. Expiry past 12 months is a bucket lifecycle
  policy (D7'), not a code path. The one exception is undoing a write the same request then refused
  — a duplicate upload's redundant copy.
- **The importer maps `agency_name`, not `agency_id`, into `transit.gtfs_routes.agency`.** §18c
  gives one TEXT column and does not say which; transit-svc answers it as `agencyName` on the
  route-detail response, and a passenger reading "SLTB" is helped where one reading "1" is not.

## Schema this service added

`db/migrations/1406__transit_trip_headsign.sql` (C056) and `1407__transit_command_log.sql` (C057),
both micro-change-sets recorded in their handoffs.

| Object | Why |
|---|---|
| `transit.gtfs_trips.trip_headsign` | D3' and D5' BR-23.2 both put the headsign on every option and §18c's five columns cannot hold it. It is what tells the two directions of one route apart on a card — "138 to Kottawa" and "138 to Pettah" share a `route_short_name` *and* a `route_long_name`. **Δ C057: the importer now maps it** from `trips.txt`; the fallback to `route_long_name` remains for a feed that omits it |
| the same column on `transit_staging.gtfs_trips` | 1404 builds the mirror with `CREATE TABLE IF NOT EXISTS … LIKE`, so a later column is only picked up where staging does not exist yet. On every other database the two sides diverge — and activation is `ALTER TABLE … SET SCHEMA`, which needs them shape-identical |
| `transit.command_log` (Δ C057) | R-14's per-bounded-context table, the eleventh instance of the same D4' §5 gap. BR-32.2 names the guarantee out loud — activation is "idempotent on `Idempotency-Key`" — and a double-clicked **Activate** must swap once. No aggregate-id column: the middleware never populates one, and the feed version is already in the request path the hash covers |

## Contract changes this component made

| Change | Why |
|---|---|
| `coverage` on the options response (`active` \| `no_feed`) | an empty list meant two different things, and AL-55 makes one of them a safety net SCR-PA-009 renders differently |
| `feedVersion` typed `[string, 'null']` | it is absent precisely when `coverage` is `no_feed`, and the client has to be able to read that |
| **Δ C057** `GET /v1/admin/transit/gtfs/objects/{feedVersionId}` (`security: []`) | the target of the `…/download` 302. A browser following a redirect does not carry the bearer that authorised it, which is why object storage answers downloads with presigned URLs; the `sig` HMAC is the credential. It disappears when a bucket is configured |
| **Δ C057** `conflict` on `activateGtfsFeed`'s `x-error-codes` | activation serialises on an advisory lock across both phases, and the caller that cannot take it is told rather than left waiting |

## Promoted into the kernel

`EncodedPolyline` moved from `Query.Api/Geo/` to `MageRide.Shared.Geo` unchanged. It is a **wire
format two services must agree on** — query-svc encodes a trip's track, this service encodes a GTFS
shape, and a client decodes both — so a second copy would let them drift on precision. The same move
C042 made with `VehicleVisibilityRules` and C052 with `LiteralKeyDictionaryConverter`.

**Δ C062:** the `audit.events` writer this service carried is gone — C062 became its third caller
and promoted it to `MageRide.Shared.Messaging.IAuditEventWriter`, which is exactly what the C057
handoff asked for. What stays here is `GtfsAuditActions`, the three facts the GTFS lifecycle is
entitled to record; the INSERT, and the four columns migration 1312 added to it, are the kernel's.

**Δ C057:** `feed-duplicate`, `feed-not-validated` and `feed-already-active` are now declared in
`MageRideErrors`. They were coined by C007 in `contracts/_shared.yaml`'s `ErrorCode` enum and had no
C# declaration until a component could raise them.

## Not here, and named rather than stubbed

- **GTFS-RT live ETAs.** Phase 3 (D6' I-32.2). The durations reported here are scheduled in-vehicle
  times from the feed.
- **The service calendar as a stored table.** `calendar`/`calendar_dates` are *validated* (BR-32.1)
  and their span becomes `service_start`/`service_end`, but §18c mirrors five tables and none is
  the calendar — so nothing can say whether a trip runs today, which is why nothing here claims a
  departure time. Escalated in the C057 handoff rather than closed: it is a §18c change plus a C056
  routing change.
- **`translations.txt`.** BR-32.1 lists it as optional and §18c has nowhere to put it; the trilingual
  rule (D-26) covers MageRide's own strings, not a third party's route names.
- **An S3 client.** `IGtfsObjectStore` is the seam; the filesystem implementation is the interim,
  the same one ride-svc's proof photos take.
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
| `Gtfs:MaxUploadBytes` | 200 MB | BR-32.1, D3', the contract |
| `Gtfs:StorageRoot` | *(temp dir)* | **not object storage** — D-36/BR-32.3's SSE bucket, when a client exists |
| `Gtfs:DownloadSigningKey` | — | **required outside Development**; the only credential on the signed object route |
| `Gtfs:DownloadUrlTtl` | 15 min | **no spec** — long enough to pull 200 MB, short enough that a pasted link is dead |
| `Gtfs:PublicBaseUrl` | *(the request's own origin)* | behind the gateway that is the gateway's |
| `Gtfs:ValidationEnabled` | on | **off ⇒ nothing is ever validated, so nothing can ever be activated** |
| `Gtfs:ValidationPollInterval` · `ValidationStaleAfter` | 10 s · 15 min | **no spec** — the latch starts a validation; these cover a replica that died |
| `Gtfs:ServiceWindowWarnDays` | 30 | BR-32.1's "warn if < 30 days ahead" |
| `Gtfs:MaxReportedIssues` | 5 000 | **no spec** — the verdict is a count, the report is a list |
| `Gtfs:ActivationLockWait` | 30 s | **no spec** — how long a second operator waits before being told |

`ConnectionStrings:Postgres` and `Jwt:*` are required. There is no `ConnectionStrings:Redis`, no
`Kafka:BootstrapServers` and no `Outbox:*`, and there must not be. `CommandLog:Schema` is
`transit` (Δ C057) with no aggregate-id column.
